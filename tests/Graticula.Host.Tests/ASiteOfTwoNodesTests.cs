using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Two nodes of one site, started once for the class — <see cref="TwoNodeSite"/>.</summary>
public sealed class TwoStartedNodes : IAsyncLifetime
{
    /// <summary>The site.</summary>
    public TwoNodeSite Site { get; } = new();

    /// <summary>The first node, which set the site up.</summary>
    public TwoNodeSite.Node A { get; private set; } = null!;

    /// <summary>The second.</summary>
    public TwoNodeSite.Node B { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        await Site.InitializeAsync();
        A = await Site.StartAsync("node-a", new Dictionary<string, string> { ["Graticula__Nodes"] = "2" });
        B = await Site.StartAsync("node-b", new Dictionary<string, string> { ["Graticula__Nodes"] = "2" });
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Site.DisposeAsync();
}

/// <summary>
/// ADR-012 §5 phase 0 — a node that is ready to have a sibling — each item against two real processes over one store
/// (condition 3).
/// </summary>
/// <remarks>
/// <b>Each of these fails on the build before phase 0</b>, where there is no <c>site_node</c>, no
/// <c>/admin/site/machines</c>, no advisory lock and no announcement: the two processes are two strangers who happen to
/// share a database, which is what condition 2's prototype observed on 2026-10-31.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ASiteOfTwoNodesTests(TwoStartedNodes nodes) : IClassFixture<TwoStartedNodes>
{
    private TwoNodeSite.Node A => nodes.A;

    private TwoNodeSite.Node B => nodes.B;

    private TwoNodeSite Site => nodes.Site;

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private static async Task<JsonElement> MachinesAsync(TwoNodeSite.Node on)
    {
        string token = await on.SignInAsync();
        (int status, string body) = await on.SendAsync(HttpMethod.Get, "/admin/site/machines", token);
        Assert.True(status == 200, $"/admin/site/machines on {on.Name} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>Item 6: both machines, up, each as it says itself — read the same from either.</summary>
    [Fact]
    public async Task Either_node_lists_both_machines_up_and_started()
    {
        foreach (TwoNodeSite.Node on in (TwoNodeSite.Node[])[A, B])
        {
            JsonElement site = await MachinesAsync(on);
            Assert.Equal(on.Name, site.GetProperty("thisMachine").GetString());

            Dictionary<string, JsonElement> machines = site.GetProperty("machines").EnumerateArray()
                .ToDictionary(m => m.GetProperty("name").GetString()!);

            foreach (string name in (string[])["node-a", "node-b"])
            {
                Assert.True(machines.ContainsKey(name), $"{on.Name} does not list {name}: {site}");
                Assert.Equal("up", machines[name].GetProperty("status").GetString());
                Assert.Equal("started", machines[name].GetProperty("configuredState").GetString());
                Assert.False(string.IsNullOrEmpty(machines[name].GetProperty("version").GetString()));
            }
        }
    }

    /// <summary>
    /// Item 1: every site sweep that has been taken is held by exactly one node, and the two nodes' leads never overlap —
    /// without it both run every sweep on their first pass.
    /// </summary>
    [Fact]
    public async Task Each_site_sweep_runs_on_one_node_only()
    {
        // The sweeps take their locks on their first pass, at start; the overviews' pass waits twenty seconds.
        JsonElement site = await TwoNodeSite.Node.UntilAsync(
            () => MachinesAsync(A),
            s => s.GetProperty("machines").EnumerateArray().Sum(m => m.GetProperty("leads").GetArrayLength()) >= 4,
            TimeSpan.FromSeconds(40),
            "the sweeps being taken");

        List<string> all = [];
        int leaders = 0;

        foreach (JsonElement machine in site.GetProperty("machines").EnumerateArray())
        {
            string[] leads = [.. machine.GetProperty("leads").EnumerateArray().Select(l => l.GetString()!)];
            all.AddRange(leads);
            leaders += leads.Length > 0 ? 1 : 0;
        }

        Assert.Equal(all.Count, all.Distinct().Count());

        // And in the store: one backend per sweep's lock, never two.
        long locks = (long)(await Site.ScalarAsync(
            """
            select count(*) from pg_locks
             where locktype = 'advisory' and objsubid = 2 and classid = hashtext(current_schema())::oid
               and objid in (select hashtext('graticula.singleton:' || s)::oid
                               from unnest(array['log-retention','history-retention','attachment-sweep','recycle-expiry',
                                                 'coverage-pyramids','backup-schedule']) s)
            """))!;
        Assert.Equal(all.Count, (int)locks);
    }

    /// <summary>
    /// Items 4, 6 and 12: an administrator stops a machine from the other one; it answers not ready at once on
    /// <c>/healthz/ready</c> and <c>/rest/info/healthcheck</c>, the other stays ready, and started again it is ready again.
    /// </summary>
    [Fact]
    public async Task A_machine_stopped_from_the_other_answers_not_ready_until_started()
    {
        string token = await A.SignInAsync();

        await TwoNodeSite.Node.UntilAsync(
            async () => (await B.SendAsync(HttpMethod.Get, "/healthz/ready", null)).Status, s => s == 200, TimeSpan.FromSeconds(30),
            "node-b becoming ready");

        (int status, string body) = await A.SendAsync(HttpMethod.Put, "/admin/site/machines/node-b", token, new { state = "stopped" });
        Assert.True(status == 200, $"stopping node-b answered {status}: {body}");

        try
        {
            await TwoNodeSite.Node.UntilAsync(
                async () => (await B.SendAsync(HttpMethod.Get, "/healthz/ready", null)).Status, s => s == 503, Soon,
                "node-b answering not ready");

            (int check, string said) = await B.SendAsync(HttpMethod.Get, "/rest/info/healthcheck", null);
            Assert.Equal(503, check);
            Assert.False(JsonDocument.Parse(said).RootElement.GetProperty("success").GetBoolean());

            Assert.Equal(200, (await A.SendAsync(HttpMethod.Get, "/healthz/ready", null)).Status);
            (int fine, string ok) = await A.SendAsync(HttpMethod.Get, "/rest/info/healthcheck", null);
            Assert.Equal(200, fine);
            Assert.True(JsonDocument.Parse(ok).RootElement.GetProperty("success").GetBoolean());

            // A stopped machine runs none of the site's sweeps.
            JsonElement site = await MachinesAsync(A);
            JsonElement b = site.GetProperty("machines").EnumerateArray().First(m => m.GetProperty("name").GetString() == "node-b");
            Assert.Equal("stopped", b.GetProperty("configuredState").GetString());
        }
        finally
        {
            await A.SendAsync(HttpMethod.Put, "/admin/site/machines/node-b", token, new { state = "started" });
        }

        await TwoNodeSite.Node.UntilAsync(
            async () => (await B.SendAsync(HttpMethod.Get, "/healthz/ready", null)).Status, s => s == 200, Soon,
            "node-b answering ready again");
    }

    /// <summary>Item 6: a machine that is up cannot be removed; ArcGIS unregisters only a machine that is gone.</summary>
    [Fact]
    public async Task A_machine_that_is_up_is_not_removed()
    {
        string token = await A.SignInAsync();
        (int status, string body) = await A.SendAsync(HttpMethod.Delete, "/admin/site/machines/node-b", token);
        Assert.True(status == 409, $"removing a running machine answered {status}: {body}");
    }

    /// <summary>Item 6, ArcGIS's same account on every machine: a node with another key is refused at start.</summary>
    [Fact]
    public async Task A_node_with_another_key_is_refused_at_start()
    {
        (int? exit, string said) = await Site.StartRefusedAsync("node-wrong-key", new Dictionary<string, string>
        {
            ["Graticula__SecretKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["Graticula__Nodes"] = "2",
        });

        Assert.True(exit is not null and not 0, $"a node with another key started:\n{said}");
        Assert.Contains("different Graticula:SecretKey", said, StringComparison.Ordinal);
    }

    /// <summary>Item 11: a node whose settings differ from the site's is refused at start, and told which and how.</summary>
    [Fact]
    public async Task A_node_whose_settings_differ_is_refused_and_told_which()
    {
        (int? exit, string said) = await Site.StartRefusedAsync("node-other-settings", new Dictionary<string, string>
        {
            ["Graticula__Nodes"] = "2",
            ["Graticula__TileCacheBudgetMB"] = "777",
        });

        Assert.True(exit is not null and not 0, $"a node with other settings started:\n{said}");
        Assert.Contains("Graticula:TileCacheBudgetMB is '777' here and unset on node-", said, StringComparison.Ordinal);
    }

    /// <summary>Item 6: a second process under a running node's name is refused, rather than becoming half of one machine.</summary>
    [Fact]
    public async Task A_second_process_under_a_running_nodes_name_is_refused()
    {
        (int? exit, string said) = await Site.StartRefusedAsync("node-a", new Dictionary<string, string> { ["Graticula__Nodes"] = "2" });

        Assert.True(exit is not null and not 0, $"a second node-a started:\n{said}");
        Assert.Contains("A node named 'node-a' is already running", said, StringComparison.Ordinal);
    }

    /// <summary>Item 3: the connection budget is the site's — both nodes' pools against one database.</summary>
    [Fact]
    public void Each_node_compares_the_whole_sites_connections_with_the_database()
    {
        Assert.Contains("This site's 2 nodes may hold up to", A.Log, StringComparison.Ordinal);
        Assert.Contains("This site's 2 nodes may hold up to", B.Log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Item 2: a change made on one node reaches the other at once — the organisation's name, read through whoami, which
    /// the other node otherwise kept until it restarted.
    /// </summary>
    [Fact]
    public async Task An_organisation_renamed_on_one_node_is_renamed_on_the_other_at_once()
    {
        string ta = await A.SignInAsync();
        string tb = await B.SignInAsync();
        string renamed = "Renamed " + Guid.NewGuid().ToString("N")[..6];

        (int status, string body) = await A.SendAsync(HttpMethod.Put, "/admin/tenants/default", ta, new { name = renamed });
        Assert.True(status == 200, $"renaming answered {status}: {body}");

        try
        {
            await TwoNodeSite.Node.UntilAsync(
                async () => JsonDocument.Parse((await B.SendAsync(HttpMethod.Get, "/rest/whoami", tb)).Body)
                    .RootElement.GetProperty("organisation").GetProperty("name").GetString(),
                name => name == renamed, Soon, "node-b saying the new name");
        }
        finally
        {
            await A.SendAsync(HttpMethod.Put, "/admin/tenants/default", ta, new { name = "Default" });
        }
    }

    /// <summary>
    /// Items 2 and 5: a pin made on one node wakes the other at once, and each node's own warm time is kept — so the
    /// service says it is kept warm on both.
    /// </summary>
    [Fact]
    public async Task A_pin_made_on_one_node_is_warmed_by_both_at_once()
    {
        string token = await A.SignInAsync();
        string name = "zz_pin_" + Guid.NewGuid().ToString("N")[..6];

        (int made, string said) = await A.SendAsync(HttpMethod.Post, "/admin/hosted/define", token, new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "Text" } },
            sharing = "private",
        });
        Assert.True(made is 200 or 201, $"defining {name} answered {made}: {said}");

        (int pinned, string state) = await A.SendAsync(HttpMethod.Put, $"/admin/services/{name}/pinned?folder=hosted", token, new { pinned = true });
        Assert.True(pinned == 200, $"pinning answered {pinned}: {state}");

        // Both nodes write their own row within a heartbeat of the wake — not a round of twenty seconds later.
        await TwoNodeSite.Node.UntilAsync(
            async () => (long)(await Site.ScalarAsync(
                $"select count(distinct w.node) from site_node_warm w join service s on s.id = w.service_id where s.name = '{name}'"))!,
            n => n == 2, TimeSpan.FromSeconds(14), "both nodes warming the pinned service");

        (int read, string pin) = await B.SendAsync(HttpMethod.Get, $"/admin/services/{name}/pinned?folder=hosted", await B.SignInAsync());
        Assert.Equal(200, read);
        JsonElement on = JsonDocument.Parse(pin).RootElement.GetProperty("nodes");
        Assert.Equal(2, on.GetProperty("warm").GetInt32());
        Assert.Equal(2, on.GetProperty("of").GetInt32());
    }
}
