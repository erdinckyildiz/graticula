using System.Diagnostics;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace GisBench;

/// <summary>
/// Q-68 — do we keep our own MVT encoder now that every tile source is PostGIS?
/// </summary>
/// <remarks>
/// <para>
/// Q-67 removed the reason the encoder existed: tiles come only from hosted
/// data, hosted data is PostGIS, and PostGIS has <c>ST_AsMVT</c>. Run 3 already
/// measured <c>ST_AsMVT</c> beating our in-process path 96.3 to 69.9 req/s under
/// load, so there is no serving argument left either.
/// </para>
/// <para>
/// <b>One argument survives, and Q-68 says to measure it rather than argue it.</b>
/// <c>ST_AsMVT</c> is one database round trip per tile. Reading a parent tile's
/// extent once and encoding all of its children in process is one round trip per
/// <em>N</em> tiles — and a z12 read covers 16 z14 tiles, or 256 z16 tiles. That
/// is exactly the shape of seeding, which is where tile cost actually lands
/// (ADR-010 §6).
/// </para>
/// <para>
/// <b>What is measured:</b> whole-pyramid throughput in tiles per second, not
/// single-tile latency. Single-tile latency is the question run 3 already
/// answered and <c>ST_AsMVT</c> won it.
/// </para>
/// <para>
/// <b>What would make this measurement a lie, and is therefore checked:</b> a
/// read-once path that wins by emitting less. Both paths report feature counts
/// and byte totals per tile, and a run whose outputs diverge materially is not a
/// speed result, it is a correctness bug.
/// </para>
/// </remarks>
public static class Q68
{
    private const string Table = "planet_osm_polygon";
    private const string GeomCol = "way";

    private sealed record TileResult(int Z, int X, int Y, int Bytes, int Features);

    private sealed record PathResult(
        string Name, double Seconds, long AllocBytes, int Gen0, int Gen2,
        double GcPauseMs, int Queries, List<TileResult> Tiles)
    {
        public int TileCount => Tiles.Count;

        public double TilesPerSecond => Tiles.Count / Seconds;

        public long TotalBytes => Tiles.Sum(t => (long)t.Bytes);

        public long TotalFeatures => Tiles.Sum(t => (long)t.Features);
    }

    public static async Task RunAsync(string[] args)
    {
        int parentZ = Arg(args, "--parent", 12);
        int childZ = Arg(args, "--child", 14);
        int parents = Arg(args, "--parents", 4);
        int rounds = Arg(args, "--rounds", 3);
        int conc = Arg(args, "--conc", 1);

        string conn = Environment.GetEnvironmentVariable("GISBENCH_CONN")
            ?? "Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis;"
             + "Maximum Pool Size=32;Minimum Pool Size=8;No Reset On Close=true";

        await using NpgsqlDataSource ds = new NpgsqlDataSourceBuilder(conn).Build();

        // Istanbul at z12, the densest four tiles in the dataset — 78,723 /
        // 70,303 / 53,013 / 48,965 features, confirmed against the table rather
        // than computed from a latitude. The first run of this benchmark used
        // coordinates worked out by hand, which were 17 tiles off and produced
        // two empty paths and a very fast meaningless result. Both paths agreed
        // perfectly on nothing.
        (int X, int Y)[] dense = [(2376, 1534), (2376, 1535), (2377, 1534), (2377, 1535)];
        List<(int Z, int X, int Y)> roots = [];
        for (int i = 0; i < parents; i++)
        {
            (int x, int y) = dense[i % dense.Length];
            roots.Add((parentZ, x, y));
        }

        // A parent zoom other than 12 has to be re-derived, not scaled from a
        // z12 tile by arithmetic nobody checked.
        if (parentZ != 12)
        {
            roots = [.. roots.Select(r => (
                parentZ,
                parentZ > 12 ? r.X << (parentZ - 12) : r.X >> (12 - parentZ),
                parentZ > 12 ? r.Y << (parentZ - 12) : r.Y >> (12 - parentZ)))];
            roots = [.. roots.Distinct()];
        }

        int perParent = 1 << (2 * (childZ - parentZ));

        Console.WriteLine($"Q-68 — read-once-encode-many against one ST_AsMVT per tile");
        Console.WriteLine($"  parent z{parentZ} × {parents}   child z{childZ}   "
                          + $"{perParent} children per parent   {parents * perParent} tiles per round");
        Console.WriteLine($"  concurrency {conc}   ·   {rounds} interleaved rounds; run 2 established "
                          + "that block-ordered runs on this machine drift 40%.\n");

        List<PathResult> a = [];
        List<PathResult> b = [];

        // Warm both paths before timing anything: the first query pays plan
        // compilation and pool creation, and charging that to whichever path
        // ran first is how a benchmark invents a winner.
        Console.WriteLine("warming…");
        await PathAsMvtAsync(ds, [roots[0]], childZ, conc);
        await PathReadOnceAsync(ds, [roots[0]], childZ, conc);

        // Interleaved, for the reason run 2 had to discover: this machine
        // carries 25-30% unrelated background load, so consecutive blocks
        // measure the background as much as the code.
        for (int round = 1; round <= rounds; round++)
        {
            a.Add(await PathAsMvtAsync(ds, roots, childZ, conc));
            b.Add(await PathReadOnceAsync(ds, roots, childZ, conc));
            Console.WriteLine($"  round {round}: ST_AsMVT {a[^1].TilesPerSecond,7:F1} tiles/s   "
                              + $"read-once {b[^1].TilesPerSecond,7:F1} tiles/s");
        }

        Console.WriteLine();
        Report("ST_AsMVT, one round trip per tile", a);
        Report("read once at the parent, encode every child", b);

        PathResult bestA = a.MaxBy(r => r.TilesPerSecond)!;
        PathResult bestB = b.MaxBy(r => r.TilesPerSecond)!;

        Console.WriteLine("\n---- the comparison Q-68 asked for ----");
        Console.WriteLine($"  round trips        {bestA.Queries,8}  vs {bestB.Queries,8}   "
                          + $"({(double)bestA.Queries / Math.Max(1, bestB.Queries):F0}× fewer)");
        Console.WriteLine($"  tiles/second       {bestA.TilesPerSecond,8:F1}  vs {bestB.TilesPerSecond,8:F1}   "
                          + $"({bestB.TilesPerSecond / bestA.TilesPerSecond:F2}× )");
        Console.WriteLine($"  MB allocated/tile  {bestA.AllocBytes / 1048576.0 / bestA.TileCount,8:F2}  "
                          + $"vs {bestB.AllocBytes / 1048576.0 / bestB.TileCount,8:F2}");
        Console.WriteLine($"  GC pause           {bestA.GcPauseMs,8:F0}  vs {bestB.GcPauseMs,8:F0} ms");

        // The check that decides whether the speed number means anything.
        double byteRatio = (double)bestB.TotalBytes / Math.Max(1, bestA.TotalBytes);
        double featureRatio = (double)bestB.TotalFeatures / Math.Max(1, bestA.TotalFeatures);
        Console.WriteLine($"\n  output: {bestB.TotalFeatures} features vs {bestA.TotalFeatures} "
                          + $"({featureRatio:P1}), {bestB.TotalBytes:N0} bytes vs {bestA.TotalBytes:N0} "
                          + $"({byteRatio:P1})");
        // The guard that would have caught the first run. Two empty paths agree
        // perfectly and produce a spectacular throughput number, and nothing
        // else in the output says the tiles were blank.
        if (bestA.TotalFeatures < bestA.TileCount || bestB.TotalFeatures < bestB.TileCount)
        {
            Console.WriteLine(
                "\n  *** REFUSING TO REPORT: tiles averaged under one feature each. "
                              + "Empty tiles are fast and mean nothing — check the tile coordinates "
                              + "against the data. ***");
            return;
        }

        Console.WriteLine(byteRatio is > 0.85 and < 1.15
            ? "  the two paths agree on what a tile contains, so the timing is comparable."
            : "  *** OUTPUTS DIVERGE — this is a correctness result, not a speed result. ***");
    }

    // ------------------------------------------------------------- path A

    /// <summary>One <c>ST_AsMVT</c> per tile: what the database can do alone.</summary>
    private static async Task<PathResult> PathAsMvtAsync(
        NpgsqlDataSource ds, List<(int Z, int X, int Y)> roots, int childZ, int conc)
    {
        string sql = $"""
            WITH bounds AS (SELECT ST_TileEnvelope(@z,@x,@y) AS geom),
            mvtgeom AS (
                SELECT ST_AsMVTGeom(t.{GeomCol}, bounds.geom,
                                    {TileMath.Extent}, {TileMath.Buffer}, true) AS geom,
                       t.osm_id, COALESCE(t.name,'') AS name
                FROM {Table} t, bounds
                WHERE t.{GeomCol} && bounds.geom
            )
            SELECT ST_AsMVT(mvtgeom.*, 'polygons', {TileMath.Extent}, 'geom') FROM mvtgeom
            """;

        List<TileResult> tiles = [];
        int queries = 0;
        Counters before = Counters.Take();
        Stopwatch clock = Stopwatch.StartNew();

        (int Z, int X, int Y)[] all = [.. roots.SelectMany(r => Children(r, childZ))];
        object gate = new();

        await Parallel.ForEachAsync(
            all,
            new ParallelOptions { MaxDegreeOfParallelism = conc },
            async (tile, token) =>
            {
                await using NpgsqlCommand cmd = ds.CreateCommand(sql);
                cmd.Parameters.AddWithValue("z", tile.Z);
                cmd.Parameters.AddWithValue("x", tile.X);
                cmd.Parameters.AddWithValue("y", tile.Y);

                byte[] bytes = (byte[]?)await cmd.ExecuteScalarAsync(token) ?? [];

                // Feature count is read back out of the tile rather than
                // counted on the way in, so both paths are counted the same way.
                TileResult result = new(tile.Z, tile.X, tile.Y, bytes.Length, CountFeatures(bytes));

                lock (gate)
                {
                    queries++;
                    tiles.Add(result);
                }
            });

        return Counters.Finish("ST_AsMVT", clock, before, queries, tiles);
    }

    // ------------------------------------------------------------- path B

    /// <summary>
    /// One read at the parent, then every child encoded from it in process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pushdown tolerance is the child's, not the parent's.</b> Simplify
    /// at the parent's resolution and every child tile is visibly coarser than
    /// the one <c>ST_AsMVT</c> produces — which would win the benchmark by
    /// shipping worse tiles. This is the mistake the output check at the end
    /// exists to catch.
    /// </para>
    /// <para>
    /// The parent envelope is buffered by a whole child tile's worth of margin,
    /// so a feature that only touches the edge child is still present.
    /// </para>
    /// </remarks>
    private static async Task<PathResult> PathReadOnceAsync(
        NpgsqlDataSource ds, List<(int Z, int X, int Y)> roots, int childZ, int conc)
    {
        List<TileResult> tiles = [];
        int queries = 0;
        Counters before = Counters.Take();
        Stopwatch clock = Stopwatch.StartNew();

        object gate = new();

        await Parallel.ForEachAsync(
            roots,
            new ParallelOptions { MaxDegreeOfParallelism = conc },
            async (root, token) =>
        {
            // A GeometryFactory and a WKB reader are not thread-safe to share,
            // so each parent gets its own. They are cheap next to the read.
            GeometryFactory factory = new(new PrecisionModel(PrecisionModels.Floating), 3857);
            PostGisReader reader = new(factory.CoordinateSequenceFactory, factory.PrecisionModel);

            TileMath.Bounds parent = TileMath.TileEnvelope(root.Z, root.X, root.Y);
            TileMath.Bounds read = TileMath.BufferedEnvelope(parent);

            // A child tile's width in map units, which is both the simplify
            // tolerance and the right amount of margin on the parent read.
            double childWidth = parent.Width / (1 << (childZ - root.Z));
            double tolerance = childWidth / TileMath.Extent;
            read = new TileMath.Bounds(
                read.MinX - tolerance * TileMath.Buffer, read.MinY - tolerance * TileMath.Buffer,
                read.MaxX + tolerance * TileMath.Buffer, read.MaxY + tolerance * TileMath.Buffer);

            // simpclip, which run 3 finding 12 made structural rather than
            // optional: without it a tile reads two orders of magnitude more
            // geometry than it emits.
            string sql = $"""
                SELECT osm_id, COALESCE(name,'') AS name,
                       ST_AsBinary(ST_ClipByBox2D(
                           ST_Simplify({GeomCol}, @tol, true),
                           ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857))) AS g
                FROM {Table}
                WHERE {GeomCol} && ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857)
                """;

            await using NpgsqlCommand cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("tol", tolerance);
            cmd.Parameters.AddWithValue("minx", read.MinX);
            cmd.Parameters.AddWithValue("miny", read.MinY);
            cmd.Parameters.AddWithValue("maxx", read.MaxX);
            cmd.Parameters.AddWithValue("maxy", read.MaxY);

            List<(long Id, string Name, Geometry Geom)> features = new(4096);

            await using (NpgsqlDataReader rdr = await cmd.ExecuteReaderAsync())
            {
                queries++;

                while (await rdr.ReadAsync())
                {
                    if (rdr.IsDBNull(2))
                    {
                        continue;
                    }

                    try
                    {
                        // Parsed once and reused by every child. This is the
                        // whole of the argument being tested: ST_AsMVT reparses
                        // and re-reads for each of the N tiles.
                        features.Add((rdr.GetInt64(0), rdr.GetString(1), reader.Read((byte[])rdr[2])));
                    }
                    catch
                    {
                        // geometry-crs-policy §1: a geometry we cannot read is
                        // skipped rather than allowed to fail the tile.
                    }
                }
            }

            foreach ((int z, int x, int y) in Children(root, childZ))
            {
                TileResult result = EncodeChild(features, z, x, y, factory);

                lock (gate)
                {
                    tiles.Add(result);
                }
            }
        });

        return Counters.Finish("read-once", clock, before, queries, tiles);
    }

    /// <summary>Clips, transforms, simplifies and encodes one child tile.</summary>
    private static TileResult EncodeChild(
        List<(long Id, string Name, Geometry Geom)> features,
        int z, int x, int y, GeometryFactory factory)
    {
        TileMath.Bounds env = TileMath.TileEnvelope(z, x, y);
        TileMath.Bounds buf = TileMath.BufferedEnvelope(env);
        Envelope clipEnv = new(buf.MinX, buf.MaxX, buf.MinY, buf.MaxY);

        MvtEncoder encoder = new("polygons");
        TileSimplify.Stats stats = default;
        int emitted = 0;

        foreach ((long id, string name, Geometry geom) in features)
        {
            // Cheapest possible rejection first. Most features in a parent's
            // extent are outside any given child, and at 256 children that
            // test runs 256 times per feature — so it has to be an envelope
            // comparison and nothing more.
            if (!geom.EnvelopeInternal.Intersects(clipEnv))
            {
                continue;
            }

            try
            {
                RectClip.Result result = RectClip.Clip(geom, clipEnv, factory, out Geometry? clipped);
                if (result == RectClip.Result.Outside || clipped is null || clipped.IsEmpty)
                {
                    continue;
                }

                // Transform before simplify — run 2 finding 7's largest single
                // change. Quantising to the 4096 grid collapses vertices on its
                // own, so simplifying first spends the walk on points the next
                // stage was going to merge anyway.
                Geometry tiled = ToTileSpace(clipped, env, factory);
                Geometry? simplified = TileSimplify.Simplify(tiled, 1.0, factory, ref stats);

                if (simplified is null || simplified.IsEmpty)
                {
                    continue;
                }

                if (encoder.Add(id, simplified, [new KeyValuePair<string, object?>("name", name)]))
                {
                    emitted++;
                }
            }
            catch
            {
                // Same policy as the read: one bad geometry does not fail a tile.
            }
        }

        byte[] bytes = encoder.Finish();
        return new TileResult(z, x, y, bytes.Length, emitted);
    }

    // ------------------------------------------------------------- helpers

    private static IEnumerable<(int Z, int X, int Y)> Children((int Z, int X, int Y) root, int childZ)
    {
        int span = 1 << (childZ - root.Z);
        int x0 = root.X * span;
        int y0 = root.Y * span;

        for (int dy = 0; dy < span; dy++)
        {
            for (int dx = 0; dx < span; dx++)
            {
                yield return (childZ, x0 + dx, y0 + dy);
            }
        }
    }

    /// <summary>
    /// Counts features in an encoded tile by walking the protobuf.
    /// </summary>
    /// <remarks>
    /// Both paths are counted from their output rather than from their input,
    /// because counting one on the way in and the other on the way out is how
    /// two numbers come to disagree for reasons that have nothing to do with
    /// the thing being measured.
    /// </remarks>
    private static int CountFeatures(byte[] tile)
    {
        int count = 0;
        int i = 0;

        while (i < tile.Length)
        {
            if (!TryTag(tile, ref i, out int field, out int wire))
            {
                break;
            }

            if (field == 3 && wire == 2)          // layer
            {
                if (!TryVarint(tile, ref i, out ulong len))
                {
                    break;
                }

                int end = i + (int)len;
                while (i < end && i < tile.Length)
                {
                    if (!TryTag(tile, ref i, out int lf, out int lw))
                    {
                        return count;
                    }

                    if (lf == 2 && lw == 2)       // feature
                    {
                        count++;
                    }

                    if (!Skip(tile, ref i, lw))
                    {
                        return count;
                    }
                }
            }
            else if (!Skip(tile, ref i, wire))
            {
                break;
            }
        }

        return count;
    }

    private static bool TryTag(byte[] b, ref int i, out int field, out int wire)
    {
        field = wire = 0;
        if (!TryVarint(b, ref i, out ulong key))
        {
            return false;
        }

        field = (int)(key >> 3);
        wire = (int)(key & 7);
        return true;
    }

    private static bool TryVarint(byte[] b, ref int i, out ulong value)
    {
        value = 0;
        int shift = 0;

        while (i < b.Length && shift < 64)
        {
            byte x = b[i++];
            value |= (ulong)(x & 0x7F) << shift;

            if ((x & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static bool Skip(byte[] b, ref int i, int wire)
    {
        switch (wire)
        {
            case 0:
                return TryVarint(b, ref i, out _);
            case 1:
                i += 8;
                return i <= b.Length;
            case 2:
                if (!TryVarint(b, ref i, out ulong len))
                {
                    return false;
                }

                i += (int)len;
                return i <= b.Length;
            case 5:
                i += 4;
                return i <= b.Length;
            default:
                return false;
        }
    }

    private static Geometry ToTileSpace(Geometry g, TileMath.Bounds tile, GeometryFactory f) => g switch
    {
        Point p => f.CreatePoint(Tx(p.Coordinate, tile)),
        LinearRing lr => f.CreateLinearRing(TxAll(lr.Coordinates, tile)),
        LineString ls => f.CreateLineString(TxAll(ls.Coordinates, tile)),
        Polygon poly => f.CreatePolygon(
            f.CreateLinearRing(TxAll(poly.ExteriorRing.Coordinates, tile)),
            [.. poly.InteriorRings.Select(r => f.CreateLinearRing(TxAll(r.Coordinates, tile)))]),
        MultiPolygon mp => f.CreateMultiPolygon(
            [.. mp.Geometries.Select(x => (Polygon)ToTileSpace(x, tile, f))]),
        MultiLineString ml => f.CreateMultiLineString(
            [.. ml.Geometries.Select(x => (LineString)ToTileSpace(x, tile, f))]),
        MultiPoint mpt => f.CreateMultiPoint(
            [.. mpt.Geometries.Select(x => (Point)ToTileSpace(x, tile, f))]),
        GeometryCollection gc => f.CreateGeometryCollection(
            [.. gc.Geometries.Select(x => ToTileSpace(x, tile, f))]),
        _ => g,
    };

    private static Coordinate Tx(Coordinate c, TileMath.Bounds t)
    {
        (int x, int y) = TileMath.ToTileSpace(c.X, c.Y, t);
        return new Coordinate(x, y);
    }

    private static Coordinate[] TxAll(Coordinate[] cs, TileMath.Bounds t)
    {
        Coordinate[] outp = new Coordinate[cs.Length];
        for (int i = 0; i < cs.Length; i++)
        {
            outp[i] = Tx(cs[i], t);
        }

        return outp;
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }

    private static void Report(string label, List<PathResult> runs)
    {
        PathResult best = runs.MaxBy(r => r.TilesPerSecond)!;
        double[] rates = [.. runs.Select(r => r.TilesPerSecond).Order()];

        Console.WriteLine($"{label}");
        Console.WriteLine($"    tiles/s   best {best.TilesPerSecond,7:F1}   median {rates[rates.Length / 2],7:F1}");
        Console.WriteLine($"    per tile  {best.Seconds * 1000 / best.TileCount,7:F1} ms   "
                          + $"{best.AllocBytes / 1048576.0 / best.TileCount,6:F2} MB   "
                          + $"{best.TotalBytes / (double)best.TileCount,8:F0} bytes out");
        Console.WriteLine($"    round trips {best.Queries,5}    gen0 {best.Gen0,4}   gen2 {best.Gen2,3}   "
                          + $"GC pause {best.GcPauseMs,6:F0} ms ({100 * best.GcPauseMs / (best.Seconds * 1000),4:F1}%)");
    }

    /// <summary>Process-wide counters, valid only because this runs strictly sequentially.</summary>
    private readonly record struct Counters(long Alloc, int Gen0, int Gen1, int Gen2, TimeSpan Pause)
    {
        public static Counters Take() => new(
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration());

        public static PathResult Finish(
            string name, Stopwatch clock, Counters before, int queries, List<TileResult> tiles)
        {
            clock.Stop();
            Counters after = Take();

            return new PathResult(
                name,
                clock.Elapsed.TotalSeconds,
                after.Alloc - before.Alloc,
                after.Gen0 - before.Gen0,
                after.Gen2 - before.Gen2,
                (after.Pause - before.Pause).TotalMilliseconds,
                queries,
                tiles);
        }
    }
}
