using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Graticula.Testing;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A picture marker set on a live layer reaches every face — ADR-099.
/// </summary>
/// <remarks>
/// <para>
/// <b>One test, walking the faces in the order a client meets them</b>: the symbology route stores it,
/// the FeatureServer layer publishes it as <c>esriPMS</c> with its picture, the tile style draws it as a
/// <c>symbol</c> layer, the sprite index names it with a rectangle inside the sprite picture at both
/// ratios, and the map service's export and legend draw something other than the circle the same layer
/// draws without it. A face that fell back to its old answer would pass a test of any other face.
/// </para>
/// <para>
/// <b>On the multi-layer fixture's point layer</b> (<c>GRATICULA_TEST_MULTILAYER</c>, e.g.
/// <c>hosted/ci_EarlyAlert</c>, whose sites are points), <b>and the layer's own symbology is put back in
/// <c>finally</c></b> — stored again if it had one, removed if it had none — so the fixture is what it
/// was whatever this asserted.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class PictureMarkerConformanceTests : ArcGisClient
{
    /// <summary>Twenty pixels square: red above, blue below, so an upside-down picture is visible.</summary>
    private static readonly byte[] Split = TestPictures.Png(20, 20, (_, y) =>
        y < 10 ? ((byte)230, (byte)20, (byte)20, (byte)255) : ((byte)20, (byte)20, (byte)230, (byte)255));

    private static readonly string[] Corners = ["xmin", "ymin", "xmax", "ymax"];

    [Fact]
    public async Task A_picture_marker_is_stored_published_packed_and_drawn()
    {
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(MultiLayerServiceConformanceTests.ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{MultiLayerServiceConformanceTests.ServiceVariable} is not set, so this test FAILS rather than skips. "
            + "Name the fixture's multi-layer service, e.g. hosted/ci_EarlyAlert, whose sites layer is points.");

        string service = configured!.Trim('/');
        (int id, string layer) = await PointLayerAsync(service);
        string at = $"/admin/layers/{Uri.EscapeDataString(layer)}/symbology?service={Uri.EscapeDataString(service)}";

        (int readStatus, string readBody) = await AdminAsync(HttpMethod.Get, at);

        Assert.True(readStatus == 200, $"Reading '{layer}' answered {readStatus}: {readBody}");

        JsonElement original = JsonDocument.Parse(readBody).RootElement;
        bool had = original.GetProperty("stored").GetBoolean();
        string? before = had ? original.GetProperty("symbology").GetRawText() : null;

        try
        {
            // ---------- a circle first, so the pictures below have something to differ from ----------
            (int circleStatus, string circleBody) = await AdminAsync(HttpMethod.Put, at, Circle());

            Assert.True(circleStatus == 200, $"Storing a circle on '{layer}' answered {circleStatus}: {circleBody}");

            JsonElement whole = await GetJsonAsync($"/rest/services/{service}/MapServer");
            JsonElement extent = whole.GetProperty("fullExtent");
            string bbox = string.Join(
                ",",
                Corners.Select(p => extent.GetProperty(p).GetDouble().ToString("R", CultureInfo.InvariantCulture)));

            string export = $"/rest/services/{service}/MapServer/export?bbox={bbox}&size=256,256&format=png&transparent=true&f=image";

            byte[] circleMap = await BytesAsync(root, export);
            string circleLegend = await LegendImageAsync(service, id);

            // ---------- the picture ----------
            (int status, string body) = await AdminAsync(HttpMethod.Put, at, Picture());

            Assert.True(status == 200, $"Storing a picture marker on '{layer}' answered {status}: {body}");

            JsonElement stored = JsonDocument.Parse(body).RootElement;

            Assert.Equal("esriPMS", stored.GetProperty("drawingInfo").GetProperty("renderer").GetProperty("symbol").GetProperty("type").GetString());

            // The FeatureServer layer document: the picture itself, as ArcGIS clients read it.
            JsonElement symbol = (await GetJsonAsync($"/rest/services/{service}/FeatureServer/{id}"))
                .GetProperty("drawingInfo").GetProperty("renderer").GetProperty("symbol");

            Assert.Equal("esriPMS", symbol.GetProperty("type").GetString());
            Assert.Equal(Convert.ToBase64String(Split), symbol.GetProperty("imageData").GetString());
            Assert.Equal("image/png", symbol.GetProperty("contentType").GetString());

            // The tile style: a `symbol` layer drawing the icon, and a sprite to draw it from.
            JsonNode style = JsonNode.Parse(await TextAsync(root, $"/rest/services/{service}/VectorTileServer/resources/styles/root.json"))!;

            Assert.Equal("../sprites/sprite", (string?)style["sprite"]);

            JsonNode? icon = style["layers"]!.AsArray()
                .FirstOrDefault(l => (string?)l!["source-layer"] == layer && (string?)l["type"] == "symbol");

            Assert.True(
                icon is not null,
                $"The tile style of {service} has no symbol layer for '{layer}'. A stored service style overrides the "
                + "generated one — remove it, or this fixture is not the one the test was written for. Style: "
                + style.ToJsonString());

            string name = (string)icon!["layout"]!["icon-image"]!;

            Assert.StartsWith("graticula-", name, StringComparison.Ordinal);
            Assert.True((bool)icon["layout"]!["icon-allow-overlap"]!);

            // The sheet, at both ratios: the index names the icon and its rectangle is inside the picture.
            foreach ((string json, string png, int ratio) in new[] { ("sprite.json", "sprite.png", 1), ("sprite@2x.json", "sprite@2x.png", 2) })
            {
                string sprites = $"/rest/services/{service}/VectorTileServer/resources/sprites/";
                JsonNode index = JsonNode.Parse(await TextAsync(root, sprites + json))!;
                byte[] picture = await BytesAsync(root, sprites + png);

                JsonNode? entry = index[name];

                Assert.True(entry is not null, $"{json} does not name '{name}': {index.ToJsonString()}");

                Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], picture[..8]);

                int wide = (int)BinaryPrimitives.ReadUInt32BigEndian(picture.AsSpan(16, 4));
                int tall = (int)BinaryPrimitives.ReadUInt32BigEndian(picture.AsSpan(20, 4));

                Assert.True(
                    (int)entry!["x"]! + (int)entry["width"]! <= wide && (int)entry["y"]! + (int)entry["height"]! <= tall,
                    $"'{name}' in {json} is {entry.ToJsonString()}, and {png} is {wide} × {tall}.");

                Assert.Equal(20 * ratio, (int)entry["width"]!);
                Assert.Equal(ratio, (int)entry["pixelRatio"]!);
            }

            // The map service draws the picture, not the circle.
            byte[] pictureMap = await BytesAsync(root, export);

            Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], pictureMap[..4]);
            Assert.False(
                pictureMap.AsSpan().SequenceEqual(circleMap),
                "MapServer export drew the same image for a picture marker as for a circle, so the picture is not "
                + "reaching the renderer.");

            string pictureLegend = await LegendImageAsync(service, id);

            Assert.NotEqual(circleLegend, pictureLegend);
        }
        finally
        {
            (int restored, string why) = before is not null
                ? await AdminAsync(HttpMethod.Put, at, before)
                : await AdminAsync(HttpMethod.Delete, at);

            Assert.True(restored == 200, $"Putting '{layer}''s symbology back answered {restored}: {why}");
        }
    }

    /// <summary>The fixture's point layer: its id and name.</summary>
    private async Task<(int Id, string Name)> PointLayerAsync(string service)
    {
        JsonElement document = await GetJsonAsync($"/rest/services/{service}/FeatureServer");

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            int id = entry.GetProperty("id").GetInt32();
            JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/{id}");

            if (layer.TryGetProperty("geometryType", out JsonElement type) && type.GetString() == "esriGeometryPoint")
            {
                return (id, layer.GetProperty("name").GetString()!);
            }
        }

        Assert.Fail($"{service} has no point layer; the fixture's multi-layer service has one, its sites.");
        throw new InvalidOperationException();
    }

    /// <summary>The map service legend's first swatch for one layer, as base64.</summary>
    private async Task<string> LegendImageAsync(string service, int id)
    {
        JsonElement legend = await GetJsonAsync($"/rest/services/{service}/MapServer/legend?f=json");

        JsonElement layer = legend.GetProperty("layers").EnumerateArray()
            .First(l => l.GetProperty("layerId").GetInt32() == id);

        JsonElement swatch = layer.GetProperty("legend")[0];

        Assert.Equal("image/png", swatch.GetProperty("contentType").GetString());

        return swatch.GetProperty("imageData").GetString()!;
    }

    private async Task<byte[]> BytesAsync(string root, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}.");

        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<string> TextAsync(string root, string path) =>
        System.Text.Encoding.UTF8.GetString(await BytesAsync(root, path));

    /// <summary>A simple renderer of one picture, 15 points tall.</summary>
    private static string Picture() => new JsonObject
    {
        ["type"] = "CIMSimpleRenderer",
        ["symbol"] = new JsonObject
        {
            ["type"] = "CIMSymbolReference",
            ["symbol"] = new JsonObject
            {
                ["type"] = "CIMPointSymbol",
                ["symbolLayers"] = new JsonArray(new JsonObject
                {
                    ["type"] = "CIMPictureMarker",
                    ["enable"] = true,
                    ["size"] = 15,
                    ["url"] = TestPictures.DataUri(Split),
                }),
            },
        },
    }.ToJsonString();

    /// <summary>A simple renderer of one grey circle of the same size, as an Esri drawingInfo.</summary>
    private static string Circle() =>
        """{"renderer":{"type":"simple","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[90,90,90,255],"size":15}}}""";
}
