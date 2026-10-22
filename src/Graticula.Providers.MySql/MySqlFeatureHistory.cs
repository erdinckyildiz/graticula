using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>
/// A registered MySQL layer's history, kept by the database — ADR-207 and ADR-208, the MySQL form of ADR-078's trigger.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database records it, not this server</b>, for ADR-078's reason: a registered table is exactly the one other
/// tools write to. Row triggers on the table write every version into <c>&lt;table&gt;__history</c> beside it, in the
/// writer's own transaction, with ArcGIS archiving's <c>gdb_from_date</c> and <c>gdb_to_date</c>; the attributes as
/// JSON, the shape as a geometry with its own spatial index.
/// </para>
/// <para>
/// <b>Where it differs from Oracle's, and why.</b> A MySQL trigger fires for one event, so there are three — after
/// insert, update and delete. Times are <c>UTC_TIMESTAMP(6)</c> in a <c>DATETIME(6)</c>, so a tool whose session keeps
/// another time zone still records the instant (and nothing ends in 2038, as a <c>TIMESTAMP</c> would). The editor is
/// the session variable <c>@graticula_editor</c>, which <see cref="MySqlFeatureWriter"/> sets for its transaction; another
/// tool's edit is recorded under the database user that made it. As on Oracle, the triggers name the table's columns as
/// they are when history is switched on, an update is a version whether or not it changed a value, and a
/// <c>TRUNCATE</c> is not recorded. <b>MySQL adds one gap of its own: a row deleted by a foreign key's cascade fires no
/// trigger</b>, so that deletion is not in the history.
/// </para>
/// <para>
/// <b>Creating a trigger on a server that writes a binary log needs <c>SUPER</c>, or
/// <c>log_bin_trust_function_creators</c> set</b> (error 1419); <see cref="Refusal"/> says so by name.
/// </para>
/// </remarks>
public sealed class MySqlFeatureHistory : IFeatureHistory
{
    /// <summary>The history table's suffix.</summary>
    public const string Suffix = "__history";

    /// <summary>The session variable a restore sets to the version it writes back.</summary>
    internal const string RestoredVariable = "@graticula_restored";

    /// <summary>What a history table's comment begins with when this server wrote it.</summary>
    private const string CommentPrefix = "graticula:";

    private static readonly string[] Events = ["ai", "au", "ad"];

    private readonly string _connectionString;
    private readonly LayerDefinition _layer;

    /// <summary>Creates the history for one layer.</summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="layer">The layer.</param>
    public MySqlFeatureHistory(string locator, LayerDefinition layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);

        // The triggers and a restore name session variables.
        _connectionString = MySqlConnections.For(locator, pooling: true, userVariables: true);
        _layer = layer;
    }

    /// <summary>The history table's name.</summary>
    public string TableName => _layer.TableName + Suffix;

    private string Qualified => QualifiedOf(_layer);

    private static string QualifiedOf(LayerDefinition layer) =>
        $"{LayerDefinition.Quote(layer.SchemaName)}.{LayerDefinition.Quote(layer.TableName + Suffix)}";

    private static string TriggerName(LayerDefinition layer, string on) => layer.TableName + "__hist_" + on;

    private string Trigger(string on) => $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TriggerName(_layer, on))}";

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn ?? _layer.IdentityColumn);

    private string Shape => LayerDefinition.Quote(_layer.RequireGeometryColumn());

    private Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        MySqlConnections.OpenAsync(_connectionString, cancellationToken);

    private static MySqlCommand Command(string sql, MySqlConnection connection, MySqlTransaction? transaction = null) =>
        new(sql, connection, transaction) { CommandTimeout = 0 };

    /// <summary>
    /// The relation a query at a moment reads instead of the table: every column the table has, as it was then —
    /// <c>OracleFeatureHistory.AtMoment</c>'s MySQL form, with each attribute read back from the version's JSON as the type
    /// its column has.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="columns">The table's columns, and how each is kept: its field type, and a GUID's storage.</param>
    /// <param name="moment">The moment, as an SQL expression of a UTC <c>DATETIME(6)</c>.</param>
    /// <param name="dialect">Which server, which decides how a recorded value is read back (ADR-209).</param>
    /// <returns>A parenthesised relation.</returns>
    internal static string AtMoment(
        LayerDefinition layer, IReadOnlyList<(string Name, FieldType Type, MySqlGuids.Storage? Guid)> columns, string moment,
        MySqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(columns);

        string id = layer.IntegerIdentityColumn ?? layer.IdentityColumn;
        StringBuilder select = new($"select h.objectid as {LayerDefinition.Quote(id)}");

        foreach ((string name, FieldType type, MySqlGuids.Storage? guid) in columns)
        {
            if (string.Equals(name, id, StringComparison.Ordinal) || string.Equals(name, layer.RequireGeometryColumn(), StringComparison.Ordinal))
            {
                continue;
            }

            string path = Path(name);
            string value = (type, guid) switch
            {
                (FieldType.Guid, MySqlGuids.Storage.Binary) or (FieldType.Binary, _) => $"unhex(json_value(h.attributes, {path}))",
                (FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger, _) => dialect.JsonAs("h.attributes", path, "signed"),
                (FieldType.Boolean, _) => dialect.JsonAs("h.attributes", path, "unsigned"),
                (FieldType.Double or FieldType.Single, _) => dialect.JsonAs("h.attributes", path, "double"),
                (FieldType.Date, _) => dialect.JsonAs("h.attributes", path, "datetime(6)"),
                _ => Text("h.attributes", path),
            };

            select.Append(", ").Append(value).Append(" as ").Append(LayerDefinition.Quote(name));
        }

        select.Append(", h.shape as ").Append(LayerDefinition.Quote(layer.RequireGeometryColumn()))
              .Append(" from ").Append(QualifiedOf(layer)).Append(" h")
              .Append($" where h.gdb_from_date <= {moment} and (h.gdb_to_date is null or h.gdb_to_date > {moment})");

        return "(" + select + ")";
    }

    /// <inheritdoc/>
    public async Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a layer keeps its history: its three triggers are there, and so is its table.</summary>
    internal static async Task<bool> IsOnAsync(MySqlConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = Command(
            """
            select count(*) from information_schema.triggers g
             where g.trigger_schema = @s and g.trigger_name in (@ai, @au, @ad)
               and exists (select 1 from information_schema.tables t where t.table_schema = @s and t.table_name = @h)
            """, connection);

        command.Parameters.AddWithValue("@s", layer.SchemaName);
        command.Parameters.AddWithValue("@ai", TriggerName(layer, "ai"));
        command.Parameters.AddWithValue("@au", TriggerName(layer, "au"));
        command.Parameters.AddWithValue("@ad", TriggerName(layer, "ad"));
        command.Parameters.AddWithValue("@h", layer.TableName + Suffix);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == Events.Length;
    }

    /// <inheritdoc/>
    public async Task<HistoryKept?> KeptAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await KeptAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How a layer's history is kept, or null when it keeps none — for a describe on its own connection.</summary>
    internal static async Task<HistoryKept?> KeptAsync(MySqlConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        if (layer.IntegerIdentityColumn is null || !await IsOnAsync(connection, layer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using MySqlCommand command = Command(
            $"""
             select coalesce((select min(gdb_from_date) from {QualifiedOf(layer)}), utc_timestamp(6)), utc_timestamp(6),
                    (select table_comment from information_schema.tables where table_schema = @s and table_name = @h)
             """, connection);

        command.Parameters.AddWithValue("@s", layer.SchemaName);
        command.Parameters.AddWithValue("@h", layer.TableName + Suffix);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset first = Utc(reader.GetDateTime(0));
        DateTimeOffset now = Utc(reader.GetDateTime(1));
        int? keep = KeepDaysOf(reader.IsDBNull(2) ? null : reader.GetString(2));

        return keep is { } days && now.AddDays(-days) > first
            ? new HistoryKept(now.AddDays(-days), days)
            : new HistoryKept(first, keep);
    }

    /// <inheritdoc/>
    public async Task<long> EnableAsync(CancellationToken cancellationToken)
    {
        // <b>Not for a table, yet — ADR-213 condition 7.</b> This history keeps each version's shape in a spatial column with
        // its own index, and a table has no shape to give it; a PostGIS table keeps its history.
        if (_layer.IsTable)
        {
            throw new InvalidOperationException(
                $"'{_layer.Name}' is a table in a MySQL database, and a history here keeps a shape with every version; a table "
                + "has none. Keep this table's history in PostGIS, or keep it without one (ADR-213).");
        }

        if (_layer.IntegerIdentityColumn is null)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' has no integer object id, so a version cannot name the feature it is of.");
        }

        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            return await CountCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<(string Name, string Type)> columns = await RecordedColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        int srid = await SridAsync(connection, cancellationToken).ConfigureAwait(false);
        MySqlDialect dialect = MySqlDialect.Of(connection);
        string role = "substring_index(user(), '@', 1)";

        // A table left behind by triggers somebody dropped by hand is a history with a hole in it; it starts again.
        List<string> statements = [.. Events.Select(e => $"drop trigger if exists {Trigger(e)}")];
        statements.Add($"drop table if exists {Qualified}");
        statements.Add(
            $"""
             create table {Qualified} (
               history_id    bigint auto_increment primary key,
               objectid      bigint not null,
               gdb_from_date datetime(6) not null,
               gdb_to_date   datetime(6),
               opened_by     varchar(10) not null check (opened_by in ('seed', 'insert', 'update')),
               closed_by     varchar(10) check (closed_by in ('update', 'delete', 'truncate')),
               from_editor   varchar(256),
               from_role     varchar(128) not null,
               to_editor     varchar(256),
               to_role       varchar(128),
               attributes    json not null,
               shape         {dialect.GeometryColumn("geometry", srid)},
               restored_from bigint,
               index (objectid, gdb_from_date),
               index (gdb_from_date),
               spatial index (shape)
             )
             """);

        // <b>The triggers before the seed</b>, as on Oracle: a row edited while the seed runs is recorded by a trigger and
        // the seed skips it, so the history begins now with no hole.
        string close(string by) =>
            $"update {Qualified} set gdb_to_date = utc_timestamp(6), closed_by = '{by}', to_editor = {MySqlFeatureWriter.EditorVariable}, "
            + $"to_role = {role} where objectid = old.{Id} and gdb_to_date is null";
        string open(string by) =>
            $"insert into {Qualified} (objectid, gdb_from_date, opened_by, from_editor, from_role, attributes, shape, restored_from) "
            + $"values (new.{Id}, utc_timestamp(6), '{by}', {MySqlFeatureWriter.EditorVariable}, {role}, {Json("new", columns)}, new.{Shape}, {RestoredVariable})";

        statements.Add($"create trigger {Trigger("ai")} after insert on {_layer.QuotedTable} for each row {open("insert")}");
        statements.Add($"create trigger {Trigger("au")} after update on {_layer.QuotedTable} for each row begin {close("update")}; {open("update")}; end");
        statements.Add($"create trigger {Trigger("ad")} after delete on {_layer.QuotedTable} for each row {close("delete")}");

        try
        {
            foreach (string statement in statements)
            {
                await using MySqlCommand command = Command(statement, connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (MySqlException)
        {
            // DDL commits as it goes, so a refused trigger leaves the table made before it: taken away again, so a
            // refusal leaves the database as it found it.
            await DisableAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await using MySqlCommand seed = Command(
            $"""
             insert into {Qualified} (objectid, gdb_from_date, opened_by, from_role, attributes, shape)
             select t.{Id}, utc_timestamp(6), 'seed', {role}, {Json("t", columns)}, t.{Shape}
               from {_layer.QuotedTable} t
              where not exists (select 1 from {Qualified} h where h.objectid = t.{Id})
             """, connection);

        return await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (string statement in Events.Select(e => $"drop trigger if exists {Trigger(e)}").Append($"drop table if exists {Qualified}"))
        {
            await using MySqlCommand command = Command(statement, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SetKeepAsync(int? days, CancellationToken cancellationToken)
    {
        if (days is { } d)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(d, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(d, 36_500);
        }

        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // A comment takes no bind; the text is a fixed prefix and an integer, written by this code alone.
        string comment = days is { } keep
            ? "'" + CommentPrefix + "{\"keepDays\":" + keep.ToString(CultureInfo.InvariantCulture) + "}'"
            : "''";

        await using MySqlCommand command = Command($"alter table {Qualified} comment = {comment}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes, from every history in this database that has a keeping period, the versions that ended before it —
    /// <c>PostGisFeatureHistory.PruneAsync</c>'s rule: a current version is never deleted.
    /// </summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each history pruned, and how many versions went.</returns>
    public static async Task<IReadOnlyList<(string Table, long Versions)>> PruneAsync(string locator, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await MySqlConnections.OpenAsync(
            MySqlConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

        List<(string Schema, string Table, int Days)> kept = [];

        await using (MySqlCommand list = Command(
            "select table_schema, table_name, table_comment from information_schema.tables "
            + "where table_schema = database() and table_name like '%\\_\\_history' and table_comment like 'graticula:%'",
            connection))
        await using (MySqlDataReader reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (KeepDaysOf(reader.GetString(2)) is { } days)
                {
                    kept.Add((reader.GetString(0), reader.GetString(1), days));
                }
            }
        }

        List<(string Table, long Versions)> pruned = [];

        foreach ((string schema, string table, int days) in kept)
        {
            await using MySqlCommand delete = Command(
                $"delete from {LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)} "
                + "where gdb_to_date is not null and gdb_to_date < utc_timestamp(6) - interval @days day",
                connection);
            delete.Parameters.AddWithValue("@days", days);

            long gone = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (gone > 0)
            {
                pruned.Add(($"{schema}.{table}", gone));
            }
        }

        return pruned;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeatureChange>> ChangesAsync(DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using MySqlCommand command = Command(
            $"""
             with v as (
               select h.*, lag(h.history_id) over (partition by h.objectid order by h.gdb_from_date, h.history_id) as previous
                 from {Qualified} h
                where h.gdb_to_date is null or h.gdb_to_date > h.gdb_from_date),
             changes as (
               select history_id, objectid, gdb_from_date as at,
                      case when opened_by = 'seed' then 'existed'
                           when restored_from is not null then 'restored'
                           when previous is null then 'added'
                           else 'updated' end as kind,
                      coalesce(from_editor, from_role) as editor, case when from_editor is null then 1 else 0 end as direct
                 from v
               union all
               select history_id, objectid, gdb_to_date,
                      case closed_by when 'truncate' then 'emptied' else 'deleted' end,
                      coalesce(to_editor, to_role), case when to_editor is null then 1 else 0 end
                 from v
                where closed_by in ('delete', 'truncate'))
             select history_id, objectid, at, kind, editor, direct
               from changes
              where kind <> 'existed' and (@before is null or at < @before)
              order by at desc, history_id desc
              limit @limit
             """, connection);

        command.Parameters.AddWithValue("@before", before is { } b ? b.UtcDateTime : DBNull.Value);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));

        List<FeatureChange> changes = [];

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new FeatureChange(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Utc(reader.GetDateTime(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) == 1));
        }

        return changes;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeatureVersion>> VersionsAsync(long objectId, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // ST_AsGeoJSON writes a geographic shape longitude first, as GeoJSON is.
        await using MySqlCommand command = Command(
            $"""
             select h.history_id, h.gdb_from_date, h.gdb_to_date, h.opened_by, h.closed_by,
                    coalesce(h.from_editor, h.from_role), case when h.from_editor is null then 1 else 0 end,
                    coalesce(h.to_editor, h.to_role), case when h.to_editor is null then 1 else 0 end,
                    h.attributes, st_asgeojson(h.shape), h.restored_from
               from {Qualified} h
              where h.objectid = @id and (h.gdb_to_date is null or h.gdb_to_date > h.gdb_from_date)
              order by h.gdb_from_date, h.history_id
             """, connection);

        command.Parameters.AddWithValue("@id", objectId);

        List<FeatureVersion> versions = [];

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new FeatureVersion(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Utc(reader.GetDateTime(1)),
                reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1,
                JsonDocument.Parse(reader.GetString(9)).RootElement.Clone(),
                reader.IsDBNull(10) ? null : reader.GetString(10))
            {
                RestoredFrom = reader.IsDBNull(11) ? null : Convert.ToInt64(reader.GetValue(11), CultureInfo.InvariantCulture),
            });
        }

        return versions;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>An edit, recorded by the triggers as a new version that says what it restored</b> — the restorer in
    /// <c>@graticula_editor</c>, the version in <c>@graticula_restored</c>, both cleared before the connection goes back.
    /// <b>The version is read first and written in a second statement</b>, because MySQL will not let a statement read the
    /// table its own trigger writes (error 1442). A recreated feature keeps its old object id; an <c>AUTO_INCREMENT</c>
    /// column takes a value it is given.
    /// </remarks>
    public async Task<RestoreOutcome> RestoreAsync(
        long objectId, long historyId, string editor, EditorTracking? tracking, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        EditorTracking track = tracking ?? EditorTracking.None;

        await using MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<(string Name, string Type)> columns = await RecordedColumnsAsync(connection, cancellationToken).ConfigureAwait(false);

        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        string attributes;
        byte[] shape;
        int srid;

        await using (MySqlCommand read = Command(
            $"select attributes, st_asbinary(shape), st_srid(shape) from {Qualified} where history_id = @version and objectid = @id",
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("@version", historyId);
            read.Parameters.AddWithValue("@id", objectId);

            await using MySqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new RestoreOutcome(RestoreResult.NoSuchVersion, false);
            }

            attributes = reader.GetString(0);
            shape = (byte[])reader.GetValue(1);
            srid = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
        }

        HashSet<string> tracked = new(
            new[] { track.Editor, track.Edited }.Where(c => c is not null).Select(c => c!), StringComparer.Ordinal);

        // The shape crosses as MySQL wrote it, in its own axis order both ways.
        List<string> set = [];
        List<string> insertColumns = [Id, Shape];
        List<string> insertValues = ["@id", "st_geomfromwkb(@shape, @srid)"];

        foreach ((string name, string type) in columns)
        {
            if (tracked.Contains(name))
            {
                continue;
            }

            string value = Read("@attributes", name, type, MySqlDialect.Of(connection));
            set.Add($"{LayerDefinition.Quote(name)} = {value}");
            insertColumns.Add(LayerDefinition.Quote(name));
            insertValues.Add(value);
        }

        foreach ((string? column, string value) in new[] { (track.Editor, "@editor"), (track.Edited, "utc_timestamp(6)") })
        {
            if (column is not null)
            {
                set.Add($"{LayerDefinition.Quote(column)} = {value}");
                insertColumns.Add(LayerDefinition.Quote(column));
                insertValues.Add(value);
            }
        }

        set.Add($"{Shape} = st_geomfromwkb(@shape, @srid)");

        void Bind(MySqlCommand command)
        {
            command.Parameters.AddWithValue("@id", objectId);
            command.Parameters.AddWithValue("@attributes", attributes);
            command.Parameters.AddWithValue("@shape", shape);
            command.Parameters.AddWithValue("@srid", srid);
            command.Parameters.AddWithValue("@editor", editor);
        }

        RestoreResult result;

        try
        {
            await using (MySqlCommand mark = Command(
                $"set {MySqlFeatureWriter.EditorVariable} = @editor, {RestoredVariable} = @version", connection, transaction))
            {
                mark.Parameters.AddWithValue("@editor", editor);
                mark.Parameters.AddWithValue("@version", historyId);
                await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The driver counts the rows matched, so a feature written back unchanged still counts as there.
            await using MySqlCommand update = Command(
                $"update {_layer.QuotedTable} set {string.Join(", ", set)} where {Id} = @id", connection, transaction);
            Bind(update);

            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                result = RestoreResult.Updated;
            }
            else
            {
                await using MySqlCommand insert = Command(
                    $"insert into {_layer.QuotedTable} ({string.Join(", ", insertColumns)}) values ({string.Join(", ", insertValues)})",
                    connection,
                    transaction);
                Bind(insert);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                result = RestoreResult.Recreated;
            }
        }
        finally
        {
            await using MySqlCommand forget = Command(
                $"set {MySqlFeatureWriter.EditorVariable} = null, {RestoredVariable} = null", connection, transaction);
            await forget.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RestoreOutcome(result, result == RestoreResult.Recreated);
    }

    /// <summary>The sentence for a refusal the administrator can act on, or null — the right this needs, named.</summary>
    /// <param name="failure">What the database said.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The sentence, or null.</returns>
    public static string? Refusal(Exception failure, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(layer);

        return failure switch
        {
            MySqlException { Number: 1419 } binlog =>
                $"The database refused to create the history's triggers on {layer.SchemaName}.{layer.TableName}: {binlog.Message}. "
                + "A MySQL server that writes a binary log lets only an account with SUPER create a trigger, unless the server's "
                + "log_bin_trust_function_creators is set to 1. Set it, or keep the layer without history (ADR-207).",
            MySqlException { Number: 1142 or 1044 } denied =>
                $"The database refused to create the history beside {layer.SchemaName}.{layer.TableName}: {denied.Message}. "
                + "Keeping a history needs CREATE, DROP and TRIGGER on the database for the user this server connects as. "
                + "Grant them, or keep the layer without history (ADR-207).",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The columns a version records: every column but the key and the shape, of a type JSON can carry — no BLOB, and no
    /// second geometry.
    /// </summary>
    private async Task<IReadOnlyList<(string Name, string Type)>> RecordedColumnsAsync(
        MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = Command(
            """
            select column_name, data_type, column_type from information_schema.columns
             where table_schema = @s and table_name = @t
             order by ordinal_position
            """, connection);

        command.Parameters.AddWithValue("@s", _layer.SchemaName);
        command.Parameters.AddWithValue("@t", _layer.TableName);

        List<(string, string)> columns = [];

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            string type = reader.GetString(1).ToLowerInvariant();
            string full = reader.GetString(2).ToLowerInvariant();

            if (string.Equals(name, _layer.IntegerIdentityColumn ?? _layer.IdentityColumn, StringComparison.Ordinal)
                || string.Equals(name, _layer.RequireGeometryColumn(), StringComparison.Ordinal)
                || type is "tinyblob" or "blob" or "mediumblob" or "longblob"
                    or "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon"
                    or "geometrycollection" or "geomcollection")
            {
                continue;
            }

            columns.Add((name, full.EndsWith(" unsigned", StringComparison.Ordinal) ? type + " unsigned" : type));
        }

        return columns;
    }

    /// <summary>The SRID the layer's geometry column declares, which the history's shape column takes.</summary>
    private async Task<int> SridAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = Command(
            $"select srs_id from {MySqlDialect.Of(connection).GeometryColumns} g where g.table_schema = @s and g.table_name = @t and g.column_name = @c",
            connection);

        command.Parameters.AddWithValue("@s", _layer.SchemaName);
        command.Parameters.AddWithValue("@t", _layer.TableName);
        command.Parameters.AddWithValue("@c", _layer.RequireGeometryColumn());

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is { } found and not DBNull
            ? Convert.ToInt32(found, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException(
                $"{_layer.SchemaName}.{_layer.TableName}.{_layer.RequireGeometryColumn()} declares no SRID, which the history's spatial index needs.");
    }

    /// <summary>A row's recorded columns as one JSON object, from the row aliased <paramref name="row"/>.</summary>
    private static string Json(string row, IReadOnlyList<(string Name, string Type)> columns) =>
        columns.Count == 0
            ? "json_object()"
            : "json_object("
              + string.Join(", ", columns.Select(c =>
              {
                  string column = $"{row}.{LayerDefinition.Quote(c.Name)}";
                  string value = c.Type switch
                  {
                      "binary" or "varbinary" => $"hex({column})",
                      "bit" => $"cast({column} as unsigned)",
                      _ => column,
                  };

                  return $"'{Literal(c.Name)}', {value}";
              }))
              + ")";

    /// <summary>A recorded column read back from a version's JSON in <paramref name="json"/>, as its column's type.</summary>
    private static string Read(string json, string name, string type, MySqlDialect dialect)
    {
        string path = Path(name);

        return type switch
        {
            "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "year" => dialect.JsonAs(json, path, "signed"),
            _ when type.EndsWith(" unsigned", StringComparison.Ordinal) || type == "bit" => dialect.JsonAs(json, path, "unsigned"),
            "decimal" or "numeric" => dialect.JsonAs(json, path, "decimal(65, 30)"),
            "float" or "double" or "real" => dialect.JsonAs(json, path, "double"),
            "date" => dialect.JsonAs(json, path, "date"),
            "datetime" or "timestamp" => dialect.JsonAs(json, path, "datetime(6)"),
            "time" => dialect.JsonAs(json, path, "time(6)"),
            "binary" or "varbinary" => $"unhex(json_value({json}, {path}))",
            "json" => $"case when json_type(json_extract({json}, {path})) = 'NULL' then null else json_extract({json}, {path}) end",
            _ => Text(json, path),
        };
    }

    /// <summary>
    /// A recorded value as text: the JSON string unquoted, and SQL NULL for a JSON null — <c>JSON_VALUE</c> would cut it to
    /// 512 characters.
    /// </summary>
    private static string Text(string json, string path) =>
        $"case when json_type(json_extract({json}, {path})) = 'NULL' then null else json_unquote(json_extract({json}, {path})) end";

    private static string Path(string name) => $"'$.\"{Literal(name)}\"'";

    private async Task<long> CountCurrentAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = Command($"select count(*) from {Qualified} where gdb_to_date is null", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Utc(DateTime stored) => new(DateTime.SpecifyKind(stored, DateTimeKind.Utc));

    private static int? KeepDaysOf(string? comment)
    {
        if (comment is null || !comment.StartsWith(CommentPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using JsonDocument said = JsonDocument.Parse(comment[CommentPrefix.Length..]);
            return said.RootElement.TryGetProperty("keepDays", out JsonElement days)
                && days.TryGetInt32(out int value) && value is >= 1 and <= 36_500
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Literal(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
}
