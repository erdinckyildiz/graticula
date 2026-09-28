using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
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
/// Uploading a sprite sheet, drawing a style's icons from it, and the two refusing to disagree — ADR-092.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sheet and the style check each other, so these are tested together.</b> A style naming an icon
/// the sheet lacks is refused when it is stored; a sheet that would take away an icon the stored style names
/// is refused when it is uploaded or removed. Either half alone would let the pair reach the state neither
/// allows — a layer that draws nothing and says nothing.
/// </para>
/// <para>
/// <b>In the tile service's collection</b>, because these store styles on the same fixture
/// <c>StyleConformanceTests</c> and <c>GlyphConformanceTests</c> read, and xunit would otherwise run
/// them at the same time.
/// </para>
/// <para>
/// <b>The PNG is built here from the format</b>, signature, header, one compressed row per scanline and
/// the trailer, rather than read from a file, so the test owns every byte it asserts comes back.
/// </para>
/// </remarks>
[Collection("tile service state")]
public sealed class SpriteConformanceTests : ArcGisClient, IAsyncLifetime
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private static string? Configured => Environment.GetEnvironmentVariable(ServiceVariable);

    /// <summary>The service name without its folder; the folder travels as <c>?folder=</c> (D-275).</summary>
    private string _service = string.Empty;

    private string _root = string.Empty;

    private string _sourceLayer = string.Empty;

    private const string Index1x = """
        {
          "marker": { "x": 0, "y": 0, "width": 8, "height": 8, "pixelRatio": 1 },
          "school": { "x": 8, "y": 0, "width": 8, "height": 8, "pixelRatio": 1 }
        }
        """;

    private const string Index2x = """
        {
          "marker": { "x": 0, "y": 0, "width": 16, "height": 16, "pixelRatio": 2 },
          "school": { "x": 16, "y": 0, "width": 16, "height": 16, "pixelRatio": 2 }
        }
        """;

    public async Task InitializeAsync()
    {
        _root = await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Configured),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip.");

        _service = Configured!.Trim('/').Split('/')[^1];

        // Start from no style and no sheet, whatever the last run left — the lesson StyleConformanceTests
        // records: a fixture that is whatever the server happens to hold fails for the wrong reasons.
        await ClearAsync();

        JsonElement style = JsonDocument.Parse(await ResourceTextAsync("styles")).RootElement;

        _sourceLayer = style.GetProperty("layers").EnumerateArray()
            .First(l => l.TryGetProperty("source-layer", out _))
            .GetProperty("source-layer").GetString()!;
    }

    /// <summary>Always leaves the service on the generated style and the empty sheet.</summary>
    public async Task DisposeAsync() => await ClearAsync();

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private string Resources =>
        $"{_root}/rest/services/{Configured!.Trim('/')}/VectorTileServer/resources";

    private async Task ClearAsync()
    {
        using HttpClient http = Client();

        foreach (string path in (string[])["style", "sprite"])
        {
            using HttpRequestMessage request = new(
                HttpMethod.Delete, new Uri($"{_root}/admin/services/{_service}/{path}{FolderQuery(Configured!)}"));

            await AuthenticateAsync(request, _root);

            using HttpResponseMessage _ = await http.SendAsync(request);
        }
    }

    private async Task<string> ResourceTextAsync(string path)
    {
        using HttpClient http = Client();

        return await http.GetStringAsync(new Uri($"{Resources}/{path}"));
    }

    private async Task<HttpResponseMessage> PutSpriteAsync(
        string index, byte[] png, int? ratio = null, bool authenticated = true)
    {
        using HttpClient http = Client();

        MultipartFormDataContent form = new()
        {
            { new StringContent(index, Encoding.UTF8, "application/json"), "index", "sprite.json" },
        };

        ByteArrayContent image = new(png);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "image", "sprite.png");

        using HttpRequestMessage request = new(
            HttpMethod.Put,
            new Uri($"{_root}/admin/services/{_service}/sprite{FolderQuery(Configured!)}"
                + (ratio is { } r ? $"&ratio={r}" : string.Empty)))
        {
            Content = form,
        };

        if (authenticated)
        {
            await AuthenticateAsync(request, _root);
        }

        return await http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using HttpClient http = Client();

        using HttpRequestMessage request = new(
            method, new Uri($"{_root}/admin/services/{_service}/{path}{FolderQuery(Configured!)}"));

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        await AuthenticateAsync(request, _root);

        return await http.SendAsync(request);
    }

    private string StyleDrawing(string iconImage) => $$$"""
        {
          "version": 8,
          "sources": { "esri": { "type": "vector", "url": "../../" } },
          "sprite": "../sprites/sprite",
          "layers": [
            { "id": "pins", "type": "symbol", "source": "esri", "source-layer": "{{{_sourceLayer}}}",
              "layout": { "icon-image": {{{iconImage}}} } }
          ]
        }
        """;

    private async Task StoreSheetAsync()
    {
        using HttpResponseMessage put = await PutSpriteAsync(Index1x, Png(16, 8));

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    // ---------- serving ----------

    /// <summary>
    /// What was uploaded is what is served, with an ETag that turns an unchanged sheet into a 304.
    /// </summary>
    [Fact]
    public async Task An_uploaded_sheet_is_served_as_it_was_sent_and_revalidates_to_304()
    {
        byte[] png = Png(16, 8);

        using (HttpResponseMessage put = await PutSpriteAsync(Index1x, png))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        using HttpClient http = Client();

        using (HttpResponseMessage json = await http.GetAsync(new Uri($"{Resources}/sprites/sprite.json")))
        {
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Index1x, await json.Content.ReadAsStringAsync());
        }

        using HttpResponseMessage image = await http.GetAsync(new Uri($"{Resources}/sprites/sprite.png"));

        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());

        EntityTagHeaderValue? tag = image.Headers.ETag;

        Assert.NotNull(tag);

        // No longer immutable: a sheet changes when its publisher uploads the next one.
        Assert.True(image.Headers.CacheControl?.NoCache, $"Cache-Control was '{image.Headers.CacheControl}'.");

        using HttpRequestMessage again = new(HttpMethod.Get, new Uri($"{Resources}/sprites/sprite.png"));
        again.Headers.IfNoneMatch.Add(tag!);

        using HttpResponseMessage unchanged = await http.SendAsync(again);

        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
    }

    /// <summary>
    /// A client on a high-density screen asks only for <c>@2x</c>, and gets the 1x sheet until there is one.
    /// </summary>
    [Fact]
    public async Task The_2x_sheet_falls_back_to_the_1x_one_until_one_is_uploaded()
    {
        await StoreSheetAsync();

        Assert.Equal(Index1x, await ResourceTextAsync("sprites/sprite@2x.json"));

        using (HttpResponseMessage put = await PutSpriteAsync(Index2x, Png(32, 16), ratio: 2))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        Assert.Equal(Index2x, await ResourceTextAsync("sprites/sprite@2x.json"));
        Assert.Equal(Index1x, await ResourceTextAsync("sprites/sprite.json"));
    }

    /// <summary>No sheet stored is still an answer, and an empty one.</summary>
    [Fact]
    public async Task A_service_with_no_sheet_serves_an_empty_one()
    {
        Assert.Equal("{}", await ResourceTextAsync("sprites/sprite.json"));
        Assert.Equal("{}", await ResourceTextAsync("sprites/sprite@2x.json"));
    }

    // ---------- the upload's own refusals ----------

    [Fact]
    public async Task A_2x_sheet_with_no_1x_sheet_under_it_is_refused()
    {
        using HttpResponseMessage put = await PutSpriteAsync(Index2x, Png(32, 16), ratio: 2);

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("1x", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_icon_past_the_edge_of_the_picture_is_refused_and_named()
    {
        using HttpResponseMessage put = await PutSpriteAsync(
            """{"far":{"x":12,"y":0,"width":8,"height":8}}""", Png(16, 8));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        string said = await put.Content.ReadAsStringAsync();

        Assert.Contains("far", said, StringComparison.Ordinal);
        Assert.Contains("16 × 8", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_not_a_png_is_refused()
    {
        using HttpResponseMessage put = await PutSpriteAsync(Index1x, Encoding.ASCII.GetBytes("GIF89a not a png at all"));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("PNG", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_upload_a_sheet()
    {
        using HttpResponseMessage put = await PutSpriteAsync(Index1x, Png(16, 8), authenticated: false);

        Assert.True(
            put.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"an anonymous PUT returned {(int)put.StatusCode}");
    }

    [Fact]
    public async Task The_admin_read_back_says_what_is_stored()
    {
        await StoreSheetAsync();

        using HttpResponseMessage get = await SendAsync(HttpMethod.Get, "sprite");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        JsonElement said = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;

        Assert.True(said.GetProperty("stored").GetBoolean());

        JsonElement sheet = said.GetProperty("sheets").EnumerateArray().Single();

        Assert.Equal(1, sheet.GetProperty("ratio").GetInt32());
        Assert.Equal(2, sheet.GetProperty("icons").GetInt32());
        Assert.Equal(16, sheet.GetProperty("width").GetInt32());
        Assert.Equal(8, sheet.GetProperty("height").GetInt32());
    }

    // ---------- the style and the sheet, checking each other ----------

    [Fact]
    public async Task An_icon_on_a_service_with_no_sheet_is_refused()
    {
        using HttpResponseMessage put = await SendAsync(HttpMethod.Put, "style", StyleDrawing("\"marker\""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("sprite", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_style_drawing_an_icon_the_sheet_has_is_stored_and_served()
    {
        await StoreSheetAsync();

        string style = StyleDrawing("\"marker\"");

        using (HttpResponseMessage put = await SendAsync(HttpMethod.Put, "style", style))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        Assert.Equal(style, await ResourceTextAsync("styles"));
    }

    [Fact]
    public async Task A_style_drawing_an_icon_the_sheet_lacks_is_refused_and_names_it()
    {
        await StoreSheetAsync();

        using HttpResponseMessage put = await SendAsync(HttpMethod.Put, "style", StyleDrawing("\"hospital\""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("hospital", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_icon_expression_is_accepted_when_there_is_a_sheet()
    {
        await StoreSheetAsync();

        using HttpResponseMessage put = await SendAsync(HttpMethod.Put, "style", StyleDrawing("""["get","kind"]"""));

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The sheet may not be removed, or replaced by one without the icon, while the style names it.
    /// </summary>
    [Fact]
    public async Task The_sheet_is_held_while_the_stored_style_names_one_of_its_icons()
    {
        await StoreSheetAsync();

        using (HttpResponseMessage put = await SendAsync(HttpMethod.Put, "style", StyleDrawing("\"school\"")))
        {
            Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage delete = await SendAsync(HttpMethod.Delete, "sprite"))
        {
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.Contains("school", await delete.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage replace = await PutSpriteAsync(
                   """{"marker":{"x":0,"y":0,"width":8,"height":8}}""", Png(16, 8)))
        {
            Assert.Equal(HttpStatusCode.Conflict, replace.StatusCode);
            Assert.Contains("school", await replace.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // And the served sheet is still the one the style draws from.
        Assert.Equal(Index1x, await ResourceTextAsync("sprites/sprite.json"));

        // Released once the style no longer names it.
        using (HttpResponseMessage _ = await SendAsync(HttpMethod.Delete, "style"))
        {
        }

        using (HttpResponseMessage delete = await SendAsync(HttpMethod.Delete, "sprite"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        Assert.Equal("{}", await ResourceTextAsync("sprites/sprite.json"));
    }

    // ---------- a PNG, from the format ----------

    /// <summary>A real, decodable RGBA PNG of the given size, every pixel opaque grey.</summary>
    private static byte[] Png(int width, int height)
    {
        using MemoryStream file = new();

        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA

        Chunk(file, "IHDR", header);

        using MemoryStream raw = new();

        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // no filter

            for (int x = 0; x < width; x++)
            {
                raw.Write([0x80, 0x80, 0x80, 0xFF]);
            }
        }

        using MemoryStream packed = new();

        using (ZLibStream zlib = new(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", []);

        return file.ToArray();
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        file.Write(length);

        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        file.Write(typed);

        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        file.Write(crc);
    }

    /// <summary>The CRC the PNG specification defines, over a chunk's type and data.</summary>
    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in bytes)
        {
            crc ^= b;

            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
