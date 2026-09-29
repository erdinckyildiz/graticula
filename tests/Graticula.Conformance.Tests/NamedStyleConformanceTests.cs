using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A service's named styles, which one is the default, the external origins a style may name, and the sprite
/// sheet held by every style — ADR-094, against a running server.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the tile service's collection</b>, because these store styles and a sprite sheet on the same fixture
/// <c>StyleConformanceTests</c>, <c>SpriteConformanceTests</c> and <c>GlyphConformanceTests</c> read, and xunit
/// would otherwise run them at the same time.
/// </para>
/// <para>
/// <b>The origin list is server-wide</b>, so what it held before is read first and put back afterwards, whatever
/// the test did — a list left behind would widen every console page's security policy on the fixture.
/// </para>
/// </remarks>
[Collection("tile service state")]
public sealed class NamedStyleConformanceTests : ArcGisClient, IAsyncLifetime
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const string Vendor = "https://tiles.example.com";

    private static readonly string[] MixedCase = ["HTTPS://Tiles.Example.com/"];

    private static string? Configured => Environment.GetEnvironmentVariable(ServiceVariable);

    private string _service = string.Empty;

    private string _root = string.Empty;

    private string _sourceLayer = string.Empty;

    private string[] _originsBefore = [];

    public async Task InitializeAsync()
    {
        _root = await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Configured),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip.");

        _service = Configured!.Trim('/').Split('/')[^1];

        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/settings/style-origins");
        Assert.True(status == 200, body);
        _originsBefore = [.. JsonDocument.Parse(body).RootElement.GetProperty("origins").EnumerateArray().Select(o => o.GetString()!)];

        await ClearAsync();

        JsonElement style = JsonDocument.Parse(await ResourceAsync("styles")).RootElement;

        _sourceLayer = style.GetProperty("layers").EnumerateArray()
            .First(l => l.TryGetProperty("source-layer", out _))
            .GetProperty("source-layer").GetString()!;
    }

    public async Task DisposeAsync()
    {
        await ClearAsync();
        await AdminAsync(HttpMethod.Put, "/admin/settings/style-origins", JsonSerializer.Serialize(new { origins = _originsBefore }));
    }

    private static string Folder => FolderQuery(Configured!);

    private string Admin(string path) => $"/admin/services/{_service}/{path}{Folder}";

    /// <summary>Every named style, the default, and the sprite sheet removed, in the order that lets each go.</summary>
    private async Task ClearAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, Admin("styles"));

        if (status == 200)
        {
            foreach (JsonElement style in JsonDocument.Parse(body).RootElement.GetProperty("styles").EnumerateArray())
            {
                await AdminAsync(HttpMethod.Delete, Admin($"styles/{style.GetProperty("name").GetString()}"));
            }
        }

        await AdminAsync(HttpMethod.Delete, Admin("style"));
        await AdminAsync(HttpMethod.Delete, Admin("sprite"));
    }

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private string Resources => $"{_root}/rest/services/{Configured!.Trim('/')}/VectorTileServer/resources";

    private async Task<string> ResourceAsync(string path)
    {
        using HttpClient http = Client();
        return await http.GetStringAsync(new Uri($"{Resources}/{path}"));
    }

    private async Task<HttpResponseMessage> ResourceResponseAsync(string path)
    {
        using HttpClient http = Client();
        return await http.GetAsync(new Uri($"{Resources}/{path}"));
    }

    private string Named(string marker, string extra = "", string layout = "") => $$"""
        {
          "version": 8,
          "sources": { "esri": { "type": "vector", "url": "../../" }{{extra}} },
          "metadata": { "marker": "{{marker}}" },
          "layers": [
            { "id": "a", "type": "{{(layout.Length > 0 ? "symbol" : "fill")}}", "source": "esri",
              "source-layer": "{{_sourceLayer}}"{{(layout.Length > 0 ? $", \"layout\": {layout}" : "")}} }
          ]
        }
        """;

    // ---------- named styles ----------

    [Fact]
    public async Task A_named_style_round_trips_and_is_served_beside_the_default()
    {
        string light = Named("light");
        string dark = Named("dark");

        (int status, string body) = await AdminAsync(HttpMethod.Put, Admin("style"), light);
        Assert.True(status == 200, body);

        (status, body) = await AdminAsync(HttpMethod.Put, Admin("styles/dark"), dark);
        Assert.True(status == 200, body);
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("isDefault").GetBoolean());

        // Byte for byte on the admin read-back and on the public address.
        (status, body) = await AdminAsync(HttpMethod.Get, Admin("styles/DARK"));
        Assert.Equal(200, status);
        Assert.Equal(dark, body);

        Assert.Equal(dark, await ResourceAsync("styles/dark.json"));
        Assert.Equal(light, await ResourceAsync("styles/root.json"));
        Assert.Equal(light, await ResourceAsync("styles/default.json"));

        (status, body) = await AdminAsync(HttpMethod.Get, Admin("styles"));
        Assert.Equal(200, status);

        JsonElement listed = JsonDocument.Parse(body).RootElement;
        Assert.Equal("default", listed.GetProperty("default").GetString());
        Assert.Equal(
            ["default", "dark"],
            listed.GetProperty("styles").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Switching_the_default_moves_root_json_and_the_style_alias_with_it()
    {
        string light = Named("light");
        string dark = Named("dark");

        Assert.Equal(200, (await AdminAsync(HttpMethod.Put, Admin("styles/light"), light)).Status);
        Assert.Equal(200, (await AdminAsync(HttpMethod.Put, Admin("styles/dark"), dark)).Status);

        // The first stored on a service with no default became it.
        Assert.Equal(light, await ResourceAsync("styles/root.json"));

        (int status, string body) = await AdminAsync(
            HttpMethod.Put, Admin("default-style"), JsonSerializer.Serialize(new { style = "dark" }));
        Assert.True(status == 200, body);

        Assert.Equal(dark, await ResourceAsync("styles/root.json"));
        Assert.Equal(dark, await ResourceAsync("styles"));
        Assert.Equal(light, await ResourceAsync("styles/light.json"));
        Assert.Equal(dark, (await AdminAsync(HttpMethod.Get, Admin("style"))).Body);

        // Back to generated: root.json is generated and both styles are kept.
        (status, body) = await AdminAsync(
            HttpMethod.Put, Admin("default-style"), JsonSerializer.Serialize(new { style = (string?)null }));
        Assert.True(status == 200, body);

        Assert.DoesNotContain("\"marker\"", await ResourceAsync("styles/root.json"), StringComparison.Ordinal);
        Assert.Equal(dark, await ResourceAsync("styles/dark.json"));

        Assert.Equal(404, (await AdminAsync(
            HttpMethod.Put, Admin("default-style"), JsonSerializer.Serialize(new { style = "nosuch" }))).Status);
    }

    [Fact]
    public async Task A_name_that_is_not_one_is_refused_and_an_unknown_one_is_a_404()
    {
        Assert.Equal(400, (await AdminAsync(HttpMethod.Put, Admin("styles/root"), Named("x"))).Status);
        Assert.Equal(400, (await AdminAsync(HttpMethod.Put, Admin("styles/dark.mode"), Named("x"))).Status);
        Assert.Equal(404, (await AdminAsync(HttpMethod.Get, Admin("styles/nosuch"))).Status);
        Assert.Equal(404, (await AdminAsync(HttpMethod.Delete, Admin("styles/nosuch"))).Status);

        using HttpResponseMessage served = await ResourceResponseAsync("styles/nosuch.json");
        Assert.Equal(HttpStatusCode.NotFound, served.StatusCode);
    }

    [Fact]
    public async Task Writing_a_named_style_is_a_privilege()
    {
        using HttpClient http = Client();

        using HttpResponseMessage response = await http.PutAsync(
            new Uri($"{_root}{Admin("styles/dark")}"),
            new StringContent(Named("dark"), Encoding.UTF8, "application/json"));

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"An anonymous PUT of a named style answered {(int)response.StatusCode}.");
    }

    // ---------- allowed origins ----------

    private string External => Named(
        "external", $", \"vendor\": {{ \"type\": \"raster\", \"tiles\": [\"{Vendor}/{{z}}/{{x}}/{{y}}.png\"] }}");

    [Fact]
    public async Task An_external_source_is_refused_until_its_origin_is_allowed_and_not_served_once_it_is_removed()
    {
        Assert.Equal(200, (await AdminAsync(
            HttpMethod.Put, "/admin/settings/style-origins", JsonSerializer.Serialize(new { origins = Array.Empty<string>() }))).Status);

        (int status, string body) = await AdminAsync(HttpMethod.Put, Admin("style"), External);
        Assert.Equal(400, status);
        Assert.Contains(Vendor, body, StringComparison.Ordinal);
        Assert.Contains("style-origins", body, StringComparison.Ordinal);

        (status, body) = await AdminAsync(
            HttpMethod.Put, "/admin/settings/style-origins",
            JsonSerializer.Serialize(new { origins = MixedCase }));
        Assert.True(status == 200, body);
        Assert.Equal(
            [Vendor],
            JsonDocument.Parse(body).RootElement.GetProperty("origins").EnumerateArray().Select(o => o.GetString()));

        (status, body) = await AdminAsync(HttpMethod.Put, Admin("style"), External);
        Assert.True(status == 200, body);
        Assert.Equal(External, await ResourceAsync("styles/root.json"));

        // Removed: not served — the generated style is, and both the answer and the admin read-back say why.
        (status, body) = await AdminAsync(
            HttpMethod.Put, "/admin/settings/style-origins", JsonSerializer.Serialize(new { origins = Array.Empty<string>() }));
        Assert.True(status == 200, body);
        Assert.Contains(Vendor, JsonDocument.Parse(body).RootElement.GetProperty("removed")[0].GetString(), StringComparison.Ordinal);

        using (HttpResponseMessage served = await ResourceResponseAsync("styles/root.json"))
        {
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.True(served.Headers.Contains("Graticula-Style-Stale"), "A style naming a removed origin was served.");
            Assert.DoesNotContain(Vendor, await served.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using HttpClient http = Client();
        using HttpRequestMessage read = new(HttpMethod.Get, new Uri($"{_root}{Admin("style")}"));
        await AuthenticateAsync(read, _root);
        using HttpResponseMessage readBack = await http.SendAsync(read);

        Assert.Equal(External, await readBack.Content.ReadAsStringAsync());
        Assert.Contains(
            Uri.EscapeDataString(Vendor),
            string.Join(" ", readBack.Headers.GetValues("Graticula-Style-Stale")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_origin_the_rules_refuse_cannot_be_allowed()
    {
        foreach (string refused in (string[])["http://tiles.example.com", "https://allowed.com@evil.com", "https://169.254.169.254", "https://*.com"])
        {
            (int status, string body) = await AdminAsync(
                HttpMethod.Put, "/admin/settings/style-origins", JsonSerializer.Serialize(new { origins = new[] { refused } }));

            Assert.True(status == 400, $"'{refused}' answered {status}: {body}");
        }
    }

    [Fact]
    public async Task The_console_policy_allows_an_allowed_origin_to_be_fetched()
    {
        Assert.Equal(200, (await AdminAsync(
            HttpMethod.Put, "/admin/settings/style-origins", JsonSerializer.Serialize(new { origins = new[] { Vendor } }))).Status);

        using HttpClient http = Client();
        using HttpResponseMessage page = await http.GetAsync(new Uri($"{_root}/studio/"));

        string policy = string.Join(" ", page.Headers.GetValues("Content-Security-Policy"));
        string connect = policy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("connect-src ", StringComparison.Ordinal));

        Assert.Contains(Vendor, connect, StringComparison.Ordinal);
    }

    // ---------- the sprite sheet is held by every style ----------

    private const string Both = """
        {
          "marker": { "x": 0, "y": 0, "width": 8, "height": 8, "pixelRatio": 1 },
          "school": { "x": 8, "y": 0, "width": 8, "height": 8, "pixelRatio": 1 }
        }
        """;

    private const string MarkerOnly = """
        { "marker": { "x": 0, "y": 0, "width": 8, "height": 8, "pixelRatio": 1 } }
        """;

    private async Task<(int Status, string Body)> PutSpriteAsync(string index)
    {
        using HttpClient http = Client();

        MultipartFormDataContent form = new()
        {
            { new StringContent(index, Encoding.UTF8, "application/json"), "index", "sprite.json" },
        };

        ByteArrayContent image = new(SpriteConformanceTests.Png(16, 8));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "image", "sprite.png");

        using HttpRequestMessage request = new(HttpMethod.Put, new Uri($"{_root}{Admin("sprite")}")) { Content = form };
        await AuthenticateAsync(request, _root);

        using HttpResponseMessage response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_sheet_is_held_by_an_icon_any_style_names()
    {
        (int status, string body) = await PutSpriteAsync(Both);
        Assert.True(status == 200, body);

        Assert.Equal(200, (await AdminAsync(
            HttpMethod.Put, Admin("styles/light"), Named("light", layout: "{ \"icon-image\": \"marker\" }"))).Status);
        Assert.Equal(200, (await AdminAsync(
            HttpMethod.Put, Admin("styles/dark"), Named("dark", layout: "{ \"icon-image\": \"school\" }"))).Status);

        // The default draws only 'marker'; the sheet is still held for 'dark'.
        (status, body) = await PutSpriteAsync(MarkerOnly);
        Assert.Equal(409, status);
        Assert.Contains("school", body, StringComparison.Ordinal);
        Assert.Contains("dark", body, StringComparison.Ordinal);

        (status, body) = await AdminAsync(HttpMethod.Delete, Admin("sprite"));
        Assert.Equal(409, status);
        Assert.Contains("dark", body, StringComparison.Ordinal);

        (status, body) = await AdminAsync(HttpMethod.Get, Admin("sprite"));
        Assert.Equal(200, status);
        Assert.Equal(
            ["marker", "school"],
            JsonDocument.Parse(body).RootElement.GetProperty("styleUses").EnumerateArray().Select(i => i.GetString()));

        // Once 'dark' stops naming it, the smaller sheet is accepted.
        Assert.Equal(200, (await AdminAsync(HttpMethod.Delete, Admin("styles/dark"))).Status);
        Assert.Equal(200, (await PutSpriteAsync(MarkerOnly)).Status);
    }
}
