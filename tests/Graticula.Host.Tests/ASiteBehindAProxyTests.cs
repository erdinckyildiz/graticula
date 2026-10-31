using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Two nodes of one site that trust the loopback address as their proxy — <see cref="TwoNodeSite"/>.</summary>
public sealed class TwoProxiedNodes : IAsyncLifetime
{
    /// <summary>The site.</summary>
    public TwoNodeSite Site { get; } = new();

    /// <summary>The first node.</summary>
    public TwoNodeSite.Node A { get; private set; } = null!;

    /// <summary>The second.</summary>
    public TwoNodeSite.Node B { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Dictionary<string, string> settings = new()
        {
            ["Graticula__Nodes"] = "2",
            ["Graticula__TrustedProxies"] = "127.0.0.1/32",
        };

        await Site.InitializeAsync();
        A = await Site.StartAsync("proxied-a", settings);
        B = await Site.StartAsync("proxied-b", settings);
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Site.DisposeAsync();
}

/// <summary>
/// ADR-012 §5 phase 3 — two nodes behind a proxy that ends TLS — the part a test can hold: the nodes honour the scheme and
/// host the proxy says the client used, from the proxy the deployment names and nobody else; and a balancer's health
/// check sends a stopped node nothing.
/// </summary>
/// <remarks>
/// <b>Caddy itself is not in this test</b>: it is not installed on the machine this was written on and downloading it
/// was refused, so the proxy is played by the test's own requests, sending what Caddy's <c>reverse_proxy</c> sends
/// (<c>X-Forwarded-Proto</c>, <c>X-Forwarded-Host</c>) from the loopback address. ADR-012 records what that leaves
/// unproven.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ASiteBehindAProxyTests(TwoProxiedNodes nodes) : IClassFixture<TwoProxiedNodes>
{
    private static async Task<JsonElement> InfoAsync(TwoNodeSite.Node on, string? proto, string? host)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/rest/info?f=json");

        if (proto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", proto);
        }

        if (host is not null)
        {
            request.Headers.Add("X-Forwarded-Host", host);
        }

        using HttpResponseMessage answer = await on.Http.SendAsync(request);
        string body = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.IsSuccessStatusCode, $"/rest/info on {on.Name} answered {(int)answer.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>Behind a proxy that ended TLS for gis.example.test, every node writes that address, not its own.</summary>
    [Fact]
    public async Task Every_node_writes_the_address_the_client_used_through_the_proxy()
    {
        foreach (TwoNodeSite.Node on in (TwoNodeSite.Node[])[nodes.A, nodes.B])
        {
            JsonElement info = await InfoAsync(on, "https", "gis.example.test");
            Assert.Equal("https://gis.example.test", info.GetProperty("owningSystemUrl").GetString());
            Assert.Equal("https://gis.example.test/rest/generateToken",
                info.GetProperty("authInfo").GetProperty("tokenServicesUrl").GetString());
        }
    }

    /// <summary>A balancer's two checks agree, and a node an administrator stops is out of both until it is started.</summary>
    [Fact]
    public async Task A_balancers_health_check_takes_a_stopped_node_out_and_puts_it_back()
    {
        string token = await nodes.A.SignInAsync();

        await TwoNodeSite.Node.UntilAsync(
            async () => (await nodes.B.SendAsync(HttpMethod.Get, "/rest/info/healthcheck", null)).Status, s => s == 200,
            TimeSpan.FromSeconds(30), "proxied-b passing the health check");

        await nodes.A.SendAsync(HttpMethod.Put, "/admin/site/machines/proxied-b", token, new { state = "maintenance" });

        try
        {
            await TwoNodeSite.Node.UntilAsync(
                async () => (await nodes.B.SendAsync(HttpMethod.Get, "/rest/info/healthcheck", null)).Status, s => s == 503,
                TimeSpan.FromSeconds(5), "proxied-b failing the health check");
            Assert.Equal(503, (await nodes.B.SendAsync(HttpMethod.Get, "/healthz/ready", null)).Status);
            Assert.Equal(200, (await nodes.A.SendAsync(HttpMethod.Get, "/rest/info/healthcheck", null)).Status);
        }
        finally
        {
            await nodes.A.SendAsync(HttpMethod.Put, "/admin/site/machines/proxied-b", token, new { state = "started" });
        }

        await TwoNodeSite.Node.UntilAsync(
            async () => (await nodes.B.SendAsync(HttpMethod.Get, "/rest/info/healthcheck", null)).Status, s => s == 200,
            TimeSpan.FromSeconds(5), "proxied-b passing the health check again");
    }
}
