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

            (List<SourceTable> tables, List<SkippedTable> skipped) = await TablesAsync(connection, cancellationToken).ConfigureAwait(false);

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

        foreach (var row in found)
        {
            string name = $"{row.Schema}.{row.Table}";

            if (row.Key is not { } key
                || await KeyTypeAsync(connection, row.Schema, row.Table, key, cancellation).ConfigureAwait(false) is not
                    ("tinyint" or "smallint" or "int" or "bigint"))
            {
                skipped.Add(new SkippedTable(name,
                    "It has no single-column primary key of a whole-number type, which ArcGIS needs as the object id."));
                continue;
            }

            (int? srid, long others, string? type) = await ShapesAsync(connection, row.Schema, row.Table, row.Column, cancellation)
                .ConfigureAwait(false);

            if (srid is not { } reference)
            {
                skipped.Add(new SkippedTable(name,
                    $"Its column {row.Column} holds no shape yet, and SQL Server keeps a reference per shape rather than per "
                    + "column, so the table's reference cannot be known until it has one."));
                continue;
            }

            if (others > 0)
            {
                skipped.Add(new SkippedTable(name,
                    $"Its column {row.Column} holds shapes in more than one reference: {others:N0} are not in EPSG:{reference}, "
                    + "the first one's. A layer is served in one."));
                continue;
            }

            tables.Add(new SourceTable(row.Schema, row.Table, row.Column, reference, type, key, key, [key],
                Writable: await SqlServerConnections.CanWriteAsync(connection, row.Schema, row.Table, cancellation).ConfigureAwait(false)));
        }

        tables.AddRange(await PlainTablesAsync(connection, cancellation).ConfigureAwait(false));
        return (tables, skipped);
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
