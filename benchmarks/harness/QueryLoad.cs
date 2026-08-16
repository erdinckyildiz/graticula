using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GisBench;

/// <summary>
/// Concurrency for the feature query path, against the real server.
/// </summary>
/// <remarks>
/// <para>
/// <b>D-30's open half and F1 of the §66 performance gate.</b> The phase
/// decomposition in <c>benchmarks/feature-query</c> answered where a query
/// spends its time at concurrency 1. It could not answer why throughput stops
/// scaling, because the load generator was a Python thread pool doing TLS and it
/// failed its own control run: <c>/rest/info</c>, which reads nothing, managed
/// one request per second at concurrency 4. Nothing measured beside that is
/// evidence.
/// </para>
/// <para>
/// <b>Why this is not <see cref="LoadGen"/>.</b> That one was written for A-037
/// against the experiment server: plain HTTP, and it samples a public
/// <c>/metrics</c> endpoint the product does not have and should not grow. This
/// one speaks TLS to a self-signed certificate, signs in, and reads the same
/// counters from <c>/admin/health</c>, where they are already behind
/// <c>admin:manageServer</c>.
/// </para>
/// <para>
/// <b>The control run is the first thing it does and the run aborts without
/// it.</b> F4: a harness in this project has been wrong three times, and the
/// third looked entirely plausible. A generator that cannot outrun the server on
/// a path the server barely touches is measuring itself.
/// </para>
/// <para>
///   GisBench.exe queryload &lt;baseUrl&gt; &lt;user&gt; &lt;password&gt; &lt;seconds&gt; &lt;path&gt;
/// </para>
/// </remarks>
internal static class QueryLoad
{
    private sealed record Snapshot(
        double AllocMB, int Gen0, int Gen1, int Gen2,
        double GcPauseMs, double CpuMs, double HeapMB, int Cores);

    private sealed record Result(int Count, long Bytes, double WallMs, List<double> Latencies);

    private static readonly int[] Levels = [1, 2, 4, 8, 16, 24, 32];

    internal static async Task RunAsync(string[] args)
    {
        string baseUrl = args[1].TrimEnd('/');
        string user = args[2];
        string password = args[3];
        int seconds = int.Parse(args[4], CultureInfo.InvariantCulture);
        string path = args[5];

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 512,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            SslOptions =
            {
                // A development certificate. This is a load generator pointed at
                // a server on loopback, not a client.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        string token = await SignInAsync(http, baseUrl, user, password);

        // <b>The control, before anything else.</b> /rest/info reads nothing and
        // touches no database. If this does not scale, the generator is the
        // ceiling and every other row would be a measurement of Python's
        // successor rather than of the server.
        Console.WriteLine();
        Console.WriteLine("=== control: /rest/info, which reads nothing");
        Console.WriteLine();

        double controlBase = 0;
        double controlPeak = 0;

        Header();

        foreach (int conc in Levels)
        {
            var r = await MeasureAsync(http, baseUrl, "/rest/info?f=json", token, conc, seconds,
                sample: false);

            double rps = r.Count / (r.WallMs / 1000.0);
            controlBase = conc == 1 ? rps : controlBase;
            controlPeak = Math.Max(controlPeak, rps);

            Row(conc, r, null, null);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  control: {controlBase:F0} req/s at concurrency 1, peak {controlPeak:F0}.");

        // <b>A ceiling to compare against, not a threshold to pass.</b> The first
        // version aborted unless the control scaled fourfold, and that rule
        // conflated two different failures: a generator too weak to load the
        // server, and a server that saturates early on every path including the
        // cheap one. The control here does scale poorly — and it turned out to
        // be the second, which the rule would have reported as the first.
        //
        // What the control is actually for is headroom. It is the same
        // generator, the same TLS, the same middleware and the same
        // authentication as the measured path, doing no work at the end of it.
        // If the measured path reaches most of the control's throughput, the
        // number is the pipeline's and not the path's, and nothing about the
        // query engine can be concluded from it. The comparison is printed
        // beside every row rather than decided here.
        Console.WriteLine();
        Console.WriteLine($"=== feature query: {path}");
        Console.WriteLine();

        Header();

        List<double> control = [];

        foreach (int conc in Levels)
        {
            var again = await MeasureAsync(
                http, baseUrl, "/rest/info?f=json", token, conc, 2, sample: false);

            control.Add(again.Count / (again.WallMs / 1000.0));
        }

        int index = 0;

        foreach (int conc in Levels)
        {
            var before = await SampleAsync(http, baseUrl, token);
            var r = await MeasureAsync(http, baseUrl, path, token, conc, seconds, sample: true);
            var after = await SampleAsync(http, baseUrl, token);

            Row(conc, r, before, after);

            double share = 100.0 * (r.Count / (r.WallMs / 1000.0)) / control[index++];

            if (share > 80)
            {
                Console.WriteLine(
                    $"         ^ {share:F0}% of the control's throughput at this concurrency. "
                  + "Too close to the pipeline's own ceiling to say anything about the query.");
            }
        }

        Console.WriteLine();
    }

    private static void Header()
    {
        Console.WriteLine("  conc |  req/s | p50 ms | p95 ms | p99 ms | MB/s | alloc MB/s |"
                        + " alloc KB/req | gen0/s | gen2 | GC pause % | CPU cores");
        Console.WriteLine("  -----+--------+--------+--------+--------+------+------------+"
                        + "--------------+--------+------+------------+----------");
    }

    private static void Row(int conc, Result r, Snapshot? before, Snapshot? after)
    {
        double wall = r.WallMs / 1000.0;
        var lat = r.Latencies;
        lat.Sort();

        double P(double q) =>
            lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)(q * lat.Count))];

        string tail = "      |            |              |        |      |            |";

        if (before is not null && after is not null)
        {
            double allocMB = after.AllocMB - before.AllocMB;
            double pauseMs = after.GcPauseMs - before.GcPauseMs;
            double cpuMs = after.CpuMs - before.CpuMs;

            tail =
                $" {allocMB / wall,10:F0} | {allocMB * 1024 / Math.Max(1, r.Count),12:F1} |"
              + $" {(after.Gen0 - before.Gen0) / wall,6:F0} | {after.Gen2 - before.Gen2,4} |"
              + $" {100.0 * pauseMs / r.WallMs,10:F1} | {cpuMs / r.WallMs,5:F2} of {before.Cores}";
        }

        Console.WriteLine(
            $"  {conc,4} | {r.Count / wall,6:F1} | {P(0.50),6:F1} | {P(0.95),6:F1} |"
          + $" {P(0.99),6:F1} | {r.Bytes / 1048576.0 / wall,4:F1} |{tail}");
    }

    private static async Task<Result> MeasureAsync(
        HttpClient http, string baseUrl, string path, string token,
        int concurrency, int seconds, bool sample)
    {
        // Warm at this concurrency first: the thread pool grows on demand, and a
        // cold pool measures as a latency problem a running server does not have.
        await DriveAsync(http, baseUrl, path, token, concurrency, TimeSpan.FromSeconds(2));

        return await DriveAsync(
            http, baseUrl, path, token, concurrency, TimeSpan.FromSeconds(seconds));
    }

    private static async Task<Result> DriveAsync(
        HttpClient http, string baseUrl, string path, string token,
        int concurrency, TimeSpan duration)
    {
        var latencies = new List<double>[concurrency];
        long totalBytes = 0;
        int totalCount = 0;

        var clock = Stopwatch.StartNew();
        var tasks = new Task[concurrency];

        for (int w = 0; w < concurrency; w++)
        {
            int slot = w;
            latencies[slot] = new List<double>(4096);

            tasks[slot] = Task.Run(async () =>
            {
                var sw = new Stopwatch();

                while (clock.Elapsed < duration)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    sw.Restart();
                    using var response = await http.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead);
                    byte[] body = await response.Content.ReadAsByteArrayAsync();
                    sw.Stop();

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"{path} answered {(int)response.StatusCode}. A load run against a "
                          + "failing path measures the error handler.");
                    }

                    latencies[slot].Add(sw.Elapsed.TotalMilliseconds);
                    Interlocked.Add(ref totalBytes, body.Length);
                    Interlocked.Increment(ref totalCount);
                }
            });
        }

        await Task.WhenAll(tasks);

        var all = new List<double>(totalCount);

        foreach (var l in latencies)
        {
            all.AddRange(l);
        }

        return new Result(totalCount, totalBytes, clock.Elapsed.TotalMilliseconds, all);
    }

    private static async Task<string> SignInAsync(
        HttpClient http, string baseUrl, string user, string password)
    {
        using var response = await http.PostAsync(
            baseUrl + "/rest/auth/login",
            new StringContent(
                JsonSerializer.Serialize(new { name = user, password }),
                System.Text.Encoding.UTF8,
                "application/json"));

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>The server's own runtime counters, from /admin/health.</summary>
    private static async Task<Snapshot> SampleAsync(HttpClient http, string baseUrl, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/admin/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (!document.RootElement.TryGetProperty("runtime", out JsonElement runtime))
        {
            throw new InvalidOperationException(
                "/admin/health has no 'runtime' block. Either the account lacks "
              + "admin:manageServer — in which case everything interesting is redacted and the "
              + "run would report zeroes — or this build predates the counters.");
        }

        return new Snapshot(
            runtime.GetProperty("allocatedBytes").GetDouble() / 1048576.0,
            runtime.GetProperty("gen0").GetInt32(),
            runtime.GetProperty("gen1").GetInt32(),
            runtime.GetProperty("gen2").GetInt32(),
            runtime.GetProperty("gcPauseMilliseconds").GetDouble(),
            runtime.GetProperty("cpuMilliseconds").GetDouble(),
            runtime.GetProperty("heapBytes").GetDouble() / 1048576.0,
            runtime.GetProperty("cores").GetInt32());
    }
}
