using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The two directions between CIM and Esri's REST <c>drawingInfo</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 conditions 1 and 5.</b> Condition 1 is the alpha rescale: `drawingInfo` writes
/// 0–255 and CIM writes 0–100, so every colour crosses a scale twice and getting it backwards
/// makes every layer either opaque or invisible. Condition 5 is the round trip through the
/// richest shape the reader supports.
/// </para>
/// <para>
/// <b>The round trip is asserted on the way out, not on the way in.</b> Comparing the two CIM
/// documents would compare this code with itself; comparing the `drawingInfo` that goes in with
/// the one that comes out compares it with the vocabulary somebody actually pasted.
/// </para>
/// </remarks>
public sealed class CimEsriTests
{
    [Theory]
    [InlineData(255)]
    [InlineData(128)]
    [InlineData(64)]
    [InlineData(1)]
    [InlineData(0)]
    public void A_colours_alpha_survives_the_journey_through_CIMs_nought_to_a_hundred(byte alpha)
    {
        JsonObject drawingInfo = Simple(
            System.String.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"type\":\"esriSFS\",\"style\":\"esriSFSSolid\","
                + $"\"color\":[12,34,56,{alpha}],"
                + $"\"outline\":{{\"type\":\"esriSLS\",\"style\":\"esriSLSSolid\","
                + $"\"color\":[7,8,9,{alpha}],\"width\":1.5}}}}"));

        CimWrite stored = CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Polygon);

        DerivedDrawingInfo back = CimEsri.ToDrawingInfo(stored.Renderer, "parcels");

        JsonObject symbol = (JsonObject)back.DrawingInfo["renderer"]!["symbol"]!;

        // <b>Within one, because 0-100 cannot address all 256 values.</b> A larger tolerance
        // would let a scale that is wrong by a factor pass; a smaller one would fail on
        // arithmetic that is correct.
        Assert.InRange((int)((JsonArray)symbol["color"]!)[3]!, alpha - 1, alpha + 1);

        Assert.InRange(
            (int)((JsonArray)symbol["outline"]!["color"]!)[3]!, alpha - 1, alpha + 1);

        // <b>And the colour itself did not move at all.</b> Red, green and blue are 0-255 in
        // both vocabularies, so anything but an exact match here is a rescale applied to the
        // wrong three channels.
        Assert.Equal(12, (int)((JsonArray)symbol["color"]!)[0]!);
        Assert.Equal(34, (int)((JsonArray)symbol["color"]!)[1]!);
        Assert.Equal(56, (int)((JsonArray)symbol["color"]!)[2]!);
    }

    [Fact]
    public void A_fill_with_an_outline_comes_back_as_a_fill_with_an_outline()
    {
        JsonObject drawingInfo = Simple(
            """
            {"type":"esriSFS","style":"esriSFSSolid","color":[204,187,68,255],
             "outline":{"type":"esriSLS","style":"esriSLSDash","color":[68,51,17,255],
             "width":2}}
            """);

        CimWrite stored = CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Polygon);

        Assert.Empty(stored.Losses);

        // <b>The outline is listed first in `symbolLayers`.</b> CIM draws index zero on top and
        // an outline is over its fill, so any other order buries every outline ever authored.
        JsonArray layers = (JsonArray)stored.Renderer["symbol"]!["symbol"]!["symbolLayers"]!;

        Assert.Equal("CIMSolidStroke", (string?)layers[0]!["type"]);
        Assert.Equal("CIMSolidFill", (string?)layers[1]!["type"]);

        DerivedDrawingInfo back = CimEsri.ToDrawingInfo(stored.Renderer, "parcels");

        JsonObject symbol = (JsonObject)back.DrawingInfo["renderer"]!["symbol"]!;

        Assert.Equal("esriSFS", (string?)symbol["type"]);
        Assert.Equal("esriSLS", (string?)symbol["outline"]!["type"]);
        Assert.Equal("esriSLSDash", (string?)symbol["outline"]!["style"]);
        Assert.Equal(2.0, (double?)symbol["outline"]!["width"]);
        Assert.Empty(back.Losses);
    }

    [Fact]
    public void A_classified_renderer_keeps_its_field_its_values_and_its_symbols()
    {
        JsonObject drawingInfo = new()
        {
            ["renderer"] = JsonNode.Parse(
                """
                {
                  "type": "uniqueValue",
                  "field1": "kind",
                  "uniqueValueInfos": [
                    { "value": "road", "label": "Road", "symbol":
                      {"type":"esriSLS","style":"esriSLSSolid","color":[200,0,0,255],"width":3} },
                    { "value": "track", "label": "Track", "symbol":
                      {"type":"esriSLS","style":"esriSLSSolid","color":[0,0,200,255],"width":1} }
                  ]
                }
                """),
        };

        CimWrite stored = CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.LineString);

        DerivedDrawingInfo back = CimEsri.ToDrawingInfo(stored.Renderer, "roads");

        JsonObject renderer = (JsonObject)back.DrawingInfo["renderer"]!;

        Assert.Equal("uniqueValue", (string?)renderer["type"]);
        Assert.Equal("kind", (string?)renderer["field1"]);

        JsonArray infos = (JsonArray)renderer["uniqueValueInfos"]!;

        Assert.Equal(2, infos.Count);
        Assert.Equal("road", (string?)infos[0]!["value"]);
        Assert.Equal("Road", (string?)infos[0]!["label"]);
        Assert.Equal(3.0, (double?)infos[0]!["width"] ?? (double?)infos[0]!["symbol"]!["width"]);
        Assert.Equal("track", (string?)infos[1]!["value"]);
        Assert.Equal(1.0, (double?)infos[1]!["symbol"]!["width"]);
    }

    [Fact]
    public void A_stack_deeper_than_an_Esri_symbol_is_flattened_and_the_flattening_is_named()
    {
        // <b>The case ADR-052 was decided for.</b> A road as a wide casing under a narrow fill
        // is two strokes. Under ADR-033 this was flattened at the moment of storage and could
        // not be recovered; now the store keeps both and only this face loses one.
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMSimpleRenderer",
              "label": "road",
              "symbol": { "type": "CIMSymbolReference", "symbol": {
                "type": "CIMLineSymbol",
                "symbolLayers": [
                  { "type": "CIMSolidStroke", "width": 2,
                    "color": { "type": "CIMRGBColor", "values": [255, 255, 255, 100] } },
                  { "type": "CIMSolidStroke", "width": 5,
                    "color": { "type": "CIMRGBColor", "values": [0, 0, 0, 100] } }
                ] } }
            }
            """)!;

        DerivedDrawingInfo derived = CimEsri.ToDrawingInfo(renderer, "roads");

        JsonObject symbol = (JsonObject)derived.DrawingInfo["renderer"]!["symbol"]!;

        // <b>The narrow white one, because it is on top.</b> `symbolLayers[0]` is drawn over
        // the rest, and it is what somebody looking at the map sees.
        Assert.Equal("esriSLS", (string?)symbol["type"]);
        Assert.Equal(2.0, (double?)symbol["width"]);
        Assert.Equal(255, (int)((JsonArray)symbol["color"]!)[0]!);

        string said = Assert.Single(derived.Losses);

        Assert.Contains("2 layers", said, StringComparison.Ordinal);
        Assert.Contains("kept in the stored document", said, StringComparison.Ordinal);

        // <b>And the map draws both.</b> The whole claim is that only this one face loses the
        // casing, so the style derivation has to still have it.
        DerivedStyle style = CimStyle.ToMapLibre(renderer, "roads");

        Assert.Equal(2, ((JsonArray)style.Style["layers"]!).Count);
    }

    [Fact]
    public void A_picture_symbol_is_refused_because_there_is_no_sprite_library()
    {
        JsonObject drawingInfo = Simple(
            """{"type":"esriPMS","url":"pin.png","width":12,"height":12}""");

        SymbologyException why = Assert.Throws<SymbologyException>(
            () => CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Point));

        Assert.Contains("esriPMS", why.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-027", why.Message, StringComparison.Ordinal);
    }

    /// <summary>Wraps one Esri symbol in the simplest renderer.</summary>
    /// <param name="symbol">The symbol's JSON.</param>
    /// <returns>The <c>drawingInfo</c>.</returns>
    private static JsonObject Simple(string symbol) =>
        new()
        {
            ["renderer"] = new JsonObject
            {
                ["type"] = "simple",
                ["label"] = "all",
                ["symbol"] = JsonNode.Parse(symbol),
            },
        };
}
