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
using Oracle.ManagedDataAccess.Client;

namespace Graticula.Providers.Oracle;

/// <summary>
/// A registered Oracle layer's history, kept by the database — ADR-207, the Oracle form of ADR-078's trigger.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database records it, not this server</b>, for ADR-078's reason: a registered table is exactly the one other
/// tools write to. A row trigger on the table writes every version into <c>&lt;TABLE&gt;__HISTORY</c> beside it, in the
/// writer's own transaction, with ArcGIS archiving's <c>GDB_FROM_DATE</c> and <c>GDB_TO_DATE</c>; the attributes as
/// JSON, the shape as an <c>SDO_GEOMETRY</c> with its own spatial index.
/// </para>
/// <para>
/// <b>Where it differs from the PostgreSQL trigger, and why.</b> Oracle has no <c>to_jsonb(new)</c>: a trigger names
/// the columns it records, so <b>the trigger is written with the table's columns as they are when history is switched
/// on</b>, and a column added later is recorded after history is switched off and on again. The editor is the
/// session's <c>CLIENT_IDENTIFIER</c>, which <see cref="OracleFeatureWriter"/> sets for its transaction as the PostGIS
/// writer sets <c>graticula.editor</c>; another tool's edit is recorded under the database user that made it. An
/// update is recorded as a version whether or not it changed a value, and a <c>TRUNCATE</c> is not recorded — a DDL
/// statement fires no row trigger.
/// </para>
/// <para>
/// <b>Made only when an administrator asks</b>, and only by the table's owner: the history table's spatial metadata is
/// the connected user's own, so a user editing another schema's table is refused with that reason.
/// </para>
/// </remarks>
public sealed class OracleFeatureHistory : IFeatureHistory
{
    /// <summary>The history table's suffix.</summary>
    public const string Suffix = "__HISTORY";

    /// <summary>What a history table's comment begins with when this server wrote it.</summary>
    private const string CommentPrefix = "graticula:";

    /// <summary>What a restore puts in the session's CLIENT_INFO, ahead of the version it writes back.</summary>
    private const string RestoredPrefix = "graticula.restored:";

    private readonly string _connectionString;
    private readonly LayerDefinition _layer;

    /// <summary>Creates the history for one layer.</summary>
    /// <param name="locator">The data source's <c>oracle:</c> locator.</param>
    /// <param name="layer">The layer.</param>
    public OracleFeatureHistory(string locator, LayerDefinition layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);

        _connectionString = OracleConnections.For(locator, pooling: true);
        _layer = layer;
    }

    /// <summary>The history table's name.</summary>
    public string TableName => _layer.TableName + Suffix;

    private string Qualified => QualifiedOf(_layer);

    private static string QualifiedOf(LayerDefinition layer) =>
        $"{LayerDefinition.Quote(layer.SchemaName)}.{LayerDefinition.Quote(layer.TableName + Suffix)}";

    private string Trigger => LayerDefinition.Quote(_layer.TableName + "__HIST_TRG");

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn ?? _layer.IdentityColumn);

    private string Shape => LayerDefinition.Quote(_layer.GeometryColumn);

    private Task<OracleConnection> OpenAsync(CancellationToken cancellationToken) =>
        OracleConnections.OpenAsync(_connectionString, cancellationToken);

    private static OracleCommand Command(string sql, OracleConnection connection) =>
        new(sql, connection) { BindByName = true, CommandTimeout = 0 };

    /// <summary>
    /// The relation a query at a moment reads instead of the table: every column the table has, as it was then —
    /// <c>PostGisFeatureHistory.AtMoment</c>'s Oracle form, with each attribute read back from the version's JSON as
    /// the type its column has.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="columns">The table's columns, and how each is kept: its field type, and a GUID's storage.</param>
    /// <param name="moment">The moment, as an SQL expression of a timestamp with time zone.</param>
    /// <returns>A parenthesised relation.</returns>
    internal static string AtMoment(
        LayerDefinition layer, IReadOnlyList<(string Name, FieldType Type, OracleGuids.Storage? Guid)> columns, string moment)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(columns);

        string id = layer.IntegerIdentityColumn ?? layer.IdentityColumn;
        StringBuilder select = new($"select h.OBJECTID as {LayerDefinition.Quote(id)}");

        foreach ((string name, FieldType type, OracleGuids.Storage? guid) in columns)
        {
            if (string.Equals(name, id, StringComparison.Ordinal) || string.Equals(name, layer.GeometryColumn, StringComparison.Ordinal))
            {
                continue;
            }

            string path = $"'$.\"{name}\"'";
            string value = (type, guid) switch
            {
                (FieldType.Guid, OracleGuids.Storage.Raw) => $"hextoraw(json_value(h.ATTRIBUTES, {path}))",
                (FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or FieldType.Double or FieldType.Single, _)
                    => $"json_value(h.ATTRIBUTES, {path} returning number)",
                (FieldType.Date, _) => $"json_value(h.ATTRIBUTES, {path} returning timestamp)",
                (FieldType.Binary, _) => "null",
                _ => $"json_value(h.ATTRIBUTES, {path} returning varchar2(4000))",
            };

            select.Append(", ").Append(value).Append(" as ").Append(LayerDefinition.Quote(name));
        }

        select.Append(", h.SHAPE as ").Append(LayerDefinition.Quote(layer.GeometryColumn))
              .Append(" from ").Append(QualifiedOf(layer)).Append(" h")
              .Append($" where h.GDB_FROM_DATE <= {moment} and (h.GDB_TO_DATE is null or h.GDB_TO_DATE > {moment})");

        return "(" + select + ")";
    }

    /// <inheritdoc/>
    public async Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a layer keeps its history: the trigger is there and enabled, and so is its table.</summary>
    internal static async Task<bool> IsOnAsync(OracleConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        await using OracleCommand command = Command(
            """
            select count(*) from all_triggers g
             where g.owner = :p_owner and g.trigger_name = :p_trigger and g.status = 'ENABLED'
               and exists (select 1 from all_tables t where t.owner = :p_owner and t.table_name = :p_history)
            """, connection);

        command.Parameters.Add("p_owner", layer.SchemaName);
        command.Parameters.Add("p_trigger", layer.TableName + "__HIST_TRG");
        command.Parameters.Add("p_history", layer.TableName + Suffix);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    /// <inheritdoc/>
    public async Task<HistoryKept?> KeptAsync(CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await KeptAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How a layer's history is kept, or null when it keeps none — for a describe on its own connection.</summary>
    internal static async Task<HistoryKept?> KeptAsync(OracleConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        if (layer.IntegerIdentityColumn is null || !await IsOnAsync(connection, layer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using OracleCommand command = Command(
            $"""
             select (select nvl(min(GDB_FROM_DATE), systimestamp) from {QualifiedOf(layer)}), systimestamp,
                    (select comments from all_tab_comments where owner = :p_owner and table_name = :p_history)
               from dual
             """, connection);

        command.Parameters.Add("p_owner", layer.SchemaName);
        command.Parameters.Add("p_history", layer.TableName + Suffix);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset first = reader.GetDateTimeOffset(0).ToUniversalTime();
        DateTimeOffset now = reader.GetDateTimeOffset(1).ToUniversalTime();
        int? keep = KeepDaysOf(reader.IsDBNull(2) ? null : reader.GetString(2));

        return keep is { } days && now.AddDays(-days) > first
            ? new HistoryKept(now.AddDays(-days), days)
            : new HistoryKept(first, keep);
    }

    /// <inheritdoc/>
    public async Task<long> EnableAsync(CancellationToken cancellationToken)
    {
        if (_layer.IntegerIdentityColumn is null)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' has no integer object id, so a version cannot name the feature it is of.");
        }

        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (OracleCommand who = Command("select user from dual", connection))
        {
            if (!string.Equals(
                    (string?)await who.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), _layer.SchemaName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"History is kept beside {_layer.SchemaName}.{_layer.TableName} by its owner, whose spatial metadata the "
                    + $"history's index needs, and this server connects to the database as another user. Register the data "
                    + $"source as {_layer.SchemaName} to keep this layer's history (ADR-207).");
            }
        }

        if (await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            return await CountCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<(string Name, string Type)> columns = await RecordedColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        string recorded = Json("t", columns);

        // A table left behind by a trigger somebody dropped by hand is a history with a hole in it; it starts again.
        foreach (string statement in (string[])
                 [
                     $"drop table if exists {Qualified} purge",
                     $"delete from user_sdo_geom_metadata where table_name = '{Literal(TableName)}'",
                     $"""
                      create table {Qualified} (
                        HISTORY_ID    number generated always as identity primary key,
                        OBJECTID      number not null,
                        GDB_FROM_DATE timestamp(6) with time zone not null,
                        GDB_TO_DATE   timestamp(6) with time zone,
                        OPENED_BY     varchar2(10) not null check (OPENED_BY in ('seed', 'insert', 'update')),
                        CLOSED_BY     varchar2(10) check (CLOSED_BY in ('update', 'delete', 'truncate')),
                        FROM_EDITOR   varchar2(256 char),
                        FROM_ROLE     varchar2(128 char) not null,
                        TO_EDITOR     varchar2(256 char),
                        TO_ROLE       varchar2(128 char),
                        ATTRIBUTES    clob not null check (ATTRIBUTES is json),
                        SHAPE         sdo_geometry,
                        RESTORED_FROM number
                      )
                      """,
                     $"""
                      insert into user_sdo_geom_metadata (table_name, column_name, diminfo, srid)
                      select '{Literal(TableName)}', 'SHAPE', m.diminfo, m.srid from all_sdo_geom_metadata m
                       where m.owner = '{Literal(_layer.SchemaName)}' and m.table_name = '{Literal(_layer.TableName)}'
                         and m.column_name = '{Literal(_layer.GeometryColumn)}'
                      """,
                     $"create index {LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName + "__HIST_OID")} on {Qualified} (OBJECTID, GDB_FROM_DATE)",
                     $"create index {LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName + "__HIST_FROM")} on {Qualified} (GDB_FROM_DATE)",
                     $"create index {LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(_layer.TableName + "__HIST_SX")} on {Qualified} (SHAPE) indextype is mdsys.spatial_index_v2",

                     // <b>The trigger before the seed</b>: a row edited while the seed runs is then recorded by the
                     // trigger, and the seed skips it — so the history begins now with no hole, without a table lock
                     // that DDL's implicit commits would let go of anyway.
                     TriggerSql(columns),
                 ])
        {
            await using OracleCommand command = Command(statement, connection);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException)
            {
                // DDL commits as it goes, so a refused trigger or index leaves the table made before it: taken away
                // again, so a refusal leaves the schema as it found it.
                await DisableAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        long seeded;

        await using (OracleCommand seed = Command(
            $"""
             insert into {Qualified} (OBJECTID, GDB_FROM_DATE, OPENED_BY, FROM_ROLE, ATTRIBUTES, SHAPE)
             select t.{Id}, systimestamp, 'seed', user, {recorded}, t.{Shape}
               from {_layer.QuotedTable} t
              where not exists (select 1 from {Qualified} h where h.OBJECTID = t.{Id})
             """, connection))
        {
            seeded = await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return seeded;
    }

    /// <inheritdoc/>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (string statement in (string[])
                 [
                     $"drop trigger if exists {LayerDefinition.Quote(_layer.SchemaName)}.{Trigger}",
                     $"drop table if exists {Qualified} purge",
                     $"delete from user_sdo_geom_metadata where table_name = '{Literal(TableName)}'",
                 ])
        {
            await using OracleCommand command = Command(statement, connection);
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

        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // A comment takes no bind; the text is a fixed prefix and an integer, written by this code alone.
        string comment = days is { } keep
            ? "'" + CommentPrefix + "{\"keepDays\":" + keep.ToString(CultureInfo.InvariantCulture) + "}'"
            : "''";

        await using OracleCommand command = Command($"comment on table {Qualified} is {comment}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes, from every history in this database that has a keeping period, the versions that ended before it —
    /// <c>PostGisFeatureHistory.PruneAsync</c>'s rule: a current version is never deleted.
    /// </summary>
    /// <param name="locator">The data source's <c>oracle:</c> locator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each history pruned, and how many versions went.</returns>
    public static async Task<IReadOnlyList<(string Table, long Versions)>> PruneAsync(string locator, CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

        List<(string Owner, string Table, int Days)> kept = [];

        await using (OracleCommand list = Command(
            "select owner, table_name, comments from all_tab_comments where table_name like '%\\_\\_HISTORY' escape '\\' and comments like 'graticula:%'",
            connection))
        await using (OracleDataReader reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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

        foreach ((string owner, string table, int days) in kept)
        {
            await using OracleCommand delete = Command(
                $"delete from {LayerDefinition.Quote(owner)}.{LayerDefinition.Quote(table)} "
                + "where GDB_TO_DATE is not null and GDB_TO_DATE < systimestamp - numtodsinterval(:p_days, 'DAY')",
                connection);
            delete.Parameters.Add("p_days", days);

            long gone = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (gone > 0)
            {
                pruned.Add(($"{owner}.{table}", gone));
            }
        }

        return pruned;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeatureChange>> ChangesAsync(DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using OracleCommand command = Command(
            $"""
             with v as (
               select h.*, lag(h.HISTORY_ID) over (partition by h.OBJECTID order by h.GDB_FROM_DATE, h.HISTORY_ID) as previous
                 from {Qualified} h
                where h.GDB_TO_DATE is null or h.GDB_TO_DATE > h.GDB_FROM_DATE),
             changes as (
               select HISTORY_ID, OBJECTID, GDB_FROM_DATE as at,
                      case when OPENED_BY = 'seed' then 'existed'
                           when RESTORED_FROM is not null then 'restored'
                           when previous is null then 'added'
                           else 'updated' end as kind,
                      nvl(FROM_EDITOR, FROM_ROLE) as editor, case when FROM_EDITOR is null then 1 else 0 end as direct
                 from v
               union all
               select HISTORY_ID, OBJECTID, GDB_TO_DATE,
                      case CLOSED_BY when 'truncate' then 'emptied' else 'deleted' end,
                      nvl(TO_EDITOR, TO_ROLE), case when TO_EDITOR is null then 1 else 0 end
                 from v
                where CLOSED_BY in ('delete', 'truncate'))
             select HISTORY_ID, OBJECTID, at, kind, editor, direct
               from changes
              where kind <> 'existed' and (:p_before is null or at < :p_before)
              order by at desc, HISTORY_ID desc
              fetch first :p_limit rows only
             """, connection);

        command.Parameters.Add("p_before", OracleDbType.TimeStampTZ, (object?)before ?? DBNull.Value, ParameterDirection.Input);
        command.Parameters.Add("p_limit", OracleDbType.Int32, Math.Clamp(limit, 1, 1000), ParameterDirection.Input);

        List<FeatureChange> changes = [];

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new FeatureChange(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.GetDateTimeOffset(2).ToUniversalTime(),
                reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) == 1));
        }

        return changes;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeatureVersion>> VersionsAsync(long objectId, CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using OracleCommand command = Command(
            $"""
             select h.HISTORY_ID, h.GDB_FROM_DATE, h.GDB_TO_DATE, h.OPENED_BY, h.CLOSED_BY,
                    nvl(h.FROM_EDITOR, h.FROM_ROLE), case when h.FROM_EDITOR is null then 1 else 0 end,
                    nvl(h.TO_EDITOR, h.TO_ROLE), case when h.TO_EDITOR is null then 1 else 0 end,
                    h.ATTRIBUTES,
                    case when h.SHAPE is null then null
                         when h.SHAPE.sdo_gtype < 4000 then sdo_util.to_geojson(h.SHAPE)
                         else sdo_util.to_geojson(sdo_cs.make_2d(h.SHAPE)) end,
                    h.RESTORED_FROM
               from {Qualified} h
              where h.OBJECTID = :p_id and (h.GDB_TO_DATE is null or h.GDB_TO_DATE > h.GDB_FROM_DATE)
              order by h.GDB_FROM_DATE, h.HISTORY_ID
             """, connection);

        command.Parameters.Add("p_id", OracleDbType.Int64, objectId, ParameterDirection.Input);

        List<FeatureVersion> versions = [];

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new FeatureVersion(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetDateTimeOffset(1).ToUniversalTime(),
                reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2).ToUniversalTime(),
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
    /// <b>An edit, recorded by the trigger as a new version that says what it restored</b> — the restorer in the
    /// session's CLIENT_IDENTIFIER, the version in its CLIENT_INFO, both cleared before the connection goes back. A
    /// recreated feature keeps its old object id where the key column lets a value be given; an identity column made
    /// <c>GENERATED ALWAYS</c> does not, and that restore is refused by name.
    /// </remarks>
    public async Task<RestoreOutcome> RestoreAsync(
        long objectId, long historyId, string editor, EditorTracking? tracking, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        EditorTracking track = tracking ?? EditorTracking.None;

        await using OracleConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<(string Name, string Type)> columns = await RecordedColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        HashSet<string> tracked = new(
            new[] { track.Editor, track.Edited }.Where(c => c is not null).Select(c => c!), StringComparer.Ordinal);

        List<string> set = [];
        List<string> insertColumns = [Id, Shape];
        List<string> insertValues = [":p_id", "g"];

        foreach ((string name, string type) in columns)
        {
            if (tracked.Contains(name))
            {
                continue;
            }

            string value = Read("a", name, type);
            set.Add($"{LayerDefinition.Quote(name)} = {value}");
            insertColumns.Add(LayerDefinition.Quote(name));
            insertValues.Add(value);
        }

        foreach ((string? column, string value) in new[] { (track.Editor, ":p_editor"), (track.Edited, "sys_extract_utc(systimestamp)") })
        {
            if (column is not null)
            {
                set.Add($"{LayerDefinition.Quote(column)} = {value}");
                insertColumns.Add(LayerDefinition.Quote(column));
                insertValues.Add(value);
            }
        }

        set.Add($"{Shape} = g");

        string block =
            $"""
             declare
               a clob;
               g sdo_geometry;
             begin
               dbms_session.set_identifier(:p_editor);
               dbms_application_info.set_client_info('{RestoredPrefix}' || to_char(:p_version));
               begin
                 select ATTRIBUTES, SHAPE into a, g from {Qualified} where HISTORY_ID = :p_version and OBJECTID = :p_id;
               exception when no_data_found then
                 :p_result := 0;
                 dbms_session.clear_identifier;
                 dbms_application_info.set_client_info(null);
                 return;
               end;
               update {_layer.QuotedTable} set {string.Join(", ", set)} where {Id} = :p_id;
               if sql%rowcount = 0 then
                 insert into {_layer.QuotedTable} ({string.Join(", ", insertColumns)}) values ({string.Join(", ", insertValues)});
                 :p_result := 2;
               else
                 :p_result := 1;
               end if;
               dbms_session.clear_identifier;
               dbms_application_info.set_client_info(null);
             exception when others then
               dbms_session.clear_identifier;
               dbms_application_info.set_client_info(null);
               raise;
             end;
             """;

        using OracleTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        await using OracleCommand command = Command(block, connection);

        command.Parameters.Add("p_editor", OracleDbType.Varchar2, editor, ParameterDirection.Input);
        command.Parameters.Add("p_version", OracleDbType.Int64, historyId, ParameterDirection.Input);
        command.Parameters.Add("p_id", OracleDbType.Int64, objectId, ParameterDirection.Input);
        OracleParameter result = command.Parameters.Add("p_result", OracleDbType.Int32, ParameterDirection.Output);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException e) when (OracleDataSourceProbe.NumberOf(e) == 32795)
        {
            transaction.Rollback();
            throw new InvalidOperationException(
                $"Feature {objectId} was deleted, and its key column '{_layer.IntegerIdentityColumn}' is an identity "
                + "column the database alone may fill (GENERATED ALWAYS), so it cannot come back under its old object id.", e);
        }

        transaction.Commit();

        return Convert.ToInt32(result.Value.ToString(), CultureInfo.InvariantCulture) switch
        {
            0 => new RestoreOutcome(RestoreResult.NoSuchVersion, false),
            2 => new RestoreOutcome(RestoreResult.Recreated, true),
            _ => new RestoreOutcome(RestoreResult.Updated, false),
        };
    }

    /// <summary>The sentence for a refusal the administrator can act on, or null — the right this needs, named.</summary>
    /// <param name="failure">What the database said.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The sentence, or null.</returns>
    public static string? Refusal(Exception failure, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(layer);

        return failure is OracleException oracle && OracleDataSourceProbe.NumberOf(oracle) is 1031 or 1950
            ? $"The database refused to create the history beside {layer.SchemaName}.{layer.TableName}: {oracle.Message}. "
              + "Keeping a history needs CREATE TABLE and CREATE TRIGGER and room in a tablespace for the user this server "
              + "connects as. Grant them, or keep the layer without history (ADR-207)."
            : null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The columns a version records: every column but the key and the shape, of a type JSON can carry.</summary>
    private async Task<IReadOnlyList<(string Name, string Type)>> RecordedColumnsAsync(
        OracleConnection connection, CancellationToken cancellationToken)
    {
        await using OracleCommand command = Command(
            """
            select column_name, data_type from all_tab_columns
             where owner = :p_owner and table_name = :p_table and data_type_owner is null
             order by column_id
            """, connection);

        command.Parameters.Add("p_owner", _layer.SchemaName);
        command.Parameters.Add("p_table", _layer.TableName);

        List<(string, string)> columns = [];

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            string type = reader.GetString(1);

            if (string.Equals(name, _layer.IntegerIdentityColumn ?? _layer.IdentityColumn, StringComparison.Ordinal)
                || type is "BLOB" or "LONG" or "LONG RAW" or "BFILE")
            {
                continue;
            }

            columns.Add((name, type));
        }

        return columns;
    }

    /// <summary>A row's recorded columns as one JSON object, from the row aliased or prefixed <paramref name="row"/>.</summary>
    private static string Json(string row, IReadOnlyList<(string Name, string Type)> columns) =>
        columns.Count == 0
            ? "'{}'"
            : "json_object("
              + string.Join(", ", columns.Select(c =>
                  $"'{Literal(c.Name)}' value "
                  + (c.Type == "RAW" ? $"rawtohex({row}.{LayerDefinition.Quote(c.Name)})" : $"{row}.{LayerDefinition.Quote(c.Name)}")))
              + " null on null returning clob)";

    /// <summary>A recorded column read back from a version's JSON in <paramref name="json"/>, as its column's type.</summary>
    private static string Read(string json, string name, string type)
    {
        string path = $"'$.\"{name}\"'";

        return type switch
        {
            "NUMBER" or "FLOAT" or "BINARY_DOUBLE" or "BINARY_FLOAT" => $"json_value({json}, {path} returning number)",
            "DATE" => $"json_value({json}, {path} returning date)",
            "RAW" => $"hextoraw(json_value({json}, {path}))",
            "CLOB" or "NCLOB" => $"json_value({json}, {path} returning clob)",
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) && type.Contains("TIME ZONE", StringComparison.Ordinal)
                => $"json_value({json}, {path} returning timestamp with time zone)",
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => $"json_value({json}, {path} returning timestamp)",
            _ => $"json_value({json}, {path} returning varchar2(4000))",
        };
    }

    /// <summary>The row trigger that writes every version, with the table's recorded columns named.</summary>
    private string TriggerSql(IReadOnlyList<(string Name, string Type)> columns) =>
        $"""
         create or replace trigger {LayerDefinition.Quote(_layer.SchemaName)}.{Trigger}
         after insert or update or delete on {_layer.QuotedTable}
         for each row
         declare
           v_now      timestamp(6) with time zone := systimestamp;
           v_editor   varchar2(256) := sys_context('USERENV', 'CLIENT_IDENTIFIER');
           v_role     varchar2(128) := sys_context('USERENV', 'SESSION_USER');
           v_info     varchar2(64)  := sys_context('USERENV', 'CLIENT_INFO');
           v_restored number;
           -- INSERTING and DELETING are PL/SQL's, not SQL's: read into variables before a statement uses them.
           v_opened   varchar2(10) := case when inserting then 'insert' else 'update' end;
           v_closed   varchar2(10) := case when deleting then 'delete' else 'update' end;
         begin
           if v_info like '{RestoredPrefix}%' then
             v_restored := to_number(substr(v_info, {RestoredPrefix.Length + 1}));
           end if;
           if updating or deleting then
             update {Qualified}
                set GDB_TO_DATE = v_now, CLOSED_BY = v_closed,
                    TO_EDITOR = v_editor, TO_ROLE = v_role
              where OBJECTID = :old.{Id} and GDB_TO_DATE is null;
           end if;
           if inserting or updating then
             insert into {Qualified} (OBJECTID, GDB_FROM_DATE, OPENED_BY, FROM_EDITOR, FROM_ROLE, ATTRIBUTES, SHAPE, RESTORED_FROM)
             values (:new.{Id}, v_now, v_opened, v_editor, v_role,
                     {Json(":new", columns)}, :new.{Shape}, v_restored);
           end if;
         end;
         """;

    private async Task<long> CountCurrentAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        await using OracleCommand command = Command($"select count(*) from {Qualified} where GDB_TO_DATE is null", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

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

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
