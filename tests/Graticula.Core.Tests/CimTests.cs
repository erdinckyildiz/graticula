using System;
using System.Collections.Generic;
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
        CimProjection projection = Cim.Project(Renderer(SpecificationPolygonSymbol));

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
        CimProjection projection = Cim.Project(Renderer(SpecificationPolygonSymbol));

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
        DerivedStyle derived = CimStyle.ToMapLibre(Renderer(SpecificationPolygonSymbol), "roads");

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

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "roads");

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

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "places");

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

        CimProjection projection = Cim.Project(renderer);

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
        // <b>The example moved on 2026-09-04, and moving it is the point.</b> This test used
        // `CIMDotDensityRenderer`, which is now read (§3.15) — so it had to be given a renderer
        // that is still refused. A dictionary renderer is one of the two that are blocked rather
        // than unwritten: it needs a dictionary style this server does not hold.
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """{ "type": "CIMDictionaryRenderer", "dictionaryName": "mil2525d" }""")!;

        SymbologyException why = Assert.Throws<SymbologyException>(
            () => Cim.Project(renderer));

        Assert.Contains("CIMDictionaryRenderer", why.Message, StringComparison.Ordinal);
        Assert.Contains("CIMSimpleRenderer", why.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_style_derived_from_a_stack_reads_back_as_the_same_stack()
    {
        // <b>ADR-052 condition 5, and the case the decision was taken for.</b> A road as a wide
        // casing under a narrow fill has to survive being published as a style and read back,
        // because that is the trip a stored document takes whenever somebody edits one.
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMSimpleRenderer",
              "label": "road",
              "symbol": { "type": "CIMSymbolReference", "symbol": {
                "type": "CIMLineSymbol",
                "symbolLayers": [
                  { "type": "CIMSolidStroke", "width": 1.5,
                    "color": { "type": "CIMRGBColor", "values": [255, 255, 255, 100] } },
                  { "type": "CIMSolidStroke", "width": 3.75,
                    "color": { "type": "CIMRGBColor", "values": [40, 40, 40, 100] } }
                ] } }
            }
            """)!;

        DerivedStyle style = CimStyle.ToMapLibre(renderer, "roads");
        CimWrite back = CimStyle.FromMapLibre(style.Style, GeometryKind.LineString);

        CimProjection projection = Cim.Project(back.Renderer);
        IReadOnlyList<CimPaint> paints = projection.Classes.Single().Symbol.Paints;

        Assert.Equal(2, paints.Count);

        // <b>Bottom first, so the casing is still underneath.</b> A round trip that reversed
        // the stack once too often or once too few would put the 3.75pt black line on top and
        // hide the road entirely.
        CimStroke casing = Assert.IsType<CimStroke>(paints[0]);
        CimStroke road = Assert.IsType<CimStroke>(paints[1]);

        Assert.Equal(3.75, casing.Width);
        Assert.Equal(1.5, road.Width);
        Assert.Equal(new Rgba(40, 40, 40, 255), casing.Colour);
        Assert.Equal(new Rgba(255, 255, 255, 255), road.Colour);
    }

    [Fact]
    public void A_classified_style_reads_back_with_its_values_paired_to_their_symbols()
    {
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "roads-0",
                "type": "line",
                "source-layer": "roads",
                "paint": {
                  "line-color": ["match", ["get", "kind"],
                    "road", "#c80000", "track", "#0000c8", "#888888"],
                  "line-width": ["match", ["get", "kind"],
                    "track", 1, "road", 4, 2]
                }
              }]
            }
            """)!;

        CimWrite written = CimStyle.FromMapLibre(style, GeometryKind.LineString);
        CimProjection projection = Cim.Project(written.Renderer);

        Assert.Equal(Cim.UniqueValue, projection.Kind);
        Assert.Equal("kind", projection.Field);
        Assert.Equal(2, projection.Classes.Count);

        // <b>The width expression lists its classes in the other order, on purpose.</b> A
        // reader that took the nth output of each property would give the road the track's
        // width, and every assertion about colour alone would still pass.
        CimStroke road = Assert.IsType<CimStroke>(
            projection.Classes.Single(c => c.Values.Contains("road")).Symbol.Paints[0]);

        CimStroke track = Assert.IsType<CimStroke>(
            projection.Classes.Single(c => c.Values.Contains("track")).Symbol.Paints[0]);

        Assert.Equal(new Rgba(200, 0, 0, 255), road.Colour);
        Assert.Equal(new Rgba(0, 0, 200, 255), track.Colour);

        // <b>Pixels back to points.</b> 4px is 3pt, and 1px is 0.75pt.
        Assert.Equal(3, road.Width);
        Assert.Equal(0.75, track.Width);
    }

    [Fact]
    public void A_class_break_survives_the_trip_out_and_back_without_drifting()
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

        DerivedStyle style = CimStyle.ToMapLibre(renderer, "places");
        CimWrite back = CimStyle.FromMapLibre(style.Style, GeometryKind.Polygon);

        CimProjection projection = Cim.Project(back.Renderer);

        Assert.Equal(Cim.ClassBreaks, projection.Kind);
        Assert.Equal("pop", projection.Field);

        // <b>Exactly 100, not 100.00000000000001.</b> Going out, the stop is the next double
        // above the break so that MapLibre's lower-inclusive step means Esri's upper-inclusive
        // one; coming back has to undo precisely that. A round trip that drifted by one bit per
        // edit would move a boundary by a visible amount after enough of them, and every step
        // would look correct.
        Assert.Equal(100, projection.Classes[0].UpperBound);

        CimFill small = Assert.IsType<CimFill>(projection.Classes[0].Symbol.Paints[0]);
        CimFill large = Assert.IsType<CimFill>(projection.Classes[1].Symbol.Paints[0]);

        Assert.Equal(new Rgba(255, 255, 0, 255), small.Colour);
        Assert.Equal(new Rgba(255, 0, 0, 255), large.Colour);
    }

    [Fact]
    public void A_style_that_classifies_by_two_fields_is_refused_rather_than_half_read()
    {
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "roads-0", "type": "line", "source-layer": "roads",
                "paint": {
                  "line-color": ["match", ["get", "kind"], "road", "#c80000", "#888888"],
                  "line-width": ["match", ["get", "surface"], "paved", 4, 2]
                }
              }]
            }
            """)!;

        SymbologyException why = Assert.Throws<SymbologyException>(
            () => CimStyle.FromMapLibre(style, GeometryKind.LineString));

        Assert.Contains("kind", why.Message, StringComparison.Ordinal);
        Assert.Contains("surface", why.Message, StringComparison.Ordinal);
        Assert.Contains("will not choose", why.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matchs_otherwise_survives_as_the_renderers_default_symbol()
    {
        // <b>Found by a migration, not by this suite, which is why it is here now.</b> A
        // `match` ends in an otherwise: the value every feature no class lists is drawn with.
        // Reading a style into CIM and dropping it stored a renderer that draws nothing for
        // those features and a legend that has no row for them -- measured on 2026-09-03 as a
        // three-row legend becoming one row after `tools symbology-migrate --apply`.
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "buildings-0",
                "type": "fill",
                "source-layer": "buildings",
                "paint": {
                  "fill-color": ["match", ["get", "kind"],
                    "residential", "#e6783c", "commercial", "#3c78e6", "#cccccc"]
                }
              }]
            }
            """)!;

        CimWrite written = CimStyle.FromMapLibre(style, GeometryKind.Polygon);
        CimProjection projection = Cim.Project(written.Renderer);

        Assert.Equal(2, projection.Classes.Count);
        Assert.NotNull(projection.Default);

        CimFill other = Assert.IsType<CimFill>(projection.Default!.Paints[0]);

        Assert.Equal(new Rgba(0xcc, 0xcc, 0xcc, 255), other.Colour);

        // <b>And back out again with the default in the otherwise, not the first class.</b>
        // Emitting the first class's colour there would draw every unlisted feature as if it
        // were residential -- wrong, and deliberate-looking.
        DerivedStyle back = CimStyle.ToMapLibre(written.Renderer, "buildings");

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)back.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        Assert.Equal("#cccccc", (string?)colour[^1]);
    }

    [Fact]
    public void A_classified_colour_beside_a_constant_opacity_still_classifies()
    {
        // <b>The map, not the legend, and that is why this is here rather than in a WMS
        // test.</b> `SymbologyPlan` used to fold a static opacity into the colour by evaluating
        // the colour expression with no feature. For a literal that is the colour; for a
        // `match` it is the *fallback* -- so a layer classified by a column and given
        // `fill-opacity` drew every one of its features in the fallback colour, and the legend
        // agreed with the map because both had lost the same thing.
        //
        // It was unreachable while stored styles rarely carried an opacity. ADR-052's
        // derivation writes one for every layer, which is how a two-class layer turned grey.
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "buildings-0",
                "type": "fill",
                "source-layer": "buildings",
                "paint": {
                  "fill-color": ["match", ["get", "kind"],
                    "residential", "#e6783c", "commercial", "#3c78e6", "#cccccc"],
                  "fill-opacity": 1
                }
              }]
            }
            """)!;

        SymbologyPlan plan = SymbologyPlan.Compile(style.ToJsonString());

        StyleExpression.Classification? classes = plan.LegendClasses();

        Assert.NotNull(classes);
        Assert.Equal("kind", classes!.Value.Field);

        // Two named classes and the *Other* every match carries.
        Assert.Equal(3, classes.Value.Cases.Count);

        // <b>And the field is still read per feature.</b> A plan that folded the colour would
        // ask the source for no columns at all, which is the same defect measured from the
        // other end.
        Assert.Contains("kind", plan.Fields);
    }

    [Fact]
    public void The_same_holds_for_a_style_derived_from_a_stored_CIM_renderer()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMUniqueValueRenderer",
              "fields": ["kind"],
              "useDefaultSymbol": true,
              "defaultLabel": "Other",
              "defaultSymbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [204, 204, 204, 100] } }] } },
              "groups": [{ "classes": [
                { "label": "Residential",
                  "values": [{ "fieldValues": ["residential"] }],
                  "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                    { "type": "CIMSolidFill",
                      "color": { "type": "CIMRGBColor", "values": [230, 120, 60, 100] } }] } } },
                { "label": "Commercial",
                  "values": [{ "fieldValues": ["commercial"] }],
                  "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                    { "type": "CIMSolidFill",
                      "color": { "type": "CIMRGBColor", "values": [60, 120, 230, 100] } }] } } }
              ] }]
            }
            """)!;

        // <b>Compiled from the stored document, the way a request does it.</b> Asserting on
        // the derived style alone would miss the fold, which happens one step later.
        SymbologyPlan plan = SymbologyPlan.Compile(renderer.ToJsonString());

        StyleExpression.Classification? classes = plan.LegendClasses();

        Assert.NotNull(classes);
        Assert.Equal(3, classes!.Value.Cases.Count);
        Assert.Contains("kind", plan.Fields);
    }

    [Fact]
    public void A_document_stored_before_the_reversal_still_answers_every_face()
    {
        // <b>ADR-052 condition 3.</b> Deployments carry MapLibre documents written under
        // ADR-033, and the promise is that they keep working without anybody running anything.
        // Each face is asked separately, because a tolerance that covered two of the three
        // would leave one screen empty and no error anywhere.
        const string stored =
            """
            {
              "version": 8,
              "layers": [{
                "id": "parcels",
                "type": "fill",
                "source-layer": "parcels",
                "paint": { "fill-color": "#ccbb44", "fill-outline-color": "#443311" }
              }]
            }
            """;

        // The renderer.
        SymbologyPlan plan = SymbologyPlan.Compile(stored);

        Assert.NotEmpty(plan.Layers);

        // The Esri face.
        DerivedDrawingInfo drawing = SymbologyConversion.ToDrawingInfo(
            stored, "parcels", GeometryKind.Polygon);

        Assert.Equal(
            "esriSFS", (string?)drawing.DrawingInfo["renderer"]!["symbol"]!["type"]);

        // The tile style face.
        DerivedStyle style = SymbologyConversion.ToStyle(stored, "parcels", GeometryKind.Polygon);

        Assert.NotEmpty((JsonArray)style.Style["layers"]!);

        // <b>And it says so, on both faces.</b> A tolerance that worked silently would leave a
        // deployment on the old shape for ever, paying the conversion on every request with
        // nothing to tell anybody it was happening.
        Assert.Contains(
            drawing.Losses,
            l => l.Contains("before the canonical vocabulary became CIM", StringComparison.Ordinal));

        Assert.Contains(
            style.Losses,
            l => l.Contains("run the migration", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dash_pattern_is_data_and_compiling_it_does_not_throw()
    {
        // <b>`line-dasharray` is `[6, 3]`: two lengths, no operator.</b> Every other array in a
        // paint value is an expression whose head names one, so the compiler read the `6` as an
        // operator name and threw *An element of type 'Number' cannot be converted to a
        // 'System.String'* — which reached the caller as a 500 with the reason only in the log.
        // The symbol library ships two dashed presets, so this was one click away.
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "yollar-0", "type": "line", "source-layer": "yollar",
                "paint": {
                  "line-color": "#783c8c",
                  "line-width": 1.6,
                  "line-dasharray": [6, 3]
                }
              }]
            }
            """)!;

        SymbologyPlan plan = SymbologyPlan.Compile(style.ToJsonString());

        Assert.NotEmpty(plan.Layers);

        // <b>And it is still a dash when it comes out.</b> Swallowing the array and drawing a
        // solid line would pass an assertion that only asked whether compiling threw.
        PlanLayer.Line line = Assert.IsType<PlanLayer.Line>(plan.Layers[0]);

        MapSymbol.Stroke stroke = Assert.IsType<MapSymbol.Stroke>(
            line.Resolve(new StyleExpression.Context(
                new Dictionary<string, object?>(StringComparer.Ordinal), 0)));

        Assert.NotNull(stroke.Dash);
        Assert.Equal(2, stroke.Dash!.Count);
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
