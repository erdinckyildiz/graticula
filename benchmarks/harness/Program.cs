using System.Diagnostics;
using System.Text;
using GisBench;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Simplify;
using Npgsql;

// Benchmark harness for A-019 and the feature-query path.
//
// NOT production code. Phase 0 permits code under /experiments and /benchmarks
// only, to answer a specific architectural question (CLAUDE.md §1). This exists
// to answer:
//
//   A-019  Does in-process MVT encoding meet latency targets?
//   A-021  Does pushdown of filter/clip/simplify work usefully?
//   A-001  Is the tile path CPU-bound in our process at all?
//
// Three endpoints deliberately do the same job three ways so the differences
// are attributable.

// Load-driver mode. Same executable, separate process: the driver's own
// allocations must not be counted against the server's, which is the entire
// point of the A-037 measurement.
if (args.Length > 0 && args[0] == "load")
{
    await LoadGen.RunAsync(args);
    return;
}

// D-30 / performance gate F1: the feature query path under concurrency, against
// the real server over TLS, with a mandatory control run in front of it.
if (args.Length > 0 && args[0] == "queryload")
{
    await QueryLoad.RunAsync(args);
    return;
}

// Q-68: read-once-encode-many against one ST_AsMVT per tile. A console mode
// rather than an endpoint, because the question is seeding throughput over a
// pyramid, and putting HTTP in the middle of it would measure Kestrel.
if (args.Length > 0 && args[0] == "q68")
{
    await Q68.RunAsync(args);
    return;
}

// A-042: where must the GeometryServer cap sit, and is a safe cap useful?
if (args.Length > 0 && args[0] == "a042")
{
    await A042.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();

var connString = Environment.GetEnvironmentVariable("GISBENCH_CONN")
    ?? "Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis;"
     + "Maximum Pool Size=32;Minimum Pool Size=8;No Reset On Close=true";

var dataSource = new NpgsqlDataSourceBuilder(connString).Build();
builder.Services.AddSingleton(dataSource);

var app = builder.Build();

const string Table = "planet_osm_polygon";
const string GeomCol = "way";
var geomFactory = new GeometryFactory(new PrecisionModel(PrecisionModels.Floating), 3857);
var wkbReader = new PostGisReader(geomFactory.CoordinateSequenceFactory, geomFactory.PrecisionModel);

// Server-side counters for A-037. Sampled by the load driver either side of a
// run, so what is reported is a delta over a known wall-clock window rather
// than an instantaneous reading.
app.MapGet("/metrics", () =>
{
    var p = Process.GetCurrentProcess();
    var mi = GC.GetGCMemoryInfo();
    return Results.Text(string.Join('\n', [
        $"alloc_mb={GC.GetTotalAllocatedBytes(precise: false) / 1048576.0:F3}",
        $"gen0={GC.CollectionCount(0)}",
        $"gen1={GC.CollectionCount(1)}",
        $"gen2={GC.CollectionCount(2)}",
        $"gc_pause_ms={GC.GetTotalPauseDuration().TotalMilliseconds:F3}",
        $"cpu_ms={p.TotalProcessorTime.TotalMilliseconds:F3}",
        $"heap_mb={mi.HeapSizeBytes / 1048576.0:F3}",
        $"workingset_mb={p.WorkingSet64 / 1048576.0:F3}",
        $"uptime_ms={Environment.TickCount64}",
        $"cores={Environment.ProcessorCount}",
        $"server_gc={System.Runtime.GCSettings.IsServerGC}",
    ]));
});

app.MapGet("/health", async (NpgsqlDataSource ds) =>
{
    await using var cmd = ds.CreateCommand("select postgis_full_version()");
    return Results.Text((string?)await cmd.ExecuteScalarAsync() ?? "?");
});

// ---------------------------------------------------------------- endpoint A
// Streamed GeoJSON. Tests C6 (streaming) and C8 (driver quality).
// The response is written as rows arrive. Nothing is materialised — that is the
// whole point, and buffering here would invalidate the measurement.
app.MapGet("/collections/polygons/items", async (HttpContext ctx, NpgsqlDataSource ds,
    double? minx, double? miny, double? maxx, double? maxy, int limit = 1000) =>
{
    var sw = Stopwatch.StartNew();
    long firstByteMs = -1, rows = 0;

    var sql = $"""
        SELECT osm_id, COALESCE(name,'') AS name, ST_AsBinary({GeomCol}) AS g
        FROM {Table}
        WHERE {GeomCol} && ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857)
        LIMIT @limit
        """;

    await using var cmd = ds.CreateCommand(sql);
    cmd.Parameters.AddWithValue("minx", minx ?? 3000000);
    cmd.Parameters.AddWithValue("miny", miny ?? 4600000);
    cmd.Parameters.AddWithValue("maxx", maxx ?? 3010000);
    cmd.Parameters.AddWithValue("maxy", maxy ?? 4610000);
    cmd.Parameters.AddWithValue("limit", limit);

    ctx.Response.ContentType = "application/geo+json";
    await using var w = new StreamWriter(ctx.Response.Body, Encoding.UTF8, 64 * 1024);
    await w.WriteAsync("""{"type":"FeatureCollection","features":[""");

    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync())
    {
        if (firstByteMs < 0) firstByteMs = sw.ElapsedMilliseconds;
        if (rows > 0) await w.WriteAsync(',');
        var geom = wkbReader.Read((byte[])rdr[2]);
        await w.WriteAsync($$"""{"type":"Feature","id":{{rdr.GetInt64(0)}},"properties":{"name":{{System.Text.Json.JsonSerializer.Serialize(rdr.GetString(1))}}},"geometry":""");
        WriteGeoJsonGeometry(w, geom);
        await w.WriteAsync('}');
        rows++;
    }
    await w.WriteAsync("]}");
    await w.FlushAsync();

    ctx.Response.Headers["X-Rows"] = rows.ToString();
    ctx.Response.Headers["X-First-Row-Ms"] = firstByteMs.ToString();
    ctx.Response.Headers["X-Total-Ms"] = sw.ElapsedMilliseconds.ToString();
});

// ---------------------------------------------------------------- endpoint B
// MVT via ST_AsMVT. The PostGIS fast path, unavailable on SQL Server and
// Oracle, which is why endpoint C exists at all.
app.MapGet("/tiles/{z:int}/{x:int}/{y:int}.mvt", async (HttpContext ctx, NpgsqlDataSource ds,
    int z, int x, int y) =>
{
    var sw = Stopwatch.StartNew();
    var sql = $"""
        WITH bounds AS (SELECT ST_TileEnvelope(@z,@x,@y) AS geom),
        mvtgeom AS (
            SELECT ST_AsMVTGeom(t.{GeomCol}, bounds.geom, {TileMath.Extent}, {TileMath.Buffer}, true) AS geom,
                   t.osm_id, COALESCE(t.name,'') AS name
            FROM {Table} t, bounds
            WHERE t.{GeomCol} && bounds.geom
        )
        SELECT ST_AsMVT(mvtgeom.*, 'polygons', {TileMath.Extent}, 'geom') FROM mvtgeom
        """;

    await using var cmd = ds.CreateCommand(sql);
    cmd.Parameters.AddWithValue("z", z);
    cmd.Parameters.AddWithValue("x", x);
    cmd.Parameters.AddWithValue("y", y);

    var bytes = (byte[]?)await cmd.ExecuteScalarAsync() ?? [];
    ctx.Response.ContentType = "application/vnd.mapbox-vector-tile";
    ctx.Response.Headers["X-Total-Ms"] = sw.ElapsedMilliseconds.ToString();
    ctx.Response.Headers["X-Bytes"] = bytes.Length.ToString();
    await ctx.Response.Body.WriteAsync(bytes);
});

// ---------------------------------------------------------------- endpoint C
// MVT encoded in-process. THE measurement that matters — this is the only path
// available on SQL Server and Oracle, so A-019 rests on it.
//
// Stages are timed separately, because a single number would not tell us where
// the cost is, and ADR-003's open question is precisely whether our own
// hot-path primitives are worth writing.
app.MapGet("/tiles-local/{z:int}/{x:int}/{y:int}.mvt", async (HttpContext ctx, NpgsqlDataSource ds,
    int z, int x, int y, string? clip, string? simplify, string? push) =>
{
    bool fastClip = clip is "fast";
    // nts    — DouglasPeuckerSimplifier, the default, topology repair on
    // ntsraw — the same, with EnsureValidTopology off. Isolates what the
    //          IsValid/Buffer(0) repair actually costs on tile geometry
    // ours   — TileSimplify, after the transform, on the integer grid
    string simpMode = simplify is "ours" or "ntsraw" ? simplify : "nts";
    // Pushdown (A-021). Added after the concurrency run found that a z16 tile
    // reads 201,580 vertices to emit 2,080: four administrative polygons —
    // Turkiye, Marmara Denizi and two protection zones — overlap every tile in
    // the city and are shipped whole across the wire every time.
    //   none      what we measured so far: whole geometries, clip in process
    //   clip      ST_ClipByBox2D in the database
    //   simpclip  simplify first, then clip, both in the database
    string pushMode = push is "clip" or "simpclip" ? push : "none";
    var total = Stopwatch.StartNew();
    long tQuery = 0, tFetch = 0, tParse = 0, tPrepare = 0, tEncode = 0;
    long tClip = 0, tSimplify = 0, tTransform = 0;
    int fetched = 0, emitted = 0, trivialInside = 0;
    long vIn = 0, vOut = 0;
    var simpStats = default(TileSimplify.Stats);

    var env = TileMath.TileEnvelope(z, x, y);
    var buf = TileMath.BufferedEnvelope(env);
    double tolerance = env.Width / TileMath.Extent; // one tile unit, in map units

    // Allocation and collection accounting. Added after the stage timings stopped
    // adding up: at z12 the measured stages are 132 ms of a 323 ms request, and a
    // claim about where the other 190 ms goes needs evidence like anything else.
    // Process-wide, not GetAllocatedBytesForCurrentThread(): this handler is
    // async and resumes on a different pool thread after each await, so the
    // per-thread counter subtracts two unrelated threads and can go negative.
    // It did, which is how the mistake was caught. Process-wide is correct here
    // only because the benchmark is strictly sequential.
    long alloc0 = GC.GetTotalAllocatedBytes(precise: false);
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    var pause0 = GC.GetTotalPauseDuration();

    // ST_ClipByBox2D can return invalid geometry, which PostGIS documents. For a
    // tile that is the same trade our own clipper makes and the same trade
    // ST_AsMVTGeom makes. It would be unacceptable on the analytical path.
    string geomExpr = pushMode switch
    {
        "clip"     => $"ST_ClipByBox2D({GeomCol}, ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857))",
        "simpclip" => $"ST_ClipByBox2D(ST_Simplify({GeomCol}, @tol, true), ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857))",
        _          => GeomCol,
    };

    var sql = $"""
        SELECT osm_id, COALESCE(name,'') AS name, ST_AsBinary({geomExpr}) AS g
        FROM {Table}
        WHERE {GeomCol} && ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857)
        """;

    await using var cmd = ds.CreateCommand(sql);
    if (pushMode == "simpclip") cmd.Parameters.AddWithValue("tol", tolerance);
    cmd.Parameters.AddWithValue("minx", buf.MinX);
    cmd.Parameters.AddWithValue("miny", buf.MinY);
    cmd.Parameters.AddWithValue("maxx", buf.MaxX);
    cmd.Parameters.AddWithValue("maxy", buf.MaxY);

    var sw = Stopwatch.StartNew();
    await using var rdr = await cmd.ExecuteReaderAsync();
    tQuery = sw.ElapsedMicroseconds();

    var raw = new List<(long Id, string Name, byte[] Wkb)>(512);
    sw.Restart();
    while (await rdr.ReadAsync())
    {
        if (rdr.IsDBNull(2)) continue;   // ST_ClipByBox2D can return NULL
        raw.Add((rdr.GetInt64(0), rdr.GetString(1), (byte[])rdr[2]));
    }
    tFetch = sw.ElapsedMicroseconds();
    fetched = raw.Count;

    var clipEnv = new Envelope(buf.MinX, buf.MaxX, buf.MinY, buf.MaxY);
    var clipBox = geomFactory.ToGeometry(clipEnv);
    var encoder = new MvtEncoder("polygons");

    foreach (var (id, name, wkb) in raw)
    {
        sw.Restart();
        Geometry geom;
        try { geom = wkbReader.Read(wkb); }
        catch { continue; }              // defensive: geometry-crs-policy §1
        tParse += sw.ElapsedMicroseconds();

        // Prepare is three distinct operations and the first measurement
        // showed it dominating at 92%. Timing them separately is the difference
        // between "geometry preparation is slow" and knowing which primitive to
        // write ourselves (ADR-003 Alternative B).
        var swp = Stopwatch.StartNew();
        Geometry prepared;
        try
        {
            swp.Restart();
            Geometry clipped;
            if (fastClip)
            {
                var r = RectClip.Clip(geom, clipEnv, geomFactory, out var cg);
                if (r == RectClip.Result.Outside || cg is null) { tClip += swp.ElapsedMicroseconds(); continue; }
                if (r == RectClip.Result.Inside) trivialInside++;
                clipped = cg;
            }
            else
            {
                clipped = geom.Intersection(clipBox);
            }
            tClip += swp.ElapsedMicroseconds();
            if (clipped.IsEmpty) continue;

            if (simpMode == "ours")
            {
                // Transform first, then simplify. Quantisation to the 4096 grid
                // collapses vertices on its own, so doing it first means DP
                // never sees the points the next stage would have merged.
                swp.Restart();
                var tiled = ToTileSpace(clipped, env, geomFactory);
                tTransform += swp.ElapsedMicroseconds();

                swp.Restart();
                var simplified = TileSimplify.Simplify(tiled, 1.0, geomFactory, ref simpStats);
                tSimplify += swp.ElapsedMicroseconds();
                if (simplified is null || simplified.IsEmpty) continue;
                prepared = simplified;
            }
            else
            {
                swp.Restart();
                Geometry simplified;
                if (simpMode == "ntsraw")
                {
                    var dp = new DouglasPeuckerSimplifier(clipped) { EnsureValidTopology = false };
                    dp.DistanceTolerance = tolerance;
                    simplified = dp.GetResultGeometry();
                }
                else
                {
                    simplified = DouglasPeuckerSimplifier.Simplify(clipped, tolerance);
                }
                tSimplify += swp.ElapsedMicroseconds();
                if (simplified.IsEmpty) continue;

                swp.Restart();
                prepared = ToTileSpace(simplified, env, geomFactory);
                tTransform += swp.ElapsedMicroseconds();
            }
        }
        catch { continue; }
        tPrepare = tClip + tSimplify + tTransform;

        // Vertex accounting sits outside the stage timers: it is reporting, not
        // pipeline work, and paying for it inside the measurement would be the
        // observer changing what it observes.
        vIn += geom.NumPoints;
        vOut += prepared.NumPoints;

        sw.Restart();
        if (encoder.Add(id, prepared, [new("name", name)])) emitted++;
        tEncode += sw.ElapsedMicroseconds();
    }

    sw.Restart();
    var bytes = encoder.Finish();
    tEncode += sw.ElapsedMicroseconds();

    var h = ctx.Response.Headers;
    h["X-Total-Ms"] = total.ElapsedMilliseconds.ToString();
    h["X-Alloc-MB"] = ((GC.GetTotalAllocatedBytes(precise: false) - alloc0) / 1048576.0).ToString("F1");
    h["X-Gc"] = $"{GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}";
    h["X-Gc-Pause-Ms"] = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds.ToString("F1");
    h["X-Fetched"] = fetched.ToString();
    h["X-Emitted"] = emitted.ToString();
    h["X-Bytes"] = bytes.Length.ToString();
    h["X-Us-Query"] = tQuery.ToString();
    h["X-Us-Fetch"] = tFetch.ToString();
    h["X-Us-Parse"] = tParse.ToString();
    h["X-Us-Prepare"] = tPrepare.ToString();
    h["X-Us-Clip"] = tClip.ToString();
    h["X-Clip-Mode"] = fastClip ? "fast" : "nts";
    h["X-Trivial-Inside"] = trivialInside.ToString();
    h["X-Us-Simplify"] = tSimplify.ToString();
    h["X-Simplify-Mode"] = simpMode;
    h["X-Push-Mode"] = pushMode;
    h["X-Vertices-In"] = vIn.ToString();
    h["X-Vertices-Out"] = vOut.ToString();
    h["X-Rings-Dropped"] = simpStats.RingsDropped.ToString();
    h["X-Us-Transform"] = tTransform.ToString();
    h["X-Us-Encode"] = tEncode.ToString();
    ctx.Response.ContentType = "application/vnd.mapbox-vector-tile";
    await ctx.Response.Body.WriteAsync(bytes);
});

app.Run("http://0.0.0.0:5080");

// Map-space geometry to integer tile space. Ours, not NTS's — this is the
// quantisation step and it is Tier 1.
//
// Bounds is passed by value rather than by `in`: a lambda cannot capture a ref
// parameter, and the struct is four doubles.
static Geometry ToTileSpace(Geometry g, TileMath.Bounds tile, GeometryFactory f)
{
    switch (g)
    {
        case Point p:
            return f.CreatePoint(Tx(p.Coordinate, tile));
        case LinearRing lr:
            return f.CreateLinearRing(TxAll(lr.Coordinates, tile));
        case LineString ls:
            return f.CreateLineString(TxAll(ls.Coordinates, tile));
        case Polygon poly:
            return f.CreatePolygon(
                f.CreateLinearRing(TxAll(poly.ExteriorRing.Coordinates, tile)),
                poly.InteriorRings.Select(r => f.CreateLinearRing(TxAll(r.Coordinates, tile))).ToArray());
        case MultiPolygon mp:
            return f.CreateMultiPolygon(mp.Geometries.Select(x => (Polygon)ToTileSpace(x, tile, f)).ToArray());
        case MultiLineString ml:
            return f.CreateMultiLineString(ml.Geometries.Select(x => (LineString)ToTileSpace(x, tile, f)).ToArray());
        case MultiPoint mpt:
            return f.CreateMultiPoint(mpt.Geometries.Select(x => (Point)ToTileSpace(x, tile, f)).ToArray());
        case GeometryCollection gc:
            return f.CreateGeometryCollection(gc.Geometries.Select(x => ToTileSpace(x, tile, f)).ToArray());
        default:
            return g;
    }

    static Coordinate Tx(Coordinate c, TileMath.Bounds t)
    {
        var (x, y) = TileMath.ToTileSpace(c.X, c.Y, t);
        return new Coordinate(x, y);
    }

    static Coordinate[] TxAll(Coordinate[] cs, TileMath.Bounds t)
    {
        var outp = new Coordinate[cs.Length];
        for (int i = 0; i < cs.Length; i++) outp[i] = Tx(cs[i], t);
        return outp;
    }
}

// Minimal GeoJSON geometry writer. Written rather than adopted for the same
// reason as the MVT encoder: serialisation is Tier 1, and a library here would
// measure the library.
static void WriteGeoJsonGeometry(TextWriter w, Geometry g)
{
    switch (g)
    {
        case Point p:
            w.Write("{\"type\":\"Point\",\"coordinates\":");
            WriteCoord(w, p.Coordinate);
            w.Write('}');
            break;
        case LineString ls:
            w.Write("{\"type\":\"LineString\",\"coordinates\":");
            WriteCoords(w, ls.Coordinates);
            w.Write('}');
            break;
        case Polygon poly:
            w.Write("{\"type\":\"Polygon\",\"coordinates\":[");
            WriteCoords(w, poly.ExteriorRing.Coordinates);
            foreach (var r in poly.InteriorRings) { w.Write(','); WriteCoords(w, r.Coordinates); }
            w.Write("]}");
            break;
        case MultiPolygon mp:
            w.Write("{\"type\":\"MultiPolygon\",\"coordinates\":[");
            for (int i = 0; i < mp.NumGeometries; i++)
            {
                if (i > 0) w.Write(',');
                var poly = (Polygon)mp.GetGeometryN(i);
                w.Write('[');
                WriteCoords(w, poly.ExteriorRing.Coordinates);
                foreach (var r in poly.InteriorRings) { w.Write(','); WriteCoords(w, r.Coordinates); }
                w.Write(']');
            }
            w.Write("]}");
            break;
        case MultiLineString ml:
            w.Write("{\"type\":\"MultiLineString\",\"coordinates\":[");
            for (int i = 0; i < ml.NumGeometries; i++)
            {
                if (i > 0) w.Write(',');
                WriteCoords(w, ml.GetGeometryN(i).Coordinates);
            }
            w.Write("]}");
            break;
        default:
            w.Write("null");
            break;
    }

    static void WriteCoord(TextWriter w, Coordinate c)
    {
        w.Write('[');
        w.Write(c.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        w.Write(',');
        w.Write(c.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        w.Write(']');
    }

    static void WriteCoords(TextWriter w, Coordinate[] cs)
    {
        w.Write('[');
        for (int i = 0; i < cs.Length; i++) { if (i > 0) w.Write(','); WriteCoord(w, cs[i]); }
        w.Write(']');
    }
}

internal static class SwExt
{
    public static long ElapsedMicroseconds(this Stopwatch sw)
        => sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
}
