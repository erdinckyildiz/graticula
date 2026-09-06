using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Talks to a running server the way an ArcGIS client does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Over HTTP against a real process, not in-process.</b> An in-process host
/// skips TLS, Kestrel, the middleware order and the routing table — which is
/// most of what a client actually meets. The three defects found by stopping the
/// datastore were all in that layer, and none of them would have appeared in a
/// <c>WebApplicationFactory</c>.
/// </para>
/// <para>
/// <b>This suite may not reference our own assemblies.</b> A conformance test
/// that asserts against the same constant the server reads will agree with the
/// server while both are wrong. Everything here is a string, a number, or a
/// shape read out of JSON — which is all a client has.
/// </para>
/// </remarks>
public abstract class ArcGisClient : IDisposable
{
    /// <summary>Where the server under test is.</summary>
    public const string UrlVariable = "GRATICULA_TEST_URL";

    /// <summary>Who to sign in as, if anybody.</summary>
    public const string UserVariable = "GRATICULA_TEST_USER";

    /// <summary>That account's password.</summary>
    public const string PasswordVariable = "GRATICULA_TEST_PASSWORD";

    /// <summary>
    /// One sign-in shared by every test, or null when running anonymously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Once, because signing in per test measures Argon2 rather than
    /// conformance.</b> The hash is deliberately expensive; sixty of them turn a
    /// two-second suite into a minute of key derivation.
    /// </para>
    /// <para>
    /// <b>And optional, because most of this suite should run anonymously.</b>
    /// Public layers are reachable without an account and a suite that always
    /// signed in could not tell the difference. The credentials exist for the
    /// services that are not public — the geometry service is shared with the
    /// organisation, so an anonymous client is <em>supposed</em> to get 404.
    /// </para>
    /// </remarks>
    private static readonly SemaphoreSlim SignInLock = new(1, 1);
    private static string? _token;
    private static bool _signedIn;

    private readonly HttpClient _http;

    /// <summary>
    /// The client this fixture holds, for a test that needs to send its own request.
    /// </summary>
    /// <remarks>
    /// <b>Exposed rather than reconstructed.</b> A test that built its own would need the
    /// certificate handler and the timeout again, and a second one of those is a second place for
    /// them to drift — which is D-46's subject. The tests that need it are the ones acting as
    /// somebody other than the configured administrator.
    /// </remarks>
    protected HttpClient Http => _http;
    private bool _disposed;

    /// <summary>Creates the client.</summary>
    protected ArcGisClient()
    {
        // <b>D-60: one database-backed suite at a time.</b> This suite does not
        // touch PostgreSQL itself; the server it drives does, with its own pool,
        // and that is the load the Postgres suite's timeouts were measuring.
        Graticula.Testing.OneSuiteAtATime.Enter();

        HttpClientHandler handler = new()
        {
            // ADR-014 generates a self-signed certificate on start, so a client
            // that validated the chain could never reach a development server.
            // Accepted here and nowhere else: this is a test harness pointed at
            // a host the operator named.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The base URL, or null when the harness is not configured.</summary>
    protected static string? BaseUrl => Environment.GetEnvironmentVariable(UrlVariable);

    /// <summary>
    /// Refuses to run without a server, rather than passing quietly.
    /// </summary>
    /// <remarks>
    /// The fourth time this project has needed this guard. A conformance suite
    /// that goes green because nothing was listening is worse than no suite: it
    /// reports that a compatibility claim holds, which is the one thing it
    /// exists to check.
    /// </remarks>
    /// <summary>
    /// The address of some service a client could add, folders included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Following the folders is what a client does, and several tests were
    /// not doing it.</b> They read <c>/rest/services</c>, took the first entry
    /// of its <c>services</c> array, and failed with "no services are visible
    /// anonymously" against a server whose services all live in a folder. Every
    /// hosted layer lands in <c>hosted</c>, so that is every server published
    /// entirely from the hosting API -- including the one CI builds from
    /// nothing. The old shape passed only on a machine that happened to have a
    /// service at the root, which is a fact about that machine.
    /// </para>
    /// <para>
    /// <b>The returned name already carries its folder</b>, so the caller
    /// composes <c>/rest/services/{name}/FeatureServer</c> exactly as before.
    /// </para>
    /// </remarks>
    protected async Task<string?> AnyServiceNameAsync()
    {
        JsonElement catalogue = await GetJsonAsync("/rest/services");

        // <b>Fixtures are skipped here too, and this one was missed the first time.</b>
        // `EveryServiceNameAsync` learnt to skip them and this did not, so a caller asking for
        // *any* service could be handed a `corpus_` layer that another class deletes three
        // requests later. D-89, found again by the run after the repair.
        if (catalogue.TryGetProperty("services", out JsonElement services)
            && services.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement service in services.EnumerateArray())
            {
                if (service.TryGetProperty("name", out JsonElement named)
                    && named.GetString() is { Length: > 0 } root
                    && !Fixture(root))
                {
                    return root;
                }
            }
        }

        if (!catalogue.TryGetProperty("folders", out JsonElement folders)
            || folders.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement folder in folders.EnumerateArray())
        {
            string? name = folder.GetString();

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            JsonElement inside = await GetJsonAsync($"/rest/services/{name}");

            if (inside.TryGetProperty("services", out JsonElement found)
                && found.ValueKind == JsonValueKind.Array
                && found.GetArrayLength() > 0)
            {
                // <b>Feature services only.</b> The Utilities folder holds the
                // geometry service, which has no layers and would fail every
                // assertion the callers make about one.
                foreach (JsonElement service in found.EnumerateArray())
                {
                    if (service.TryGetProperty("type", out JsonElement type)
                        && string.Equals(type.GetString(), "FeatureServer",
                            StringComparison.Ordinal)
                        && service.GetProperty("name").GetString() is { Length: > 0 } inFolder
                        && !Fixture(inFolder))
                    {
                        return inFolder;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every FeatureServer a client could add, folders included.
    /// </summary>
    /// <returns>Qualified service names, in the order the catalogue lists them.</returns>
    /// <remarks>
    /// <para>
    /// <b>Because "the first service" is a fact about the fixture, not about the
    /// server.</b> Most of this suite asks its question of <c>AnyServiceNameAsync</c>,
    /// which returns whichever service the catalogue happens to list first — so an
    /// invariant asserted that way is asserted about one layer, and the other nine are
    /// unexamined.
    /// </para>
    /// <para>
    /// <b>It cost a real defect, and the test that should have caught it was passing.</b>
    /// On 2026-08-18 `Pages_do_not_overlap_or_skip` went red the moment an unrelated
    /// change moved which layer came first — and the bug it then found (D-21: the first
    /// page ordered differently from every later one) had been live for four days on three
    /// of the owner's ten layers. The seven it was not visible on were small enough that
    /// heap order happened to be identity order. A test whose coverage is decided by row
    /// order is a test that reports on the data rather than on the server.
    /// </para>
    /// <para>
    /// <b>So the invariants walk all of them.</b> Not every test — a test about one
    /// document's shape has nothing to gain from repetition — but the ones whose claim is
    /// *for every layer this server serves*.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The name prefixes that belong to a test fixture rather than to a deployment.
    /// </summary>
    /// <remarks>
    /// <b>`zz_` belongs to the sharing, archive-refusal, lifecycle and delete suites;
    /// `corpus_` to `ShapefileCorpusTests`.</b> Both are published and deleted while other
    /// classes walk the server, which is [D-89](../../docs/architecture-debt.md).
    /// </remarks>
    private static readonly string[] FixturePrefixes = ["zz_", "corpus_"];

    /// <summary>
    /// Whether a name belongs to a fixture this suite creates and deletes as it runs.
    /// </summary>
    /// <param name="name">The service or collection name, qualified or not.</param>
    /// <returns>True when it is a fixture rather than something the deployment publishes.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-89](../../docs/architecture-debt.md), and the row said neither of its two repairs
    /// was obvious.</b> Skipping fixture names, it argued, hides a class of service from the only
    /// tests that check all of them; the alternative was to stop the suites sharing a server,
    /// which changes how every integration test runs.
    /// </para>
    /// <para>
    /// <b>The objection does not hold for these names, and the reason is worth being exact
    /// about.</b> A `zz_` or `corpus_` service is not a class of service this walk would
    /// otherwise cover: it is created by a test that then asserts on it directly, with knowledge
    /// of exactly what it should contain, and deleted seconds later. Walking it adds no coverage
    /// and subtracts a stable suite. What the row is right about is that a *skip* must not be the
    /// only mechanism, because a skip cannot tell a deleted service from a broken one — and that
    /// is what <see cref="StillListedAsync"/> is for. The two together are the repair: fixtures
    /// are not the walk's subject, and everything else that 404s is a defect until the catalogue
    /// says otherwise.
    /// </para>
    /// <para>
    /// <b>And removing the skip was tried first, which is how the second reason was found.</b>
    /// With the fixtures walked, the suite put 24 requests in flight against one data source with
    /// 96 more queued and the server shed load exactly as
    /// [ADR-046](../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md) says
    /// it should: `ConnectionBudgetFullException`, 503, two tests failing on a healthy server.
    /// The fixtures roughly double how many services a full run walks, and every walk is
    /// multiplied by every walking test.
    /// </para>
    /// </remarks>
    protected static bool Fixture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (string prefix in FixturePrefixes)
        {
            if (name.Contains(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    protected async Task<IReadOnlyList<string>> EveryServiceNameAsync()
    {
        List<string> found = [];
        JsonElement root = await GetJsonAsync("/rest/services");

        Collect(root, found);

        if (root.TryGetProperty("folders", out JsonElement folders)
            && folders.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in folders.EnumerateArray())
            {
                if (folder.GetString() is { Length: > 0 } name)
                {
                    Collect(await GetJsonAsync($"/rest/services/{name}"), found);
                }
            }
        }

        Assert.NotEmpty(found);
        return found;

        // <b>Feature services only.</b> The Utilities folder holds the geometry service,
        // which has no layers and would fail every assertion a caller makes about one.
        static void Collect(JsonElement catalogue, List<string> into)
        {
            if (!catalogue.TryGetProperty("services", out JsonElement services)
                || services.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement service in services.EnumerateArray())
            {
                if (service.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "FeatureServer", StringComparison.Ordinal)
                    && service.TryGetProperty("name", out JsonElement name)
                    && name.GetString() is { Length: > 0 } qualified
                    && !Fixture(qualified)
                    && !into.Contains(qualified))
                {
                    into.Add(qualified);
                }
            }
        }
    }

    protected async Task<string> RequireServerAsync()
    {
        Assert.False(
            string.IsNullOrWhiteSpace(BaseUrl),
            $"{UrlVariable} is not set, so these tests FAIL rather than skip. Start the server "
            + "and set it, e.g. https://127.0.0.1:8443. They walk the request sequence a real "
            + "ArcGIS client makes; passing them with nothing listening would assert that the "
            + "compatibility claim holds.");

        string root = BaseUrl!.TrimEnd('/');

        using HttpResponseMessage live = await _http.GetAsync(new Uri($"{root}/healthz/live"));

        Assert.True(
            live.IsSuccessStatusCode,
            $"{root} did not answer /healthz/live ({(int)live.StatusCode}). The server must be "
            + "running and migrated before this suite means anything.");

        return root;
    }

    /// <summary>
    /// The bearer token for the configured account, or null if none is set.
    /// </summary>
    /// <param name="root">The server root.</param>
    /// <returns>The token, or null.</returns>
    protected static async Task<string?> TokenAsync(string root)
    {
        if (_signedIn)
        {
            return _token;
        }

        await SignInLock.WaitAsync();

        try
        {
            if (_signedIn)
            {
                return _token;
            }

            string? user = Environment.GetEnvironmentVariable(UserVariable);
            string? password = Environment.GetEnvironmentVariable(PasswordVariable);

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            {
                _signedIn = true;
                return null;
            }

            using HttpClientHandler handler = new()
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };

            using HttpClient http = new(handler);

            using HttpResponseMessage response = await http.PostAsync(
                new Uri($"{root}/rest/auth/login"),
                new StringContent(
                    JsonSerializer.Serialize(new { name = user, password }),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            // A configured-but-wrong credential fails loudly. Falling back to
            // anonymous would turn every authorization test into a test of
            // whether the resource is public, and they would all still pass.
            Assert.True(
                response.IsSuccessStatusCode,
                $"{UserVariable} is set but signing in returned {(int)response.StatusCode}. "
                + "Fix the credentials or unset them; running anonymously instead would quietly "
                + "change what this suite is testing.");

            JsonElement body = JsonDocument
                .Parse(await response.Content.ReadAsStringAsync()).RootElement;

            _token = body.GetProperty("token").GetString();
            _signedIn = true;

            return _token;
        }
        finally
        {
            SignInLock.Release();
        }
    }

    /// <summary>Attaches the bearer token to a request, if there is one.</summary>
    /// <param name="request">The request.</param>
    /// <param name="root">The server root.</param>
    protected static async Task AuthenticateAsync(HttpRequestMessage request, string root)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await TokenAsync(root) is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// Fetches a document as a caller with no credential, and reports what
    /// happened instead of asserting that it worked.
    /// </summary>
    /// <param name="path">Path and query, relative to the root.</param>
    /// <returns>The status, and the body when there was one.</returns>
    /// <remarks>
    /// <b>The point of this method is the line it does not have.</b> Every other
    /// request here calls <see cref="AuthenticateAsync"/>, so the whole suite sees
    /// the server as an administrator — which is the one caller whose experience
    /// proves least about a product whose promise is that an unmodified client
    /// keeps working. This deliberately omits the header, and returns the status
    /// rather than throwing on it, because here a refusal is the measurement.
    /// </remarks>
    protected async Task<(HttpStatusCode Status, string Body)> AnonymousAsync(string path)
    {
        string root = await RequireServerAsync();
        string separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        Uri uri = new($"{root}{path}{separator}f=json");

        using HttpRequestMessage request = new(HttpMethod.Get, uri);

        using HttpResponseMessage response = await _http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Fetches a document, or answers null when the server says there is no such thing.
    /// </summary>
    /// <param name="path">Path and query, relative to the root.</param>
    /// <returns>The document, or null on 404.</returns>
    /// <remarks>
    /// <b>Only 404, and only for a caller that then asks whether it was really absent.</b>
    /// Anything else still fails the test. See <see cref="StillListedAsync"/> for what a walk
    /// does with the null, and why swallowing the 404 on its own would be the wrong repair.
    /// </remarks>
    protected async Task<JsonElement?> TryGetJsonAsync(string path)
    {
        string root = await RequireServerAsync();

        string separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        Uri uri = new($"{root}{path}{separator}f=json");

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await _http.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {uri} returned {(int)response.StatusCode}. An ArcGIS client stops here. "
            + Said(body));

        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException e)
        {
            Assert.Fail(
                $"GET {uri} did not return parseable JSON: {e.Message}"
                + $"\n{body[..Math.Min(400, body.Length)]}");
            throw;
        }
    }

    /// <summary>
    /// Whether the catalogue still lists this service, asked after it answered 404.
    /// </summary>
    /// <param name="qualifiedName">The name as the directory gave it, folder and all.</param>
    /// <returns>True when the directory still names it, which makes the 404 a real defect.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-89](../../docs/architecture-debt.md), and this is the repair the row said was not
    /// obvious.</b> These suites walk the whole server while three others create and delete
    /// fixtures beside them, so a service listed at the top of a walk can answer 404 three
    /// requests later — a false failure naming a real endpoint and a real 404. The row's two
    /// candidate repairs were to skip fixture names, which hides a class of service from the
    /// only tests that check all of them, or to stop the suites sharing a server, which changes
    /// how every integration test runs.
    /// </para>
    /// <para>
    /// <b>This is a third one, and it hides nothing.</b> A 404 is not treated as *gone*; it is
    /// treated as a question, and the question is asked of the catalogue. If the service is
    /// still listed, the server is advertising something it will not serve and the walk fails —
    /// which is exactly the defect a conformance walk exists to find. If it is no longer listed,
    /// it was deleted between the listing and the request and there is nothing to report.
    /// </para>
    /// <para>
    /// <b>Costs one directory read per 404</b>, which is twice a run, and nothing at all on a
    /// run where nothing vanishes.
    /// </para>
    /// </remarks>
    protected async Task<bool> StillListedAsync(string qualifiedName)
    {
        foreach (string name in await EveryServiceNameAsync())
        {
            if (string.Equals(name, qualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fetches a document about one service, or null when that service has gone.
    /// </summary>
    /// <param name="service">The service, qualified as the directory named it.</param>
    /// <param name="path">Path and query, relative to the root.</param>
    /// <returns>The document, or null when the service is no longer catalogued.</returns>
    /// <remarks>
    /// <b>The walk's half of <see cref="StillListedAsync"/>.</b> A null here means *skip this
    /// one*; a service that 404s and is still catalogued fails inside this method, with the
    /// service named, rather than being skipped quietly.
    /// </remarks>
    protected async Task<JsonElement?> AboutServiceAsync(string service, string path)
    {
        if (await TryGetJsonAsync(path) is { } document)
        {
            return document;
        }

        Assert.False(
            await StillListedAsync(service),
            $"GET {path} returned 404 and '{service}' is still in the services directory. The "
            + "catalogue is advertising a service the server will not serve, which is a defect "
            + "rather than the fixture race D-89 records.");

        return null;
    }

    /// <summary>Fetches a document, asserting it came back as JSON.</summary>
    /// <param name="path">Path and query, relative to the root.</param>
    protected async Task<JsonElement> GetJsonAsync(string path)
    {
        string root = await RequireServerAsync();

        // f=json on every request, which is what a client sends. A server that
        // only works without it works for curl and for nothing else.
        string separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        Uri uri = new($"{root}{path}{separator}f=json");

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await _http.SendAsync(request);

        // <b>Read before asserting, so the refusal is in the failure —
        // [D-174](../../docs/architecture-debt.md).</b> This server answers a refusal with a
        // sentence naming the cause, and the assertion below fired one line before the body
        // was read, so every failure here said only *returned 503*. Four of its causes are
        // 503 — an unreachable source, a full connection budget, an unreadable platform
        // store and an undecryptable credential — and they need four different repairs.
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {uri} returned {(int)response.StatusCode}. An ArcGIS client stops here. "
            + Said(body));

        Assert.Contains(
            "json",
            response.Content.Headers.ContentType?.MediaType ?? "none",
            StringComparison.OrdinalIgnoreCase);

        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException e)
        {
            Assert.Fail($"GET {uri} did not return parseable JSON: {e.Message}\n{body[..Math.Min(400, body.Length)]}");
            throw;
        }
    }

    /// <summary>
    /// Fetches a page as a browser would, asserting it came back as HTML.
    /// </summary>
    /// <param name="path">Path and query, relative to the root.</param>
    /// <returns>The page.</returns>
    /// <remarks>
    /// <b>An Accept header and no <c>f</c>, which is exactly what a browser
    /// sends.</b> Asking with <c>?f=html</c> would test the parameter and leave
    /// the header path — the one a person typing a URL actually takes —
    /// unexercised.
    /// </remarks>
    protected async Task<string> GetHtmlAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await _http.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {root}{path} returned {(int)response.StatusCode} for a browser. "
            + Said(body));

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        return body;
    }

    /// <summary>The server's own explanation, trimmed for a failure message.</summary>
    /// <param name="body">What came back.</param>
    /// <remarks>
    /// <b>Trimmed rather than dropped.</b> A refusal from this server is a sentence and
    /// fits; an HTML error page does not, and three hundred characters of one still says
    /// which page it is. The alternative that was there — printing nothing — is what made a
    /// 503 unattributable.
    /// </remarks>
    private static string Said(string body) =>
        string.IsNullOrWhiteSpace(body)
            ? "The response had no body to explain it."
            : "The server said: "
                + (body.Length <= 300 ? body : body[..300] + "…");

    /// <summary>
    /// Fetches with an explicit format and a browser's Accept header.
    /// </summary>
    /// <param name="path">Path and query, relative to the root.</param>
    /// <param name="format">The <c>f</c> value.</param>
    /// <returns>The media type that came back.</returns>
    protected async Task<string?> MediaTypeForAsync(string path, string format)
    {
        string root = await RequireServerAsync();

        string separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        using HttpRequestMessage request =
            new(HttpMethod.Get, new Uri($"{root}{path}{separator}f={format}"));

        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await _http.SendAsync(request);

        return response.Content.Headers.ContentType?.MediaType;
    }

    /// <summary>Fetches a document and returns its status without asserting.</summary>
    protected async Task<int> StatusOfAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await _http.SendAsync(request);
        return (int)response.StatusCode;
    }

    /// <summary>
    /// The first table in the fixture's hosted schema that no layer claims, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A service cannot be created without layers, so every class that needs one needs a
    /// table</b> — ADR-057 condition 4, owner decision 2026-09-06. Three classes used to make an
    /// empty container with <c>POST /admin/featureservices</c> and were about something else
    /// entirely; they publish a real layer now, and this is what they publish over.
    /// </para>
    /// <para>
    /// <b>CI has exactly two of these and they are shared.</b> <c>tools/ci-free-tables.sql</c>
    /// makes <c>hosted.zz_free_one</c> and <c>zz_free_two</c>; a developer machine has dozens.
    /// Nothing here reserves one, and nothing needs to: every class that touches the catalogue
    /// is in the <c>catalogue walk</c> collection, which xUnit runs one at a time — the
    /// architecture suite has a test that keeps it that way. What each caller owes is cleanup,
    /// because a class that keeps its table takes it from the next one.
    /// </para>
    /// <para>
    /// <b>Read from the server rather than named, because the two fixtures differ.</b> Pinning
    /// <c>zz_free_one</c> works in CI and picks a table a developer's server already serves.
    /// </para>
    /// </remarks>
    /// <param name="skip">How many free tables to pass over, for a caller that needs two.</param>
    /// <returns>Everything a publish needs, or null when the fixture has none free.</returns>
    protected async Task<(string Schema, string Table, string Geometry, string Type,
        string Identity, int Srid)?> FreeTableAsync(int skip = 0)
    {
        if (await DatastoreIdAsync() is not { Length: > 0 } datastore)
        {
            return null;
        }

        (int seen, string capability) = await AdminAsync(
            HttpMethod.Get, $"/admin/datasources/{datastore}/capability");

        if (seen != 200)
        {
            return null;
        }

        (int listed, string layers) = await AdminAsync(HttpMethod.Get, "/admin/layers");

        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        if (listed == 200)
        {
            // <b>`table`, which is the qualified name the listing carries.</b> There is no
            // `schemaName` on a layer row — reading one would leave this set empty and hand back
            // a table that is already served, which is a 409 from `layer_table_unique` and a
            // confusing one, because the sentence is about a table and the test is about a name.
            foreach (JsonElement layer in JsonDocument.Parse(layers)
                .RootElement.GetProperty("layers").EnumerateArray())
            {
                if (layer.TryGetProperty("table", out JsonElement qualified)
                    && qualified.GetString() is { Length: > 0 } where)
                {
                    taken.Add(where);
                }
            }
        }

        int passed = 0;

        foreach (JsonElement table in JsonDocument.Parse(capability)
            .RootElement.GetProperty("tables").EnumerateArray())
        {
            if (!table.TryGetProperty("geometryColumn", out JsonElement geometry)
                || geometry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // <b>Nominated, not inferred — Q-57.</b> `POST /admin/layers` requires an identity
            // column and the probe reports which ones qualify; a table with none cannot be
            // published at all, so it is not a free table for this purpose.
            if (!table.TryGetProperty("objectIdColumn", out JsonElement identity)
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

            if (passed++ < skip)
            {
                continue;
            }

            return (
                schema,
                named,
                geometry.GetString()!,
                table.TryGetProperty("geometryType", out JsonElement kind)
                    && kind.ValueKind == JsonValueKind.String
                        ? kind.GetString()!
                        : "Polygon",
                identity.GetString()!,
                table.TryGetProperty("srid", out JsonElement srid)
                    && srid.ValueKind == JsonValueKind.Number
                        ? srid.GetInt32()
                        : 3857);
        }

        return null;
    }

    /// <summary>The datastore data source's id, or null when there is none.</summary>
    /// <returns>Its id.</returns>
    protected async Task<string?> DatastoreIdAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/datasources");

        if (status != 200)
        {
            return null;
        }

        foreach (JsonElement source in JsonDocument.Parse(body)
            .RootElement.GetProperty("dataSources").EnumerateArray())
        {
            if (source.TryGetProperty("name", out JsonElement name)
                && string.Equals(name.GetString(), "datastore", StringComparison.Ordinal))
            {
                return source.GetProperty("id").GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Publishes one service made of one layer, which is the only way a service is created.
    /// </summary>
    /// <remarks>
    /// <b><c>POST /admin/publish</c>, because <c>POST /admin/featureservices</c> refuses
    /// now</b> — ADR-057 condition 4. The caller owes the teardown; see
    /// <see cref="UnpublishAsync"/>.
    /// </remarks>
    /// <param name="service">What to call it.</param>
    /// <param name="layer">What to call its layer.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="sharing">Who may read it.</param>
    /// <param name="skip">Which free table to take.</param>
    /// <returns>The status and the body.</returns>
    protected async Task<(int Status, string Body)> PublishOneAsync(
        string service,
        string layer,
        string? folder = null,
        string sharing = "private",
        int skip = 0)
    {
        if (await DatastoreIdAsync() is not { Length: > 0 } datastore)
        {
            return (0, "no datastore data source is registered");
        }

        if (await FreeTableAsync(skip) is not { } table)
        {
            return (0, "the fixture has no table that nothing publishes");
        }

        // <b>Built as an object rather than a format string.</b> A composition nests two
        // levels, and hand-written braces in a raw interpolated literal put four closing braces
        // in a row where the compiler cannot tell content from interpolation.
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
                        schemaName = table.Schema,
                        tableName = table.Table,
                        geometryColumn = table.Geometry,
                        geometryType = table.Type,
                        identityColumn = table.Identity,
                        srid = table.Srid,
                    },
                },
            },
        });

        return await AdminAsync(HttpMethod.Post, "/admin/publish", json);
    }

    /// <summary>
    /// Takes a layer out of a service and then the service out of the directory.
    /// </summary>
    /// <remarks>
    /// <b>Never <c>drop=true</c>.</b> That empties hosted tables on the way out, and the tables
    /// here belong to the fixture — dropping one takes it from every class that runs after.
    /// </remarks>
    /// <param name="service">The service.</param>
    /// <param name="layer">Its layer.</param>
    /// <param name="folder">Its folder, or null.</param>
    /// <returns>The task.</returns>
    protected async Task UnpublishAsync(string service, string layer, string? folder = null)
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/layers/{Uri.EscapeDataString(layer)}");

        await AdminAsync(
            HttpMethod.Delete,
            $"/admin/featureservices/{Uri.EscapeDataString(service)}"
            + (folder is null ? string.Empty : $"?folder={Uri.EscapeDataString(folder)}"));
    }

    /// <summary>Sends an administrative request as the suite's account.</summary>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path, from the root.</param>
    /// <param name="json">A body, or null.</param>
    /// <returns>The status and the body.</returns>
    protected async Task<(int Status, string Body)> AdminAsync(
        HttpMethod method, string path, string? json = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, new Uri($"{root}{path}"));

        await AuthenticateAsync(request, root);

        if (json is not null)
        {
            request.Content = new StringContent(
                json, System.Text.Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(request);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Asserts a property exists and returns it.</summary>
    /// <remarks>
    /// Named rather than inlined because the failure message is the point: a
    /// client that cannot find a property does not report which one, it simply
    /// fails to add the layer.
    /// </remarks>
    protected static JsonElement Require(JsonElement document, string property, string why)
    {
        Assert.True(
            document.TryGetProperty(property, out JsonElement value),
            $"'{property}' is missing. {why}");

        return value;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
