using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A site whose nodes receive client certificates each in their own way — ADR-315 condition 1: through Kestrel's
/// handshake; through a trusted proxy's header; refusing what revocation cannot settle; and demanding one at the handshake.
/// </summary>
public sealed class CertificateNodes : IAsyncLifetime
{
    /// <summary>The header a proxy forwards the certificate in.</summary>
    public const string Header = "X-Client-Cert";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "graticula-pki-" + Guid.NewGuid().ToString("N")[..10]);

    /// <summary>One site per configuration: ADR-012 refuses a node whose settings differ from its site's.</summary>
    private readonly List<TwoNodeSite> _sites = [];

    private System.Security.Cryptography.X509Certificates.X509Certificate2? _harness;

    /// <summary>The deployment's certificate authority.</summary>
    public TestPki Pki { get; } = new("Graticula test CA");

    /// <summary>An authority this deployment does not trust.</summary>
    public TestPki Stranger { get; } = new("Somebody else's CA");

    /// <summary>Serves the deployment's CRL.</summary>
    public TestPki.CrlServer Crl { get; } = new();

    /// <summary>Kestrel asks for a certificate; a header is configured but its only trusted proxy is not the loopback.</summary>
    public Server Kestrel { get; private set; } = null!;

    /// <summary>No handshake certificate; the loopback is the trusted proxy that forwards one.</summary>
    public Server Proxied { get; private set; } = null!;

    /// <summary>Kestrel asks, and revocation that cannot be learned refuses.</summary>
    public Server Hard { get; private set; } = null!;

    /// <summary>Kestrel demands a certificate the authorities vouch for.</summary>
    public Server Require { get; private set; } = null!;

    /// <summary>A site of one node, its PKI provider and an administrator's token.</summary>
    /// <param name="Site">The site.</param>
    /// <param name="Node">Its node.</param>
    /// <param name="ProviderId">Its PKI provider.</param>
    /// <param name="Token">An administrator's token.</param>
    public sealed record Server(TwoNodeSite Site, TwoNodeSite.Node Node, string ProviderId, string Token);

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        string authorities = Pki.WriteTo(_directory);
        (string, string) trust = ("Graticula__ClientCertificates__Authorities", authorities);

        Kestrel = await StartAsync("pki-kestrel", trust,
            ("Graticula__ClientCertificates__Mode", "Allow"),
            ("Graticula__ClientCertificates__Header", Header),
            ("Graticula__TrustedProxies", "192.0.2.1/32"));
        Proxied = await StartAsync("pki-proxied", trust,
            ("Graticula__ClientCertificates__Header", Header),
            ("Graticula__TrustedProxies", "127.0.0.1/32"));
        Hard = await StartAsync("pki-hard", trust,
            ("Graticula__ClientCertificates__Mode", "Allow"),
            ("Graticula__ClientCertificates__Revocation", "HardFail"));
        Require = await StartAsync("pki-require", trust,
            ("Graticula__ClientCertificates__Mode", "Require"));
    }

    private async Task<Server> StartAsync(string name, params (string Key, string Value)[] settings)
    {
        TwoNodeSite site = new();
        _sites.Add(site);
        await site.InitializeAsync();

        // The harness's own client presents a certificate, so a node that requires one can be set up and asked about.
        _harness ??= Pki.Issue("Graticula test harness");
        TwoNodeSite.Node node = await site.StartAsync(name, TestPki.Settings(settings), clientCertificate: _harness);

        string token = await node.SignInAsync();
        (int status, string body) = await node.SendAsync(HttpMethod.Post, "/admin/identity-providers", token, new
        {
            kind = "pki",
            name = "Smart card",
            usernameClaim = "cn",
            autoCreate = true,
            defaultRole = "viewer",
        });
        Assert.True(status == 201, $"Adding the PKI provider on {name} answered {status}: {body}");
        return new Server(site, node, JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, token);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        foreach (TwoNodeSite site in _sites)
        {
            await site.DisposeAsync();
        }

        Crl.Dispose();
        Pki.Dispose();
        Stranger.Dispose();
        _harness?.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A node may still hold the file for a moment; it is a temporary directory.
        }
    }
}

/// <summary>
/// ADR-315: a member signs in with a client certificate — through Kestrel or a trusted proxy, mapped by the provider's
/// field, refused when nobody vouches for it — and ArcGIS's <c>generateToken</c> takes one while passwords keep working.
/// </summary>
/// <remarks>
/// <b>Every certificate is made in the test</b> by <see cref="TestPki"/>; the nodes are real server processes
/// (<see cref="TwoNodeSite"/>), because what is tested is Kestrel's handshake and a header's source address.
/// </remarks>
[Trait("Needs", "TwoNodes")]
public sealed class ACertificateSignsAMemberInTests(CertificateNodes nodes) : IClassFixture<CertificateNodes>
{
    /// <summary>Signs in as a browser would; the status, who <c>/rest/whoami</c> then says, and the page.</summary>
    private static async Task<(HttpStatusCode Status, string? Name, string Body)> SignInAsync(
        CertificateNodes.Server server, X509Certificate2? presented, string? header = null)
    {
        CookieContainer jar = new();
        using HttpClient browser = TestPki.Browser(server.Node.Port, presented, jar);
        using HttpRequestMessage start = new(HttpMethod.Get, $"/rest/auth/pki/{server.ProviderId}/start?return=/rest/whoami");

        if (header is not null)
        {
            start.Headers.Add(CertificateNodes.Header, header);
        }

        using HttpResponseMessage started = await browser.SendAsync(start);
        string body = await started.Content.ReadAsStringAsync();

        if (started.StatusCode != HttpStatusCode.Redirect)
        {
            return (started.StatusCode, null, body);
        }

        Assert.Equal("/rest/whoami", started.Headers.Location!.OriginalString);
        string who = await browser.GetStringAsync(new Uri("/rest/whoami", UriKind.Relative));
        return (started.StatusCode, JsonDocument.Parse(who).RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : null, who);
    }

    private static string Der(X509Certificate2 certificate) => Convert.ToBase64String(certificate.RawData);

    /// <summary>Through Kestrel: the certificate's CN makes the account at its first sign-in, and finds it at the next.</summary>
    [Fact]
    public async Task A_certificate_through_kestrel_signs_in_and_makes_the_account()
    {
        string cn = $"Ada Lovelace {Guid.NewGuid().ToString("N")[..6]}";
        using X509Certificate2 card = nodes.Pki.Issue(cn);

        (HttpStatusCode status, string? name, string body) = await SignInAsync(nodes.Kestrel, card);
        Assert.True(status == HttpStatusCode.Redirect, $"The sign-in answered {(int)status}: {body}");
        Assert.Equal(cn.Replace(' ', '.'), name);

        (status, name, _) = await SignInAsync(nodes.Kestrel, card);
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal(cn.Replace(' ', '.'), name);

        Assert.Equal(1L, await nodes.Kestrel.Site.ScalarAsync(
            $"select count(*) from external_identity where subject = '{cn.ToLowerInvariant()}'"));
    }

    /// <summary>Through a trusted proxy: the certificate in the header, from the loopback the node names, signs in.</summary>
    [Fact]
    public async Task A_certificate_a_trusted_proxy_forwards_signs_in()
    {
        string cn = $"Grace Hopper {Guid.NewGuid().ToString("N")[..6]}";
        using X509Certificate2 card = nodes.Pki.Issue(cn);

        (HttpStatusCode status, string? name, string body) = await SignInAsync(nodes.Proxied, null, Der(card));
        Assert.True(status == HttpStatusCode.Redirect, $"The sign-in answered {(int)status}: {body}");
        Assert.Equal(cn.Replace(' ', '.'), name);

        // PEM, and URL-encoded PEM as nginx's $ssl_client_escaped_cert sends it, are read too.
        (status, name, _) = await SignInAsync(nodes.Proxied, null, Uri.EscapeDataString(card.ExportCertificatePem()));
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal(cn.Replace(' ', '.'), name);
    }

    /// <summary>
    /// The header from a peer that is not a trusted proxy is refused — the one thing that keeps a public certificate from
    /// being a password everybody knows — and the log says so.
    /// </summary>
    [Fact]
    public async Task A_certificate_header_from_anybody_but_a_trusted_proxy_is_refused()
    {
        using X509Certificate2 card = nodes.Pki.Issue($"Mallory {Guid.NewGuid().ToString("N")[..6]}");

        (HttpStatusCode status, string? name, string body) = await SignInAsync(nodes.Kestrel, null, Der(card));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Null(name);
        Assert.Contains("not a trusted proxy", body, StringComparison.Ordinal);
        Assert.Contains("is not in Graticula:TrustedProxies, and was ignored", nodes.Kestrel.Node.Log, StringComparison.Ordinal);

        // And generateToken does not take it either.
        using HttpClient script = TestPki.Browser(nodes.Kestrel.Node.Port, null);
        using HttpRequestMessage token = new(HttpMethod.Post, "/rest/generateToken")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }),
        };
        token.Headers.Add(CertificateNodes.Header, Der(card));
        using HttpResponseMessage answer = await script.SendAsync(token);
        string said = await answer.Content.ReadAsStringAsync();
        Assert.False(answer.IsSuccessStatusCode, said);
        Assert.DoesNotContain("\"token\"", said, StringComparison.Ordinal);
    }

    /// <summary>Another authority's certificate, one past its date and one for servers only sign nobody in, and say why.</summary>
    [Fact]
    public async Task A_certificate_nobody_here_vouches_for_signs_nobody_in()
    {
        using X509Certificate2 foreign = nodes.Stranger.Issue("Eve Stranger");
        (HttpStatusCode status, _, string body) = await SignInAsync(nodes.Kestrel, foreign);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("not an authority this server trusts", body, StringComparison.Ordinal);

        (status, _, body) = await SignInAsync(nodes.Proxied, null, Der(foreign));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("not an authority this server trusts", body, StringComparison.Ordinal);

        using X509Certificate2 expired = nodes.Pki.Issue("Old Card", notAfter: DateTimeOffset.UtcNow.AddDays(-2));
        (status, _, body) = await SignInAsync(nodes.Proxied, null, Der(expired));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("not valid today", body, StringComparison.Ordinal);

        using X509Certificate2 server = nodes.Pki.Issue("A Web Server", clientAuth: false);
        (status, _, body) = await SignInAsync(nodes.Proxied, null, Der(server));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("not for signing in", body, StringComparison.Ordinal);

        (status, _, body) = await SignInAsync(nodes.Kestrel, null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("sent no certificate", body, StringComparison.Ordinal);
    }

    /// <summary>The provider's field names the account: the e-mail address, then the user principal name, then the CN.</summary>
    [Fact]
    public async Task Each_field_names_the_account()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        using X509Certificate2 card = nodes.Pki.Issue($"Katherine Johnson {tag}", email: $"kjohnson{tag}@nasa.test", upn: $"kj{tag}@nasa.test");
        using X509Certificate2 bare = nodes.Pki.Issue(null, email: $"nocn{tag}@nasa.test");

        try
        {
            await FieldAsync("email");
            (HttpStatusCode status, string? name, _) = await SignInAsync(nodes.Proxied, null, Der(card));
            Assert.Equal(HttpStatusCode.Redirect, status);
            Assert.Equal($"kjohnson{tag}", name);

            await FieldAsync("upn");
            (status, name, _) = await SignInAsync(nodes.Proxied, null, Der(card));
            Assert.Equal(HttpStatusCode.Redirect, status);
            Assert.Equal($"kj{tag}", name);

            (status, _, string body) = await SignInAsync(nodes.Proxied, null, Der(bare));
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Contains("no user principal name", body, StringComparison.Ordinal);
        }
        finally
        {
            await FieldAsync("cn");
        }

        (HttpStatusCode again, _, string said) = await SignInAsync(nodes.Proxied, null, Der(bare));
        Assert.Equal(HttpStatusCode.Unauthorized, again);
        Assert.Contains("no common name", said, StringComparison.Ordinal);

        async Task FieldAsync(string field)
        {
            (int status, string body) = await nodes.Proxied.Node.SendAsync(HttpMethod.Put, $"/admin/identity-providers/{nodes.Proxied.ProviderId}", nodes.Proxied.Token, new
            {
                kind = "pki",
                name = "Smart card",
                usernameClaim = field,
                autoCreate = true,
                defaultRole = "viewer",
            });
            Assert.True(status == 200, $"Setting the field to {field} answered {status}: {body}");
        }
    }

    /// <summary>
    /// A revoked certificate is refused; one whose CRL cannot be reached is accepted under SoftFail and refused under
    /// HardFail — ADR-315 §5.6.
    /// </summary>
    [Fact]
    public async Task Revocation_refuses_a_revoked_certificate_and_soft_fails_by_default()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        using X509Certificate2 revoked = nodes.Pki.Issue($"Revoked {tag}", crl: nodes.Crl.Point());
        nodes.Crl.Body = nodes.Pki.Crl(revoked);

        (HttpStatusCode status, _, string body) = await SignInAsync(nodes.Proxied, null, Der(revoked));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("revoked", body, StringComparison.Ordinal);

        using X509Certificate2 unknowable = nodes.Pki.Issue($"Offline {tag}", crl: TestPki.Unreachable());
        (status, _, body) = await SignInAsync(nodes.Kestrel, unknowable);
        Assert.True(status == HttpStatusCode.Redirect, $"SoftFail answered {(int)status}: {body}");

        (status, _, body) = await SignInAsync(nodes.Hard, unknowable);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("could not be learned", body, StringComparison.Ordinal);

        using X509Certificate2 current = nodes.Pki.Issue($"Current {tag}", crl: nodes.Crl.Point());
        (status, _, body) = await SignInAsync(nodes.Hard, current);
        Assert.True(status == HttpStatusCode.Redirect, $"A certificate its CRL does not list answered {(int)status}: {body}");
    }

    /// <summary>
    /// ArcGIS's <c>generateToken</c>: no name, no password and a certificate give an ArcGIS token for the account; with a
    /// name and password, on a node that asks every connection for a certificate, a client that has none still gets one.
    /// </summary>
    [Fact]
    public async Task GenerateToken_takes_a_certificate_and_still_takes_a_password()
    {
        string cn = $"Margaret Hamilton {Guid.NewGuid().ToString("N")[..6]}";
        using X509Certificate2 card = nodes.Pki.Issue(cn);

        using HttpClient script = TestPki.Browser(nodes.Kestrel.Node.Port, card);
        using HttpResponseMessage answer = await script.PostAsync(new Uri("/rest/generateToken", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["client"] = "requestip" }));
        string body = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.IsSuccessStatusCode, body);
        string token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;

        (int status, string who) = await nodes.Kestrel.Node.SendAsync(HttpMethod.Get, "/rest/whoami", token);
        Assert.Equal(200, status);
        Assert.Equal(cn.Replace(' ', '.'), JsonDocument.Parse(who).RootElement.GetProperty("name").GetString());

        using HttpClient pro = TestPki.Browser(nodes.Kestrel.Node.Port, null);
        using HttpResponseMessage password = await pro.PostAsync(new Uri("/rest/generateToken", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json", ["username"] = TwoNodeSite.Administrator, ["password"] = TwoNodeSite.Password, ["client"] = "requestip",
            }));
        string said = await password.Content.ReadAsStringAsync();
        Assert.True(password.IsSuccessStatusCode, said);
        Assert.True(JsonDocument.Parse(said).RootElement.TryGetProperty("token", out _), said);

        // Without a certificate and without a password, the old answer.
        using HttpResponseMessage neither = await pro.PostAsync(new Uri("/rest/generateToken", UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" }));
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
    }

    /// <summary>
    /// The provider is a button where providers are buttons — the sign-in panel's list and the portal's sign-in page —
    /// on a node that receives certificates.
    /// </summary>
    [Fact]
    public async Task The_provider_is_offered_where_sign_in_is_offered()
    {
        (int status, string body) = await nodes.Kestrel.Node.SendAsync(HttpMethod.Get, "/rest/auth/providers", null);
        Assert.Equal(200, status);
        Assert.Contains($"/rest/auth/pki/{nodes.Kestrel.ProviderId}/start", body, StringComparison.Ordinal);

        (status, body) = await nodes.Kestrel.Node.SendAsync(HttpMethod.Get, "/rest/login", null);
        Assert.Equal(200, status);
        Assert.Contains($"/rest/auth/pki/{nodes.Kestrel.ProviderId}/start", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Require</c>: a connection without a certificate, or with one another authority issued, never completes its
    /// handshake; one the deployment's authority issued does.
    /// </summary>
    [Fact]
    public async Task Require_refuses_a_connection_without_a_certificate_the_authorities_vouch_for()
    {
        using X509Certificate2 card = nodes.Pki.Issue($"Hedy Lamarr {Guid.NewGuid().ToString("N")[..6]}");
        using X509Certificate2 foreign = nodes.Stranger.Issue("Eve Stranger");

        using (HttpClient vouched = TestPki.Browser(nodes.Require.Node.Port, card))
        {
            using HttpResponseMessage live = await vouched.GetAsync(new Uri("/healthz/live", UriKind.Relative));
            Assert.True(live.IsSuccessStatusCode);
        }

        foreach (X509Certificate2? presented in new[] { null, foreign })
        {
            using HttpClient refused = TestPki.Browser(nodes.Require.Node.Port, presented);
            Exception? failed = await Record.ExceptionAsync(() => refused.GetAsync(new Uri("/healthz/live", UriKind.Relative)));
            Assert.True(failed is HttpRequestException or AuthenticationException or IOException,
                $"A connection {(presented is null ? "without a certificate" : "with another authority's")} was answered: {failed}");
        }
    }

    /// <summary>A header with no trusted proxy to send it refuses to start, and says which setting to fix.</summary>
    [Fact]
    public async Task A_header_with_no_trusted_proxy_refuses_to_start()
    {
        (int? exit, string said) = await nodes.Kestrel.Site.StartRefusedAsync("pki-misconfigured", TestPki.Settings(
            ("Graticula__ClientCertificates__Header", CertificateNodes.Header),
            ("Graticula__ClientCertificates__Authorities", "unused.pem")));

        Assert.Equal(78, exit);
        Assert.Contains("Graticula:TrustedProxies is empty", said, StringComparison.Ordinal);
    }
}
