using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>
/// What a registered MySQL database offers to publish — ADR-208: its tables with a spatial column carrying an
/// <c>SRID</c> attribute and a spatial index, each with a single whole-number key for ArcGIS's object id.
/// </summary>
/// <remarks>
/// <b>The SRID attribute and the index are both required</b>, and a table without either is listed as skipped with
/// the reason rather than offered and then failing: without an SRID a column may hold shapes of any reference, and
/// without a spatial index <c>MBRIntersects</c> reads every row.
/// </remarks>
public sealed class MySqlDataSourceProbe : IDataSourceProbe
{
    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        string driver;

        try
        {
            driver = MySqlConnections.For(connectionString, pooling: false);
        }
        catch (ArgumentException)
        {
            // Said as what is missing, not as the argument exception's own text, which ends "(Parameter 'stored')"
            // — a design review found that tail in the console's red box.
            return Refused(ProbeOutcome.CannotConnect, "A host, a port, a database and a user are all needed to reach a MySQL server.");
        }

        try
        {
            await using MySqlConnection connection = await MySqlConnections.OpenAsync(driver, cancellationToken).ConfigureAwait(false);

            string version = connection.ServerVersion;

            if (!int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int major) || major < 8)
            {
                return new ProbeResult(
                    ProbeOutcome.UnusableGeometry,
                    $"This is MySQL {version}, and MySQL 8.0 or later is needed: earlier versions keep no spatial reference "
                    + "per column. A MariaDB server is not MySQL here (ADR-208).",
                    version, null, []);
            }

            (List<SourceTable> tables, List<SkippedTable> skipped) =
                await TablesAsync(connection, connection.Database, cancellationToken).ConfigureAwait(false);

            string message = tables.Count == 0
                ? "Connected, and nothing here can be published: no table with a spatial column that has an SRID and a "
                  + "spatial index, and a single whole-number key, is in this database."
                : $"Connected. {tables.Count} table{(tables.Count == 1 ? "" : "s")} can be published.";

            return new ProbeResult(ProbeOutcome.Usable, message, version, "MySQL spatial " + version, tables, skipped);
        }
        catch (MySqlException e) when (e.ErrorCode is MySqlErrorCode.UnableToConnectToHost)
        {
            // Nothing answered, so nothing refused: said as where it looked — a design review read "The database
            // refused" over a port nothing listens on.
            MySqlSource source = MySqlLocator.Of(connectionString);
            return Refused(
                ProbeOutcome.CannotConnect,
                $"Nothing answered at {source.Host}:{source.Port}. Check the instance and the port, and that this server may reach it.");
        }
        catch (MySqlException e)
        {
            return Refused(
                e.ErrorCode is MySqlErrorCode.AccessDenied or MySqlErrorCode.DatabaseAccessDenied
                    ? ProbeOutcome.InsufficientPrivilege
                    : ProbeOutcome.CannotConnect,
                "The database refused: " + e.Message);
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return Refused(ProbeOutcome.CannotConnect, "The database could not be reached: " + e.Message);
        }
    }

    /// <inheritdoc/>
    /// <remarks><b>The one database the registration names</b>, after checking it answers, as Oracle's probe does.</remarks>
    public async Task<DatabaseListing> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        ProbeResult probed = await ProbeAsync(connectionString, cancellationToken).ConfigureAwait(false);

        return probed.Outcome is ProbeOutcome.CannotConnect or ProbeOutcome.InsufficientPrivilege
            ? new DatabaseListing(probed.Outcome, probed.Message, [])
            : new DatabaseListing(
                ProbeOutcome.Usable,
                "A MySQL source is one database; this is the one the connection names.",
                [MySqlLocator.Of(connectionString).Database]);
    }

    private static async Task<(List<SourceTable> Tables, List<SkippedTable> Skipped)> TablesAsync(
        MySqlConnection connection, string database, CancellationToken cancellation)
    {
        // Every spatial column of the database, its SRID and declared type, whether a spatial index covers it, and the
        // table's primary key when that key is one column of a whole-number type.
        const string Sql = """
            select g.table_name, g.column_name, g.srs_id, g.geometry_type_name,
                   (select count(*) from information_schema.statistics s
                     where s.table_schema = g.table_schema and s.table_name = g.table_name
                       and s.column_name = g.column_name and s.index_type = 'SPATIAL') as indexed,
                   (select max(k.column_name) from information_schema.key_column_usage k
                     where k.table_schema = g.table_schema and k.table_name = g.table_name and k.constraint_name = 'PRIMARY'
                    having count(*) = 1) as key_column,
                   (select c.data_type from information_schema.columns c
                     where c.table_schema = g.table_schema and c.table_name = g.table_name
                       and c.column_name = (select max(k.column_name) from information_schema.key_column_usage k
                                             where k.table_schema = g.table_schema and k.table_name = g.table_name
                                               and k.constraint_name = 'PRIMARY')) as key_type,
                   (select t.table_type from information_schema.tables t
                     where t.table_schema = g.table_schema and t.table_name = g.table_name) as table_type
              from information_schema.st_geometry_columns g
             where g.table_schema = @db
             order by g.table_name, g.column_name
            """;

        List<(string Table, string Column, int? Srid, string Type, bool Indexed, string? Key, string? KeyType, string? Kind)> found = [];

        await using (MySqlCommand command = new(Sql, connection))
        {
            command.Parameters.AddWithValue("@db", database);

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                found.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                    reader.GetString(3),
                    Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture) > 0,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        List<SourceTable> tables = [];
        List<SkippedTable> skipped = [];

        foreach (var row in found)
        {
            string name = $"{database}.{row.Table}";

            if (row.Srid is not { } srid || srid == 0)
            {
                skipped.Add(new SkippedTable(name,
                    $"Its column {row.Column} has no SRID attribute, so its coordinates' reference is unknown. Declare it — "
                    + $"ALTER TABLE … MODIFY {row.Column} {row.Type} NOT NULL SRID 4326 — and give it a spatial index."));
                continue;
            }

            if (!row.Indexed)
            {
                skipped.Add(new SkippedTable(name,
                    $"Its column {row.Column} has no spatial index, which a spatial filter needs. Create one with "
                    + $"CREATE SPATIAL INDEX … ON {row.Table} ({row.Column})."));
                continue;
            }

            if (row.Key is not { } key || row.KeyType is not ("tinyint" or "smallint" or "mediumint" or "int" or "bigint"))
            {
                skipped.Add(new SkippedTable(name,
                    "It has no single-column primary key of a whole-number type, which ArcGIS needs as the object id."));
                continue;
            }

            tables.Add(new SourceTable(
                database,
                row.Table,
                row.Column,
                srid,
                GeometryTypeOf(row.Type),
                key,
                key,
                [key],
                Writable: row.Kind == "BASE TABLE"
                    && await MySqlConnections.CanWriteAsync(connection, database, row.Table, cancellation).ConfigureAwait(false)));
        }

        return (tables, skipped);
    }

    /// <summary>The ArcGIS-style geometry type a MySQL column type names, or null for <c>geometry</c>, which holds any.</summary>
    internal static string? GeometryTypeOf(string type) => type.ToLowerInvariant() switch
    {
        "point" => "Point",
        "multipoint" => "MultiPoint",
        "linestring" => "LineString",
        "multilinestring" => "MultiLineString",
        "polygon" => "Polygon",
        "multipolygon" => "MultiPolygon",
        _ => null,
    };

    private static ProbeResult Refused(ProbeOutcome outcome, string message) => new(outcome, message, null, null, []);
}
