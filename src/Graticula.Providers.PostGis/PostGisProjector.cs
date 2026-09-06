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
                await DescribeAsync(fromSrid, toSrid, cancellationToken).ConfigureAwait(false));
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
            await DescribeAsync(fromSrid, toSrid, cancellationToken).ConfigureAwait(false));
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
    /// <inheritdoc cref="IProjector.DescribeAsync"/>
    /// <remarks>
    /// <b>Public since 2026-08-25, and it is the same method.</b> [Q-141](../../../docs/open-questions.md)
    /// needed the datum answer on a path that never calls <see cref="ProjectAsync"/>, and this
    /// was already computing it for the paths that do. Publishing what exists beats a second
    /// implementation of the same WKT read, which would be the propagation shape
    /// [D-130](../../../docs/architecture-debt.md) records with the added cost that the two
    /// could disagree about the same pair of references.
    /// </remarks>
    public async Task<ProjectionProvenance> DescribeAsync(
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

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The same statement as the code path with one parameter typed differently.</b>
    /// PostGIS 3.4's <c>ST_Transform(geometry, text)</c> hands the definition to PROJ directly,
    /// and it needs no row in <c>spatial_ref_sys</c> — measured 2026-09-06 against the code
    /// path beside it: identical coordinates to the last digit. That is what makes a written
    /// reference possible without writing into the database a registered source points at,
    /// which belongs to somebody else.
    /// </remarks>
    public async Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
        IReadOnlyList<Geometry> geometries,
        int fromSrid,
        string definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);

        if (geometries.Count == 0)
        {
            return [];
        }

        // Ordered by an explicit index for the same reason the code path is: `unnest` does not
        // promise input order, and geometries returned in another order is every coordinate
        // right and every one attached to the wrong thing.
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
        command.Parameters.AddWithValue("to", definition);
        command.Parameters.Add(
            new NpgsqlParameter("geometries", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
            {
                Value = wkb,
            });

        List<Geometry> projected = new(geometries.Count);

        try
        {
            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    return null;
                }

                projected.Add(WkbReader.Read(reader.GetFieldValue<byte[]>(0)));
            }
        }
        catch (PostgresException)
        {
            // <b>A definition PROJ will not read is a refusal, not a fault here.</b> The caller
            // asked whether this reference can be used; *no* is an answer it has a route for,
            // and the operator sees it where they typed it rather than as a stack trace.
            return null;
        }

        return projected;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>One row of <c>spatial_ref_sys</c>, cached forever.</b> The table is PROJ's
    /// own, it is written when PostGIS is installed, and a deployment that edits it
    /// restarts this server anyway. Caching a negative answer is deliberate: a client
    /// looping on a bad code should cost one round trip in total rather than one each.
    /// </para>
    /// <para>
    /// <b>An unreachable store answers <em>yes</em>, not <em>no</em>.</b> This exists to
    /// turn a caller's mistake into a 400 before any bytes are written; it must never
    /// turn a database outage into a 400 that blames the caller for it. When the lookup
    /// itself fails, the request proceeds and the outage is reported by the path that
    /// knows it is an outage.
    /// </para>
    /// </remarks>
    public async Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken)
    {
        if (_known.TryGetValue(srid, out bool cached))
        {
            return cached;
        }

        bool answer;

        try
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(
                "select 1 from spatial_ref_sys where srid = @srid");

            command.Parameters.AddWithValue("srid", srid);

            answer = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is not null;
        }
        catch (NpgsqlException)
        {
            return true;
        }

        _known[srid] = answer;

        return answer;
    }

    private readonly ConcurrentDictionary<int, bool> _known = new();

    /// <summary>
    /// What a reference can represent, from the projection database's own area of use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-165](../../../docs/architecture-debt.md).</b> `ProjectionDomain` answers for
    /// geographic references and Web Mercator from arithmetic, and null for every projected
    /// one — so a layer in EPSG:2180 or 5254 had a caller's whole-world bounding box passed
    /// to `st_transform` unclamped. `postgis_srs` publishes the reference's **area of use**
    /// in degrees, which is the right answer from an authoritative source with no table of
    /// ours to maintain. Measured 2026-08-26: EPSG:5254 is (28.5, 36.06)–(31.5, 41.46),
    /// EPSG:2180 is (14.14, 49)–(24.15, 55.93), EPSG:3857 is (−180, −85.06)–(180, 85.06).
    /// </para>
    /// <para>
    /// <b>It arrived in PostGIS 3.4, and this does not make 3.4 a requirement.</b> That
    /// version commitment is what stopped this being built: the repository declares no
    /// minimum PostGIS version and calls the function nowhere else. Asking for it inside a
    /// `try` and answering **null** when it is not there costs nothing on an older server and
    /// leaves it exactly as it was — null already means *do not clamp*. A capability used
    /// where it exists is not a dependency.
    /// </para>
    /// <para>
    /// <b>An unknown code answers a row of nulls rather than no row</b>, measured against
    /// `EPSG:999999`, so the null check is on the ordinates and not on the row count.
    /// </para>
    /// </remarks>
    /// <param name="srid">The EPSG code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The area of use in degrees, or null.</returns>
    public async Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken)
    {
        if (_domains.TryGetValue(srid, out Envelope? cached))
        {
            return cached;
        }

        Envelope? answer = null;

        try
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(
                "select st_x(point_sw), st_y(point_sw), st_x(point_ne), st_y(point_ne) "
                + "from postgis_srs('EPSG', @srid)");

            command.Parameters.AddWithValue(
                "srid", srid.ToString(System.Globalization.CultureInfo.InvariantCulture));

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                && !await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                && !await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                && !await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false))
            {
                answer = new Envelope(
                    reader.GetDouble(0), reader.GetDouble(1),
                    reader.GetDouble(2), reader.GetDouble(3));
            }
        }
        catch (NpgsqlException)
        {
            // <b>Older PostGIS, or a store that cannot be reached.</b> Both mean the same
            // thing to the caller — this deployment cannot say — and that is what null is.
            // Not cached, because an outage is not an answer about a reference.
            return null;
        }

        _domains[srid] = answer;

        return answer;
    }

    private readonly ConcurrentDictionary<int, Envelope?> _domains = new();
}
