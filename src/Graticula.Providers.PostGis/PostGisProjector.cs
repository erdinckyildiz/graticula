using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Graticula.Providers.PostGis;

/// <summary>
/// Projects with the datastore's PROJ, rather than a library of our own.
/// </summary>
/// <remarks>
/// <para>
/// <b>No new dependency, and that is the argument.</b> The datastore is
/// mandatory ([Q-69]) and fused into the product ([ADR-019]); it already carries
/// PROJ, its grid files, and the EPSG database. Adding a .NET projection library
/// beside it would mean **two** coordinate engines with two EPSG datasets and two
/// sets of grids, disagreeing by metres on exactly the cadastral and survey work
/// where metres are legally significant — and disagreeing silently, because both
/// would answer.
/// </para>
/// <para>
/// <b>It also settles a question rather than answering it.</b> [Q-23] asks
/// whether a PROJ transformation object is thread-affine, because that decides
/// whether prepared transformations are shared or duplicated per thread. Through
/// PostgreSQL the question does not arise: each connection has its own.
/// </para>
/// <para>
/// <b>The cost is a round trip, and it is the right trade here.</b> This is not
/// the tile path — ADR-021 measured that one and the datastore won there too.
/// A GeometryServer <c>project</c> call is a whole request, so one round trip
/// per batch is invisible next to the HTTP it arrived on.
/// </para>
/// <para>
/// <b>WKB in both directions</b>, using the reader and writer this project wrote
/// and verified against PostGIS on 6.5 million polygons. Text would be simpler
/// and would round-trip coordinates through a decimal representation, losing
/// digits on exactly the operation whose whole purpose is not to.
/// </para>
/// </remarks>
public sealed class PostGisProjector : IProjector
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates a projector over the datastore.</summary>
    /// <param name="dataSource">The datastore pool.</param>
    public PostGisProjector(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)>
        ProjectAsync(
            IReadOnlyList<Geometry> geometries,
            int fromSrid,
            int toSrid,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometries);

        if (geometries.Count == 0)
        {
            return (
                [],
                await ProvenanceAsync(fromSrid, toSrid, cancellationToken).ConfigureAwait(false));
        }

        // <b>One statement for the whole batch, ordered by an explicit index.</b>
        // unnest does not promise to preserve input order, and a projection
        // service that silently returns geometries in a different order than it
        // received them is the worst possible failure: every coordinate is
        // right and every one is attached to the wrong thing.
        const string Sql = """
            select ST_AsBinary(ST_Transform(ST_SetSRID(ST_GeomFromWKB(g), @from), @to))
            from unnest(@geometries) with ordinality as t(g, n)
            order by t.n
            """;

        byte[][] wkb = new byte[geometries.Count][];

        for (int i = 0; i < geometries.Count; i++)
        {
            wkb[i] = WkbWriter.ToArray(geometries[i]);
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("from", fromSrid);
        command.Parameters.AddWithValue("to", toSrid);
        command.Parameters.Add(new NpgsqlParameter("geometries", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = wkb,
        });

        List<Geometry> projected = new(geometries.Count);

        await using (NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // ST_Transform returns NULL only for NULL input, which cannot
                // happen here — every element was written from a geometry. A
                // null would mean the ordering assumption broke, so it is louder
                // than a skipped element.
                if (reader.IsDBNull(0))
                {
                    throw new InvalidOperationException(
                        "ST_Transform returned NULL for a non-null geometry, which means the "
                        + "batch and its results are no longer aligned.");
                }

                // The SRID is not carried on our geometry type — it belongs to
                // the layer or, here, to the request. The caller knows it is
                // toSrid because it asked for it, and the response says so.
                projected.Add(WkbReader.Read((byte[])reader[0]));
            }
        }

        if (projected.Count != geometries.Count)
        {
            throw new InvalidOperationException(
                $"Projected {projected.Count} geometries from a batch of {geometries.Count}. "
                + "Returning a short list would silently pair coordinates with the wrong inputs.");
        }

        return (
            projected,
            await ProvenanceAsync(fromSrid, toSrid, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Which PROJ did the work.</summary>
    /// <remarks>
    /// <b>The accuracy is not reported and that is a gap, not a choice.</b>
    /// PROJ knows which pipeline it selected and what its stated accuracy is;
    /// <c>ST_Transform</c> does not surface either. Reaching it needs
    /// <c>ST_TransformPipeline</c> with a pipeline we chose, which is the pinning
    /// geometry-crs-policy §3 asks for and nobody has designed. Until then the
    /// engine version is the whole of what can honestly be said, and null is
    /// truthful where a number would not be.
    /// </remarks>
    private async Task<ProjectionProvenance> ProvenanceAsync(
        int fromSrid, int toSrid, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("select postgis_proj_version()");

        object? version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        string engine = version as string ?? "PROJ, version unknown";

        (bool? shift, string? caution) =
            await DatumAsync(fromSrid, toSrid, cancellationToken).ConfigureAwait(false);

        return new ProjectionProvenance(engine, null, shift, caution);
    }

    /// <summary>
    /// Whether this pair of references needs a datum change, and what to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>From the datum names in each reference's WKT, which is the one thing
    /// this server can find out.</b> The pipeline PROJ chose and its stated
    /// accuracy live in PROJ's operation database and are not reachable from SQL
    /// (Q-100). The datum is right there in <c>spatial_ref_sys.srtext</c>, and
    /// it is the whole of the difference that matters: a transformation within
    /// one datum is a closed formula and exact; a transformation across two is a
    /// shift, and when the grids for the accurate path are absent PROJ falls
    /// back to a ballpark <em>and does not fail</em>.
    /// </para>
    /// <para>
    /// <b>Cached, because it is a property of the pair and not of the
    /// request.</b> A tile pipeline projecting every request would otherwise
    /// read two WKT strings per tile to learn something that cannot change while
    /// the process runs.
    /// </para>
    /// <para>
    /// <b>Unknown is reported as unknown.</b> A reference with no WKT, or one
    /// whose WKT names no datum, yields null rather than false — claiming "no
    /// datum change" about a reference we could not read would be the same
    /// silent wrongness in a new place.
    /// </para>
    /// </remarks>
    private async Task<(bool? Shift, string? Caution)> DatumAsync(
        int fromSrid, int toSrid, CancellationToken cancellationToken)
    {
        if (fromSrid == toSrid)
        {
            return (false, null);
        }

        if (_datums.TryGetValue((fromSrid, toSrid), out (bool? Shift, string? Caution) known))
        {
            return known;
        }

        string? from = await DatumOfAsync(fromSrid, cancellationToken).ConfigureAwait(false);
        string? to = await DatumOfAsync(toSrid, cancellationToken).ConfigureAwait(false);

        (bool? Shift, string? Caution) answer;

        if (from is null || to is null)
        {
            answer = (
                null,
                $"This server could not read the datum of "
                + (from is null ? $"EPSG:{fromSrid}" : $"EPSG:{toSrid}")
                + ", so it cannot say whether this transformation crossed one. Treat the result "
                + "as unverified for survey work.");
        }
        else if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            answer = (false, null);
        }
        else
        {
            answer = (
                true,
                $"This crossed a datum: '{from}' to '{to}'. PROJ chose the pipeline and this "
                + "server cannot name it or state its accuracy (Q-100). If the shift grids for "
                + "the accurate path are not installed, PROJ falls back to a ballpark "
                + "transformation without failing, and the result can be metres from where the "
                + "data is \u2014 with no error and no visual signature. Do not treat this as "
                + "authoritative for cadastral or survey work without checking which grids the "
                + "datastore's PROJ has. See docs/geometry-crs-policy.md \u00a73.");
        }

        _datums[(fromSrid, toSrid)] = answer;

        return answer;
    }

    private async Task<string?> DatumOfAsync(int srid, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "select srtext from spatial_ref_sys where srid = @srid");

        command.Parameters.AddWithValue("srid", srid);

        object? text = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (text is not string wkt || wkt.Length == 0)
        {
            return null;
        }

        // <b>The first DATUM, which is the geodetic one.</b> A projected system
        // nests its geographic system inside it, so the outermost DATUM in the
        // text is the one both systems share; there is never a second, different
        // datum further in.
        Match match = DatumName.Match(wkt);

        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly Regex DatumName =
        new("DATUM\\[\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<(int From, int To), (bool? Shift, string? Caution)>
        _datums = new();
}
