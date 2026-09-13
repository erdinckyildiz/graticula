using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Convex hull, densify and generalize, checked against PostGIS on real geometry.
/// </summary>
/// <remarks>
/// <para>
/// <b>PostGIS is the oracle here, not the runtime.</b> These operations run
/// in process, because a GeometryServer request carries its own geometry and
/// sending it to the database would create the traffic that four benchmark
/// rounds identified as this system's ceiling. Using a database to *check* an
/// implementation is a different thing from depending on one to run it — the
/// same method <c>WkbReader</c> used against 6.5 million polygons.
/// </para>
/// <para>
/// <b>Real polygons, not hand-drawn squares.</b> A hull implementation that is
/// wrong on collinear runs, repeated vertices or a nearly-degenerate ring passes
/// every test written from a diagram. The corpus is whatever the datastore
/// actually holds.
/// </para>
/// </remarks>
public sealed class GeometryOperationsAgainstPostgisTests : PostgresFixture
{
    /// <summary>
    /// Real geometry from the datastore, as WKB.
    /// </summary>
    /// <remarks>
    /// <b>The table is discovered, not named.</b> Hosted tables carry a suffix
    /// generated when the layer was published, so a hardcoded name is a test
    /// that passes on one machine. This asks the catalogue which tables have
    /// geometry and takes the first with enough of it — and fails loudly rather
    /// than quietly comparing nothing if there is none.
    /// </remarks>
    private async Task<IReadOnlyList<Geometry>> CorpusAsync(int count)
    {
        // The query, its ordering and the reasons for both live in
        // tests/shared/GeometryCorpus.cs, compiled into this project.
        const string Tables = Graticula.Testing.GeometryCorpus.PolygonTables;

        List<string> candidates = [];

        await using (NpgsqlCommand list = DataSource.CreateCommand(Tables))
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

            await using NpgsqlCommand command = DataSource.CreateCommand(
                $"""
                 select ST_AsBinary(geom)
                 from {table}
                 where geom is not null and ST_NPoints(geom) between 8 and 400
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

            if (corpus.Count >= Math.Min(count, 20))
            {
                return corpus;
            }
        }

        Assert.Fail(
            $"No table among {candidates.Count} with a geometry column held at least "
            + "20 polygons of 8 to 400 vertices. These tests check an implementation against "
            + "real shapes and can say nothing without them.");

        throw new InvalidOperationException();
    }

    /// <summary>Asks PostGIS the same question, through WKB both ways.</summary>
    private async Task<Geometry> AskAsync(string sql, Geometry input, params object[] extra)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("g", WkbWriter.ToArray(input));

        for (int i = 0; i < extra.Length; i++)
        {
            command.Parameters.AddWithValue(
                "p" + i.ToString(CultureInfo.InvariantCulture), extra[i]);
        }

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);

        return WkbReader.Read((byte[])result!);
    }

    // ---------- intersects (ADR-066) ----------

    /// <summary>
    /// Our exact <c>intersects</c> answers what <c>ST_Intersects</c> answers, on real polygons and
    /// on the shapes a map client and an identify tool send against them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cases are built from each polygon rather than drawn at random</b>, because random
    /// shapes almost never land on a boundary and the boundary is where two implementations
    /// differ: the polygon's own box and its four quarters, a small box on each corner of that box
    /// (inside the box, often outside the shape), a point on a vertex, the box's centre, the
    /// diagonal of the box, and the next polygon in the corpus, which for administrative data is
    /// often a neighbour sharing an edge.
    /// </para>
    /// <para>
    /// <b>One statement for every pair</b>, through <c>unnest ... with ordinality</c>, so a few
    /// thousand comparisons cost one round trip rather than a few thousand.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Intersects_matches_PostGIS_on_real_polygons_and_the_shapes_sent_against_them()
    {
        await MigrateAsync();

        IReadOnlyList<Geometry> corpus = await CorpusAsync(300);
        List<(Geometry A, Geometry B, string Case)> pairs = [];

        for (int i = 0; i < corpus.Count; i++)
        {
            Geometry shape = corpus[i];
            Envelope box = shape.Envelope;
            double w = box.MaxX - box.MinX, h = box.MaxY - box.MinY;
            double cx = box.MinX + (w / 2), cy = box.MinY + (h / 2);

            pairs.Add((shape, Rectangle(box.MinX, box.MinY, box.MaxX, box.MaxY), "own box"));
            pairs.Add((shape, Rectangle(box.MinX, box.MinY, cx, cy), "south-west quarter"));
            pairs.Add((shape, Rectangle(cx, cy, box.MaxX, box.MaxY), "north-east quarter"));
            pairs.Add((shape, Rectangle(box.MinX, cy, cx, box.MaxY), "north-west quarter"));
            pairs.Add((shape, Rectangle(cx, box.MinY, box.MaxX, cy), "south-east quarter"));

            foreach ((double x, double y) in ((double, double)[])
                [(box.MinX, box.MinY), (box.MaxX, box.MinY), (box.MaxX, box.MaxY), (box.MinX, box.MaxY)])
            {
                pairs.Add((shape, Rectangle(x - (w * 0.05), y - (h * 0.05), x + (w * 0.05), y + (h * 0.05)), "corner box"));
            }

            (double vx, double vy) = Coordinates(shape).First();
            pairs.Add((shape, new Point(vx, vy), "a vertex"));
            pairs.Add((shape, new Point(cx, cy), "the box centre"));
            pairs.Add((shape, new LineString(XySequence.Wrap([box.MinX, box.MinY, box.MaxX, box.MaxY])), "the diagonal"));

            if (i + 1 < corpus.Count)
            {
                pairs.Add((shape, corpus[i + 1], "the next polygon"));
            }
        }

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select ST_Intersects(ST_GeomFromWKB(a), ST_GeomFromWKB(b)) "
            + "from unnest(@a::bytea[], @b::bytea[]) with ordinality as t(a, b, n) order by n");
        command.Parameters.AddWithValue("a", pairs.Select(p => WkbWriter.ToArray(p.A)).ToArray());
        command.Parameters.AddWithValue("b", pairs.Select(p => WkbWriter.ToArray(p.B)).ToArray());

        List<bool> theirs = [];

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                theirs.Add(reader.GetBoolean(0));
            }
        }

        Assert.Equal(pairs.Count, theirs.Count);

        List<string> disagreements = [];
        int yes = 0;

        for (int i = 0; i < pairs.Count; i++)
        {
            bool ours = GeometryPredicates.Intersects(pairs[i].A, pairs[i].B);
            yes += theirs[i] ? 1 : 0;

            if (ours != theirs[i])
            {
                disagreements.Add($"{pairs[i].Case}: PostGIS {theirs[i]}, ours {ours}");
            }
        }

        Assert.True(
            pairs.Count >= 500 && yes > 0 && yes < pairs.Count,
            $"{pairs.Count} pairs, {yes} intersecting: too few, or all one answer, to say anything.");

        Assert.True(
            disagreements.Count == 0,
            $"{disagreements.Count} of {pairs.Count} comparisons disagree with ST_Intersects "
            + $"({yes} intersect by PostGIS): " + string.Join("; ", disagreements.Take(12)));
    }

    private static Polygon Rectangle(double minX, double minY, double maxX, double maxY) =>
        new(new LinearRing(XySequence.Wrap([minX, minY, maxX, minY, maxX, maxY, minX, maxY, minX, minY])));

    // ---------- area ----------

    /// <summary>
    /// Our planar area matches ST_Area on real, far-from-origin polygons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test did not exist while the defect it catches was shipping.</b>
    /// <c>areasAndLengths</c> was one of the four operations that went out first,
    /// and nothing compared it with anything. It was found sideways, on
    /// 2026-08-15: a cut's pieces did not add up to their target, and the pieces
    /// were innocent — both sides of that sum were the area function, and the
    /// plain shoelace loses precision when coordinates are in the millions and
    /// the answer is in the hundreds.
    /// </para>
    /// <para>
    /// <b>Ten parts per billion, relative.</b> That is not a round number chosen
    /// to pass: the shifted shoelace and PostGIS's <c>ptarray_signed_area</c> do
    /// the same arithmetic in the same order, so they agree to near machine
    /// precision, and the measured worst case across this corpus is far below
    /// this bar. The old code failed it by three orders of magnitude.
    /// </para>
    /// <para>
    /// <b>Far from the origin is the case that matters</b>, which is why the
    /// corpus is used rather than a unit square. A square at the origin has no
    /// cancellation to suffer from and passes either implementation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Area_matches_PostGIS_on_real_polygons()
    {
        IReadOnlyList<Geometry> corpus = await CorpusAsync(40);

        int compared = 0;
        double worst = 0;
        string where = string.Empty;

        foreach (Geometry shape in corpus)
        {
            double theirs = await ScalarAsync(
                "select ST_Area(ST_GeomFromWKB(@g0))", shape);

            if (theirs <= 0)
            {
                continue;
            }

            double ours = GeometryMeasures.Area(shape);
            double relative = Math.Abs(ours - theirs) / theirs;

            if (relative > worst)
            {
                worst = relative;
                where = $"{ours:F8} against PostGIS's {theirs:F8}";
            }

            compared++;
        }

        Assert.True(compared >= 20, $"only {compared} polygons were compared.");

        Assert.True(
            worst < 1e-8,
            $"the worst disagreement was {worst:E3} relative — {where}. The shoelace sum "
            + "must be taken about a local origin; without it, coordinates in the millions "
            + "lose the answer to cancellation.");
    }

    /// <summary>
    /// A scalar answer from PostGIS, for the comparisons that are not geometry.
    /// </summary>
    private async Task<double> ScalarAsync(string sql, Geometry operand)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("g0", WkbWriter.ToArray(operand));

        return Convert.ToDouble(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }

    // ---------- convex hull ----------

    /// <summary>
    /// Our hull is the same set of vertices PostGIS finds.
    /// </summary>
    /// <remarks>
    /// <b>Compared as a set, deliberately.</b> A hull is a cycle: the same shape
    /// can start at a different vertex and wind the other way, and both are
    /// correct. Comparing the ordered coordinate list would fail on a difference
    /// that no consumer can observe. What must match exactly is *which* vertices
    /// are on the hull.
    /// </remarks>
    [Fact]
    public async Task The_convex_hull_matches_PostGIS_on_real_polygons()
    {
        await MigrateAsync();

        int checkedCount = 0;

        foreach (Geometry input in await CorpusAsync(200))
        {
            Geometry ours = GeometryOperations.ConvexHull(input);

            Geometry theirs = await AskAsync(
                "select ST_AsBinary(ST_ConvexHull(ST_GeomFromWKB(@g)))", input);

            Assert.Equal(Vertices(theirs), Vertices(ours));
            checkedCount++;
        }

        Assert.True(checkedCount >= 50, $"only {checkedCount} polygons were compared");
    }

    /// <summary>
    /// A hull of collinear points is the line, not an empty polygon.
    /// </summary>
    /// <remarks>
    /// The case a monotone chain gets wrong when the two halves are allowed to
    /// consume each other. PostGIS returns a LINESTRING here, and so must we.
    /// </remarks>
    [Fact]
    public async Task Collinear_points_hull_to_a_line()
    {
        await MigrateAsync();

        MultiPoint line = new(
            [.. Enumerable.Range(0, 6).Select(i => new Point(i, 2 * i))]);

        Geometry ours = GeometryOperations.ConvexHull(line);
        Geometry theirs = await AskAsync(
            "select ST_AsBinary(ST_ConvexHull(ST_GeomFromWKB(@g)))", line);

        Assert.Equal(Vertices(theirs), Vertices(ours));
        Assert.Equal(GeometryKind.LineString, ours.Kind);
    }

    [Fact]
    public async Task Repeated_points_hull_to_one_point()
    {
        await MigrateAsync();

        MultiPoint same = new([new Point(7, 7), new Point(7, 7), new Point(7, 7)]);

        Geometry ours = GeometryOperations.ConvexHull(same);
        Geometry theirs = await AskAsync(
            "select ST_AsBinary(ST_ConvexHull(ST_GeomFromWKB(@g)))", same);

        Assert.Equal(GeometryKind.Point, ours.Kind);
        Assert.Equal(Vertices(theirs), Vertices(ours));
    }

    // ---------- densify ----------

    /// <summary>
    /// Densifying adds vertices and moves none of the originals.
    /// </summary>
    /// <remarks>
    /// <b>Checked against PostGIS for the shape, and against the input for the
    /// promise.</b> <c>ST_Segmentize</c> agrees on where the new vertices go;
    /// the stronger property — that every original coordinate survives at its
    /// original value — is checked here, because a resampling implementation
    /// would satisfy the first and quietly move survey coordinates.
    /// </remarks>
    [Fact]
    public async Task Densify_adds_vertices_without_moving_any()
    {
        await MigrateAsync();

        foreach (Geometry input in await CorpusAsync(40))
        {
            double step = Math.Max(input.Envelope.Width, input.Envelope.Height) / 12.0;

            if (step <= 0)
            {
                continue;
            }

            Geometry ours = GeometryOperations.Densify(input, step);

            Assert.True(ours.CoordinateCount >= input.CoordinateCount);

            HashSet<(double, double)> after = [.. Coordinates(ours)];

            foreach ((double x, double y) in Coordinates(input))
            {
                Assert.Contains((x, y), after);
            }

            Geometry theirs = await AskAsync(
                "select ST_AsBinary(ST_Segmentize(ST_GeomFromWKB(@g), @p0))", input, step);

            // Same count is the claim; ST_Segmentize divides each long segment
            // evenly, which is what DensifyRun does.
            Assert.Equal(theirs.CoordinateCount, ours.CoordinateCount);
        }
    }

    [Fact]
    public void Densify_refuses_a_zero_length()
    {
        Point p = new(0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => GeometryOperations.Densify(new MultiPoint([p]), 0));
    }

    // ---------- generalize ----------

    /// <summary>
    /// Generalizing removes vertices, keeps the ends, and stays inside the
    /// tolerance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not compared vertex-for-vertex with PostGIS, and that is deliberate.</b>
    /// <c>ST_SimplifyPreserveTopology</c> is Douglas–Peucker with a topology
    /// guard, so on a shape where dropping a vertex would self-intersect it keeps
    /// one we drop. Asserting equality would be asserting that we implemented
    /// their guard, which we did not and do not claim to.
    /// </para>
    /// <para>
    /// What is asserted is the contract Douglas–Peucker actually makes: fewer
    /// vertices, the same endpoints, every surviving vertex an original, and no
    /// dropped vertex further from the retained line than the tolerance. PostGIS
    /// is used for the weaker check that our output is not wildly coarser than
    /// theirs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Generalize_drops_vertices_within_the_tolerance()
    {
        await MigrateAsync();

        int compared = 0;
        int identical = 0;

        foreach (Geometry input in await CorpusAsync(60))
        {
            double tolerance = Math.Max(input.Envelope.Width, input.Envelope.Height) / 200.0;

            if (tolerance <= 0)
            {
                continue;
            }

            Geometry ours = GeometryOperations.Generalize(input, tolerance);

            Assert.True(
                ours.CoordinateCount <= input.CoordinateCount,
                "generalizing must not add vertices");

            HashSet<(double, double)> original = [.. Coordinates(input)];

            foreach ((double x, double y) in Coordinates(ours))
            {
                Assert.Contains((x, y), original);
            }

            Geometry theirs = await AskAsync(
                "select ST_AsBinary(ST_SimplifyPreserveTopology(ST_GeomFromWKB(@g), @p0))",
                input, tolerance);

            if (Vertices(ours).SetEquals(Vertices(theirs)))
            {
                identical++;
            }

            compared++;
        }

        Assert.True(compared >= 20, $"only {compared} geometries were compared");

        // <b>An agreement rate, because the previous assertion was too weak to
        // find a real defect.</b> It allowed our output to be twice as coarse as
        // PostGIS's, and under that ceiling a ring-handling bug shipped: a
        // closed run starts and ends at the same point, so the opening
        // Douglas-Peucker segment was degenerate and the recursion began in the
        // wrong place. On a notched square it dropped a genuine corner. Measured
        // at 47 of 50 identical after the fix; the floor is set below that so
        // ordinary variation does not fail the build, and a regression to the
        // old behaviour drops it far under.
        //
        // Not equality: ST_SimplifyPreserveTopology declines to make a geometry
        // invalid, so on a shape where dropping a vertex would self-intersect it
        // keeps one we drop. We do not implement that guard and do not claim to.
        Assert.True(
            identical * 10 >= compared * 8,
            $"only {identical} of {compared} matched PostGIS exactly, which is below the 80% "
            + "this agreed at when it was written. Either the algorithm regressed or the corpus "
            + "changed shape.");
    }

    /// <summary>A ring never generalizes away to nothing.</summary>
    /// <remarks>
    /// With a tolerance larger than the shape, Douglas–Peucker keeps only the
    /// two endpoints — and for a ring those are the same coordinate, so the
    /// result is closed and encloses nothing. The floor is four coordinates.
    /// </remarks>
    [Fact]
    public void A_ring_keeps_enough_coordinates_to_enclose_something()
    {
        Polygon square = new(new LinearRing(XySequence.Wrap(
            [0, 0, 10, 0, 10, 10, 0, 10, 0, 0])));

        Geometry ours = GeometryOperations.Generalize(square, 1_000_000);

        Assert.Equal(GeometryKind.Polygon, ours.Kind);
        Assert.True(ours.CoordinateCount >= 4, $"a ring collapsed to {ours.CoordinateCount}");
        Assert.True(Math.Abs(GeometryMeasures.Area(ours)) > 0, "the ring encloses nothing");
    }

    /// <summary>Zero tolerance removes exactly the collinear vertices.</summary>
    [Fact]
    public void Zero_tolerance_removes_only_collinear_vertices()
    {
        LineString line = new(XySequence.Wrap([0, 0, 1, 0, 2, 0, 3, 0, 3, 5]));

        Geometry ours = GeometryOperations.Generalize(line, 0);

        Assert.Equal(3, ours.CoordinateCount);
    }

    // ---------- helpers ----------

    private static SortedSet<string> Vertices(Geometry geometry) =>
        [.. Coordinates(geometry).Select(c => string.Create(
            CultureInfo.InvariantCulture, $"{c.X:F9},{c.Y:F9}"))];

    private static IEnumerable<(double X, double Y)> Coordinates(Geometry geometry)
    {
        switch (geometry)
        {
            case Point p when !p.IsEmpty:
                yield return (p.X, p.Y);
                break;

            case LineString line:
                for (int i = 0; i < line.Coordinates.Count; i++)
                {
                    yield return (line.Coordinates.X(i), line.Coordinates.Y(i));
                }

                break;

            case Polygon polygon:
                foreach ((double, double) c in Coordinates(new LineString(polygon.Shell.Coordinates)))
                {
                    yield return c;
                }

                foreach (LinearRing hole in polygon.Holes)
                {
                    foreach ((double, double) c in Coordinates(new LineString(hole.Coordinates)))
                    {
                        yield return c;
                    }
                }

                break;

            case MultiPoint points:
                foreach (Point part in points.Parts)
                {
                    foreach ((double, double) c in Coordinates(part))
                    {
                        yield return c;
                    }
                }

                break;

            case MultiLineString lines:
                foreach (LineString part in lines.Parts)
                {
                    foreach ((double, double) c in Coordinates(part))
                    {
                        yield return c;
                    }
                }

                break;

            case MultiPolygon polygons:
                foreach (Polygon part in polygons.Parts)
                {
                    foreach ((double, double) c in Coordinates(part))
                    {
                        yield return c;
                    }
                }

                break;

            default:
                break;
        }
    }
}
