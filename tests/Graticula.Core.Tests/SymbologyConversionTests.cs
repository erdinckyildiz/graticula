using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The two conversions ADR-033 rests on, and the losses they report.
/// </summary>
/// <remarks>
/// <b>These are §7's conditions 2, 3 and 4 written as tests, and they are the whole
/// reason the decision was allowed to accept a lossy conversion.</b> §2C's chosen
/// alternative takes a risk — that a MapLibre document can be projected onto an Esri
/// renderer well enough to be useful — and the mitigation is not that the projection
/// is good. It is that when the projection loses something, it says so.
/// </remarks>
public sealed class SymbologyConversionTests
{
    // <b>Named rather than inline, because CA1861 is on and it is right here.</b> These
    // are the colours the round-trip tests expect back, and an expectation with a name
    // reads better in a failure than `ParcelFill` does.
    private static readonly int[] ParcelFill = [204, 187, 68, 115];
    private static readonly int[] ParcelOutline = [80, 70, 20, 255];
    private static readonly int[] Residential = [68, 170, 153];
    private static readonly int[] Park = [17, 119, 51, 255];
    private static readonly int[] Fallback = [153, 153, 153, 255];
    private static readonly int[] LightestBreak = [237, 248, 233, 255];
    private static readonly int[] DarkestBreak = [35, 139, 69, 255];
    private static readonly int[] HalfOpaque = [10, 20, 30, 128];
    private static readonly string[] Zoning = ["residential", "industrial", "park"];
    /// <summary>
    /// A zoom-interpolated width is reported as lost, naming the property.
    /// </summary>
    /// <remarks>
    /// <b>§7 condition 2, and it names this exact case: "a zoom-interpolated width is
    /// the obvious case".</b> An Esri symbol carries one width; this style carries
    /// four. A server that emitted the width at zoom 6 and said nothing would have
    /// told a client something untrue about the map — the line is hairline at 6 and
    /// heavy at 14, and no client could tell which number it received.
    /// </remarks>
    [Fact]
    public void A_zoom_interpolated_width_is_reported_as_lost()
    {
        const string style = """
        {
          "version": 8,
          "layers": [{
            "id": "roads",
            "type": "line",
            "paint": {
              "line-color": "#8b1a1a",
              "line-width": ["interpolate", ["linear"], ["zoom"], 6, 0.5, 14, 6]
            }
          }]
        }
        """;

        SymbologyWrite stored = SymbologyConversion.Read(style, GeometryKind.LineString);

        DerivedDrawingInfo derived = SymbologyConversion.ToDrawingInfo(
            stored.Canonical, "roads", GeometryKind.LineString);

        Assert.Contains(derived.Losses, l =>
            l.Contains("line-width", StringComparison.Ordinal)
            && l.Contains("zoom", StringComparison.Ordinal));

        // And the number it did emit is the value at the lowest stop, not a guess:
        // 0.5 px at zoom 6, which is 0.375 pt.
        JsonNode symbol = derived.DrawingInfo["renderer"]!["symbol"]!;
        Assert.Equal(0.375, symbol["width"]!.GetValue<double>(), 3);
    }

    /// <summary>
    /// A style that loses nothing reports nothing.
    /// </summary>
    /// <remarks>
    /// <b>The pair, and it is what makes the report worth reading.</b> A conversion
    /// that always reported a loss would be a conversion nobody read the report of —
    /// the same failure as a warning that fires on every build.
    /// </remarks>
    [Fact]
    public void A_style_that_loses_nothing_reports_nothing()
    {
        const string style = """
        {
          "version": 8,
          "layers": [{
            "id": "parcels",
            "type": "fill",
            "paint": { "fill-color": "rgba(204,187,68,0.45)" }
          }]
        }
        """;

        SymbologyWrite stored = SymbologyConversion.Read(style, GeometryKind.Polygon);
        Assert.Empty(stored.Losses);

        DerivedDrawingInfo derived = SymbologyConversion.ToDrawingInfo(
            stored.Canonical, "parcels", GeometryKind.Polygon);

        Assert.Empty(derived.Losses);
    }

    /// <summary>
    /// Labels, hatched fills and marker shapes are each named rather than dropped.
    /// </summary>
    [Theory]
    [InlineData("esriSFSBackwardDiagonal", "hatch or a picture fill")]
    [InlineData("esriSFSCross", "hatch or a picture fill")]
    public void A_fill_style_with_no_sprite_is_reported(string esriStyle, string expected)
    {
        string drawingInfo = $$"""
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSFS",
              "style": "{{esriStyle}}",
              "color": [200, 30, 30, 255]
            }
          }
        }
        """;

        SymbologyWrite stored = SymbologyConversion.Read(drawingInfo, GeometryKind.Polygon);

        Assert.Contains(stored.Losses, l => l.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(stored.Losses, l => l.Contains(esriStyle, StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>simple</c> renderer comes back as the same <c>simple</c> renderer.
    /// </summary>
    /// <remarks>
    /// <b>§7 condition 3, first of the three families.</b> If a customer's own
    /// symbology comes back different from what they sent, the migration promise is
    /// worth less than the paste-in convenience that motivated accepting it.
    /// </remarks>
    [Fact]
    public void A_simple_renderer_round_trips()
    {
        const string drawingInfo = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSFS",
              "style": "esriSFSSolid",
              "color": [204, 187, 68, 115],
              "outline": {
                "type": "esriSLS",
                "style": "esriSLSSolid",
                "color": [80, 70, 20, 255],
                "width": 1
              }
            }
          },
          "transparency": 0
        }
        """;

        JsonNode back = RoundTrip(drawingInfo, "parcels", GeometryKind.Polygon);
        JsonNode renderer = back["renderer"]!;

        Assert.Equal("simple", renderer["type"]!.GetValue<string>());

        JsonNode symbol = renderer["symbol"]!;
        Assert.Equal("esriSFS", symbol["type"]!.GetValue<string>());
        Assert.Equal("esriSFSSolid", symbol["style"]!.GetValue<string>());
        Assert.Equal(ParcelFill, Rgba(symbol["color"]!));

        JsonNode outline = symbol["outline"]!;
        Assert.Equal(ParcelOutline, Rgba(outline["color"]!));
    }

    /// <summary>
    /// A <c>uniqueValue</c> renderer comes back with its field and every class.
    /// </summary>
    /// <remarks>§7 condition 3, second family.</remarks>
    [Fact]
    public void A_unique_value_renderer_round_trips()
    {
        const string drawingInfo = """
        {
          "renderer": {
            "type": "uniqueValue",
            "field1": "kind",
            "defaultSymbol": {
              "type": "esriSFS", "style": "esriSFSSolid", "color": [153, 153, 153, 255]
            },
            "uniqueValueInfos": [
              { "value": "residential",
                "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                            "color": [ 68, 170, 153, 255] } },
              { "value": "industrial",
                "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                            "color": [204, 102, 119, 255] } },
              { "value": "park",
                "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                            "color": [ 17, 119,  51, 255] } }
            ]
          }
        }
        """;

        JsonNode back = RoundTrip(drawingInfo, "zoning", GeometryKind.Polygon);
        JsonNode renderer = back["renderer"]!;

        Assert.Equal("uniqueValue", renderer["type"]!.GetValue<string>());
        Assert.Equal("kind", renderer["field1"]!.GetValue<string>());

        JsonArray infos = (JsonArray)renderer["uniqueValueInfos"]!;
        Assert.Equal(3, infos.Count);

        Assert.Equal(
            Zoning,
            infos.Select(i => i!["value"]!.GetValue<string>()).ToArray());

        Assert.Equal(Residential, Rgba(infos[0]!["symbol"]!["color"]!)[..3]);
        Assert.Equal(Park, Rgba(infos[2]!["symbol"]!["color"]!));
        Assert.Equal(
            Fallback, Rgba(renderer["defaultSymbol"]!["color"]!));
    }

    /// <summary>
    /// A <c>classBreaks</c> renderer comes back with its breaks in order.
    /// </summary>
    /// <remarks>
    /// <para>§7 condition 3, third family.</para>
    /// <para>
    /// <b>The top class is where this conversion is honestly lossy.</b> A MapLibre
    /// <c>step</c> expression has no upper bound on its last class, so the last
    /// <c>classMaxValue</c> cannot survive the trip — it is reconstructed from the
    /// last break. That is reported as a loss, and this test asserts the report
    /// rather than pretending the number came back.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_class_breaks_renderer_round_trips_and_says_what_the_top_class_costs()
    {
        const string drawingInfo = """
        {
          "renderer": {
            "type": "classBreaks",
            "field": "population",
            "classBreakInfos": [
              { "classMaxValue": 1000,
                "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                            "color": [237, 248, 233, 255] } },
              { "classMaxValue": 10000,
                "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                            "color": [186, 228, 179, 255] } },
              { "classMaxValue": 100000,
                "symbol": { "type": "esriSFS", "style": "esriSFSSolid",
                            "color": [ 35, 139,  69, 255] } }
            ]
          }
        }
        """;

        SymbologyWrite stored = SymbologyConversion.Read(drawingInfo, GeometryKind.Polygon);

        DerivedDrawingInfo derived = SymbologyConversion.ToDrawingInfo(
            stored.Canonical, "towns", GeometryKind.Polygon);

        JsonNode renderer = derived.DrawingInfo["renderer"]!;

        Assert.Equal("classBreaks", renderer["type"]!.GetValue<string>());
        Assert.Equal("population", renderer["field"]!.GetValue<string>());

        JsonArray infos = (JsonArray)renderer["classBreakInfos"]!;
        Assert.Equal(3, infos.Count);

        // The two interior breaks survive exactly.
        Assert.Equal(1000, infos[0]!["classMaxValue"]!.GetValue<double>());
        Assert.Equal(10000, infos[1]!["classMaxValue"]!.GetValue<double>());

        // Colours survive in order.
        Assert.Equal(LightestBreak, Rgba(infos[0]!["symbol"]!["color"]!));
        Assert.Equal(DarkestBreak, Rgba(infos[2]!["symbol"]!["color"]!));

        // And the top class is declared rather than quietly wrong.
        Assert.Contains(derived.Losses, l =>
            l.Contains("last class", StringComparison.Ordinal)
            && l.Contains("unbounded", StringComparison.Ordinal));
    }

    /// <summary>
    /// A marker renderer round-trips through pixels without drifting.
    /// </summary>
    /// <remarks>
    /// <b>The arithmetic is the risk here, not the shape.</b> An Esri marker size is
    /// a diameter in points and a MapLibre radius is half of it in pixels, so the
    /// trip is ×2/3 and back — and a point is 4/3 of a pixel, which is a repeating
    /// decimal. A 6-point marker that came back as 5.9 would be a conversion nobody
    /// could trust with a symbol set.
    /// </remarks>
    [Fact]
    public void A_marker_size_survives_the_trip_through_pixels()
    {
        const string drawingInfo = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSMS",
              "style": "esriSMSCircle",
              "color": [200, 100, 50, 255],
              "size": 6,
              "outline": {
                "type": "esriSLS", "style": "esriSLSSolid",
                "color": [255, 255, 255, 255], "width": 1
              }
            }
          }
        }
        """;

        JsonNode back = RoundTrip(drawingInfo, "wells", GeometryKind.Point);
        JsonNode symbol = back["renderer"]!["symbol"]!;

        Assert.Equal("esriSMS", symbol["type"]!.GetValue<string>());
        Assert.Equal(6, symbol["size"]!.GetValue<double>());
        Assert.Equal(1, symbol["outline"]!["width"]!.GetValue<double>());
    }

    /// <summary>
    /// A line's width and dash pattern survive the trip.
    /// </summary>
    [Fact]
    public void A_dashed_line_survives_the_trip()
    {
        const string drawingInfo = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSLS",
              "style": "esriSLSDash",
              "color": [30, 30, 30, 255],
              "width": 2
            }
          }
        }
        """;

        JsonNode back = RoundTrip(drawingInfo, "tracks", GeometryKind.LineString);
        JsonNode symbol = back["renderer"]!["symbol"]!;

        Assert.Equal("esriSLSDash", symbol["style"]!.GetValue<string>());
        Assert.Equal(2, symbol["width"]!.GetValue<double>());
    }

    /// <summary>
    /// No absolute URL is ever stored, wherever it was written.
    /// </summary>
    /// <remarks>
    /// <b>§7 condition 4, and the condition's own reason: "§5c is the kind of rule
    /// that decays the first time somebody stores what they were sent".</b> So this
    /// checks the three blocks §5c names <em>and</em> a paint property, because a
    /// rule enforced only where it was first written is a rule with a hole in it.
    /// </remarks>
    [Theory]
    [InlineData(
        """{"version":8,"sprite":"https://example.test/sprites/basic","layers":[{"id":"a","type":"fill","paint":{"fill-color":"#123456"}}]}""")]
    [InlineData(
        """{"version":8,"glyphs":"https://example.test/fonts/{fontstack}/{range}.pbf","layers":[{"id":"a","type":"fill","paint":{"fill-color":"#123456"}}]}""")]
    [InlineData(
        """{"version":8,"sources":{"s":{"type":"vector","tiles":["https://example.test/{z}/{x}/{y}.pbf"]}},"layers":[{"id":"a","type":"fill","paint":{"fill-color":"#123456"}}]}""")]
    public void The_generated_blocks_are_stripped_rather_than_stored(string style)
    {
        SymbologyWrite stored = SymbologyConversion.Read(style, GeometryKind.Polygon);

        Assert.DoesNotContain("example.test", stored.Canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", stored.Canonical, StringComparison.Ordinal);

        // And the reader is told, because a silently dropped block is a style that
        // renders differently from the one that was written.
        Assert.NotEmpty(stored.Losses);
    }

    /// <summary>
    /// An absolute URL in a paint property is refused, not stripped.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than dropped, because it is not a block this server
    /// regenerates.</b> A <c>fill-pattern</c> naming somebody's host is a style that
    /// only works from that host: dropping it silently changes the appearance, and
    /// keeping it stores a fact with an expiry date. Neither is acceptable, so the
    /// write fails and says which URL and why.
    /// </remarks>
    [Fact]
    public void An_absolute_url_in_a_paint_property_is_refused()
    {
        const string style = """
        {
          "version": 8,
          "layers": [{
            "id": "a", "type": "fill",
            "paint": { "fill-pattern": "https://example.test/hatch.png" }
          }]
        }
        """;

        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => SymbologyConversion.Read(style, GeometryKind.Polygon));

        Assert.Contains("absolute URL", refused.Message, StringComparison.Ordinal);
        Assert.Contains("example.test", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A style authored for another geometry is refused rather than stored.
    /// </summary>
    /// <remarks>
    /// A fill style on a point layer renders as nothing, and nothing looks exactly
    /// like a layer whose data failed to load. Refusing at the write is the only
    /// place the operator can be told which of the two it is.
    /// </remarks>
    [Fact]
    public void A_style_for_the_wrong_geometry_is_refused()
    {
        const string style = """
        {"version":8,"layers":[{"id":"a","type":"fill","paint":{"fill-color":"#123456"}}]}
        """;

        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => SymbologyConversion.Read(style, GeometryKind.Point));

        Assert.Contains("circle", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A renderer family outside the three is refused with the reason.
    /// </summary>
    [Fact]
    public void A_renderer_family_outside_the_three_is_refused()
    {
        const string drawingInfo = """
        {"renderer":{"type":"dotDensity","field1":"pop"}}
        """;

        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => SymbologyConversion.Read(drawingInfo, GeometryKind.Polygon));

        Assert.Contains("dotDensity", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-033 §5e", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Layer transparency is folded into the alpha channel rather than applied twice.
    /// </summary>
    /// <remarks>
    /// The 45%-becoming-20% fault, from the other direction. An Esri drawingInfo can
    /// express opacity in <c>transparency</c> and again in each symbol's alpha; a
    /// reader that carried both would multiply them.
    /// </remarks>
    [Fact]
    public void Layer_transparency_is_folded_into_the_colour_once()
    {
        const string drawingInfo = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSFS", "style": "esriSFSSolid", "color": [10, 20, 30, 255]
            }
          },
          "transparency": 50
        }
        """;

        SymbologyWrite stored = SymbologyConversion.Read(drawingInfo, GeometryKind.Polygon);

        Assert.Contains("0.5", stored.Canonical, StringComparison.Ordinal);

        DerivedDrawingInfo derived = SymbologyConversion.ToDrawingInfo(
            stored.Canonical, "x", GeometryKind.Polygon);

        // Half of 255, once. Not a quarter.
        Assert.Equal(
            HalfOpaque,
            Rgba(derived.DrawingInfo["renderer"]!["symbol"]!["color"]!));

        Assert.Equal(0, derived.DrawingInfo["transparency"]!.GetValue<int>());
    }

    /// <summary>
    /// A document too large to store is refused with the bound in the message.
    /// </summary>
    [Fact]
    public void A_document_past_the_bound_is_refused_with_the_number()
    {
        // Padded past the bound with a comment, which is a real thing a generator
        // does: the document is valid and simply enormous.
        string padding = new('x', SymbologyConversion.MaximumCharacters);

        string style =
            "{\"version\":8,\"layers\":[{\"id\":\"a\",\"type\":\"fill\","
            + "\"paint\":{\"fill-color\":\"#123456\"},\"metadata\":\"" + padding + "\"}]}";

        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => SymbologyConversion.Read(style, GeometryKind.Polygon));

        Assert.Contains("262,144", refused.Message, StringComparison.Ordinal);
    }

    private static JsonNode RoundTrip(string drawingInfo, string layer, GeometryKind geometry)
    {
        SymbologyWrite stored = SymbologyConversion.Read(drawingInfo, geometry);

        return SymbologyConversion
            .ToDrawingInfo(stored.Canonical, layer, geometry)
            .DrawingInfo;
    }

    private static int[] Rgba(JsonNode colour) =>
        ((JsonArray)colour).Select(c => (int)c!.GetValue<double>()).ToArray();
}
