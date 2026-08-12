using System.Diagnostics;
using System.Globalization;

namespace GisBench;

/// <summary>
/// Concurrency driver for A-037: <i>allocation rate, not CPU, sets the
/// tile-serving ceiling per worker</i>.
///
/// Single-request latency cannot answer that. A GC pause lands on whichever
/// request is unlucky, so at concurrency 1 it looks like variance; the question
/// is whether throughput stops scaling while CPU is still available.
///
/// Runs in the same executable as the server but as a separate process — the
/// driver's own allocations must not be counted against the server's. Server
/// metrics come from the server's <c>/metrics</c> endpoint, sampled either side
/// of the run.
///
///   GisBench.exe load &lt;baseUrl&gt; &lt;seconds&gt; &lt;label&gt; &lt;path&gt; [path...]
///
/// with concurrency levels swept internally.
/// </summary>
internal static class LoadGen
{
    private sealed record Snapshot(
        double AllocMB, int Gen0, int Gen1, int Gen2,
        double GcPauseMs, double CpuMs, double HeapMB, double UptimeMs, int Cores);

    internal static async Task RunAsync(string[] args)
    {
        // load <baseUrl> <seconds> <label> <path> [path...]
        string baseUrl = args[1].TrimEnd('/');
        int seconds = int.Parse(args[2], CultureInfo.InvariantCulture);
        string label = args[3];
        var paths = args.Skip(4).ToArray();
        int[] levels = [1, 2, 4, 8, 16];

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        Console.WriteLine();
        Console.WriteLine($"=== {label} — {paths.Length} distinct tile(s), {seconds}s per level");
        Console.WriteLine();
        Console.WriteLine("  conc |  req/s | p50 ms | p95 ms | p99 ms |  max ms | MB/s out |"
                        + " alloc MB/s | alloc MB/req | gen0/s | gen2 | GC pause % | server CPU cores");
        Console.WriteLine("  -----+--------+--------+--------+--------+---------+----------+"
                        + "------------+--------------+--------+------+------------+-----------------");

        foreach (int conc in levels)
        {
            // Warm at this concurrency before measuring: the thread pool grows
            // on demand and a cold pool would be measured as a latency problem
            // that does not exist in a running server.
            await DriveAsync(http, baseUrl, paths, conc, TimeSpan.FromSeconds(3));

            var before = await SampleAsync(http, baseUrl);
            var r = await DriveAsync(http, baseUrl, paths, conc, TimeSpan.FromSeconds(seconds));
            var after = await SampleAsync(http, baseUrl);

            double wall = r.WallMs / 1000.0;
            double allocMB = after.AllocMB - before.AllocMB;
            double cpuMs = after.CpuMs - before.CpuMs;
            double pauseMs = after.GcPauseMs - before.GcPauseMs;

            var lat = r.Latencies;
            lat.Sort();
            double P(double q) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)(q * lat.Count))];

            Console.WriteLine(
                $"  {conc,4} | {r.Count / wall,6:F1} | {P(0.50),6:F0} | {P(0.95),6:F0} | {P(0.99),6:F0} |"
              + $" {(lat.Count > 0 ? lat[^1] : 0),7:F0} | {r.Bytes / 1048576.0 / wall,8:F1} |"
              + $" {allocMB / wall,10:F0} | {allocMB / Math.Max(1, r.Count),12:F1} |"
              + $" {(after.Gen0 - before.Gen0) / wall,6:F0} | {after.Gen2 - before.Gen2,4} |"
              + $" {100.0 * pauseMs / r.WallMs,10:F1} | {cpuMs / r.WallMs,8:F2} of {before.Cores}");
        }

        Console.WriteLine();
    }

    private sealed record Result(int Count, long Bytes, double WallMs, List<double> Latencies);

    private static async Task<Result> DriveAsync(
        HttpClient http, string baseUrl, string[] paths, int concurrency, TimeSpan duration)
    {
        var latencies = new List<double>[concurrency];
        long totalBytes = 0;
        int totalCount = 0;
        int cursor = 0;

        var deadline = Stopwatch.StartNew();
        var tasks = new Task[concurrency];

        for (int w = 0; w < concurrency; w++)
        {
            int slot = w;
            latencies[slot] = new List<double>(1024);
            tasks[slot] = Task.Run(async () =>
            {
                var sw = new Stopwatch();
                while (deadline.Elapsed < duration)
                {
                    string path = paths[Math.Abs(Interlocked.Increment(ref cursor)) % paths.Length];
                    sw.Restart();
                    using var resp = await http.GetAsync(baseUrl + path, HttpCompletionOption.ResponseHeadersRead);
                    var body = await resp.Content.ReadAsByteArrayAsync();
                    sw.Stop();

                    latencies[slot].Add(sw.Elapsed.TotalMilliseconds);
                    Interlocked.Add(ref totalBytes, body.Length);
                    Interlocked.Increment(ref totalCount);
                }
            });
        }

        await Task.WhenAll(tasks);
        double wall = deadline.Elapsed.TotalMilliseconds;

        var all = new List<double>(totalCount);
        foreach (var l in latencies) all.AddRange(l);
        return new Result(totalCount, totalBytes, wall, all);
    }

    private static async Task<Snapshot> SampleAsync(HttpClient http, string baseUrl)
    {
        var text = await http.GetStringAsync(baseUrl + "/metrics");
        var kv = new Dictionary<string, double>();
        foreach (var line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            // Non-numeric values (server_gc=True) are informational; skip them
            // rather than let one flag abort a fifteen-second measurement.
            if (double.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double v))
                kv[line[..eq].Trim()] = v;
        }
        return new Snapshot(
            kv.GetValueOrDefault("alloc_mb"), (int)kv.GetValueOrDefault("gen0"),
            (int)kv.GetValueOrDefault("gen1"), (int)kv.GetValueOrDefault("gen2"),
            kv.GetValueOrDefault("gc_pause_ms"), kv.GetValueOrDefault("cpu_ms"),
            kv.GetValueOrDefault("heap_mb"), kv.GetValueOrDefault("uptime_ms"),
            (int)kv.GetValueOrDefault("cores"));
    }
}
