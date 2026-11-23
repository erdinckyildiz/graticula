using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Host.Iwa;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>Two sites, one with Windows sign-in on and one without — ADR-316 condition 1.</summary>
public sealed class WindowsNodes : IAsyncLifetime
{
    private readonly List<TwoNodeSite> _sites = [];

    /// <summary>Windows sign-in turned on.</summary>
    public CertificateNodes.Server On { get; private set; } = null!;

    /// <summary>Not turned on: the provider is saved, and is no button.</summary>
    public CertificateNodes.Server Off { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        On = await StartAsync("iwa-on", ("Graticula__Iwa__Enabled", "true"));
        Off = await StartAsync("iwa-off");
    }

    private async Task<CertificateNodes.Server> StartAsync(string name, params (string Key, string Value)[] settings)
    {
        TwoNodeSite site = new();
        _sites.Add(site);
        await site.InitializeAsync();
        TwoNodeSite.Node node = await site.StartAsync(name, TestPki.Settings(settings));

        string token = await node.SignInAsync();
        (int status, string body) = await node.SendAsync(HttpMethod.Post, "/admin/identity-providers", token, new
        {
            kind = "iwa",
            name = "Windows",
            autoCreate = true,
            defaultRole = "viewer",
        });
        Assert.True(status == 201, $"Adding the Windows provider on {name} answered {status}: {body}");
        return new CertificateNodes.Server(site, node, JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, token);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        foreach (TwoNodeSite site in _sites)
        {
            await site.DisposeAsync();
        }
    }
}

/// <summary>
/// ADR-316: a member signs in with Windows through .NET's Negotiate handler — offered only where it is turned on,
/// challenging with <c>Negotiate</c>, and, on a Windows machine, signing the machine's own user in through SSPI.
/// </summary>
/// <remarks>
/// <b>A domain is not in this test</b>: Kerberos from a domain controller is ADR-316 condition 2, measured against Samba
/// AD. What runs here is the route, the button, the challenge and — on Windows — a whole Negotiate handshake with the
/// signed-in user's own credentials, which SSPI answers locally.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class AMemberSignsInWithWindowsTests(WindowsNodes nodes) : IClassFixture<WindowsNodes>
{
    [Fact]
    public async Task Turned_off_it_is_no_button_and_its_route_says_so()
    {
        (int status, string body) = await nodes.Off.Node.SendAsync(HttpMethod.Get, "/rest/auth/providers", null);
        Assert.Equal(200, status);
        Assert.DoesNotContain("/rest/auth/iwa/", body, StringComparison.Ordinal);

        (status, body) = await nodes.Off.Node.SendAsync(HttpMethod.Get, $"/rest/auth/iwa/{nodes.Off.ProviderId}/start", null);
        Assert.Equal(503, status);
        Assert.Contains("not set up", body, StringComparison.Ordinal);

        (status, body) = await nodes.Off.Node.SendAsync(HttpMethod.Post, $"/admin/identity-providers/{nodes.Off.ProviderId}/check", nodes.Off.Token);
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task Turned_on_it_is_a_button_and_challenges_with_negotiate()
    {
        (int status, string body) = await nodes.On.Node.SendAsync(HttpMethod.Get, "/rest/auth/providers", null);
        Assert.Equal(200, status);
        Assert.Contains($"/rest/auth/iwa/{nodes.On.ProviderId}/start", body, StringComparison.Ordinal);

        (status, body) = await nodes.On.Node.SendAsync(HttpMethod.Get, "/rest/login", null);
        Assert.Contains($"/rest/auth/iwa/{nodes.On.ProviderId}/start", body, StringComparison.Ordinal);

        using HttpClient anonymous = TestPki.Browser(nodes.On.Node.Port, null);
        using HttpRequestMessage start = new(HttpMethod.Get, $"/rest/auth/iwa/{nodes.On.ProviderId}/start") { Version = HttpVersion.Version11 };
        using HttpResponseMessage challenged = await anonymous.SendAsync(start);
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        Assert.Contains(challenged.Headers.WwwAuthenticate, h => h.Scheme == "Negotiate");
        Assert.Contains("Windows did not sign you in", await challenged.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        (status, body) = await nodes.On.Node.SendAsync(HttpMethod.Post, $"/admin/identity-providers/{nodes.On.ProviderId}/check", nodes.On.Token);
        Assert.Equal(200, status);
        Assert.Contains("\"iwa\":true", body, StringComparison.Ordinal);
    }

    /// <summary>One Windows sign-in on at a time: a second is refused while the first is on.</summary>
    [Fact]
    public async Task A_second_windows_sign_in_is_refused_while_one_is_on()
    {
        (int status, string body) = await nodes.On.Node.SendAsync(HttpMethod.Post, "/admin/identity-providers", nodes.On.Token, new
        {
            kind = "iwa",
            name = "Windows again",
            autoCreate = true,
            defaultRole = "viewer",
        });

        Assert.Equal(400, status);
        Assert.Contains("already signs people in with Windows", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// On Windows, the whole handshake: the test's own Windows user, through SSPI, signs in, is made an account named by
    /// the account alone, and is the session's member. Elsewhere there is no SSPI to answer, and condition 2 measures it.
    /// </summary>
    [Fact]
    public async Task On_windows_the_signed_in_user_signs_in_with_no_password()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CookieContainer jar = new();
#pragma warning disable CA5400, CA2000
        using HttpClient browser = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = jar,
            UseDefaultCredentials = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{nodes.On.Node.Port}"),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
#pragma warning restore CA5400, CA2000

        using HttpResponseMessage started = await browser.GetAsync(
            new Uri($"/rest/auth/iwa/{nodes.On.ProviderId}/start?return=/rest/whoami", UriKind.Relative));
        string body = await started.Content.ReadAsStringAsync();
        Assert.True(started.StatusCode == HttpStatusCode.Redirect, $"Windows sign-in answered {(int)started.StatusCode}: {body}");

        string who = await browser.GetStringAsync(new Uri("/rest/whoami", UriKind.Relative));
        string? name = JsonDocument.Parse(who).RootElement.GetProperty("name").GetString();
        Assert.Equal(WindowsSignIn.AccountName($"{Environment.UserDomainName}\\{Environment.UserName}"), name);
    }

    [Theory]
    [InlineData("CONTOSO\\jane", "jane")]
    [InlineData("jane@CONTOSO.COM", "jane")]
    [InlineData("jane", "jane")]
    public void An_account_is_named_by_the_windows_account_alone(string given, string expected) =>
        Assert.Equal(expected, WindowsSignIn.AccountName(given));
}
