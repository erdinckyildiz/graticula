using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

/// <summary>
/// The headers every response carries, and the inventory an anonymous caller
/// does not get.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both of these are findings from the §66 security gate, 2026-08-15.</b>
/// Attachment downloads were hardened by hand — <c>nosniff</c>, a sandboxing
/// policy, <c>Content-Disposition: attachment</c> — because somebody thought
/// carefully about the one surface serving arbitrary user bytes. Every other
/// response carried nothing at all. That is what protecting surfaces one at a
/// time produces: the considered one is safe and the default is bare.
/// </para>
/// <para>
/// <b>And <c>/admin/health</c> counted content it will not confirm exists.</b>
/// An anonymous caller saw two services in the catalogue and was told the server
/// holds twenty-six layers. Everywhere else a private layer answers 404,
/// indistinguishable from absent, specifically so nobody can learn it is there.
/// </para>
/// </remarks>
public sealed class SecurityHeaderConformanceTests : ArcGisClient
{
    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<HttpResponseMessage> GetAsync(string path, string? accept = null)
    {
        string root = await RequireServerAsync();

        HttpClient http = Client();
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        if (accept is not null)
        {
            request.Headers.Add("Accept", accept);
        }

        HttpResponseMessage response = await http.SendAsync(request);
        request.Dispose();
        http.Dispose();

        return response;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out System.Collections.Generic.IEnumerable<string>? v)
            ? string.Join(" ", v)
            : null;

    /// <summary>
    /// Every response carries them, including the ones no handler wrote.
    /// </summary>
    /// <remarks>
    /// <b>The 404 is the interesting case.</b> It comes from routing, not from a
    /// handler, and a hardening pass that adds headers inside handlers misses
    /// exactly the responses nobody wrote — the 404, the 405, the 500 from the
    /// exception middleware. Those are also the responses most likely to carry
    /// something a browser should not render.
    /// </remarks>
    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/rest/info")]
    [InlineData("/rest/services?f=json")]
    [InlineData("/no-such-route-exists")]
    [InlineData("/admin/layers")]
    public async Task Every_response_carries_the_headers(string path)
    {
        using HttpResponseMessage response = await GetAsync(path);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));

        Assert.NotNull(Header(response, "Content-Security-Policy"));
        Assert.Contains(
            "frame-ancestors 'none'",
            Header(response, "Content-Security-Policy")!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Referrer-Policy</c> specifically, because security.md names the hole.
    /// </summary>
    /// <remarks>
    /// ArcGIS compatibility puts a token in the query string. security.md lists
    /// where that leaks — logs, proxies, browser history, and <c>Referer</c>
    /// headers sent to third parties — and gives four mitigations, none of which
    /// closes the <c>Referer</c> channel. This header does, and it is asserted
    /// separately from the others so that removing it fails for its own reason.
    /// </remarks>
    [Fact]
    public async Task The_referrer_is_never_sent_because_a_token_may_be_in_the_url()
    {
        using HttpResponseMessage response = await GetAsync("/rest/services?f=json");

        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
    }

    /// <summary>
    /// The browsable directory gets a policy that forbids script outright.
    /// </summary>
    /// <remarks>
    /// It renders user-supplied layer names to an administrator holding every
    /// privilege the server has. The encoding is tested and correct; this is
    /// what stands between an encoding mistake and a stolen session. The pages
    /// contain no script by design, so the policy says <c>default-src 'none'</c>
    /// rather than restricting sources — a page that later needs one has to
    /// change the policy, which is the point.
    /// </remarks>
    [Fact]
    public async Task The_directory_forbids_script_and_framing()
    {
        using HttpResponseMessage response = await GetAsync("/rest/services", "text/html");

        string policy = Header(response, "Content-Security-Policy")!;

        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", policy, StringComparison.Ordinal);

        // The sign-in and query pages post back here, so forms must be allowed —
        // to this origin and nowhere else.
        Assert.Contains("form-action 'self'", policy, StringComparison.Ordinal);

        Assert.DoesNotContain("script-src 'unsafe", policy, StringComparison.Ordinal);
    }

    // ---------- the inventory ----------

    /// <summary>
    /// An anonymous caller learns whether the server is alive and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The endpoint has to stay anonymous</b> — sessions live in the platform
    /// store, so during the outage it exists for, nobody can authenticate
    /// (D-18). That is exactly why what it says matters.
    /// </remarks>
    [Fact]
    public async Task Health_tells_a_stranger_only_whether_it_is_alive()
    {
        using HttpResponseMessage response = await GetAsync("/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement health = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(health.TryGetProperty("status", out _));
        Assert.True(health.GetProperty("platformStore").TryGetProperty("reachable", out _));

        // The inventory, and our own version, are for operators.
        Assert.False(health.GetProperty("platformStore").TryGetProperty("layers", out _));
        Assert.False(health.TryGetProperty("tileCache", out _));
        Assert.False(health.TryGetProperty("describedShapes", out _));
        Assert.False(health.TryGetProperty("version", out _));
    }

    /// <summary>
    /// An administrator still gets everything, or the endpoint is useless.
    /// </summary>
    /// <remarks>
    /// The redaction is only defensible if it costs the operator nothing, so
    /// this asserts the other half. Without it, a future tightening could quietly
    /// blind the people the endpoint is for.
    /// </remarks>
    [Fact]
    public async Task An_administrator_still_sees_the_whole_picture()
    {
        string root = await RequireServerAsync();

        using HttpClient http = Client();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + "/admin/health"));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        JsonElement health = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(health.GetProperty("platformStore").TryGetProperty("layers", out _));
        Assert.True(health.TryGetProperty("tileCache", out _));
        Assert.True(health.TryGetProperty("describedShapes", out _));
        Assert.True(health.TryGetProperty("version", out _));
    }

    /// <summary>
    /// The inventory does not contradict the catalogue.
    /// </summary>
    /// <remarks>
    /// <b>This is the finding stated as a property.</b> A stranger sees some
    /// number of services in the catalogue; nothing they can reach may imply
    /// there are more. The two numbers are not comparable directly — services
    /// are not layers — so this asserts the weaker and sufficient thing: no
    /// count reaches them at all.
    /// </remarks>
    [Fact]
    public async Task Nothing_a_stranger_can_reach_counts_what_they_cannot_see()
    {
        using HttpResponseMessage response = await GetAsync("/admin/health");

        string body = await response.Content.ReadAsStringAsync();

        foreach (char digit in body.Where(char.IsDigit))
        {
            Assert.Fail(
                "The anonymous health document contains a number. Counts are inventory, and "
                + $"inventory is a disclosure about content this server will not confirm: {body}");
        }
    }
}
