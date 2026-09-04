using System;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The bottom of a classification, which is on the renderer and not on any of its breaks.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-205](../../docs/architecture-debt.md).</b> A `CIMClassBreak` carries only its
/// `upperBound`. The floor of the whole classification is `minimumBreak`, on the renderer —
/// `CIMClassBreaksProperties` in the specification — and a value below it is *outside the
/// classification*, not inside its first class. This server wrote the property in one direction
/// and never read it back in the other, so every derived face behaved as though the
/// classification started at negative infinity.
/// </para>
/// <para>
/// <b>What that looked like:</b> a population choropleth floored at 1,000 drew every village in
/// the colour of a small town, and published `minValue: 0` — a literal zero, hard-coded — to
/// every ArcGIS client. Nothing failed, and the map is the kind that gets read at a glance and
/// believed.
/// </para>
/// </remarks>
public sealed class ClassBreaksFloorTests
{
    /// <summary>Two classes over a population, floored at a thousand, with a default.</summary>
    private const string FlooredAtAThousand =
        """
        {
          "type": "CIMClassBreaksRenderer",
          "field": "nufus",
          "minimumBreak": 1000,
          "useDefaultSymbol": true,
          "defaultSymbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [200, 200, 200, 100] } }] } },
          "breaks": [
            { "upperBound": 5000, "label": "kasaba", "symbol": { "symbol": {
              "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [255, 255, 0, 100] } }] } } },
            { "upperBound": 20000, "label": "sehir", "symbol": { "symbol": {
              "type": "CIMPolygonSymbol", "symbolLayers": [{ "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [255, 0, 0, 100] } }] } } }
          ]
        }
        """;

    [Fact]
    public void The_floor_is_read_off_the_renderer_and_not_invented()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(FlooredAtAThousand)!);

        Assert.Equal(1000.0, projection.Floor);
        Assert.Empty(projection.NotDrawn);
    }

    [Fact]
    public void The_tile_face_draws_below_the_floor_with_the_default_symbol()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(FlooredAtAThousand)!, "iller");

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)derived.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        // `["step", ["get", f], below, 1000, kasaba, 5000+e, sehir]`
        Assert.Equal("step", (string?)colour[0]);

        // <b>The grey, and this is the whole defect.</b> Before the repair this slot held the
        // first class's yellow, so a village of 400 people was drawn as a town.
        Assert.Equal("#c8c8c8", (string?)colour[2]);

        Assert.Equal(1000.0, (double?)colour[3]);
        Assert.Equal("#ffff00", (string?)colour[4]);

        // <b>The floor is exact and the interior breaks are not, on purpose.</b> `minimumBreak`
        // is the first value *inside* the classification, so `step`'s lower-inclusive stop is
        // the number itself; an `upperBound` is the last value inside its class, so that stop is
        // the next representable double above it.
        Assert.Equal(Math.BitIncrement(5000.0), (double?)colour[5]);
        Assert.Equal("#ff0000", (string?)colour[6]);
        Assert.Equal(7, colour.Count);
    }

    [Fact]
    public void The_ArcGIS_face_publishes_the_floor_instead_of_a_hard_coded_zero()
    {
        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(FlooredAtAThousand)!, "iller");

        JsonObject renderer = (JsonObject)drawing.DrawingInfo["renderer"]!;

        Assert.Equal("classBreaks", (string?)renderer["type"]);
        Assert.Equal(1000.0, (double?)renderer["minValue"]);

        JsonArray infos = (JsonArray)renderer["classBreakInfos"]!;

        // The first class starts at the floor. It used to start at null, which a client reads
        // as "no lower bound" and draws exactly the way the tile face did.
        Assert.Equal(1000.0, (double?)infos[0]!["classMinValue"]);
        Assert.Equal(5000.0, (double?)infos[0]!["classMaxValue"]);
        Assert.Equal(5000.0, (double?)infos[1]!["classMinValue"]);
    }

    [Fact]
    public void A_floor_with_nothing_to_fall_to_is_reported_rather_than_half_applied()
    {
        // <b>The honest half.</b> Without a default symbol there is nothing to paint the
        // features below the floor with, and this server cannot say *draw nothing* in an
        // expression that is only about colour. Saying so beats picking a colour.
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(
            FlooredAtAThousand
                .Replace("\"useDefaultSymbol\": true,", "\"useDefaultSymbol\": false,",
                    StringComparison.Ordinal))!);

        Assert.Null(projection.Floor);
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("classifies from 1000 upwards", StringComparison.Ordinal));
    }

    [Fact]
    public void A_renderer_with_no_floor_still_starts_at_its_first_class()
    {
        // <b>The repair must not invent a floor.</b> Most documents carry none, and for those
        // the first class is still what everything below the first bound gets.
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(
                FlooredAtAThousand.Replace(
                    "\"minimumBreak\": 1000,", "", StringComparison.Ordinal))!,
            "iller");

        JsonArray colour = Assert.IsType<JsonArray>(
            ((JsonArray)derived.Style["layers"]!).Single()!["paint"]!["fill-color"]);

        Assert.Equal("#ffff00", (string?)colour[2]);
        Assert.Equal(5, colour.Count);
    }
}
