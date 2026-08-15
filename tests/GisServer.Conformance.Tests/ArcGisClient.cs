using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

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
    public const string UrlVariable = "GISSERVER_TEST_URL";

    /// <summary>Who to sign in as, if anybody.</summary>
    public const string UserVariable = "GISSERVER_TEST_USER";

    /// <summary>That account's password.</summary>
    public const string PasswordVariable = "GISSERVER_TEST_PASSWORD";

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
    private bool _disposed;

    /// <summary>Creates the client.</summary>
    protected ArcGisClient()
    {
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
