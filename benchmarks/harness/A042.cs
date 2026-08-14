using System.Diagnostics;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;
using Npgsql;

namespace GisBench;

/// <summary>
/// A-042 — where must the GeometryServer cap sit, and is a safe cap useful?
/// </summary>
/// <remarks>
/// <para>
/// GeometryServer publishes <c>intersect</c>, <c>difference</c>, <c>union</c>
/// and <c>cut</c>: general polygon overlay, on geometry a caller posts. Run 1
/// measured <c>NTS.Intersection</c> at 438.6 ms — 79% of a tile request — and
/// the fix there was to stop calling it. **That escape does not exist when
/// overlay is the product.**
/// </para>
/// <para>
/// So the endpoint needs caps, and A-042 is the assumption that caps on vertex
/// count, batch size and wall clock make a public overlay endpoint safe. It says
/// how to validate it: measure overlay cost against vertex count to find where
/// the cap must sit, <b>and check whether a cap low enough to be safe is still
/// high enough to be useful.</b> The second half is the one that can fail.
/// </para>
/// <para>
/// <b>Real geometry, not generated.</b> A synthetic polygon with n vertices on a
/// circle is the easy case for an overlay engine — no self-proximity, no slivers,
/// no near-degenerate edges. Administrative boundaries are the hard case and
/// they are what a caller actually posts.
/// </para>
/// </remarks>
public static class A042
{
    private sealed record Case(string Name, int Vertices, Geometry A, Geometry B);

    private sealed record Result(
        string Op, int Vertices, double Median, double Worst, long AllocBytes, int OutVertices);

    public static async Task RunAsync(string[] args)
    {
        int rounds = Arg(args, "--rounds", 5);

        string conn = Environment.GetEnvironmentVariable("GISBENCH_CONN")
            ?? "Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis";

        await using NpgsqlDataSource ds = new NpgsqlDataSourceBuilder(conn).Build();

        GeometryFactory factory = new(new PrecisionModel(PrecisionModels.Floating), 3857);
        PostGisReader reader = new(factory.CoordinateSequenceFactory, factory.PrecisionModel);

        Console.WriteLine("A-042 — overlay cost against vertex count, on real polygons");
        Console.WriteLine("  NetTopologySuite OverlayNG, single-threaded, warm.\n");

        List<Case> cases = await LoadAsync(ds, reader, factory);

        Console.WriteLine($"  {cases.Count} cases loaded: "
                          + string.Join(", ", cases.Select(c => c.Vertices.ToString("N0"))) + " vertices\n");

        List<Result> results = [];

        foreach (Case c in cases)
        {
            foreach ((string op, Func<Geometry, Geometry, Geometry> run) in Operations)
            {
                results.Add(Measure(op, c, run, rounds));
            }
        }

        Console.WriteLine(
            "operation".PadRight(12) + "vertices".PadLeft(10) + "median ms".PadLeft(12)
            + "worst ms".PadLeft(11) + "alloc MB".PadLeft(10) + "out verts".PadLeft(11));
        Console.WriteLine(new string('-', 68));

        foreach (Result r in results.OrderBy(r => r.Vertices).ThenBy(r => r.Op, StringComparer.Ordinal))
        {
            Console.WriteLine($"{r.Op,-12} {r.Vertices,9:N0} {r.Median,11:F1} {r.Worst,10:F1} "
                              + $"{r.AllocBytes / 1048576.0,9:F1} {r.OutVertices,10:N0}");
        }

        Console.WriteLine();
        Console.WriteLine("  candidate segment pairs, for the same real cases:");

        foreach (Case c in cases)
        {
            Stopwatch clock = Stopwatch.StartNew();
            long pairs = CandidatePairs(c.A, c.B);
            Console.WriteLine($"    {c.Vertices,9:N0} vertices -> {pairs,10:N0} candidates "
                              + $"in {clock.Elapsed.TotalMilliseconds,7:F1} ms");
        }

        Adversarial();

        Verdict(results);
    }

    /// <summary>
    /// Counts how many segment pairs could possibly cross, without crossing them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question this answers: is there a cheap number that predicts the
    /// expensive one?</b> Vertex count is not it — measured, a 6,408-vertex
    /// adversarial pair costs 458× the time of a 72,919-vertex national outline.
    /// So a cap on input size cannot make the endpoint safe, and something else
    /// has to.
    /// </para>
    /// <para>
    /// Candidate pairs are what overlay actually works on. An R-tree over one
    /// geometry's segments, queried with the other's, gives the count in
    /// O((n+m) log n) — cheap enough to run before deciding whether to run the
    /// overlay at all. If it tracks cost, a pre-flight check is the control that
    /// A-042 assumed a vertex cap would be.
    /// </para>
    /// </remarks>
    private static long CandidatePairs(Geometry a, Geometry b)
    {
        STRtree<Envelope> tree = new();

        foreach (Envelope segment in Segments(a))
        {
            tree.Insert(segment, segment);
        }

        tree.Build();

        long pairs = 0;

        foreach (Envelope segment in Segments(b))
        {
            pairs += tree.Query(segment).Count;
        }

        return pairs;
    }

    private static IEnumerable<Envelope> Segments(Geometry g)
    {
        Coordinate[] cs = g.Coordinates;

        for (int i = 1; i < cs.Length; i++)
        {
            yield return new Envelope(cs[i - 1], cs[i]);
        }
    }

    /// <summary>
    /// What somebody posts on purpose, rather than what a GIS analyst posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The real-data curve is not the security question.</b> A-042 is about a
    /// <em>public</em> overlay endpoint, and an attacker does not post
    /// administrative boundaries. Two combs at right angles have n teeth each and
    /// produce n² intersection points, so the output — and the work — is
    /// quadratic in an input that satisfies any vertex cap comfortably.
    /// </para>
    /// <para>
    /// This is the test that decides whether A-042 holds as written. If a small
    /// polygon can cost more than a large one, then a cap on vertex count is not
    /// the control it was assumed to be.
    /// </para>
    /// </remarks>
    private static void Adversarial()
    {
        GeometryFactory f = new(new PrecisionModel(PrecisionModels.Floating), 3857);

        Console.WriteLine();
        Console.WriteLine("---- what a caller posts on purpose ----");
        Console.WriteLine(
            "teeth".PadLeft(7) + "vertices".PadLeft(10) + "candidates".PadLeft(12)
            + "predict ms".PadLeft(12) + "intersect ms".PadLeft(14)
            + "alloc MB".PadLeft(10) + "out verts".PadLeft(11));

        // <b>Stops at 400, and the earlier run is why.</b> The sweep reached
        // 800 teeth once: 153 seconds and 16.7 GB allocated, which pushed the
        // host into swap and took the Docker daemon — and with it the database
        // this benchmark reads from — down with it. The 800 figure is recorded
        // in RESULTS.md and does not need reproducing. A benchmark that has to
        // destroy the machine to make its point only has to do it once.
        foreach (int teeth in (int[])[50, 100, 200, 400])
        {
            Geometry a = Comb(f, teeth, horizontal: true);
            Geometry b = Comb(f, teeth, horizontal: false);

            Stopwatch predicting = Stopwatch.StartNew();
            long candidates = CandidatePairs(a, b);
            double predictMs = predicting.Elapsed.TotalMilliseconds;

            long before = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch clock = Stopwatch.StartNew();

            Geometry result;

            try
            {
                result = a.Intersection(b);
            }
            catch (Exception e)
            {
                Console.WriteLine($"{teeth,7} {a.NumPoints + b.NumPoints,9:N0} {candidates,11:N0}"
                                  + $"   threw {e.GetType().Name}");
                continue;
            }

            double ms = clock.Elapsed.TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Console.WriteLine(
                $"{teeth,7} {a.NumPoints + b.NumPoints,9:N0} {candidates,11:N0} "
                + $"{predictMs,11:F1} {ms,13:F1} "
                + $"{allocated / 1048576.0,9:F1} {result.NumPoints,10:N0}");

            if (ms > 20_000)
            {
                Console.WriteLine("        stopping — past twenty seconds the point is made");
                break;
            }
        }
    }

    /// <summary>
    /// A comb: a rectangle with n slots cut into one edge.
    /// </summary>
    /// <remarks>
    /// Crossed with a comb turned ninety degrees, every tooth of one meets every
    /// tooth of the other. n teeth each, n² crossings, and roughly 4n vertices of
    /// input — so the input is linear and the answer is quadratic.
    /// </remarks>
    private static Geometry Comb(GeometryFactory f, int teeth, bool horizontal)
    {
        List<Coordinate> ring = [];
        double span = 1000.0;
        double pitch = span / (teeth * 2);

        // Up one side, cutting slots, then straight back along the other.
        for (int i = 0; i < teeth; i++)
        {
            double x0 = i * 2 * pitch;
            ring.Add(At(x0, 0, horizontal));
            ring.Add(At(x0, span * 0.9, horizontal));
            ring.Add(At(x0 + pitch, span * 0.9, horizontal));
            ring.Add(At(x0 + pitch, 0, horizontal));
        }

        ring.Add(At(span, 0, horizontal));
        ring.Add(At(span, -span * 0.1, horizontal));
        ring.Add(At(0, -span * 0.1, horizontal));
        ring.Add(ring[0].Copy());

        return f.CreatePolygon(f.CreateLinearRing([.. ring]));
    }

    private static Coordinate At(double x, double y, bool horizontal) =>
        horizontal ? new Coordinate(x, y) : new Coordinate(y, x);

    private static readonly (string Name, Func<Geometry, Geometry, Geometry> Run)[] Operations =
    [
        ("intersect", (a, b) => a.Intersection(b)),
        ("difference", (a, b) => a.Difference(b)),
        ("union", (a, b) => a.Union(b)),
    ];

    private static Result Measure(
        string op, Case c, Func<Geometry, Geometry, Geometry> run, int rounds)
    {
        // One warm-up: OverlayNG's first call in a process pays JIT and static
        // initialisation, and charging that to the smallest case would make the
        // curve look superlinear for the wrong reason.
        Geometry warm;

        try
        {
            warm = run(c.A, c.B);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  {op} at {c.Vertices:N0} vertices threw: {e.GetType().Name}");
            return new Result(op, c.Vertices, -1, -1, 0, 0);
        }

        List<double> times = [];
        long before = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < rounds; i++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            warm = run(c.A, c.B);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        long allocated = (GC.GetTotalAllocatedBytes(precise: true) - before) / rounds;
        times.Sort();

        return new Result(op, c.Vertices, times[times.Count / 2], times[^1], allocated, warm.NumPoints);
    }

    /// <summary>
    /// Loads real polygons across the vertex range, each paired with a shifted
    /// copy of itself.
    /// </summary>
    /// <remarks>
    /// <b>Shifted by a tenth of its own width</b>, which forces maximal edge
    /// interaction: every ring crosses its twin many times, which is the
    /// expensive case and the one a caller reaches by intersecting two versions
    /// of the same boundary. Intersecting a polygon with a distant one is the
    /// case OverlayNG rejects on a bounding box in microseconds, and measuring
    /// that would produce a flattering curve about nothing.
    /// </remarks>
    private static async Task<List<Case>> LoadAsync(
        NpgsqlDataSource ds, PostGisReader reader, GeometryFactory factory)
    {
        int[] targets = [500, 2_000, 5_000, 10_000, 20_000, 30_000, 45_000, 60_000, 72_919];
        List<Case> cases = [];

        foreach (int target in targets)
        {
            // Reads a materialised table with verts indexed, not planet_osm_polygon.
            // The original ordered 6.5 million rows by abs(ST_NPoints(way) - target),
            // which computes the vertex count of every polygon in the corpus. It
            // worked on a warm cache and timed out on a cold one, which is a
            // benchmark that measures whether somebody ran it recently.
            //
            //   create table a042_cases as select osm_id, coalesce(name,'?') name,
            //     way, ST_NPoints(way) verts from planet_osm_polygon
            //     where ST_NPoints(way) >= 300 and ST_IsValid(way);
            //   create index on a042_cases (verts);
            const string Sql = """
                select ST_AsBinary(way), verts, name
                from public.a042_cases
                where verts between @low and @high
                order by abs(verts - @target)
                limit 1
                """;

            await using NpgsqlCommand command = ds.CreateCommand(Sql);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("low", (int)(target * 0.7));
            command.Parameters.AddWithValue("high", (int)(target * 1.4));

            await using NpgsqlDataReader rdr = await command.ExecuteReaderAsync();

            if (!await rdr.ReadAsync())
            {
                continue;
            }

            Geometry a = reader.Read((byte[])rdr[0]);
            int vertices = rdr.GetInt32(1);

            double shift = a.EnvelopeInternal.Width / 10;
            Geometry b = Shift(a, shift, shift, factory);

            cases.Add(new Case(rdr.GetString(2), vertices, a, b));
        }

        return cases;
    }

    private static Geometry Shift(Geometry g, double dx, double dy, GeometryFactory f)
    {
        Coordinate[] Move(Coordinate[] cs) =>
            [.. cs.Select(c => new Coordinate(c.X + dx, c.Y + dy))];

        return g switch
        {
            Polygon p => f.CreatePolygon(
                f.CreateLinearRing(Move(p.ExteriorRing.Coordinates)),
                [.. p.InteriorRings.Select(r => f.CreateLinearRing(Move(r.Coordinates)))]),
            MultiPolygon mp => f.CreateMultiPolygon(
                [.. mp.Geometries.Select(x => (Polygon)Shift(x, dx, dy, f))]),
            _ => g,
        };
    }

    /// <summary>
    /// Reads the numbers back as the two answers A-042 actually asked for.
    /// </summary>
    private static void Verdict(List<Result> results)
    {
        // One second is the budget. Not because a second is pleasant, but
        // because an HTTP endpoint holding a request thread longer than that
        // under concurrency is how a public surface becomes a denial of service
        // against itself — and because a caller waiting longer than a second for
        // an intersect will assume it hung.
        const double Budget = 1000;

        Console.WriteLine($"\n---- where the cap must sit (budget {Budget:F0} ms) ----");

        foreach (string op in Operations.Select(o => o.Name))
        {
            List<Result> series = [.. results.Where(r => r.Op == op && r.Median >= 0)
                                             .OrderBy(r => r.Vertices)];

            Result? last = series.LastOrDefault(r => r.Median <= Budget);
            Result? first = series.FirstOrDefault(r => r.Median > Budget);

            Console.WriteLine(first is null
                ? $"  {op,-12} every case measured stayed inside the budget "
                  + $"(worst {series.Max(r => r.Median):F0} ms at {series[^1].Vertices:N0} vertices)"
                : $"  {op,-12} last inside {last?.Vertices ?? 0:N0} vertices "
                  + $"({last?.Median ?? 0:F0} ms), first outside {first.Vertices:N0} "
                  + $"({first.Median:F0} ms)");
        }

        Console.WriteLine("\n---- and is a safe cap still useful? ----");
        Console.WriteLine("  Compare against what a caller actually posts. In this corpus:");
        Console.WriteLine("    33,058 polygons have 200-5,000 vertices");
        Console.WriteLine("       227 have 5,000-10,000");
        Console.WriteLine("        56 have more than 15,000");
        Console.WriteLine("         1 has 72,919 — the national outline");
        Console.WriteLine("  A cap excludes the tail. Whether that tail is the work people came "
                          + "for is not\n  a question a benchmark can answer, and it is the half of "
                          + "A-042 that stays open.");
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }
}
