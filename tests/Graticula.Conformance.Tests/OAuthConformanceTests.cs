using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// OAuth 2.0 authorization code with PKCE, end to end against a running server — ADR-076.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven the way a browser drives it</b>: the authorize page is fetched, its form token and cookie
/// are carried back, the redirect is read rather than followed, and the code is exchanged with the
/// verifier. What a real client does with the result is condition 1, measured separately with the
/// Maps SDK; this is the protocol's own contract, including each refusal.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed partial class OAuthConformanceTests : ArcGisClient
{
    private const string Redirect = "https://app.example/callback";

    /// <summary>
    /// ArcGIS Pro's own sign-in, as Pro sends it: <c>arcgisprodesktop</c>, out of band, no PKCE, <c>expiration=-1</c>.
    /// </summary>
    /// <remarks>
    /// <b>2026-10-16, ADR-076 condition 3.</b> The request is the one Pro's embedded browser recorded opening against
    /// ArcGIS Online. The page signs the person in, the out-of-band page's title carries the code — which is where Pro
    /// reads it — and the code is exchanged for a token without a secret, as Pro has none.
    /// </remarks>
    [Fact]
    public async Task ArcGIS_Pros_own_sign_in_ends_in_a_token()
    {
        string root = await RequireServerAsync();
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        Assert.False(user is null || password is null, "No administrator credential.");

        using HttpClient browser = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        const string Oob = "urn:ietf:wg:oauth:2.0:oob";
        string authorize = $"{root}/sharing/rest/oauth2/authorize?client_id=arcgisprodesktop&expiration=-1"
            + "&redirect_uri=urn:ietf:wg:oauth:2.0:oob&response_type=code&sessionName=ArcGISPro_60&locale=en-US";

        using HttpResponseMessage page = await browser.GetAsync(authorize);
        string html = await page.Content.ReadAsStringAsync();
        Assert.True(page.StatusCode == HttpStatusCode.OK, $"Pro's authorize page answered {(int)page.StatusCode}: {html}");

        string formToken = FormToken().Match(html).Groups[1].Value;
        string cookie = page.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("graticula_oauth_form=", StringComparison.Ordinal)).Split(';')[0];

        string code;

        using (HttpResponseMessage signedIn = await PostFormAsync(browser, $"{root}/sharing/rest/oauth2/signin", cookie,
            [("form_token", formToken), ("client_id", "arcgisprodesktop"), ("response_type", "code"), ("redirect_uri", Oob),
             ("username", user!), ("password", password!)]))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

            using HttpResponseMessage approval = await browser.GetAsync(new Uri(new Uri(root), signedIn.Headers.Location!));
            string approved = await approval.Content.ReadAsStringAsync();
            code = Regex.Match(approved, "<title>SUCCESS code=(.*?)</title>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(code), $"The out-of-band page carries no code in its title: {approved}");
        }

        JsonElement issued = await TokenAsync(browser, root, ("client_id", "arcgisprodesktop"), ("grant_type", "authorization_code"),
            ("code", code), ("redirect_uri", Oob), ("f", "json"));

        Assert.True(issued.TryGetProperty("access_token", out JsonElement access), $"Pro's code was not exchanged: {issued}");
        Assert.True(issued.TryGetProperty("refresh_token", out _), $"Pro is given no refresh token: {issued}");

        JsonElement self = await GetWithAsync(browser, $"{root}/sharing/rest/portals/self?f=json", access.GetString()!);
        Assert.Equal(user, self.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_registered_app_signs_a_person_in_and_every_misuse_is_refused()
    {
        string root = await RequireServerAsync();
        string? administrator = await TokenAsync(root);
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);

        Assert.False(administrator is null || user is null || password is null, "No administrator credential.");

        string clientId = "zz_adr076_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode registered, string app) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/oauth/apps", administrator!,
            JsonSerializer.Serialize(new { clientId, title = "ADR-076 probe", redirectUris = new[] { Redirect } }));

        Assert.True(registered == HttpStatusCode.Created, $"Registering an app answered {(int)registered}: {app}");

        using HttpClient browser = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        try
        {
            // ---- an unregistered redirect is shown, never followed ----
            using (HttpResponseMessage elsewhere = await browser.GetAsync(Authorize(root, clientId, "https://evil.example/cb", "x")))
            {
                Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
                Assert.Null(elsewhere.Headers.Location);
            }

            // ---- the sign-in page, with its form token and its cookie ----
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            (string formToken, string cookie, string page) = await SignInPageAsync(browser, Authorize(root, clientId, Redirect, challenge));

            Assert.Contains("ADR-076 probe", page, StringComparison.Ordinal);

            // ---- a post without the cookie is refused: login CSRF ----
            using (HttpResponseMessage forged = await PostFormAsync(browser, $"{root}/sharing/rest/oauth2/signin", cookie: null,
                SignInFields(clientId, challenge, formToken, user!, password!)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
                Assert.Null(forged.Headers.Location);
            }

            // ---- a wrong password stays on the page, says the one sentence, and issues nothing ----
            (formToken, cookie, _) = await SignInPageAsync(browser, Authorize(root, clientId, Redirect, challenge));

            using (HttpResponseMessage wrong = await PostFormAsync(browser, $"{root}/sharing/rest/oauth2/signin", cookie,
                SignInFields(clientId, challenge, formToken, user!, password + "-not")))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
                Assert.Contains("The name or password is incorrect.", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // ---- the right one: a redirect to the registered address, with the code and the state ----
            (formToken, cookie, _) = await SignInPageAsync(browser, Authorize(root, clientId, Redirect, challenge));

            string code;

            using (HttpResponseMessage signedIn = await PostFormAsync(browser, $"{root}/sharing/rest/oauth2/signin", cookie,
                SignInFields(clientId, challenge, formToken, user!, password!)))
            {
                Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

                Uri back = signedIn.Headers.Location!;
                Assert.StartsWith(Redirect + "?", back.ToString(), StringComparison.Ordinal);

                Dictionary<string, string> query = Query(back);
                Assert.Equal("state-76", query["state"]);
                code = query["code"];
            }

            // ---- the wrong verifier is refused, and the code is spent by it ----
            JsonElement badVerifier = await TokenAsync(browser, root, ("client_id", clientId), ("grant_type", "authorization_code"),
                ("code", code), ("redirect_uri", Redirect), ("code_verifier", Base64Url(RandomNumberGenerator.GetBytes(32))));

            Assert.Equal("invalid_grant", badVerifier.GetProperty("error").GetProperty("error").GetString());

            // <b>A code is spent by its first exchange, verified or not.</b> So a fresh sign-in is needed —
            // which is the behaviour to want: a guessed verifier gets one attempt per code.
            (formToken, cookie, _) = await SignInPageAsync(browser, Authorize(root, clientId, Redirect, challenge));

            using (HttpResponseMessage again = await PostFormAsync(browser, $"{root}/sharing/rest/oauth2/signin", cookie,
                SignInFields(clientId, challenge, formToken, user!, password!)))
            {
                code = Query(again.Headers.Location!)["code"];
            }

            JsonElement issued = await TokenAsync(browser, root, ("client_id", clientId), ("grant_type", "authorization_code"),
                ("code", code), ("redirect_uri", Redirect), ("code_verifier", verifier));

            string access = issued.GetProperty("access_token").GetString()!;
            string refresh = issued.GetProperty("refresh_token").GetString()!;

            Assert.Equal(user, issued.GetProperty("username").GetString());
            Assert.InRange(issued.GetProperty("expires_in").GetInt64(), 60, 30 * 60);
            Assert.True(issued.GetProperty("refresh_token_expires_in").GetInt64() > 24 * 3600);

            // ---- the access token opens the portal as that person ----
            JsonElement self = await GetWithAsync(browser, $"{root}/sharing/rest/portals/self?f=json", access);
            Assert.Equal(user, self.GetProperty("user").GetProperty("username").GetString());

            // ---- and it does not open the native administration API (Q-154) ----
            using (HttpRequestMessage admin = new(HttpMethod.Get, $"{root}/admin/oauth/apps"))
            {
                admin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                using HttpResponseMessage refused = await browser.SendAsync(admin);
                Assert.True((int)refused.StatusCode is 401 or 403, $"An OAuth access token opened /admin: {(int)refused.StatusCode}");
            }

            // ---- a refresh token issues a new access token ----
            JsonElement refreshed = await TokenAsync(browser, root, ("client_id", clientId), ("grant_type", "refresh_token"), ("refresh_token", refresh));
            Assert.NotEqual(access, refreshed.GetProperty("access_token").GetString());
            Assert.False(refreshed.TryGetProperty("refresh_token", out _));

            // ---- the code a second time: refused, and its refresh token revoked with it ----
            JsonElement replay = await TokenAsync(browser, root, ("client_id", clientId), ("grant_type", "authorization_code"),
                ("code", code), ("redirect_uri", Redirect), ("code_verifier", verifier));

            Assert.Equal("invalid_grant", replay.GetProperty("error").GetProperty("error").GetString());

            JsonElement dead = await TokenAsync(browser, root, ("client_id", clientId), ("grant_type", "refresh_token"), ("refresh_token", refresh));
            Assert.Equal("invalid_grant", dead.GetProperty("error").GetProperty("error").GetString());

            // ---- what the Maps SDK actually sends: the other prefix, and its page's own query ----
            // Measured 2026-09-16 driving the SDK 4.30: it opens /sharing/oauth2/authorize, not
            // /sharing/rest/oauth2/authorize, and sends the page it is on — query and all — as the
            // redirect. Both have to be accepted, and the path and origin still have to match.
            using (HttpResponseMessage sdkShaped = await browser.GetAsync(
                Authorize(root, clientId, Redirect + "?portal=x&appId=y", challenge).Replace("/sharing/rest/oauth2/", "/sharing/oauth2/", StringComparison.Ordinal)))
            {
                Assert.Equal(HttpStatusCode.OK, sdkShaped.StatusCode);
            }

            using (HttpResponseMessage otherPath = await browser.GetAsync(Authorize(root, clientId, "https://app.example/callback/elsewhere", challenge)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, otherPath.StatusCode);
                Assert.Null(otherPath.Headers.Location);
            }

            // ---- the implicit grant is sent back to the app, refused ----
            using (HttpResponseMessage implicitGrant = await browser.GetAsync(
                $"{root}/sharing/rest/oauth2/authorize?client_id={clientId}&response_type=token&redirect_uri={Uri.EscapeDataString(Redirect)}"))
            {
                Assert.Equal(HttpStatusCode.Redirect, implicitGrant.StatusCode);
                Assert.Equal("unsupported_response_type", Query(implicitGrant.Headers.Location!)["error"]);
            }
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/oauth/apps/{clientId}", administrator!, json: null);
        }
    }

    /// <summary>
    /// The ArcGIS API for Python's own sign-in: <c>pythonapi</c>, out of band, the page's <c>oAuthInfo</c> and its
    /// <c>oauth_state</c> posted alone to <c>/sharing/oauth2/signin</c> — owner decision 2026-10-15, Q-170.
    /// </summary>
    /// <remarks>
    /// <b>Walked as arcgis 2.4.3's <c>_init_token_auth_handshake</c> walks it</b>: no hidden field of the form is sent
    /// back, only the state, the name and the password; the code is read from the out-of-band page's title; a refusal
    /// is read from <c>oAuthInfo.messages</c>.
    /// </remarks>
    [Fact]
    public async Task The_python_api_signs_in_with_a_password_through_the_page_state()
    {
        string root = await RequireServerAsync();
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        Assert.False(user is null || password is null, "No administrator credential.");

        using HttpClient browser = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        string authorize = $"{root}/sharing/rest/oauth2/authorize?response_type=code&client_id=pythonapi"
            + "&redirect_uri=urn%3Aietf%3Awg%3Aoauth%3A2.0%3Aoob&state=py&expiration=20160&allow_verification=false";

        async Task<(string State, string Cookie)> PageAsync()
        {
            using HttpResponseMessage response = await browser.GetAsync(authorize);
            string page = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"pythonapi's authorize page answered {(int)response.StatusCode}: {page}");
            string cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("graticula_oauth_form=", StringComparison.Ordinal));
            Assert.Contains("path=/sharing;", cookie + ";", StringComparison.OrdinalIgnoreCase);
            return (Info(page).GetProperty("oauth_state").GetString()!, cookie.Split(';')[0]);
        }

        (string Key, string Value)[] Fields(string state, string secret) =>
            [("oauth_state", state), ("authorize", "true"), ("username", user!), ("password", secret)];

        // ---- without the cookie the state is not enough ----
        (string state, string cookie) = await PageAsync();
        using (HttpResponseMessage forged = await PostFormAsync(browser, $"{root}/sharing/oauth2/signin", null, Fields(state, password!)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        // ---- a wrong password is said in oAuthInfo.messages ----
        (state, cookie) = await PageAsync();
        using (HttpResponseMessage wrong = await PostFormAsync(browser, $"{root}/sharing/oauth2/signin", cookie, Fields(state, password + "-not")))
        {
            string said = await wrong.Content.ReadAsStringAsync();
            Assert.Contains("Invalid username or password.",
                string.Join("|", Info(said).GetProperty("messages").EnumerateArray().Select(m => m.GetString())), StringComparison.Ordinal);
        }

        // ---- the right one: to the out-of-band page, whose title carries the code ----
        (state, cookie) = await PageAsync();
        string code;

        using (HttpResponseMessage signedIn = await PostFormAsync(browser, $"{root}/sharing/oauth2/signin", cookie, Fields(state, password!)))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            using HttpResponseMessage approval = await browser.GetAsync(new Uri(new Uri(root), signedIn.Headers.Location!));
            string page = await approval.Content.ReadAsStringAsync();
            code = Regex.Match(page, "<title>SUCCESS code=(.*?)</title>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(code), $"The out-of-band page carries no code: {page}");

            // The API reads oAuthInfo from the answer to its sign-in as well, and fails on a page without one.
            Assert.Equal(JsonValueKind.Object, Info(page).ValueKind);
        }

        JsonElement token = await TokenAsync(browser, root, ("grant_type", "authorization_code"), ("code", code),
            ("redirect_uri", "urn:ietf:wg:oauth:2.0:oob"), ("client_id", "pythonapi"), ("expiration", "20160"));
        Assert.True(token.TryGetProperty("access_token", out JsonElement access) && access.GetString() is { Length: > 0 },
            $"Exchanging pythonapi's code answered {token}");
    }

    /// <summary>The <c>oAuthInfo</c> a page carries, as the Python API finds it.</summary>
    /// <remarks>
    /// <b>By the API's own expression, not by the assignment's name</b>: arcgis 2.4.3 takes the first <c>{</c> in the
    /// page to the last <c>}</c> on that line — <c>re.search(r"\{.*\}", page, re.MULTILINE)</c> — and parses it. Read by
    /// name, as this did until 2026-10-31, the test passed while every password sign-in through the API failed,
    /// because the page's inline style came first on the same line (ADR-204 condition 3).
    /// </remarks>
    private static JsonElement Info(string page)
    {
        Match found = Regex.Match(page, @"\{.*\}");
        Assert.True(found.Success, $"The page carries no oAuthInfo: {page}");

        try
        {
            return JsonDocument.Parse(found.Value).RootElement.Clone();
        }
        catch (JsonException)
        {
            Assert.Fail($"The first braces of the page are not oAuthInfo, so the ArcGIS API for Python cannot read it: {found.Value[..Math.Min(200, found.Value.Length)]}");
            throw;
        }
    }

    private static string Authorize(string root, string clientId, string redirect, string challenge) =>
        $"{root}/sharing/rest/oauth2/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=code"
        + $"&redirect_uri={Uri.EscapeDataString(redirect)}&state=state-76"
        + $"&code_challenge={challenge}&code_challenge_method=S256";

    private static (string Key, string Value)[] SignInFields(string clientId, string challenge, string formToken, string user, string password) =>
    [
        ("form_token", formToken), ("client_id", clientId), ("response_type", "code"), ("redirect_uri", Redirect),
        ("state", "state-76"), ("code_challenge", challenge), ("code_challenge_method", "S256"),
        ("username", user), ("password", password),
    ];

    private static async Task<(string FormToken, string Cookie, string Page)> SignInPageAsync(HttpClient browser, string url)
    {
        using HttpResponseMessage response = await browser.GetAsync(url);
        string page = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The authorize page answered {(int)response.StatusCode}: {page}");

        // <b>No script may run on it</b>, and its form may post only here and to the one registered redirect.
        string policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action 'self' https://app.example", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", policy, StringComparison.Ordinal);
        // The one script element is data — `oAuthInfo`, for the ArcGIS API for Python (Q-170) — typed so no browser runs it.
        Assert.DoesNotMatch("<script(?![^>]*type=\"application/json\")", page);

        string token = FormToken().Match(page).Groups[1].Value;
        string cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("graticula_oauth_form=", StringComparison.Ordinal));

        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        return (token, cookie.Split(';')[0], page);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string url, string? cookie, (string Key, string Value)[] fields)
    {
        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await browser.SendAsync(request);
    }

    private static async Task<JsonElement> TokenAsync(HttpClient browser, string root, params (string Key, string Value)[] fields)
    {
        using HttpResponseMessage response = await PostFormAsync(browser, $"{root}/sharing/rest/oauth2/token", null, fields);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> GetWithAsync(HttpClient browser, string url, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await browser.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("name=\"form_token\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();
}
