using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A fourth renderer, read as the first one carrying a size variable.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.10, by owner decision 2026-09-04.</b> `CIMProportionalRenderer` is one of the
/// two renderers CIM has that the JavaScript SDK does not, and the reason it is missing there is
/// the reason it costs nothing here: the SDK draws the same map with a `SimpleRenderer` and a
/// size visual variable. The specification says as much of `CIMSizeVisualVariable` — *VariableType
/// = Proportional, unit NOT defined use Expression, MinSize, MinValue, could use MaxSize*.
/// </para>
/// <para>
/// <b>So the projection's `Kind` is `CIMSimpleRenderer` and both faces were left alone.</b> These
/// tests exist to hold that claim down: what comes out of the two faces has to be a simple
/// renderer whose size slides, and it has to slide along the right curve.
/// </para>
/// </remarks>
public sealed class CimProportionalRendererTests
{
    /// <summary>Six-point dots growing with a population between 100 and 10,000.</summary>
    /// <remarks>
    /// The ratio is exactly 100 and the exponent exactly a half, so the largest symbol is
    /// exactly ten times the smallest and no assertion here needs a tolerance to hide behind.
    /// </remarks>
    private const string DotsByPopulation =
        """
        {
          "type": "CIMProportionalRenderer",
          "heading": "nufus",
          "field": "nufus",
          "minDataValue": 100,
          "maxDataValue": 10000,
          "minSymbol": { "symbol": { "type": "CIMPointSymbol", "symbolLayers": [
            { "type": "CIMVectorMarker", "size": 6,
              "markerGraphics": [ { "type": "CIMMarkerGraphic", "symbol": {
                "type": "CIMPolygonSymbol", "symbolLayers": [
                  { "type": "CIMSolidFill",
                    "color": { "type": "CIMRGBColor", "values": [0, 122, 194, 100] } }] } } ] }] } }
        }
        """;

    [Fact]
    public void It_projects_as_a_simple_renderer_carrying_one_size_variable()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(DotsByPopulation)!);

        // <b>The `Kind` is the load-bearing part.</b> The claim that this renderer needed no new
        // drawing is exactly the claim that it reduces to this one. Falsified: with the
        // projection keeping `CIMProportionalRenderer`, this test and the ArcGIS face's fail and
        // the tile face's does not — the tile face has already collapsed a projection with no
        // classifying field to a constant before it would look at the kind.
        Assert.Equal(Cim.Simple, projection.Kind);
        Assert.Null(projection.Field);
        Assert.Single(projection.Classes);
        Assert.Empty(projection.NotDrawn);

        CimVary sizing = Assert.Single(projection.Vary);

        Assert.Equal(CimVaries.Size, sizing.What);
        Assert.Equal("nufus", sizing.Field);
        Assert.Equal(12, sizing.Stops.Count);
        Assert.Equal(12, sizing.Numbers.Count);
    }

    [Fact]
    public void The_curve_starts_at_the_minimum_symbol_and_ends_where_area_proportion_puts_it()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(DotsByPopulation)!);

        CimVary sizing = Assert.Single(projection.Vary);

        Assert.Equal(100.0, sizing.Stops[0]);
        Assert.Equal(6.0, sizing.Numbers[0]);

        // <b>Exactly the maximum, not near it.</b> Eleven multiplications of the hundredth root
        // of a hundred land at 9999.999999999998, and a legend reading that for a maximum of
        // 10,000 is a legend somebody has to explain.
        Assert.Equal(10000.0, sizing.Stops[^1]);

        // Area proportional: a hundred times the value is ten times the radius.
        Assert.Equal(60.0, sizing.Numbers[^1], 9);

        // <b>Geometric, which is the measured part.</b> Stops spaced evenly by value are 41%
        // wrong against the true curve at this many stops; spaced by a constant ratio the same
        // twelve are wrong by 1.22% at worst. Every neighbouring pair shares one ratio.
        double ratio = sizing.Stops[1] / sizing.Stops[0];

        for (int i = 1; i < sizing.Stops.Count - 1; i++)
        {
            Assert.Equal(ratio, sizing.Stops[i + 1] / sizing.Stops[i], 9);
        }

        // And every size is the curve's own value at its stop.
        for (int i = 0; i < sizing.Stops.Count; i++)
        {
            Assert.Equal(
                6.0 * Math.Sqrt(sizing.Stops[i] / 100.0), sizing.Numbers[i], 9);
        }
    }

    [Fact]
    public void Flannery_compensation_bends_the_curve_and_is_read_from_the_document()
    {
        CimProjection plain = Cim.Project((JsonObject)JsonNode.Parse(DotsByPopulation)!);

        CimProjection compensated = Cim.Project((JsonObject)JsonNode.Parse(
            DotsByPopulation.Replace(
                "\"field\": \"nufus\",",
                "\"field\": \"nufus\", \"flanneryCompensation\": true,",
                StringComparison.Ordinal))!);

        double flat = Assert.Single(plain.Vary).Numbers[^1];
        double bent = Assert.Single(compensated.Vary).Numbers[^1];

        // <b>0.5716, from the literature and not from any implementation.</b> Over a ratio of a
        // hundred that is 10x against 13.9x, which is a visible difference rather than a
        // rounding one — the point of the correction is that readers under-read large circles.
        Assert.Equal(60.0, flat, 9);
        Assert.Equal(6.0 * Math.Pow(100.0, 0.5716), bent, 9);
        Assert.True(bent > flat * 1.3, $"Flannery gave {bent:0.###} against {flat:0.###}.");
    }

    [Fact]
    public void The_tile_face_draws_it_as_a_circle_whose_radius_slides()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(DotsByPopulation)!, "sehirler");

        JsonObject paint = (JsonObject)((JsonArray)derived.Style["layers"]!).Single()!["paint"]!;

        JsonArray radius = Assert.IsType<JsonArray>(paint["circle-radius"]);

        Assert.Equal("interpolate", (string?)radius[0]);
        Assert.Equal("nufus", (string?)((JsonArray)radius[2]!)[1]);

        // `["interpolate", ["linear"], ["get", f], v0, r0, ... ]` — twelve pairs after three.
        Assert.Equal(3 + (12 * 2), radius.Count);

        // <b>A marker's size is across, a radius is from the centre, and CIM measures in points
        // while a style measures in pixels.</b> Six points across is three points of radius is
        // four pixels.
        Assert.Equal(100.0, (double?)radius[3]);
        Assert.Equal(4.0, (double?)radius[4]);
        Assert.Equal(10000.0, (double?)radius[^2]);
        Assert.Equal(40.0, (double?)radius[^1]);
    }

    [Fact]
    public void The_ArcGIS_face_publishes_a_simple_renderer_with_a_sizeInfo()
    {
        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(DotsByPopulation)!, "sehirler");

        JsonObject renderer = (JsonObject)drawing.DrawingInfo["renderer"]!;

        // <b>`simple`, because that is what a client can read.</b> ArcGIS's REST `drawingInfo`
        // has no proportional renderer either; it has `simple` and a `sizeInfo` visual variable,
        // which is the same reduction the JavaScript SDK makes.
        Assert.Equal("simple", (string?)renderer["type"]);

        JsonObject one = (JsonObject)((JsonArray)renderer["visualVariables"]!).Single()!;

        Assert.Equal("sizeInfo", (string?)one["type"]);
        Assert.Equal("nufus", (string?)one["field"]);

        JsonArray stops = (JsonArray)one["stops"]!;

        Assert.Equal(12, stops.Count);
        Assert.Equal(100.0, (double?)stops[0]!["value"]);
        Assert.Equal(6.0, (double?)stops[0]!["size"]);
        Assert.Equal(10000.0, (double?)stops[^1]!["value"]);
        Assert.Equal(60.0, (double?)stops[^1]!["size"]);
    }

    [Fact]
    public void A_symbol_sized_in_ground_units_is_reported_rather_than_drawn_at_one_scale()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(
            DotsByPopulation.Replace(
                "\"field\": \"nufus\",",
                """
                "field": "nufus",
                "unitSymbolization": {
                  "type": "CIMUnitSymbolization",
                  "valueRepresentation": "Area",
                  "valueUnit": { "uwkid": 9001 },
                  "symbolShape": "Circle"
                },
                """,
                StringComparison.Ordinal))!);

        // <b>Drawn, and honest about what was dropped.</b> A ground-unit symbol changes size as
        // you zoom; this server sizes markers in points, so a fixed size would be right at one
        // scale and silently wrong at every other.
        Assert.Empty(projection.Vary);
        Assert.Single(projection.Classes);
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("ground units", StringComparison.Ordinal));
    }

    [Fact]
    public void A_range_that_reaches_zero_has_no_proportional_size_and_says_so()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(
            DotsByPopulation.Replace(
                "\"minDataValue\": 100,", "\"minDataValue\": 0,", StringComparison.Ordinal))!);

        // A proportional size is a ratio to the smallest value, so a smallest value of zero
        // divides by it. Inventing a treatment here would be inventing the map.
        Assert.Empty(projection.Vary);
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("read as 0 to 10000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_size_variable_the_author_wrote_wins_over_the_one_this_server_computes()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(
            DotsByPopulation.Replace(
                "\"field\": \"nufus\",",
                """
                "field": "nufus",
                "visualVariables": [{
                  "type": "CIMSizeVisualVariable",
                  "valueExpressionInfo": { "expression": "$feature.alan" },
                  "minValue": 1, "maxValue": 9, "minSize": 4, "maxSize": 40
                }],
                """,
                StringComparison.Ordinal))!);

        // <b>A proportional renderer inherits `CIMVisualVariableRenderer`.</b> When it carries a
        // size variable of its own, that one is what somebody authored and this one is
        // arithmetic derived from two numbers, so the author's wins and there is exactly one.
        CimVary sizing = Assert.Single(projection.Vary);

        Assert.Equal("alan", sizing.Field);
        Assert.Equal([1.0, 9.0], sizing.Stops);
        Assert.Equal([4.0, 40.0], sizing.Numbers);
    }

    [Fact]
    public void A_stored_proportional_renderer_is_stored_exactly_as_it_arrived()
    {
        SymbologyWrite written = SymbologyConversion.Read(DotsByPopulation, GeometryKind.Point);

        // <b>ADR-052's whole argument.</b> The projection is what can be drawn; the document is
        // what was meant. `flanneryCompensation`, `legendSymbolCount`, `showInAscendingOrder`
        // and everything else this server does not read has to survive a round trip, or storing
        // CIM buys nothing over storing a style.
        JsonObject back = (JsonObject)JsonNode.Parse(written.Canonical)!;

        Assert.Equal("CIMProportionalRenderer", (string?)back["type"]);
        Assert.Equal(100.0, (double?)back["minDataValue"]);
        Assert.Equal("CIM", written.Source);
    }
}
