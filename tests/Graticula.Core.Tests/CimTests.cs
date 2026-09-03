using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The CIM reader and the MapLibre derivation ADR-052 rests on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The worked example is the specification's own.</b> `docs/v3/Example-Symbols.md` at
/// `github.com/Esri/cim-spec` carries a `CIMPolygonSymbol` whose symbol layers are a
/// `CIMSolidStroke` then a `CIMSolidFill`, with colours `[110, 110, 110, 100]` and
/// `[0, 122, 194, 100]`. Using it rather than one invented here means the test fails if the
/// reading of the specification was wrong, which is the thing most likely to be wrong.
/// </para>
/// <para>
/// <b>Two properties are asserted that no amount of care makes obvious.</b> CIM draws
/// `symbolLayers[0]` on **top** and MapLibre draws the first layer at the **bottom**, so the
/// stack is reversed on the way in — get that backwards and a road's casing covers the road. And
/// CIM's alpha is 0–100 where every other vocabulary here uses 0–255, so `100` means opaque and
/// copying it means 39% opaque, which reads as a rendering bug rather than a units mistake.
/// </para>
/// </remarks>
public sealed class CimTests
{
    /// <summary>The specification's own pin symbol, stroke first.</summary>
    private const string SpecificationPolygonSymbol =
        """
        {
          "type": "CIMPolygonSymbol",
          "symbolLayers": [{
              "type": "CIMSolidStroke",
              "enable": true,
              "capStyle": "Round",
              "joinStyle": "Round",
              "miterLimit": 10,
              "width": 0.75,
              "color": { "type": "CIMRGBColor", "values": [110, 110, 110, 100] }
            }, {
              "type": "CIMSolidFill",
              "enable": true,
              "color": { "type": "CIMRGBColor", "values": [0, 122, 194, 100] }
            }
          ]
        }
        """;

    [Fact]
    public void A_symbols_layers_are_reversed_so_the_first_one_authored_is_drawn_on_top()
    {
        CimProjection projection = Cim.Project(
            Renderer(SpecificationPolygonSymbol), GeometryKind.Polygon);

        CimSymbol symbol = projection.Classes.Single().Symbol;

        Assert.Equal(2, symbol.Paints.Count);

        // <b>The fill is at the bottom.</b> The specification's symbol lists the stroke first,
        // and CIM draws the first one on top — so after the reversal the fill comes first, which
        // is what a renderer that paints in order needs.
        CimFill fill = Assert.IsType<CimFill>(symbol.Paints[0]);
        CimStroke stroke = Assert.IsType<CimStroke>(symbol.Paints[1]);

        Assert.Equal(new Rgba(0, 122, 194, 255), fill.Colour);
        Assert.Equal(new Rgba(110, 110, 110, 255), stroke.Colour);
        Assert.Equal(0.75, stroke.Width);
    }

    [Fact]
    public void An_opaque_colour_is_a_hundred_in_CIM_and_two_hundred_and_fifty_five_here()
    {
        CimProjection projection = Cim.Project(
            Renderer(SpecificationPolygonSymbol), GeometryKind.Polygon);

        CimFill fill = Assert.IsType<CimFill>(projection.Classes.Single().Symbol.Paints[0]);

        Assert.Equal(
            255,
            fill.Colour.A);

        // <b>Both directions, because only the round trip catches a scale used one way.</b>
        Assert.Equal(255, Cim.Opacity(100));
        Assert.Equal(0, Cim.Opacity(0));
        Assert.Equal(128, Cim.Opacity(50));
        Assert.Equal(100.0, Cim.Percent(255));
        Assert.Equal(0.0, Cim.Percent(0));

        // <b>A half-transparent colour survives the round trip to within a unit.</b> ADR-052
        // condition 1 asks for exactly this, and the tolerance is one because 0-100 cannot
        // address all 256 values.
        for (int alpha = 0; alpha <= 255; alpha++)
        {
            byte back = Cim.Opacity(Cim.Percent((byte)alpha));

            Assert.True(
                Math.Abs(back - alpha) <= 1,
                $"Alpha {alpha} came back as {back} after a round trip through CIM's 0-100. "
                + "Every colour in every stored document goes through this twice.");
        }
    }

    [Fact]
    public void A_two_layer_symbol_becomes_two_style_layers_in_painting_order()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            Renderer(SpecificationPolygonSymbol), "roads", GeometryKind.Polygon);

        JsonArray layers = (JsonArray)derived.Style["layers"]!;

        Assert.Equal(2, layers.Count);

        // <b>This is the structural gain ADR-052 was decided for.</b> A stack of symbol layers
        // becomes a stack of style layers, which SymbologyPlan already compiles — so a casing
        // under a line draws without a new renderer.
        Assert.Equal("fill", (string?)layers[0]!["type"]);
        Assert.Equal("line", (string?)layers[1]!["type"]);

        Assert.Equal("#007ac2", (string?)layers[0]!["paint"]!["fill-color"]);
        Assert.Equal("#6e6e6e", (string?)layers[1]!["paint"]!["line-color"]);

        // <b>Points to pixels.</b> CIM measures a stroke in points and a style in pixels, and
        // 0.75pt is 1px at the 96-per-inch a style assumes.
        Assert.Equal(1.0, (double?)layers[1]!["paint"]!["line-width"]);

        Assert.Equal("roads", (string?)layers[0]!["source-layer"]);
        Assert.Empty(derived.Losses);
    }

    [Fact]
    public void A_classified_renderer_becomes_a_match_over_the_field_it_classifies_by()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMUniqueValueRenderer",
              "fields": ["kind"],
              "groups": [{
                "classes": [
                  {
                    "label": "Road",
                    "values": [{ "type": "CIMUniqueValue", "fieldValues": ["road"] }],
                    "symbol": { "symbol": { "type": "CIMLineSymbol", "symbolLayers": [
                      { "type": "CIMSolidStroke", "width": 1.5,
                        "color": { "type": "CIMRGBColor", "values": [200, 0, 0, 100] } }] } }
                  },
                  {
                    "label": "Track",
                    "values": [{ "type": "CIMUniqueValue", "fieldValues": ["track"] }],
                    "symbol": { "symbol": { "type": "CIMLineSymbol", "symbolLayers": [
                      { "type": "CIMSolidStroke", "width": 0.75,
                        "color": { "type": "CIMRGBColor", "values": [0, 0, 200, 100] } }] } }
                  }
                ]
              }]
            }
            """)!;

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "roads", GeometryKind.LineString);

        JsonArray layers = (JsonArray)derived.Style["layers"]!;
        JsonObject paint = (JsonObject)layers.Single()!["paint"]!;

        JsonArray colour = Assert.IsType<JsonArray>(paint["line-color"]);

        Assert.Equal("match", (string?)colour[0]);
        Assert.Equal("kind", (string?)((JsonArray)colour[1]!)[1]);
        Assert.Equal("road", (string?)colour[2]);
        Assert.Equal("#c80000", (string?)colour[3]);
        Assert.Equal("track", (string?)colour[4]);
        Assert.Equal("#0000c8", (string?)colour[5]);

        // <b>The width classifies too.</b> A converter that carried the colour and flattened
        // every other property would pass an assertion about the colour alone, and half of what
        // distinguishes a track from a road is that it is thinner.
        JsonArray width = Assert.IsType<JsonArray>(paint["line-width"]);

        Assert.Equal(2.0, (double?)width[3]);
        Assert.Equal(1.0, (double?)width[5]);
    }

    [Fact]
    public void Class_breaks_step_where_the_break_is_so_no_value_changes_class()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMClassBreaksRenderer",
              "field": "pop",
              "breaks": [
                { "upperBound": 100, "label": "small", "symbol": { "symbol": {
                  "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [255, 255, 0, 100] } }] } } },
                { "upperBound": 500, "label": "large", "symbol": { "symbol": {
                  "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [255, 0, 0, 100] } }] } } }
              ]
            }
            """)!;

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "places", GeometryKind.Polygon);

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)derived.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        Assert.Equal("step", (string?)colour[0]);
        Assert.Equal("#ffff00", (string?)colour[2]);

        // <b>The stop is the next representable double above the break, and that is exact.</b>
        // Esri's break is upper-bound inclusive and `step` is lower-bound inclusive, so a
        // population of exactly 100 belongs to *small* and would land in *large* if the stop
        // were 100. Stepping at `BitIncrement(100)` makes `v < stop` mean `v <= 100` for every
        // double there is.
        double stop = (double)colour[3]!;

        Assert.True(stop > 100, $"The step is at {stop}, so a value of exactly 100 changes class.");
        Assert.Equal(Math.BitIncrement(100), stop);
        Assert.Equal("#ff0000", (string?)colour[4]);
    }

    [Fact]
    public void What_the_renderer_cannot_draw_is_reported_rather_than_dropped()
    {
        JsonObject renderer = Renderer(
            """
            {
              "type": "CIMPolygonSymbol",
              "symbolLayers": [
                { "type": "CIMHatchFill", "separation": 4 },
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [0, 122, 194, 100] } }
              ]
            }
            """);

        CimProjection projection = Cim.Project(renderer, GeometryKind.Polygon);

        // <b>ADR-052 condition 4.</b> The whole argument for a canonical model richer than the
        // renderer is that it keeps what it was given; that is only worth anything if somebody
        // is told which parts of it they are not getting.
        string said = Assert.Single(projection.NotDrawn);

        Assert.Contains("CIMHatchFill", said, StringComparison.Ordinal);
        Assert.Contains("kept in the stored document", said, StringComparison.Ordinal);

        // <b>And the symbol still draws.</b> An unknown layer must not take the rest with it.
        Assert.Single(projection.Classes.Single().Symbol.Paints);
    }

    [Fact]
    public void A_renderer_this_server_cannot_read_is_refused_with_what_it_does_read()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """{ "type": "CIMDotDensityRenderer", "fields": ["a"] }""")!;

        SymbologyException why = Assert.Throws<SymbologyException>(
            () => Cim.Project(renderer, GeometryKind.Polygon));

        Assert.Contains("CIMDotDensityRenderer", why.Message, StringComparison.Ordinal);
        Assert.Contains("CIMSimpleRenderer", why.Message, StringComparison.Ordinal);
    }

    /// <summary>Wraps a symbol in the simplest renderer that carries one.</summary>
    /// <param name="symbol">The symbol's JSON.</param>
    /// <returns>The renderer.</returns>
    private static JsonObject Renderer(string symbol) =>
        new()
        {
            ["type"] = Cim.Simple,
            ["label"] = "all",
            ["symbol"] = new JsonObject
            {
                ["type"] = "CIMSymbolReference",
                ["symbol"] = JsonNode.Parse(symbol),
            },
        };
}
