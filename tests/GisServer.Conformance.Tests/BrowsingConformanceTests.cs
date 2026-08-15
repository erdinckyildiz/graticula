using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

/// <summary>
/// The directory as a browser meets it: signing in, and running an operation
/// from its own page.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because of a question the owner asked and the answer was
/// embarrassing.</b> "Why are GeometryServer and its capabilities not listed
/// under Utilities?" — because authentication read <c>Authorization: Bearer</c>
/// and nothing else, so a browser was permanently anonymous, and the geometry
/// service is shared with the organisation. Every page of a directory built for
/// browsing showed a stranger.
/// </para>
/// <para>
/// <b>And because the fix creates a rule that is easy to lose.</b> The session
/// cookie authenticates GET and HEAD only. Nothing enforces that except the code
/// and this file; a future change that "simplifies" the cookie into a general
/// credential would pass every other test in the suite.
/// </para>
/// </remarks>
public sealed class BrowsingConformanceTests : ArcGisClient
{
    private const string Geometry = "/rest/services/Utilities/Geometry/GeometryServer";

    private const string Point =
        """{"geometryType":"esriGeometryPoint","geometries":[{"x":29.0,"y":41.0}]}""";

    /// <summary>A browser, which follows redirects and keeps cookies.</summary>
    private static HttpClient Browser(CookieContainer jar, bool follow = true) =>
        new(new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = follow,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient http, string root, string returnTo)
    {
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("name", Environment.GetEnvironmentVariable(UserVariable) ?? "root"),
            new("password",
                Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty),
            new("return", returnTo),
        });

        return await http.PostAsync(new Uri(root + "/rest/auth/login"), form);
    }

    private static async Task<string> HtmlAsync(HttpClient http, string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url));
        request.Headers.Add("Accept", "text/html");

        using HttpResponseMessage response = await http.SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The sign-in form works, and lands where it said it would.
    /// </summary>
    /// <remarks>
    /// <b>It returned 415 when first tried.</b> The endpoint bound its body from
    /// JSON, and a browser cannot set a JSON content type on a form post — so
    /// the sign-in page this project had just built could not sign anybody in.
    /// </remarks>
    [Fact]
    public async Task The_sign_in_form_signs_a_browser_in()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar, follow: false);

        using HttpResponseMessage response =
            await SignInAsync(http, root, "/rest/services/Utilities");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/rest/services/Utilities", response.Headers.Location?.OriginalString);

        Assert.Contains(
            jar.GetCookies(new Uri(root)),
            cookie => cookie.Name == "gis-session" && cookie.HttpOnly && cookie.Secure);
    }

    /// <summary>
    /// A return path that leaves this server is not honoured.
    /// </summary>
    /// <remarks>
    /// <b>An open redirect on a sign-in page is a phishing primitive.</b> The
    /// link is ours, the credential prompt is ours, and the landing page is
    /// somebody else's. <c>//host</c> is the case that catches people, because
    /// it passes a "starts with a slash" check and is a protocol-relative URL.
    /// </remarks>
    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    public async Task A_return_path_that_leaves_this_server_is_refused(string target)
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar, follow: false);

        using HttpResponseMessage response = await SignInAsync(http, root, target);

        Assert.Equal("/rest/services", response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// A signed-in browser sees the geometry service under Utilities.
    /// </summary>
    /// <remarks>
    /// This is the owner's question, as an assertion.
    /// </remarks>
    [Fact]
    public async Task The_geometry_service_is_listed_under_Utilities()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar);

        using (HttpResponseMessage _ = await SignInAsync(http, root, "/rest/services"))
        {
        }

        string html = await HtmlAsync(http, root + "/rest/services/Utilities");

        Assert.Contains("GeometryServer", html, StringComparison.Ordinal);
        Assert.Contains("Signed in:", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every operation is a link to a page, not a word in a list.
    /// </summary>
    [Fact]
    public async Task Each_operation_is_a_link()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar);

        using (HttpResponseMessage _ = await SignInAsync(http, root, "/rest/services"))
        {
        }

        string html = await HtmlAsync(http, root + Geometry);

        foreach (string operation in (string[])
            ["project", "areasAndLengths", "lengths", "labelPoints",
             "intersect", "difference", "union"])
        {
            Assert.Contains($"href=\"{Geometry}/{operation}\"", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Following the link shows the form, and does not run the operation.
    /// </summary>
    /// <remarks>
    /// <b>The layer query page shipped doing the opposite and had to be
    /// corrected</b> — "the query page queries directly. It shouldn't be that
    /// way." The same mistake is available here for free, so it is asserted
    /// rather than remembered.
    /// </remarks>
    [Fact]
    public async Task Following_the_link_shows_a_form_rather_than_running_it()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar);

        using (HttpResponseMessage _ = await SignInAsync(http, root, "/rest/services"))
        {
        }

        string html = await HtmlAsync(http, root + Geometry + "/project");

        Assert.Contains("<form method=\"get\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"inSR\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"outSR\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"geometries\"", html, StringComparison.Ordinal);

        // The answer would contain a projected coordinate. Istanbul in Web
        // Mercator is about 3,228,000 — its absence is the assertion.
        Assert.DoesNotContain("3228", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pressing the button runs it, and the page and the JSON agree.
    /// </summary>
    [Fact]
    public async Task Pressing_the_button_runs_the_operation()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar);

        using (HttpResponseMessage _ = await SignInAsync(http, root, "/rest/services"))
        {
        }

        string query =
            $"?inSR=4326&outSR=3857&geometries={Uri.EscapeDataString(Point)}";

        string html = await HtmlAsync(http, root + Geometry + "/project" + query);
        string json = await http.GetStringAsync(new Uri(root + Geometry + "/project" + query));

        Assert.Contains("3228265", html, StringComparison.Ordinal);
        Assert.Contains("3228265", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cookie does not authenticate a POST.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the control that makes a session cookie safe to have at
    /// all.</b> A cookie is attached by the browser whatever caused the request,
    /// which is the whole of cross-site request forgery. <c>SameSite=Strict</c>
    /// is set and is the usual answer, but it is one flag honoured by the
    /// browser, and browsers have had bugs.
    /// </para>
    /// <para>
    /// So the credential simply does not work for the methods that matter. A
    /// forged request can read, and reading is what the directory is for. The
    /// geometry service is organisation-shared, so an anonymous POST gets the
    /// 404 that every unshared resource gets.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_cookie_does_not_authenticate_a_post()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar);

        using (HttpResponseMessage _ = await SignInAsync(http, root, "/rest/services"))
        {
        }

        // The same request as a GET succeeds — proving the cookie is present and
        // valid, and that the method is what makes the difference.
        using HttpResponseMessage read = await http.GetAsync(
            new Uri(root + Geometry + "/project?inSR=4326&outSR=3857&geometries="
                    + Uri.EscapeDataString(Point)));

        Assert.True(read.IsSuccessStatusCode, "The cookie should authenticate a read.");

        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("inSR", "4326"),
            new("outSR", "3857"),
            new("geometries", Point),
        });

        using HttpResponseMessage write =
            await http.PostAsync(new Uri(root + Geometry + "/project"), form);

        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    /// <summary>
    /// A refused operation explains itself as a page.
    /// </summary>
    [Fact]
    public async Task An_unimplemented_operation_explains_itself()
    {
        string root = await RequireServerAsync();

        CookieContainer jar = new();
        using HttpClient http = Browser(jar);

        using (HttpResponseMessage _ = await SignInAsync(http, root, "/rest/services"))
        {
        }

        using HttpRequestMessage request =
            new(HttpMethod.Get, new Uri(root + Geometry + "/buffer"));
        request.Headers.Add("Accept", "text/html");

        using HttpResponseMessage response = await http.SendAsync(request);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("not implemented", html, StringComparison.Ordinal);
        Assert.Contains("Q-97", html, StringComparison.Ordinal);
    }
}
