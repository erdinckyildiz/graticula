using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Graticula.Testing;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A layer's picture markers are read, bounded, stored and published — ADR-099.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusal is the thing being replaced, so the refusals that remain are tested as hard as the
/// acceptance.</b> A picture given by URL is never fetched, SVG is not read, and every bound is checked
/// from the header before anything decodes. Each of those is a sentence a publisher reads, and a test
/// that only asserted the exception type would let the sentence rot.
/// </para>
/// <para>
/// <b>The tile face is asserted in the shape ArcGIS Pro draws</b> — a `symbol` layer per class with a
/// literal `icon-image` and no expression over a feature's attribute (D-280) — and then read back,
/// because a style this server publishes has to store again as the renderer it came from.
/// </para>
/// </remarks>
public sealed class PictureMarkerTests
{
    private static readonly byte[] Red = TestPictures.Png(32, 16, 220, 30, 30);

    private static readonly byte[] Blue = TestPictures.Png(24, 24, 30, 30, 220);

    [Fact]
    public void A_png_data_uri_is_read_with_its_size_and_a_name_from_its_bytes()
    {
        MarkerPicture picture = MarkerPicture.FromUrl(TestPictures.DataUri(Red), "the test");

        Assert.Equal("image/png", picture.ContentType);
        Assert.Equal(32, picture.PixelWidth);
        Assert.Equal(16, picture.PixelHeight);
        Assert.StartsWith(MarkerPicture.NamePrefix, picture.Name, StringComparison.Ordinal);
        Assert.Equal(32, picture.SheetWidth);
        Assert.Equal(16, picture.SheetHeight);

        // The same bytes are the same name; one byte different is another.
        Assert.Equal(picture.Name, MarkerPicture.FromUrl(TestPictures.DataUri(Red), "again").Name);
        Assert.NotEqual(picture.Name, MarkerPicture.FromUrl(TestPictures.DataUri(Blue), "other").Name);
    }

    [Fact]
    public void A_picture_larger_than_the_sheet_side_is_packed_smaller_in_proportion()
    {
        MarkerPicture picture = MarkerPicture.FromBytes(TestPictures.Png(512, 256, 1, 2, 3), null, "the test");

        Assert.Equal(MarkerPicture.SheetSide, picture.SheetWidth);
        Assert.Equal(MarkerPicture.SheetSide / 2, picture.SheetHeight);
    }

    [Theory]
    [InlineData("https://example.com/school.png")]
    [InlineData("http://10.0.0.1/admin/icon.png")]
    [InlineData("//example.com/school.png")]
    public void A_picture_named_by_url_is_refused_and_never_fetched(string url)
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(() => MarkerPicture.FromUrl(url, "the class"));

        Assert.Contains("never fetches", refused.Message, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_name_is_refused_because_it_resolves_against_nothing_held_here()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => MarkerPicture.FromUrl("4f1c7a3e9b0d", "the class"));

        Assert.Contains("not a data URI", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_svg_is_refused_whether_it_is_declared_or_only_sniffed()
    {
        string svg = Convert.ToBase64String("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray());

        Assert.Contains(
            "SVG",
            Assert.Throws<SymbologyException>(() => MarkerPicture.FromUrl("data:image/svg+xml;base64," + svg, "x")).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "SVG",
            Assert.Throws<SymbologyException>(() => MarkerPicture.FromBase64(svg, null, "x")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_gif_is_refused_by_name()
    {
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0];

        SymbologyException refused = Assert.Throws<SymbologyException>(() => MarkerPicture.FromBytes(gif, null, "x"));

        Assert.Contains("neither a PNG nor a JPEG", refused.Message, StringComparison.Ordinal);
        Assert.Throws<SymbologyException>(() => MarkerPicture.FromBytes(Red, "image/gif", "x"));
    }

    [Fact]
    public void A_declaration_that_disagrees_with_the_bytes_is_refused()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => MarkerPicture.FromBytes(Red, "image/jpeg", "x"));

        Assert.Contains("have to agree", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_over_the_side_bound_is_refused_from_its_header()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => MarkerPicture.FromBytes(TestPictures.Png(600, 1, 0, 0, 0), null, "x"));

        Assert.Contains("600 × 1", refused.Message, StringComparison.Ordinal);

        // <b>From the header, before any decoder.</b> A JPEG that declares 20,000 pixels and holds none is
        // refused for its size, which is only possible if nothing tried to decode it.
        Assert.Contains(
            "20000 × 20000",
            Assert.Throws<SymbologyException>(
                () => MarkerPicture.FromBytes(TestPictures.JpegHeader(20000, 20000), null, "x")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_jpeg_is_sized_from_its_start_of_frame()
    {
        MarkerPicture picture = MarkerPicture.FromBytes(TestPictures.JpegHeader(40, 30), "image/jpg", "x");

        Assert.Equal("image/jpeg", picture.ContentType);
        Assert.Equal(40, picture.PixelWidth);
        Assert.Equal(30, picture.PixelHeight);
    }

    [Fact]
    public void A_picture_over_the_byte_bound_is_refused_before_it_is_decoded_from_base64()
    {
        string huge = new('A', (MarkerPicture.MaximumBytes * 4 / 3) + 400);

        Assert.Contains(
            "at most 256 KB",
            Assert.Throws<SymbologyException>(() => MarkerPicture.FromBase64(huge, "image/png", "x")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_renderer_whose_pictures_together_pass_the_layer_bound_is_refused()
    {
        // Five pictures of noise, each under the per-picture bound and together over a megabyte.
        JsonArray classes = [];

        for (int i = 0; i < 5; i++)
        {
            Random noise = new(i);
            byte[] png = TestPictures.Png(250, 250, (_, _) =>
                ((byte)noise.Next(256), (byte)noise.Next(256), (byte)noise.Next(256), 255));

            Assert.True(png.Length <= MarkerPicture.MaximumBytes, $"Picture {i} is {png.Length} bytes.");

            classes.Add(Class(i.ToString(System.Globalization.CultureInfo.InvariantCulture), png, 12));
        }

        SymbologyException refused = Assert.Throws<SymbologyException>(() => Cim.Project(Unique(classes), strict: true));

        Assert.Contains("together", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_picture_used_by_many_classes_is_counted_once()
    {
        JsonArray classes = [];

        for (int i = 0; i < 40; i++)
        {
            classes.Add(Class(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Red, 12));
        }

        CimProjection projection = Cim.Project(Unique(classes));

        Assert.Single(projection.Pictures());
    }

    [Fact]
    public void A_cim_picture_marker_is_projected_with_its_size_offset_and_rotation()
    {
        JsonObject renderer = Simple(Picture(Red, size: 12, offsetX: 3, offsetY: -2, rotation: 30));

        CimProjection projection = Cim.Project(renderer);

        CimPicture picture = Assert.IsType<CimPicture>(Assert.Single(projection.Classes[0].Symbol.Paints));

        Assert.Equal(12, picture.Size);
        Assert.Equal(3, picture.OffsetX);
        Assert.Equal(-2, picture.OffsetY);
        Assert.Equal(30, picture.Rotation);
        Assert.Empty(projection.NotDrawn);
    }

    [Fact]
    public void A_clockwise_marker_is_stored_turned_the_other_way()
    {
        JsonObject marker = Picture(Red, 12, rotation: 30);
        marker["rotateClockwise"] = true;

        CimPicture picture = (CimPicture)Cim.Project(Simple(marker)).Classes[0].Symbol.Paints[0];

        Assert.Equal(-30, picture.Rotation);
    }

    [Fact]
    public void An_esri_picture_marker_is_read_from_its_image_data_and_written_back_with_it()
    {
        JsonObject drawingInfo = new()
        {
            ["renderer"] = new JsonObject
            {
                ["type"] = "simple",
                ["symbol"] = new JsonObject
                {
                    ["type"] = "esriPMS",
                    ["url"] = "4f1c7a3e9b0d",
                    ["imageData"] = Convert.ToBase64String(Red),
                    ["contentType"] = "image/png",
                    ["width"] = 24,
                    ["height"] = 12,
                    ["angle"] = 45,
                    ["xoffset"] = 2,
                    ["yoffset"] = 3,
                },
            },
        };

        CimWrite stored = CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Point);

        JsonObject layer = (JsonObject)stored.Renderer["symbol"]!["symbol"]!["symbolLayers"]![0]!;

        Assert.Equal("CIMPictureMarker", (string?)layer["type"]);
        Assert.StartsWith("data:image/png;base64,", (string?)layer["url"], StringComparison.Ordinal);
        Assert.Equal(12, (double)layer["size"]!);

        JsonObject back = (JsonObject)CimEsri.ToDrawingInfo(stored.Renderer, "sites").DrawingInfo["renderer"]!["symbol"]!;

        Assert.Equal("esriPMS", (string?)back["type"]);
        Assert.Equal(Convert.ToBase64String(Red), (string?)back["imageData"]);
        Assert.Equal("image/png", (string?)back["contentType"]);
        Assert.Equal(24, (double)back["width"]!);
        Assert.Equal(12, (double)back["height"]!);
        Assert.Equal(45, (double)back["angle"]!);
        Assert.Equal(2, (double)back["xoffset"]!);
        Assert.Equal(3, (double)back["yoffset"]!);
    }

    [Fact]
    public void An_esri_picture_marker_with_only_a_url_is_refused()
    {
        JsonObject drawingInfo = new()
        {
            ["renderer"] = new JsonObject
            {
                ["type"] = "simple",
                ["symbol"] = new JsonObject
                {
                    ["type"] = "esriPMS",
                    ["url"] = "https://example.com/icon.png",
                    ["width"] = 12,
                    ["height"] = 12,
                },
            },
        };

        Assert.Contains(
            "never fetches",
            Assert.Throws<SymbologyException>(() => CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Point)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_fill_is_still_refused_and_says_why()
    {
        JsonObject drawingInfo = new()
        {
            ["renderer"] = new JsonObject
            {
                ["type"] = "simple",
                ["symbol"] = new JsonObject { ["type"] = "esriPFS", ["imageData"] = Convert.ToBase64String(Red) },
            },
        };

        Assert.Contains(
            "picture fills",
            Assert.Throws<SymbologyException>(() => CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Polygon)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_simple_picture_renderer_publishes_one_icon_layer_from_the_sprite()
    {
        MarkerPicture red = MarkerPicture.FromBytes(Red, null, "x");

        DerivedStyle style = CimStyle.ToMapLibre(Simple(Picture(Red, 12)), "sites");

        JsonObject layer = (JsonObject)Assert.Single((JsonArray)style.Style["layers"]!)!;

        Assert.Equal("symbol", (string?)layer["type"]);

        JsonObject layout = (JsonObject)layer["layout"]!;

        Assert.Equal(red.Name, (string?)layout["icon-image"]);

        // 12 points is 16 pixels, and the icon is 16 pixels tall in the sheet.
        Assert.Equal(Math.Round(12 / 0.75 / red.SheetHeight, 4), (double)layout["icon-size"]!);
        Assert.True((bool)layout["icon-allow-overlap"]!);
        Assert.True((bool)layout["icon-ignore-placement"]!);
        Assert.Null(layout["icon-offset"]);
        Assert.Null(layout["icon-rotate"]);
    }

    [Fact]
    public void Each_class_gets_its_own_filtered_icon_layer_and_no_expression_over_a_field()
    {
        JsonArray classes = [Class("school", Red, 12), Class("clinic", Blue, 18)];

        DerivedStyle style = CimStyle.ToMapLibre(Unique(classes), "sites");

        List<JsonObject> layers = [.. ((JsonArray)style.Style["layers"]!).OfType<JsonObject>()];

        Assert.Equal(2, layers.Count);
        Assert.All(layers, l => Assert.Equal("symbol", (string?)l["type"]));
        Assert.All(layers, l => Assert.NotNull(l["filter"]));

        // <b>No `get`, `match` or `step` in any paint or layout</b> — D-280: Pro draws none of them.
        Assert.All(layers, l =>
        {
            string text = (l["layout"]!.ToJsonString()) + (l["paint"]?.ToJsonString() ?? string.Empty);

            Assert.DoesNotContain("\"get\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"match\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"step\"", text, StringComparison.Ordinal);
        });

        string[] names = [.. layers.Select(l => (string)l["layout"]!["icon-image"]!)];

        Assert.Contains(MarkerPicture.FromBytes(Red, null, "x").Name, names);
        Assert.Contains(MarkerPicture.FromBytes(Blue, null, "x").Name, names);
    }

    [Fact]
    public void The_published_style_reads_back_into_the_renderer_it_came_from()
    {
        JsonArray classes =
        [
            Class("school", Red, 12, offsetX: 4, offsetY: 2, rotation: 90),
            Class("clinic", Blue, 18),
        ];

        JsonObject renderer = Unique(classes);
        DerivedStyle first = CimStyle.ToMapLibre(renderer, "sites");

        Dictionary<string, MarkerPicture> pictures = Cim.Project(renderer).Pictures().ToDictionary(p => p.Name);

        CimWrite read = CimStyle.FromMapLibre(first.Style, GeometryKind.Point, pictures);

        CimProjection back = Cim.Project(read.Renderer);

        Assert.Equal(Cim.UniqueValue, back.Kind);
        Assert.Equal(2, back.Classes.Count);

        CimPicture school = (CimPicture)back.Classes.Single(c => c.Values.Contains("school")).Symbol.Paints.Single();

        Assert.Equal(12, school.Size, 3);
        Assert.Equal(4, school.OffsetX, 2);
        Assert.Equal(2, school.OffsetY, 2);
        Assert.Equal(90, school.Rotation, 3);

        // And it publishes the same style a second time.
        DerivedStyle second = CimStyle.ToMapLibre(read.Renderer, "sites");

        Assert.Equal(first.Style.ToJsonString(), second.Style.ToJsonString());
    }

    [Fact]
    public void An_icon_this_server_does_not_hold_is_refused_with_the_way_forward()
    {
        JsonObject style = JsonNode.Parse("""
            {"version":8,"layers":[{"id":"s","type":"symbol","layout":{"icon-image":"school-15"}}]}
            """)!.AsObject();

        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => SymbologyConversion.Read(style.ToJsonString(), GeometryKind.Point));

        Assert.Contains("school-15", refused.Message, StringComparison.Ordinal);
        Assert.Contains("CIMPictureMarker", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raster_faces_compile_the_icon_layer_and_resolve_the_class_picture()
    {
        JsonArray classes = [Class("school", Red, 12), Class("clinic", Blue, 18)];

        SymbologyPlan plan = SymbologyPlan.Compile(Unique(classes).ToJsonString());

        PlanLayer.Icon icon = Assert.IsType<PlanLayer.Icon>(Assert.Single(plan.Layers));

        MapSymbol.Picture clinic = Assert.IsType<MapSymbol.Picture>(icon.Resolve(new StyleExpression.Context(
            new Dictionary<string, object?> { ["kind"] = "clinic" }, 10)));

        Assert.Equal(MarkerPicture.FromBytes(Blue, null, "x"), clinic.Image);
        Assert.Equal(18 / 0.75, clinic.Height, 1);

        // The legend has a row per picture, beside the fallback row every `match` legend carries.
        StyleExpression.Classification legend = plan.LegendClasses()!.Value;

        Assert.Equal("kind", legend.Field);
        Assert.Contains(legend.Cases, c => Equals(c.Value, "school"));
        Assert.Contains(legend.Cases, c => Equals(c.Value, "clinic"));
    }

    [Fact]
    public void The_map_draws_the_picture_at_the_point()
    {
        SymbologyPlan plan = SymbologyPlan.Compile(Simple(Picture(Red, 12)).ToJsonString());

        Recording canvas = new();
        MapRenderer renderer = new(canvas, new PixelTransform(new Envelope(0, 0, 100, 100), 100, 100), geographic: false);

        renderer.Draw(plan, [new Graticula.Features.Feature(
            "1", new Point(25, 75), new Graticula.Features.FeatureSchema(["kind"]), ["x"])]);

        (double x, double y, MapSymbol.Picture drawn) = Assert.Single(canvas.Pictures);

        Assert.Equal(25, x, 3);
        Assert.Equal(25, y, 3);
        Assert.Equal(16, drawn.Height, 1);
        Assert.Equal(32, drawn.Width, 1);
    }

    [Fact]
    public void The_sheet_is_packed_without_overlap_and_the_same_way_whatever_the_order()
    {
        List<MarkerPicture> pictures = [];

        for (int i = 0; i < 30; i++)
        {
            pictures.Add(MarkerPicture.FromBytes(
                TestPictures.Png(10 + (i * 7 % 120), 8 + (i * 13 % 110), (byte)i, 0, 0), null, "x"));
        }

        SpritePacking packed = SpriteLayout.Pack(pictures);
        SpritePacking again = SpriteLayout.Pack([.. Enumerable.Reverse(pictures)]);

        Assert.Equal(
            packed.Slots.Select(s => (s.Picture.Name, s.X, s.Y)),
            again.Slots.Select(s => (s.Picture.Name, s.X, s.Y)));

        foreach (SpriteSlot a in packed.Slots)
        {
            Assert.InRange(a.X + a.Picture.SheetWidth, 0, packed.Width);
            Assert.InRange(a.Y + a.Picture.SheetHeight, 0, packed.Height);
            Assert.True(a.X + a.Picture.SheetWidth <= SpriteLayout.RowWidth);

            foreach (SpriteSlot b in packed.Slots)
            {
                if (ReferenceEquals(a.Picture, b.Picture))
                {
                    continue;
                }

                bool apart = a.X + a.Picture.SheetWidth <= b.X || b.X + b.Picture.SheetWidth <= a.X
                    || a.Y + a.Picture.SheetHeight <= b.Y || b.Y + b.Picture.SheetHeight <= a.Y;

                Assert.True(apart, $"{a.Picture.Name} and {b.Picture.Name} overlap.");
            }
        }
    }

    [Fact]
    public void The_index_matches_the_packing_below_an_uploaded_sheet_and_drops_a_reserved_name()
    {
        MarkerPicture red = MarkerPicture.FromBytes(Red, null, "x");
        SpritePacking packed = SpriteLayout.Pack([red]);

        string uploaded = "{\"school\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"sdf\":true},\""
            + MarkerPicture.NamePrefix + "old\":{\"x\":0,\"y\":0,\"width\":1,\"height\":1}}";

        JsonObject index = JsonNode.Parse(SpriteLayout.Index(packed, 2, 40, uploaded))!.AsObject();

        Assert.True((bool)index["school"]!["sdf"]!);
        Assert.Null(index[MarkerPicture.NamePrefix + "old"]);

        JsonObject icon = index[red.Name]!.AsObject();

        Assert.Equal(red.SheetWidth * 2, (int)icon["width"]!);
        Assert.Equal(red.SheetHeight * 2, (int)icon["height"]!);
        Assert.Equal(40, (int)icon["y"]!);
        Assert.Equal(2, (int)icon["pixelRatio"]!);
    }

    [Fact]
    public void The_service_pictures_are_collected_once_by_name_and_a_bad_document_adds_nothing()
    {
        string one = Simple(Picture(Red, 12)).ToJsonString();
        string two = Unique([Class("a", Red, 10), Class("b", Blue, 10)]).ToJsonString();

        IReadOnlyList<MarkerPicture> found = SpriteLayout.PicturesOf([one, null, "{\"CIMPictureMarker\":", two]);

        Assert.Equal(2, found.Count);
        Assert.True(string.CompareOrdinal(found[0].Name, found[1].Name) < 0);
    }

    [Fact]
    public void A_stored_picture_named_by_url_is_a_loss_and_the_rest_of_the_renderer_projects()
    {
        // As a document kept from a real ArcGIS server would store it: one class's picture only a URL.
        JsonObject urlOnly = Picture(Red, 14);
        urlOnly["url"] = "https://source.example/arcgis/rest/services/x/MapServer/0/images/4f1c7a3e";

        JsonObject Stored()
        {
            JsonArray classes = [Class("school", Red, 12), Class("clinic", Blue, 18)];
            ((JsonObject)classes[1]!)["symbol"] = Symbol((JsonObject)urlOnly.DeepClone());

            return Unique(classes);
        }

        // Refused on a write, with the reason.
        Assert.Contains(
            "never fetches",
            Assert.Throws<SymbologyException>(() => Cim.Project(Stored(), strict: true)).Message,
            StringComparison.Ordinal);

        // Read as stored: the loss is reported, the other class keeps its picture, and the picture-only
        // class draws a grey marker of its size instead of sending the layer to its generated appearance.
        CimProjection read = Cim.Project(Stored());

        Assert.Contains(read.NotDrawn, l => l.Contains("never fetches", StringComparison.Ordinal));
        Assert.IsType<CimPicture>(read.Classes[0].Symbol.Paints.Single());

        CimMarker grey = Assert.IsType<CimMarker>(read.Classes[1].Symbol.Paints.Single());

        Assert.Equal(14, grey.Size);
        Assert.Equal(new Rgba(136, 136, 136, 255), grey.Colour);

        // Every face derives from it, and the losses reach the places losses are read today.
        string stored = Stored().ToJsonString();

        DerivedDrawingInfo esri = SymbologyConversion.ToDrawingInfo(stored, "sites", GeometryKind.Point);

        Assert.Contains(esri.Losses, l => l.Contains("never fetches", StringComparison.Ordinal));
        Assert.Contains(SymbologyConversion.ToStyle(stored, "sites", GeometryKind.Point).Losses,
            l => l.Contains("never fetches", StringComparison.Ordinal));
        // The two classes now differ in shape, which a style layer cannot, and that is said; the clinic draws
        // no icon on the tile face and the raster faces rather than borrowing the school's.
        Assert.Contains(
            SymbologyConversion.ToStyle(stored, "sites", GeometryKind.Point).Losses,
            l => l.Contains("different stack", StringComparison.Ordinal));

        PlanLayer.Icon icon = Assert.IsType<PlanLayer.Icon>(Assert.Single(SymbologyPlan.Compile(stored).Layers));

        Assert.Null(icon.Resolve(new StyleExpression.Context(new Dictionary<string, object?> { ["kind"] = "clinic" }, 10)));
        Assert.NotNull(icon.Resolve(new StyleExpression.Context(new Dictionary<string, object?> { ["kind"] = "school" }, 10)));
        Assert.Single(SpriteLayout.PicturesOf([stored]));
    }

    [Fact]
    public void A_stored_picture_beside_other_layers_is_dropped_as_the_old_reader_dropped_it()
    {
        JsonObject svg = Picture(Red, 12);
        svg["url"] = "data:image/svg+xml;base64," + Convert.ToBase64String("<svg/>"u8.ToArray());

        JsonObject symbol = Symbol(svg);
        ((JsonArray)symbol["symbol"]!["symbolLayers"]!).Add(new JsonObject
        {
            ["type"] = "CIMVectorMarker",
            ["enable"] = true,
            ["size"] = 6,
            ["markerGraphics"] = new JsonArray(),
        });

        CimProjection read = Cim.Project(new JsonObject { ["type"] = Cim.Simple, ["symbol"] = symbol });

        Assert.IsType<CimMarker>(Assert.Single(read.Classes[0].Symbol.Paints));
        Assert.Contains(read.NotDrawn, l => l.Contains("SVG", StringComparison.Ordinal));
    }

    [Fact]
    public void The_migrate_path_keeps_a_url_only_picture_as_a_loss_and_carries_image_data_with_its_picture()
    {
        string drawingInfo = new JsonObject
        {
            ["renderer"] = new JsonObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = "kind",
                ["uniqueValueInfos"] = new JsonArray(
                    new JsonObject
                    {
                        ["value"] = "school",
                        ["symbol"] = new JsonObject
                        {
                            ["type"] = "esriPMS",
                            ["imageData"] = Convert.ToBase64String(Red),
                            ["contentType"] = "image/png",
                            ["width"] = 24,
                            ["height"] = 12,
                        },
                    },
                    new JsonObject
                    {
                        ["value"] = "clinic",
                        ["symbol"] = new JsonObject
                        {
                            ["type"] = "esriPMS",
                            ["url"] = "4f1c7a3e9b0d",
                            ["width"] = 16,
                            ["height"] = 16,
                        },
                    }),
            },
        }.ToJsonString();

        // A PUT without the flag is refused, and says why.
        Assert.Contains(
            "4f1c7a3e9b0d",
            Assert.Throws<SymbologyException>(() => SymbologyConversion.Read(drawingInfo, GeometryKind.Point)).Message,
            StringComparison.Ordinal);

        // What `graticula tools migrate` sends: the layer's drawing is stored, with the loss.
        SymbologyWrite migrated = SymbologyConversion.Read(
            drawingInfo, GeometryKind.Point, keepUnusablePictures: true);

        Assert.Contains(migrated.Losses, l => l.Contains("4f1c7a3e9b0d", StringComparison.Ordinal));

        CimProjection stored = Cim.Project(JsonNode.Parse(migrated.Canonical)!.AsObject());

        CimClass school = stored.Classes.Single(c => c.Values.Contains("school"));
        CimClass clinic = stored.Classes.Single(c => c.Values.Contains("clinic"));

        Assert.Equal(
            MarkerPicture.FromBytes(Red, null, "x"),
            Assert.IsType<CimPicture>(school.Symbol.Paints.Single()).Picture);
        Assert.Equal(16, Assert.IsType<CimMarker>(clinic.Symbol.Paints.Single()).Size);

        // The URL is kept in the stored document as it was given, and nothing else is.
        Assert.Contains("\"url\":\"4f1c7a3e9b0d\"", migrated.Canonical, StringComparison.Ordinal);
    }

    private static JsonObject Picture(
        byte[] png, double size, double offsetX = 0, double offsetY = 0, double rotation = 0) =>
        new()
        {
            ["type"] = "CIMPictureMarker",
            ["enable"] = true,
            ["size"] = size,
            ["offsetX"] = offsetX,
            ["offsetY"] = offsetY,
            ["rotation"] = rotation,
            ["url"] = TestPictures.DataUri(png),
        };

    private static JsonObject Symbol(JsonObject layer) =>
        new()
        {
            ["type"] = "CIMSymbolReference",
            ["symbol"] = new JsonObject
            {
                ["type"] = "CIMPointSymbol",
                ["symbolLayers"] = new JsonArray(layer),
            },
        };

    private static JsonObject Simple(JsonObject layer) =>
        new()
        {
            ["type"] = Cim.Simple,
            ["symbol"] = Symbol(layer),
        };

    private static JsonObject Class(
        string value, byte[] png, double size, double offsetX = 0, double offsetY = 0, double rotation = 0) =>
        new()
        {
            ["label"] = value,
            ["values"] = new JsonArray(new JsonObject
            {
                ["type"] = "CIMUniqueValue",
                ["fieldValues"] = new JsonArray(value),
            }),
            ["symbol"] = Symbol(Picture(png, size, offsetX, offsetY, rotation)),
        };

    private static JsonObject Unique(JsonArray classes) =>
        new()
        {
            ["type"] = Cim.UniqueValue,
            ["fields"] = new JsonArray("kind"),
            ["groups"] = new JsonArray(new JsonObject { ["classes"] = classes }),
        };

    /// <summary>A canvas that remembers where pictures were asked for.</summary>
    private sealed class Recording : IMapCanvas
    {
        public List<(double X, double Y, MapSymbol.Picture Symbol)> Pictures { get; } = [];

        public int Width => 100;

        public int Height => 100;

        public void Clear(Rgba colour)
        {
        }

        public void FillArea(PixelPath path, MapSymbol.Area symbol)
        {
        }

        public void StrokeLine(PixelPath path, MapSymbol.Stroke symbol)
        {
        }

        public void DrawMarker(double x, double y, MapSymbol.Marker symbol)
        {
        }

        public void DrawPicture(double x, double y, MapSymbol.Picture symbol) => Pictures.Add((x, y, symbol));

        public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y) => new(x, y, x, y);

        public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
        {
        }

        public void DrawImage(ReadOnlySpan<Rgba> pixels, int width, int height, PixelBox destination)
        {
        }

        public byte[] Encode(MapImageFormat format, int quality) => [];

        public void Dispose()
        {
        }
    }
}
