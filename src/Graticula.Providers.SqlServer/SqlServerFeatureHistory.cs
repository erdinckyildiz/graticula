using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Formats;
using Graticula.Geometries;
using Microsoft.Data.SqlClient;

namespace Graticula.Providers.SqlServer;

/// <summary>
/// A registered SQL Server layer's history, kept by the database — ADR-207 and ADR-210 condition 5, the T-SQL form of
/// <c>MySqlFeatureHistory</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database records it, not this server</b>, for ADR-078's reason: a registered table is exactly the one other
/// tools write to. A trigger on the table writes every version into <c>&lt;table&gt;__history</c> beside it, in the
/// writer's own transaction, with ArcGIS archiving's <c>gdb_from_date</c> and <c>gdb_to_date</c>; the attributes as
/// JSON (<c>FOR JSON</c>), the shape in a column of the layer's own type.
/// </para>
/// <para>
/// <b>Where it differs from MySQL's, and why.</b> A SQL Server trigger fires once per statement, for any of insert,
/// update and delete, so there is one, reading <c>inserted</c> and <c>deleted</c>, with one clock reading
/// (<c>SYSUTCDATETIME()</c>, a <c>datetime2(7)</c>) for the whole statement. The editor is
/// <c>SESSION_CONTEXT(N'graticula_editor')</c>, which <see cref="SqlServerFeatureWriter"/> sets for its transaction;
/// another tool's edit is recorded under the login that made it (<c>SUSER_SNAME()</c>). <b>A row deleted by a foreign
/// key's cascade is recorded</b> — SQL Server fires a table's triggers for cascaded actions, which MySQL does not. As on
/// MySQL and Oracle, the trigger names the table's columns as they are when history is switched on, an update is a
/// version whether or not it changed a value, and a <c>TRUNCATE</c> is not recorded. <b>The history's shape column has no
/// spatial index</b>: SQL Server's needs a bounding box for <c>geometry</c> that this code would have to guess, and a
/// read at a moment is a filter on the dates first.
/// </para>
/// <para>
/// <b>The keeping period is an extended property</b> named <c>graticula</c> on the history table — MySQL keeps it in the
/// table's comment, which SQL Server has no other form of.
/// </para>
/// </remarks>
public sealed class SqlServerFeatureHistory : IFeatureHistory
{
    /// <summary>The history table's suffix.</summary>
    public const string Suffix = "__history";

    /// <summary>The session key the writer sets to the editor's name.</summary>
    internal const string EditorKey = "graticula_editor";

    /// <summary>The session key a restore sets to the version it writes back.</summary>
    internal const string RestoredKey = "graticula_restored";

    /// <summary>The extended property that carries the keeping period.</summary>
    private const string Property = "graticula";

    /// <summary>Types <c>FOR JSON</c> cannot carry, or a restore cannot write back.</summary>
    private static readonly HashSet<string> Unrecorded = new(StringComparer.OrdinalIgnoreCase)
    {
        "geometry", "geography", "hierarchyid", "sql_variant", "image", "text", "ntext", "timestamp", "rowversion", "xml",
    };

    private readonly string _connectionString;
    private readonly LayerDefinition _layer;

    /// <summary>Creates the history for one layer.</summary>
    /// <param name="locator">The data source's <c>sqlserver:</c> locator.</param>
    /// <param name="layer">The layer.</param>
    public SqlServerFeatureHistory(string locator, LayerDefinition layer)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentNullException.ThrowIfNull(layer);

        _connectionString = SqlServerConnections.For(locator, pooling: true);
        _layer = layer;
    }

    /// <summary>A recorded column: its name, its declaration (<c>nvarchar(20)</c>), and whether a restore may write it.</summary>
    internal sealed record Recorded(string Name, string Declared, bool Writable);

    /// <summary>The history table's name.</summary>
    public string TableName => _layer.TableName + Suffix;

    private string Qualified => QualifiedOf(_layer);

    private static string QualifiedOf(LayerDefinition layer) =>
        $"{LayerDefinition.Quote(layer.SchemaName)}.{LayerDefinition.Quote(layer.TableName + Suffix)}";

    private static string TriggerName(LayerDefinition layer) => layer.TableName + "__hist";

    private string Trigger => $"{LayerDefinition.Quote(_layer.SchemaName)}.{LayerDefinition.Quote(TriggerName(_layer))}";

    private string Id => LayerDefinition.Quote(_layer.IntegerIdentityColumn ?? _layer.IdentityColumn);

    private string Shape => LayerDefinition.Quote(_layer.RequireGeometryColumn());

    private Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqlServerConnections.OpenAsync(_connectionString, cancellationToken);

    private static SqlCommand Command(string sql, SqlConnection connection, SqlTransaction? transaction = null) =>
        new(sql, connection, transaction) { CommandTimeout = 0 };

    /// <summary>
    /// A column's declaration from what <c>INFORMATION_SCHEMA.COLUMNS</c> says of it — what <c>OPENJSON … WITH</c> reads a
    /// recorded value back as.
    /// </summary>
    internal static string Declaration(string type, long? characters, int? precision, int? scale, int? datetimePrecision)
    {
        string t = type.ToLowerInvariant();

        return t switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
                $"{t}({(characters is null or -1 ? "max" : characters.Value.ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" =>
                $"{t}({(precision ?? 18).ToString(CultureInfo.InvariantCulture)}, {(scale ?? 0).ToString(CultureInfo.InvariantCulture)})",
            "datetime2" or "datetimeoffset" or "time" => $"{t}({(datetimePrecision ?? 7).ToString(CultureInfo.InvariantCulture)})",
            _ => t,
        };
    }

    /// <summary>
    /// The relation a query at a moment reads instead of the table: the layer's served columns as they were then — each
    /// attribute read back from the version's JSON as its column is declared.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="columns">The served columns and their declarations.</param>
    /// <param name="moment">The moment, as an SQL expression of a UTC <c>datetime2</c>.</param>
    /// <returns>A parenthesised relation.</returns>
    internal static string AtMoment(LayerDefinition layer, IReadOnlyList<(string Name, string Declared)> columns, string moment)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(columns);

        string id = layer.IntegerIdentityColumn ?? layer.IdentityColumn;
        string shape = layer.RequireGeometryColumn();
        (string Name, string Declared)[] read =
            [.. columns.Where(c => !string.Equals(c.Name, id, StringComparison.Ordinal) && !string.Equals(c.Name, shape, StringComparison.Ordinal))];

        StringBuilder select = new($"select h.objectid as {LayerDefinition.Quote(id)}");

        foreach ((string name, _) in read)
        {
            select.Append(", j.").Append(LayerDefinition.Quote(name));
        }

        select.Append(", h.shape as ").Append(LayerDefinition.Quote(shape)).Append(" from ").Append(QualifiedOf(layer)).Append(" h");

        if (read.Length > 0)
        {
            select.Append(" cross apply openjson(h.attributes) with (")
                  .Append(string.Join(", ", read.Select(c => $"{LayerDefinition.Quote(c.Name)} {c.Declared} {Path(c.Name)}")))
                  .Append(") j");
        }

        select.Append($" where h.gdb_from_date <= {moment} and (h.gdb_to_date is null or h.gdb_to_date > {moment})");

        return "(" + select + ")";
    }

    /// <summary>A moment as the literal <see cref="AtMoment"/> compares with.</summary>
    internal static string MomentLiteral(DateTimeOffset moment) =>
        "cast('" + moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "' as datetime2(7))";

    /// <inheritdoc/>
    public async Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a layer keeps its history: its trigger is on its table, and its history table is there.</summary>
    internal static async Task<bool> IsOnAsync(SqlConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            """
            select count(*) from sys.triggers g
             where g.parent_id = object_id(@table) and g.name = @trigger
               and object_id(@history, 'U') is not null
            """, connection);

        command.Parameters.AddWithValue("@table", layer.QuotedTable);
        command.Parameters.AddWithValue("@trigger", TriggerName(layer));
        command.Parameters.AddWithValue("@history", QualifiedOf(layer));

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    /// <inheritdoc/>
    public async Task<HistoryKept?> KeptAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await KeptAsync(connection, _layer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How a layer's history is kept, or null when it keeps none — for a describe on its own connection.</summary>
    internal static async Task<HistoryKept?> KeptAsync(SqlConnection connection, LayerDefinition layer, CancellationToken cancellationToken)
    {
        if (layer.IntegerIdentityColumn is null || layer.IsTable || !await IsOnAsync(connection, layer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using SqlCommand command = Command(
            $"""
             select coalesce((select min(gdb_from_date) from {QualifiedOf(layer)}), sysutcdatetime()), sysutcdatetime(),
                    (select cast(value as nvarchar(4000)) from sys.extended_properties
                      where class = 1 and major_id = object_id(@history) and minor_id = 0 and name = @property)
             """, connection);

        command.Parameters.AddWithValue("@history", QualifiedOf(layer));
        command.Parameters.AddWithValue("@property", Property);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        // As on MySQL — ADR-213 condition 7: a version keeps a shape, and a table has none.
        if (_layer.IsTable)
        {
            throw new InvalidOperationException(
                $"'{_layer.Name}' is a table in a SQL Server database, and a history here keeps a shape with every version; a "
                + "table has none. Keep this table's history in PostGIS, or keep it without one (ADR-213).");
        }

        if (_layer.IntegerIdentityColumn is null)
        {
            throw new InvalidOperationException(
                $"Layer '{_layer.Name}' has no integer object id, so a version cannot name the feature it is of.");
        }

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            return await CountCurrentAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Recorded> columns = await RecordedColumnsAsync(connection, null, cancellationToken).ConfigureAwait(false);

        // FOR JSON PATH reads a dot in an alias as a nested object, so such a column would be recorded somewhere its
        // read-back never looks. Refused by name rather than lost.
        if (columns.FirstOrDefault(c => c.Name.Contains('.', StringComparison.Ordinal)) is { } dotted)
        {
            throw new InvalidOperationException(
                $"Column '{dotted.Name}' of {_layer.SchemaName}.{_layer.TableName} has a '.' in its name, which SQL Server's "
                + "JSON cannot record as one value. Rename it, or keep the layer without history (ADR-207).");
        }

        string shapeType = await ShapeTypeAsync(connection, cancellationToken).ConfigureAwait(false);

        string json = columns.Count == 0
            ? "N'{}'"
            : "(select " + string.Join(", ", columns.Select(c => $"i.{LayerDefinition.Quote(c.Name)} as {LayerDefinition.Quote(c.Name)}"))
              + " for json path, without_array_wrapper, include_null_values)";

        // A table left behind by a trigger somebody dropped by hand is a history with a hole in it; it starts again.
        string[] statements =
        [
            $"drop trigger if exists {Trigger}",
            $"drop table if exists {Qualified}",
            $"""
             create table {Qualified} (
               history_id    bigint identity primary key,
               objectid      bigint not null,
               gdb_from_date datetime2(7) not null,
               gdb_to_date   datetime2(7) null,
               opened_by     varchar(10) not null check (opened_by in ('seed', 'insert', 'update')),
               closed_by     varchar(10) null check (closed_by in ('update', 'delete', 'truncate')),
               from_editor   nvarchar(256) null,
               from_role     nvarchar(128) not null,
               to_editor     nvarchar(256) null,
               to_role       nvarchar(128) null,
               attributes    nvarchar(max) not null check (isjson(attributes) = 1),
               shape         {shapeType} null,
               restored_from bigint null
             )
             """,
            $"create index {LayerDefinition.Quote(TableName + "_oid")} on {Qualified} (objectid, gdb_from_date)",
            $"create index {LayerDefinition.Quote(TableName + "_from")} on {Qualified} (gdb_from_date)",

            // The trigger before the seed, as on MySQL: a row edited while the seed runs is recorded by the trigger and the
            // seed skips it, so the history begins now with no hole.
            $"""
             create trigger {Trigger} on {_layer.QuotedTable} after insert, update, delete as
             begin
               set nocount on;
               declare @now datetime2(7) = sysutcdatetime();
               declare @editor nvarchar(256) = cast(session_context(N'{EditorKey}') as nvarchar(256));
               declare @restored bigint = cast(session_context(N'{RestoredKey}') as bigint);
               declare @role nvarchar(128) = suser_sname();
               update h set gdb_to_date = @now,
                      closed_by = case when exists (select 1 from inserted) then 'update' else 'delete' end,
                      to_editor = @editor, to_role = @role
                 from {Qualified} h join deleted d on h.objectid = d.{Id}
                where h.gdb_to_date is null;
               insert into {Qualified} (objectid, gdb_from_date, opened_by, from_editor, from_role, attributes, shape, restored_from)
               select i.{Id}, @now, case when exists (select 1 from deleted) then 'update' else 'insert' end, @editor, @role,
                      {json}, i.{Shape}, @restored
                 from inserted i;
             end
             """,
        ];

        try
        {
            foreach (string statement in statements)
            {
                await using SqlCommand command = Command(statement, connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqlException)
        {
            // Each statement commits as it goes, so a refused trigger leaves the table made before it: taken away again,
            // so a refusal leaves the database as it found it.
            await DisableAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        string seedJson = json.Replace("i.", "t.", StringComparison.Ordinal);

        await using SqlCommand seed = Command(
            $"""
             insert into {Qualified} (objectid, gdb_from_date, opened_by, from_role, attributes, shape)
             select t.{Id}, sysutcdatetime(), 'seed', suser_sname(), {seedJson}, t.{Shape}
               from {_layer.QuotedTable} t
              where not exists (select 1 from {Qualified} h where h.objectid = t.{Id})
             """, connection);

        return await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (string statement in (string[])[$"drop trigger if exists {Trigger}", $"drop table if exists {Qualified}"])
        {
            await using SqlCommand command = Command(statement, connection);
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

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!await IsOnAsync(connection, _layer, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using SqlCommand command = Command(
            """
            if exists (select 1 from sys.extended_properties
                        where class = 1 and major_id = object_id(@history) and minor_id = 0 and name = @property)
              exec sys.sp_dropextendedproperty @name = @property, @level0type = N'SCHEMA', @level0name = @schema,
                   @level1type = N'TABLE', @level1name = @table;
            if @value is not null
              exec sys.sp_addextendedproperty @name = @property, @value = @value, @level0type = N'SCHEMA', @level0name = @schema,
                   @level1type = N'TABLE', @level1name = @table;
            """, connection);

        command.Parameters.AddWithValue("@history", Qualified);
        command.Parameters.AddWithValue("@property", Property);
        command.Parameters.AddWithValue("@schema", _layer.SchemaName);
        command.Parameters.AddWithValue("@table", TableName);
        command.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, 4000)
        {
            Value = days is { } keep ? "{\"keepDays\":" + keep.ToString(CultureInfo.InvariantCulture) + "}" : DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes, from every history in this database that has a keeping period, the versions that ended before it —
    /// <c>PostGisFeatureHistory.PruneAsync</c>'s rule: a current version is never deleted.
    /// </summary>
    /// <param name="locator">The data source's <c>sqlserver:</c> locator.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Each history pruned, and how many versions went.</returns>
    public static async Task<IReadOnlyList<(string Table, long Versions)>> PruneAsync(string locator, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await SqlServerConnections.OpenAsync(
            SqlServerConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

        List<(string Schema, string Table, int Days)> kept = [];

        await using (SqlCommand list = Command(
            """
            select s.name, t.name, cast(p.value as nvarchar(4000))
              from sys.extended_properties p
              join sys.tables t on t.object_id = p.major_id
              join sys.schemas s on s.schema_id = t.schema_id
             where p.class = 1 and p.minor_id = 0 and p.name = N'graticula' and t.name like N'%\_\_history' escape N'\'
            """,
            connection))
        await using (SqlDataReader reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (KeepDaysOf(reader.IsDBNull(2) ? null : reader.GetString(2)) is { } days)
                {
                    kept.Add((reader.GetString(0), reader.GetString(1), days));
                }
            }
        }

        List<(string Table, long Versions)> pruned = [];

        foreach ((string schema, string table, int days) in kept)
        {
            await using SqlCommand delete = Command(
                $"delete from {LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)} "
                + "where gdb_to_date is not null and gdb_to_date < dateadd(day, -@days, sysutcdatetime())",
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
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(
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
             select top (@limit) history_id, objectid, at, kind, editor, direct
               from changes
              where kind <> 'existed' and (@before is null or at < @before)
              order by at desc, history_id desc
             """, connection);

        command.Parameters.Add(new SqlParameter("@before", SqlDbType.DateTime2) { Value = before is { } b ? b.UtcDateTime : DBNull.Value });
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));

        List<FeatureChange> changes = [];

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new FeatureChange(
                reader.GetInt64(0),
                reader.GetInt64(1),
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
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // SQL Server writes no GeoJSON, so the shape crosses as WKB — longitude first, as the source reads it — and is
        // written here.
        await using SqlCommand command = Command(
            $"""
             select h.history_id, h.gdb_from_date, h.gdb_to_date, h.opened_by, h.closed_by,
                    coalesce(h.from_editor, h.from_role), case when h.from_editor is null then 1 else 0 end,
                    coalesce(h.to_editor, h.to_role), case when h.to_editor is null then 1 else 0 end,
                    h.attributes, h.shape.STAsBinary(), h.restored_from
               from {Qualified} h
              where h.objectid = @id and (h.gdb_to_date is null or h.gdb_to_date > h.gdb_from_date)
              order by h.gdb_from_date, h.history_id
             """, connection);

        command.Parameters.AddWithValue("@id", objectId);

        List<FeatureVersion> versions = [];

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new FeatureVersion(
                reader.GetInt64(0),
                Utc(reader.GetDateTime(1)),
                reader.IsDBNull(2) ? null : Utc(reader.GetDateTime(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1,
                JsonDocument.Parse(reader.GetString(9)).RootElement.Clone(),
                reader.IsDBNull(10) ? null : GeoJsonOf((byte[])reader.GetValue(10)))
            {
                RestoredFrom = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            });
        }

        return versions;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>An edit, recorded by the trigger as a new version that says what it restored</b> — the restorer in
    /// <c>SESSION_CONTEXT(N'graticula_editor')</c>, the version in <c>graticula_restored</c>, both cleared before the
    /// connection goes back. The version is written back in one statement that reads it from the history, so its shape
    /// never leaves the database. A recreated feature keeps its old object id: <c>IDENTITY_INSERT</c> is switched on for
    /// that statement when the key is an identity column, which needs <c>ALTER</c> on the table.
    /// </remarks>
    public async Task<RestoreOutcome> RestoreAsync(
        long objectId, long historyId, string editor, EditorTracking? tracking, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editor);

        EditorTracking track = tracking ?? EditorTracking.None;

        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (SqlCommand exists = Command(
            $"select count(*) from {Qualified} where history_id = @version and objectid = @id", connection, transaction))
        {
            exists.Parameters.AddWithValue("@version", historyId);
            exists.Parameters.AddWithValue("@id", objectId);

            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
            {
                return new RestoreOutcome(RestoreResult.NoSuchVersion, false);
            }
        }

        IReadOnlyList<Recorded> columns = await RecordedColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        bool identity = await IsIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        HashSet<string> tracked = new(
            new[] { track.Editor, track.Edited }.Where(c => c is not null).Select(c => c!), StringComparer.Ordinal);

        Recorded[] written = [.. columns.Where(c => c.Writable && !tracked.Contains(c.Name))];

        List<string> set = [.. written.Select(c => $"t.{LayerDefinition.Quote(c.Name)} = j.{LayerDefinition.Quote(c.Name)}")];
        List<string> insertColumns = [Id, Shape, .. written.Select(c => LayerDefinition.Quote(c.Name))];
        List<string> insertValues = ["h.objectid", "h.shape", .. written.Select(c => $"j.{LayerDefinition.Quote(c.Name)}")];

        foreach ((string? column, string value) in new[] { (track.Editor, "@editor"), (track.Edited, "sysutcdatetime()") })
        {
            if (column is not null)
            {
                set.Add($"t.{LayerDefinition.Quote(column)} = {value}");
                insertColumns.Add(LayerDefinition.Quote(column));
                insertValues.Add(value);
            }
        }

        set.Add($"t.{Shape} = h.shape");

        string apply = written.Length == 0
            ? string.Empty
            : " cross apply openjson(h.attributes) with ("
              + string.Join(", ", written.Select(c => $"{LayerDefinition.Quote(c.Name)} {c.Declared} {Path(c.Name)}"))
              + ") j";

        RestoreResult result;

        try
        {
            await SetContextAsync(connection, transaction, editor, historyId, cancellationToken).ConfigureAwait(false);

            await using SqlCommand update = Command(
                $"update t set {string.Join(", ", set)} from {_layer.QuotedTable} t "
                + $"join {Qualified} h on h.history_id = @version and h.objectid = @id{apply} where t.{Id} = @id",
                connection,
                transaction);
            update.Parameters.AddWithValue("@version", historyId);
            update.Parameters.AddWithValue("@id", objectId);
            update.Parameters.AddWithValue("@editor", editor);

            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                result = RestoreResult.Updated;
            }
            else
            {
                string insert =
                    $"insert into {_layer.QuotedTable} ({string.Join(", ", insertColumns)}) "
                    + $"select {string.Join(", ", insertValues)} from {Qualified} h{apply} where h.history_id = @version and h.objectid = @id";

                await using SqlCommand recreate = Command(
                    identity
                        ? $"set identity_insert {_layer.QuotedTable} on; {insert}; set identity_insert {_layer.QuotedTable} off;"
                        : insert,
                    connection,
                    transaction);
                recreate.Parameters.AddWithValue("@version", historyId);
                recreate.Parameters.AddWithValue("@id", objectId);
                recreate.Parameters.AddWithValue("@editor", editor);
                await recreate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                result = RestoreResult.Recreated;
            }
        }
        finally
        {
            // Best effort: a transaction the failure doomed refuses even this, and the pool's reset clears the context
            // when the connection goes back, so the first failure is the one the caller hears.
            try
            {
                await SetContextAsync(connection, transaction, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqlException)
            {
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RestoreOutcome(result, result == RestoreResult.Recreated);
    }

    /// <summary>Names the editor, and a restore's version, to the session for a history trigger; null clears them.</summary>
    internal static async Task SetContextAsync(
        SqlConnection connection, SqlTransaction transaction, string? editor, long? restored, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            $"exec sys.sp_set_session_context @key = N'{EditorKey}', @value = @editor; "
            + $"exec sys.sp_set_session_context @key = N'{RestoredKey}', @value = @restored;",
            connection,
            transaction);
        command.Parameters.Add(new SqlParameter("@editor", SqlDbType.NVarChar, 256) { Value = (object?)editor ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@restored", SqlDbType.BigInt) { Value = (object?)restored ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            SqlException { Number: 262 or 229 or 230 or 1088 or 3701 or 15247 or 8104 } denied =>
                $"The database refused to create the history beside {layer.SchemaName}.{layer.TableName}: {denied.Message} "
                + "Keeping a history needs CREATE TABLE in the database and ALTER on the schema for the login this server "
                + "connects as (a trigger on a table needs ALTER on it). Grant them, or keep the layer without history (ADR-207).",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The columns a version records: every column but the key and the shape, of a type JSON can carry; whether a restore
    /// writes one back is whether it is neither computed nor an identity.
    /// </summary>
    private async Task<IReadOnlyList<Recorded>> RecordedColumnsAsync(
        SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            """
            select c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.DATETIME_PRECISION,
                   columnproperty(object_id(@table), c.COLUMN_NAME, 'IsComputed'),
                   columnproperty(object_id(@table), c.COLUMN_NAME, 'IsIdentity')
              from INFORMATION_SCHEMA.COLUMNS c
             where c.TABLE_SCHEMA = @s and c.TABLE_NAME = @t
             order by c.ORDINAL_POSITION
            """, connection, transaction);

        command.Parameters.AddWithValue("@table", _layer.QuotedTable);
        command.Parameters.AddWithValue("@s", _layer.SchemaName);
        command.Parameters.AddWithValue("@t", _layer.TableName);

        List<Recorded> columns = [];

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            string type = reader.GetString(1);

            if (string.Equals(name, _layer.IntegerIdentityColumn ?? _layer.IdentityColumn, StringComparison.Ordinal)
                || string.Equals(name, _layer.RequireGeometryColumn(), StringComparison.Ordinal)
                || Unrecorded.Contains(type))
            {
                continue;
            }

            string declared = Declaration(
                type,
                reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture));

            bool computed = !reader.IsDBNull(6) && Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 1;
            bool identity = !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1;

            columns.Add(new Recorded(name, declared, !computed && !identity));
        }

        return columns;
    }

    private async Task<bool> IsIdentityAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            "select columnproperty(object_id(@table), @column, 'IsIdentity')", connection, transaction);
        command.Parameters.AddWithValue("@table", _layer.QuotedTable);
        command.Parameters.AddWithValue("@column", _layer.IntegerIdentityColumn ?? _layer.IdentityColumn);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is { } found and not DBNull
            && Convert.ToInt32(found, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>The layer's shape column's type — <c>geometry</c> or <c>geography</c> — which the history's takes.</summary>
    private async Task<string> ShapeTypeAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            "select DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME = @c",
            connection);
        command.Parameters.AddWithValue("@s", _layer.SchemaName);
        command.Parameters.AddWithValue("@t", _layer.TableName);
        command.Parameters.AddWithValue("@c", _layer.RequireGeometryColumn());

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string type
            && type.ToLowerInvariant() is "geometry" or "geography"
                ? type.ToLowerInvariant()
                : throw new InvalidOperationException(
                    $"{_layer.SchemaName}.{_layer.TableName}.{_layer.RequireGeometryColumn()} is not a geometry or geography column.");
    }

    /// <summary>A JSON path to a top-level member, as a T-SQL literal.</summary>
    private static string Path(string name) =>
        "N'$.\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal) + "\"'";

    private static string? GeoJsonOf(byte[] wkb)
    {
        Geometry geometry = WkbReader.Read(wkb);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            GeoJsonWriter.WriteGeometry(json, geometry);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<long> CountCurrentAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command($"select count_big(*) from {Qualified} where gdb_to_date is null", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Utc(DateTime stored) => new(DateTime.SpecifyKind(stored, DateTimeKind.Utc));

    private static int? KeepDaysOf(string? said)
    {
        if (said is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(said);
            return document.RootElement.TryGetProperty("keepDays", out JsonElement days)
                && days.TryGetInt32(out int value) && value is >= 1 and <= 36_500
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
