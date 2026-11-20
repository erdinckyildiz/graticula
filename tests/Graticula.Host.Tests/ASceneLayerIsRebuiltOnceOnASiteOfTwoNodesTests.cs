using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Two nodes of one site whose scene rebuild sweep looks every few seconds — <see cref="TwoNodeSite"/>.</summary>
public sealed class TwoSweepingNodes : IAsyncLifetime
{
    /// <summary>How often each node's sweep looks, in seconds: short, so a test sees a rebuild in seconds.</summary>
    public const int Seconds = 2;

    /// <summary>The site.</summary>
    public TwoNodeSite Site { get; } = new();

    /// <summary>The first node, which set the site up.</summary>
    public TwoNodeSite.Node A { get; private set; } = null!;

    /// <summary>The second.</summary>
    public TwoNodeSite.Node B { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Dictionary<string, string> settings = new()
        {
            ["Graticula__Nodes"] = "2",
            ["Graticula__SceneRebuildSeconds"] = Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await Site.InitializeAsync();
        A = await Site.StartAsync("scene-a", settings);
        B = await Site.StartAsync("scene-b", settings);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Site.DisposeAsync();
}

/// <summary>
/// ADR-277 condition 2: a scene layer whose owner asked is made again after its layer changes, <b>and once only on a site
/// of two nodes</b>. Both nodes run <see cref="SceneRebuilds"/>, each looking every two seconds; only the one holding the
/// site's <c>scene-rebuilds</c> lease (ADR-012) may make it.
/// </summary>
/// <remarks>
/// <b>The control</b>, run by hand on 2026-11-20 rather than kept: with the lease's question taken out of
/// <see cref="SceneRebuilds"/> (every node sweeping), the same test failed, both nodes saying they had made the layer
/// again. The edit is made on the node that does <i>not</i> lead the sweep, so the change has to reach the other through
/// the store, as it would between two machines.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed partial class ASceneLayerIsRebuiltOnceOnASiteOfTwoNodesTests(TwoSweepingNodes nodes) : IClassFixture<TwoSweepingNodes>
{
    private TwoNodeSite.Node A => nodes.A;

    private TwoNodeSite.Node B => nodes.B;

    /// <summary>What <see cref="SceneRebuilds"/> logs when it has made a scene layer again (event 1450).</summary>
    [GeneratedRegex(@"Scene layer '(?<title>[^']+)' was made again from its changed layer")]
    private static partial Regex Rebuilt();

    private static int RebuildsOf(TwoNodeSite.Node node, string title) =>
        Rebuilt().Matches(node.Log).Count(m => m.Groups["title"].Value == title);

    private static async Task<string[]> LeadsAsync(TwoNodeSite.Node on, string token, string machine)
    {
        (int status, string body) = await on.SendAsync(HttpMethod.Get, "/admin/site/machines", token);
        Assert.True(status == 200, $"/admin/site/machines answered {status}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("machines").EnumerateArray()
            .Where(m => m.GetProperty("name").GetString() == machine)
            .SelectMany(m => m.GetProperty("leads").EnumerateArray().Select(l => l.GetString()!))];
    }

    /// <summary>
    /// A point added on the node that does not lead the sweep: the leader makes the scene layer again within seconds, and
    /// across several more passes of both nodes it is made exactly once, by the leader alone.
    /// </summary>
    [Fact]
    public async Task A_changed_layer_makes_its_scene_layer_again_once_on_the_node_that_leads_the_sweep()
    {
        string ta = await A.SignInAsync();
        string name = "zz_scene_sweep_" + Guid.NewGuid().ToString("N")[..6];
        string title = "zz two-node sweep " + name;

        (int made, string said) = await A.SendAsync(HttpMethod.Post, "/admin/hosted/define", ta, new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "Text" } },
            sharing = "private",
        });
        Assert.True(made is 200 or 201, $"defining {name} answered {made}: {said}");
        await AddPointAsync(A, ta, name, 29.0, 41.0);

        (int status, string body) = await A.SendAsync(HttpMethod.Post, "/content/scenes", ta,
            new { service = name, folder = "hosted", layer = 0, title });
        Assert.True(status == 201, $"making the scene layer answered {status}: {body}");
        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        (status, body) = await A.SendAsync(HttpMethod.Put, $"/content/scenes/{id}/source", ta, new { autoRebuild = true });
        Assert.True(status == 200, $"asking for the automatic rebuild answered {status}: {body}");

        // Which node leads the sweep: the one whose passes may rebuild. Its lease is taken on its first pass.
        string[] leadsA = [], leadsB = [];
        await TwoNodeSite.Node.UntilAsync(
            async () =>
            {
                leadsA = await LeadsAsync(A, ta, A.Name);
                leadsB = await LeadsAsync(A, ta, B.Name);
                return leadsA.Contains(SiteSingletons.SceneRebuilds) || leadsB.Contains(SiteSingletons.SceneRebuilds);
            },
            taken => taken, TimeSpan.FromSeconds(60), "a node taking the scene-rebuilds lease");
        bool aLeads = leadsA.Contains(SiteSingletons.SceneRebuilds);
        Assert.False(aLeads && leadsB.Contains(SiteSingletons.SceneRebuilds), "Both nodes say they lead the scene rebuild sweep.");
        TwoNodeSite.Node leader = aLeads ? A : B, other = aLeads ? B : A;

        // Nothing has changed since the build: neither node makes it again.
        await Task.Delay(TimeSpan.FromSeconds(3 * TwoSweepingNodes.Seconds));
        Assert.Equal(0, RebuildsOf(A, title) + RebuildsOf(B, title));

        // The layer changes, through the node that does not lead the sweep.
        string to = await other.SignInAsync();
        await AddPointAsync(other, to, name, 29.001, 41.001);

        JsonElement automatic = await TwoNodeSite.Node.UntilAsync(
            async () =>
            {
                (int read, string source) = await A.SendAsync(HttpMethod.Get, $"/content/scenes/{id}/source", ta);
                Assert.True(read == 200, $"the scene source answered {read}: {source}");
                return JsonDocument.Parse(source).RootElement.Clone();
            },
            s => s.TryGetProperty("lastAutomatic", out JsonElement last) && last.ValueKind == JsonValueKind.Object,
            TimeSpan.FromSeconds(60), "the scene layer being made again on its own");
        JsonElement last = automatic.GetProperty("lastAutomatic");
        Assert.True(last.GetProperty("made").GetBoolean(), last.GetRawText());

        // Several more passes of both nodes: still once, and by the leader.
        await Task.Delay(TimeSpan.FromSeconds(5 * TwoSweepingNodes.Seconds));
        Assert.True(RebuildsOf(leader, title) == 1,
            $"{leader.Name}, which leads the sweep, made the scene layer again {RebuildsOf(leader, title)} times; once was expected.\n{leader.Log}");
        Assert.True(RebuildsOf(other, title) == 0,
            $"{other.Name}, which does not lead the sweep, made the scene layer again {RebuildsOf(other, title)} times.\n{other.Log}");

        await A.SendAsync(HttpMethod.Delete, $"/content/documents/{id}", ta);
    }

    private static async Task AddPointAsync(TwoNodeSite.Node on, string token, string name, double x, double y)
    {
        using HttpRequestMessage add = new(HttpMethod.Post, $"/rest/services/hosted/{name}/FeatureServer/0/addFeatures")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["features"] = JsonSerializer.Serialize(new[]
                {
                    new { geometry = new { x, y, spatialReference = new { wkid = 4326 } }, attributes = new { label = "p" } },
                }),
            }),
        };
        add.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage answer = await on.Http.SendAsync(add);
        string body = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.IsSuccessStatusCode && body.Contains("\"success\":true", StringComparison.Ordinal),
            $"addFeatures on {on.Name} answered {(int)answer.StatusCode}: {body}");
    }
}
