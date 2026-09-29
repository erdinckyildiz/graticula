using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The published tile style: one style layer per class, with a legacy filter and constant paint.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-280](../../docs/architecture-debt.md), measured by the owner on 2026-09-29 in ArcGIS Pro
/// 3.x.</b> A generated style whose `line-color` was a `match` over the province field drew
/// nothing in Pro; the same service with a constant colour drew. Esri's own vector styles write
/// one style layer per class with `["==", field, value]`, and that is what the tile face
/// publishes now.
/// </para>
/// <para>
/// <b>Equivalence is checked by evaluating both forms, not by reading them.</b> The legacy
/// filter semantics below are MapLibre's own (strictly typed `==`, `in` and comparisons; `!in`
/// true for a missing attribute), and every published layer's constant is compared with what
/// <see cref="StyleExpression"/> — the evaluator this server draws with — answers for the same
/// feature against the expression form. A shape assertion alone would pass a style that put the
/// right filters on the wrong colours.
/// </para>
/// </remarks>
public sealed class PerClassStyleTests
{
    // ------------------------------------------------------------------ fixtures

    private static string Colour(int r, int g, int b) =>
        $$"""{ "type": "CIMRGBColor", "values": [{{r}}, {{g}}, {{b}}, 100] }""";

    /// <summary>A polygon symbol: a fill, and a casing when asked.</summary>
    private static string Fill((int R, int G, int B) fill, bool casing = false) =>
        $$"""
        { "type": "CIMSymbolReference", "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
          {{(casing ? $$"""{ "type": "CIMSolidStroke", "width": 1.5, "color": {{Colour(40, 40, 40)}} },""" : "")}}
          { "type": "CIMSolidFill", "color": {{Colour(fill.R, fill.G, fill.B)}} } ] } }
        """;

    /// <summary>A unique-value renderer over one or more fields.</summary>
    private static JsonObject Unique(
        string[] fields,
        (string[][] Tuples, (int, int, int) Fill)[] classes,
        (int, int, int)? otherwise,
        bool casing = false)
    {
        string body = string.Join(",", classes.Select(c =>
            $$"""
            { "label": "x", "visible": true,
              "values": [{{string.Join(",", c.Tuples.Select(t =>
                  $$"""{ "type": "CIMUniqueValue", "fieldValues": [{{string.Join(",", t.Select(v => $"\"{v}\""))}}] }"""))}}],
              "symbol": {{Fill(c.Fill, casing)}} }
            """));

        string fallback = otherwise is { } o
            ? $$""", "useDefaultSymbol": true, "defaultSymbol": {{Fill(o, casing)}}"""
            : string.Empty;

        return (JsonObject)JsonNode.Parse(
            $$"""
            { "type": "CIMUniqueValueRenderer",
              "fields": [{{string.Join(",", fields.Select(f => $"\"{f}\""))}}],
              "groups": [{ "classes": [{{body}}] }]{{fallback}} }
            """)!;
    }

    /// <summary>Three provinces and a grey for the rest: the showcase's `tr_il`, in miniature.</summary>
    private static JsonObject Provinces(bool otherwise = true, bool casing = false) =>
        Unique(
            ["il"],
            [
                ([["Ankara"]], (68, 119, 170)),
                ([["İzmir"]], (238, 102, 119)),
                ([["Bursa"]], (34, 136, 51)),
            ],
            otherwise ? (200, 200, 200) : null,
            casing);

    /// <summary>Population in three classes, floored at a thousand, with a default below it.</summary>
    private static JsonObject Population(bool floored) =>
        (JsonObject)JsonNode.Parse(
            $$"""
            { "type": "CIMClassBreaksRenderer", "field": "nufus",
              {{(floored ? "\"minimumBreak\": 1000," : "")}}
              "useDefaultSymbol": true, "defaultSymbol": {{Fill((200, 200, 200))}},
              "breaks": [
                { "upperBound": 5000, "label": "kasaba", "symbol": {{Fill((255, 255, 0))}} },
                { "upperBound": 20000, "label": "sehir", "symbol": {{Fill((255, 128, 0))}} },
                { "upperBound": 1000000, "label": "buyuk", "symbol": {{Fill((255, 0, 0))}} } ] }
            """)!;

    /// <summary>A fill faded from cream to brown across a population.</summary>
    private static JsonObject Fading() =>
        (JsonObject)JsonNode.Parse(
            $$"""
            { "type": "CIMSimpleRenderer", "symbol": {{Fill((200, 200, 200))}},
              "visualVariables": [{ "type": "CIMColorVisualVariable",
                "expression": "$feature.nufus", "minValue": 0, "maxValue": 2000000,
                "colorRamp": { "type": "CIMLinearContinuousColorRamp",
                  "fromColor": {{Colour(255, 245, 235)}}, "toColor": {{Colour(140, 45, 4)}} } }] }
            """)!;

    /// <summary>Proportional dots, which project as a simple renderer with twelve size stops.</summary>
    private static JsonObject Dots() =>
        (JsonObject)JsonNode.Parse(
            $$"""
            { "type": "CIMProportionalRenderer", "field": "nufus",
              "minDataValue": 100, "maxDataValue": 10000,
              "minSymbol": { "symbol": { "type": "CIMPointSymbol", "symbolLayers": [
                { "type": "CIMVectorMarker", "size": 6,
                  "markerGraphics": [ { "type": "CIMMarkerGraphic", "symbol": {
                    "type": "CIMPolygonSymbol", "symbolLayers": [
                      { "type": "CIMSolidFill", "color": {{Colour(0, 122, 194)}} }] } } ] }] } } }
            """)!;

    private static JsonArray Layers(DerivedStyle style) => (JsonArray)style.Style["layers"]!;

    private static JsonObject Paint(JsonNode? layer) => (JsonObject)layer!["paint"]!;

    // ------------------------------------------------------------------ unique values

    [Fact]
    public void A_unique_value_renderer_is_one_filtered_layer_per_class_and_one_for_the_rest()
    {
        DerivedStyle style = CimStyle.ToMapLibre(Provinces(), "tr_il");
        JsonArray layers = Layers(style);

        Assert.Equal(4, layers.Count);

        // <b>The default at the bottom, then the classes last first</b>, so the first class is
        // drawn on top — MapLibre draws later layers over earlier ones.
        Assert.Equal("tr_il-0-other", (string?)layers[0]!["id"]);
        Assert.Equal("tr_il-0-2", (string?)layers[1]!["id"]);
        Assert.Equal("tr_il-0-1", (string?)layers[2]!["id"]);
        Assert.Equal("tr_il-0-0", (string?)layers[3]!["id"]);

        Assert.Equal(
            """["!in","il","Ankara","İzmir","Bursa"]""",
            layers[0]!["filter"]!.ToJsonString(Json.Plain));
        Assert.Equal("""["==","il","Ankara"]""", layers[3]!["filter"]!.ToJsonString(Json.Plain));

        Assert.Equal("#c8c8c8", (string?)Paint(layers[0])["fill-color"]);
        Assert.Equal("#4477aa", (string?)Paint(layers[3])["fill-color"]);
        Assert.Equal("#228833", (string?)Paint(layers[1])["fill-color"]);

        foreach (JsonNode? layer in layers)
        {
            Assert.Equal("tr_il", (string?)layer!["source-layer"]);
            Assert.Equal("fill", (string?)layer["type"]);
        }
    }

    [Fact]
    public void A_value_that_reads_as_a_number_is_matched_as_text_and_as_a_number()
    {
        // <b>CIM keeps "7" and the tile keeps 7</b>, and a legacy `==` is strict about the type.
        JsonObject renderer = Unique(
            ["kod"], [([["7"]], (255, 0, 0)), ([["A1"]], (0, 0, 255))], (200, 200, 200));

        JsonArray layers = Layers(CimStyle.ToMapLibre(renderer, "yol"));

        JsonObject seven = layers.OfType<JsonObject>().Single(l => (string?)l["id"] == "yol-0-0");
        JsonObject a1 = layers.OfType<JsonObject>().Single(l => (string?)l["id"] == "yol-0-1");

        Assert.Equal("""["in","kod","7",7]""", seven["filter"]!.ToJsonString(Json.Plain));
        Assert.Equal("""["==","kod","A1"]""", a1["filter"]!.ToJsonString(Json.Plain));

        Assert.True(Filter.Matches(seven["filter"], new() { ["kod"] = 7.0 }));
        Assert.True(Filter.Matches(seven["filter"], new() { ["kod"] = "7" }));
        Assert.False(Filter.Matches(layers[0]!["filter"], new() { ["kod"] = 7.0 }));
    }

    [Fact]
    public void Without_a_default_symbol_every_symbol_layer_is_filtered_to_the_listed_values()
    {
        DerivedStyle style = CimStyle.ToMapLibre(Provinces(otherwise: false, casing: true), "tr_il");
        JsonArray layers = Layers(style);

        // Two symbol layers, three classes each, no default: the outline is spread too, or an
        // unlisted province would keep its outline and lose its fill.
        Assert.Equal(6, layers.Count);
        Assert.All(layers, l => Assert.NotNull(l!["filter"]));
        Assert.DoesNotContain(layers, l => ((string?)l!["id"])!.EndsWith("-other", StringComparison.Ordinal));

        // The fill's three first, then the outline's drawn over them: the stack is unchanged.
        Assert.Equal("fill,fill,fill,line,line,line", string.Join(",", layers.Select(l => (string)l!["type"]!)));

        Assert.Contains(style.Losses, l => l.Contains("no default symbol", StringComparison.Ordinal));
    }

    [Fact]
    public void A_renderer_of_two_fields_tests_each_field_on_its_own()
    {
        JsonObject renderer = Unique(
            ["kullanim", "ilce"],
            [
                ([["tarim", "Merkez"]], (120, 180, 90)),
                ([["tarim", "Kuzey"], ["konut", "Kuzey"]], (200, 80, 60)),
            ],
            (200, 200, 200));

        JsonArray layers = Layers(CimStyle.ToMapLibre(renderer, "parsel"));

        Assert.Equal(
            """["none",["all",["==","kullanim","tarim"],["==","ilce","Merkez"]],["all",["==","kullanim","tarim"],["==","ilce","Kuzey"]],["all",["==","kullanim","konut"],["==","ilce","Kuzey"]]]""",
            layers[0]!["filter"]!.ToJsonString(Json.Plain));

        Assert.Equal(
            """["any",["all",["==","kullanim","tarim"],["==","ilce","Kuzey"]],["all",["==","kullanim","konut"],["==","ilce","Kuzey"]]]""",
            layers[1]!["filter"]!.ToJsonString(Json.Plain));

        Assert.Equal(
            """["all",["==","kullanim","tarim"],["==","ilce","Merkez"]]""",
            layers[2]!["filter"]!.ToJsonString(Json.Plain));
    }

    // ------------------------------------------------------------------ class breaks

    [Fact]
    public void Class_breaks_filter_on_the_ranges_Esri_means_upper_bound_included()
    {
        JsonArray layers = Layers(CimStyle.ToMapLibre(Population(floored: true), "iller"));

        Assert.Equal(
            [
                """["<","nufus",1000]""",
                """[">","nufus",20000]""",
                """["all",[">","nufus",5000],["<=","nufus",20000]]""",
                """["all",[">=","nufus",1000],["<=","nufus",5000]]""",
            ],
            layers.Select(l => l!["filter"]!.ToJsonString(Json.Plain)));

        // <b>Exactly on a break is the class below</b>, and exactly on the floor is inside.
        Assert.Equal("#ffff00", Drawn(layers, new() { ["nufus"] = 5000.0 }));
        Assert.Equal("#ff8000", Drawn(layers, new() { ["nufus"] = 5000.5 }));
        Assert.Equal("#ffff00", Drawn(layers, new() { ["nufus"] = 1000.0 }));
        Assert.Equal("#c8c8c8", Drawn(layers, new() { ["nufus"] = 999.0 }));

        // <b>Above the last break is still the last class</b>, as the `step` drew it.
        Assert.Equal("#ff0000", Drawn(layers, new() { ["nufus"] = 5e6 }));
    }

    [Fact]
    public void Without_a_floor_the_first_class_reaches_down_to_everything()
    {
        JsonArray layers = Layers(CimStyle.ToMapLibre(Population(floored: false), "iller"));

        Assert.Equal(3, layers.Count);
        Assert.Equal("""["<=","nufus",5000]""", layers[^1]!["filter"]!.ToJsonString(Json.Plain));
        Assert.Equal("#ffff00", Drawn(layers, new() { ["nufus"] = -3.0 }));
    }

    // ------------------------------------------------------------------ variables

    [Fact]
    public void A_colour_variable_is_drawn_in_bands_each_painted_at_its_lower_edge()
    {
        DerivedStyle style = CimStyle.ToMapLibre(Fading(), "iller");
        JsonArray layers = Layers(style);

        // Below the first stop, eight across the range, and from the last stop up.
        Assert.Equal(10, layers.Count);

        Assert.Equal(
            """["any",["!has","nufus"],["<","nufus",0]]""",
            layers[0]!["filter"]!.ToJsonString(Json.Plain));
        Assert.Equal(
            """["all",[">=","nufus",250000],["<","nufus",500000]]""",
            layers[2]!["filter"]!.ToJsonString(Json.Plain));
        Assert.Equal("""[">=","nufus",2000000]""", layers[^1]!["filter"]!.ToJsonString(Json.Plain));

        Assert.Equal("#fff5eb", (string?)Paint(layers[0])["fill-color"]);
        Assert.Equal("#fff5eb", (string?)Paint(layers[1])["fill-color"]);
        Assert.Equal("#8c2d04", (string?)Paint(layers[^1])["fill-color"]);

        Assert.Contains(style.Losses, l => l.Contains("in 10 bands", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proportional_renderer_is_drawn_in_one_band_per_stop()
    {
        JsonArray layers = Layers(CimStyle.ToMapLibre(Dots(), "noktalar"));

        // Twelve geometric stops: one band below, eleven between, one from the last up.
        Assert.Equal(13, layers.Count);
        Assert.Equal(4.0, (double?)Paint(layers[0])["circle-radius"]);
        Assert.Equal(40.0, (double?)Paint(layers[^1])["circle-radius"]);

        double[] radii = [.. layers.Select(l => (double)Paint(l)["circle-radius"]!)];

        Assert.Equal(radii.Order(), radii);
    }

    // ------------------------------------------------------------------ equivalence

    public static TheoryData<string> Renderers => new()
    {
        "provinces", "provinces-no-default", "provinces-cased", "numeric-codes",
        "two-fields", "breaks-floored", "breaks", "fading", "dots", "classes-and-a-variable",
    };

    private static JsonObject Named(string name) => name switch
    {
        "provinces" => Provinces(),
        "provinces-no-default" => Provinces(otherwise: false),
        "provinces-cased" => Provinces(casing: true),
        "numeric-codes" => Unique(
            ["kod"], [([["7"], ["8"]], (255, 0, 0)), ([["12"]], (0, 0, 255))], (200, 200, 200)),
        "two-fields" => Unique(
            ["kullanim", "ilce"],
            [([["tarim", "Merkez"]], (120, 180, 90)), ([["konut", "Kuzey"]], (200, 80, 60))],
            (200, 200, 200)),
        "breaks-floored" => Population(floored: true),
        "breaks" => Population(floored: false),
        "fading" => Fading(),
        "dots" => Dots(),
        "classes-and-a-variable" => WithOpacity(Provinces()),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>A renderer with a transparency variable over a second field on top.</summary>
    private static JsonObject WithOpacity(JsonObject renderer)
    {
        renderer["visualVariables"] = JsonNode.Parse(
            """
            [{ "type": "CIMTransparencyVisualVariable", "field": "guven",
               "dataValues": [0, 100], "transparencyValues": [80, 0] }]
            """);

        return renderer;
    }

    private static IEnumerable<Dictionary<string, object?>> Features()
    {
        foreach (object? il in new object?[] { "Ankara", "İzmir", "Bursa", "Konya", null })
        {
            foreach (object? kod in new object?[] { 7.0, "7", 8.0, 12.0, 13.0, null })
            {
                foreach (double nufus in new[] { -5, 0, 999, 1000, 4999.5, 5000, 5000.25, 19999, 20000, 1e6, 3e6, 250000, 1234567 })
                {
                    yield return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["il"] = il,
                        ["kod"] = kod,
                        ["nufus"] = nufus,
                        ["guven"] = nufus % 101,
                        ["kullanim"] = nufus > 5000 ? "konut" : "tarim",
                        ["ilce"] = nufus > 1000 ? "Kuzey" : "Merkez",
                    };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void No_published_style_reads_a_feature_attribute_inside_its_paint(string name)
    {
        DerivedStyle style = CimStyle.ToMapLibre(Named(name), "katman");

        foreach (JsonNode? layer in Layers(style))
        {
            foreach ((string property, JsonNode? value) in Paint(layer))
            {
                Assert.False(
                    ReadsAnAttribute(value),
                    $"{name}: `{property}` on {layer!["id"]} is {value!.ToJsonString()} — an "
                    + "expression over a feature's attribute, which ArcGIS Pro does not draw.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void Each_feature_is_drawn_by_one_layer_per_symbol_layer_in_the_colour_this_server_paints(string name)
    {
        JsonObject renderer = Named(name);
        JsonArray published = Layers(CimStyle.ToMapLibre(renderer, "katman"));
        JsonArray expressions = Layers(CimStyle.ToExpressions(renderer, "katman"));
        CimProjection projection = Cim.Project(renderer);

        foreach (Dictionary<string, object?> feature in Features())
        {
            StyleExpression.Context context = new(feature, 0);

            // <b>A unique-value renderer without a default draws an unlisted value with nothing</b>,
            // on purpose; the expression form still paints it as the first class.
            bool unlisted = projection.Kind == Cim.UniqueValue
                && projection.Default is null
                && !projection.Classes.Any(c => c.Values.Contains(Key(projection, feature)));

            foreach (JsonObject level in expressions.OfType<JsonObject>())
            {
                string stem = (string)level["id"]!;
                List<JsonObject> drawn = [.. published.OfType<JsonObject>()
                    .Where(l => ((string)l["id"]!).StartsWith(stem, StringComparison.Ordinal)
                        && (string?)l["type"] == (string?)level["type"])
                    .Where(l => Filter.Matches(l["filter"], feature))];

                if (unlisted)
                {
                    Assert.Empty(drawn);
                    continue;
                }

                JsonObject one = Assert.Single(drawn);

                foreach ((string property, JsonNode? value) in Paint(level))
                {
                    if (value is JsonArray { Count: > 0 } array && array[0] is JsonValue head
                        && head.TryGetValue(out string? op)
                        && op is "match" or "step")
                    {
                        object? painted = StyleExpression.Compile(value).Evaluate(context);

                        Assert.Equal(
                            Normalise(painted),
                            Normalise(Plain(Paint(one)[property])));
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ the cap

    [Fact]
    public void Past_the_cap_the_tile_style_keeps_its_match_and_says_ArcGIS_Pro_will_not_draw_it()
    {
        JsonObject renderer = Unique(
            ["mahalle"],
            [.. Enumerable.Range(0, CimStyle.MostClassLayers + 1)
                .Select(i => (new[] { new[] { $"m{i}" } }, (i % 256, 0, 0)))],
            (200, 200, 200));

        DerivedStyle style = CimStyle.ToMapLibre(renderer, "mahalleler");

        JsonArray colour = Assert.IsType<JsonArray>(Paint(Layers(style).Single())["fill-color"]);

        Assert.Equal("match", (string?)colour[0]);
        Assert.Contains(
            style.Losses,
            l => l.Contains("ArcGIS Pro draws nothing", StringComparison.Ordinal)
                && l.Contains(CimStyle.MostClassLayers.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public void At_the_cap_the_tile_style_is_still_spread()
    {
        JsonObject renderer = Unique(
            ["mahalle"],
            [.. Enumerable.Range(0, CimStyle.MostClassLayers - 1)
                .Select(i => (new[] { new[] { $"m{i}" } }, (i % 256, 0, 0)))],
            (200, 200, 200));

        Assert.Equal(CimStyle.MostClassLayers, Layers(CimStyle.ToMapLibre(renderer, "mahalleler")).Count);
    }

    // ------------------------------------------------------------------ what did not change

    [Fact]
    public void A_simple_renderer_publishes_exactly_what_it_did_before()
    {
        // The showcase's `tr_yol`, which Pro already drew: nothing about it may move.
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            $$"""{ "type": "CIMSimpleRenderer", "symbol": {{Fill((139, 26, 26), casing: true)}} }""")!;

        Assert.Equal(
            CimStyle.ToExpressions(renderer, "tr_yol").Style.ToJsonString(),
            CimStyle.ToMapLibre(renderer, "tr_yol").Style.ToJsonString());
    }

    [Fact]
    public void This_servers_own_renderer_still_compiles_the_expression_form()
    {
        // `SymbologyPlan` refuses a filter; if it were handed the published form, every
        // classified layer would stop drawing on WMS and in the map service.
        SymbologyPlan plan = SymbologyPlan.Compile(Provinces().ToJsonString());

        StyleExpression.Classification? legend = plan.LegendClasses();

        Assert.NotNull(legend);
        Assert.Equal("il", legend!.Value.Field);
        Assert.Equal(4, legend.Value.Cases.Count);
    }

    // ------------------------------------------------------------------ the round trip

    [Theory]
    [MemberData(nameof(Renderers))]
    public void A_published_style_stores_again_as_a_renderer_that_publishes_it_again(string name)
    {
        DerivedStyle first = CimStyle.ToMapLibre(Named(name), "katman");

        CimWrite back = CimStyle.FromMapLibre(first.Style, GeometryKind.Polygon);
        DerivedStyle again = CimStyle.ToMapLibre(back.Renderer, "katman");

        Assert.Equal(first.Style.ToJsonString(), again.Style.ToJsonString());
    }

    [Fact]
    public void The_round_trip_keeps_the_default_symbol_and_its_absence()
    {
        CimProjection with = Cim.Project(CimStyle.FromMapLibre(
            CimStyle.ToMapLibre(Provinces(), "tr_il").Style, GeometryKind.Polygon).Renderer);

        CimProjection without = Cim.Project(CimStyle.FromMapLibre(
            CimStyle.ToMapLibre(Provinces(otherwise: false), "tr_il").Style, GeometryKind.Polygon).Renderer);

        Assert.Equal(Cim.UniqueValue, with.Kind);
        Assert.Equal("Ankara,İzmir,Bursa", string.Join(",", with.Classes.Select(c => c.Values.Single())));
        Assert.Equal(new Rgba(200, 200, 200, 255), Assert.IsType<CimFill>(with.Default!.Paints[0]).Colour);

        // <b>A `match` cannot say this; the missing default layer can.</b>
        Assert.Null(without.Default);
    }

    [Fact]
    public void The_round_trip_keeps_a_class_breaks_floor_and_its_bounds_exactly()
    {
        CimProjection back = Cim.Project(CimStyle.FromMapLibre(
            CimStyle.ToMapLibre(Population(floored: true), "iller").Style, GeometryKind.Polygon).Renderer);

        Assert.Equal(Cim.ClassBreaks, back.Kind);
        Assert.Equal(1000.0, back.Floor);
        Assert.Equal(5000.0, back.Classes[0].UpperBound);
        Assert.Equal(20000.0, back.Classes[1].UpperBound);
        Assert.Equal(new Rgba(200, 200, 200, 255), Assert.IsType<CimFill>(back.Default!.Paints[0]).Colour);
    }

    [Fact]
    public void The_round_trip_keeps_two_fields_and_a_class_of_two_values()
    {
        JsonObject renderer = Unique(
            ["kullanim", "ilce"],
            [([["tarim", "Merkez"]], (120, 180, 90)), ([["konut", "Kuzey"], ["konut", "Guney"]], (200, 80, 60))],
            null);

        CimProjection back = Cim.Project(CimStyle.FromMapLibre(
            CimStyle.ToMapLibre(renderer, "parsel").Style, GeometryKind.Polygon).Renderer);

        Assert.Equal(["kullanim", "ilce"], back.Fields);
        Assert.Equal(2, back.Classes.Count);
        Assert.Equal([["konut", "Kuzey"], ["konut", "Guney"]], back.Classes[1].Tuples);
    }

    [Fact]
    public void A_published_style_is_accepted_by_the_write_path_and_stored_as_CIM()
    {
        string published = CimStyle.ToMapLibre(Provinces(), "tr_il").Style.ToJsonString();

        SymbologyWrite written = SymbologyConversion.Read(published, GeometryKind.Polygon);

        Assert.Equal("MapLibre", written.Source);

        // <b>What is stored is the renderer, never the filters.</b>
        Assert.DoesNotContain("filter", written.Canonical, StringComparison.Ordinal);
        Assert.Equal(
            published,
            CimStyle.ToMapLibre((JsonObject)JsonNode.Parse(written.Canonical)!, "tr_il").Style.ToJsonString());
    }

    [Fact]
    public void A_filter_this_server_did_not_write_is_still_refused()
    {
        const string Style =
            """
            { "version": 8, "layers": [
              { "id": "a", "type": "fill", "filter": ["==", "il", "Ankara"],
                "paint": { "fill-color": "#ff0000" } },
              { "id": "b", "type": "fill", "filter": ["has", "il"],
                "paint": { "fill-color": "#00ff00" } } ] }
            """;

        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => SymbologyConversion.Read(Style, GeometryKind.Polygon));

        Assert.Contains("filter", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_match_written_by_a_person_is_still_read_as_before()
    {
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            { "version": 8, "layers": [{ "id": "x", "type": "fill",
              "paint": { "fill-color": ["match", ["get", "il"], "Ankara", "#4477aa", "#cccccc"] } }] }
            """)!;

        CimProjection back = Cim.Project(CimStyle.FromMapLibre(style, GeometryKind.Polygon).Renderer);

        Assert.Equal(Cim.UniqueValue, back.Kind);
        Assert.Equal("Ankara", back.Classes.Single().Values.Single());
        Assert.NotNull(back.Default);
    }

    [Fact]
    public void A_match_over_two_fields_joined_is_read_as_a_renderer_of_two_fields()
    {
        // <b>New on 2026-09-29</b>: this server writes that `concat` for a two-field renderer and
        // could not read it back, so its own expression form was unstorable.
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            { "version": 8, "layers": [{ "id": "x", "type": "fill",
              "paint": { "fill-color": ["match",
                ["concat", ["to-string", ["get", "a"]], " / ", ["to-string", ["get", "b"]]],
                "x / y", "#ff0000", "#cccccc"] } }] }
            """)!;

        CimProjection back = Cim.Project(CimStyle.FromMapLibre(style, GeometryKind.Polygon).Renderer);

        Assert.Equal(["a", "b"], back.Fields);
        Assert.Equal(["x", "y"], back.Classes.Single().Tuples.Single());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The colour of the one layer whose filter takes the feature.</summary>
    private static string? Drawn(JsonArray layers, Dictionary<string, object?> feature) =>
        (string?)Paint(Assert.Single(layers, l => Filter.Matches(l!["filter"], feature)))["fill-color"];

    /// <summary>The joined key a unique-value renderer would look a feature up by.</summary>
    private static string Key(CimProjection projection, Dictionary<string, object?> feature)
    {
        IReadOnlyList<string> fields = projection.Fields.Count > 0 ? projection.Fields : [projection.Field!];

        return string.Join(
            projection.Delimiter,
            fields.Select(f => feature.GetValueOrDefault(f) switch
            {
                double d => d.ToString(CultureInfo.InvariantCulture),
                { } other => other.ToString(),
                null => string.Empty,
            }));
    }

    private static object? Plain(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue(out string? text) ? text : value.GetValue<double>()
            : null;

    private static string? Normalise(object? value) => value switch
    {
        string s when Rgba.TryParse(s, out Rgba c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}",
        string s => s,
        null => null,
        _ => StyleExpression.AsNumber(value) is { } n
            ? Math.Round(n, 3).ToString(CultureInfo.InvariantCulture)
            : value.ToString(),
    };

    /// <summary>Whether a paint value is an expression that reads a feature's attribute.</summary>
    private static bool ReadsAnAttribute(JsonNode? node) =>
        node is JsonArray array
        && (array.Any(ReadsAnAttribute)
            || (array.Count >= 2
                && array[0] is JsonValue head
                && head.TryGetValue(out string? op)
                && op is "get" or "has" or "properties" or "feature-state"));

    /// <summary>MapLibre's legacy filter semantics, strictly typed as the specification says.</summary>
    private static class Filter
    {
        public static bool Matches(JsonNode? filter, Dictionary<string, object?> feature)
        {
            if (filter is null)
            {
                return true;
            }

            JsonArray f = (JsonArray)filter;
            string op = (string)f[0]!;

            return op switch
            {
                "all" => f.Skip(1).All(c => Matches(c, feature)),
                "any" => f.Skip(1).Any(c => Matches(c, feature)),
                "none" => !f.Skip(1).Any(c => Matches(c, feature)),
                "has" => Value(feature, f[1]) is not null,
                "!has" => Value(feature, f[1]) is null,
                "==" => Equal(Value(feature, f[1]), f[2]),
                "!=" => !Equal(Value(feature, f[1]), f[2]),
                "in" => f.Skip(2).Any(v => Equal(Value(feature, f[1]), v)),
                "!in" => !f.Skip(2).Any(v => Equal(Value(feature, f[1]), v)),
                "<" or "<=" or ">" or ">=" => Value(feature, f[1]) is double d
                    && f[2]!.GetValue<double>() is var n
                    && op switch
                    {
                        "<" => d < n,
                        "<=" => d <= n,
                        ">" => d > n,
                        _ => d >= n,
                    },
                _ => throw new InvalidOperationException($"`{op}` is not a legacy filter this test knows."),
            };
        }

        private static object? Value(Dictionary<string, object?> feature, JsonNode? key) =>
            feature.GetValueOrDefault((string)key!);

        private static bool Equal(object? value, JsonNode? literal) =>
            literal is JsonValue v
            && (v.TryGetValue(out string? s)
                ? value is string t && t == s
                : value is double d && d == v.GetValue<double>());
    }

    private static class Json
    {
        public static readonly System.Text.Json.JsonSerializerOptions Plain = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
