using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Geometries;
using Microsoft.Data.SqlClient;

namespace Graticula.Providers.SqlServer;

/// <summary>
/// The two questions a publish asks of a table before it is served — how much of its geometry is valid, and whether
/// the reference declared is the one it holds — asked of a SQL Server table (ADR-210), as <c>MySqlTableChecks</c> asks
/// them of a MySQL one.
/// </summary>
public static class SqlServerTableChecks
{
    /// <summary>How much of a table's geometry SQL Server considers valid.</summary>
    /// <param name="Rows">How many rows carry a shape.</param>
    /// <param name="Invalid">How many of those <c>STIsValid</c> rejects.</param>
    /// <param name="Reasons">At most one line: SQL Server's own reasons are per shape.</param>
    public sealed record Validity(long Rows, long Invalid, IReadOnlyList<string> Reasons);

    /// <summary>What the table holds against what was declared.</summary>
    /// <param name="Stored">The SRID its first shape carries, or null.</param>
    /// <param name="Complaint">What is wrong, in one sentence, or null.</param>
    public sealed record Reference(int? Stored, string? Complaint);

    /// <summary>Counts the shapes <c>STIsValid</c> rejects — by SQL Server's rules, as Oracle's are counted by Oracle's.</summary>
    public static async Task<Validity?> ValidityAsync(
        string locator, string schema, string table, string column, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string shape = "t." + LayerDefinition.Quote(column);
        string from = $"{LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)}";

        try
        {
            await using SqlConnection connection = await SqlServerConnections.OpenAsync(
                SqlServerConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

            await using SqlCommand command = new(
                $"select count_big(*), coalesce(sum(case when {shape}.STIsValid() = 1 then 0 else 1 end), 0) from {from} t where {shape} is not null",
                connection)
            {
                CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds),
            };

            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            long rows = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            long invalid = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);

            return new Validity(rows, invalid, invalid == 0 ? [] : [$"invalid by SQL Server's STIsValid ({invalid:N0})"]);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the reference declared is the one the table's shapes carry — all of them, since SQL Server keeps one per
    /// shape — and whether its coordinates could be in it (<c>MySqlTableChecks.ReferenceAsync</c>'s heuristic).
    /// </summary>
    public static async Task<Reference?> ReferenceAsync(
        string locator, string schema, string table, string column, int declared, CancellationToken cancellationToken)
    {
        try
        {
            await using SqlConnection connection = await SqlServerConnections.OpenAsync(
                SqlServerConnections.For(locator, pooling: true), cancellationToken).ConfigureAwait(false);

            (int? stored, long others, _) = await SqlServerDataSourceProbe.ShapesAsync(connection, schema, table, column, cancellationToken)
                .ConfigureAwait(false);

            // An empty table declares nothing and proves nothing.
            if (stored is not { } srid)
            {
                return new Reference(null, null);
            }

            if (srid != declared)
            {
                return new Reference(srid, $"the column's shapes carry EPSG:{srid} and this asked for EPSG:{declared}");
            }

            if (others > 0)
            {
                return new Reference(srid, $"{others:N0} shape{(others == 1 ? " carries" : "s carry")} another reference than EPSG:{declared}");
            }

            string shape = "t." + LayerDefinition.Quote(column);
            string from = $"{LayerDefinition.Quote(schema)}.{LayerDefinition.Quote(table)}";

            await using SqlCommand extent = new(
                $"select geometry::EnvelopeAggregate(geometry::STGeomFromWKB({shape}.STAsBinary(), 0)).STAsBinary() from {from} t where {shape} is not null",
                connection)
            {
                CommandTimeout = 30,
            };

            if (await extent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not byte[] wkb
                || WkbReader.Read(wkb) is not { IsEmpty: false } box)
            {
                return new Reference(srid, null);
            }

            (double xmin, double ymin, double xmax, double ymax) = (box.Envelope.MinX, box.Envelope.MinY, box.Envelope.MaxX, box.Envelope.MaxY);
            bool geographic = AxisOrder.IsGeographic(declared);

            if (geographic && (xmin < -180 || xmax > 180 || ymin < -90 || ymax > 90))
            {
                return new Reference(
                    srid,
                    $"EPSG:{declared} is a geographic system measured in degrees, and this table's coordinates run from "
                    + $"({xmin:F0}, {ymin:F0}) to ({xmax:F0}, {ymax:F0}) — outside ±180/±90, so they are not degrees");
            }

            return new Reference(srid, null);
        }
        catch (SqlException)
        {
            return null;
        }
    }
}
