using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Oracle.ManagedDataAccess.Client;

namespace Graticula.Providers.Oracle;

/// <summary>
/// What a registered Oracle database offers to publish — ADR-205 phase 1: its tables with an <c>SDO_GEOMETRY</c>
/// column in Oracle's spatial metadata, each with a single integer key for ArcGIS's object id.
/// </summary>
/// <remarks>
/// <para>
/// <b>The metadata is the list, not every column of type <c>SDO_GEOMETRY</c>.</b> A table Oracle's own spatial
/// operators can use is one registered in <c>ALL_SDO_GEOM_METADATA</c> — <c>SDO_FILTER</c> refuses a column that is
/// not, and so does ArcGIS Pro reading the same database — so a table outside it is skipped with that reason rather
/// than offered and then failing on its first query.
/// </para>
/// <para>
/// <b>Read-only in phase 1</b>: every table is offered with <c>Writable</c> false, whatever the grants say. Editing
/// is phase 2 and waits on the writer passing the PostGIS writer's rules (ADR-205 condition 6).
/// </para>
/// </remarks>
public sealed class OracleDataSourceProbe : IDataSourceProbe
{
    /// <inheritdoc/>
    public async Task<ProbeResult> ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        string driver;

        try
        {
            driver = OracleConnections.For(connectionString, pooling: false);
        }
        catch (ArgumentException)
        {
            // Said as what is missing, not as the argument exception's own text, which ends "(Parameter 'stored')"
            // — a design review found that tail in the console's red box.
            return Refused(ProbeOutcome.CannotConnect, "A host, a port, a service and a user are all needed — the service is the name the Oracle listener knows the database by, such as FREEPDB1 or ORCLPDB1.");
        }

        try
        {
            await using OracleConnection connection = new(driver);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string? version = await ScalarAsync(connection,
                "select version_full from product_component_version where product like 'Oracle%' and rownum = 1",
                cancellationToken).ConfigureAwait(false) ?? connection.ServerVersion;

            string? spatial = await SpatialVersionAsync(connection, cancellationToken).ConfigureAwait(false);

            if (spatial is null)
            {
                return new ProbeResult(
                    ProbeOutcome.UnusableGeometry,
                    "Oracle Spatial is not usable in this database: MDSYS.SDO_GEOMETRY and its operators are missing or "
                    + "not valid. Every edition since 2019 includes it; ask the database's administrator to install it.",
                    version, null, []);
            }

            (List<SourceTable> tables, List<SkippedTable> skipped) =
                await TablesAsync(connection, cancellationToken).ConfigureAwait(false);

            int plain = tables.Count(t => t.GeometryColumn is null);

            // ADR-205 phase 2 made a table this user may write editable, so "read-only" is no longer the whole list's.
            string message = tables.Count == 0
                ? "Connected, and nothing here can be published: no table with an SDO_GEOMETRY column registered in "
                  + "ALL_SDO_GEOM_METADATA, and none without one, has a single integer key this user can read."
                : $"Connected. {tables.Count - plain} table{(tables.Count - plain == 1 ? "" : "s")} with geometry can be published"
                  + (plain > 0 ? $", and {plain} without, which publish as tables (ADR-213)." : ".");

            return new ProbeResult(ProbeOutcome.Usable, message, version, "Oracle Spatial " + spatial, tables, skipped);
        }
        catch (OracleException e)
        {
            return Refused(OutcomeOf(e), Explain(e));
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return Refused(ProbeOutcome.CannotConnect, "The database could not be reached: " + e.Message);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>One database, named by its service.</b> An Oracle listener serves pluggable databases as services, and the
    /// one to read is the one the registration names; listing the others needs privileges a reading account should
    /// not hold, so this answers the one it was given after checking it answers.
    /// </remarks>
    public async Task<DatabaseListing> ListDatabasesAsync(string connectionString, CancellationToken cancellationToken)
    {
        ProbeResult probed = await ProbeAsync(connectionString, cancellationToken).ConfigureAwait(false);

        if (probed.Outcome is ProbeOutcome.CannotConnect or ProbeOutcome.InsufficientPrivilege)
        {
            return new DatabaseListing(probed.Outcome, probed.Message, []);
        }

        return new DatabaseListing(
            ProbeOutcome.Usable,
            "An Oracle database is named by its service; this is the one the connection names.",
            [OracleLocator.Of(connectionString).Service]);
    }

    private static async Task<string?> SpatialVersionAsync(OracleConnection connection, CancellationToken cancellation)
    {
        try
        {
            return await ScalarAsync(connection, "select mdsys.sdo_version from dual", cancellation).ConfigureAwait(false);
        }
        catch (OracleException)
        {
            return null;
        }
    }

    private static async Task<(List<SourceTable> Tables, List<SkippedTable> Skipped)> TablesAsync(
        OracleConnection connection, CancellationToken cancellation)
    {
        // Every registered spatial column this user can see, with the type its spatial index declares when it has one
        // and the table's primary key when that key is one column of a whole-number type.
        const string Sql = """
            select m.owner, m.table_name, m.column_name, m.srid,
                   (select max(i.sdo_layer_gtype) from all_sdo_index_info x
                      join all_sdo_index_metadata i on i.sdo_index_owner = x.sdo_index_owner and i.sdo_index_name = x.index_name
                     where x.table_owner = m.owner and x.table_name = m.table_name and x.column_name = m.column_name) as gtype,
                   (select max(cc.column_name) from all_constraints c
                      join all_cons_columns cc on cc.owner = c.owner and cc.constraint_name = c.constraint_name
                     where c.owner = m.owner and c.table_name = m.table_name and c.constraint_type = 'P'
                    having count(*) = 1) as key_column,
                   (select count(*) from all_sdo_index_info x
                     where x.table_owner = m.owner and x.table_name = m.table_name and x.column_name = m.column_name) as indexes,
                   nvl(sdo_cs.map_oracle_srid_to_epsg(m.srid), m.srid) as epsg
              from all_sdo_geom_metadata m
             where exists (select 1 from all_tables t where t.owner = m.owner and t.table_name = m.table_name)
             order by m.owner, m.table_name, m.column_name
            """;

        List<(string Owner, string Table, string Column, int? Srid, string? Gtype, string? Key, bool Indexed)> found = [];

        await using (OracleCommand command = new(Sql, connection))
        await using (OracleDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                found.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    // The EPSG code where Oracle has its own number for the same reference (8307 is 4326).
                    reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetDecimal(7)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetDecimal(6) > 0));
            }
        }

        List<SourceTable> tables = [];
        List<SkippedTable> skipped = [];

        // ADR-217 condition 2: what publish refuses is not offered, and the sentence is the one it answers.
        IReadOnlyDictionary<string, string> refused = await GeodatabaseRefusalsAsync(connection, cancellation).ConfigureAwait(false);

        foreach (var row in found)
        {
            string name = $"{row.Owner}.{row.Table}";

            if (refused.TryGetValue(name.ToUpperInvariant(), out string? geodatabase))
            {
                skipped.Add(new SkippedTable(name, geodatabase));
                continue;
            }

            if (row.Srid is not { } srid)
            {
                skipped.Add(new SkippedTable(name, "Its spatial metadata names no SRID, so its coordinates' reference is unknown."));
                continue;
            }

            // The reader asks the spatial index for a box (SDO_FILTER), which Oracle refuses without one (ORA-13226).
            if (!row.Indexed)
            {
                skipped.Add(new SkippedTable(name,
                    "Its geometry column has no spatial index, which Oracle needs to answer a spatial filter. Create one "
                    + "with INDEXTYPE IS MDSYS.SPATIAL_INDEX_V2."));
                continue;
            }

            if (row.Key is not { } key || !await IsWholeNumberAsync(connection, row.Owner, row.Table, key, cancellation).ConfigureAwait(false))
            {
                skipped.Add(new SkippedTable(name,
                    "It has no single-column primary key of a whole-number type, which ArcGIS needs as the object id."));
                continue;
            }

            // A spatial index created without `layer_gtype` declares COLLECTION — measured on the test tables, and the
            // default — so the index often does not say; then the rows are asked.
            string? type = GeometryTypeOf(row.Gtype)
                ?? await SampledGeometryTypeAsync(connection, row.Owner, row.Table, row.Column, cancellation).ConfigureAwait(false);

            tables.Add(new SourceTable(
                row.Owner,
                row.Table,
                row.Column,
                srid,
                type,
                key,
                key,
                [key],
                Writable: await OracleConnections.CanWriteAsync(connection, row.Owner, row.Table, cancellation).ConfigureAwait(false)));
        }

        foreach (SourceTable plain in await PlainTablesAsync(connection, cancellation).ConfigureAwait(false))
        {
            string name = $"{plain.SchemaName}.{plain.TableName}";

            if (refused.TryGetValue(name.ToUpperInvariant(), out string? geodatabase))
            {
                skipped.Add(new SkippedTable(name, geodatabase));
            }
            else
            {
                tables.Add(plain);
            }
        }

        return (tables, skipped);
    }

    /// <summary>
    /// Every table an enterprise geodatabase in this database versions or archives, keyed by owner and table in upper
    /// case, with the sentence a publish of it answers — ADR-217 condition 2. Two reads for the whole database, the
    /// marker columns and the registry's versioned classes, as <see cref="OracleTableChecks.GeodatabaseRefusalAsync"/>
    /// reads them for one table; the sentence is <see cref="GeodatabaseClasses.Refusal"/>'s, so the listing and the
    /// publish cannot say two things.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> GeodatabaseRefusalsAsync(
        OracleConnection connection, CancellationToken cancellation)
    {
        Dictionary<string, (string Owner, string Table, List<string> Columns, long? Versioned)> marked = new(StringComparer.Ordinal);

        string markers = string.Join(", ", GeodatabaseClasses.MarkerColumns.Select(m => $"'{m}'"));

        await using (OracleCommand command = new(
            $"select owner, table_name, column_name from all_tab_columns where upper(column_name) in ({markers})", connection))
        await using (OracleDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                string key = $"{reader.GetString(0)}.{reader.GetString(1)}".ToUpperInvariant();

                if (!marked.TryGetValue(key, out var entry))
                {
                    marked[key] = entry = (reader.GetString(0), reader.GetString(1), [], null);
                }

                entry.Columns.Add(reader.GetString(2));
            }
        }

        // The registry an Oracle geodatabase keeps as SDE.TABLE_REGISTRY, readable only where granted.
        try
        {
            await using OracleCommand command = new(
                """
                select r.owner, r.table_name, r.registration_id from sde.table_registry r
                 where exists (select 1 from all_tables a where a.owner = r.owner and a.table_name = 'A' || r.registration_id)
                """, connection);
            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                string key = $"{reader.GetString(0)}.{reader.GetString(1)}".ToUpperInvariant();
                long id = Convert.ToInt64(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);

                marked[key] = marked.TryGetValue(key, out var entry)
                    ? entry with { Versioned = id }
                    : (reader.GetString(0), reader.GetString(1), [], id);
            }
        }
        catch (OracleException)
        {
            // No geodatabase registry here, or not readable: the columns still say what they say.
        }

        Dictionary<string, string> refused = new(StringComparer.Ordinal);

        foreach ((string key, var entry) in marked)
        {
            if (GeodatabaseClasses.Refusal($"{entry.Owner}.{entry.Table}", entry.Columns, entry.Versioned) is { } why)
            {
                refused[key] = why;
            }
        }

        return refused;
    }

    /// <summary>
    /// The tables with no SDO_GEOMETRY column and a single whole-number primary key — ADR-213 condition 7: each publishes
    /// as a table. Only in schemas Oracle does not maintain itself, and never the ones this server keeps beside a layer
    /// (its attachments and its history), a spatial index's own tables, or a table in the recycle bin.
    /// </summary>
    private static async Task<List<SourceTable>> PlainTablesAsync(OracleConnection connection, CancellationToken cancellation)
    {
        const string Sql = """
            select t.owner, t.table_name, max(cc.column_name)
              from all_tables t
              join all_users u on u.username = t.owner and u.oracle_maintained = 'N'
              join all_constraints c on c.owner = t.owner and c.table_name = t.table_name and c.constraint_type = 'P'
              join all_cons_columns cc on cc.owner = c.owner and cc.constraint_name = c.constraint_name
             where t.dropped = 'NO' and t.nested = 'NO' and t.secondary = 'N'
               and t.table_name not like '%\_\_ATTACH' escape '\'
               and t.table_name not like '%\_\_HISTORY' escape '\'
               and t.table_name not like 'MDRT\_%' escape '\'
               and t.table_name not like 'MDXT\_%' escape '\'
               and t.table_name not like 'DR$%'
               and not exists (select 1 from all_tab_columns g
                                where g.owner = t.owner and g.table_name = t.table_name and g.data_type = 'SDO_GEOMETRY')
             group by t.owner, t.table_name
            having count(*) = 1
             order by t.owner, t.table_name
            """;

        List<(string Owner, string Table, string Key)> found = [];

        await using (OracleCommand command = new(Sql, connection))
        await using (OracleDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<SourceTable> tables = [];

        foreach ((string owner, string table, string key) in found)
        {
            if (!await IsWholeNumberAsync(connection, owner, table, key, cancellation).ConfigureAwait(false))
            {
                continue;
            }

            tables.Add(new SourceTable(owner, table, null, 0, null, key, key, [key],
                Writable: await OracleConnections.CanWriteAsync(connection, owner, table, cancellation).ConfigureAwait(false)));
        }

        return tables;
    }

    private static async Task<bool> IsWholeNumberAsync(
        OracleConnection connection, string owner, string table, string column, CancellationToken cancellation)
    {
        await using OracleCommand command = new(
            "select data_type, data_scale, data_precision from all_tab_columns "
            + "where owner = :owner and table_name = :tab and column_name = :col", connection) { BindByName = true };
        command.Parameters.Add("owner", owner);
        command.Parameters.Add("tab", table);
        command.Parameters.Add("col", column);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            return false;
        }

        string type = reader.GetString(0);
        int? scale = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetDecimal(1));
        int? precision = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetDecimal(2));

        // NUMBER(p) or NUMBER(p, 0) up to 18 digits fits a 64-bit integer; INTEGER is NUMBER(38) with scale 0, which may
        // not, and is still the type most schemas declare an id with, so it is taken and a value past 2^63 is a read error.
        return type == "NUMBER" && scale is 0 && (precision is null or <= 38);
    }

    /// <summary>
    /// The geometry type the first thousand rows agree on, from the last two digits of <c>SDO_GTYPE</c>; the multi
    /// type when single and multi parts are mixed, as one feature class holds both; null when the rows mix kinds.
    /// </summary>
    private static async Task<string?> SampledGeometryTypeAsync(
        OracleConnection connection, string owner, string table, string column, CancellationToken cancellation)
    {
        // The names come from Oracle's own dictionary, upper-case identifiers; quoted so that one created quoted, in
        // mixed case, is still found.
        string sql = $"select distinct mod(t.\"{column.Replace("\"", "\"\"")}\".sdo_gtype, 100) "
            + $"from \"{owner.Replace("\"", "\"\"")}\".\"{table.Replace("\"", "\"\"")}\" t "
            + $"where t.\"{column.Replace("\"", "\"\"")}\" is not null and rownum <= 1000";

        List<int> kinds = [];

        try
        {
            await using OracleCommand command = new(sql, connection);
            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                kinds.Add(Convert.ToInt32(reader.GetDecimal(0)));
            }
        }
        catch (OracleException)
        {
            return null;
        }

        return TypeOfGtypes(kinds);
    }

    /// <summary>The ArcGIS-style type a set of <c>SDO_GTYPE</c> kinds (its last two digits) agree on.</summary>
    internal static string? TypeOfGtypes(IReadOnlyCollection<int> kinds)
    {
        if (kinds.Count == 0)
        {
            return null;
        }

        bool Only(params int[] allowed) => kinds.All(allowed.Contains);

        return Only(1) ? "Point"
            : Only(1, 5) ? "MultiPoint"
            : Only(2) ? "LineString"
            : Only(2, 6) ? "MultiLineString"
            : Only(3) ? "Polygon"
            : Only(3, 7) ? "MultiPolygon"
            : null;
    }

    /// <summary>The ArcGIS-style geometry type a spatial index's layer type names, or null when it does not say.</summary>
    internal static string? GeometryTypeOf(string? gtype) => gtype?.ToUpperInvariant() switch
    {
        "POINT" => "Point",
        "MULTIPOINT" => "MultiPoint",
        "LINE" or "CURVE" => "LineString",
        "MULTILINE" or "MULTICURVE" => "MultiLineString",
        "POLYGON" or "SURFACE" => "Polygon",
        "MULTIPOLYGON" or "MULTISURFACE" => "MultiPolygon",
        _ => null,
    };

    private static async Task<string?> ScalarAsync(OracleConnection connection, string sql, CancellationToken cancellation)
    {
        await using OracleCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ProbeResult Refused(ProbeOutcome outcome, string message) => new(outcome, message, null, null, []);

    /// <summary>
    /// The error that actually happened. The managed driver wraps a failure to connect in its own ORA-50201
    /// (<i>failed to connect to server or failed to parse connect string</i>) and puts the database's answer —
    /// ORA-12514 for an unknown service, ORA-12541 for no listener — in the innermost exception's message, measured
    /// against Oracle 26ai Free on 2026-10-19.
    /// </summary>
    internal static int NumberOf(OracleException e)
    {
        int number = e.Number;

        for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
        {
            System.Text.RegularExpressions.Match code =
                System.Text.RegularExpressions.Regex.Match(inner.Message, @"ORA-(\d{5})");

            if (code.Success && int.Parse(code.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is var found
                && found < 50000)
            {
                number = found;
            }
        }

        return number;
    }

    /// <summary>Which outcome an Oracle error is.</summary>
    internal static ProbeOutcome OutcomeOf(OracleException e) => NumberOf(e) switch
    {
        1031 or 1045 or 942 => ProbeOutcome.InsufficientPrivilege,
        _ => ProbeOutcome.CannotConnect,
    };

    /// <summary>An Oracle error as an administrator would want it said.</summary>
    internal static string Explain(OracleException e) => NumberOf(e) switch
    {
        1017 => "The user name or password was refused (ORA-01017).",
        28000 => "The account is locked (ORA-28000).",
        28001 => "The account's password has expired (ORA-28001).",
        12514 => "The listener does not know that service (ORA-12514). Check the service name: a pluggable database "
                 + "is reached by its own service, such as FREEPDB1, not by the container's.",
        12541 => "Nothing is listening at that host and port (ORA-12541).",
        12170 or 12545 => "The host could not be reached (ORA-" + NumberOf(e).ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ").",
        1045 => "The user may not open a session (ORA-01045): it needs CREATE SESSION.",
        _ => $"Oracle answered: {FirstLine(InnermostMessage(e))}",
    };

    private static string InnermostMessage(Exception e)
    {
        Exception deepest = e;

        while (deepest.InnerException is not null)
        {
            deepest = deepest.InnerException;
        }

        return deepest.Message;
    }

    private static string FirstLine(string message)
    {
        int at = message.IndexOf('\n', StringComparison.Ordinal);
        return (at < 0 ? message : message[..at]).Trim();
    }
}
