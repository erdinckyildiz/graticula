// ADR-012 condition 1: where one node saturates — Graticula's process or PostgreSQL's — on each kind of work.
// usage: satload <baseUrl[,baseUrl2…]> <pointsFile> <workload> <seconds> <levels,comma> [affinityMask]
//
// Environment, all optional (added 2026-10-31 for the re-measurement and the two-node prototype):
//   SATLOAD_SERVICE   the service path under /rest/services (default hosted/bench_hosted)
//   SATLOAD_NODE_PATH counts only Graticula.Host processes whose executable path contains this text, so another
//                     fixture's server on the same machine is not counted as this node's CPU
//   SATLOAD_PG_PID    counts only this postmaster's children as PostgreSQL, so another cluster on the machine is
//                     not counted; with it the rig also reports the peak number of backends (= connections)
// Several base URLs are taken in turn per request: the client is the load balancer, round robin.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

string[] bases = args[0].Split(',').Select(b => b.TrimEnd('/')).ToArray();
string[] points = File.ReadAllLines(args[1]).Where(l => l.Contains(',')).ToArray();
string workload = args[2];
int seconds = int.Parse(args[3], CultureInfo.InvariantCulture);
int[] levels = args[4].Split(',').Select(int.Parse).ToArray();
if (args.Length > 5)
{
    Process.GetCurrentProcess().ProcessorAffinity = (nint)Convert.ToInt64(args[5], 16);
}

string service = Environment.GetEnvironmentVariable("SATLOAD_SERVICE") ?? "hosted/bench_hosted";
string? nodePath = Environment.GetEnvironmentVariable("SATLOAD_NODE_PATH");
int pgPid = int.TryParse(Environment.GetEnvironmentVariable("SATLOAD_PG_PID"), out int pp) ? pp : 0;
long turn = 0;

const double R = 20037508.342789244;

(double X, double Y) Point(Random r)
{
    string[] p = points[r.Next(points.Length)].Split(',');
    return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
}

string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

string Url(string kind, Random r)
{
    string baseUrl = bases[(int)(Interlocked.Increment(ref turn) % bases.Length)];
    string svc = baseUrl + "/rest/services/" + service;
    (double x, double y) = Point(r);
    switch (kind)
    {
        case "query":
            return $"{svc}/FeatureServer/0/query?where=1%3D1&geometry={F(x - 250)},{F(y - 250)},{F(x + 250)},{F(y + 250)}"
                + "&geometryType=esriGeometryEnvelope&inSR=3857&outFields=*&resultRecordCount=200&f=json";
        case "tile":
            int z = r.Next(15, 19);
            double n = Math.Pow(2, z);
            long tx = (long)((x + R) / (2 * R) * n) + r.Next(-3, 4);
            long ty = (long)((R - y) / (2 * R) * n) + r.Next(-3, 4);
            return $"{svc}/VectorTileServer/tile/{z}/{ty}/{tx}.pbf";
        case "export":
            double h = 300 + r.NextDouble() * 1700;
            return $"{svc}/MapServer/export?bbox={F(x - h)},{F(y - h)},{F(x + h)},{F(y + h)}&bboxSR=3857&size=512,512&format=png&f=image";
        case "control":
            return baseUrl + "/healthz/live";
        case "mix":
            string[] kinds = ["query", "query", "tile", "tile", "tile", "export"];
            return Url(kinds[r.Next(kinds.Length)], r);
        default:
            throw new ArgumentException(kind);
    }
}

double NodeCpuSeconds()
{
    double total = 0;
    foreach (Process p in Process.GetProcessesByName("Graticula.Host"))
    {
        try
        {
            if (nodePath is null || (p.MainModule?.FileName ?? "").Contains(nodePath, StringComparison.OrdinalIgnoreCase))
            {
                total += p.TotalProcessorTime.TotalSeconds;
            }
        }
        catch { }
        p.Dispose();
    }
    return total;
}

HashSet<int> PostgresPids()
{
    var set = new HashSet<int>();
    if (pgPid == 0)
    {
        foreach (Process p in Process.GetProcessesByName("postgres")) { set.Add(p.Id); p.Dispose(); }
        return set;
    }
    set.Add(pgPid);
    foreach ((int pid, int parent) in Toolhelp.Processes())
    {
        if (parent == pgPid) set.Add(pid);
    }
    return set;
}

(double Cpu, int Backends) PostgresSample()
{
    double total = 0;
    int count = 0;
    foreach (int pid in PostgresPids())
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            total += p.TotalProcessorTime.TotalSeconds;
            count++;
        }
        catch { }
    }
    // A postmaster's children include its own helpers (checkpointer, writer, WAL writer, autovacuum launcher,
    // logical replication launcher, …); those are subtracted with the idle baseline taken at start.
    return (total, count);
}

// PostgreSQL backends come and go; the ones that end during a level take their CPU with them, so the sum is
// sampled every half second and only its increases are counted.
double pgCounted = 0;
(double pgLast, int idleProcesses) = PostgresSample();
int peakProcesses = 0;
using var sampler = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
var stopSampling = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (await sampler.WaitForNextTickAsync(stopSampling.Token).ConfigureAwait(false))
    {
        (double now, int n) = PostgresSample();
        lock (sampler)
        {
            if (now > pgLast) pgCounted += now - pgLast;
            pgLast = now;
            if (n > peakProcesses) peakProcesses = n;
        }
    }
});

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 1024,
    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

Console.WriteLine($"=== {workload}, {seconds}s per level, {bases.Length} node(s): {string.Join(" ", bases)}");
Console.WriteLine($"    postgres processes idle: {idleProcesses}{(pgPid == 0 ? " (every postgres on the machine)" : $" (postmaster {pgPid} and its children)")}");
Console.WriteLine("  conc |  req/s |  p50 ms |  p95 ms | errors | non-200 | MB/s | node cores | postgres cores | peak pg processes");

foreach (int conc in levels)
{
    // Warm-up, not counted.
    await Drive(conc, TimeSpan.FromSeconds(3), null);

    var latencies = new List<double>(100_000);
    double nodeBefore = NodeCpuSeconds();
    double pgBefore; lock (sampler) { pgBefore = pgCounted; peakProcesses = 0; }
    var wall = Stopwatch.StartNew();
    (int errors, int other, long bytes) = await Drive(conc, TimeSpan.FromSeconds(seconds), latencies);
    double elapsed = wall.Elapsed.TotalSeconds;
    double node = (NodeCpuSeconds() - nodeBefore) / elapsed;
    double pg; int peak; lock (sampler) { pg = (pgCounted - pgBefore) / elapsed; peak = peakProcesses; }
    latencies.Sort();
    double P(double q) => latencies.Count == 0 ? 0 : latencies[(int)Math.Min(latencies.Count - 1, q * latencies.Count)];
    Console.WriteLine($"  {conc,4} | {latencies.Count / elapsed,6:0} | {P(0.5),7:0.0} | {P(0.95),7:0.0} | {errors,6} | {other,7} | {bytes / elapsed / 1048576,4:0.0} | {node,10:0.00} | {pg,14:0.00} | {peak,17}");
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

static class Toolhelp
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr HeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static List<(int Pid, int Parent)> Processes()
    {
        var list = new List<(int, int)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        try
        {
            var e = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
            if (Process32FirstW(snap, ref e))
            {
                do { list.Add(((int)e.ProcessId, (int)e.ParentProcessId)); } while (Process32NextW(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }
        return list;
    }
}
