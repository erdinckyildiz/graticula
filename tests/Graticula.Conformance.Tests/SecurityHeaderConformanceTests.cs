using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The headers every response carries, and the inventory an anonymous caller
/// does not get.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both of these are findings from the Â§66 security gate, 2026-08-15.</b>
/// Attachment downloads were hardened by hand â€” <c>nosniff</c>, a sandboxing
/// policy, <c>Content-Disposition: attachment</c> â€” because somebody thought
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
    /// exactly the responses nobody wrote â€” the 404, the 405, the 500 from the
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
    /// where that leaks â€” logs, proxies, browser history, and <c>Referer</c>
    /// headers sent to third parties â€” and gives four mitigations, none of which
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
    /// rather than restricting sources â€” a page that later needs one has to
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

        // The sign-in and query pages post back here, so forms must be allowed â€”
        // to this origin and nowhere else.
        Assert.Contains("form-action 'self'", policy, StringComparison.Ordinal);

        Assert.DoesNotContain("script-src 'unsafe", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The console's policy permits the console's own script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is D-44's regression test, and the bug it guards was invisible from
    /// the server side.</b> The console is the fourth HTML surface and the only one
    /// that is an application. It was served the policy written for the three that
    /// are documents â€” <c>default-src 'none'</c> with no <c>script-src</c> â€” which
    /// blocked its script as firmly as it blocks a CDN. The page rendered, because
    /// inline styles were allowed, and then nothing worked: every button was inert
    /// and the sign-in form fell back to a native submit.
    /// </para>
    /// <para>
    /// <b>Nothing in the server could see it.</b> A blocked script is refused by
    /// the browser after the response was already sent, so there is no log line, no
    /// error status and no failed request to notice. The existing header tests all
    /// passed throughout â€” they assert the headers are present, which they were.
    /// The gap was that no test asked whether the page still worked with them.
    /// </para>
    /// <para>
    /// Asserted as a property rather than as a string: the console's script must be
    /// permitted, and it must be permitted without <c>'unsafe-inline'</c> â€” which is
    /// what keeps the fix from decaying into "allow everything on that path".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_console_may_run_its_own_script()
    {
        using HttpResponseMessage page = await GetAsync("/server/", "text/html");
        page.EnsureSuccessStatusCode();

        string policy = Header(page, "Content-Security-Policy")!;
        string html = await page.Content.ReadAsStringAsync();

        // The page loads its behaviour from a file, so the policy can permit it by
        // origin rather than by allowing inline script.
        Assert.Contains("console.js", html, StringComparison.Ordinal);

        int start = policy.IndexOf("script-src ", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "The console is served a policy with no script-src, so default-src 'none' blocks "
            + "its own script and every button is inert. This is D-44.\nPolicy: " + policy);

        int end = policy.IndexOf(';', start);
        string scriptSrc = end < 0 ? policy[start..] : policy[start..end];

        Assert.Contains("'self'", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'", scriptSrc, StringComparison.Ordinal);

        // And the file itself must be reachable: a policy that permits a script
        // the server does not serve is the same outcome by a different route.
        using HttpResponseMessage script = await GetAsync("/server/console.js");
        script.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// No page the console serves carries an inline script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The invariant, after checking one page proved not to be enough.</b>
    /// D-44's first test asserted that the application's own page loads its behaviour from a
    /// file. It does — and one edit later the viewer beside it (then <c>/console/map.html</c>,
    /// now <c>/studio/map.html</c>)
    /// was still inline, so the same policy blocked the same way and the page
    /// rendered a header with nothing under it. A test that names one page
    /// protects one page.
    /// </para>
    /// <para>
    /// The policy is deliberately without <c>'unsafe-inline'</c>, which is what
    /// makes <c>script-src 'self'</c> worth anything — so "no inline script" is a
    /// property of every document served under that policy, and it is cheaper to
    /// assert than to remember.
    /// <summary>
    /// The console decides which screen to paint before its 454 KB of JavaScript arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's report:</b> *"server'dan studio ya geçerken 1 sn liğine password ekranı
    /// gelip kapanıyor."* Server is <c>/server/</c> and Studio is <c>/studio/</c>, so switching
    /// surface is a whole-page navigation and every switch is a cold load. <c>#signin</c> had no
    /// initial state, so the browser painted it as soon as the HTML arrived and only
    /// <c>start()</c> — after <c>console.js</c> had loaded and compiled — hid it again.
    /// <c>/rest/whoami</c> answers in 6 to 15 ms, so the network was never the cause.
    /// </para>
    /// <para>
    /// <b>This is a structural assertion and that is deliberate.</b> The behaviour cannot be
    /// tested here in a way that would fail: on any machine that serves <c>console.js</c> in
    /// twenty milliseconds the flash is invisible whether the fix works or not, which is exactly
    /// how the first attempt came to look cured while being refused by the browser on every
    /// load. Reproducing it needs the script artificially delayed, which is a browser
    /// instrument rather than a conformance one.
    /// </para>
    /// <para>
    /// <b>So this guards the two ways it can regress.</b> Moving the decision back inline —
    /// which the test below already refuses, for the same reason — or giving it <c>defer</c>,
    /// which puts it after document parsing and is the whole problem restated. Measured with
    /// <c>console.js</c> delayed 800 ms: before the fix the sign-in panel stayed for the full
    /// delay, after it the panel was never visible in any of about 1,380 samples per run.
    /// </para>
    /// </remarks>
    /// <param name="path">The console page.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("/server/")]
    [InlineData("/studio/")]
    public async Task The_console_reads_its_session_before_it_paints(string path)
    {
        using HttpResponseMessage page = await GetAsync(path, "text/html");
        page.EnsureSuccessStatusCode();

        string html = await page.Content.ReadAsStringAsync();

        string withoutComments = System.Text.RegularExpressions.Regex.Replace(
            html, "<!--.*?-->", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        System.Text.RegularExpressions.Match tag =
            System.Text.RegularExpressions.Regex.Match(
                withoutComments, "<script[^>]*\\bsrc\\s*=\\s*[\"']session\\.js[\"'][^>]*>");

        Assert.True(
            tag.Success,
            $"""
             {path} does not load session.js, so nothing decides whether this page is the
             console or the sign-in screen until console.js has arrived — 454 KB later. The
             owner reported that as a password screen appearing for a second on every surface
             switch.
             """);

        // <b>`defer` would restore the bug exactly.</b> It is the attribute that says *run this
        // after the document is parsed*, and running after the document is parsed is what put
        // the wrong screen on display in the first place.
        Assert.False(
            tag.Value.Contains("defer", StringComparison.OrdinalIgnoreCase)
            || tag.Value.Contains("async", StringComparison.OrdinalIgnoreCase),
            $"""
             {path} loads session.js with defer or async, which puts it back after document
             parsing. That is the original bug: the browser paints the sign-in screen and
             corrects it later.
             """);

        // <b>And it is before the body, because after it is too late.</b>
        int at = withoutComments.IndexOf(tag.Value, StringComparison.Ordinal);
        int body = withoutComments.IndexOf("<body", StringComparison.OrdinalIgnoreCase);

        Assert.True(
            body < 0 || at < body,
            $"{path} loads session.js after the body starts, so the first paint has already "
            + "happened by the time it runs.");
    }

    [Fact]
    public async Task The_session_script_and_the_stylesheet_agree_on_one_attribute()
    {
        // <b>Three files hold one decision, and this is what stops them drifting.</b>
        // `session.js` writes `data-session`, `console.css` paints from it, and `console.js`
        // corrects it once `/rest/whoami` has answered. A rename in one of them would leave the
        // other two describing a mechanism that no longer exists — and the failure mode is a
        // page that looks right on a fast machine.
        using HttpResponseMessage script = await GetAsync("/server/session.js", null);
        script.EnsureSuccessStatusCode();

        string js = await script.Content.ReadAsStringAsync();

        Assert.Contains("dataset.session", js, StringComparison.Ordinal);
        Assert.Contains("gis-token", js, StringComparison.Ordinal);

        using HttpResponseMessage sheet = await GetAsync("/server/console.css", null);
        sheet.EnsureSuccessStatusCode();

        string css = await sheet.Content.ReadAsStringAsync();

        Assert.Contains("data-session=\"held\"", css, StringComparison.Ordinal);

        using HttpResponseMessage app = await GetAsync("/server/console.js", null);
        app.EnsureSuccessStatusCode();

        string console = await app.Content.ReadAsStringAsync();

        // <b>Both directions.</b> Writing only *held* would strand an expired session on a
        // console it cannot use; writing only *none* would never turn the optimism on.
        Assert.Contains("dataset.session = \"held\"", console, StringComparison.Ordinal);
        Assert.Contains("dataset.session = \"none\"", console, StringComparison.Ordinal);
    }

    /// </para>
    /// </remarks>
    /// <summary>
    /// Every console page this server serves, discovered rather than typed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-46](../../docs/architecture-debt.md) instance 10 was about these two theories,
    /// and it was still true — repaired 2026-08-26.</b> The row records that the enumerating
    /// form was named as its own remedy and then carried a hand-typed list of pages that had
    /// drifted. It had drifted further than the row said: `wwwroot` holds `index.html`,
    /// `map.html` and `view.html`, and both surfaces serve all three, so the real set is
    /// **six addresses**. The two theories listed four. **`/server/map.html` and
    /// `/server/view.html` were checked by neither** — not for an inline script, not for a
    /// permitted subresource.
    /// </para>
    /// <para>
    /// <b>Discovered from the source tree, which is what makes it an invariant.</b> A page
    /// added to `wwwroot` tomorrow is covered the day it is added, by both theories, with no
    /// edit anywhere — which is the property this row says the enumerating form was supposed
    /// to have and did not. `DeadColumnsStayDeadTests` finds the repository the same way.
    /// </para>
    /// <para>
    /// <b>The prefixes stay typed and that is deliberate.</b> `/server` and `/studio` are
    /// two surfaces `Program.cs` names in one place; discovering them would mean parsing C#
    /// to protect a list of two, and a test whose own machinery can be wrong is worse than
    /// a short list that is right.
    /// </para>
    /// </remarks>
    public static TheoryData<string> ConsolePages
    {
        get
        {
            DirectoryInfo? at = new(AppContext.BaseDirectory);

            while (at is not null
                && !Directory.Exists(Path.Combine(at.FullName, "src")))
            {
                at = at.Parent;
            }

            Assert.True(at is not null, "Could not find the repository root from the test assembly.");

            string wwwroot = Path.Combine(
                at!.FullName, "src", "Graticula.Host", "wwwroot");

            Assert.True(Directory.Exists(wwwroot), $"No wwwroot at {wwwroot}.");

            TheoryData<string> pages = [];

            foreach (string file in Directory
                .EnumerateFiles(wwwroot, "*.html", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(file);

                foreach (string surface in (string[])["/server", "/studio"])
                {
                    // The index is served at the surface's root, which is the address a
                    // browser actually asks for.
                    pages.Add(string.Equals(name, "index.html", StringComparison.Ordinal)
                        ? surface + "/"
                        : $"{surface}/{name}");
                }
            }

            Assert.True(
                pages.Count >= 4,
                $"Only {pages.Count} console page(s) were discovered under {wwwroot}, which is "
                + "fewer than this server is known to serve — the discovery is broken rather "
                + "than the server being small.");

            return pages;
        }
    }

    [Theory]
    [MemberData(nameof(ConsolePages))]
    public async Task No_console_page_carries_an_inline_script(string path)
    {
        using HttpResponseMessage page = await GetAsync(path, "text/html");
        page.EnsureSuccessStatusCode();

        string html = await page.Content.ReadAsStringAsync();

        // Opening tags with no src=. Comments are stripped first, because this
        // project's pages explain themselves and several of those explanations
        // mention a script tag.
        string withoutComments = System.Text.RegularExpressions.Regex.Replace(
            html, "<!--.*?-->", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        System.Text.RegularExpressions.MatchCollection inline =
            System.Text.RegularExpressions.Regex.Matches(
                withoutComments, "<script(?![^>]*\\bsrc\\s*=)[^>]*>");

        Assert.True(
            inline.Count == 0,
            $"""
             {path} carries {inline.Count} inline <script> block(s), and the console's
             Content-Security-Policy has no 'unsafe-inline' — so the browser refuses them and
             the page renders with nothing working. Move the code to a file beside the page.
             D-44.
             """);

        Assert.Contains("<script", withoutComments, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every file a console page asks for is permitted by the policy it is sent with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-44 a fourth time, and the third dimension of the same mistake.</b> The
    /// console's stylesheet moved out of the page into <c>console.css</c> on
    /// 2026-08-17, and <c>style-src</c> permitted <c>'unsafe-inline'</c> and a CDN but
    /// not <c>'self'</c> — so the server sent every rule and the browser applied none.
    /// The page was not blank: unstyled, <c>.view { display: none }</c> never arrived
    /// and all five screens stacked into one document, which reads as a layout bug
    /// rather than as a refused request.
    /// </para>
    /// <para>
    /// The two existing tests could not catch it. One asks whether the policy permits
    /// <c>script-src 'self'</c>; the other whether any page carries inline script.
    /// Both name the resource kind they were written for, and the fault moves to
    /// whichever kind nobody named. So this one enumerates instead: whatever a console
    /// page references, the directive governing it must permit this origin, and the
    /// file must actually be served. It needs no editing when the next kind of
    /// subresource is added — which is the property the other two lack.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(ConsolePages))]
    public async Task Every_file_a_console_page_asks_for_is_permitted(string path)
    {
        using HttpResponseMessage page = await GetAsync(path, "text/html");
        page.EnsureSuccessStatusCode();

        string policy = Header(page, "Content-Security-Policy")!;
        string html = System.Text.RegularExpressions.Regex.Replace(
            await page.Content.ReadAsStringAsync(), "<!--.*?-->", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        // (attribute carrying the URL, the directive that governs it).
        (string Pattern, string Directive)[] kinds =
        [
            ("<script[^>]*\\bsrc\\s*=\\s*[\"']([^\"']+)", "script-src"),
            ("<link[^>]*\\bstylesheet[^>]*\\bhref\\s*=\\s*[\"']([^\"']+)", "style-src"),
            ("<link[^>]*\\bhref\\s*=\\s*[\"']([^\"']+)[\"'][^>]*\\bstylesheet", "style-src"),
        ];

        int checked_ = 0;

        foreach ((string pattern, string directive) in kinds)
        {
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(html, pattern))
            {
                string reference = match.Groups[1].Value;

                // Only what this server serves. A CDN is named in the policy on
                // purpose and is checked by reading the policy, not by fetching it.
                if (reference.Contains("//", StringComparison.Ordinal))
                {
                    continue;
                }

                checked_++;

                int start = policy.IndexOf(directive + " ", StringComparison.Ordinal);
                Assert.True(start >= 0,
                    $"{path} asks for {reference}, and its policy has no {directive} — so "
                    + $"default-src 'none' refuses it and the browser says nothing the server "
                    + $"can see. D-44.\nPolicy: {policy}");

                int end = policy.IndexOf(';', start);
                string clause = end < 0 ? policy[start..] : policy[start..end];

                Assert.True(clause.Contains("'self'", StringComparison.Ordinal),
                    $"{path} asks for {reference}, which is this origin, but {directive} does "
                    + $"not permit 'self'. The file is served and then ignored.\n{clause}");

                string absolute = reference.StartsWith('/')
                    ? reference
                    : path[..(path.LastIndexOf('/') + 1)] + reference;

                using HttpResponseMessage file = await GetAsync(absolute);
                Assert.True(file.IsSuccessStatusCode,
                    $"{path} asks for {absolute} and the server answers "
                    + $"{(int)file.StatusCode}. A permitted file that is not served is the "
                    + $"same outcome by a different route.");
            }
        }

        Assert.True(checked_ > 0, $"{path} references no local file, so this proved nothing.");
    }

    /// <summary>
    /// The sign-in form cannot put a credential in a URL, even with no script.
    /// </summary>
    /// <remarks>
    /// <b>The second half of D-44, and the worse half.</b> With its script blocked
    /// the form submitted natively, as a GET, carrying <c>name="username"</c> and
    /// <c>name="password"</c> â€” so the password landed in the address bar, the
    /// browser history and any proxy log on the way. A console whose buttons do
    /// nothing is broken; one that leaks a credential while looking broken is
    /// worse. Neither input carries a name now, so a native submit has nothing to
    /// serialise, and the form posts, so it could not be a query string anyway.
    /// </remarks>
    [Fact]
    public async Task The_sign_in_form_cannot_leak_a_credential_without_script()
    {
        using HttpResponseMessage page = await GetAsync("/server/", "text/html");
        string html = await page.Content.ReadAsStringAsync();

        int form = html.IndexOf("id=\"signinForm\"", StringComparison.Ordinal);
        Assert.True(form >= 0, "The console has no sign-in form.");

        int close = html.IndexOf("</form>", form, StringComparison.Ordinal);
        string markup = html[form..close];

        Assert.Contains("method=\"post\"", html[..(form + 40)], StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"username\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"password\"", markup, StringComparison.Ordinal);
    }

    // ---------- the inventory ----------

    /// <summary>
    /// An anonymous caller learns whether the server is alive and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The endpoint has to stay anonymous</b> â€” sessions live in the platform
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
    /// there are more. The two numbers are not comparable directly â€” services
    /// are not layers â€” so this asserts the weaker and sufficient thing: no
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
