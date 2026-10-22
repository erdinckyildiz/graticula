// ADR-012 condition 1: where one node saturates — Graticula's process or PostgreSQL's — on each kind of work.
// usage: satload <baseUrl> <pointsFile> <workload> <seconds> <levels,comma> [affinityMask]
using System.Diagnostics;
using System.Globalization;

string baseUrl = args[0].TrimEnd('/');
string[] points = File.ReadAllLines(args[1]).Where(l => l.Contains(',')).ToArray();
string workload = args[2];
int seconds = int.Parse(args[3], CultureInfo.InvariantCulture);
int[] levels = args[4].Split(',').Select(int.Parse).ToArray();
if (args.Length > 5)
{
    Process.GetCurrentProcess().ProcessorAffinity = (nint)Convert.ToInt64(args[5], 16);
}

const double R = 20037508.342789244;
string Svc = baseUrl + "/rest/services/hosted/bench_hosted";

(double X, double Y) Point(Random r)
{
    string[] p = points[r.Next(points.Length)].Split(',');
    return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
}

string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

string Url(string kind, Random r)
{
    (double x, double y) = Point(r);
    switch (kind)
    {
        case "query":
            return $"{Svc}/FeatureServer/0/query?where=1%3D1&geometry={F(x - 250)},{F(y - 250)},{F(x + 250)},{F(y + 250)}"
                + "&geometryType=esriGeometryEnvelope&inSR=3857&outFields=*&resultRecordCount=200&f=json";
        case "tile":
            int z = r.Next(15, 19);
            double n = Math.Pow(2, z);
            long tx = (long)((x + R) / (2 * R) * n) + r.Next(-3, 4);
            long ty = (long)((R - y) / (2 * R) * n) + r.Next(-3, 4);
            return $"{Svc}/VectorTileServer/tile/{z}/{ty}/{tx}.pbf";
        case "export":
            double h = 300 + r.NextDouble() * 1700;
            return $"{Svc}/MapServer/export?bbox={F(x - h)},{F(y - h)},{F(x + h)},{F(y + h)}&bboxSR=3857&size=512,512&format=png&f=image";
        case "control":
            return baseUrl + "/healthz/live";
        case "mix":
            string[] kinds = ["query", "query", "tile", "tile", "tile", "export"];
            return Url(kinds[r.Next(kinds.Length)], r);
        default:
            throw new ArgumentException(kind);
    }
}

double CpuSeconds(string name)
{
    double total = 0;
    foreach (Process p in Process.GetProcessesByName(name))
    {
        try { total += p.TotalProcessorTime.TotalSeconds; } catch { }
        p.Dispose();
    }
    return total;
}

// PostgreSQL backends come and go; the ones that end during a level take their CPU with them, so the sum is
// sampled every half second and only its increases are counted.
double pgCounted = 0;
double pgLast = CpuSeconds("postgres");
using var sampler = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
var stopSampling = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (await sampler.WaitForNextTickAsync(stopSampling.Token).ConfigureAwait(false))
    {
        double now = CpuSeconds("postgres");
        lock (sampler) { if (now > pgLast) pgCounted += now - pgLast; pgLast = now; }
    }
});

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 1024,
    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

Console.WriteLine($"=== {workload}, {seconds}s per level");
Console.WriteLine("  conc |  req/s |  p50 ms |  p95 ms | errors | non-200 | MB/s | node cores | postgres cores");

foreach (int conc in levels)
{
    // Warm-up, not counted.
    await Drive(conc, TimeSpan.FromSeconds(3), null);

    var latencies = new List<double>(100_000);
    double nodeBefore = CpuSeconds("Graticula.Host");
    double pgBefore; lock (sampler) { pgBefore = pgCounted; }
    var wall = Stopwatch.StartNew();
    (int errors, int other, long bytes) = await Drive(conc, TimeSpan.FromSeconds(seconds), latencies);
    double elapsed = wall.Elapsed.TotalSeconds;
    double node = (CpuSeconds("Graticula.Host") - nodeBefore) / elapsed;
    double pg; lock (sampler) { pg = (pgCounted - pgBefore) / elapsed; }
    latencies.Sort();
    double P(double q) => latencies.Count == 0 ? 0 : latencies[(int)Math.Min(latencies.Count - 1, q * latencies.Count)];
    Console.WriteLine($"  {conc,4} | {latencies.Count / elapsed,6:0} | {P(0.5),7:0.0} | {P(0.95),7:0.0} | {errors,6} | {other,7} | {bytes / elapsed / 1048576,4:0.0} | {node,10:0.00} | {pg,14:0.00}");
}

stopSampling.Cancel();

async Task<(int, int, long)> Drive(int conc, TimeSpan length, List<double>? latencies)
{
    int errors = 0, other = 0;
    long bytes = 0;
    var until = Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, conc).Select(async i =>
    {
        var r = new Random(Environment.TickCount ^ (i * 7919));
        var local = new List<double>();
        while (until.Elapsed < length)
        {
            var t = Stopwatch.StartNew();
            try
            {
                using HttpResponseMessage m = await http.GetAsync(Url(workload, r), HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
                byte[] body = await m.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                Interlocked.Add(ref bytes, body.Length);
                if ((int)m.StatusCode is not (200 or 204)) Interlocked.Increment(ref other);
                local.Add(t.Elapsed.TotalMilliseconds);
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
        }
        if (latencies is not null) lock (latencies) latencies.AddRange(local);
    }).ToArray();
    await Task.WhenAll(tasks).ConfigureAwait(false);
    return (errors, other, bytes);
}
