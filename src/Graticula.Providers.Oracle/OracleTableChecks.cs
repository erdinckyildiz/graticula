using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Platform.Admin;
using Oracle.ManagedDataAccess.Client;

namespace Graticula.Providers.Oracle;

/// <summary>
/// The two questions a publish asks of a table before it is served — how much of its geometry is valid, and whether
/// the reference declared is the one it holds — asked of an Oracle table (ADR-205), as <c>GeometryValidity</c> and
/// <c>DeclaredReference</c> ask them of a PostGIS one.
/// </summary>
public static class OracleTableChecks
{
    /// <summary>How much of a table's geometry Oracle considers valid, and why not.</summary>
    /// <param name="Rows">How many rows carry a geometry.</param>
    /// <param name="Invalid">How many of those Oracle calls invalid.</param>
    /// <param name="Reasons">The distinct reasons, most common first, at most five.</param>
    public sealed record Validity(long Rows, long Invalid, IReadOnlyList<string> Reasons);

    /// <summary>What the table holds against what was declared.</summary>
    /// <param name="Stored">The EPSG code its spatial metadata names, or null.</param>
    /// <param name="Complaint">What is wrong, in one sentence, or null.</param>
    public sealed record Reference(int? Stored, string? Complaint);

    /// <summary>
    /// Adds columns to a registered table, where they are not there — ADR-212: the editor-tracking columns, on an
    /// administrator's word, as ADR-207 makes attachment tables and history triggers.
    /// </summary>
    /// <param name="locator">The data source's locator.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="columns">Each column, with whether it holds a moment (otherwise an account name).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The columns added.</returns>
    public static async Task<IReadOnlyList<string>> AddColumnsAsync(
        string locator, string schema, string table, IReadOnlyList<(string Name, bool Moment)> columns, CancellationToken cancellationToken)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);
        List<string> added = [];

        // An ALTER waits up to five seconds for the table's lock, then is refused (ORA-00054) rather than queueing.
        await using (OracleCommand bound = new("alter session set ddl_lock_timeout = 5", connection))
        {
            await bound.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach ((string name, bool moment) in columns)
        {
            await using OracleCommand there = new(
                "select count(*) from all_tab_columns where owner = :o and table_name = :t and upper(column_name) = upper(:c)", connection)
            {
                BindByName = true,
            };
            there.Parameters.Add("o", OracleDbType.Varchar2, schema, System.Data.ParameterDirection.Input);
            there.Parameters.Add("t", OracleDbType.Varchar2, table, System.Data.ParameterDirection.Input);
            there.Parameters.Add("c", OracleDbType.Varchar2, name, System.Data.ParameterDirection.Input);

            if (Convert.ToInt64(await there.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            {
                continue;
            }

            await using OracleCommand alter = new(
                $"alter table {LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)} add ({LayerDefinition.Quote(name)} "
                + (moment ? "timestamp" : "varchar2(255)") + ")",
                connection);
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            added.Add(name);
        }

        return added;
    }

    /// <summary>
    /// Counts the geometries Oracle's <c>SDO_GEOM.VALIDATE_GEOMETRY_WITH_CONTEXT</c> rejects, at the tolerance the
    /// column's metadata declares — <b>by Oracle's rules, not PostGIS's</b> (ADR-205 condition 8): the land that PostGIS
    /// serves and Oracle calls invalid is counted here as invalid, because it is Oracle's table and Oracle's operators
    /// are what other clients of it use.
    /// </summary>
    /// <param name="locator">The data source's <c>oracle:</c> locator.</param>
    /// <param name="schema">The table's owner.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">Its geometry column.</param>
    /// <param name="timeout">How long the scan may take; a publish is interactive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The count, or null when the table could not be asked in time.</returns>
    public static async Task<Validity?> ValidityAsync(
        string locator, string schema, string table, string column, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string shape = LayerDefinition.Quote(column);
        string from = $"{LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)}";

        try
        {
            await using OracleConnection connection = await OracleConnections.OpenAsync(
                OracleConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

            // The reason is the error number Oracle puts first; the rest of its answer names the element and edge.
            await using OracleCommand command = new(
                $"""
                with tolerance as (
                  select nvl((select min(d.sdo_tolerance) from all_sdo_geom_metadata m, table(m.diminfo) d
                               where m.owner = :o and m.table_name = :t and m.column_name = :c), 0.005) as tol from dual),
                checked as (
                  select sdo_geom.validate_geometry_with_context(g.{shape}, (select tol from tolerance)) as v
                    from {from} g where g.{shape} is not null)
                select count(*) as n, sum(case when v = 'TRUE' then 0 else 1 end) as bad, null as reason, null as times from checked
                union all
                select * from (
                  select null as n, null as bad, regexp_substr(v, '^[0-9]+') as reason, count(*) as times from checked where v <> 'TRUE'
                   group by regexp_substr(v, '^[0-9]+') order by 4 desc fetch first 5 rows only)
                """, connection)
            {
                BindByName = true,
                CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds),
            };

            command.Parameters.Add("o", schema);
            command.Parameters.Add("t", table);
            command.Parameters.Add("c", column);

            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            long rows = 0;
            long invalid = 0;
            List<string> reasons = [];

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    rows = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                    invalid = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
                else if (!reader.IsDBNull(2))
                {
                    reasons.Add(
                        $"ORA-{reader.GetString(2)} in Oracle's validation "
                        + $"({Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture):N0})");
                }
            }

            return new Validity(rows, invalid, reasons);
        }
        catch (OracleException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the reference declared for a table is the one its spatial metadata names, and whether its coordinates
    /// could be in it — <c>DeclaredReference</c>'s two checks, the second stated as the heuristic it is.
    /// </summary>
    /// <param name="locator">The data source's <c>oracle:</c> locator.</param>
    /// <param name="schema">The table's owner.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">Its geometry column.</param>
    /// <param name="declared">The EPSG code the publisher asked for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found, or null when the table could not be asked.</returns>
    public static async Task<Reference?> ReferenceAsync(
        string locator, string schema, string table, string column, int declared, CancellationToken cancellationToken)
    {
        try
        {
            await using OracleConnection connection = await OracleConnections.OpenAsync(
                OracleConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

            // The stored EPSG code, whether the declared one is geographic in Oracle's own register, and the extent the
            // spatial index holds (SDO_TUNE.EXTENT_OF reads the index, not the rows).
            await using OracleCommand command = new(
                """
                select nvl(sdo_cs.map_oracle_srid_to_epsg(m.srid), m.srid),
                       (select case when r.coord_ref_sys_kind like 'GEOGRAPHIC%' then 1 else 0 end
                          from mdsys.sdo_coord_ref_sys r where r.srid = :declared),
                       (select min(v.x) from table(sdo_util.getvertices(sdo_tune.extent_of(:qualified, m.column_name))) v),
                       (select min(v.y) from table(sdo_util.getvertices(sdo_tune.extent_of(:qualified, m.column_name))) v),
                       (select max(v.x) from table(sdo_util.getvertices(sdo_tune.extent_of(:qualified, m.column_name))) v),
                       (select max(v.y) from table(sdo_util.getvertices(sdo_tune.extent_of(:qualified, m.column_name))) v)
                  from all_sdo_geom_metadata m
                 where m.owner = :o and m.table_name = :t and m.column_name = :c
                """, connection)
            {
                BindByName = true,
                CommandTimeout = 10,
            };

            command.Parameters.Add("declared", declared);
            command.Parameters.Add("qualified", schema + "." + table);
            command.Parameters.Add("o", schema);
            command.Parameters.Add("t", table);
            command.Parameters.Add("c", column);

            await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
            {
                return null;
            }

            int stored = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);

            if (stored != declared)
            {
                return new Reference(stored, $"the column holds EPSG:{stored} and this asked for EPSG:{declared}");
            }

            bool? geographic = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1;

            // An empty table, or one whose index holds no extent, declares nothing and proves nothing.
            if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
            {
                return new Reference(stored, null);
            }

            double xmin = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
            double ymin = Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture);
            double xmax = Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture);
            double ymax = Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture);

            if (geographic is true && (xmin < -180 || xmax > 180 || ymin < -90 || ymax > 90))
            {
                return new Reference(
                    stored,
                    $"EPSG:{declared} is a geographic system measured in degrees, and this table's coordinates run from "
                    + $"({xmin:F0}, {ymin:F0}) to ({xmax:F0}, {ymax:F0}) — outside ±180/±90, so they are not degrees");
            }

            if (geographic is false && xmin >= -180 && xmax <= 180 && ymin >= -90 && ymax <= 90)
            {
                return new Reference(
                    stored,
                    $"EPSG:{declared} is a projected system, and this table's whole extent fits inside ±180/±90 — "
                    + $"({xmin:F4}, {ymin:F4}) to ({xmax:F4}, {ymax:F4}) — which is what degrees stored under a projected "
                    + "code look like");
            }

            return new Reference(stored, null);
        }
        catch (OracleException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why a table cannot be published because an enterprise geodatabase versions or archives it — ADR-217 — or null.
    /// Null as well when the database could not be asked: the other checks of a publish say so in their own words.
    /// </summary>
    /// <param name="locator">The data source's locator.</param>
    /// <param name="schema">The table's owner.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The sentence, or null.</returns>
    public static async Task<string?> GeodatabaseRefusalAsync(
        string locator, string schema, string table, CancellationToken cancellationToken)
    {
        try
        {
            await using OracleConnection connection = await OracleConnections.OpenAsync(
                OracleConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

            List<string> columns = [];

            await using (OracleCommand command = new(
                "select column_name from all_tab_columns where owner = :o and table_name = :t", connection) { BindByName = true })
            {
                command.Parameters.Add("o", schema);
                command.Parameters.Add("t", table);
                await using OracleDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    columns.Add(reader.GetString(0));
                }
            }

            long? versioned = null;

            // The registry an Oracle geodatabase keeps as SDE.TABLE_REGISTRY, readable only where granted; a table it
            // registered as versioned has its adds and deletes in A<id> and D<id> under its owner.
            try
            {
                await using OracleCommand command = new(
                    """
                    select r.registration_id from sde.table_registry r
                     where r.owner = :o and r.table_name = :t
                       and exists (select 1 from all_tables a where a.owner = r.owner and a.table_name = 'A' || r.registration_id)
                    """, connection) { BindByName = true };
                command.Parameters.Add("o", schema);
                command.Parameters.Add("t", table);

                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is { } id and not DBNull)
                {
                    versioned = Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (OracleException missing) when (missing.Number == 942)
            {
                // No geodatabase here, or its registry is not readable by this user: the columns still say what they say.
            }

            return GeodatabaseClasses.Refusal($"{schema}.{table}", columns, versioned);
        }
        catch (OracleException)
        {
            return null;
        }
    }
}
