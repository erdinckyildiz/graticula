using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The second axis: one property of a symbol sliding with the value of a field.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.6.</b> A
/// renderer says which feature gets which symbol; a visual variable says how one property of
/// that symbol moves continuously with a number. Half of ArcGIS's named styles are a renderer
/// plus one of these — `Counts and Amounts`, `Age`, `Predominance` — so a canonical model
/// without them cannot hold what people actually author.
/// </para>
/// <para>
/// <b>The renderer needed nothing new.</b> `SymbologyPlan` has compiled `interpolate` since
/// ADR-041 and evaluates its input per feature, so the drawing half was already there and
/// unreachable. These tests are about the vocabulary reaching it.
/// </para>
/// </remarks>
public sealed class CimVisualVariableTests
{
    /// <summary>A polygon renderer whose fill fades from pale to dark with population.</summary>
    private const string FadingByPopulation =
        """
        {
          "type": "CIMSimpleRenderer",
          "label": "il",
          "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [200, 200, 200, 100] } }] } },
          "visualVariables": [{
            "type": "CIMColorVisualVariable",
            "expression": "$feature.nufus",
            "minValue": 0,
            "maxValue": 2000000,
            "colorRamp": {
              "type": "CIMLinearContinuousColorRamp",
              "fromColor": { "type": "CIMRGBColor", "values": [255, 245, 235, 100] },
              "toColor":   { "type": "CIMRGBColor", "values": [140, 45, 4, 100] }
            }
          }]
        }
        """;

    [Fact]
    public void A_colour_variable_becomes_an_interpolate_over_its_own_field()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(FadingByPopulation)!, "iller");

        JsonObject paint = (JsonObject)((JsonArray)derived.Style["layers"]!).Single()!["paint"]!;

        JsonArray colour = Assert.IsType<JsonArray>(paint["fill-color"]);

        Assert.Equal("interpolate", (string?)colour[0]);
        Assert.Equal("linear", (string?)((JsonArray)colour[1]!)[0]);
        Assert.Equal("get", (string?)((JsonArray)colour[2]!)[0]);
        Assert.Equal("nufus", (string?)((JsonArray)colour[2]!)[1]);

        Assert.Equal(0.0, (double?)colour[3]);
        Assert.Equal("#fff5eb", (string?)colour[4]);
        Assert.Equal(2000000.0, (double?)colour[5]);
        Assert.Equal("#8c2d04", (string?)colour[6]);

        // <b>The symbol's own colour is replaced, not blended with.</b> The stored fill is grey;
        // if it survived into the style the map would be grey wherever the interpolation was not
        // reached, which is a map that changes appearance for a reason nobody can see.
        Assert.DoesNotContain("#c8c8c8", colour.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_field_is_read_from_all_three_spellings_Esri_uses()
    {
        foreach (string spelling in
                 new[] { "\"nufus\"", "\"$feature.nufus\"", "\"$feature[\\\"nufus\\\"]\"" })
        {
            JsonObject renderer = (JsonObject)JsonNode.Parse(
                FadingByPopulation.Replace(
                    "\"$feature.nufus\"", spelling, StringComparison.Ordinal))!;

            CimProjection projection = Cim.Project(renderer);

            CimVary one = Assert.Single(projection.Vary);

            Assert.Equal("nufus", one.Field);
            Assert.Equal(CimVaries.Colour, one.What);
        }
    }

    /// <summary>
    /// The fourth spelling, and the one ArcGIS Pro writes.
    /// </summary>
    /// <remarks>
    /// <b>`expression` is the Python and VBScript slot.</b> `CIMRenderers.md` says of it that it
    /// *is used for Python or VBScript expressions. Arcade expressions will use the
    /// ValueExpressionInfo property* — so the spelling this reader was built around is the
    /// legacy one, and the current one was falling through to *names no field this server can
    /// read*. The renderer then drew one flat symbol, which is a picture with nothing wrong
    /// with it except that it is not what the document asked for.
    /// </remarks>
    [Fact]
    public void Arcade_in_valueExpressionInfo_is_read_because_that_is_where_Pro_puts_it()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            FadingByPopulation.Replace(
                "\"expression\": \"$feature.nufus\",",
                """
                "valueExpressionInfo": {
                  "type": "CIMExpressionInfo",
                  "title": "Nufus",
                  "expression": "$feature.nufus",
                  "returnType": "Default"
                },
                """,
                StringComparison.Ordinal))!;

        CimProjection projection = Cim.Project(renderer);

        CimVary one = Assert.Single(projection.Vary);

        Assert.Equal("nufus", one.Field);
        Assert.Equal(CimVaries.Colour, one.What);
        Assert.Empty(projection.NotDrawn);
    }

    [Fact]
    public void An_Arcade_expression_that_computes_is_refused_rather_than_read_as_a_column()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            FadingByPopulation.Replace(
                "\"$feature.nufus\"",
                "\"$feature.nufus / $feature.alan\"",
                StringComparison.Ordinal))!;

        CimProjection projection = Cim.Project(renderer);

        // <b>Named, not guessed at.</b> Reading `nufus / alan` as a column name would ask the
        // source for a column that does not exist and fail as a database error a long way from
        // the document that caused it.
        Assert.Empty(projection.Vary);
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("Arcade expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Transparency_is_stored_the_other_way_up_from_opacity()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMSimpleRenderer",
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [10, 20, 30, 100] } }] } },
              "visualVariables": [{
                "type": "CIMTransparencyVisualVariable",
                "field": "guven",
                "dataValues": [0, 100],
                "transparencyValues": [80, 0]
              }]
            }
            """)!;

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "iller");

        JsonArray opacity = Assert.IsType<JsonArray>(
            ((JsonArray)derived.Style["layers"]!).Single()!["paint"]!["fill-opacity"]);

        // CIM transparency 80 means 20% opaque; 0 means fully opaque. Copying the number
        // instead of turning it over makes every faint feature solid and every solid one faint.
        Assert.Equal(0.0, (double?)opacity[3]);
        Assert.Equal(0.2, (double?)opacity[4]);
        Assert.Equal(100.0, (double?)opacity[5]);
        Assert.Equal(1.0, (double?)opacity[6]);
    }

    [Fact]
    public void A_size_variable_on_a_fill_changes_nothing_and_says_so()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMSimpleRenderer",
              "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                { "type": "CIMSolidFill",
                  "color": { "type": "CIMRGBColor", "values": [10, 20, 30, 100] } }] } },
              "visualVariables": [{
                "type": "CIMSizeVisualVariable",
                "expression": "$feature.nufus",
                "minValue": 0, "maxValue": 100, "minSize": 2, "maxSize": 20
              }]
            }
            """)!;

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "iller");

        // <b>Not applied to the outline instead.</b> That is the plausible guess and it is a
        // renderer inventing an intent: a fill has no size, and widening its edge is a different
        // map from the one that was asked for.
        JsonObject paint = (JsonObject)((JsonArray)derived.Style["layers"]!).Single()!["paint"]!;

        Assert.False(paint.ContainsKey("line-width"));

        Assert.Contains(
            derived.Losses,
            l => l.Contains("a fill has no size", StringComparison.Ordinal));
    }

    [Fact]
    public void A_variable_that_wants_a_classified_property_wins_and_the_conflict_is_reported()
    {
        JsonObject renderer = (JsonObject)JsonNode.Parse(
            """
            {
              "type": "CIMUniqueValueRenderer",
              "fields": ["tur"],
              "groups": [{ "classes": [
                { "label": "a", "values": [{ "fieldValues": ["a"] }],
                  "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                    { "type": "CIMSolidFill",
                      "color": { "type": "CIMRGBColor", "values": [255, 0, 0, 100] } }] } } },
                { "label": "b", "values": [{ "fieldValues": ["b"] }],
                  "symbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
                    { "type": "CIMSolidFill",
                      "color": { "type": "CIMRGBColor", "values": [0, 0, 255, 100] } }] } } }
              ] }],
              "visualVariables": [{
                "type": "CIMColorVisualVariable",
                "expression": "$feature.nufus",
                "minValue": 0, "maxValue": 100,
                "colorRamp": { "type": "CIMLinearContinuousColorRamp",
                  "fromColor": { "type": "CIMRGBColor", "values": [255, 255, 255, 100] },
                  "toColor": { "type": "CIMRGBColor", "values": [0, 0, 0, 100] } }
              }]
            }
            """)!;

        DerivedStyle derived = CimStyle.ToMapLibre(renderer, "iller");

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)derived.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        // The variable, not the classes.
        Assert.Equal("interpolate", (string?)colour[0]);

        Assert.Contains(
            derived.Losses,
            l => l.Contains("The variable wins", StringComparison.Ordinal));
    }

    [Fact]
    public void A_continuous_style_reads_back_as_a_variable_instead_of_being_reported_lost()
    {
        // <b>This used to be a loss and is now a round trip.</b> Under ADR-033 a style that
        // faded a colour with a column was flattened to the colour at its lowest stop and the
        // variation was reported gone, because the canonical document had nowhere to keep it.
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "iller-0", "type": "fill", "source-layer": "iller",
                "paint": {
                  "fill-color": ["interpolate", ["linear"], ["get", "nufus"],
                    0, "#fff5eb", 2000000, "#8c2d04"]
                }
              }]
            }
            """)!;

        CimWrite written = CimStyle.FromMapLibre(style, GeometryKind.Polygon);

        Assert.DoesNotContain(
            written.Losses,
            l => l.Contains("interpolate", StringComparison.OrdinalIgnoreCase));

        CimProjection projection = Cim.Project(written.Renderer);
        CimVary one = Assert.Single(projection.Vary);

        Assert.Equal(CimVaries.Colour, one.What);
        Assert.Equal("nufus", one.Field);
        Assert.Equal(new Rgba(255, 245, 235, 255), one.Colours[0]);
        Assert.Equal(new Rgba(140, 45, 4, 255), one.Colours[^1]);

        // <b>And out again unchanged.</b> A round trip that drifted would move the boundary of
        // every choropleth a little on each edit.
        DerivedStyle back = CimStyle.ToMapLibre(written.Renderer, "iller");

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)back.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        Assert.Equal(0.0, (double?)colour[3]);
        Assert.Equal("#fff5eb", (string?)colour[4]);
        Assert.Equal(2000000.0, (double?)colour[5]);
        Assert.Equal("#8c2d04", (string?)colour[6]);
    }

    [Fact]
    public void An_interpolate_over_the_zoom_is_still_not_a_statement_about_the_data()
    {
        JsonObject style = (JsonObject)JsonNode.Parse(
            """
            {
              "version": 8,
              "layers": [{
                "id": "yollar-0", "type": "line", "source-layer": "yollar",
                "paint": {
                  "line-color": "#8b1a1a",
                  "line-width": ["interpolate", ["linear"], ["zoom"], 6, 0.5, 14, 6]
                }
              }]
            }
            """)!;

        CimWrite written = CimStyle.FromMapLibre(style, GeometryKind.LineString);

        // <b>Reported, not stored.</b> Filing a zoom rule as a visual variable would claim the
        // map says something about a field it never mentions.
        Assert.Contains(
            written.Losses,
            l => l.Contains("line-width", StringComparison.Ordinal)
                && l.Contains("zoom", StringComparison.Ordinal));

        Assert.Empty(Cim.Project(written.Renderer).Vary);
    }

    [Fact]
    public void The_Esri_face_publishes_the_variable_as_a_colorInfo()
    {
        DerivedDrawingInfo derived = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(FadingByPopulation)!, "iller");

        JsonArray variables = Assert.IsType<JsonArray>(
            derived.DrawingInfo["renderer"]!["visualVariables"]);

        JsonObject one = (JsonObject)variables.Single()!;

        Assert.Equal("colorInfo", (string?)one["type"]);
        Assert.Equal("nufus", (string?)one["field"]);

        JsonArray stops = (JsonArray)one["stops"]!;

        Assert.Equal(2, stops.Count);
        Assert.Equal(0.0, (double?)stops[0]!["value"]);
        Assert.Equal(255, (int)((JsonArray)stops[0]!["color"]!)[0]!);
        Assert.Equal(2000000.0, (double?)stops[1]!["value"]);
        Assert.Equal(140, (int)((JsonArray)stops[1]!["color"]!)[0]!);
    }

    [Fact]
    public void An_Esri_sizeInfo_comes_in_and_goes_back_out_the_same()
    {
        JsonObject drawingInfo = (JsonObject)JsonNode.Parse(
            """
            {
              "renderer": {
                "type": "simple",
                "symbol": { "type": "esriSMS", "style": "esriSMSCircle",
                  "color": [200, 30, 30, 255], "size": 8 },
                "visualVariables": [{
                  "type": "sizeInfo",
                  "field": "buyukluk",
                  "stops": [ { "value": 1, "size": 4 }, { "value": 9, "size": 40 } ]
                }]
              }
            }
            """)!;

        CimWrite stored = CimEsri.FromDrawingInfo(drawingInfo, GeometryKind.Point);

        DerivedDrawingInfo back = CimEsri.ToDrawingInfo(stored.Renderer, "yerler");

        JsonObject one = (JsonObject)((JsonArray)
            back.DrawingInfo["renderer"]!["visualVariables"]!).Single()!;

        Assert.Equal("sizeInfo", (string?)one["type"]);
        Assert.Equal("buyukluk", (string?)one["field"]);

        JsonArray stops = (JsonArray)one["stops"]!;

        Assert.Equal(1.0, (double?)stops[0]!["value"]);
        Assert.Equal(4.0, (double?)stops[0]!["size"]);
        Assert.Equal(9.0, (double?)stops[1]!["value"]);
        Assert.Equal(40.0, (double?)stops[1]!["size"]);

        // <b>And it draws.</b> A marker's size is across and MapLibre's radius is from the
        // centre, so the style must carry half.
        JsonArray radius = Assert.IsType<JsonArray>(
            ((JsonArray)CimStyle.ToMapLibre(stored.Renderer, "yerler").Style["layers"]!)
                .Single()!["paint"]!["circle-radius"]);

        Assert.Equal(2.667, (double?)radius[4]);
    }
}
