using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Geometries;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>
/// The two questions a publish asks of a table before it is served — how much of its geometry is valid, and whether
/// the reference declared is the one it holds — asked of a MySQL table (ADR-208), as <c>OracleTableChecks</c> asks them
/// of an Oracle one.
/// </summary>
public static class MySqlTableChecks
{
    /// <summary>How much of a table's geometry MySQL considers valid, and why not.</summary>
    /// <param name="Rows">How many rows carry a geometry.</param>
    /// <param name="Invalid">How many of those MySQL calls invalid.</param>
    /// <param name="Reasons">What can be said of why; MySQL's <c>ST_IsValid</c> gives no reason, so at most one line.</param>
    public sealed record Validity(long Rows, long Invalid, IReadOnlyList<string> Reasons);

    /// <summary>What the table holds against what was declared.</summary>
    /// <param name="Stored">The SRID its column's attribute names, or null.</param>
    /// <param name="Complaint">What is wrong, in one sentence, or null.</param>
    public sealed record Reference(int? Stored, string? Complaint);

    /// <summary>
    /// Counts the geometries MySQL's <c>ST_IsValid</c> rejects — <b>by MySQL's rules, not PostGIS's</b>, as Oracle's are
    /// counted by Oracle's (ADR-205 condition 8): it is MySQL's table, and MySQL's functions are what its other clients use.
    /// </summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="schema">The table's database.</param>
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
            await using MySqlConnection connection = await MySqlConnections.OpenAsync(
                MySqlConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);
            await using MySqlCommand command = new(
                $"select count(*), coalesce(sum(case when st_isvalid({shape}) then 0 else 1 end), 0) from {from} where {shape} is not null",
                connection)
            {
                CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds),
            };

            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            long rows = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            long invalid = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);

            return new Validity(rows, invalid, invalid == 0 ? [] : [$"invalid by MySQL's ST_IsValid ({invalid:N0})"]);
        }
        catch (MySqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the reference declared for a table is the one its column's <c>SRID</c> attribute names, and whether its
    /// coordinates could be in it — <c>OracleTableChecks.ReferenceAsync</c>'s two checks, the second the heuristic it is.
    /// </summary>
    /// <param name="locator">The data source's <c>mysql:</c> locator.</param>
    /// <param name="schema">The table's database.</param>
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
            await using MySqlConnection connection = await MySqlConnections.OpenAsync(
                MySqlConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

            await using MySqlCommand command = new(
                """
                select g.srs_id,
                       (select case when r.definition like 'GEOGCS%' then 1 else 0 end
                          from information_schema.st_spatial_reference_systems r where r.srs_id = @declared)
                  from information_schema.st_geometry_columns g
                 where g.table_schema = @s and g.table_name = @t and g.column_name = @c
                """, connection)
            {
                CommandTimeout = 10,
            };

            command.Parameters.AddWithValue("@declared", declared);
            command.Parameters.AddWithValue("@s", schema);
            command.Parameters.AddWithValue("@t", table);
            command.Parameters.AddWithValue("@c", column);

            int stored;
            bool? geographic;

            await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
                {
                    return null;
                }

                stored = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                geographic = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1;
            }

            if (stored != declared)
            {
                return new Reference(stored, $"the column holds EPSG:{stored} and this asked for EPSG:{declared}");
            }

            // The extent as the reader takes it: with the SRID set to 0, so longitude comes first and MySQL computes a
            // box under a geographic reference at all.
            string shape = LayerDefinition.Quote(column);
            await using MySqlCommand extent = new(
                $"select st_asbinary(st_envelope(st_srid(st_collect({shape}), 0))) from {LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)}",
                connection)
            {
                CommandTimeout = 10,
            };

            // An empty table declares nothing and proves nothing.
            if (await extent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not byte[] wkb
                || WkbReader.Read(wkb) is not { IsEmpty: false } box)
            {
                return new Reference(stored, null);
            }

            (double xmin, double ymin, double xmax, double ymax) = (box.Envelope.MinX, box.Envelope.MinY, box.Envelope.MaxX, box.Envelope.MaxY);

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
        catch (MySqlException)
        {
            return null;
        }
    }
}
