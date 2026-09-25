using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An app's OAuth sign-in page offers the organisation's own sign-in, and a person who takes it comes back to the app
/// with a code — ADR-088 condition 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole round trip a browser makes</b>, against a provider this suite runs: the app sends the person to the
/// authorize page, they choose the provider, sign in there, and the app receives a code it redeems for a token in
/// their name — never a session cookie, which a sign-in coming back from another site would not carry.
/// </para>
/// <para>
/// <b>And the two ways somebody else could try to use it</b>: the provider link opened without the page's own
/// cookie, and a sign-in started from a link prepared in another browser. Neither issues anything.
/// </para>
/// </remarks>
public sealed partial class OAuthThroughAProviderConformanceTests : ArcGisClient
{
    private const string Redirect = "https://app.example/callback";

    private static HttpClient Browser(CookieContainer jar) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = jar,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Authorize(string root, string clientId, string challenge) =>
        $"{root}/sharing/rest/oauth2/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=code"
        + $"&redirect_uri={Uri.EscapeDataString(Redirect)}&state=state-88&code_challenge={challenge}&code_challenge_method=S256";

    /// <summary>The authorize page, and the link on it to the provider named.</summary>
    private static async Task<Uri> ProviderLinkAsync(HttpClient browser, string root, string clientId, string challenge, string provider)
    {
        using HttpResponseMessage page = await browser.GetAsync(new Uri(Authorize(root, clientId, challenge)));
        string html = await page.Content.ReadAsStringAsync();
        Assert.True(page.StatusCode == HttpStatusCode.OK, $"The authorize page answered {(int)page.StatusCode}: {html}");

        Match link = Regex.Match(html, "<a href=\"(/sharing/rest/oauth2/external\\?[^\"]+)\">Sign in with " + Regex.Escape(provider) + "</a>");
        Assert.True(link.Success, $"The authorize page offers no way in through {provider}: {html}");

        return new Uri(root + WebUtility.HtmlDecode(link.Groups[1].Value));
    }

    /// <summary>Follows redirects by hand from <paramref name="from"/> until one leaves for the app, or a page answers.</summary>
    private static async Task<(HttpStatusCode Status, Uri? App, string Body)> WalkAsync(HttpClient browser, Uri from)
    {
        Uri at = from;

        for (int hop = 0; hop < 8; hop++)
        {
            using HttpResponseMessage response = await browser.GetAsync(at);

            if (response.StatusCode != HttpStatusCode.Redirect)
            {
                return (response.StatusCode, null, await response.Content.ReadAsStringAsync());
            }

            Uri next = response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location : new Uri(at, response.Headers.Location);

            if (next.ToString().StartsWith(Redirect, StringComparison.Ordinal))
            {
                return (response.StatusCode, next, string.Empty);
            }

            at = next;
        }

        throw new InvalidOperationException("More than eight redirects.");
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);

    [Fact]
    public async Task A_person_who_signs_in_through_the_provider_comes_back_to_the_app_with_a_code_in_their_name()
    {
        string root = await RequireServerAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string clientId = "zz_adr088_" + suffix;
        string providerName = $"zz Staff {suffix}";
        using FakeOidcProvider idp = new($"graticula-{suffix}", $"secret-{suffix}");

        (int registered, string app) = await AdminAsync(HttpMethod.Post, "/admin/oauth/apps",
            JsonSerializer.Serialize(new { clientId, title = "ADR-088 probe", redirectUris = new[] { Redirect } }));
        Assert.True(registered == 201, $"Registering an app answered {registered}: {app}");

        (int made, string providerSaid) = await AdminAsync(HttpMethod.Post, "/admin/identity-providers",
            JsonSerializer.Serialize(new
            {
                name = providerName, issuer = idp.Issuer, clientId = idp.ClientId, clientSecret = idp.Secret,
                autoCreate = true, defaultRole = "viewer",
            }));
        Assert.True(made == 201, $"Adding the provider answered {made}: {providerSaid}");
        string providerId = JsonDocument.Parse(providerSaid).RootElement.GetProperty("id").GetString()!;
        string? account = null;

        try
        {
            idp.Next = ($"sub-{suffix}", $"gail{suffix}@example.org");

            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            // ---- the link, opened without the page's cookie, issues nothing ----
            CookieContainer elsewhere = new();
            using (HttpClient first = Browser(new CookieContainer()))
            {
                Uri link = await ProviderLinkAsync(first, root, clientId, challenge, providerName);

                using HttpClient stranger = Browser(elsewhere);
                using HttpResponseMessage refused = await stranger.GetAsync(link);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Null(refused.Headers.Location);
            }

            // ---- a sign-in started from a link somebody else's browser prepared issues nothing ----
            using (HttpClient attacker = Browser(new CookieContainer()))
            {
                Uri link = await ProviderLinkAsync(attacker, root, clientId, challenge, providerName);
                using HttpResponseMessage toStart = await attacker.GetAsync(link);
                Assert.Equal(HttpStatusCode.Redirect, toStart.StatusCode);
                Uri start = new(new Uri(root), toStart.Headers.Location!);

                using HttpClient victim = Browser(new CookieContainer());
                (HttpStatusCode status, Uri? toApp, string body) = await WalkAsync(victim, start);
                Assert.True(toApp is null, $"A sign-in started from another browser's link sent a code to the app: {toApp}");
                Assert.Equal(HttpStatusCode.BadRequest, status);
                Assert.Contains("not started from this browser", body, StringComparison.Ordinal);
            }

            // ---- the way it is meant: the page, the provider, and back to the app with a code ----
            using HttpClient browser = Browser(new CookieContainer());
            Uri way = await ProviderLinkAsync(browser, root, clientId, challenge, providerName);
            (HttpStatusCode last, Uri? back, string said) = await WalkAsync(browser, way);

            Assert.True(back is not null, $"Signing in through {providerName} did not come back to the app ({(int)last}): {said} {idp.LastRefusal}");
            Dictionary<string, string> query = Query(back!);
            Assert.Equal("state-88", query["state"]);

            using FormUrlEncodedContent exchange = new(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = query["code"],
                ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
            });
            using HttpResponseMessage redeemed = await browser.PostAsync(new Uri($"{root}/sharing/rest/oauth2/token"), exchange);
            JsonElement issued = JsonDocument.Parse(await redeemed.Content.ReadAsStringAsync()).RootElement;

            account = issued.GetProperty("username").GetString();
            Assert.Equal($"gail{suffix}", account);
            Assert.False(string.IsNullOrEmpty(issued.GetProperty("access_token").GetString()));
        }
        finally
        {
            if (account is not null) await AdminAsync(HttpMethod.Delete, $"/admin/members/{account}");
            await AdminAsync(HttpMethod.Delete, $"/admin/identity-providers/{providerId}");
            await AdminAsync(HttpMethod.Delete, $"/admin/oauth/apps/{clientId}");
        }
    }
}
