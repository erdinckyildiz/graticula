using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>
/// A hosted layer's history, kept by the database — ADR-078.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database records it, not this server.</b> ADR-005 §3.8: anyone with credentials — QGIS,
/// a script, a DBA — writes rows directly, so a history this server wrote from its own edits would
/// be missing exactly the edits made around it. A row trigger on the layer's table writes every
/// version into <c>&lt;table&gt;__history</c>, beside <c>&lt;table&gt;__attach</c>, in the writer's
/// own transaction: a rolled-back edit leaves no version behind.
/// </para>
/// <para>
/// <b>ArcGIS archiving's shape.</b> A version is a row state with the moment it became true
/// (<c>gdb_from_date</c>) and the moment it stopped (<c>gdb_to_date</c>, null while current), so
/// <c>historicMoment</c> is a range test rather than a replay. The attributes are kept as
/// <c>jsonb</c> — the table's columns can change under a layer (ADR-058) and a version must survive
/// that — and the geometry as its own column with a spatial index.
/// </para>
/// <para>
/// <b>Hosted layers only.</b> This is DDL, and ADR-002 §4.2 says this server never runs DDL in a
/// database it does not own. <see cref="RefuseOutsideHosted"/> is the same guard the importer uses.
/// </para>
/// </remarks>
public sealed class PostGisFeatureHistory
{
    /// <summary>The companion table's suffix, beside <see cref="PostGisAttachmentStore.Suffix"/>.</summary>
    public const string Suffix = "__history";

    /// <summary>The setting the writer puts the editor's name in, for the length of its transaction.</summary>
    public const string EditorSetting = "graticula.editor";

    /// <summary>The setting a restore puts the version it writes back in, so the new version says what it restored.</summary>
    private const string RestoredSetting = "graticula.restored_from";

    /// <summary>The suffix of the trigger function's name, one per archived table.</summary>
    public const string FunctionSuffix = "__history_fn";

    private readonly NpgsqlDataSource _dataSource;
    private readonly LayerDefinition _layer;

    /// <summary>Creates the history for one layer.</summary>
    /// <param name="dataSource">The pool for the layer's database.</param>
    /// <param name="layer">The layer.</param>
    public PostGisFeatureHistory(NpgsqlDataSource dataSource, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(layer);

        _dataSource = dataSource;
        _layer = layer;
    }

    /// <summary>The companion table's name.</summary>
    public string TableName => _layer.TableName + Suffix;

    private string Qualified =>
        $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TableName)}";

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn ?? _layer.IdentityColumn);

    private string Geometry => LayerDefinition.Quote(_layer.GeometryColumn);

    /// <summary>
    /// The relation a query at <paramref name="parameter"/> reads instead of the table: every column
    /// of the table, as it was at that moment.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="parameter">The name of the <c>timestamptz</c> parameter holding the moment.</param>
    /// <returns>A parenthesised relation, aliased as the table.</returns>
    /// <remarks>
    /// <para>
    /// <b>The table's own row type recovers the column types.</b> <c>jsonb_populate_record</c>
    /// against <c>null::&lt;table&gt;</c> reads each key as the column's type, so a date is a date and
    /// a numeric a numeric without this code knowing either; a column added since the version was
    /// written reads null, which is what it was, and one dropped since is ignored. The geometry is
    /// put back in as its text form — hex EWKB, which the geometry type's input function reads — so
    /// the relation has every column the table has and a query written against the table runs
    /// against it unchanged.
    /// </para>
    /// <para>
    /// <b>The cost, stated rather than hidden:</b> a spatial filter on a historic read cannot use the
    /// history's spatial index, because the geometry it tests is the one rebuilt from text. The
    /// validity filter uses the index on the dates. ADR-078 condition 6 is where that gets measured.
    /// </para>
    /// </remarks>
    public static string AtMoment(LayerDefinition layer, string parameter)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);

        string table = layer.QuotedTable;
        string history = $"{LayerDefinition.Quote(layer.SchemaName)}.{LayerDefinition.Quote(layer.TableName + Suffix)}";
        string geometry = layer.GeometryColumn.Replace("'", "''", StringComparison.Ordinal);

        return $"(select p.* from {history} h, "
            + $"lateral jsonb_populate_record(null::{table}, "
            + $"h.attributes || jsonb_build_object('{geometry}', h.geom::text)) p "
            + $"where h.gdb_from_date <= @{parameter} "
            + $"and (h.gdb_to_date is null or h.gdb_to_date > @{parameter})) "
            + LayerDefinition.Quote(layer.TableName);
    }

    /// <summary>Whether this layer keeps its history: the table exists and the trigger is on it.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether history is on.</returns>
    /// <remarks>
    /// <b>Read from the database, never stored in the catalogue.</b> The trigger is the history; a
    /// flag somewhere else that said it was on while somebody had dropped the trigger in
    /// <c>psql</c> would be the one sentence on the page that was false.
    /// </remarks>
    public async Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a layer keeps its history, on a connection the caller holds.</summary>
    /// <param name="connection">An open connection to the layer's database.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether history is on.</returns>
    public static async Task<bool> IsOnAsync(
        NpgsqlConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(layer);

        const string Sql = """
            select exists (
                select 1
                from pg_trigger g
                join pg_class c on c.oid = g.tgrelid
                join pg_namespace n on n.oid = c.relnamespace
                where n.nspname = @schema and c.relname = @table
                  and g.tgname = 'graticula_history' and not g.tgisinternal)
              and to_regclass(format('%I.%I', @schema::text, @history::text)) is not null
            """;

        await using NpgsqlCommand command = new(Sql, connection);
        command.Parameters.AddWithValue("schema", layer.SchemaName);
        command.Parameters.AddWithValue("table", layer.TableName);
        command.Parameters.AddWithValue("history", layer.TableName + Suffix);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Turns history on: creates the companion table, copies every current row into it as the first
    /// version, and puts the trigger on the table.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many features history began with.</returns>
    /// <remarks>
    /// <para>
    /// <b>One transaction, under a lock that stops writers and not readers.</b> <c>share row
    /// exclusive</c> conflicts with every row write and with itself, and lets <c>select</c> through,
    /// so the copy and the trigger see the same table: a write cannot land after the copy and before
    /// the trigger, which would be a change with no version.
    /// </para>
    /// <para>
    /// <b>No statement timeout for this one transaction.</b> The pool's thirty seconds is right for a
    /// query somebody is waiting on; copying a large layer once is an administrator's deliberate
    /// act and is bounded by the table, not by a reader.
    /// </para>
    /// </remarks>
    public async Task<long> EnableAsync(CancellationToken cancellationToken)
    {
        RefuseOutsideHosted();

        if (_layer.IntegerIdentityColumn is null)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' has no integer object id, so a version cannot name the feature it is of.");
        }

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, "set local statement_timeout = 0", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, $"lock table {_layer.QuotedTable} in share row exclusive mode", cancellationToken)
            .ConfigureAwait(false);

        if (await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await CurrentCountAsync(cancellationToken).ConfigureAwait(false);
        }

        // A table left behind by a trigger somebody dropped by hand is a history with a hole in it;
        // starting again is the honest answer, and turning history off would have dropped it anyway.
        await ExecuteAsync(connection, transaction, $"drop table if exists {Qualified}", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            $"""
             create table {Qualified} (
                 history_id bigint generated always as identity primary key,
                 objectid bigint not null,
                 gdb_from_date timestamptz not null,
                 gdb_to_date timestamptz,
                 opened_by text not null check (opened_by in ('seed', 'insert', 'update')),
                 closed_by text check (closed_by in ('update', 'delete', 'truncate')),
                 from_editor text,
                 from_role text not null,
                 to_editor text,
                 to_role text,
                 attributes jsonb not null,
                 geom geometry,
                 restored_from bigint
             )
             """,
            cancellationToken).ConfigureAwait(false);

        long seeded;
        await using (NpgsqlCommand seed = new(
            $"""
             insert into {Qualified} (objectid, gdb_from_date, opened_by, from_role, attributes, geom)
             select t.{Id}, now(), 'seed', session_user, to_jsonb(t) - @geometry, t.{Geometry}
             from {_layer.QuotedTable} t
             """,
            connection,
            transaction))
        {
            seed.Parameters.AddWithValue("geometry", _layer.GeometryColumn);
            seeded = await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string name = _layer.TableName + Suffix;

        // After the history table, because the function's statements name it and PL/pgSQL checks
        // nothing until it first runs — but a reader of this method should not have to know that.
        await ExecuteAsync(connection, transaction, FunctionSql(), cancellationToken).ConfigureAwait(false);

        foreach (string statement in (string[])
                 [
                     $"create index {LayerDefinition.Quote(name + "_objectid")} on {Qualified} (objectid, gdb_from_date)",
                     $"create index {LayerDefinition.Quote(name + "_from")} on {Qualified} (gdb_from_date)",
                     $"create index {LayerDefinition.Quote(name + "_current")} on {Qualified} (objectid) where gdb_to_date is null",
                     $"create index {LayerDefinition.Quote(name + "_geom")} on {Qualified} using gist (geom)",
                     TriggerSql("insert or delete", null),
                     TriggerSql("update", "old.* is distinct from new.*"),
                     $"create trigger graticula_history_truncate after truncate on {_layer.QuotedTable} "
                         + $"for each statement execute function {QualifiedFunction}()",
                 ])
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return seeded;
    }

    /// <summary>
    /// Turns history off: removes the triggers and the history with them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>The history goes too, and the page says so before anybody presses it.</b> A history kept
    /// after its trigger stopped is a history with a hole in it that nothing marks, and the next
    /// time history is turned on it would begin again anyway.
    /// </remarks>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        RefuseOutsideHosted();

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (string statement in (string[])
                 [
                     $"drop trigger if exists graticula_history on {_layer.QuotedTable}",
                     $"drop trigger if exists graticula_history_update on {_layer.QuotedTable}",
                     $"drop trigger if exists graticula_history_truncate on {_layer.QuotedTable}",
                     $"drop table if exists {Qualified}",
                     $"drop function if exists {QualifiedFunction}()",
                 ])
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The layer's changes, newest first.</summary>
    /// <param name="before">Only changes strictly before this moment, for the next page; null for the newest.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The changes.</returns>
    /// <remarks>
    /// <para>
    /// <b>A change is a version beginning, or a feature ending.</b> Each version that opened is one
    /// change — an add if the feature had no earlier version that was ever true, an update if it
    /// did — and a version closed by a delete or a truncate is one more. A version that began and
    /// ended at the same instant was never true to anybody outside its transaction (a feature
    /// updated twice in one batch), and is left out.
    /// </para>
    /// <para>
    /// <b>The first versions are listed as <i>existed</i></b>, not as adds: they are the rows the
    /// table held when history was switched on, and nobody added them at that moment.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<FeatureChange>> ChangesAsync(
        DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        string sql =
            $"""
             with v as (
                 select h.*,
                        lag(h.history_id) over (partition by h.objectid order by h.gdb_from_date, h.history_id) as previous
                 from {Qualified} h
                 where h.gdb_to_date is null or h.gdb_to_date > h.gdb_from_date
             ),
             changes as (
                 select history_id, objectid, gdb_from_date as at,
                        case when opened_by = 'seed' then 'existed'
                             when restored_from is not null then 'restored'
                             when previous is null then 'added'
                             else 'updated' end as kind,
                        coalesce(from_editor, from_role) as editor, from_editor is null as direct
                 from v
                 union all
                 select history_id, objectid, gdb_to_date,
                        case closed_by when 'truncate' then 'emptied' else 'deleted' end,
                        coalesce(to_editor, to_role), to_editor is null
                 from v
                 where closed_by in ('delete', 'truncate')
             )
             select history_id, objectid, at, kind, editor, direct
             from changes
             where kind <> 'existed' and (@before::timestamptz is null or at < @before)
             order by at desc, history_id desc
             limit @limit
             """;

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));

        List<FeatureChange> changes = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new FeatureChange(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.GetBoolean(5)));
        }

        return changes;
    }

    /// <summary>One feature's versions, oldest first.</summary>
    /// <param name="objectId">The feature.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its versions; empty if it never had one.</returns>
    public async Task<IReadOnlyList<FeatureVersion>> VersionsAsync(long objectId, CancellationToken cancellationToken)
    {
        string sql =
            $"""
             select history_id, gdb_from_date, gdb_to_date, opened_by, closed_by,
                    coalesce(from_editor, from_role), from_editor is null,
                    coalesce(to_editor, to_role), to_editor is null,
                    attributes::text, st_asgeojson(geom), restored_from
             from {Qualified}
             where objectid = @id and (gdb_to_date is null or gdb_to_date > gdb_from_date)
             order by gdb_from_date, history_id
             """;

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", objectId);

        List<FeatureVersion> versions = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new FeatureVersion(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(7) && reader.GetBoolean(8),
                JsonDocument.Parse(reader.GetString(9)).RootElement.Clone(),
                reader.IsDBNull(10) ? null : reader.GetString(10))
            {
                RestoredFrom = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            });
        }

        return versions;
    }

    /// <summary>
    /// Puts a feature back as one of its versions was: the row is written back, or written again
    /// under its old object id if it has been deleted.
    /// </summary>
    /// <param name="objectId">The feature.</param>
    /// <param name="historyId">The version to restore.</param>
    /// <param name="editor">Who is restoring it, for the version the restore itself becomes.</param>
    /// <param name="tracking">The layer's editor-tracking columns, which carry the restorer rather than the version's editor.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <para>
    /// <b>Restore is an edit, and history is never rewritten.</b> The version's row is written to the
    /// table in a transaction that names the restorer, so the trigger records the restore as a new
    /// version like any other change, and restoring the version before it undoes it.
    /// </para>
    /// <para>
    /// <b>Every column, from the table's own row type</b> — the same <c>jsonb_populate_record</c> a
    /// historic read uses — so the restored row is the version, not the subset of it a client would
    /// have sent. The editor-tracking columns are then set to the restorer and now, because a tracked
    /// layer's <i>last edited</i> must say who last edited it.
    /// </para>
    /// <para>
    /// <b>Attachments are not in history</b> (ADR-078 §5.6): a deleted feature comes back without the
    /// files it had, and the outcome says so.
    /// </para>
    /// </remarks>
    public async Task<RestoreOutcome> RestoreAsync(
        long objectId,
        long historyId,
        string editor,
        EditorTracking? tracking,
        CancellationToken cancellationToken)
    {
        RefuseOutsideHosted();
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        EditorTracking track = tracking ?? EditorTracking.None;

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Who, and which version: the trigger writes both onto the version this restore becomes, so the
        // history says "put back" rather than passing a restore off as a hand edit.
        await using (NpgsqlCommand who = new(
            $"select set_config('{EditorSetting}', @editor, true), set_config('{RestoredSetting}', @version, true)",
            connection,
            transaction))
        {
            who.Parameters.AddWithValue("editor", editor);
            who.Parameters.AddWithValue("version", historyId.ToString(CultureInfo.InvariantCulture));
            await who.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string versionRow =
            $"(select jsonb_populate_record(null::{_layer.QuotedTable}, "
            + $"h.attributes || jsonb_build_object(@geometry, h.geom::text)) as r "
            + $"from {Qualified} h where h.history_id = @version and h.objectid = @id)";

        // Which columns the table has now: a version written before a column was dropped still
        // carries it in its JSON, and writing it back would name a column that is not there.
        List<string> columns = [];
        await using (NpgsqlCommand read = new(
            """
            select a.attname
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = @schema and c.relname = @table and a.attnum > 0 and not a.attisdropped
            order by a.attnum
            """,
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("schema", _layer.SchemaName);
            read.Parameters.AddWithValue("table", _layer.TableName);

            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        string id = _layer.IntegerIdentityColumn!;
        List<string> written = columns.FindAll(c => !string.Equals(c, id, StringComparison.Ordinal));

        bool exists;
        await using (NpgsqlCommand probe = new(
            $"select true from {_layer.QuotedTable} where {Id} = @id for update", connection, transaction))
        {
            probe.Parameters.AddWithValue("id", objectId);
            exists = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }

        bool versionExists;
        await using (NpgsqlCommand check = new(
            $"select exists (select 1 from {Qualified} where history_id = @version and objectid = @id)",
            connection,
            transaction))
        {
            check.Parameters.AddWithValue("version", historyId);
            check.Parameters.AddWithValue("id", objectId);
            versionExists = (bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!versionExists)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RestoreOutcome(RestoreResult.NoSuchVersion, false);
        }

        string quotedWritten = string.Join(", ", written.ConvertAll(LayerDefinition.Quote));
        string fromVersion = string.Join(", ", written.ConvertAll(c => $"(v.r).{LayerDefinition.Quote(c)}"));

        string sql = exists
            ? $"update {_layer.QuotedTable} t set ({quotedWritten}) = (select {fromVersion} from {versionRow} v) "
              + $"where t.{Id} = @id"
            : $"insert into {_layer.QuotedTable} ({Id}, {quotedWritten}) overriding system value "
              + $"select @id, {fromVersion} from {versionRow} v";

        await using (NpgsqlCommand write = new(sql, connection, transaction))
        {
            write.Parameters.AddWithValue("id", objectId);
            write.Parameters.AddWithValue("version", historyId);
            write.Parameters.AddWithValue("geometry", _layer.GeometryColumn);
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        List<string> stamps = [];
        if (track.Editor is not null) { stamps.Add($"{LayerDefinition.Quote(track.Editor)} = @editor"); }
        if (track.Edited is not null) { stamps.Add($"{LayerDefinition.Quote(track.Edited)} = now()"); }
        if (!exists && track.Creator is not null) { stamps.Add($"{LayerDefinition.Quote(track.Creator)} = @editor"); }
        if (!exists && track.Created is not null) { stamps.Add($"{LayerDefinition.Quote(track.Created)} = now()"); }

        if (stamps.Count > 0)
        {
            await using NpgsqlCommand stamp = new(
                $"update {_layer.QuotedTable} set {string.Join(", ", stamps)} where {Id} = @id",
                connection,
                transaction);
            stamp.Parameters.AddWithValue("id", objectId);
            stamp.Parameters.AddWithValue("editor", editor);
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RestoreOutcome(exists ? RestoreResult.Updated : RestoreResult.Recreated, !exists);
    }

    /// <summary>How many features are current in the history.</summary>
    private async Task<long> CurrentCountAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new($"select count(*) from {Qualified} where gdb_to_date is null", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>The table's own trigger function.</summary>
    private string QualifiedFunction =>
        $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName + FunctionSuffix)}";

    private string TriggerSql(string events, string? when)
    {
        string name = when is null ? "graticula_history" : "graticula_history_update";
        return $"create trigger {name} after {events} on {_layer.QuotedTable} for each row "
            + (when is null ? string.Empty : $"when ({when}) ")
            + $"execute function {QualifiedFunction}()";
    }

    /// <summary>
    /// The trigger function, one per archived table, with the table's names written into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One per table rather than one shared, and that was measured.</b> The first version was a
    /// single function per schema that took the column names as arguments and built its two statements
    /// with <c>format</c> and <c>execute</c> — which PL/pgSQL plans afresh on every row. Measured
    /// 2026-09-19 through <c>applyEdits</c> on a 600-polygon hosted layer: 500 updates in one batch took
    /// <b>274 ms without history and 561 ms with it</b>. Written out per table the statements are
    /// static, PL/pgSQL keeps their plans for the session, and the same batch is ADR-078 §4a's number.
    /// </para>
    /// <para>
    /// <b>The moment is the transaction's</b> — <c>now()</c> — so a batch of edits is one moment and a
    /// historic read never sees half of one. <b>But a version never ends before it began:</b> a
    /// transaction that started before another committed an edit to the same row, and then edits it
    /// itself, has an earlier <c>now()</c> than the version it closes. The close is held at the
    /// version's own start, which leaves a version that was never true — hidden by every reader —
    /// rather than one that ends before it begins.
    /// </para>
    /// <para>
    /// <b>Who:</b> the editor the writer named with <c>set_config</c>, which lasts exactly as long as
    /// its transaction, and always the database role. A write that did not come through this server
    /// has no editor, and the history says <i>database role</i> beside the role's name rather than
    /// pretending it knows the person.
    /// </para>
    /// </remarks>
    private string FunctionSql()
    {
        string id = LayerDefinition.Quote(_layer.IntegerIdentityColumn!);
        string geometry = LayerDefinition.Quote(_layer.GeometryColumn);
        string geometryKey = Literal(_layer.GeometryColumn);

        return $$"""
            create or replace function {{QualifiedFunction}}() returns trigger
            language plpgsql as $graticula$
            declare
                v_editor text := nullif(current_setting('{{EditorSetting}}', true), '');
                v_restored bigint := nullif(current_setting('{{RestoredSetting}}', true), '')::bigint;
                v_closed timestamptz;
            begin
                if tg_op = 'TRUNCATE' then
                    update {{Qualified}}
                       set gdb_to_date = greatest(now(), gdb_from_date), to_editor = v_editor,
                           to_role = session_user, closed_by = 'truncate'
                     where gdb_to_date is null;
                    return null;
                end if;

                if tg_op in ('UPDATE', 'DELETE') then
                    update {{Qualified}}
                       set gdb_to_date = greatest(now(), gdb_from_date), to_editor = v_editor,
                           to_role = session_user, closed_by = lower(tg_op)
                     where objectid = old.{{id}} and gdb_to_date is null
                    returning gdb_to_date into v_closed;
                end if;

                if tg_op in ('INSERT', 'UPDATE') then
                    insert into {{Qualified}} (objectid, gdb_from_date, opened_by, from_editor, from_role, attributes, geom,
                                               restored_from)
                    values (new.{{id}}, greatest(now(), coalesce(v_closed, now())), lower(tg_op), v_editor,
                            session_user, to_jsonb(new) - '{{geometryKey}}', new.{{geometry}}, v_restored);
                end if;

                return null;
            end
            $graticula$
            """;
    }

    private void RefuseOutsideHosted()
    {
        if (!string.Equals(_layer.SchemaName, PostGisImporter.HostedSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{_layer.SchemaName}.{_layer.TableName}' is not in the hosted schema. History is a trigger and a table "
                + "beside the layer's, and this server runs DDL only in the datastore it owns (ADR-002 §4.2).");
        }
    }

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>One change in a layer's history.</summary>
/// <param name="HistoryId">The version it opened or closed.</param>
/// <param name="ObjectId">The feature.</param>
/// <param name="At">When.</param>
/// <param name="Kind"><c>added</c>, <c>updated</c>, <c>restored</c>, <c>deleted</c> or <c>emptied</c>.</param>
/// <param name="Editor">Who: the account, or the database role when <paramref name="Direct"/>.</param>
/// <param name="Direct">Whether the write came straight to the database rather than through this server.</param>
public sealed record FeatureChange(long HistoryId, long ObjectId, DateTimeOffset At, string Kind, string Editor, bool Direct);

/// <summary>One version of a feature.</summary>
/// <param name="HistoryId">Its id.</param>
/// <param name="From">When it became true.</param>
/// <param name="To">When it stopped, or null while it is current.</param>
/// <param name="OpenedBy"><c>seed</c>, <c>insert</c> or <c>update</c>.</param>
/// <param name="ClosedBy"><c>update</c>, <c>delete</c>, <c>truncate</c>, or null while current.</param>
/// <param name="Editor">Who made it.</param>
/// <param name="Direct">Whether that write came straight to the database.</param>
/// <param name="EndedBy">Who ended it, or null.</param>
/// <param name="EndedDirect">Whether that write came straight to the database.</param>
/// <param name="Attributes">Its attributes, as stored.</param>
/// <param name="GeoJson">Its shape as GeoJSON, in the layer's reference, or null.</param>
public sealed record FeatureVersion(
    long HistoryId,
    DateTimeOffset From,
    DateTimeOffset? To,
    string OpenedBy,
    string? ClosedBy,
    string Editor,
    bool Direct,
    string? EndedBy,
    bool EndedDirect,
    JsonElement Attributes,
    string? GeoJson)
{
    /// <summary>The version a restore wrote back to make this one, or null when it was not a restore.</summary>
    public long? RestoredFrom { get; init; }
}

/// <summary>What a restore did.</summary>
public enum RestoreResult
{
    /// <summary>The feature existed and was written back as the version.</summary>
    Updated,

    /// <summary>The feature had been deleted and was written again under its old object id.</summary>
    Recreated,

    /// <summary>No such version of that feature.</summary>
    NoSuchVersion,
}

/// <summary>A restore's outcome.</summary>
/// <param name="Result">What happened.</param>
/// <param name="AttachmentsNotRestored">Whether a recreated feature came back without attachments it may have had.</param>
public sealed record RestoreOutcome(RestoreResult Result, bool AttachmentsNotRestored);
