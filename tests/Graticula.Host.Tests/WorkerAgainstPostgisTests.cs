using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The worker-backed operations, checked against PostGIS on real geometry.
/// </summary>
/// <remarks>
/// <para>
/// <b>PostGIS is the oracle, not the runtime</b> — the same arrangement
/// <c>GeometryOperationsAgainstPostgisTests</c> uses for the in-process
/// operations, and the same one <c>WkbReader</c> used against 6.5 million
/// polygons. A GeometryServer request carries its own geometry, so sending it to
/// the database at runtime would create traffic rather than avoid it. Using a
/// database to <em>check</em> an implementation is a different thing.
/// </para>
/// <para>
/// <b>Six operations shipped on 2026-08-15 because the owner removed the rule
/// that kept them out, and shipping them on that authority alone would have been
/// the wrong lesson.</b> The rule that changed was about who decides what is
/// worth attempting. It was not a licence to skip checking that the answers are
/// right, and NetTopologySuite being a mature library is not evidence about the
/// code in this repository that calls it — which is where the two bugs found on
/// the way here lived.
/// </para>
/// <para>
/// <b>They fail rather than skip when the datastore is absent</b>, following
/// <c>PostgresFixture</c>'s rule and for its reason: this project has four times
/// written a test that went green with its subject missing. Filter them out
/// deliberately if you mean to; do not let them pass by default.
/// </para>
/// </remarks>
public sealed class WorkerAgainstPostgisTests : IAsyncLifetime, IAsyncDisposable
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    private NpgsqlDataSource? _source;
    private GeometryWorkerPool? _pool;

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable);

    /// <summary>Sets up the datastore connection and the worker pool.</summary>
    /// <returns>Nothing.</returns>
    public Task InitializeAsync()
    {
        if (ConnectionString is { Length: > 0 } connection)
        {
            // <b>D-60: one database-backed suite at a time.</b> These compare our
            // arithmetic against PostGIS on real geometry, over the same connection
            // the other three suites are loading.
            Graticula.Testing.OneSuiteAtATime.Enter();

            _source = NpgsqlDataSource.Create(connection);

            _pool = new GeometryWorkerPool(
                GeometryWorkerPool.ExecutableBesideThisOne(),
                workers: 2,
                NullLoggerFactory.Instance);
        }

        return Task.CompletedTask;
    }

    /// <summary>Tears both down.</summary>
    /// <returns>Nothing.</returns>
    public async Task DisposeAsync()
    {
        if (_pool is not null)
        {
            await _pool.DisposeAsync();
        }

        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
    }

    /// <summary>
    /// The same teardown, under the interface the analyzer looks for.
    /// </summary>
    /// <remarks>
    /// <b>Both interfaces, because their DisposeAsync signatures differ.</b>
    /// xunit's returns <c>Task</c> and the framework one returns
    /// <c>ValueTask</c>; without this, owning a worker pool in a field is
    /// CA1001 and the build fails.
    /// </remarks>
    /// <returns>Nothing.</returns>
    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    /// <summary>Fails loudly when there is nothing to compare against.</summary>
    private void Require()
    {
        Assert.True(
            _source is not null,
            $"{ConnectionVariable} is not set, so these tests FAIL rather than skip. A test that "
            + "goes green with its subject absent is worse than no test.");

        Assert.True(
            _pool is not null && _pool.Available,
            "The geometry worker is not beside the host build. It is copied there by the host "
            + "project; if it is missing, that copy step is broken and every one of these "
            + "operations answers 503 in production.");
    }

    /// <summary>
    /// The reference the corpus is treated as being in.
    /// </summary>
    /// <remarks>
    /// <b>Nominal, and nothing here depends on it.</b> Every comparison is
    /// planar arithmetic that both engines perform in the coordinates they are
    /// given, and no projection happens on either side — so the units cancel.
    /// <b>The first version of this file transformed the corpus to 3857 and
    /// every test failed with "latitude or longitude exceeded limits"</b>: it
    /// assumed the geometry was in degrees and told PostGIS so with
    /// <c>ST_SetSRID</c>, which does not convert anything, it overwrites the
    /// label. Distances are derived from each shape's own extent below for the
    /// same reason — a fixed 25 works in metres and is absurd in degrees.
    /// </remarks>
    private const int Srid = 3857;

    /// <summary>Real geometry from the datastore, in whatever units it holds.</summary>
    private async Task<IReadOnlyList<Geometry>> CorpusAsync(int count)
    {
        // The query, its ordering and the reasons for both live in
        // tests/shared/GeometryCorpus.cs, compiled into this project.
        const string Tables = Graticula.Testing.GeometryCorpus.PolygonTables;

        List<string> candidates = [];

        await using (NpgsqlCommand list = _source!.CreateCommand(Tables))
        await using (NpgsqlDataReader reader =
                     await list.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                candidates.Add(reader.GetString(0));
            }
        }

        foreach (string table in candidates)
        {
            List<Geometry> corpus = [];

            await using NpgsqlCommand command = _source.CreateCommand(
                $"""
                 select ST_AsBinary(geom)
                 from {table}
                 where geom is not null
                   and ST_NPoints(geom) between 8 and 200
                   and ST_IsValid(geom)
                 limit {count.ToString(CultureInfo.InvariantCulture)}
                 """);

            try
            {
                await using NpgsqlDataReader reader =
                    await command.ExecuteReaderAsync(CancellationToken.None);

                while (await reader.ReadAsync(CancellationToken.None))
                {
                    corpus.Add(WkbReader.Read((byte[])reader[0]));
                }
            }
            catch (PostgresException e) when (e.SqlState == "42P01")
            {
                // <b>The table was listed and then dropped.</b> Publishing tests
                // create hosted tables and remove them, and the catalogue read
                // above is a snapshot — so a name can be valid when it is
                // returned and gone a millisecond later. Try the next one; that
                // is what a discovery loop is for. Surfaced as a real failure
                // twice before being handled, both times on an unrelated run.
                continue;
            }

            if (corpus.Count >= Math.Min(count, 10))
            {
                return corpus;
            }
        }

        Assert.Fail(
            $"No table among {candidates.Count} with a geometry column held at least ten valid "
            + "polygons of 8 to 200 vertices. These tests check an implementation against real "
            + "shapes and can say nothing without them.");

        throw new InvalidOperationException();
    }

    private async Task<double> ScalarAsync(string sql, params Geometry[] operands)
    {
        await using NpgsqlCommand command = _source!.CreateCommand(sql);

        for (int i = 0; i < operands.Length; i++)
        {
            command.Parameters.AddWithValue(
                "g" + i.ToString(CultureInfo.InvariantCulture), WkbWriter.ToArray(operands[i]));
        }

        return Convert.ToDouble(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }

    private async Task<EngineResult> RunAsync(
        EngineOperation operation,
        IReadOnlyList<Geometry> left,
        IReadOnlyList<Geometry> right,
        double distance = 0,
        string? pattern = null)
    {
        EngineResult result = await _pool!.ComputeAsync(
            new EngineRequest(operation, left, right, Srid)
            {
                Distance = distance,
                Pattern = pattern,
            },
            CancellationToken.None);

        Assert.Equal(EngineRefusal.None, result.Refusal);

        return result;
    }

    // ---------- distance ----------

    /// <summary>
    /// distance matches ST_Distance on every pair tried.
    /// </summary>
    /// <remarks>
    /// <b>Exact agreement is the right bar here, and it would not be for buffer.</b>
    /// A minimum over segment pairs is arithmetic both engines do the same way;
    /// a buffer approximates a curve and the two need not choose the same
    /// number of segments.
    /// </remarks>
    [Fact]
    public async Task Distance_matches_PostGIS_exactly()
    {
        Require();

        IReadOnlyList<Geometry> corpus = await CorpusAsync(20);

        int compared = 0;

        for (int i = 0; i + 1 < corpus.Count; i += 2)
        {
            double theirs = await ScalarAsync(
                "select ST_Distance(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
                corpus[i], corpus[i + 1]);

            EngineResult ours = await RunAsync(
                EngineOperation.Distance, [corpus[i]], [corpus[i + 1]]);

            Assert.Equal(theirs, ours.Scalar!.Value, 6);
            compared++;
        }

        Assert.True(compared >= 5, $"only {compared} pairs were compared.");
    }

    // ---------- buffer ----------

    /// <summary>
    /// buffer agrees with ST_Buffer on area, within the segmentation difference.
    /// </summary>
    /// <remarks>
    /// <b>Area within one per cent, not geometric equality.</b> Both engines
    /// approximate the round end of a buffer with straight segments and neither
    /// documents the count as part of its contract, so identical polygons would
    /// be a coincidence rather than a correctness criterion. What must hold is
    /// that the shape is the right size — a wrong sign, a wrong unit or a
    /// buffer of the wrong operand all move the area by far more than the
    /// segmentation does.
    /// </remarks>
    [Fact]
    public async Task Buffer_agrees_with_PostGIS_on_area()
    {
        Require();

        IReadOnlyList<Geometry> corpus = await CorpusAsync(12);

        int compared = 0;

        foreach (Geometry shape in corpus)
        {
            Envelope box = shape.Envelope;

            // Five per cent of the shape's own width: large enough that a wrong
            // sign or a missing distance moves the area unmistakably, small
            // enough that the buffer does not swallow the shape.
            double distance = Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY) * 0.05;

            double theirs = await ScalarAsync(
                "select ST_Area(ST_Buffer(ST_GeomFromWKB(@g0), "
                + distance.ToString("R", CultureInfo.InvariantCulture) + "))", shape);

            EngineResult ours = await RunAsync(
                EngineOperation.Buffer, [shape], [], distance: distance);

            double mine = 0;

            foreach (Geometry part in ours.Geometries)
            {
                mine += Math.Abs(GeometryMeasures.Area(part));
            }

            Assert.True(
                Math.Abs(mine - theirs) <= theirs * 0.01,
                $"buffered area {mine:N1} against PostGIS's {theirs:N1} — more than one per "
                + "cent apart, which segmentation alone does not explain.");

            compared++;
        }

        Assert.True(compared >= 10, $"only {compared} shapes were compared.");
    }

    // ---------- simplify ----------

    /// <summary>
    /// simplify makes an invalid geometry valid, and PostGIS agrees it is.
    /// </summary>
    /// <remarks>
    /// <b>The oracle here is ST_IsValid, not ST_MakeValid.</b> Two repairs of the
    /// same broken shape may legitimately differ — a bow-tie can be split into
    /// two triangles or have its crossing noded — and demanding the same output
    /// would test which library we chose rather than whether the result is
    /// sound. Validity is the property the operation promises.
    /// </remarks>
    [Fact]
    public async Task Simplify_produces_something_PostGIS_calls_valid()
    {
        Require();

        // A bow-tie: the ring crosses itself, so it encloses two lobes of
        // opposite orientation and is invalid by any definition.
        Polygon bowtie = new(new LinearRing(XySequence.Wrap(
            [0, 0, 10, 10, 10, 0, 0, 10, 0, 0])));


        double before = await ScalarAsync(
            "select case when ST_IsValid(ST_GeomFromWKB(@g0)) then 1 else 0 end", bowtie);

        Assert.Equal(0, before);

        EngineResult ours = await RunAsync(EngineOperation.Simplify, [bowtie], []);

        Assert.NotEmpty(ours.Geometries);

        foreach (Geometry repaired in ours.Geometries)
        {
            double after = await ScalarAsync(
                "select case when ST_IsValid(ST_GeomFromWKB(@g0)) then 1 else 0 end", repaired);

            Assert.Equal(1, after);
        }
    }

    // ---------- cut ----------

    /// <summary>
    /// The pieces of a cut cover the target and nothing more.
    /// </summary>
    /// <remarks>
    /// <b>Area conservation, checked by PostGIS rather than by us.</b> A cut that
    /// loses a piece, keeps a face the cutter closed off outside the target, or
    /// double-counts an overlap all show up as an area that is not the target's.
    /// Comparing against <c>ST_Split</c> directly would compare orderings and
    /// ring directions, which are not part of what a cut promises.
    /// </remarks>
    [Fact]
    public async Task The_pieces_of_a_cut_add_up_to_the_target()
    {
        Require();

        IReadOnlyList<Geometry> corpus = await CorpusAsync(10);

        int split = 0;

        foreach (Geometry shape in corpus)
        {
            Envelope box = shape.Envelope;

            // A line across the middle of the shape's box, extended past both
            // ends so it certainly crosses rather than stopping inside.
            double y = (box.MinY + box.MaxY) / 2;
            double pad = (box.MaxX - box.MinX) * 0.1;

            LineString cutter = new(XySequence.Wrap(
                [box.MinX - pad, y, box.MaxX + pad, y]));

            EngineResult ours = await RunAsync(EngineOperation.Cut, [shape], [cutter]);

            double target = await ScalarAsync("select ST_Area(ST_GeomFromWKB(@g0))", shape);

            double pieces = 0;

            foreach (Geometry piece in ours.Geometries)
            {
                pieces += Math.Abs(GeometryMeasures.Area(piece));
            }

            Assert.True(
                Math.Abs(pieces - target) <= Math.Max(target * 1e-6, 1e-6),
                $"the pieces total {pieces:N4} where the target is {target:N4}.");

            if (ours.Geometries.Count > 1)
            {
                split++;
            }
        }

        // A cutter through the middle of a box should divide most real shapes.
        // If it divides none of them, the operation is returning the input and
        // the area check above is passing for the wrong reason.
        Assert.True(
            split >= corpus.Count / 2,
            $"only {split} of {corpus.Count} shapes were actually divided, so the area "
            + "assertion above is mostly comparing a shape against itself.");
    }

    // ---------- relation ----------

    /// <summary>
    /// relation picks the same pairs ST_Intersects does.
    /// </summary>
    /// <remarks>
    /// <b>Every pair is checked, including the ones that must not match.</b> A
    /// predicate that returns everything passes any test that only looks at what
    /// it found.
    /// </remarks>
    [Fact]
    public async Task Relation_picks_the_same_pairs_as_PostGIS()
    {
        Require();

        IReadOnlyList<Geometry> corpus = await CorpusAsync(12);

        List<Geometry> left = [.. corpus];
        List<Geometry> right = [];

        // Right-hand shapes that certainly do intersect: each is the first
        // shape's own box, nudged. Real corpus shapes rarely touch each other,
        // and a test where nothing matches proves nothing.
        foreach (Geometry shape in corpus)
        {
            Envelope box = shape.Envelope;

            right.Add(new Polygon(new LinearRing(XySequence.Wrap(
            [
                box.MinX, box.MinY,
                box.MinX, box.MaxY,
                box.MaxX, box.MaxY,
                box.MaxX, box.MinY,
                box.MinX, box.MinY,
            ]))));
        }

        EngineResult ours = await RunAsync(
            EngineOperation.Relate, left, right, pattern: "esriGeometryRelationIntersection");

        HashSet<(int, int)> mine = [];

        foreach (int[] pair in ours.Pairs!)
        {
            mine.Add((pair[0], pair[1]));
        }

        int matched = 0;

        for (int i = 0; i < left.Count; i++)
        {
            for (int j = 0; j < right.Count; j++)
            {
                bool theirs = await ScalarAsync(
                    "select case when ST_Intersects(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1)) "
                    + "then 1 else 0 end",
                    left[i], right[j]) == 1;

                Assert.Equal(theirs, mine.Contains((i, j)));

                if (theirs)
                {
                    matched++;
                }
            }
        }

        Assert.True(matched >= left.Count, $"only {matched} pairs intersected at all.");
    }
}
