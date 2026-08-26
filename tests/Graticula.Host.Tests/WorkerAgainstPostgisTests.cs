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

            // <b>The same two-minute bound as PostgresFixture, for the same measured
            // reason.</b> These compare the overlay worker against PostGIS on the same
            // 2,240 MB corpus table, so they meet the same 1,362 MB cold read at 40.8
            // MB/s — 33.4 s against Npgsql's 30-second default. The full account is in
            // PostgresFixture and on D-43; what matters here is that the bound lives in
            // both places that read the corpus, because a fix applied to one of the
            // several places that carry a behaviour is D-46 exactly.
            NpgsqlDataSourceBuilder builder = new(connection);
            builder.ConnectionStringBuilder.CommandTimeout = 120;
            _source = builder.Build();

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

    // ---------- Q-20: the two engines, on the cases a corpus does not contain ----------

    /// <summary>
    /// Every predicate both engines can answer, on the cases where engines diverge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[Q-20](../../docs/open-questions.md) asks how many geometry engines end up evaluating our
    /// predicates and how divergence is prevented. In v1 the answer is two</b> — PostGIS's GEOS for every
    /// spatial filter on a query, and NetTopologySuite's JTS port in the overlay worker for
    /// <c>GeometryServer/relation</c> — and six predicates are answerable by both. Until 2026-08-19
    /// nothing had compared them beyond <c>intersects</c> on a corpus.
    /// </para>
    /// <para>
    /// <b>The cases are not from the corpus, and that is the point.</b>
    /// <c>Relation_picks_the_same_pairs_as_PostGIS</c> above runs on real polygons from the datastore,
    /// which is the right test for *does this work at all* and the wrong one for divergence: real data
    /// contains almost no self-intersecting bowties, no pairs a nanometre apart, and no polygon sitting
    /// inside another's hole. Those are where GEOS and JTS are documented to be able to part company, so
    /// they are written out here by hand — the fifteen cases from
    /// <c>experiments/geometry-oracle</c>, which measured all six predicates over HTTP and found the two
    /// engines agreeing on every one.
    /// </para>
    /// <para>
    /// <b>What this guards is a version, not a design.</b> The experiment established agreement once;
    /// this fails when an NetTopologySuite upgrade, a GEOS upgrade, or a change to
    /// <c>Satisfies</c> moves an answer. That is the only way the agreement can be lost, and it is
    /// exactly the kind of change that arrives in a dependency bump nobody reads.
    /// </para>
    /// <para>
    /// <b>PostGIS is the oracle and it is not being called correct.</b> Where they agree, both are
    /// consistent — that is all this asserts. If a future case is found where the two differ, the
    /// question of which is right is a decision for an ADR, not for a test.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("shared edge")]
    [InlineData("shared vertex only")]
    [InlineData("identical")]
    [InlineData("point on the boundary")]
    [InlineData("point in the middle")]
    [InlineData("line ending on the boundary")]
    [InlineData("line through")]
    [InlineData("line along the boundary")]
    [InlineData("invalid bowtie against a square")]
    [InlineData("invalid bowtie against itself")]
    [InlineData("a nanometre apart")]
    [InlineData("a nanometre overlapping")]
    [InlineData("collapsed sliver")]
    [InlineData("polygon inside a hole")]
    [InlineData("multipolygon touching at one part")]
    public async Task Both_engines_agree_on_the_case_engines_disagree_about(string which)
    {
        Require();

        // The six predicates with a name on both surfaces, and the eight DE-9IM patterns. `st_dwithin`
        // is the query path's alone, and `st_contains` is `within` with the arguments swapped.
        //
        // <b>`st_relate` was excluded from the first version of this test and of the experiment, on the
        // grounds that no Esri relation name maps to it. That was wrong:</b>
        // `esriGeometryRelationRelation` carries a pattern in `relationParam` and reaches
        // `relation.Matches(pattern)` in the worker, while `SpatialRelation.Relate` reaches
        // `st_relate(column, filter, @pattern)` in the provider. So both engines answer DE-9IM — the
        // most intricate predicate either library has — and excluding it left the hardest comparison
        // unmade.
        (string Sql, string? Esri, string? Pattern)[] predicates =
        [
            ("ST_Intersects(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
             "esriGeometryRelationIntersection", null),
            ("ST_Within(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
             "esriGeometryRelationWithin", null),
            ("ST_Crosses(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
             "esriGeometryRelationCross", null),
            ("ST_Overlaps(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
             "esriGeometryRelationOverlap", null),
            ("ST_Touches(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
             "esriGeometryRelationTouch", null),
            ("ST_Disjoint(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1))",
             "esriGeometryRelationDisjoint", null),

            // Each pattern separates two cases a named predicate runs together.
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), 'T********')", null, "T********"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), 'FF*FF****')", null, "FF*FF****"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), 'T*F**F***')", null, "T*F**F***"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), 'F***T****')", null, "F***T****"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), '2********')", null, "2********"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), '*T*******')", null, "*T*******"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), '****1****')", null, "****1****"),
            ("ST_Relate(ST_GeomFromWKB(@g0), ST_GeomFromWKB(@g1), 'T*T***T**')", null, "T*T***T**"),
        ];

        // <b>Twice, because floating point diverges with magnitude.</b> The cases are written within
        // 0–25 and web-Mercator metres run to 2×10⁷: at an offset of 2×10⁶ the gap between
        // representable doubles is about 5×10⁻¹⁰, so the nanometre cases sit two steps above it —
        // close enough that two libraries could round in opposite directions, and far enough that the
        // case is still a real gap rather than a collapsed one. Verified in the experiment: the
        // nanometre pair is still disjoint at 2×10⁶ rather than having snapped together, so the
        // comparison is not vacuous at that magnitude.
        // <b>And 2×10⁷, which is where web Mercator actually ends — Q-20, 2026-08-26.</b>
        // The row left this open in as many words: *magnitudes beyond 2×10⁶ — the far edge of
        // web Mercator is ten times further, where the step between doubles is larger than the
        // nanometre cases, so a different set would be needed rather than the same one moved*.
        // `Separation` is that different set: the same fifteen shapes with the one measurement
        // that depends on magnitude scaled to the magnitude.
        foreach (double offset in (double[])[0, 2_000_000, 20_000_000])
        {
            // <b>The far slice has to be a comparison rather than a tautology.</b> If the
            // separation rounds away at this magnitude, the precision cases become two
            // identical polygons and fifteen green assertions say nothing — the shape of a
            // check that passes because it is looking at nothing. So the arithmetic is
            // asserted before the engines are asked.
            Assert.True(
                offset + 20 + Separation(offset) != offset + 20,
                $"At an offset of {offset:N0} the separation {Separation(offset):E3} is smaller "
                + "than the gap between representable doubles, so the precision cases collapse "
                + "and this magnitude proves nothing. That is Q-20's reason for scaling it.");

            (Geometry left, Geometry right) = HardCase(which, offset);

            foreach ((string sql, string? esri, string? pattern) in predicates)
            {
                bool theirs = await ScalarAsync(
                    $"select case when {sql} then 1 else 0 end", left, right) == 1;

                EngineResult ours = await RunAsync(
                    EngineOperation.Relate, [left], [right], pattern: pattern ?? esri!);

                bool mine = ours.Pairs is { Count: > 0 };

                Assert.True(
                    theirs == mine,
                    $"'{which}' at offset {offset}: PostGIS says {sql} is {theirs} and this server's "
                    + $"own engine says {mine}. Two engines answering the same question differently is "
                    + "Q-20 arriving with an instance — and a query and a GeometryServer call can both "
                    + "be asked this. Which answer is right is a decision for an ADR; what is certain "
                    + "is that the product must not give both.");
            }
        }
    }

    /// <summary>
    /// The fifteen pairs, built here so that both engines are handed the same numbers.
    /// </summary>
    /// <remarks>
    /// <b>One definition, two consumers.</b> The worker gets these objects and PostGIS gets their WKB,
    /// so a disagreement cannot be a transcription error — which is what writing the same polygon twice,
    /// once as WKT and once as rings, would risk. The oracle in <c>experiments/geometry-oracle</c> makes
    /// the same arrangement over HTTP and had to compute ring winding to do it; here there is no
    /// winding question, because <c>Polygon</c> carries shell and holes as separate rings.
    /// </remarks>
    /// <summary>
    /// The smallest separation that is still a real gap at this distance from the origin.
    /// </summary>
    /// <param name="at">How far out the case is being written.</param>
    /// <remarks>
    /// <b>Two representable steps, or a nanometre, whichever is larger — Q-20.</b> The cases
    /// this feeds exist to ask whether two libraries round the same way about a distance near
    /// the limit of what a double can hold, and *near the limit* is a different number at every
    /// magnitude: 3.6×10⁻¹⁵ at the origin, 2.3×10⁻¹⁰ at 2×10⁶ and **3.7×10⁻⁹ at 2×10⁷**. A
    /// fixed nanometre is two steps at the middle magnitude and **less than one** at the far
    /// one, where it would round away and leave two identical polygons being compared. The
    /// floor keeps the near and middle slices exactly as they were measured on 2026-08-19.
    /// </remarks>
    private static double Separation(double at) =>
        Math.Max(1e-9, 2 * Math.Abs(Math.BitIncrement(at + 20) - (at + 20)));

    private static (Geometry Left, Geometry Right) HardCase(string which, double at = 0)
    {
        // Every coordinate is shifted by the same offset, so the *shape* is identical and only its
        // distance from the origin changes — which is the one variable being tested.
        Polygon Square(double x0, double y0, double x1, double y1) =>
            new(new LinearRing(XySequence.Wrap(
            [
                x0 + at, y0 + at, x0 + at, y1 + at, x1 + at, y1 + at,
                x1 + at, y0 + at, x0 + at, y0 + at,
            ])));

        LineString Line(params double[] xy) =>
            new(XySequence.Wrap([.. System.Linq.Enumerable.Select(xy, v => v + at)]));

        LinearRing Ring(params double[] xy) =>
            new(XySequence.Wrap([.. System.Linq.Enumerable.Select(xy, v => v + at)]));

        Polygon unit = Square(0, 0, 10, 10);

        return which switch
        {
            "shared edge" => (unit, Square(10, 0, 20, 10)),
            "shared vertex only" => (unit, Square(10, 10, 20, 20)),
            "identical" => (unit, Square(0, 0, 10, 10)),

            "point on the boundary" => (new Point(0 + at, 5 + at), unit),
            "point in the middle" => (new Point(5 + at, 5 + at), unit),

            "line ending on the boundary" => (Line(-5, 5, 0, 5), unit),
            "line through" => (Line(-5, 5, 15, 5), unit),
            "line along the boundary" => (Line(0, 2, 0, 8), unit),

            // Self-intersecting: the diagonals cross, so this is invalid and both engines are being
            // asked what they make of it rather than being expected to refuse.
            "invalid bowtie against a square" => (
                new Polygon(Ring(0, 0, 10, 10, 10, 0, 0, 10, 0, 0)),
                Square(2, 2, 8, 8)),
            "invalid bowtie against itself" => (
                new Polygon(Ring(0, 0, 10, 10, 10, 0, 0, 10, 0, 0)),
                new Polygon(Ring(0, 0, 10, 10, 10, 0, 0, 10, 0, 0))),

            // <b>The separation is a function of the magnitude, not a constant — Q-20.</b>
            // A nanometre is two representable steps at 2×10⁶ and *below* one at 2×10⁷, where
            // the gap between doubles is 3.7×10⁻⁹ — so moving the same case out there would
            // collapse it and the comparison would prove that two engines agree about one
            // polygon. `Separation` keeps it at 1e-9 where 1e-9 is real and widens it to two
            // steps where it is not, which is what makes the far slice mean the same thing as
            // the near one.
            "a nanometre apart" => (unit, Square(10 + Separation(at), 0, 20, 10)),
            "a nanometre overlapping" => (unit, Square(10 - Separation(at), 0, 20, 10)),
            "collapsed sliver" => (Square(0, 0, 10, Separation(at)), unit),

            // Inside the hole, so disjoint from the polygon rather than within it.
            "polygon inside a hole" => (
                Square(3, 3, 7, 7),
                new Polygon(
                    Ring(0, 0, 0, 10, 10, 10, 10, 0, 0, 0),
                    [Ring(2, 2, 2, 8, 8, 8, 8, 2, 2, 2)])),

            "multipolygon touching at one part" => (
                new MultiPolygon([Square(0, 0, 5, 5), Square(20, 20, 25, 25)]),
                Square(5, 0, 10, 5)),

            _ => throw new ArgumentOutOfRangeException(nameof(which), which, "No such case."),
        };
    }
}
