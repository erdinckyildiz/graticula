using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Platform.Admin;
using Microsoft.Data.SqlClient;

namespace Graticula.Providers.SqlServer;

/// <summary>
/// What a registered SQL Server database offers to publish — ADR-210: its tables with a <c>geometry</c> or
/// <c>geography</c> column whose rows hold one SRID, each with a single whole-number primary key for ArcGIS's object id.
/// </summary>
/// <remarks>
/// <para>
/// <b>SQL Server keeps a reference per value, not per column</b>, so a column's reference is read from its rows: an empty
/// table is skipped with that reason, and one whose rows disagree is skipped with how many disagree, rather than offered
/// and then served in two references at once.
/// </para>
/// </remarks>
public sealed class SqlServerDataSourceProbe : IDataSourceProbe
{
    /// <summary>The lowest database compatibility level served: 130 (SQL Server 2016) is the first with <c>OPENJSON</c>.</summary>
    public const int LowestCompatibility = 130;

    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        string driver;

        try
        {
            driver = SqlServerConnections.For(connectionString, pooling: false);
        }
        catch (ArgumentException)
        {
            return Refused(ProbeOutcome.CannotConnect, "A host, a database and a user are all needed to reach a SQL Server.");
        }

        try
        {
            await using SqlConnection connection = await SqlServerConnections.OpenAsync(driver, cancellationToken).ConfigureAwait(false);

            // <b>The catalogue is read without waiting on DDL's locks — D-314.</b> A probe only describes, so seeing a
            // table another session is creating or dropping costs nothing (a vanished one is passed over below), while
            // waiting for its locks made the probe a deadlock victim whenever DDL ran beside it.
            await using (SqlCommand dirty = new("set transaction isolation level read uncommitted", connection))
            {
                await dirty.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            string version = connection.ServerVersion;
            int level;

            await using (SqlCommand command = new("select compatibility_level from sys.databases where name = db_name()", connection))
            {
                level = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (level < LowestCompatibility)
            {
                return new ProbeResult(
                    ProbeOutcome.UnusableGeometry,
                    $"This database's compatibility level is {level}, and {LowestCompatibility} (SQL Server 2016) or later is "
                    + "needed: earlier levels have no OPENJSON, which every filter by object id is written with (ADR-210).",
                    version, null, []);
            }

            (List<SourceTable> tables, List<SkippedTable> skipped) =
                await RetriedOnDeadlockAsync(() => TablesAsync(connection, cancellationToken)).ConfigureAwait(false);

            int plain = tables.Count(t => t.GeometryColumn is null);

            string message = tables.Count == 0
                ? "Connected, and nothing here can be published: no table with a geometry or geography column whose rows "
                  + "hold one SRID, and none without one, with a single whole-number primary key, is in this database."
                : $"Connected. {tables.Count - plain} table{(tables.Count - plain == 1 ? "" : "s")} with geometry can be published"
                  + (plain > 0 ? $", and {plain} without, which publish as tables (ADR-213)." : ".");

            return new ProbeResult(ProbeOutcome.Usable, message, version, $"SQL Server spatial {version}", tables, skipped);
        }
        catch (SqlException e) when (e.Number is 18456 or 4060 or 916 or 229)
        {
            return Refused(ProbeOutcome.InsufficientPrivilege, "The database refused: " + e.Message);
        }
        catch (SqlException e)
        {
            SqlServerSource source = SqlServerLocator.Of(connectionString);
            return Refused(
                ProbeOutcome.CannotConnect,
                e.Number is -1 or 2 or 53 or 258 or 10060 or 10061 or 11001
                    ? $"Nothing answered at {source.Host}{(source.Port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty)}. "
                      + "Check the instance and the port, and that this server may reach it."
                    : "The database refused: " + e.Message);
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return Refused(ProbeOutcome.CannotConnect, "The database could not be reached: " + e.Message);
        }
    }

    /// <inheritdoc/>
    /// <remarks><b>The one database the registration names</b>, after checking it answers, as MySQL's probe does.</remarks>
    public async Task<DatabaseListing> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        ProbeResult probed = await ProbeAsync(connectionString, cancellationToken).ConfigureAwait(false);

        return probed.Outcome is ProbeOutcome.CannotConnect or ProbeOutcome.InsufficientPrivilege
            ? new DatabaseListing(probed.Outcome, probed.Message, [])
            : new DatabaseListing(
                ProbeOutcome.Usable,
                "A SQL Server source is one database; this is the one the connection names.",
                [SqlServerLocator.Of(connectionString).Database]);
    }

    /// <summary>
    /// Reads the catalogue again when SQL Server chose the read as a deadlock victim — D-314.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not supposed:</b> with a table created and dropped beside it, 19 probes of 20 failed, most as error
    /// 1205 on <c>INFORMATION_SCHEMA</c> — the catalogue's locks meeting the DDL's. A deadlock victim is told to rerun,
    /// and a probe has nothing half-done to undo, so it does, four times at most, waiting longer each time.
    /// </remarks>
    private static async Task<T> RetriedOnDeadlockAsync<T>(Func<Task<T>> read)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (SqlException e) when (e.Number == 1205 && attempt < 5)
            {
                await Task.Delay(100 * attempt).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(List<SourceTable> Tables, List<SkippedTable> Skipped)> TablesAsync(
        SqlConnection connection, CancellationToken cancellation)
    {
        // Every geometry and geography column, its table's kind, and the table's primary key when that key is one
        // column of a whole-number type.
        const string Sql = """
            select c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, t.TABLE_TYPE,
                   (select max(k.COLUMN_NAME)
                      from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                      join INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                        on k.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA and k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                     where tc.TABLE_SCHEMA = c.TABLE_SCHEMA and tc.TABLE_NAME = c.TABLE_NAME
                       and tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                    having count(*) = 1) as key_column
              from INFORMATION_SCHEMA.COLUMNS c
              join INFORMATION_SCHEMA.TABLES t on t.TABLE_SCHEMA = c.TABLE_SCHEMA and t.TABLE_NAME = c.TABLE_NAME
             where c.DATA_TYPE in ('geometry', 'geography')
             order by c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME
            """;

        List<(string Schema, string Table, string Column, string Type, string Kind, string? Key)> found = [];

        await using (SqlCommand command = new(Sql, connection))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        List<SourceTable> tables = [];
        List<SkippedTable> skipped = [];

        // ADR-217 condition 2: what publish refuses is not offered, and the sentence is the one it answers.
        IReadOnlyDictionary<string, string> refused = await GeodatabaseRefusalsAsync(connection, cancellation).ConfigureAwait(false);

        foreach (var row in found)
        {
            if (refused.TryGetValue($"{row.Schema}.{row.Table}".ToLowerInvariant(), out string? why))
            {
                skipped.Add(new SkippedTable($"{row.Schema}.{row.Table}", why));
                continue;
            }

            try
            {
                await DescribeAsync(connection, row, tables, skipped, cancellation).ConfigureAwait(false);
            }
            catch (SqlException e) when (e.Number == 208)
            {
                // D-314: listed, then dropped before it was read — by another tool, or another test. It is not in the
                // database any more, so it is not offered; failing the whole probe over it answered with no tables.
            }
        }

        foreach (SourceTable plain in await PlainTablesAsync(connection, cancellation).ConfigureAwait(false))
        {
            if (refused.TryGetValue($"{plain.SchemaName}.{plain.TableName}".ToLowerInvariant(), out string? why))
            {
                skipped.Add(new SkippedTable($"{plain.SchemaName}.{plain.TableName}", why));
            }
            else
            {
                tables.Add(plain);
            }
        }

        return (tables, skipped);
    }

    /// <summary>
    /// Every table an enterprise geodatabase in this database versions or archives, keyed by schema and table in lower
    /// case, with the sentence a publish of it answers — ADR-217 condition 2. Two reads for the whole database, the
    /// marker columns and the registry's versioned classes, as <see cref="SqlServerTableChecks.GeodatabaseRefusalAsync"/>
    /// reads them for one table; the sentence is <see cref="GeodatabaseClasses.Refusal"/>'s, so the listing and the
    /// publish cannot say two things.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> GeodatabaseRefusalsAsync(
        SqlConnection connection, CancellationToken cancellation)
    {
        Dictionary<string, (string Schema, string Table, List<string> Columns, long? Versioned)> marked = new(StringComparer.Ordinal);

        string markers = string.Join(", ", GeodatabaseClasses.MarkerColumns.Select(m => $"'{m}'"));

        await using (SqlCommand command = new(
            $"select TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME from INFORMATION_SCHEMA.COLUMNS where upper(COLUMN_NAME) in ({markers})",
            connection))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                string key = $"{reader.GetString(0)}.{reader.GetString(1)}".ToLowerInvariant();

                if (!marked.TryGetValue(key, out var entry))
                {
                    marked[key] = entry = (reader.GetString(0), reader.GetString(1), [], null);
                }

                entry.Columns.Add(reader.GetString(2));
            }
        }

        await using (SqlCommand command = new(
            """
            if object_id(N'sde.SDE_table_registry') is not null
                select r.owner, r.table_name, r.registration_id from sde.SDE_table_registry r
                 where object_id(quotename(r.owner) + N'.' + quotename(N'a' + cast(r.registration_id as nvarchar(20)))) is not null
            """, connection))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                string key = $"{reader.GetString(0)}.{reader.GetString(1)}".ToLowerInvariant();
                long id = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);

                marked[key] = marked.TryGetValue(key, out var entry)
                    ? entry with { Versioned = id }
                    : (reader.GetString(0), reader.GetString(1), [], id);
            }
        }

        Dictionary<string, string> refused = new(StringComparer.Ordinal);

        foreach ((string key, var entry) in marked)
        {
            if (GeodatabaseClasses.Refusal($"{entry.Schema}.{entry.Table}", entry.Columns, entry.Versioned) is { } why)
            {
                refused[key] = why;
            }
        }

        return refused;
    }

    /// <summary>One listed geometry table: offered, or skipped with the reason.</summary>
    private static async Task DescribeAsync(
        SqlConnection connection,
        (string Schema, string Table, string Column, string Type, string Kind, string? Key) row,
        List<SourceTable> tables,
        List<SkippedTable> skipped,
        CancellationToken cancellation)
    {
        string name = $"{row.Schema}.{row.Table}";

        if (row.Key is not { } key
            || await KeyTypeAsync(connection, row.Schema, row.Table, key, cancellation).ConfigureAwait(false) is not
                ("tinyint" or "smallint" or "int" or "bigint"))
        {
            skipped.Add(new SkippedTable(name,
                "It has no single-column primary key of a whole-number type, which ArcGIS needs as the object id."));
            return;
        }

        (int? srid, long others, string? type) = await ShapesAsync(connection, row.Schema, row.Table, row.Column, cancellation)
            .ConfigureAwait(false);

        if (srid is not { } reference)
        {
            skipped.Add(new SkippedTable(name,
                $"Its column {row.Column} holds no shape yet, and SQL Server keeps a reference per shape rather than per "
                + "column, so the table's reference cannot be known until it has one."));
            return;
        }

        if (others > 0)
        {
            skipped.Add(new SkippedTable(name,
                $"Its column {row.Column} holds shapes in more than one reference: {others:N0} are not in EPSG:{reference}, "
                + "the first one's. A layer is served in one."));
            return;
        }

        tables.Add(new SourceTable(row.Schema, row.Table, row.Column, reference, type, key, key, [key],
            Writable: await SqlServerConnections.CanWriteAsync(connection, row.Schema, row.Table, cancellation).ConfigureAwait(false)));
    }

    /// <summary>
    /// The tables and views with no geometry or geography column and a single whole-number primary key — ADR-213 condition
    /// 7: each publishes as a table. Never the ones this server keeps beside a layer's: its attachments and their chunks.
    /// </summary>
    private static async Task<List<SourceTable>> PlainTablesAsync(SqlConnection connection, CancellationToken cancellation)
    {
        const string Sql = """
            select t.TABLE_SCHEMA, t.TABLE_NAME, k.COLUMN_NAME
              from INFORMATION_SCHEMA.TABLES t
              join INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                on tc.TABLE_SCHEMA = t.TABLE_SCHEMA and tc.TABLE_NAME = t.TABLE_NAME and tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              join INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                on k.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA and k.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
              join INFORMATION_SCHEMA.COLUMNS c
                on c.TABLE_SCHEMA = t.TABLE_SCHEMA and c.TABLE_NAME = t.TABLE_NAME and c.COLUMN_NAME = k.COLUMN_NAME
             where c.DATA_TYPE in ('tinyint', 'smallint', 'int', 'bigint')
               and t.TABLE_SCHEMA not in ('sys', 'INFORMATION_SCHEMA')
               and t.TABLE_NAME not like '%[_][_]attach'
               and t.TABLE_NAME not like '%[_][_]attach[_]chunk'
               and not exists (
                 select 1 from INFORMATION_SCHEMA.COLUMNS g
                  where g.TABLE_SCHEMA = t.TABLE_SCHEMA and g.TABLE_NAME = t.TABLE_NAME
                    and g.DATA_TYPE in ('geometry', 'geography'))
               and (select count(*) from INFORMATION_SCHEMA.KEY_COLUMN_USAGE kk
                     where kk.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA and kk.CONSTRAINT_NAME = tc.CONSTRAINT_NAME) = 1
             order by t.TABLE_SCHEMA, t.TABLE_NAME
            """;

        List<(string Schema, string Table, string Key)> found = [];

        await using (SqlCommand command = new(Sql, connection))
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<SourceTable> tables = [];

        foreach ((string schema, string table, string key) in found)
        {
            tables.Add(new SourceTable(schema, table, null, 0, null, key, key, [key],
                Writable: await SqlServerConnections.CanWriteAsync(connection, schema, table, cancellation).ConfigureAwait(false)));
        }

        return tables;
    }

    private static async Task<string?> KeyTypeAsync(SqlConnection connection, string schema, string table, string column, CancellationToken cancellation)
    {
        await using SqlCommand command = new(
            "select DATA_TYPE from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @s and TABLE_NAME = @t and COLUMN_NAME = @c", connection);
        command.Parameters.AddWithValue("@s", schema);
        command.Parameters.AddWithValue("@t", table);
        command.Parameters.AddWithValue("@c", column);
        return await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// A column's reference as its first shape gives it, how many shapes are in another, and its one geometry type if it
    /// has one — read from the rows, since SQL Server declares none of the three on a column.
    /// </summary>
    internal static async Task<(int? Srid, long Others, string? Type)> ShapesAsync(
        SqlConnection connection, string schema, string table, string column, CancellationToken cancellation)
    {
        string shape = "t." + LayerDefinition.Quote(column);
        string from = $"{LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)}";

        await using SqlCommand command = new(
            $"""
            declare @srid int = (select top (1) {shape}.STSrid from {from} t where {shape} is not null);
            select @srid,
                   (select count_big(*) from {from} t where {shape} is not null and {shape}.STSrid <> @srid),
                   (select case when count(distinct {shape}.STGeometryType()) = 1 then max({shape}.STGeometryType()) end
                      from {from} t where {shape} is not null)
            """, connection)
        {
            CommandTimeout = 60,
        };

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        await reader.ReadAsync(cancellation).ConfigureAwait(false);

        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : GeometryTypeOf(reader.GetString(2)));
    }

    /// <summary>The ArcGIS-style geometry type an OGC type name names, or null for a collection, which holds any.</summary>
    internal static string? GeometryTypeOf(string type) => type switch
    {
        "Point" or "MultiPoint" or "LineString" or "MultiLineString" or "Polygon" or "MultiPolygon" => type,
        _ => null,
    };

    private static ProbeResult Refused(ProbeOutcome outcome, string message) => new(outcome, message, null, null, []);
}
