using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The renderer whose answer does not belong to a feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.14.</b> Every other renderer here says which symbol a feature gets. A heat map
/// says how crowded a place is, so a pixel's colour depends on every point near it and there is
/// nothing to resolve per feature. It accumulates while the features go past and is composited
/// once, through `IMapCanvas.DrawImage`.
/// </para>
/// <para>
/// <b>It needed no new drawing primitive, which corrects a claim this project shipped.</b> The
/// refusal message said each unread renderer *needs a drawing primitive this server does not
/// have*; `DrawImage` was declared and implemented the whole time. The claim was falsified by
/// reading the canvas rather than remembering it.
/// </para>
/// </remarks>
public sealed class HeatMapTests
{
    private const string OverIncidents =
        """
        {
          "type": "CIMHeatMapRenderer",
          "field": "agirlik",
          "radius": 12,
          "heading": "yogunluk",
          "maxPixelIntensity": 40,
          "colorScheme": {
            "type": "CIMLinearContinuousColorRamp",
            "fromColor": { "type": "CIMRGBColor", "values": [0, 0, 255, 100] },
            "toColor":   { "type": "CIMRGBColor", "values": [255, 0, 0, 100] }
          }
        }
        """;

    [Fact]
    public void The_projection_carries_a_surface_and_no_classes_to_speak_of()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(OverIncidents)!);

        Assert.Equal(Cim.HeatMap, projection.Kind);

        CimHeat surface = Assert.IsType<CimHeat>(projection.Heat);

        Assert.Equal("agirlik", surface.Field);
        Assert.Equal(12.0, surface.Radius);
        Assert.Equal(40.0, surface.Ceiling);
        Assert.Equal(2, surface.Ramp.Count);
        Assert.Equal(new Rgba(0, 0, 255, 255), surface.Ramp[0]);
        Assert.Equal(new Rgba(255, 0, 0, 255), surface.Ramp[^1]);
    }

    [Fact]
    public void The_tile_face_is_a_heatmap_layer_and_the_ramp_is_its_own_density()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(OverIncidents)!, "olaylar");

        JsonObject layer = (JsonObject)((JsonArray)derived.Style["layers"]!).Single()!;

        Assert.Equal("heatmap", (string?)layer["type"]);

        JsonObject paint = (JsonObject)layer["paint"]!;

        Assert.Equal(12.0, (double?)paint["heatmap-radius"]);
        Assert.Equal("agirlik", (string?)((JsonArray)paint["heatmap-weight"]!)[1]);

        JsonArray colour = (JsonArray)paint["heatmap-color"]!;

        // <b>`heatmap-density` is neither a field nor the zoom.</b> It is the surface's own value
        // at a pixel, which does not exist until every feature has been read — so this is the one
        // paint expression in the whole style that cannot be evaluated per feature.
        Assert.Equal("interpolate", (string?)colour[0]);
        Assert.Equal("heatmap-density", (string?)((JsonArray)colour[2]!)[0]);
        Assert.Equal("#0000ff", (string?)colour[4]);
        Assert.Equal("#ff0000", (string?)colour[^1]);

        // The ceiling has no MapLibre property of its own; intensity is the same lever.
        Assert.Equal(1 / 40.0, (double?)paint["heatmap-intensity"]);
    }

    [Fact]
    public void The_ArcGIS_face_publishes_the_kernel_density_properties_not_the_deprecated_ones()
    {
        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(OverIncidents)!, "olaylar");

        JsonObject renderer = (JsonObject)drawing.DrawingInfo["renderer"]!;

        Assert.Equal("heatmap", (string?)renderer["type"]);
        Assert.Equal("agirlik", (string?)renderer["field"]);

        // <b>`radius` and `maxDensity`.</b> The web map specification marks `blurRadius` and
        // `maxPixelIntensity` deprecated — they belong to the older Gaussian-blur heat map — and
        // this server computes a kernel density, so it publishes the pair that means that.
        Assert.Equal(12.0, (double?)renderer["radius"]);
        Assert.Equal(40.0, (double?)renderer["maxDensity"]);
        Assert.Null(renderer["blurRadius"]);

        JsonArray stops = (JsonArray)renderer["colorStops"]!;

        Assert.Equal(0.0, (double?)stops[0]!["ratio"]);
        Assert.Equal(1.0, (double?)stops[^1]!["ratio"]);
    }

    [Fact]
    public void A_surface_with_no_ceiling_says_its_tiles_are_not_comparable()
    {
        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(
                OverIncidents.Replace(
                    "\"maxPixelIntensity\": 40,", "", StringComparison.Ordinal))!,
            "olaylar");

        // <b>Not an error, and not silence either.</b> Without a fixed ceiling every image scales
        // against its own densest pixel, so a client holding two tiles of this layer is holding
        // two different scales — which is the document's choice and the reader's business.
        Assert.Contains(
            drawing.Losses,
            l => l.Contains("not comparable", StringComparison.Ordinal));
    }

    [Fact]
    public void The_style_reads_back_into_a_plan_that_carries_the_ramp_and_widens_the_margin()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(OverIncidents)!, "olaylar");

        SymbologyPlan plan = SymbologyPlan.Compile(derived.Style.ToJsonString());

        PlanLayer.Heat heat = Assert.IsType<PlanLayer.Heat>(Assert.Single(plan.Layers));

        Assert.Equal(2, heat.Ramp.Count);
        Assert.Equal(40.0, heat.Ceiling);

        // <b>The margin is the whole reason tiles join.</b> A point half a radius outside the
        // image still lights pixels inside it, so the reader has to fetch beyond what it draws.
        Assert.True(
            plan.Margin >= 12,
            $"The plan asks for {plan.Margin} pixels of margin and the heat spreads 12. Every "
            + "tile boundary would show a seam.");
    }

    [Fact]
    public void A_surface_is_denser_where_the_points_are_and_transparent_where_they_are_not()
    {
        // <b>The measurement, not the assertion.</b> Twenty points in one corner and one in the
        // other: the corner with twenty has to come out both more opaque and further along the
        // ramp than the corner with one, and the middle has to stay empty.
        HeatField field = new(200, 200);

        for (int i = 0; i < 20; i++)
        {
            field.Add(40 + (i % 5), 40 + (i / 5), 15, 1);
        }

        field.Add(160, 160, 15, 1);

        Counting canvas = new(200, 200);

        field.Paint(canvas, [new Rgba(0, 0, 255, 255), new Rgba(255, 0, 0, 255)], null, 1);

        Rgba crowded = canvas.At(42, 42);
        Rgba lonely = canvas.At(160, 160);
        Rgba between = canvas.At(100, 100);

        Assert.True(between.A == 0, $"The empty middle is {between}, and nothing is there.");

        Assert.True(
            crowded.A > lonely.A,
            $"Twenty points give alpha {crowded.A} and one gives {lonely.A}. A density surface "
            + "that does not get more opaque where the data is dense is not one.");

        Assert.True(
            crowded.R > lonely.R,
            $"Twenty points sit at red {crowded.R} on a blue-to-red ramp and one sits at "
            + $"{lonely.R}. The ramp is supposed to run with the density.");
    }

    [Fact]
    public void The_heat_falls_off_with_distance_rather_than_stopping_at_an_edge()
    {
        // <b>This test exists because its absence was measured.</b> The first version of this
        // class asserted that a crowded corner is hotter than a lonely one and that the middle
        // is empty — all three of which stay true if the kernel is replaced by a flat disc, and
        // the falsification proved it by passing. A pile of hard-edged discs is what a heat map
        // looks like when it is wrong, so this asserts the one thing that tells them apart: one
        // point, and the middle of its circle is hotter than the rim.
        HeatField field = new(100, 100);

        field.Add(50, 50, 30, 1);

        Counting canvas = new(100, 100);

        field.Paint(canvas, [new Rgba(0, 0, 255, 255), new Rgba(255, 0, 0, 255)], null, 1);

        Rgba middle = canvas.At(50, 50);
        Rgba rim = canvas.At(50 + 26, 50);

        Assert.True(
            middle.R > rim.R + 100,
            $"The centre reads red {middle.R} and a point near the rim reads {rim.R}. "
            + "Epanechnikov's kernel is 1 at the centre and 0.25 at 0.87 of the radius; a flat "
            + "disc gives the same value at both, which is a pile of circles rather than a "
            + "surface.");

        Assert.True(
            rim.A > 0,
            "The rim is fully transparent, so the kernel stopped before the radius did.");
    }

    [Fact]
    public void A_point_outside_the_image_still_lights_the_pixels_inside_it()
    {
        // <b>This is the seam test.</b> A point ten pixels off the left edge with a radius of
        // thirty lights the first twenty columns; dropping it because its centre is outside is
        // how every tile boundary in a heat map gets a visible line down it.
        HeatField field = new(100, 100);

        field.Add(-10, 50, 30, 1);

        Assert.False(field.IsEmpty, "A point outside the image contributed nothing.");
        Assert.True(field.Peak > 0);
    }

    [Fact]
    public void A_fixed_ceiling_makes_two_images_comparable_and_the_peak_does_not()
    {
        HeatField busy = new(100, 100);
        HeatField quiet = new(100, 100);

        for (int i = 0; i < 10; i++)
        {
            busy.Add(50, 50, 20, 1);
        }

        quiet.Add(50, 50, 20, 1);

        Counting one = new(100, 100);
        Counting two = new(100, 100);

        Rgba[] ramp = [new Rgba(0, 0, 255, 255), new Rgba(255, 0, 0, 255)];

        // Against their own peaks, the quiet image looks exactly as hot as the busy one.
        busy.Paint(one, ramp, null, 1);
        quiet.Paint(two, ramp, null, 1);

        Assert.Equal(one.At(50, 50).R, two.At(50, 50).R);

        // Against a fixed ceiling, they do not.
        Counting three = new(100, 100);
        Counting four = new(100, 100);

        busy.Paint(three, ramp, 10, 1);
        quiet.Paint(four, ramp, 10, 1);

        Assert.True(
            three.At(50, 50).R > four.At(50, 50).R,
            $"With a ceiling of 10, ten points read {three.At(50, 50).R} and one reads "
            + $"{four.At(50, 50).R}. A fixed scale is the whole point of `maxPixelIntensity`.");
    }

    /// <summary>A canvas that keeps the one image it is given.</summary>
    private sealed class Counting(int width, int height) : IMapCanvas
    {
        private Rgba[] _pixels = [];

        public int Width => width;

        public int Height => height;

        public Rgba At(int x, int y) =>
            _pixels.Length == 0 ? Rgba.Transparent : _pixels[(y * width) + x];

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

        public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y) =>
            new(x, y, x, y);

        public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y)
        {
        }

        public void DrawImage(ReadOnlySpan<Rgba> pixels, int imageWidth, int imageHeight, PixelBox destination)
        {
            _pixels = pixels.ToArray();
        }

        public byte[] Encode(MapImageFormat format, int quality) => [];

        public void Dispose()
        {
        }
    }
}
