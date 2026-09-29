using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Graticula.Testing;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// The ArcGIS documents a picture-marker layer is published in — ADR-099.
/// </summary>
/// <remarks>
/// <b>What a client reads, serialised as it goes out.</b> Pro and the JavaScript SDK draw a layer's
/// picture from the <c>esriPMS</c> in its <c>drawingInfo</c>, and a MapLibre client from the service
/// style's <c>symbol</c> layer and its <c>sprite</c>; each assertion is on the JSON those clients parse.
/// </remarks>
public sealed class PictureMarkerFacesTests
{
    private static readonly byte[] Pin = TestPictures.Png(16, 24, 200, 40, 40);

    private static string Renderer() => new JsonObject
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
                    ["size"] = 18,
                    ["url"] = TestPictures.DataUri(Pin),
                }),
            },
        },
    }.ToJsonString();

    [Fact]
    public void The_feature_layer_document_carries_the_picture_as_image_data()
    {
        object drawing = FeatureServerMetadataWriter.Drawing("sites", GeometryKind.Point, Renderer(), out bool generated);

        Assert.False(generated);

        JsonNode symbol = JsonNode.Parse(JsonSerializer.Serialize(drawing))!["renderer"]!["symbol"]!;

        Assert.Equal("esriPMS", (string?)symbol["type"]);
        Assert.Equal(Convert.ToBase64String(Pin), (string?)symbol["imageData"]);
        Assert.Equal("image/png", (string?)symbol["contentType"]);
        Assert.Equal(18, (double)symbol["height"]!);
        Assert.Equal(12, (double)symbol["width"]!);
    }

    [Fact]
    public void The_generated_style_draws_the_icon_and_names_the_sprite_even_without_glyphs()
    {
        object style = VectorTileServerMetadataWriter.Style([("sites", GeometryKind.Point, Renderer())], fontStack: null);

        JsonNode document = JsonNode.Parse(JsonSerializer.Serialize(style))!;

        Assert.Equal("../sprites/sprite", (string?)document["sprite"]);

        JsonNode layer = Assert.Single(document["layers"]!.AsArray())!;

        Assert.Equal("symbol", (string?)layer["type"]);
        Assert.Equal("sites", (string?)layer["source-layer"]);
        Assert.Equal(MarkerPicture.FromBytes(Pin, null, "x").Name, (string?)layer["layout"]!["icon-image"]);
    }

    [Fact]
    public void An_uploaded_sheet_may_not_use_the_generated_prefix()
    {
        string index = "{\"" + MarkerPicture.NamePrefix + "pin\":{\"x\":0,\"y\":0,\"width\":16,\"height\":24}}";

        Assert.False(SpriteSheet.TryValidate(index, TestPictures.Png(16, 24, 1, 1, 1), 1, out _, out string? error));
        Assert.Contains("reserved", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_style_may_draw_a_generated_icon_the_served_sheet_carries()
    {
        string name = MarkerPicture.FromBytes(Pin, null, "x").Name;
        string style = "{\"version\":8,\"sources\":{\"esri\":{\"type\":\"vector\",\"url\":\"../../\"}},"
            + "\"sprite\":\"../sprites/sprite\","
            + "\"layers\":[{\"id\":\"s\",\"type\":\"symbol\",\"source\":\"esri\",\"source-layer\":\"sites\","
            + "\"layout\":{\"icon-image\":\"" + name + "\"}}]}";

        Assert.True(StyleDocument.TryValidate(style, ["sites"], [name], out string? error), error);
        Assert.False(StyleDocument.TryValidate(style, ["sites"], ["school"], out _));
    }
}
