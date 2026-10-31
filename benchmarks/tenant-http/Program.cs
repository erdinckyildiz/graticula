// ADR-204 conditions 13 and 14: the HTTP p95 of /sharing/rest/search, a services directory and a FeatureServer
// query, measured on the built floor with ten organisations against the same build with one. Disposable rig.
//
//   tenant-http <base> <plan.json> <seconds> <callers> <out.json>
//
// plan.json lists the organisations: [{ "host": "t1.localhost", "token": "…", "layer": "hp_t1", "weight": 1 }, …].
// Each caller picks an organisation by weight for each round and asks the three addresses in turn at its host with
// its token. Prints p50/p95/p99 per address over everyone and for the heaviest organisation, failures by status,
// and the platform store's connections, sampled by the caller-supplied command if any.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

string root = args[0].TrimEnd('/');
JsonElement[] plan = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement.EnumerateArray().ToArray();
int seconds = int.Parse(args[2], CultureInfo.InvariantCulture);
int callers = int.Parse(args[3], CultureInfo.InvariantCulture);
string outPath = args[4];

var orgs = plan.Select(p => (
    Host: p.GetProperty("host").GetString()!,
    Token: p.GetProperty("token").GetString()!,
    Layer: p.GetProperty("layer").GetString()!,
    Weight: p.GetProperty("weight").GetInt32())).ToArray();
int totalWeight = orgs.Sum(o => o.Weight);
string busy = orgs.OrderByDescending(o => o.Weight).First().Host;

string[] Paths(string layer) =>
[
    $"/sharing/rest/search?q=hp&num=10&f=json",
    "/rest/services/hosted?f=json",
    $"/rest/services/hosted/{layer}/FeatureServer/0/query?where=1%3D1&outFields=*&resultRecordCount=50&f=json",
];
string[] names = ["search", "services", "query"];

using HttpClient http = new(new SocketsHttpHandler
{
    MaxConnectionsPerServer = callers * 2,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
})
{ Timeout = TimeSpan.FromSeconds(30) };

ConcurrentBag<(string Org, int Path, double Ms, int Status)> samples = [];

async Task<(int, double)> AskAsync(string host, string token, string path)
{
    using HttpRequestMessage request = new(HttpMethod.Get, root + path);
    request.Headers.Host = host;
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    long start = Stopwatch.GetTimestamp();
    try
    {
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        _ = await response.Content.ReadAsByteArrayAsync();
        return ((int)response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    catch (Exception)
    {
        return (-1, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}

async Task RunAsync(TimeSpan length, bool record, int seed)
{
    DateTime end = DateTime.UtcNow + length;
    Random random = new(seed);
    while (DateTime.UtcNow < end)
    {
        int pick = random.Next(totalWeight);
        var org = orgs.First(o => (pick -= o.Weight) < 0);
        string[] paths = Paths(org.Layer);
        for (int i = 0; i < paths.Length; i++)
        {
            (int status, double ms) = await AskAsync(org.Host, org.Token, paths[i]);
            if (record)
            {
                samples.Add((org.Host, i, ms, status));
            }
        }
    }
}

// Warm-up: every organisation's every address, then three seconds of load unrecorded.
foreach (var org in orgs)
{
    foreach (string path in Paths(org.Layer))
    {
        (int status, _) = await AskAsync(org.Host, org.Token, path);
        if (status != 200)
        {
            Console.WriteLine($"warm-up: {org.Host} {path.Split('?')[0]} answered {status}");
        }
    }
}

await Task.WhenAll(Enumerable.Range(0, callers).Select(i => Task.Run(() => RunAsync(TimeSpan.FromSeconds(3), false, i))));
samples.Clear();
Stopwatch wall = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, callers).Select(i => Task.Run(() => RunAsync(TimeSpan.FromSeconds(seconds), true, 1000 + i))));
double elapsed = wall.Elapsed.TotalSeconds;

static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

var all = samples.ToArray();
var result = new Dictionary<string, object>
{
    ["organisations"] = orgs.Length,
    ["callers"] = callers,
    ["seconds"] = Math.Round(elapsed, 1),
    ["requests"] = all.Length,
    ["perSecond"] = Math.Round(all.Length / elapsed, 1),
    ["failures"] = all.Where(s => s.Status != 200).GroupBy(s => s.Status).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
};

foreach ((string scope, Func<(string Org, int Path, double Ms, int Status), bool> filter) in new (string, Func<(string Org, int Path, double Ms, int Status), bool>)[]
{
    ("all", _ => true),
    ("busy", s => s.Org == busy),
    ("others", s => s.Org != busy),
})
{
    for (int i = 0; i < names.Length; i++)
    {
        List<double> ms = [.. all.Where(s => s.Path == i && s.Status == 200 && filter(s)).Select(s => s.Ms).Order()];
        if (ms.Count == 0)
        {
            continue;
        }

        result[$"{scope}.{names[i]}"] = new
        {
            n = ms.Count,
            p50 = Math.Round(Pct(ms, 0.50), 2),
            p95 = Math.Round(Pct(ms, 0.95), 2),
            p99 = Math.Round(Pct(ms, 0.99), 2),
        };
    }
}

string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(outPath, json);
Console.WriteLine(json);
