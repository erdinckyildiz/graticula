using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Render.Skia;
using Graticula.Testing;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The sprite files a service with picture markers serves, and the style checks that read them — ADR-099 §5.4.
/// </summary>
/// <remarks>
/// <b>Without a database</b>: the sheet is generated from the service the caller already holds, so every
/// part of it can be asked with no store. The uploaded half and a live server are
/// <c>PictureMarkerConformanceTests</c>'.
/// </remarks>
public sealed class GeneratedSpriteSheetTests
{
    private static readonly byte[] Pin = TestPictures.Png(16, 24, 200, 40, 40);

    private static readonly byte[] Flag = TestPictures.Png(40, 20, 40, 160, 40);

    private static string Renderer(params byte[][] pictures)
    {
        JsonArray classes = [];

        for (int i = 0; i < pictures.Length; i++)
        {
            classes.Add(new JsonObject
            {
                ["label"] = $"class {i}",
                ["values"] = new JsonArray(new JsonObject
                {
                    ["type"] = "CIMUniqueValue",
                    ["fieldValues"] = new JsonArray(i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                }),
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
                            ["size"] = 12,
                            ["url"] = TestPictures.DataUri(pictures[i]),
                        }),
                    },
                },
            });
        }

        return new JsonObject
        {
            ["type"] = "CIMUniqueValueRenderer",
            ["fields"] = new JsonArray("kind"),
            ["groups"] = new JsonArray(new JsonObject { ["classes"] = classes }),
        }.ToJsonString();
    }

    private static PublishedLayer Layer(string name, string? symbology) =>
        new(
            Guid.NewGuid(),
            new LayerDefinition(name, "public", name, "geom", 3857, "objectid", "objectid", isHosted: true),
            "datastore",
            "Host=db;Database=gis",
            GeometryKind.Point,
            owner: null,
            SharingScope.Public,
            ServiceStatus.Started,
            symbology: symbology);

    private static PublishedService Service(params PublishedLayer[] layers) =>
        new(Guid.NewGuid(), "sites", null, "FeatureServer", null, null, SharingScope.Public, ServiceStatus.Started, layers);

    [Fact]
    public async Task A_service_with_no_picture_markers_serves_the_empty_sheet_it_always_did()
    {
        PublishedService service = Service(Layer("roads", null));

        byte[] index = await GeneratedSprites.FileAsync(service, 1, false, null, new SkiaMapCanvasFactory(), CancellationToken.None);
        byte[] png = await GeneratedSprites.FileAsync(service, 1, true, null, new SkiaMapCanvasFactory(), CancellationToken.None);

        Assert.Equal("{}", Encoding.UTF8.GetString(index));
        Assert.Equal(VectorTileEndpoints.EmptySheet, png);
    }

    [Fact]
    public async Task Every_picture_of_every_layer_is_in_the_sheet_once_and_the_index_matches_the_png()
    {
        PublishedService service = Service(
            Layer("schools", Renderer(Pin, Flag)),
            Layer("clinics", Renderer(Pin)));

        foreach (int ratio in (int[])[1, 2])
        {
            JsonObject index = JsonNode.Parse(Encoding.UTF8.GetString(await GeneratedSprites.FileAsync(
                service, ratio, false, null, new SkiaMapCanvasFactory(), CancellationToken.None)))!.AsObject();

            byte[] png = await GeneratedSprites.FileAsync(
                service, ratio, true, null, new SkiaMapCanvasFactory(), CancellationToken.None);

            Assert.Equal(2, index.Count);

            using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

            MarkerPicture pin = MarkerPicture.FromBytes(Pin, null, "x");
            JsonNode entry = index[pin.Name]!;

            Assert.Equal(pin.SheetWidth * ratio, (int)entry["width"]!);
            Assert.Equal(pin.SheetHeight * ratio, (int)entry["height"]!);
            Assert.Equal(ratio, (int)entry["pixelRatio"]!);

            foreach (KeyValuePair<string, JsonNode?> icon in index)
            {
                int x = (int)icon.Value!["x"]!;
                int y = (int)icon.Value["y"]!;
                int w = (int)icon.Value["width"]!;
                int h = (int)icon.Value["height"]!;

                Assert.True(x + w <= bitmap.Width && y + h <= bitmap.Height, $"{icon.Key} runs off the sheet.");

                // The middle of each rectangle is the icon's own opaque colour, not a gap.
                Assert.Equal(255, bitmap.GetPixel(x + (w / 2), y + (h / 2)).Alpha);
            }

            // The pin is red where the index says it is.
            SkiaSharp.SKColor middle = bitmap.GetPixel(
                (int)entry["x"]! + (pin.SheetWidth * ratio / 2), (int)entry["y"]! + (pin.SheetHeight * ratio / 2));

            Assert.True(middle.Red > 180 && middle.Green < 80, $"The pin's rectangle holds {middle}.");
        }
    }

    [Fact]
    public async Task The_same_service_serves_the_same_bytes_twice()
    {
        PublishedService service = Service(Layer("schools", Renderer(Pin, Flag)));

        byte[] first = await GeneratedSprites.FileAsync(service, 1, true, null, new SkiaMapCanvasFactory(), CancellationToken.None);
        byte[] second = await GeneratedSprites.FileAsync(service, 1, true, null, new SkiaMapCanvasFactory(), CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_stored_style_drawing_a_generated_icon_fits_while_a_layer_draws_the_picture()
    {
        string name = MarkerPicture.FromBytes(Pin, null, "x").Name;
        string style = "{\"version\":8,\"sources\":{\"esri\":{\"type\":\"vector\",\"url\":\"../../\"}},"
            + "\"sprite\":\"../sprites/sprite\","
            + "\"layers\":[{\"id\":\"s\",\"type\":\"symbol\",\"source\":\"esri\",\"source-layer\":\"schools\","
            + "\"layout\":{\"icon-image\":\"" + name + "\"}}]}";

        PublishedService drawing = Service(Layer("schools", Renderer(Pin)));
        PublishedService not = Service(Layer("schools", null));

        Assert.True(VectorTileEndpoints.StoredStyleFits(
            style, ["schools"], GeneratedSprites.Names(drawing), out string? stale), stale);
        Assert.False(VectorTileEndpoints.StoredStyleFits(
            style, ["schools"], GeneratedSprites.Names(not).Count == 0 ? null : GeneratedSprites.Names(not), out _));
    }

    [Fact]
    public void A_generated_icon_does_not_hold_an_uploaded_sheet()
    {
        string name = MarkerPicture.FromBytes(Pin, null, "x").Name;
        string style = "{\"version\":8,\"layers\":[{\"id\":\"s\",\"type\":\"symbol\",\"source-layer\":\"schools\","
            + "\"layout\":{\"icon-image\":\"" + name + "\"}}]}";

        Assert.Empty(AdminEndpoints.MissingFromSheet(style, ["school"]));
    }
}
