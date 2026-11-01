using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Schema;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A stand-in for Caddy in front of a site's nodes — ADR-012 §5 phase 3's <c>reverse_proxy</c> as the rolling upgrade
/// meets it: round robin over the nodes whose <c>/healthz/ready</c> last answered 200, checked twice a second, and
/// <b>no retry</b> — a request sent to a node that fails it is a failed request, which is the harder test (Caddy's
/// <c>lb_try_duration</c> would hide some of them).
/// </summary>
/// <remarks>
/// Written for the test because Caddy is not installed on the machines these run on; the real Caddy in front of two
/// nodes is <c>tools/caddy-site-probe.py</c>. What it shares with Caddy is what the upgrade depends on: a drained node
/// answers 503 on its health path and is sent nothing new.
/// </remarks>
internal sealed class Balancer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<(string Name, HttpClient Http)> _upstreams = [];
    private readonly ConcurrentDictionary<string, bool> _healthy = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _checks;
    private int _next;

    /// <summary>Starts checking the nodes it is given.</summary>
    public Balancer(params TwoNodeSite.Node[] nodes)
    {
        foreach (TwoNodeSite.Node node in nodes)
        {
            Set(node);
        }

        _checks = Task.Run(CheckAsync);
    }

    /// <summary>Puts a node in the upstream list, or replaces the one of its name — not sent anything until it checks ready.</summary>
    public void Set(TwoNodeSite.Node node)
    {
        lock (_gate)
        {
            _upstreams.RemoveAll(u => u.Name == node.Name);
            _upstreams.Add((node.Name, node.Http));
            _healthy[node.Name] = false;
        }
    }

    /// <summary>Whether the last health check of a node said ready.</summary>
    public bool Healthy(string name) => _healthy.TryGetValue(name, out bool ok) && ok;

    private async Task CheckAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            (string Name, HttpClient Http)[] now;

            lock (_gate)
            {
                now = [.. _upstreams];
            }

            foreach ((string name, HttpClient http) in now)
            {
                try
                {
                    using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(2));
                    using HttpResponseMessage ready = await http.GetAsync("/healthz/ready", limit.Token);
                    _healthy[name] = ready.IsSuccessStatusCode;
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ObjectDisposedException)
                {
                    _healthy[name] = false;
                }
            }

            try
            {
                await Task.Delay(500, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Sends a request to the next node that checked ready, as a proxy would; never retried.</summary>
    /// <returns>The node it went to (empty when none was ready), its status (0 when it failed outright), and why.</returns>
    public async Task<(string Node, int Status, string? Failure)> SendAsync(HttpMethod method, string path, string? token)
    {
        (string Name, HttpClient Http)? chosen = null;

        lock (_gate)
        {
            for (int i = 0; i < _upstreams.Count && chosen is null; i++)
            {
                (string Name, HttpClient Http) candidate = _upstreams[(_next + i) % _upstreams.Count];

                if (Healthy(candidate.Name))
                {
                    chosen = candidate;
                    _next = (_next + i + 1) % _upstreams.Count;
                }
            }
        }

        if (chosen is not { } upstream)
        {
            return ("", 502, "no node was ready");
        }

        try
        {
            using HttpRequestMessage request = new(method, path);

            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using HttpResponseMessage answer = await upstream.Http.SendAsync(request);
            return (upstream.Name, (int)answer.StatusCode, answer.IsSuccessStatusCode ? null : await answer.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            return (upstream.Name, 0, e.Message);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _checks;
        _stop.Dispose();
    }
}

/// <summary>Callers sending requests through a <see cref="Balancer"/> without pause, counting every answer that is not a 200.</summary>
internal sealed class SteadyLoad : IAsyncDisposable
{
    // Not a health path: those answer 503 on a drained node by design, and a balancer's check is not a client's request.
    private static readonly string[] Paths = ["/rest/info?f=json", "/rest/services?f=json", "/sharing/rest/portals/self?f=json"];

    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _callers;
    private int _sent;

    /// <summary>Starts the callers.</summary>
    public SteadyLoad(Balancer balancer, string token, int callers = 4)
    {
        _callers = [.. Enumerable.Range(0, callers).Select(c => Task.Run(async () =>
        {
            for (int i = c; !_stop.IsCancellationRequested; i++)
            {
                string path = Paths[i % Paths.Length];
                (string node, int status, string? failure) = await balancer.SendAsync(HttpMethod.Get, path, token);
                Interlocked.Increment(ref _sent);

                if (status == 200)
                {
                    Served.AddOrUpdate(node, 1, (_, n) => n + 1);
                }
                else
                {
                    Failures.Add($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {path} on {(node.Length == 0 ? "(none)" : node)}: {status} {failure}");
                }

                await Task.Delay(5);
            }
        }))];
    }

    /// <summary>How many requests were sent.</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>The 200s each node answered.</summary>
    public ConcurrentDictionary<string, int> Served { get; } = new(StringComparer.Ordinal);

    /// <summary>Every request that was not answered 200.</summary>
    public ConcurrentBag<string> Failures { get; } = [];

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_callers);
        _stop.Dispose();
    }
}

/// <summary>
/// ADR-012 §5 phase 4 — the rolling upgrade — each part against real processes over one store (condition 3): a node of a
/// newer build joins a site of the older while the schema stays readable by both; one node at a time is drained behind
/// the proxy, stopped, started on the new build and returned, with no failed request; the migration step runs once under
/// its advisory lock however many run it; a contract migration waits until no node older than its minimum reader runs;
/// a node refuses to start on a store whose minimum reader is above it; and System › Machines says what each node runs.
/// </summary>
/// <remarks>
/// <b>Two builds</b> are the previous release's and this one, in
/// <see cref="A_rolling_upgrade_from_the_previous_build_to_this_one_fails_no_request"/>, which needs the previous build's
/// folder (<c>GRATICULA_PREVIOUS_HOST</c>) and so is <c>Needs=PreviousBuild</c>, not run by CI. The others run in CI, the
/// new build played by this one and its expand migration by the test.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ARollingUpgradeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> TwoNodes = new() { ["Graticula__Nodes"] = "2" };

    private static async Task<JsonElement> MachinesAsync(TwoNodeSite.Node on, string token)
    {
        (int status, string body) = await on.SendAsync(HttpMethod.Get, "/admin/site/machines", token);
        Assert.True(status == 200, $"/admin/site/machines on {on.Name} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement MachineOf(JsonElement site, string name) =>
        site.GetProperty("machines").EnumerateArray().First(m => m.GetProperty("name").GetString() == name);

    private static Task<bool> WaitAsync(Func<bool> holds, TimeSpan within, string what) =>
        TwoNodeSite.Node.UntilAsync(() => Task.FromResult(holds()), ok => ok, within, what);

    /// <summary>
    /// One node's turn: put under maintenance from the other, taken out by the balancer, stopped once its last requests are
    /// answered, started on <paramref name="build"/> at the same port — still under maintenance, as its row says — and
    /// started again, back in rotation once it checks ready.
    /// </summary>
    private static async Task<TwoNodeSite.Node> UpgradeAsync(
        TwoNodeSite site, Balancer balancer, TwoNodeSite.Node node, TwoNodeSite.Node other, string token, string? build,
        Func<TwoNodeSite.Node, Task>? whileOut = null, Func<Task>? beforeStart = null)
    {
        (int status, string body) = await other.SendAsync(HttpMethod.Put, $"/admin/site/machines/{node.Name}", token, new { state = "maintenance" });
        Assert.True(status == 200, $"draining {node.Name} answered {status}: {body}");

        await WaitAsync(() => !balancer.Healthy(node.Name), TimeSpan.FromSeconds(10), $"the balancer taking {node.Name} out");

        // A request the balancer chose just before it saw the 503 is still being answered.
        await Task.Delay(TimeSpan.FromSeconds(1));
        node.Kill();

        if (beforeStart is not null)
        {
            await beforeStart();
        }

        TwoNodeSite.Node started = await site.StartAsync(node.Name, TwoNodes, build, node.Port);
        balancer.Set(started);

        if (whileOut is not null)
        {
            await whileOut(started);
        }

        // Back from maintenance: an upgraded machine is returned on purpose, not by starting.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(balancer.Healthy(node.Name), $"{node.Name} was sent requests before it was started again");

        (status, body) = await started.SendAsync(HttpMethod.Put, $"/admin/site/machines/{node.Name}", token, new { state = "started" });
        Assert.True(status == 200, $"starting {node.Name} answered {status}: {body}");
        await WaitAsync(() => balancer.Healthy(node.Name), TimeSpan.FromSeconds(60), $"{node.Name} back in rotation");
        return started;
    }

    /// <summary>
    /// The whole upgrade, one node at a time behind the balancer under steady load, with the new build's expand migration
    /// run while both nodes serve (played by the test: a column and the stamp one higher, the minimum reader unmoved) —
    /// and no request fails. The Machines screen says, during it, that the store is ahead of the machine still out.
    /// </summary>
    [Fact]
    public async Task A_node_at_a_time_is_drained_restarted_and_returned_with_no_failed_request()
    {
        TwoNodeSite site = new();
        await site.InitializeAsync();

        try
        {
            TwoNodeSite.Node a = await site.StartAsync("rolling-a", TwoNodes);
            TwoNodeSite.Node b = await site.StartAsync("rolling-b", TwoNodes);
            string token = await a.SignInAsync();

            await using Balancer balancer = new(a, b);
            await WaitAsync(() => balancer.Healthy(a.Name) && balancer.Healthy(b.Name), TimeSpan.FromSeconds(60), "both nodes ready");

            int applied;
            SteadyLoad load = new(balancer, token);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));

                // The next build's expand, run while both nodes serve: neither is refused, since the minimum reader is unmoved.
                await site.ExecuteAsync(
                    "alter table site_node add column if not exists zz_next_build int; "
                    + "update platform_schema set applied_version = applied_version + 1");
                applied = Convert.ToInt32(await site.ScalarAsync("select applied_version from platform_schema"), System.Globalization.CultureInfo.InvariantCulture);

                a = await UpgradeAsync(site, balancer, a, b, token, build: null, whileOut: async restarted =>
                {
                    JsonElement during = await MachinesAsync(b, token);
                    Assert.Equal(applied, during.GetProperty("store").GetProperty("schema").GetInt32());
                    JsonElement out_ = MachineOf(during, "rolling-a");
                    Assert.Equal("maintenance", out_.GetProperty("configuredState").GetString());
                    Assert.Equal(PlatformMigrations.ComponentSchemaVersion.Value, out_.GetProperty("schema").GetInt32());
                });

                b = await UpgradeAsync(site, balancer, b, a, token, build: null);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await load.DisposeAsync();
            }

            output.WriteLine($"{load.Sent} requests sent through the balancer, {load.Failures.Count} failed; 200s by node: {string.Join(", ", load.Served.OrderBy(p => p.Key))}");
            Assert.True(load.Failures.IsEmpty, $"{load.Failures.Count} of {load.Sent} requests failed:\n{string.Join("\n", load.Failures.Take(20))}");
            Assert.True(load.Served.GetValueOrDefault("rolling-a") > 0 && load.Served.GetValueOrDefault("rolling-b") > 0,
                $"both nodes should have served: {string.Join(", ", load.Served)}");

            JsonElement after = await MachinesAsync(a, token);
            Assert.False(after.GetProperty("rollingUpgrade").GetProperty("inProgress").GetBoolean(), after.ToString());
            Assert.Equal(PlatformMigrations.ComponentSchemaVersion.Value, after.GetProperty("rollingUpgrade").GetProperty("readerCeiling").GetInt32());
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>
    /// The control for the test above: the same site, balancer and load, and a node stopped without being drained first —
    /// requests fail. So the drain is what makes the upgrade lossless, not the balancer.
    /// </summary>
    [Fact]
    public async Task A_node_stopped_without_draining_fails_requests_behind_the_same_balancer()
    {
        TwoNodeSite site = new();
        await site.InitializeAsync();

        try
        {
            TwoNodeSite.Node a = await site.StartAsync("undrained-a", TwoNodes);
            TwoNodeSite.Node b = await site.StartAsync("undrained-b", TwoNodes);
            string token = await a.SignInAsync();

            await using Balancer balancer = new(a, b);
            await WaitAsync(() => balancer.Healthy(a.Name) && balancer.Healthy(b.Name), TimeSpan.FromSeconds(60), "both nodes ready");

            SteadyLoad load = new(balancer, token, callers: 8);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                b.Kill();
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            finally
            {
                await load.DisposeAsync();
            }

            output.WriteLine($"{load.Sent} requests sent, {load.Failures.Count} failed across the kill");
            Assert.False(load.Failures.IsEmpty, $"no request failed across a node killed under load ({load.Sent} sent)");
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>
    /// Two upgrades running the migration at once — each node's start script, say — run it once: the second waits for the
    /// advisory lock, then finds the store current. Without the lock both apply the same migrations to one schema.
    /// </summary>
    [Fact]
    public async Task Two_migrations_started_at_once_run_it_once()
    {
        TwoNodeSite site = new(build: null, migrate: false);
        await site.InitializeAsync();

        try
        {
            Task<(int Exit, string Said)>[] both =
            [
                site.RunAsync(["migrate", "--apply", "--no-backup"]),
                site.RunAsync(["migrate", "--apply", "--no-backup"]),
            ];

            (int Exit, string Said)[] ran = await Task.WhenAll(both);

            foreach ((int exit, string said) in ran)
            {
                Assert.True(exit == 0, $"a migration run at the same time as another failed ({exit}):\n{said}");
            }

            Assert.Single(ran, r => r.Said.Contains("Applied ", StringComparison.Ordinal));
            Assert.Single(ran, r => r.Said.Contains("up to date", StringComparison.Ordinal));
            Assert.Equal(
                PlatformMigrations.All.Latest.Value,
                Convert.ToInt32(await site.ScalarAsync("select applied_version from platform_schema"), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>
    /// A contract migration waits until no node older than its minimum reader runs: with a running node that reads below
    /// it, <c>migrate --apply</c> changes nothing and names the node; and a node whose row was rewritten by a build that does
    /// not say its schema counts as older. The pending contract is the store's own history from migration 43 on, reached by
    /// setting the stamp back to 42; the refusal comes before anything is applied, so nothing is.
    /// </summary>
    [Fact]
    public async Task A_contract_migration_waits_until_no_node_older_than_its_minimum_reader_runs()
    {
        TwoNodeSite site = new();
        await site.InitializeAsync();

        try
        {
            TwoNodeSite.Node a = await site.StartAsync("contract-a");

            // As a build for schema 33 would have written its row: older than migration 43's minimum reader of 34.
            await site.ExecuteAsync(
                "update site_node set schema_version = 33 where name = 'contract-a'; "
                + "update platform_schema set applied_version = 42, minimum_reader_version = 1");

            (int exit, string said) = await site.RunAsync(["migrate", "--apply", "--no-backup"]);
            Assert.True(exit != 0, $"a contract ran with an older node up:\n{said}");
            Assert.Contains("No migration was run.", said, StringComparison.Ordinal);
            Assert.Contains("'contract-a' (version ", said, StringComparison.Ordinal);
            Assert.Contains("reads schema 33", said, StringComparison.Ordinal);
            Assert.Equal(42, Convert.ToInt32(await site.ScalarAsync("select applied_version from platform_schema"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, Convert.ToInt32(await site.ScalarAsync("select minimum_reader_version from platform_schema"), System.Globalization.CultureInfo.InvariantCulture));

            // The plan says so too, without --apply.
            (int planned, string plan) = await site.RunAsync(["migrate"]);
            Assert.Equal(0, planned);
            Assert.Contains("With --apply this would be refused.", plan, StringComparison.Ordinal);

            // Rewritten at start by a build before migration 125, which leaves the number and not its start: does not say.
            await site.ExecuteAsync(
                "update site_node set schema_version = 125, schema_version_at = started_at - interval '1 second' where name = 'contract-a'");
            (exit, said) = await site.RunAsync(["migrate", "--apply", "--no-backup"]);
            Assert.True(exit != 0, $"a contract ran with a node up that does not say its schema:\n{said}");
            Assert.Contains("which does not say the schema it reads", said, StringComparison.Ordinal);
            Assert.True(a.Running);
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>
    /// A node refuses to start on a store whose minimum reader is above its build — a contract a newer build ran — and says
    /// both numbers; the node already running is not touched by it.
    /// </summary>
    [Fact]
    public async Task A_node_refuses_to_start_on_a_store_whose_minimum_reader_is_above_it()
    {
        TwoNodeSite site = new();
        await site.InitializeAsync();

        try
        {
            TwoNodeSite.Node a = await site.StartAsync("reader-a", TwoNodes);
            int mine = PlatformMigrations.ComponentSchemaVersion.Value;

            await site.ExecuteAsync($"update platform_schema set applied_version = {mine + 1}, minimum_reader_version = {mine + 1}");

            (int? exit, string said) = await site.StartRefusedAsync("reader-b", TwoNodes);
            Assert.True(exit is not null and not 0, $"a node older than the store's minimum reader started:\n{said}");
            Assert.Contains($"is built for schema {mine}, and the platform store requires at least {mine + 1}", said, StringComparison.Ordinal);

            Assert.Equal(200, (await a.SendAsync(HttpMethod.Get, "/rest/info?f=json", null)).Status);
        }
        finally
        {
            await site.DisposeAsync();
        }
    }

    /// <summary>
    /// The two builds: a site of two nodes on the previous release's build, upgraded one node at a time to this one behind
    /// the balancer under steady load — this build's migrations run by it once the first node is out, while the other,
    /// older node keeps serving — with no failed request. During it the Machines screen says a rolling upgrade is in
    /// progress and that no contract may run; after it, that one may.
    /// </summary>
    [Fact]
    [Trait("Needs", "PreviousBuild")]
    public async Task A_rolling_upgrade_from_the_previous_build_to_this_one_fails_no_request()
    {
        string previous = Environment.GetEnvironmentVariable("GRATICULA_PREVIOUS_HOST") is { Length: > 0 } folder
            ? folder
            : throw new InvalidOperationException(
                "GRATICULA_PREVIOUS_HOST is not set: it names the folder of the previous release's Graticula.Host.dll, "
                + "which this test upgrades from (ADR-012 phase 4).");

        TwoNodeSite site = new(previous);
        await site.InitializeAsync();

        try
        {
            int before = Convert.ToInt32(await site.ScalarAsync("select applied_version from platform_schema"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(before < PlatformMigrations.ComponentSchemaVersion.Value,
                $"the previous build migrated the store to {before}, which is not older than this build's {PlatformMigrations.ComponentSchemaVersion}");

            TwoNodeSite.Node a = await site.StartAsync("upgrade-a", TwoNodes);
            TwoNodeSite.Node b = await site.StartAsync("upgrade-b", TwoNodes);
            string token = await a.SignInAsync();

            await using Balancer balancer = new(a, b);
            await WaitAsync(() => balancer.Healthy(a.Name) && balancer.Healthy(b.Name), TimeSpan.FromSeconds(60), "both nodes ready");

            SteadyLoad load = new(balancer, token);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));

                // This build's migrations, run by it while the first node is out and the older one serves: expand only, so
                // the older node is not refused, and run under the lock (a second run would wait, then change nothing).
                a = await UpgradeAsync(site, balancer, a, b, token, TwoNodeSite.ThisBuild, beforeStart: async () =>
                {
                    (int exit, string said) = await site.RunAsync(["migrate", "--apply", "--no-backup"], TwoNodeSite.ThisBuild);
                    Assert.True(exit == 0, $"this build's migration failed ({exit}):\n{said}");
                    Assert.Contains($"Now at schema {PlatformMigrations.ComponentSchemaVersion},", said, StringComparison.Ordinal);
                    Assert.DoesNotContain("WARNING: this closes the rollback window", said, StringComparison.Ordinal);
                });

                // Mixed builds: the upgraded node reports both, and a contract would be refused for the older one.
                JsonElement during = await MachinesAsync(a, token);
                JsonElement rolling = during.GetProperty("rollingUpgrade");
                Assert.True(rolling.GetProperty("inProgress").GetBoolean(), during.ToString());
                Assert.Equal(JsonValueKind.Null, rolling.GetProperty("readerCeiling").ValueKind);
                Assert.Equal(PlatformMigrations.ComponentSchemaVersion.Value, MachineOf(during, "upgrade-a").GetProperty("schema").GetInt32());
                Assert.Equal(JsonValueKind.Null, MachineOf(during, "upgrade-b").GetProperty("schema").ValueKind);
                Assert.Equal(PlatformMigrations.ComponentSchemaVersion.Value, during.GetProperty("store").GetProperty("schema").GetInt32());

                await using (NpgsqlDataSource store = NpgsqlDataSource.Create(site.Store))
                {
                    IReadOnlyList<Machine> machines = await SiteNodes.ReadAsync(store, CancellationToken.None);
                    string? refused = SiteNodes.ContractRefusal(machines, PlatformMigrations.ComponentSchemaVersion.Value);
                    Assert.NotNull(refused);
                    Assert.Contains("'upgrade-b'", refused, StringComparison.Ordinal);
                    Assert.DoesNotContain("'upgrade-a'", refused, StringComparison.Ordinal);
                }

                b = await UpgradeAsync(site, balancer, b, a, token, TwoNodeSite.ThisBuild);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await load.DisposeAsync();
            }

            output.WriteLine($"{load.Sent} requests sent through the balancer, {load.Failures.Count} failed; 200s by node: {string.Join(", ", load.Served.OrderBy(p => p.Key))}");
            Assert.True(load.Failures.IsEmpty, $"{load.Failures.Count} of {load.Sent} requests failed:\n{string.Join("\n", load.Failures.Take(20))}");
            Assert.True(load.Served.GetValueOrDefault("upgrade-a") > 0 && load.Served.GetValueOrDefault("upgrade-b") > 0,
                $"both nodes should have served: {string.Join(", ", load.Served)}");

            JsonElement after = await MachinesAsync(b, token);
            Assert.False(after.GetProperty("rollingUpgrade").GetProperty("inProgress").GetBoolean(), after.ToString());
            Assert.Equal(PlatformMigrations.ComponentSchemaVersion.Value, after.GetProperty("rollingUpgrade").GetProperty("readerCeiling").GetInt32());
        }
        finally
        {
            await site.DisposeAsync();
        }
    }
}

/// <summary>The rolling upgrade's rule as a decision about machines — ADR-012 §5 phase 4.</summary>
public sealed class ARollingUpgradesRuleTests
{
    private static Machine M(string name, int? schema, bool up = true, string version = "1.0.0") => new(
        name, "https://x:1", version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, up, "started", true, [], schema);

    /// <summary>A contract to a reader every machine up reads at or past may run; one past any of them may not.</summary>
    [Fact]
    public void A_contract_runs_only_to_the_oldest_schema_a_machine_up_reads()
    {
        Machine[] site = [M("a", 125), M("b", 130)];
        Assert.Null(SiteNodes.ContractRefusal(site, 125));
        string? refused = SiteNodes.ContractRefusal(site, 126);
        Assert.NotNull(refused);
        Assert.Contains("'a' (version 1.0.0, reads schema 125)", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("'b'", refused, StringComparison.Ordinal);
    }

    /// <summary>A machine that does not say its schema blocks every contract; a machine that is down blocks none.</summary>
    [Fact]
    public void A_machine_that_does_not_say_blocks_and_one_that_is_down_does_not()
    {
        Assert.NotNull(SiteNodes.ContractRefusal([M("old", null)], 2));
        Assert.Null(SiteNodes.ContractRefusal([M("gone", 3, up: false), M("new", 200)], 150));
    }

    /// <summary>The machines up are grouped by build, so two groups is a rolling upgrade in progress.</summary>
    [Fact]
    public void Machines_are_grouped_by_the_build_they_run()
    {
        var builds = SiteNodes.Builds([M("a", 125, version: "1.0.2"), M("b", null, version: "1.0.1"), M("c", 125, version: "1.0.2"), M("d", 1, up: false)]);
        Assert.Equal(2, builds.Count);
        Assert.Equal(["b"], builds[0].Machines);
        Assert.Equal(["a", "c"], builds[1].Machines);
    }
}
