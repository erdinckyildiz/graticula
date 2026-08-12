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
    int z, int x, int y, string? clip) =>
{
    bool fastClip = clip is "fast";
    var total = Stopwatch.StartNew();
    long tQuery = 0, tFetch = 0, tParse = 0, tPrepare = 0, tEncode = 0;
    long tClip = 0, tSimplify = 0, tTransform = 0;
    int fetched = 0, emitted = 0, trivialInside = 0;

    var env = TileMath.TileEnvelope(z, x, y);
    var buf = TileMath.BufferedEnvelope(env);
    double tolerance = env.Width / TileMath.Extent; // one tile unit, in map units

    var sql = $"""
        SELECT osm_id, COALESCE(name,'') AS name, ST_AsBinary({GeomCol}) AS g
        FROM {Table}
        WHERE {GeomCol} && ST_MakeEnvelope(@minx,@miny,@maxx,@maxy,3857)
        """;

    await using var cmd = ds.CreateCommand(sql);
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
        raw.Add((rdr.GetInt64(0), rdr.GetString(1), (byte[])rdr[2]));
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

            swp.Restart();
            var simplified = DouglasPeuckerSimplifier.Simplify(clipped, tolerance);
            tSimplify += swp.ElapsedMicroseconds();
            if (simplified.IsEmpty) continue;

            swp.Restart();
            prepared = ToTileSpace(simplified, env, geomFactory);
            tTransform += swp.ElapsedMicroseconds();
        }
        catch { continue; }
        tPrepare = tClip + tSimplify + tTransform;

        sw.Restart();
        if (encoder.Add(id, prepared, [new("name", name)])) emitted++;
        tEncode += sw.ElapsedMicroseconds();
    }

    sw.Restart();
    var bytes = encoder.Finish();
    tEncode += sw.ElapsedMicroseconds();

    var h = ctx.Response.Headers;
    h["X-Total-Ms"] = total.ElapsedMilliseconds.ToString();
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
