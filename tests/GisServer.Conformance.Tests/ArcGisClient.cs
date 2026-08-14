using System;
using System.Net.Http;
using System.Text.Json;
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

    /// <summary>Fetches a document, asserting it came back as JSON.</summary>
    /// <param name="path">Path and query, relative to the root.</param>
    protected async Task<JsonElement> GetJsonAsync(string path)
    {
        string root = await RequireServerAsync();

        // f=json on every request, which is what a client sends. A server that
        // only works without it works for curl and for nothing else.
        string separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        Uri uri = new($"{root}{path}{separator}f=json");

        using HttpResponseMessage response = await _http.GetAsync(uri);

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

        using HttpResponseMessage response = await _http.GetAsync(new Uri($"{root}{path}"));
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
