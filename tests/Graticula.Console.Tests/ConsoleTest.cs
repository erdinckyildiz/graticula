using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Opens the operator console in a browser and asks it questions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> D-59: nothing tested the console, and four
/// defects in two days were all found by the owner pressing a button — Stop
/// opening the service page, a cookie-only session painted as an administrator,
/// the Server surface visible to a publisher, and eight of nine services with no
/// reachable Limits page. Every one of them is one click deep, and every one of
/// them would have been caught here. Four of the seven tests in this project are
/// those four defects, written as the assertions that were missing.
/// </para>
/// <para>
/// <b>The other three are their pairs, and that is a rule rather than padding.</b>
/// The cheapest way to pass <em>Stop must not open the service</em> is to stop the
/// row navigating at all; the cheapest way to pass <em>a cookie session is not
/// painted as an administrator</em> is to paint nobody; the cheapest way to hide
/// the Server surface from a publisher is to hide it from everybody. Each of those
/// is a worse defect than the one it would replace, so the opposite direction is
/// asserted beside each.
/// </para>
/// <para>
/// <b>Reads go to the server; writes do not leave the page.</b> The subject of
/// these tests is which code path a click takes, not what the server does
/// afterwards — and a suite that stops the operator's services to prove a button
/// works is worse than no suite. So the harness replaces <c>fetch</c> for
/// anything that is not a <c>GET</c> or <c>HEAD</c>, records it, and answers with
/// an empty document. The recording is the assertion: a click that reached the
/// button's action shows up there, and a click that fell through to the row's
/// navigation shows up in the address instead.
/// </para>
/// <para>
/// <b>Where a fact has to be changed, it is edited out of the server's own
/// answer.</b> One test needs a reader who does not hold <c>admin:manageServer</c>.
/// It could create an account, but there is no <c>DELETE /admin/members</c>, so
/// every run would leave one behind. Instead the real <c>/rest/whoami</c> is
/// fetched and that one privilege is removed on the way back to the page. The
/// shape stays the server's — which matters, because a stub written from memory
/// is the trap the conformance suite's project file warns about: it agrees with
/// what the server used to send while both are wrong.
/// </para>
/// </remarks>
public abstract class ConsoleTest : IAsyncLifetime
{
    /// <summary>Where the server under test is.</summary>
    public const string UrlVariable = "GRATICULA_TEST_URL";

    /// <summary>An account that administers it.</summary>
    public const string UserVariable = "GRATICULA_TEST_USER";

    /// <summary>That account's password.</summary>
    public const string PasswordVariable = "GRATICULA_TEST_PASSWORD";

    /// <summary>
    /// The one client that signs in, shared by the assembly.
    /// </summary>
    /// <remarks>
    /// <b>Cookies off, which is not a detail here.</b> A handler with a cookie jar
    /// would keep <c>gis-session</c> from the sign-in and send it on every later
    /// request, and the difference between a token session and a cookie-only one
    /// is the entire subject of <see cref="SessionTests"/>. Each is planted into
    /// the browser deliberately, one at a time.
    /// </remarks>
    private static readonly HttpClient Http = new(
        new HttpClientHandler
        {
            // ADR-014's development certificate is self-signed; see ArcGisClient.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = false,
        })
    { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly SemaphoreSlim SignInLock = new(1, 1);
    private static (string Token, string Cookie)? _session;

    private DevTools? _browser;
    private string? _planted;

    /// <summary>The server root, without a trailing slash.</summary>
    protected string Root { get; private set; } = string.Empty;

    /// <summary>The same, for the static sign-in that runs before any instance.</summary>
    private static string Server() =>
        (Environment.GetEnvironmentVariable(UrlVariable) ?? string.Empty).TrimEnd('/');

    /// <summary>The browser, once <see cref="InitializeAsync"/> has run.</summary>
    protected DevTools Browser =>
        _browser ?? throw new InvalidOperationException("The browser is not open yet.");

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        // <b>D-60: one database-backed suite at a time.</b> This suite is the
        // heaviest reader of the lot — every page load boots the whole console
        // against the server, and the reachability test does that once per service.
        // Adding it took the solution-wide run from occasionally red to reliably so,
        // which is how the first attempt at D-60 was found to be doing nothing.
        Graticula.Testing.OneSuiteAtATime.Enter();

        Assert.False(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable)),
            $"{UrlVariable} is not set, so these tests FAIL rather than skip. Start the server and "
            + "set it, e.g. https://127.0.0.1:8443. They drive the operator console in a real "
            + "browser; passing them with nothing listening would assert that it behaves.");

        Root = Server();

        using HttpResponseMessage live = await Http.GetAsync(new Uri($"{Root}/healthz/live"));

        Assert.True(
            live.IsSuccessStatusCode,
            $"{Root} did not answer /healthz/live ({(int)live.StatusCode}).");

        _browser = await DevTools.LaunchAsync();
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_browser is { } browser)
        {
            await browser.DisposeAsync();
        }
    }

    /// <summary>
    /// Signs in over HTTP and returns the bearer token and the session cookie.
    /// </summary>
    /// <returns>The token, and the cookie's value.</returns>
    /// <remarks>
    /// <para>
    /// <b>One request yields both, which is the whole of what one test is
    /// about.</b> <c>POST /rest/auth/login</c> returns a token in its body and
    /// sets the same token as <c>gis-session</c> — and per ADR-023 §4c that cookie
    /// authenticates <c>GET</c> and <c>HEAD</c> only. A browser holding the cookie
    /// and not the token can read everything and write nothing, and until
    /// 2026-08-17 the console painted that reader as an administrator.
    /// </para>
    /// <para>
    /// <b>Once for the assembly.</b> Argon2 is deliberately expensive, and this
    /// suite signs in for the token, for the cookie, and again for every catalogue
    /// read — which is a suite that measures key derivation. Cached for the same
    /// reason <c>ArcGisClient</c> caches it, and safe to share because the two
    /// halves are handed to the browser separately by the caller.
    /// </para>
    /// </remarks>
    protected static async Task<(string Token, string Cookie)> SignInAsync()
    {
        if (_session is { } cached)
        {
            return cached;
        }

        await SignInLock.WaitAsync();

        try
        {
            return _session ??= await FreshSignInAsync();
        }
        finally
        {
            SignInLock.Release();
        }
    }

    private static async Task<(string Token, string Cookie)> FreshSignInAsync()
    {
        string? user = Environment.GetEnvironmentVariable(UserVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password),
            $"{UserVariable} and {PasswordVariable} must be set to an account that administers "
            + $"the server named by {UrlVariable}. The console is an administrative surface; there "
            + "is nothing to test anonymously.");

        using HttpRequestMessage request =
            new(HttpMethod.Post, new Uri($"{Server()}/rest/auth/login"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { name = user, password }),
                    Encoding.UTF8,
                    "application/json"),
            };

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Signing in as '{user}' returned {(int)response.StatusCode}. Fix the credentials; "
            + "running these anonymously would test the sign-in form and nothing else.");

        JsonElement body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync()).RootElement;

        string token = body.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("The sign-in answered no token.");

        Assert.True(
            response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies),
            "Signing in set no cookie. ADR-023 §4c has the directory's form depending on one, and "
            + "one of these tests is about the reader who holds it and no token.");

        string? session = null;

        foreach (string cookie in cookies!)
        {
            if (cookie.StartsWith("gis-session=", StringComparison.Ordinal))
            {
                session = cookie["gis-session=".Length..].Split(';')[0];
            }
        }

        Assert.NotNull(session);
        return (token, session!);
    }

    /// <summary>
    /// Every folder that holds at least one service, and what it holds.
    /// </summary>
    /// <returns>
    /// Folder names with the empty string standing for the root, each with the
    /// qualified names of its services.
    /// </returns>
    /// <remarks>
    /// <b>Because the services screen at the root is usually empty, and a suite
    /// that stops there tests nothing.</b> Every hosted layer lands in
    /// <c>hosted</c> and the geometry service lives in <c>Utilities</c>, so a
    /// server published entirely through the hosting API — including the one CI
    /// builds from nothing — has no service at the root at all. Three of these
    /// tests failed on their first run for exactly that, reporting "the services
    /// screen listed no rows" against a server with eleven services. The
    /// conformance suite learned this first and says so in
    /// <c>AnyServiceNameAsync</c>; the lesson is the same one twice, so it is
    /// written down twice.
    /// </remarks>
    protected async Task<(string Folder, string[] Services)[]> FoldersWithServicesAsync()
    {
        List<(string, string[])> found = new();
        JsonElement root = await ReadAsync("/rest/services");

        if (Holds(root) is { Length: > 0 } atRoot)
        {
            found.Add((string.Empty, atRoot));
        }

        if (root.TryGetProperty("folders", out JsonElement named)
            && named.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in named.EnumerateArray())
            {
                if (folder.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                if (Holds(await ReadAsync($"/rest/services/{Uri.EscapeDataString(name)}"))
                    is { Length: > 0 } inside)
                {
                    found.Add((name, inside));
                }
            }
        }

        Assert.NotEmpty(found);
        return found.ToArray();

        // <b>Qualified names, which is what the catalogue returns and what the
        // console's rows carry.</b> The two agreeing by construction is what lets a
        // test wait for the screen to be showing the folder it asked for, rather
        // than for it to be showing anything at all.
        static string[] Holds(JsonElement catalogue) =>
            catalogue.TryGetProperty("services", out JsonElement services)
            && services.ValueKind == JsonValueKind.Array
                ? services.EnumerateArray()
                    .Select(s => s.TryGetProperty("name", out JsonElement n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToArray()
                : Array.Empty<string>();
    }

    /// <summary>Reads a catalogue document as the configured administrator.</summary>
    private async Task<JsonElement> ReadAsync(string path)
    {
        (string token, _) = await SignInAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{Root}{path}?f=json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}.");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>
    /// Some layer this server holds, for the tests that are about a layer.
    /// </summary>
    /// <returns>An unqualified layer name.</returns>
    /// <remarks>
    /// <b>Asked for rather than assumed, for the same reason the folders are.</b> Which
    /// layers exist is a fact about the server a test is pointed at; a suite that named
    /// one would pass here and fail in CI, where the fixtures are seeded under different
    /// names.
    /// </remarks>
    protected async Task<string> AnyLayerAsync()
    {
        JsonElement listing = await ReadAsync("/admin/layers");

        JsonElement layers = listing.ValueKind == JsonValueKind.Array
            ? listing
            : listing.TryGetProperty("layers", out JsonElement named)
                ? named
                : default;

        Assert.True(
            layers.ValueKind == JsonValueKind.Array && layers.GetArrayLength() > 0,
            $"{Root} has no published layers, so the layer tests cannot run. Publish one first.");

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("name", out JsonElement name)
                && name.GetString() is { Length: > 0 } found)
            {
                return found;
            }
        }

        Assert.Fail("The layer listing has entries and none of them has a name.");
        return string.Empty;
    }

    /// <summary>The console address of a folder's services screen.</summary>
    /// <param name="folder">A folder name, or the empty string for the root.</param>
    protected static string ServicesIn(string folder) =>
        string.IsNullOrEmpty(folder)
            ? "/server/#/services"
            : $"/server/#/services/{Uri.EscapeDataString(folder)}";

    /// <summary>
    /// Waits until the services screen is showing the folder that was asked for.
    /// </summary>
    /// <param name="folder">The folder, or the empty string for the root.</param>
    /// <param name="expected">The qualified service names the catalogue reports.</param>
    /// <remarks>
    /// <b>The catalogue is the answer key, which makes this an assertion as well as
    /// a wait.</b> Waiting for "a row, any row" is what let a stale screen pass for
    /// a fresh one; waiting for <em>these</em> rows cannot. It also checks the
    /// listing against what the server says it holds, which is worth checking on
    /// its own: a folder quietly dropping a service from the screen is the shape of
    /// D-61's reachability defect.
    /// </remarks>
    protected async Task ShowingAsync(string folder, string[] expected)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(expected);

        string wanted = JsonSerializer.Serialize(expected);

        await WaitForAsync(
            $"(() => {{ const want = {wanted}; const have = "
            + "Array.from(document.querySelectorAll('tr[data-service]'))"
            + ".map(r => r.dataset.service); "
            + "return want.every(n => have.includes(n)); })()",
            $"The services screen for '{(folder.Length == 0 ? "the root" : folder)}' never listed "
            + $"all {expected.Length} of the services the catalogue says it holds: "
            + string.Join(", ", expected));
    }

    /// <summary>
    /// Opens the first services screen that has something matching a selector.
    /// </summary>
    /// <param name="selector">What the test needs to be there.</param>
    /// <param name="token">The bearer token to plant.</param>
    /// <returns>The address it settled on, for the failure message.</returns>
    /// <remarks>
    /// <b>Searching rather than assuming, and it is the folders that make it
    /// necessary.</b> Which folder holds a service with a startable cover is a fact
    /// about the server a test is pointed at, not about the console — so a test
    /// that hard-coded <c>hosted</c> passes on this machine and fails in CI, where
    /// the seeded fixtures are somewhere else. The browser is left on the screen
    /// that matched, so the caller carries on from a page that has the control.
    /// </remarks>
    protected async Task<string> OpenFolderHoldingAsync(string selector, string token)
    {
        ArgumentNullException.ThrowIfNull(selector);

        string quoted = JsonSerializer.Serialize(selector);
        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();

        foreach ((string folder, string[] services) in folders)
        {
            string address = ServicesIn(folder);
            await OpenAsync(address, token);

            // This folder's own rows first, so a selector that matched the previous
            // folder's listing cannot answer for this one.
            await ShowingAsync(folder, services);

            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (await Browser.EvaluateAsync<bool>($"!!document.querySelector({quoted})"))
                {
                    return address;
                }

                await Task.Delay(100);
            }
        }

        Assert.Fail(
            $"No services screen on {Root} has anything matching '{selector}'. Looked in: "
            + string.Join(", ", folders.Select(f => f.Folder.Length == 0 ? "the root" : f.Folder))
            + ". These tests need a published service to click on.");

        return string.Empty;
    }

    /// <summary>
    /// Opens a console address in the browser, as a given kind of reader.
    /// </summary>
    /// <param name="address">
    /// The path and hash, e.g. <c>/server/#/services</c>. Both halves matter:
    /// ADR-034 puts the surface in the path and the screen in the hash, so
    /// <c>/studio/#/services</c> is a different request from <c>/server/#/services</c>
    /// and one of these tests is about which one a reader is left on.
    /// </param>
    /// <param name="token">The bearer token to plant, or null for none.</param>
    /// <param name="cookie">The session cookie to plant, or null for none.</param>
    /// <param name="without">Privileges to edit out of <c>/rest/whoami</c>.</param>
    protected async Task OpenAsync(
        string address,
        string? token = null,
        string? cookie = null,
        params string[] without)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(without);

        if (cookie is not null)
        {
            // <b>Planted through the URL, and with the server's own attributes.</b>
            // They are load bearing: a cookie without Secure is not sent over TLS,
            // and the test would quietly become the anonymous case — which passes
            // for the wrong reason, since an anonymous reader is also shown the
            // form.
            await Browser.CallAsync("Network.setCookie", new
            {
                url = Root,
                name = "gis-session",
                value = cookie,
                path = "/",
                httpOnly = true,
                secure = true,
                sameSite = "Strict",
            });
        }

        // <b>Registered once per browser, because Chrome accumulates them.</b>
        // `Page.addScriptToEvaluateOnNewDocument` adds; it does not replace. Two
        // calls with the same source means the fetch trap wraps the fetch trap,
        // which happens to work and is one refactor away from not — a wrapper that
        // recorded a write and delegated would record it twice, and the test that
        // counts writes would be asserting against the harness.
        string plant = Plant(token, without);

        if (_planted != plant)
        {
            await Browser.PlantAsync(plant);
            _planted = plant;
        }

        // <b>Through about:blank, because two console addresses can differ only in
        // their hash.</b> `/server/#/services/hosted` and `/server/#/services/turkiye`
        // are the same document to a browser, so navigating between them is a
        // same-document navigation: `readyState` never leaves *complete*, the wait
        // inside NavigateAsync returns at once, and the page is still showing the
        // previous folder's rows. The reachability test collected one folder's
        // services twice and attributed them to the other's screen, and failed
        // about one run in three, naming a different service each time. A blank
        // page in between makes every open mean what every test here assumes it
        // means: a fresh document at this address.
        await Browser.NavigateAsync("about:blank");
        await Browser.NavigateAsync(Root + address);
    }

    /// <summary>
    /// The script that runs before the console's own, in the console's own tab.
    /// </summary>
    /// <remarks>
    /// <b>Three jobs, and each is here rather than in a test so that no test can
    /// forget one.</b> It plants the token where <c>console.js</c> looks for it;
    /// it traps writes so a click cannot change the server; and it edits named
    /// privileges out of <c>/rest/whoami</c> so a reader's view can be tested
    /// without an account that has to be cleaned up afterwards.
    /// </remarks>
    private static string Plant(string? token, string[] without)
    {
        string quotedToken = token is null ? "null" : JsonSerializer.Serialize(token);
        string quotedWithout = JsonSerializer.Serialize(without);

        return $$"""
        (() => {
          const token = {{quotedToken}};
          const without = {{quotedWithout}};

          if (token) { sessionStorage.setItem("gis-token", token); }
          else { sessionStorage.removeItem("gis-token"); }

          // What the tests read back. Named on window because Runtime.evaluate
          // runs in the page's own world, which is also why the console can see
          // this fetch — a test that ran in an isolated world would trap nothing.
          window.__writes = [];
          window.__confirmed = [];

          // <b>Every way a page can fail without saying so.</b> The four defects this
          // suite was built for were all visible on screen; a thrown exception is not —
          // it stops one section and leaves the rest looking finished, which is exactly
          // how a half-loaded console reads as a loaded one. `error` catches a throw,
          // `unhandledrejection` catches an await nobody caught, and the console's own
          // `api()` rejects on every refusal, so the second is the one that matters here.
          window.__pageErrors = [];

          window.addEventListener("error", e => {
            window.__pageErrors.push("error: " + (e.message || String(e.error)));
          });

          window.addEventListener("unhandledrejection", e => {
            const why = e.reason && (e.reason.message || e.reason);
            window.__pageErrors.push("unhandled rejection: " + String(why));
          });

          // Every confirm is answered yes. A dialog that blocks is a headless
          // browser that hangs, and what a test wants to know is what the click
          // did once it was allowed to proceed.
          window.confirm = message => { window.__confirmed.push(String(message)); return true; };

          const real = window.fetch.bind(window);

          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const method = ((init && init.method) || (input && input.method) || "GET").toUpperCase();

            if (method !== "GET" && method !== "HEAD") {
              window.__writes.push(method + " " + url);

              return new Response("{}", {
                status: 200,
                headers: { "Content-Type": "application/json" },
              });
            }

            const response = await real(input, init);

            if (!without.length || !url.includes("/rest/whoami") || !response.ok) {
              return response;
            }

            // <b>The server's answer with one field edited, not an answer written
            // from memory.</b> If whoami's shape changes, this changes with it and
            // the test keeps asking the question it was written to ask.
            const me = await response.json();

            if (Array.isArray(me.privileges)) {
              me.privileges = me.privileges.filter(p => !without.includes(p));
            }

            return new Response(JSON.stringify(me), {
              status: response.status,
              headers: { "Content-Type": "application/json" },
            });
          };
        })();
        """;
    }

    /// <summary>
    /// Waits until an expression in the page is true.
    /// </summary>
    /// <param name="expression">JavaScript yielding a boolean.</param>
    /// <param name="why">What the caller was waiting for, for the failure.</param>
    /// <remarks>
    /// <para>
    /// <b>Polling, because the console loads each section on its own.</b> There is
    /// no single moment at which it is finished — that is deliberate, so one
    /// refused endpoint cannot blank the page — so a test waits for the thing it
    /// is about rather than for the page.
    /// </para>
    /// <para>
    /// <b>The expression must be null-safe, and that is a rule rather than advice.</b>
    /// A throw is not a false answer: it propagates out of here as a page error and
    /// skips the diagnostic below entirely. `document.getElementById('x').textContent`
    /// on a screen the router has not rendered yet reports *cannot read properties of
    /// null* about a page that was merely a tick early — which cost a diagnosis on the
    /// Symbology page and looked exactly like a missing element. Write
    /// `getElementById('x')?.textContent || ''` and let the wait do its job.
    /// </para>
    /// </remarks>
    protected async Task WaitForAsync(string expression, string why)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await Browser.EvaluateAsync<bool>($"!!({expression})"))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(
            $"Ten seconds passed and '{expression}' was never true. {why}\n"
            + $"The address was {await Browser.EvaluateAsync<string>("location.href")} and the "
            + "page's visible text began: "
            + $"{await Browser.EvaluateAsync<string>("(document.body.innerText || '').slice(0, 400)")}");
    }

    /// <summary>Clicks the first element matching a selector.</summary>
    /// <param name="selector">A CSS selector.</param>
    /// <remarks>
    /// <b>Found and clicked in one expression, because the first version had a gap
    /// in it and the gap was real.</b> It waited for the element and then clicked
    /// it in a second call, and the console redraws its listings on its own — so a
    /// row that existed when it was found had been replaced by a new one when it
    /// was clicked, and the suite reported <em>cannot read properties of null</em>
    /// against a console that was working. That is D-60's lesson arriving inside
    /// the suite D-60 was being cleared for: a test that fails at random teaches
    /// its reader to run it again. Polling one atomic expression removes the gap
    /// rather than making it narrower.
    /// </remarks>
    protected async Task ClickAsync(string selector)
    {
        string quoted = JsonSerializer.Serialize(selector);

        string clicked = $"(() => {{ const e = document.querySelector({quoted}); "
            + "if (!e) return false; e.click(); return true; })()";

        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await Browser.EvaluateAsync<bool>(clicked))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(
            $"Ten seconds passed and nothing on the page matched '{selector}'. The address was "
            + $"{await Browser.EvaluateAsync<string>("location.href")}.");
    }

    /// <summary>
    /// Anything the page threw or failed to await, in order.
    /// </summary>
    /// <remarks>
    /// <b>Not the same question as "does the screen look right".</b> The console loads
    /// each section on its own so that one refused endpoint cannot blank the page — which
    /// is the right design and means a section that threw leaves the rest looking
    /// finished. A test that only checks what is visible cannot tell a loaded console from
    /// a half-loaded one.
    /// </remarks>
    protected async Task<string[]> PageErrorsAsync() =>
        await Browser.EvaluateAsync<string[]>("window.__pageErrors")
            ?? Array.Empty<string>();

    /// <summary>What the page tried to write while the test was running.</summary>
    protected async Task<string[]> WritesAsync() =>
        await Browser.EvaluateAsync<string[]>("window.__writes") ?? Array.Empty<string>();
}
