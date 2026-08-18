using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Graticula.Providers.PostGis;

/// <summary>
/// How much of a table's geometry PostGIS considers valid, and why not.
/// </summary>
/// <param name="Rows">How many rows carry a geometry.</param>
/// <param name="Invalid">How many of those are invalid.</param>
/// <param name="Reasons">
/// The distinct reasons, most common first, at most five. <c>ST_IsValidReason</c>
/// answers per row and a caller needs the shape of the problem, not a list as long as
/// the table.
/// </param>
public sealed record GeometryValidity(long Rows, long Invalid, IReadOnlyList<string> Reasons)
{
    /// <summary>Whether every geometry in the table is valid.</summary>
    public bool AllValid => Invalid == 0;

    /// <summary>A sentence for whoever asked, naming the count and the reason.</summary>
    public string Explanation => Invalid == 0
        ? $"All {Rows:N0} geometries are valid."
        : $"{Invalid:N0} of {Rows:N0} geometries are invalid: "
          + string.Join("; ", Reasons)
          + ". An invalid geometry is served as it is stored — this server does not repair "
          + "silently — and a client that computes with it may get a wrong area, a failed "
          + "intersection, or a refusal.";

    /// <summary>
    /// Asks PostGIS whether a table's geometry is valid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-53: the importer accepted invalid geometry and nothing on the publish path
    /// looked.</b> Measured 2026-08-17: <c>hosted.tr_ilce_511f6767</c>, written by our own
    /// importer from OpenStreetMap, holds 18 invalid geometries in 25,280 — degenerate rings,
    /// not self-intersections. Neither import nor publish asked. It was found because another
    /// server refused to publish the table, which is a poor way to learn a fact about your own
    /// data.
    /// </para>
    /// <para>
    /// <b>This counts and reports; it never repairs.</b> <c>ST_MakeValid</c> is available and
    /// deliberately not called here. Repairing is a change to somebody's data made by a
    /// process they asked to *store* it — and the repair is not neutral: it can drop a
    /// degenerate ring, split a polygon into a multipolygon, or turn an area into a line.
    /// A server that silently returns different geometry from what it was handed is a server
    /// nobody can reconcile against their source.
    /// </para>
    /// <para>
    /// <b>Nor does it refuse by itself.</b> Refusing is the caller's choice, because the
    /// answer differs by case: an import of somebody's authoritative extract with 18 bad rows
    /// in 25,280 is usually still worth having, and a registered table somebody else maintains
    /// is not ours to reject. So both call sites ask, report what came back, and refuse only
    /// when they were told to.
    /// </para>
    /// <para>
    /// <b>Bounded, because a validity scan is a full pass over the geometry.</b> There is no
    /// index that can answer this, so on a large table it costs what a sequential scan costs.
    /// The reasons are limited to five and the query has its own timeout at the call site.
    /// </para>
    /// </remarks>
    /// <param name="dataSource">Where the table lives.</param>
    /// <param name="schema">Its schema.</param>
    /// <param name="table">Its name.</param>
    /// <param name="column">The geometry column.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The counts and the distinct reasons.</returns>
    public static async Task<GeometryValidity> MeasureAsync(
        NpgsqlDataSource dataSource,
        string schema,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        // <b>Quoted with `quote_ident`, and the identifiers come from the catalogue
        // rather than from a request.</b> security.md's rule: a table name is a filename
        // in a different dialect. There is no parameter form for an identifier in SQL, so
        // the safety has to be that the caller never passes a caller's string — and the
        // format specifier is `%I`, which is PostgreSQL's own quoting rather than ours.
        const string Sql = """
            select format(
              'select count(*) as rows,
                      count(*) filter (where not st_isvalid(%1$I)) as invalid,
                      coalesce(array_agg(distinct r) filter (where r is not null), ''{}'') as why
                 from (select %1$I,
                              case when st_isvalid(%1$I) then null
                                   else st_isvalidreason(%1$I) end as r
                         from %2$I.%3$I
                        where %1$I is not null) s',
              $1, $2, $3)
            """;

        string measure;

        await using (NpgsqlCommand build = dataSource.CreateCommand(Sql))
        {
            build.Parameters.AddWithValue(column);
            build.Parameters.AddWithValue(schema);
            build.Parameters.AddWithValue(table);

            measure = (string)(await build.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false))!;
        }

        await using NpgsqlCommand command = dataSource.CreateCommand(measure);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new GeometryValidity(0, 0, Array.Empty<string>());
        }

        long rows = reader.GetInt64(0);
        long invalid = reader.GetInt64(1);
        string[] reasons = reader.GetFieldValue<string[]>(2);

        // <b>Trimmed to the shape of the problem.</b> ST_IsValidReason includes the
        // coordinate of the fault — "Too few points in geometry component[28.9 41.0]" —
        // so a table with eighteen bad rows has eighteen distinct "reasons" that are one
        // reason. Cutting at the bracket is what makes the report readable.
        List<string> distinct = [];

        foreach (string reason in reasons)
        {
            string shape = reason.Split('[')[0].Trim();

            if (shape.Length > 0 && !distinct.Contains(shape))
            {
                distinct.Add(shape);

                if (distinct.Count == 5)
                {
                    break;
                }
            }
        }

        return new GeometryValidity(rows, invalid, distinct);
    }
}
