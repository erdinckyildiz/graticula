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

        if (catalogue.TryGetProperty("services", out JsonElement services)
            && services.ValueKind == JsonValueKind.Array
            && services.GetArrayLength() > 0)
        {
            return services[0].GetProperty("name").GetString();
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
                            StringComparison.Ordinal))
                    {
                        return service.GetProperty("name").GetString();
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

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {uri} returned {(int)response.StatusCode}. An ArcGIS client stops here.");

        string body = await response.Content.ReadAsStringAsync();

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

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {root}{path} returned {(int)response.StatusCode} for a browser.");

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        return await response.Content.ReadAsStringAsync();
    }

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
