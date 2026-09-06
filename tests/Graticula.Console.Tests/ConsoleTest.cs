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
    private bool _warmed;

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
        /*
          <b>Every kind, including the ones the console has no screens for.</b> This is
          the catalogue's own answer and it is what the services list is checked
          against, so filtering here would hide a real difference between what the
          console draws and what the server holds.

          The walk that clicks through to a per-service page filters separately — see
          <see cref="ImageServicesAsync"/> — because *the ArcGIS catalogue publishes it*
          and *the console can manage it* are two different claims, and on 2026-08-21
          the first became true for image services while the second stayed false.
        */
        static string[] Holds(JsonElement catalogue) =>
            catalogue.TryGetProperty("services", out JsonElement services)
            && services.ValueKind == JsonValueKind.Array
                ? services.EnumerateArray()
                    .Select(s => s.TryGetProperty("name", out JsonElement n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
    }

    /// <summary>
    /// Every service that is an image service, which the console knows nothing about.
    /// </summary>
    /// <remarks>
    /// <b>[D-136](../../docs/architecture-debt.md), named rather than hidden.</b> An
    /// ImageServer holds a registered coverage
    /// ([ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)) and the
    /// console has no screen for one — it is absent from `/admin/services` too, so it
    /// does not appear in any list there. The walk that clicks through to a per-service
    /// page therefore has nothing to click, and skipping these keeps the two claims
    /// apart: *the catalogue publishes it* and *the console can manage it*. The second
    /// is false, and it is false in a register rather than in a silence.
    /// </remarks>
    /// <returns>Their qualified names.</returns>
    protected async Task<HashSet<string>> ImageServicesAsync()
    {
        HashSet<string> found = new(StringComparer.Ordinal);

        // <b>The folders the server has, not the ones this machine happened to
        // have — 2026-08-25.</b> This listed `turkiye` by name, which is a folder on
        // the machine the suite was written on and a 404 anywhere else: the first CI
        // run to reach the console suite reported *GET /rest/services/turkiye returned
        // 404* as a failure of reachability. The root listing already names its
        // folders, so asking is both correct and shorter than a list that has to be
        // maintained by whoever adds one.
        List<string?> folders = [null];

        JsonElement root = await ReadAsync("/rest/services");

        if (root.TryGetProperty("folders", out JsonElement named)
            && named.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in named.EnumerateArray())
            {
                if (folder.GetString() is { Length: > 0 } value)
                {
                    folders.Add(value);
                }
            }
        }

        foreach (string? folder in folders)
        {
            JsonElement catalogue = await ReadAsync(
                folder is null ? "/rest/services" : $"/rest/services/{folder}");

            if (!catalogue.TryGetProperty("services", out JsonElement services)
                || services.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement service in services.EnumerateArray())
            {
                if (service.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "ImageServer", StringComparison.Ordinal)
                    && service.TryGetProperty("name", out JsonElement name)
                    && name.GetString() is { Length: > 0 } qualified)
                {
                    found.Add(qualified);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Calls an admin endpoint as the configured administrator, and says what came back.
    /// </summary>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path, from the root.</param>
    /// <param name="json">A body, or null.</param>
    /// <returns>The status and the body, for a caller that wants to assert on either.</returns>
    /// <remarks>
    /// <b>Added so a test can provision what it needs instead of requiring the server to already
    /// have it.</b> A suite that needs eleven services and finds ten either fails on the fixture —
    /// which is a fact about the machine, not about the code — or asserts something weaker than
    /// it meant to. Creating and removing an empty service used to be the cheapest provisioning
    /// this console had; `POST /admin/featureservices` refuses since 2026-09-06 and
    /// <see cref="EmptyServiceAsync"/> is what replaced it.
    /// </remarks>
    /// <summary>
    /// Makes an empty service the way one actually comes about: publish a layer, take it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A service is not created without layers</b> — owner decision 2026-09-06, ADR-057
    /// condition 4 — so <c>POST /admin/featureservices</c> refuses and the provisioning two
    /// suites here depended on had to go somewhere. <b>Empty services still exist</b>: §5h says
    /// so plainly, because unpublishing the last layer leaves the container, and
    /// <c>POST /admin/featureservices/sweep</c> exists to find those. So this reaches the state
    /// through the route the product actually has, which makes it a better fixture than the one
    /// it replaces — that one made a state by a path no operator could take any more.
    /// </para>
    /// <para>
    /// <b>One table, reused as many times as the caller likes.</b> Unpublishing frees the table
    /// again — <c>layer_table_unique</c> is on the registration, not the data — so eleven empty
    /// services cost eleven publish-and-unpublish cycles over the same table rather than eleven
    /// free tables. CI's fixture has two, which is what makes that distinction matter.
    /// </para>
    /// </remarks>
    /// <param name="service">What to call it.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="sharing">Who may read it.</param>
    /// <returns>The status and body of whichever step failed, or the publish's on success.</returns>
    protected async Task<(int Status, string Body)> EmptyServiceAsync(
        string service, string? folder = null, string sharing = "private")
    {
        string layer = $"zz_seed_{Guid.NewGuid():N}"[..24];

        (int status, string body) = await PublishOneAsync(service, layer, folder, sharing);

        if (status is not (200 or 201))
        {
            return (status, body);
        }

        (int gone, string why) = await AdminAsync(
            HttpMethod.Delete, $"/admin/layers/{Uri.EscapeDataString(layer)}");

        return gone == 200
            ? (status, body)
            : (gone, $"the layer was published and would not come back off: {why}");
    }

    /// <summary>
    /// Publishes one service made of one layer, over a table the fixture leaves unpublished.
    /// </summary>
    /// <remarks>
    /// <b>The conformance suite has the same pair and cannot share it</b> — that suite may not
    /// reference any of our assemblies and this one is a different project, so the shape is
    /// written twice on purpose. If a third copy is ever wanted, a shared test-support project
    /// is the answer rather than a fourth.
    /// </remarks>
    /// <param name="service">What to call it.</param>
    /// <param name="layer">What to call its layer.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="sharing">Who may read it.</param>
    /// <returns>The status and the body.</returns>
    protected async Task<(int Status, string Body)> PublishOneAsync(
        string service, string layer, string? folder = null, string sharing = "private")
    {
        (int listed, string sources) = await AdminAsync(HttpMethod.Get, "/admin/datasources");

        if (listed != 200)
        {
            return (listed, sources);
        }

        string? datastore = null;

        foreach (JsonElement source in JsonDocument.Parse(sources)
            .RootElement.GetProperty("dataSources").EnumerateArray())
        {
            if (source.TryGetProperty("name", out JsonElement named)
                && string.Equals(named.GetString(), "datastore", StringComparison.Ordinal))
            {
                datastore = source.GetProperty("id").GetString();
            }
        }

        if (datastore is null)
        {
            return (0, "no datastore data source is registered");
        }

        (int probed, string capability) = await AdminAsync(
            HttpMethod.Get, $"/admin/datasources/{datastore}/capability");

        if (probed != 200)
        {
            return (probed, capability);
        }

        (int served, string layers) = await AdminAsync(HttpMethod.Get, "/admin/layers");

        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        if (served == 200)
        {
            foreach (JsonElement one in JsonDocument.Parse(layers)
                .RootElement.GetProperty("layers").EnumerateArray())
            {
                if (one.TryGetProperty("table", out JsonElement qualified)
                    && qualified.GetString() is { Length: > 0 } where)
                {
                    taken.Add(where);
                }
            }
        }

        foreach (JsonElement table in JsonDocument.Parse(capability)
            .RootElement.GetProperty("tables").EnumerateArray())
        {
            if (!table.TryGetProperty("geometryColumn", out JsonElement geometry)
                || geometry.ValueKind != JsonValueKind.String
                || !table.TryGetProperty("objectIdColumn", out JsonElement identity)
                || identity.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string schema = table.GetProperty("schemaName").GetString()!;
            string named = table.GetProperty("tableName").GetString()!;

            if (taken.Contains($"{schema}.{named}"))
            {
                continue;
            }

            string json = JsonSerializer.Serialize(new
            {
                name = service,
                folder,
                sharing,
                nodes = new[]
                {
                    new
                    {
                        layer = new
                        {
                            name = layer,
                            dataSourceId = datastore,
                            schemaName = schema,
                            tableName = named,
                            geometryColumn = geometry.GetString(),
                            geometryType = table.TryGetProperty("geometryType", out JsonElement kind)
                                && kind.ValueKind == JsonValueKind.String
                                    ? kind.GetString()
                                    : "Polygon",
                            identityColumn = identity.GetString(),
                            srid = table.TryGetProperty("srid", out JsonElement srid)
                                && srid.ValueKind == JsonValueKind.Number
                                    ? srid.GetInt32()
                                    : 3857,
                        },
                    },
                },
            });

            return await AdminAsync(HttpMethod.Post, "/admin/publish", json);
        }

        return (0, "the fixture has no table that nothing publishes");
    }

    protected async Task<(int Status, string Body)> AdminAsync(
        HttpMethod method, string path, string? json = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        (string token, _) = await SignInAsync();

        using HttpRequestMessage request = new(method, new Uri($"{Root}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Reads a catalogue document as the configured administrator.</summary>
    private async Task<JsonElement> ReadAsync(string path)
    {
        (string token, _) = await SignInAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{Root}{path}?f=json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        // Read first, so a refusal reaches the failure — [D-174](../../docs/architecture-debt.md),
        // the same repair as `ArcGisClient` and for the same reason: this server explains
        // itself in the body and the assertion was firing before anybody read it.
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}. "
            + (string.IsNullOrWhiteSpace(body)
                ? "The response had no body to explain it."
                : "The server said: " + (body.Length <= 300 ? body : body[..300] + "…")));

        return JsonDocument.Parse(body).RootElement.Clone();
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
    /// <summary>A published service's address, as the console addresses it.</summary>
    /// <remarks>
    /// <b><see cref="AnyLayerAsync"/> answers a layer's name, and a page URL wants
    /// `folder/service` — 2026-08-25.</b> Substituting one for the other builds
    /// `/server/#/service/ci_buildings` where the route is
    /// `/server/#/service/hosted/ci_buildings`, and the page renders nothing at all,
    /// which reads as *this service's page shows no sharing scope*. The admin listing
    /// carries both halves; this puts them together once so the next caller does not
    /// have to know that it must.
    /// </remarks>
    protected async Task<string> AnyServiceAddressAsync()
    {
        JsonElement listing = await ReadAsync("/admin/layers");

        JsonElement layers = listing.ValueKind == JsonValueKind.Array
            ? listing
            : listing.TryGetProperty("layers", out JsonElement named)
                ? named
                : default;

        if (layers.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("service", out JsonElement service)
                && service.GetString() is { Length: > 0 } name)
            {
                string folder = layer.TryGetProperty("folder", out JsonElement inside)
                    ? inside.GetString() ?? string.Empty
                    : string.Empty;

                return folder.Length > 0 ? $"{folder}/{name}" : name;
            }
        }

        return string.Empty;
    }

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
    /// <para>
    /// <b>Narrowed 2026-08-18, when Server's listings became paged at ten.</b> This required the
    /// folder's <em>whole</em> contents to be on screen, which a paged list makes false by
    /// design — so a folder of eleven services would have failed every test that opens it. What
    /// it asserts now keeps the defect it was written for and loses nothing else: **the screen is
    /// showing a page of this folder, every row on it is a service the catalogue reports, and
    /// there is at least one.** A folder dropping a service still fails, because a name on screen
    /// that the catalogue does not hold fails, and an empty page fails. What it can no longer
    /// prove on its own is that the *last* service is reachable — `ListPagingTests` proves that,
    /// by turning the page and requiring different rows.
    /// </para>
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
            + "if (want.length === 0) return true; "
            + "if (have.length === 0) return false; "
            + "return have.every(n => want.includes(n)); })()",
            $"The services screen for '{(folder.Length == 0 ? "the root" : folder)}' is not showing "
            + $"a page of what the catalogue says the folder holds ({expected.Length} service(s): "
            + string.Join(", ", expected)
            + "). Either it drew nothing, or it drew a name the catalogue does not report — which "
            + "is a screen reading from somewhere other than this folder.");
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
        await WarmAsync();

        await Browser.NavigateAsync("about:blank");
        await Browser.NavigateAsync(Root + address);
    }

    /// <summary>
    /// Reaches the origin once before the first page under test, and waits for the browser's
    /// certificate verifier to settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is [D-173](../../docs/architecture-debt.md), named at last.</b> That row has
    /// tracked *one console test fails per CI run, never the same test* since 2026-08-26. The
    /// shape narrowed to: a same-origin asset fires `error`, the server's log records **200**
    /// for it, a re-fetch seconds later returns the full bytes, and the browser's timing entry
    /// reads `0B transferred / 0B decoded` with an **empty** protocol while its siblings read
    /// `h2`. Every cheap explanation was eliminated in turn — the cache, a navigation, the
    /// file, the build, the connection.
    /// </para>
    /// <para>
    /// <b>Chrome's own net log answered it on the first run that kept all of them.</b> In the
    /// browser that failed: <c>HTTP2_STREAM_ERROR … ERR_CERT_VERIFIER_CHANGED</c> on
    /// `/studio/ground.js`, and `ERR_FAILED` on `console.js` beside it — **396 milliseconds
    /// into that browser's life**, during its very first page load. Chrome reconfigures its
    /// certificate verifier shortly after start, and abandons whatever is in flight so it can
    /// be verified again. The server had sent the bytes; the browser threw them away and said
    /// nothing a page could catch except `error` on the element.
    /// </para>
    /// <para>
    /// <b>So it is start-up, and the repair is to be past it before the assertion.</b> One
    /// navigation to a cheap endpoint on the same origin, waited out, moves the window that
    /// produced the failure ahead of the page under test. It costs one request per browser and
    /// this suite launches one browser per test.
    /// </para>
    /// <para>
    /// <b>It re-warms rather than assuming once is enough.</b> The verifier can change more
    /// than once; if the warming page recorded a cert-verifier error, that is the event this
    /// exists to absorb and it goes round again. Three attempts, because a browser that cannot
    /// settle in three is a browser with a different problem and should say so through the
    /// test rather than here.
    /// </para>
    /// <para>
    /// <b>What this is not.</b> It is not a retry of the assertion and does not touch what a
    /// test asks of the page: a genuinely lost asset still fails, on the page under test,
    /// through the same listener. What it removes is a browser's own start-up from the
    /// measurement.
    /// </para>
    /// </remarks>
    private async Task WarmAsync()
    {
        if (_warmed)
        {
            return;
        }

        _warmed = true;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            // <b>A page with subresources, and the first version's fault was that this was not
            // one.</b> It warmed on `/healthz/live` — a bare JSON document that fetches
            // nothing — so the probe below had no resource entries to look at, saw no
            // verifier change on any run, and returned after one navigation every time. The
            // handshake was warmed and the thing that actually gets thrown away, a page's
            // subresources, was not: the first real page was still the first page to ask for
            // `console.css`, `session.js` and `surface.js`, and CI went on losing one of them
            // on two runs in three. Measured 2026-09-05 across runs 33927079540 and its
            // re-run: five failures, every one of them a subresource at `0B/0B` on the page
            // under test, `readyState=loading`, 25 to 65 ms in.
            //
            // <b>Both surfaces, because their assets are different URLs.</b> ADR-034 §5a gives
            // the console two prefixes over one set of files, so `/studio/console.css` and
            // `/server/console.css` are two requests and warming one warms nothing about the
            // other. Measured 2026-09-05, run 33928618197: warming only `/studio/` took the
            // failures from five to one, and the one that was left was `/server/ground.js` on a
            // `/server/` page — the same signature, on the surface the warm-up had not
            // visited. Every file at risk is fetched by these two documents: the shell's own
            // `console.css`, `session.js`, `surface.js`, `console.js` and `ground.js`, which is
            // `defer` rather than lazy and so arrives with the document.
            //
            // Neither needs a token to load its shell, and what the page does afterwards does
            // not matter here — the assets have been asked for by the time it settles.
            bool changed = false;

            foreach (string surface in new[] { "/studio/", "/server/" })
            {
                await Browser.NavigateAsync(Root + surface);

                // <b>Asked of the browser, not assumed from the clock.</b> A verifier change
                // during a warming navigation is the thing being waited out, so seeing one
                // means going round again rather than declaring the browser ready.
                changed |= await Browser.EvaluateAsync<bool>(
                    "performance.getEntriesByType('resource')"
                    + ".some(e => e.transferSize === 0 && e.decodedBodySize === 0"
                    + " && e.responseEnd > 0 && !e.nextHopProtocol)");
            }

            if (!changed)
            {
                return;
            }
        }
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

          // <b>Which documents this tab has loaded, in order — D-173.</b> A page that
          // navigates cancels everything in flight, and the cancellation is reported by
          // the browser exactly like a request that failed. The console does navigate on
          // purpose: its surface router calls `location.replace` on a *path*, and signing
          // out replaces the page. `sessionStorage` survives a navigation within the tab
          // where `window` does not, so this is the one record that outlives the document
          // it describes — and a list longer than the opens a test performed is a
          // navigation nobody asked for.
          try {
            const seen = JSON.parse(sessionStorage.getItem("__documents") || "[]");
            seen.push(location.pathname + location.hash);
            sessionStorage.setItem("__documents", JSON.stringify(seen.slice(-8)));
          } catch (e) { /* a storage a browser refuses is not worth a failed page */ }

          window.addEventListener("error", e => {
            window.__pageErrors.push("error: " + (e.message || String(e.error)));
          });

          // <b>A file that never arrived, which the listener above cannot see —
          // [D-176](../../docs/architecture-debt.md).</b> A failed `<script src>` or
          // `<link>` fires `error` on the element and **does not bubble**, so a bubbling
          // listener records the consequence and never the cause: on 2026-08-26 a CI run
          // reported `Uncaught ReferenceError: OSM_TILES is not defined` from `view.js`
          // when what had actually happened was that `ground.js`, which defines it, never
          // loaded. Capture phase is the only way to hear it, and `e.target` is the element
          // rather than the window — which is also how this stays out of the way of the
          // listener above, since a thrown exception targets the window.
          window.addEventListener("error", e => {
            const failed = e.target;

            if (failed && failed !== window && (failed.src || failed.href)) {
              const url = failed.src || failed.href;

              // <b>The two facts that classify it, recorded with it — D-173.</b> The load
              // report reaches a caller only through the two waits, and the assertions that
              // read `__pageErrors` directly are the ones that fail *fast* — so the richest
              // failures were carrying the least. There are fifty-four of those call sites
              // and one of this, which is why the context goes here. `transferSize` is the
              // whole question: a resource that was fetched and delivered nothing accuses
              // the server, and one with no timing entry was never requested.
              // <b>`transferSize` alone cannot tell *nothing arrived* from *served from
              // cache*, and that ambiguity is what has stalled D-173.</b> A cache hit reports
              // `transferSize: 0` with a real `responseEnd`, which is exactly the shape a
              // lost response reports -- so `96ms/0B` has been read as an accusation against
              // the server for two days without anything ruling out the browser's own cache.
              // `decodedBodySize` separates them: bytes the browser *has* after decoding.
              // Zero transferred with a body is a cache hit; zero of both is nothing.
              //
              // <b>And the entry is matched, not popped.</b> `.pop()` takes the last timing
              // entry for the URL, which on a page that fetched it more than once is not
              // necessarily the one that failed. The count is reported so a second entry
              // stops being invisible.
              let how = "no timing entry";

              try {
                const all = (performance.getEntriesByType("resource") || [])
                  .filter(r => r.name === url);

                const timed = all[all.length - 1];

                if (timed) {
                  const transferred = timed.transferSize || 0;
                  const decoded = timed.decodedBodySize || 0;

                  how = Math.round(timed.responseEnd) + "ms/"
                    + transferred + "B transferred/"
                    + decoded + "B decoded"
                    + (transferred === 0 && decoded > 0 ? " (cache hit)" : "")
                    + (all.length > 1 ? ", " + all.length + " entries" : "");
                }
              } catch (ignored) { how = "timing refused"; }

              // <b>Which document it belongs to -- D-173, and this is the fact that
              // classifies it.</b> The CI run of 2026-08-27 reported `/server/session.js`
              // and `/server/console.css` as never arriving with `46ms/0B`, and the
              // server's own log for the same run recorded **200** for both, twice over.
              // So the bytes were sent and the browser abandoned them, which is what a
              // navigation does to everything in flight. The document list is the record
              // that outlives the document, and until now it was collected and never
              // printed.
              let where = "?";

              try {
                const seen = JSON.parse(sessionStorage.getItem("__documents") || "[]");
                where = seen.length + ":" + (seen[seen.length - 1] || location.pathname);
              } catch (ignored) { where = "storage refused"; }

              window.__pageErrors.push(
                "never arrived: " + url
                + " [" + how + ", readyState=" + document.readyState
                + ", document " + where + "]");
            }
          }, true);

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
              // <b>The fields, not only the method and the URL.</b> Knowing that a form posted
              // somewhere is weaker than knowing what it posted, and the difference is a whole class
              // of defect: a control that sends the right request without the field the server
              // requires looks identical from here. Recorded as sorted names because an order is not
              // part of a form's contract, and only for `FormData` — a JSON body is the caller's own
              // string and reading it back would be asserting against the test's own construction.
              const fields = init && init.body instanceof FormData
                ? " [" + [...init.body.keys()].sort().join(",") + "]"
                : "";

              window.__writes.push(method + " " + url + fields);

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
            if (await StillWaitingOrTrueAsync($"!!({expression})"))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(
            $"Ten seconds passed and '{expression}' was never true. {why}\n"
            + $"The address was {await Browser.EvaluateAsync<string>("location.href")} and the "
            + "page's visible text began: "
            + $"{await Browser.EvaluateAsync<string>("(document.body.innerText || '').slice(0, 400)")}\n"
            + await DiagnosisAsync());
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
    /// <summary>
    /// Clicks the selector if the page has one, and does nothing if it does not.
    /// </summary>
    /// <remarks>
    /// <b>For a control that exists on some of a screen's shapes and not others.</b> A service with
    /// layers has a tab strip and a system service does not, and a test walking every service wants the
    /// same steps for both — `ClickAsync` fails after ten seconds when nothing matches, which is right
    /// for a control that must be there and wrong for one that is conditional. Returns whether it
    /// clicked, so a caller can say which case it was in.
    /// </remarks>
    protected async Task<bool> ClickIfPresentAsync(string selector)
    {
        string quoted = JsonSerializer.Serialize(selector);

        return await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.querySelector({quoted}); "
            + "if (!e || e.offsetParent === null) return false; e.click(); return true; })()");
    }

    /// <summary>
    /// Narrows a listing to one name, so a row on a later page becomes reachable.
    /// </summary>
    /// <remarks>
    /// <b>Because the console pages at ten and a test that walks everything cannot
    /// assume everything fits.</b> Found 2026-08-20: publishing an eleventh service
    /// into <c>hosted</c> put it on page two, and
    /// <c>Every_service_reaches_its_own_limits_page_from_a_click</c> failed against a
    /// console that was working correctly. The assumption was invisible while the
    /// folder had ten.
    /// </remarks>
    /// <param name="input">The filter input's element id.</param>
    /// <param name="text">What to type into it.</param>
    protected async Task FilterAsync(string input, string text)
    {
        string quotedId = JsonSerializer.Serialize(input);
        string quotedText = JsonSerializer.Serialize(text);

        // <b>An `input` event, not a value assignment.</b> The console listens for
        // the event; setting `value` alone changes what the box shows and not what
        // the table is filtered by, which is a test that passes while proving the
        // opposite of what it says.
        bool typed = await Browser.EvaluateAsync<bool>(
            $"(() => {{ const e = document.getElementById({quotedId}); if (!e) return false; "
            + $"e.value = {quotedText}; "
            + "e.dispatchEvent(new Event('input', { bubbles: true })); return true; })()");

        Assert.True(typed, $"There is no filter input with id '{input}' on this screen.");
    }

    /// <summary>
    /// The condition <i>this control is on screen</i>, written so that a missing one fails it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because the obvious spelling is true when the element is absent.</b>
    /// <c>document.getElementById('x')?.offsetParent !== null</c> reads as *x is visible* and
    /// evaluates to <c>undefined !== null</c> — <b>true</b> — the moment there is no x. Every
    /// wait written that way passes instantly on a screen that drew nothing, and the failure
    /// surfaces on the next line as a null reference with no idea what it was waiting for.
    /// Found 2026-09-06 when a wait for a dialog passed before the dialog existed; it was in
    /// ten files and twenty-three places by then.
    /// </para>
    /// <para>
    /// <b>Which matters more here than in most suites.</b> This console has shipped a control
    /// that existed and rendered nowhere three separate times, and <c>offsetParent</c> is the
    /// check written to catch exactly that — so an expression that cannot tell *invisible* from
    /// *absent* defeats the guard while looking like it.
    /// </para>
    /// </remarks>
    /// <param name="selector">A CSS selector for the control.</param>
    /// <returns>A JavaScript expression that is true only when it is there and visible.</returns>
    protected static string Shown(string selector)
    {
        string quoted = JsonSerializer.Serialize(selector);

        return $"(() => {{ const e = document.querySelector({quoted}); "
            + "return !!e && e.offsetParent !== null; })()";
    }

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
            + $"{await Browser.EvaluateAsync<string>("location.href")}.\n"
            + await DiagnosisAsync());
    }

    /// <summary>
    /// What the page can still be asked when a wait has run out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-172](../../docs/architecture-debt.md): the suite already knew and did not
    /// say.</b> `window.__pageErrors` is planted before the console's own scripts and has
    /// recorded every throw since it was written — and neither wait printed it, so a
    /// timeout arrived as *ten seconds passed* and nothing else. Two CI runs on 2026-08-26
    /// failed that way on two different tests, and the failures read as unrelated flakes
    /// because the one line that would have tied them together was collected and discarded.
    /// </para>
    /// <para>
    /// <b>It also says whether `console.js` arrived.</b> A timeout on a global has exactly
    /// three shapes — the script was never requested, it was requested and is still coming,
    /// or it ran and threw — and they need different repairs. `readyState` and the resource
    /// entry separate the first two; `__pageErrors` separates the third.
    /// </para>
    /// <para>
    /// <b>Nothing here may throw.</b> This runs inside a failure, and an exception raised
    /// while explaining one replaces the assertion with itself — which is how a diagnostic
    /// becomes the thing being diagnosed.
    /// </para>
    /// </remarks>
    private async Task<string> DiagnosisAsync()
    {
        string errors;

        try
        {
            string[] thrown = await PageErrorsAsync();

            errors = thrown.Length == 0
                ? "The page recorded no errors of its own."
                : $"The page recorded {thrown.Length} error(s): "
                    + string.Join(" | ", thrown.Take(5));
        }
        catch (Exception asking)
        {
            errors = $"The page could not be asked what it recorded: {asking.Message}";
        }

        try
        {
            // Null-safe throughout, per this class's own rule: a router that has not
            // rendered yet must produce a report rather than an exception.
            string report = await Browser.EvaluateAsync<string>(
                """
                (async () => {
                  try {
                    const timings = {};

                    for (const r of (performance.getEntriesByType('resource') || [])) {
                      // The same separation the recorded errors carry -- D-173. A cache
                      // hit and a response that delivered nothing both report
                      // `transferSize: 0`, and reading the first as the second is how this
                      // has been chased for two days.
                      // <b>The protocol, because it is the last untested hypothesis --
                      // D-173.</b> A re-fetch of the three that delivered nothing returns
                      // 200 with the full bytes, so the files are fine and the first
                      // delivery lost them. What separates the victims from the survivors on
                      // one page is not size, not order and not the connection: `h2` beside
                      // `http/1.1` would separate them in one field.
                      timings[r.name] = Math.round(r.responseEnd) + 'ms/'
                        + (r.transferSize || 0) + 'B/'
                        + (r.decodedBodySize || 0) + 'B'
                        + '/' + (r.nextHopProtocol || '?')
                        + ((r.transferSize || 0) === 0 && (r.decodedBodySize || 0) > 0
                            ? '(cached)' : '');
                    }

                    // <b>Every script, not one by name — D-176.</b> The first version
                    // reported `console.js` alone, because that was the file the failures
                    // in hand were about. The next one was about `ground.js` and the
                    // report had nothing to say. What a page is waiting for is whichever
                    // script did not arrive, so all of them are listed with what the
                    // browser recorded fetching — and `0B` on a script that is on the
                    // page is the shape being hunted.
                    //
                    // <b>And stylesheets, because the third one was about `console.css` —
                    // D-173.</b> A stylesheet is the page's *first* subresource, so
                    // whether it was fetched at all separates a failed request from a
                    // cancelled one, and that distinction is the open question: the
                    // console's own surface router calls `location.replace` on a path,
                    // which cancels whatever is in flight. `never fetched` beside a
                    // recorded error is a cancellation; a timing beside one is a
                    // response that did not land.
                    const listed = [];

                    for (const s of Array.from(document.scripts || [])) {
                      const name = (s.src || '').split('/').pop() || 'inline';
                      listed.push(s.src
                        ? name + '=' + (timings[s.src] || 'never fetched')
                        : name);
                    }

                    for (const l of Array.from(
                        document.querySelectorAll('link[rel=stylesheet]') || [])) {
                      const name = (l.href || '').split('/').pop() || 'inline';
                      listed.push(l.href
                        ? name + '=' + (timings[l.href] || 'never fetched')
                        : name);
                    }

                    let documents = 'unknown';

                    try {
                      documents = sessionStorage.getItem('__documents') || '[]';
                    } catch (e) { documents = 'storage refused'; }

                    // <b>Ask the server again for whatever delivered nothing — D-173.</b>
                    // As of 2026-08-27 the shape is exact and the cause is not: the browser
                    // records `0B transferred/0B decoded` on the page's three `defer`
                    // scripts while a stylesheet on the same connection arrives whole, one
                    // document, one timing entry each — and the server's log records **200**
                    // for all of them and closes the connection with *the send loop
                    // completed gracefully*. Two accounts that cannot both be complete.
                    //
                    // A fetch settles the half nobody has asked: whether the bytes are there
                    // to be had. **466303 bytes on a re-fetch** means the file is fine and
                    // the first delivery lost them; **0** means the server is serving an
                    // empty file and the browser is right.
                    //
                    // <b>Only for assets that recorded nothing, and only on a failure.</b>
                    // This runs when a wait has already timed out, so it costs a broken run
                    // three requests and a passing run none.
                    const empty = listed
                      .filter(a => a.indexOf('/0B/0B') > 0 || a.indexOf('=0ms/0B') > 0)
                      .map(a => a.split('=')[0]);

                    let refetched = '';

                    for (const name of empty.slice(0, 3)) {
                      try {
                        const url = new URL(name, location.href).href;
                        const again = await fetch(url, { cache: 'no-store' });
                        const body = await again.arrayBuffer();

                        refetched += ' ' + name + '=' + again.status + '/'
                          + body.byteLength + 'B'
                          + '/' + (again.headers.get('content-length') || 'no length');
                      } catch (e) {
                        refetched += ' ' + name + '=refetch threw: ' + String(e).slice(0, 60);
                      }
                    }

                    return 'readyState=' + document.readyState
                      + ' at=' + location.pathname
                      + ' assets=[' + listed.join(', ') + ']'
                      + ' documents=' + documents
                      + (refetched ? ' refetched:' + refetched : '');
                  } catch (e) { return 'the report itself threw: ' + e; }
                })()
                """) ?? "no load report";

            return errors + "\n" + report;
        }
        catch (Exception asking)
        {
            return $"{errors}\nThe page could not be asked how it loaded: {asking.Message}";
        }
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
    /// <summary>
    /// Fails with everything the page reported, rather than with the first fifty characters.
    /// </summary>
    /// <param name="reported">What <see cref="PageErrorsAsync"/> returned.</param>
    /// <remarks>
    /// <para>
    /// <b>[D-173](../../docs/architecture-debt.md), and this is about the instrument rather
    /// than the defect.</b> Forty-nine assertions said <c>Assert.Empty(errors)</c>, and xUnit
    /// renders a non-empty collection as <c>Collection: ["first fifty characters"···]</c>. The
    /// diagnosis added for exactly this failure — how long the request took, how many bytes
    /// arrived, what the document's readyState was — is past the truncation, so a CI run that
    /// finally carried the evidence printed *"never arrived: https://127.0.0.1:8443/studio/previ"*
    /// and stopped.
    /// </para>
    /// <para>
    /// <b>One line each, because the list is the finding.</b> A run that loses one asset and a
    /// run that loses the whole deferred batch are different faults with the same first entry.
    /// </para>
    /// </remarks>
    protected static void NothingWentWrong(IReadOnlyCollection<string> reported)
    {
        ArgumentNullException.ThrowIfNull(reported);

        Assert.True(
            reported.Count == 0,
            $"The page reported {reported.Count} problem(s):"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", reported));
    }

    /// <summary>
    /// What the page reported, and — when it reported anything — the documents this tab
    /// loaded, which is what tells a cancelled request from a missing file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The trail goes here rather than at the fifty-odd assertions — D-173.</b> The
    /// document list has been collected since 2026-08-26 and never printed, so the run that
    /// finally carried it printed everything except the one fact that classifies it. Putting
    /// it in the reader means every caller gets it and none had to be edited, which is also
    /// how it avoids being a second mechanism beside `NothingWentWrong`.
    /// </para>
    /// <para>
    /// <b>Only when something went wrong.</b> A passing test that appended a document list
    /// to an empty collection would turn every `Assert.Empty` in the suite red.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Evaluates a wait's expression, treating a page that is navigating as *not yet*.
    /// </summary>
    /// <param name="expression">The expression, already wrapped in a truth test.</param>
    /// <returns>Whether it is true now.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-173](../../docs/architecture-debt.md)'s fourth distinct cause, CI 2026-08-27.</b>
    /// `SurfaceTests.Without_admin_manageServer_there_is_no_Server_surface_to_see` failed with
    /// `Runtime.evaluate failed: {"code":-32000,"message":"Inspected target navigated or
    /// closed"}` — and unlike every earlier occurrence, **the navigation is real and is the
    /// behaviour under test**: a reader without `admin:manageServer` is moved out of Server by
    /// the console's own router, which calls `location.replace` on a path. The test waits for
    /// exactly that redirect and can be evaluating at the instant it commits.
    /// </para>
    /// <para>
    /// <b>Not yet, rather than an error, and that is not a weakening.</b> A wait's contract is
    /// *keep asking until this is true*; a target that is mid-navigation has not answered
    /// either way, so the honest reading is *not yet* and the loop's own hundred attempts
    /// bound it. A wait that gave up because the page moved was reporting the browser's
    /// timing rather than the server's behaviour.
    /// </para>
    /// <para>
    /// <b>Only this message.</b> Every other DevTools failure still throws — a page that
    /// threw, a target that died, a protocol error. Swallowing those would turn a wait into a
    /// timeout with no cause, which is what this suite spent two days undoing.
    /// </para>
    /// </remarks>
    private async Task<bool> StillWaitingOrTrueAsync(string expression)
    {
        try
        {
            return await Browser.EvaluateAsync<bool>(expression);
        }
        catch (InvalidOperationException moving)
            when (moving.Message.Contains("navigated or closed", StringComparison.Ordinal))
        {
            return false;
        }
    }

    protected async Task<string[]> PageErrorsAsync()
    {
        string[] reported =
            await Browser.EvaluateAsync<string[]>("window.__pageErrors")
                ?? Array.Empty<string>();

        if (reported.Length == 0)
        {
            return reported;
        }

        string[] documents =
            await Browser.EvaluateAsync<string[]>(
                "JSON.parse(sessionStorage.getItem('__documents') || '[]')")
                ?? Array.Empty<string>();

        return documents.Length == 0
            ? reported
            : [.. reported, "documents this tab loaded, in order: " + string.Join(" -> ", documents)];
    }

    /// <summary>What the page tried to write while the test was running.</summary>
    protected async Task<string[]> WritesAsync() =>
        await Browser.EvaluateAsync<string[]>("window.__writes") ?? Array.Empty<string>();
}
