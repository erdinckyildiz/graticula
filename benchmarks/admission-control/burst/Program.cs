// Burst probe for ADR-046 §5a's StoreGate: C HTTP/2 clients (one connection each, as a browser has), each firing
// bursts of N concurrent requests, R rounds. Prints status counts and latency per scenario. Status codes and
// timings only; no bodies and no credentials are printed.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var a = Args(args);
string url = a.GetValueOrDefault("url", "https://127.0.0.1:18572").TrimEnd('/');
string scenario = a.GetValueOrDefault("scenario", "tile-cached");
int burst = int.Parse(a.GetValueOrDefault("burst", "64"));
int clients = int.Parse(a.GetValueOrDefault("clients", "1"));
int rounds = int.Parse(a.GetValueOrDefault("rounds", "5"));
int gapMs = int.Parse(a.GetValueOrDefault("gap", "1000"));
string service = a.GetValueOrDefault("service", "hosted/ci_many");
bool anon = a.GetValueOrDefault("auth", "token") == "anon";
bool h1 = a.GetValueOrDefault("proto", "h2") == "h1";
string? outFile = a.GetValueOrDefault("out");
string label = a.GetValueOrDefault("label", $"{scenario}-{burst}x{clients}");

string? token = null;
if (!anon)
{
    using var h = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
    var body = JsonSerializer.Serialize(new { name = Environment.GetEnvironmentVariable("BURST_USER"), password = Environment.GetEnvironmentVariable("BURST_PASSWORD") });
    using var r = await h.PostAsync($"{url}/rest/auth/login", new StringContent(body, Encoding.UTF8, "application/json"));
    if (!r.IsSuccessStatusCode) { Console.WriteLine($"login {(int)r.StatusCode}"); return 2; }
    token = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
}

// ci_many's extent, EPSG:3857.
double xmin = double.Parse(a.GetValueOrDefault("xmin", "3657800")), ymin = double.Parse(a.GetValueOrDefault("ymin", "4862900"));
double xmax = double.Parse(a.GetValueOrDefault("xmax", "3665120")), ymax = double.Parse(a.GetValueOrDefault("ymax", "4869920"));
const double Half = 20037508.342789244;

List<(int z, int y, int x)> TilesAt(int z)
{
    double size = 2 * Half / (1 << z);
    int x0 = (int)Math.Floor((xmin + Half) / size), x1 = (int)Math.Floor((xmax + Half) / size);
    int y0 = (int)Math.Floor((Half - ymax) / size), y1 = (int)Math.Floor((Half - ymin) / size);
    var list = new List<(int, int, int)>();
    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) list.Add((z, y, x));
    return list;
}

var cachedSet = TilesAt(15).Concat(TilesAt(16)).Concat(TilesAt(17)).ToList();
// Uncached supply: z19/z20 addresses, offset by a seed so separate runs do not reuse each other's tiles.
int seed = int.Parse(a.GetValueOrDefault("seed", "0"));
var uncachedSet = TilesAt(int.Parse(a.GetValueOrDefault("uz", "19"))).ToList();
var rnd = new Random(seed);
uncachedSet = uncachedSet.OrderBy(_ => rnd.Next()).ToList();
int uncachedNext = int.Parse(a.GetValueOrDefault("uoff", "0"));
int cachedNext = 0;
var qr = new Random(seed + 1);

string Tile((int z, int y, int x) t) => $"{url}/rest/services/{service}/VectorTileServer/tile/{t.z}/{t.y}/{t.x}.pbf";

string Env(double w)
{
    double cx, cy;
    lock (qr) { cx = xmin + qr.NextDouble() * (xmax - xmin - w); cy = ymin + qr.NextDouble() * (ymax - ymin - w); }
    return FormattableString.Invariant($"{cx:0},{cy:0},{cx + w:0},{cy + w:0}");
}

string Next(string kind)
{
    switch (kind)
    {
        case "tile-cached": return Tile(cachedSet[Interlocked.Increment(ref cachedNext) % cachedSet.Count]);
        case "tile-uncached":
            int i = Interlocked.Increment(ref uncachedNext);
            if (i >= uncachedSet.Count) throw new InvalidOperationException("uncached supply exhausted");
            return Tile(uncachedSet[i]);
        case "export": return $"{url}/rest/services/{service}/MapServer/export?bbox={Env(800)}&bboxSR=3857&imageSR=3857&size=256,256&format=png32&transparent=true&f=image";
        case "query": return $"{url}/rest/services/{service}/FeatureServer/0/query?geometry={Env(600)}&geometryType=esriGeometryEnvelope&inSR=3857&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=true&outSR=3857&resultRecordCount=200&f=json";
        case "info": return $"{url}/rest/services/{service}/VectorTileServer?f=json";
        default: throw new ArgumentException(kind);
    }
}

string Pick(int k) => scenario switch
{
    // A viewer pan over a vector-tile basemap with a feature layer on top: mostly tiles it has seen, some new, a few queries.
    "viewer" => (k % 10) switch { < 6 => "tile-cached", < 9 => "tile-uncached", _ => "query" },
    _ => scenario,
};

HttpClient NewClient()
{
    var handler = new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        EnableMultipleHttp2Connections = false,
        MaxConnectionsPerServer = h1 ? 6 : int.MaxValue,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    };
    var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    c.DefaultRequestVersion = h1 ? HttpVersion.Version11 : HttpVersion.Version20;
    c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
    if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return c;
}

var cacheSeen = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
var http = Enumerable.Range(0, clients).Select(_ => NewClient()).ToArray();

async Task<(int status, double ms, string kind, Version v)> One(HttpClient c, string kind)
{
    string u = Next(kind);
    var sw = Stopwatch.StartNew();
    try
    {
        using var resp = await c.GetAsync(u, HttpCompletionOption.ResponseContentRead);
        string hc = resp.Headers.TryGetValues("X-Tile-Cache", out var hv) ? hv.First() : "-";
        cacheSeen.AddOrUpdate($"{kind}:{(int)resp.StatusCode}:{hc}", 1, (_, n) => n + 1);
        return ((int)resp.StatusCode, sw.Elapsed.TotalMilliseconds, kind, resp.Version);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return (-1, sw.Elapsed.TotalMilliseconds, kind, HttpVersion.Unknown);
    }
}

// Warm: one connection per client, and every cached-set tile requested once so "cached" is cached.
foreach (var c in http) await One(c, "info");
if (scenario is "tile-cached" or "viewer" && a.GetValueOrDefault("warm", "1") == "1")
{
    var sem = new SemaphoreSlim(8);
    var hits = new List<(int z, int y, int x)>();
    await Task.WhenAll(cachedSet.Select(async t =>
    {
        await sem.WaitAsync();
        try
        {
            using var r1 = await http[0].GetAsync(Tile(t));
            using var r2 = await http[0].GetAsync(Tile(t));
            if ((int)r2.StatusCode == 200 && r2.Headers.TryGetValues("X-Tile-Cache", out var v) && v.First() == "HIT") lock (hits) hits.Add(t);
        }
        finally { sem.Release(); }
    }));
    cachedSet = hits.OrderBy(t => t).ToList();
    Console.WriteLine($"    cached set: {cachedSet.Count} tiles answering 200 HIT");
}

var all = new List<(int status, double ms, string kind, Version v, int round)>();
var burstWall = new List<double>();
for (int round = 0; round < rounds; round++)
{
    var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = new List<Task<(int, double, string, Version)>>();
    for (int ci = 0; ci < clients; ci++)
    {
        var c = http[ci];
        for (int k = 0; k < burst; k++)
        {
            string kind = Pick(k);
            tasks.Add(Task.Run(async () => { await go.Task; return await One(c, kind); }));
        }
    }
    await Task.Delay(50);
    var wall = Stopwatch.StartNew();
    go.SetResult();
    var res = await Task.WhenAll(tasks);
    burstWall.Add(wall.Elapsed.TotalMilliseconds);
    foreach (var r in res) all.Add((r.Item1, r.Item2, r.Item3, r.Item4, round));
    await Task.Delay(gapMs);
}

double P(IEnumerable<double> xs, double p)
{
    var s = xs.OrderBy(x => x).ToArray();
    if (s.Length == 0) return -1;
    return s[Math.Min(s.Length - 1, (int)Math.Ceiling(p * s.Length) - 1)];
}

int total = all.Count;
int ok = all.Count(r => r.status is 200 or 204);
int refused = all.Count(r => r.status == 503);
int other = total - ok - refused;
var okMs = all.Where(r => r.status is 200 or 204).Select(r => r.ms).ToList();
var refusedMs = all.Where(r => r.status == 503).Select(r => r.ms).ToList();
int roundsWithRefusal = all.Where(r => r.status == 503).Select(r => r.round).Distinct().Count();
string versions = string.Join("/", all.Select(r => r.v.ToString()).Distinct());
string otherCodes = string.Join(",", all.Where(r => r.status is not 200 and not 204 and not 503).GroupBy(r => r.status).Select(g => $"{g.Key}x{g.Count()}"));
var line = FormattableString.Invariant(
    $"{label,-28} req {total,6}  200 {ok,6}  503 {refused,5} ({100.0 * refused / total,5:0.0}%) other {other} [{otherCodes}]  rounds-refused {roundsWithRefusal}/{rounds}  ok p50 {P(okMs, .5),7:0.0} p95 {P(okMs, .95),7:0.0} max {P(okMs, 1),7:0.0} ms  503 p50 {P(refusedMs, .5),6:0.0}  burst wall p50 {P(burstWall, .5),7:0.0} ms  http {versions}");
Console.WriteLine(line);
var perKind = all.GroupBy(r => r.kind).Select(g => FormattableString.Invariant(
    $"    {g.Key,-14} n {g.Count(),5} 200 {g.Count(r => r.status is 200 or 204),5} 503 {g.Count(r => r.status == 503),5} ok p50 {P(g.Where(r => r.status is 200 or 204).Select(r => r.ms), .5),7:0.0} p95 {P(g.Where(r => r.status is 200 or 204).Select(r => r.ms), .95),7:0.0}"));
foreach (var l in perKind) Console.WriteLine(l);
Console.WriteLine("    seen: " + string.Join(" ", cacheSeen.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
if (outFile is not null)
{
    File.AppendAllText(outFile, JsonSerializer.Serialize(new
    {
        label, scenario, burst, clients, rounds, total, ok, refused, other, roundsWithRefusal,
        okP50 = P(okMs, .5), okP95 = P(okMs, .95), okMax = P(okMs, 1), refusedP50 = P(refusedMs, .5),
        wallP50 = P(burstWall, .5), versions,
        kinds = all.GroupBy(r => r.kind).ToDictionary(g => g.Key, g => new { n = g.Count(), ok = g.Count(r => r.status is 200 or 204), refused = g.Count(r => r.status == 503), p50 = P(g.Where(r => r.status is 200 or 204).Select(r => r.ms), .5) }),
    }) + "\n");
}
return 0;

static Dictionary<string, string> Args(string[] args)
{
    var d = new Dictionary<string, string>();
    foreach (var s in args)
    {
        var i = s.IndexOf('=');
        if (s.StartsWith("--") && i > 2) d[s[2..i]] = s[(i + 1)..];
    }
    return d;
}
