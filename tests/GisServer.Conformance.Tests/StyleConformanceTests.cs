using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

/// <summary>
/// Conformance classes that share one fixture service and mutate its state.
/// </summary>
/// <remarks>
/// <b>xunit runs test classes in parallel, and these two write and read the same
/// thing.</b> `StyleConformanceTests` stores a style on the tile fixture;
/// `GlyphConformanceTests` reads that service's style and asserts the generated
/// one carries `glyphs` and `sprite`. Run at the same time, the second sees the
/// first's document and fails — which is what happened on 2026-08-15, and it is
/// a defect in the tests rather than in the server.
///
/// A collection serialises them. The alternative — a second fixture service —
/// would be cleaner and costs another environment variable that every runner
/// has to set, so it is not obviously worth it for two classes.
/// </remarks>
[CollectionDefinition("tile service state")]
public sealed class SharedTileServiceState
{
}

/// <summary>
/// Storing a style, serving it, and getting the generated one back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test is that nothing is normalised.</b> A style is a
/// document a cartographer wrote and will open again; the column is text rather
/// than <c>jsonb</c> so that whitespace and key order survive, and a round trip
/// that reformatted the file would be a silent edit of somebody's work. That is
/// asserted byte for byte here, because it is exactly the kind of property a
/// later "tidy-up" removes without noticing.
/// </para>
/// <para>
/// These run against a real server, so they also cover the parts a unit test
/// cannot: the privilege check, the body bound, and the clear path — which had
/// its own defect that only a database could produce.
/// </para>
/// </remarks>
[Collection("tile service state")]
public sealed class StyleConformanceTests : ArcGisClient, IAsyncLifetime
{
    private const string ServiceVariable = "GISSERVER_TEST_TILE_SERVICE";

    private static string? Configured => Environment.GetEnvironmentVariable(ServiceVariable);

    /// <summary>The service name without its folder, which is how admin addresses it.</summary>
    private string _service = string.Empty;

    private string _root = string.Empty;

    private string _sourceLayer = string.Empty;

    public async Task InitializeAsync()
    {
        _root = await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Configured),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip.");

        _service = Configured!.Trim('/').Split('/')[^1];

        // <b>Start from the generated style, whatever the last run left.</b>
        // The first attempt read the served style to find a source layer and
        // hit a stored one whose first layer was a background — no
        // source-layer, KeyNotFound, and a failure that had nothing to do with
        // the behaviour under test. A test whose fixture is whatever the server
        // happens to be holding is a test that fails for the wrong reasons.
        await ClearAsync();

        // Take a real source layer out of the generated style rather than
        // assuming one: the validator checks against what the service actually
        // has, so a hardcoded name would make these pass or fail on the fixture.
        JsonElement style = JsonDocument.Parse(await ServedAsync()).RootElement;

        _sourceLayer = style.GetProperty("layers").EnumerateArray()
            .First(l => l.TryGetProperty("source-layer", out _))
            .GetProperty("source-layer").GetString()!;
    }

    /// <summary>Always leaves the service on the generated style.</summary>
    public async Task DisposeAsync() => await ClearAsync();

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<HttpResponseMessage> PutAsync(string style, bool authenticated = true)
    {
        using HttpClient http = Client();

        using HttpRequestMessage request = new(
            HttpMethod.Put, new Uri($"{_root}/admin/services/{_service}/style"))
        {
            Content = new StringContent(style, Encoding.UTF8, "application/json"),
        };

        if (authenticated)
        {
            await AuthenticateAsync(request, _root);
        }

        return await http.SendAsync(request);
    }

    private async Task ClearAsync()
    {
        using HttpClient http = Client();

        using HttpRequestMessage request = new(
            HttpMethod.Delete, new Uri($"{_root}/admin/services/{_service}/style"));

        await AuthenticateAsync(request, _root);

        using HttpResponseMessage _ = await http.SendAsync(request);
    }

    private async Task<string> ServedAsync()
    {
        using HttpClient http = Client();

        return await http.GetStringAsync(new Uri(
            $"{_root}/rest/services/{Configured!.Trim('/')}"
            + "/VectorTileServer/resources/styles"));
    }

    private string Authored => $$"""
        {
          "version": 8,
          "name": "Conformance",
          "sources": { "esri": { "type": "vector", "url": "../../" } },
          "glyphs": "../fonts/{fontstack}/{range}.pbf",
          "layers": [
            { "id": "a", "type": "fill", "source": "esri",
              "source-layer": "{{_sourceLayer}}", "paint": { "fill-color": "#123456" } }
          ]
        }
        """;

    // ---------- the round trip ----------

    /// <summary>
    /// What is served is the file that was sent, byte for byte.
    /// </summary>
    [Fact]
    public async Task A_stored_style_comes_back_exactly_as_it_was_written()
    {
        using (HttpResponseMessage put = await PutAsync(Authored))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        Assert.Equal(Authored, await ServedAsync());
    }

    /// <summary>Clearing it returns the generated style, not an empty one.</summary>
    /// <remarks>
    /// <b>The clear path had a defect of its own.</b> A null parameter inside a
    /// CASE gave Postgres nothing to infer a type from, so storing a style
    /// worked and removing one returned a 500 — a state somebody could get into
    /// and not get out of.
    /// </remarks>
    [Fact]
    public async Task Clearing_a_style_goes_back_to_the_generated_one()
    {
        using (HttpResponseMessage _ = await PutAsync(Authored))
        {
        }

        await ClearAsync();

        JsonElement generated = JsonDocument.Parse(await ServedAsync()).RootElement;

        Assert.Equal(8, generated.GetProperty("version").GetInt32());
        Assert.False(generated.TryGetProperty("name", out _));
        Assert.NotEmpty(generated.GetProperty("layers").EnumerateArray());
    }

    // ---------- what is refused ----------

    /// <summary>
    /// A style naming a layer that is not there is refused, and says what is.
    /// </summary>
    [Fact]
    public async Task A_style_naming_a_layer_that_is_not_there_is_refused()
    {
        using HttpResponseMessage response = await PutAsync("""
            {"version":8,"layers":[{"id":"a","type":"fill","source-layer":"not-a-layer"}]}
            """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("not-a-layer", body, StringComparison.Ordinal);
        Assert.Contains(_sourceLayer, body, StringComparison.Ordinal);
    }

    /// <summary>A style that would send every viewer elsewhere is refused.</summary>
    [Fact]
    public async Task A_style_pointing_off_this_server_is_refused()
    {
        using HttpResponseMessage response = await PutAsync("""
            {"version":8,"sources":{"x":{"url":"https://evil.example/tiles.json"}},"layers":[]}
            """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Styling is a privilege, not an open door.</summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_style_a_service()
    {
        using HttpResponseMessage response = await PutAsync(Authored, authenticated: false);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound,
            $"an anonymous PUT returned {(int)response.StatusCode}");
    }

    /// <summary>
    /// A refused style leaves the stored one alone.
    /// </summary>
    /// <remarks>
    /// <b>The property that makes the validator safe to rely on.</b> If a bad
    /// PUT cleared or corrupted what was there, every refusal would be an
    /// outage, and an author experimenting would take their map down.
    /// </remarks>
    [Fact]
    public async Task A_refused_style_does_not_disturb_the_stored_one()
    {
        using (HttpResponseMessage _ = await PutAsync(Authored))
        {
        }

        using (HttpResponseMessage bad = await PutAsync("""{"version":8}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        Assert.Equal(Authored, await ServedAsync());
    }

    /// <summary>A style over the cap is refused rather than stored.</summary>
    [Fact]
    public async Task A_style_over_the_cap_is_refused()
    {
        string padding = new('x', 1024 * 1024 + 16);

        using HttpResponseMessage response =
            await PutAsync($$"""{"version":8,"layers":[],"pad":"{{padding}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
