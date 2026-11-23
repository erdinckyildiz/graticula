using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-319: the platform store's primary and standby, with real PostgreSQL clusters and real <c>Graticula.Host</c>
/// processes — conditions 1–4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test makes its own two clusters</b> (<see cref="StandbyStore"/>), because a failover changes them for good:
/// the standby is a primary afterwards and the old primary is fenced or rewound.
/// </para>
/// <para>
/// <b>Writes are groups</b> (<c>POST /admin/groups</c>): a row in the store per request, named so the test can count, on
/// each cluster directly, which landed where.
/// </para>
/// <para>
/// <c>Needs=StandbyStore</c>: CI has PostgreSQL's client and no server binaries to make clusters from, so it runs none
/// of these and says so.
/// </para>
/// </remarks>
[Trait("Needs", "StandbyStore")]
public sealed class TheStoreFailsOverTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> TwoNodes = new() { ["Graticula__Nodes"] = "2" };

    /// <summary>
    /// Conditions 1, 2 and 4 on a site of one node — the case with no majority (§5.4): the primary is killed, the node
    /// and the standby agree, the standby is promoted, writes resume on it; the old primary comes back read-write on the
    /// old timeline, first in the connection string, and nothing is written to it; rewound, it streams from the new one.
    /// </summary>
    [Fact]
    public async Task A_killed_primary_is_replaced_by_its_standby_and_never_written_to_again()
    {
        await using StandbyStore store = await StandbyStore.StartAsync();
        TwoNodeSite site = new(build: null, migrate: true, database: store.Hosts());

        try
        {
            await site.InitializeAsync();
            TwoNodeSite.Node node = await site.StartAsync("ha-one");
            string token = await node.SignInAsync();
            string schema = site.Schema;

            for (int i = 0; i < 5; i++)
            {
                await WriteAsync(node, token, "before", i);
            }

            await StreamedAsync(store, schema, "before", 5);
            JsonElement before = await StoreReportAsync(node, token);
            Assert.Equal("primary", RoleOf(before, store.PrimaryPort));
            Assert.Equal("standby", RoleOf(before, store.StandbyPort));

            // Condition 1: kill the primary and time the first write the server accepts.
            Stopwatch clock = Stopwatch.StartNew();
            await store.KillAsync(store.PrimaryData);
            int attempt = 0;
            TimeSpan firstWrite = TimeSpan.Zero;

            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                if (await TryWriteAsync(node, token, "after", attempt++))
                {
                    firstWrite = clock.Elapsed;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(firstWrite > TimeSpan.Zero, $"No write succeeded within 90 s of the kill.\n{node.Log}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Killed primary to first accepted write: {firstWrite.TotalSeconds:0.00} s ({attempt} attempts)."));

            Assert.False(await StandbyStore.ScalarAsync<bool>(store.StandbyPort, "select pg_is_in_recovery()", "postgres"),
                "The standby was not promoted.");
            JsonElement after = await StoreReportAsync(node, token);
            Assert.Equal("primary", RoleOf(after, store.StandbyPort));
            JsonElement last = after.GetProperty("lastFailover");
            Assert.Equal("automatic", last.GetProperty("how").GetString());
            Assert.Equal("ha-one", last.GetProperty("decidedBy").GetString());
            Assert.Equal(2, last.GetProperty("timeline").GetInt32());
            Assert.Equal(1L, await StandbyStore.ScalarAsync<long>(store.StandbyPort, $"select count(*) from {schema}.store_failover"));

            // Condition 2: the old primary returns as it was -- read-write, timeline 1, first in the connection string.
            await store.StartAsync(store.PrimaryData);
            Assert.False(await StandbyStore.ScalarAsync<bool>(store.PrimaryPort, "select pg_is_in_recovery()", "postgres"));
            long onOldBefore = await CountAsync(store, store.PrimaryPort, schema, "ha-%");

            for (int i = 0; i < 20; i++)
            {
                await WriteAsync(node, token, "returned", i);
            }

            Assert.Equal(onOldBefore, await CountAsync(store, store.PrimaryPort, schema, "ha-%"));
            Assert.Equal(20L, await CountAsync(store, store.StandbyPort, schema, "ha-returned-%"));
            await TwoNodeSite.Node.UntilAsync(
                () => StandbyStore.ScalarAsync<string>(store.PrimaryPort, "show default_transaction_read_only", "postgres"),
                fenced => fenced == "on", TimeSpan.FromSeconds(10), "The old primary being fenced");
            Assert.Equal("fenced", RoleOf(await StoreReportAsync(node, token), store.PrimaryPort));
            Assert.Contains("came back as a primary on timeline 1", node.Log, StringComparison.Ordinal);

            // Condition 4: rewound against the new primary, it is its standby and streams; writes keep going.
            await store.StopAsync(store.PrimaryData);
            StandbyStore.Relay back = store.RelayTo(store.StandbyPort);
            await store.RejoinAsync(store.PrimaryData, store.PrimaryPort, store.StandbyPort, back);
            await StandbyStore.UntilStreamingAsync(store.PrimaryPort, TimeSpan.FromSeconds(30));
            Assert.True(await StandbyStore.ScalarAsync<bool>(store.PrimaryPort, "select pg_is_in_recovery()", "postgres"));

            for (int i = 0; i < 5; i++)
            {
                await WriteAsync(node, token, "rejoined", i);
            }

            await TwoNodeSite.Node.UntilAsync(
                () => CountAsync(store, store.PrimaryPort, schema, "ha-rejoined-%"),
                n => n == 5, TimeSpan.FromSeconds(10), "The rejoined standby receiving the writes");
            Assert.Equal(20L, await CountAsync(store, store.PrimaryPort, schema, "ha-returned-%"));
            await TwoNodeSite.Node.UntilAsync(
                async () => RoleOf(await StoreReportAsync(node, token), store.PrimaryPort),
                role => role == "standby", TimeSpan.FromSeconds(10), "The report naming the rejoined host a standby");
        }
        finally
        {
            await DisposeQuietlyAsync(site);
        }
    }

    /// <summary>
    /// Condition 1 and §5.4 on a site of two nodes: both lose the primary, ask each other, agree, and one promotes;
    /// both write to the new primary.
    /// </summary>
    [Fact]
    public async Task Two_nodes_that_both_lose_the_primary_agree_and_promote_the_standby()
    {
        await using StandbyStore store = await StandbyStore.StartAsync();
        TwoNodeSite site = new(build: null, migrate: true, database: store.Hosts());

        try
        {
            await site.InitializeAsync();
            TwoNodeSite.Node a = await site.StartAsync("ha-a", TwoNodes);
            TwoNodeSite.Node b = await site.StartAsync("ha-b", TwoNodes);
            string ta = await a.SignInAsync();
            string tb = await b.SignInAsync();

            // Each must have read the other as a peer from the store before the primary goes.
            await Task.Delay(TimeSpan.FromSeconds(12));

            Stopwatch clock = Stopwatch.StartNew();
            await store.KillAsync(store.PrimaryData);
            TimeSpan firstWrite = TimeSpan.Zero;
            int attempt = 0;

            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                if (await TryWriteAsync(a, ta, "two", attempt++))
                {
                    firstWrite = clock.Elapsed;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(firstWrite > TimeSpan.Zero, $"No write succeeded within 90 s.\nA:\n{a.Log}\nB:\n{b.Log}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Two nodes: killed primary to first accepted write: {firstWrite.TotalSeconds:0.00} s."));

            await TwoNodeSite.Node.UntilAsync(() => TryWriteAsync(b, tb, "two-b", 0), done => done, TimeSpan.FromSeconds(20),
                "A write through the second node");
            Assert.Equal(1L, await StandbyStore.ScalarAsync<long>(store.StandbyPort, $"select count(*) from {site.Schema}.store_failover"));
        }
        finally
        {
            await DisposeQuietlyAsync(site);
        }
    }

    /// <summary>
    /// Condition 3: a node that loses the primary while the standby still streams from it promotes nothing — the
    /// standby's vote says the primary is alive — and writes resume on the same primary once the path mends.
    /// </summary>
    [Fact]
    public async Task A_node_cut_from_a_live_primary_holds_while_the_standby_still_streams()
    {
        await using StandbyStore store = await StandbyStore.StartAsync();
        StandbyStore.Relay path = store.RelayTo(store.PrimaryPort);
        TwoNodeSite site = new(build: null, migrate: true, database: store.Hosts(primaryVia: path.Port));

        try
        {
            await site.InitializeAsync();
            TwoNodeSite.Node node = await site.StartAsync("ha-cut");
            string token = await node.SignInAsync();
            await WriteAsync(node, token, "held", 0);

            path.Cut();
            (int status, string body) = await TwoNodeSite.Node.UntilAsync(
                () => node.SendAsync(HttpMethod.Get, "/healthz/ready", null),
                r => r.Status == 503 && r.Body.Contains("still streams", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20), "Readiness naming the held failover");
            Assert.Contains("\"state\":\"store-failover\"", body, StringComparison.Ordinal);
            Assert.Equal(503, status);

            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.True(await StandbyStore.ScalarAsync<bool>(store.StandbyPort, "select pg_is_in_recovery()", "postgres"),
                "The standby was promoted beside a live primary.");

            path.Mend();

            // The vote holds the primary out for its 30 s (§5.4); then writes go back to it.
            await TwoNodeSite.Node.UntilAsync(() => TryWriteAsync(node, token, "mended", 0), done => done,
                TimeSpan.FromSeconds(60), "A write once the path mended");
            Assert.Equal(1L, await CountAsync(store, store.PrimaryPort, site.Schema, "ha-mended-%"));
        }
        finally
        {
            await DisposeQuietlyAsync(site);
        }
    }

    /// <summary>
    /// Condition 3 on two nodes: the standby has lost the primary too, but the other node still reaches it — so the
    /// node cut off holds, naming the node that disagrees, and nothing is promoted.
    /// </summary>
    [Fact]
    public async Task A_node_that_still_reaches_the_primary_stops_the_other_from_promoting()
    {
        await using StandbyStore store = await StandbyStore.StartAsync();
        StandbyStore.Relay path = store.RelayTo(store.PrimaryPort);

        // Node A reaches the primary through the relay; node B directly. Both read the same store.
        Func<string> logs = () => "";
        TwoNodeSite site = new(build: null, migrate: true, database: store.Hosts());

        try
        {
            await site.InitializeAsync();
            TwoNodeSite.Node a = await site.StartAsync("ha-cut-a", new Dictionary<string, string>(TwoNodes)
            {
                ["Graticula__PlatformStore"] = new Npgsql.NpgsqlConnectionStringBuilder(store.Hosts(primaryVia: path.Port))
                {
                    SearchPath = site.Schema,
                }.ConnectionString,
            });
            TwoNodeSite.Node b = await site.StartAsync("ha-live-b", TwoNodes);
            logs = () => "A:" + Environment.NewLine + a.Log + Environment.NewLine + "B:" + Environment.NewLine + b.Log;
            await Task.Delay(TimeSpan.FromSeconds(12));

            path.Cut();
            store.StandbyLink.Cut();

            await TwoNodeSite.Node.UntilAsync(
                () => a.SendAsync(HttpMethod.Get, "/healthz/ready", null),
                r => r.Status == 503 && r.Body.Contains("node 'ha-live-b' still reaches the primary", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30), "Node A holding the failover because node B disagrees");
            Assert.Contains("still reaches the primary", a.Log, StringComparison.Ordinal);
            Assert.True(await StandbyStore.ScalarAsync<bool>(store.StandbyPort, "select pg_is_in_recovery()", "postgres"),
                "The standby was promoted while a node still wrote to the primary.");

            string tb = await b.SignInAsync();
            Assert.True(await TryWriteAsync(b, tb, "live", 0), "Node B, which reaches the primary, could not write.");
        }
        catch
        {
            output.WriteLine(logs());
            throw;
        }
        finally
        {
            store.StandbyLink.Mend();
            await DisposeQuietlyAsync(site);
        }
    }

    /// <summary>
    /// §5.7: with <c>Graticula:StoreFailover=manual</c> a lost primary is not replaced until an administrator promotes
    /// the standby with <c>graticula store promote</c> — refused while the primary answers, done once it is gone, and
    /// recorded as manual. <b>A command, because this test found the admin route could never work</b>: it answered 401,
    /// since with the primary gone no session can be read.
    /// </summary>
    [Fact]
    public async Task A_manual_site_waits_for_an_administrator_to_promote_the_standby()
    {
        await using StandbyStore store = await StandbyStore.StartAsync();
        TwoNodeSite site = new(build: null, migrate: true, database: store.Hosts());

        try
        {
            await site.InitializeAsync();
            TwoNodeSite.Node node = await site.StartAsync("ha-manual", new Dictionary<string, string> { ["Graticula__StoreFailover"] = "manual" });
            string token = await node.SignInAsync();

            (int refused, string said) = await site.RunAsync(["store", "promote"]);
            Assert.True(refused == 1, $"store promote beside a live primary exited {refused}: {said}");
            Assert.Contains("can still write to the primary", said, StringComparison.Ordinal);

            await store.KillAsync(store.PrimaryData);
            await TwoNodeSite.Node.UntilAsync(
                () => node.SendAsync(HttpMethod.Get, "/healthz/ready", null),
                r => r.Status == 503 && r.Body.Contains("manual", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20), "Readiness saying the failover waits for an administrator");
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.True(await StandbyStore.ScalarAsync<bool>(store.StandbyPort, "select pg_is_in_recovery()", "postgres"),
                "A manual site promoted on its own.");

            (int promoted, string body) = await site.RunAsync(["store", "promote"]);
            Assert.True(promoted == 0, $"store promote exited {promoted}: {body}");
            Assert.Contains("is the primary now, on timeline 2", body, StringComparison.Ordinal);

            await TwoNodeSite.Node.UntilAsync(() => TryWriteAsync(node, token, "manual", 0), done => done,
                TimeSpan.FromSeconds(30), "A write after the administrator promoted");
            Assert.Equal("manual", await StandbyStore.ScalarAsync<string>(store.StandbyPort, $"select how from {site.Schema}.store_failover"));
        }
        finally
        {
            await DisposeQuietlyAsync(site);
        }
    }

    private static async Task<bool> TryWriteAsync(TwoNodeSite.Node node, string token, string what, int i)
    {
        try
        {
            (int status, _) = await node.SendAsync(HttpMethod.Post, "/admin/groups", token, new
            {
                name = string.Create(CultureInfo.InvariantCulture, $"ha-{what}-{i}-{Guid.NewGuid():N}")[..40],
                title = "ADR-319 " + what,
            });
            return status is >= 200 and < 300;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task WriteAsync(TwoNodeSite.Node node, string token, string what, int i)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"ha-{what}-{i}-{Guid.NewGuid():N}")[..40];
        (int status, string body) = await node.SendAsync(HttpMethod.Post, "/admin/groups", token, new { name, title = "ADR-319 " + what });
        Assert.True(status is >= 200 and < 300, $"Writing {name} through {node.Name} answered {status}: {body}");
    }

    private static Task<long> CountAsync(StandbyStore store, int port, string schema, string like) =>
        StandbyStore.ScalarAsync<long>(port, $"select count(*) from {schema}.sharing_group where name like '{like}'");

    private static async Task StreamedAsync(StandbyStore store, string schema, string what, long expected) =>
        await TwoNodeSite.Node.UntilAsync(() => CountAsync(store, store.StandbyPort, schema, $"ha-{what}-%"),
            n => n == expected, TimeSpan.FromSeconds(10), "The standby receiving the writes");

    private static async Task<JsonElement> StoreReportAsync(TwoNodeSite.Node node, string token)
    {
        (int status, string body) = await node.SendAsync(HttpMethod.Get, "/admin/site/store", token);
        Assert.True(status == 200, $"/admin/site/store answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string? RoleOf(JsonElement report, int port)
    {
        foreach (JsonElement host in report.GetProperty("hosts").EnumerateArray())
        {
            if (host.GetProperty("address").GetString()!.EndsWith(":" + port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return host.GetProperty("role").GetString();
            }
        }

        return null;
    }

    private static async Task DisposeQuietlyAsync(TwoNodeSite site)
    {
        try
        {
            await site.DisposeAsync();
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The schema lives in clusters this test stops and deletes anyway.
        }
    }
}
