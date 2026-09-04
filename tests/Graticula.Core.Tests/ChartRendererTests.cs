using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A small chart at each feature, instead of a symbol.
/// </summary>
/// <remarks>
/// <b>ADR-052 §3.16, the last of the three that were called blocked and were not.</b> A wedge
/// tessellated into a ring is a polygon and `FillArea` fills polygons. What the tests are for is
/// the arithmetic: shares that add to the whole, a start at twelve o'clock, and a circle whose
/// corners are below what antialiasing shows.
/// </remarks>
public sealed class ChartRendererTests
{
    private const string ByLanguage =
        """
        {
          "type": "CIMChartRenderer",
          "fieldNames": ["turk", "kurt", "arap"],
          "label": "diller",
          "preventChartOverlap": true,
          "colorRamp": {
            "type": "CIMFixedColorRamp",
            "colors": [
              { "type": "CIMRGBColor", "values": [220, 50, 40, 100] },
              { "type": "CIMRGBColor", "values": [40, 90, 200, 100] },
              { "type": "CIMRGBColor", "values": [240, 200, 60, 100] }
            ]
          },
          "baseSymbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [235, 235, 235, 100] } }] } }
        }
        """;

    private static Polygon Square() =>
        new(new LinearRing(XySequence.Wrap([0, 0, 100, 0, 100, 100, 0, 100, 0, 0])));

    [Fact]
    public void The_projection_carries_the_slices_and_reports_what_it_cannot_place()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(ByLanguage)!);

        Assert.Equal(Cim.Chart, projection.Kind);

        CimPie pie = Assert.IsType<CimPie>(projection.Pie);

        Assert.Equal(["turk", "kurt", "arap"], pie.Fields);
        Assert.Equal(3, pie.Colours.Count);
        Assert.NotNull(pie.Base);

        // <b>A real limit, said out loud.</b> Moving a chart away from the feature it describes
        // without saying so is worse than letting two neighbours overlap.
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("sit on each other", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bar_chart_is_reported_because_it_says_a_different_thing_from_a_pie()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(
            ByLanguage.Replace(
                "\"label\": \"diller\",",
                """
                "label": "diller",
                "chartSymbol": { "symbol": { "type": "CIMBarChartSymbol" } },
                """,
                StringComparison.Ordinal))!);

        // A bar chart compares magnitudes and a pie compares shares of a whole. Drawing one as
        // the other is not a smaller picture; it is a different claim, so it is reported.
        Assert.Contains(
            projection.NotDrawn,
            l => l.Contains("CIMBarChartSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public void The_faces_publish_what_each_can_and_say_what_it_cannot()
    {
        DerivedStyle tiles = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(ByLanguage)!, "iller");

        // MapLibre has no chart layer, so the tile face is a circle and a sentence.
        Assert.Equal(
            "circle",
            (string?)((JsonArray)tiles.Style["layers"]!).Single()!["type"]);

        Assert.Contains(
            tiles.Losses,
            l => l.Contains("MapLibre has no layer type for one", StringComparison.Ordinal));

        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(ByLanguage)!, "iller");

        JsonObject renderer = (JsonObject)drawing.DrawingInfo["renderer"]!;

        Assert.Equal("pieChart", (string?)renderer["type"]);

        JsonArray attributes = (JsonArray)renderer["attributes"]!;

        Assert.Equal(3, attributes.Count);
        Assert.Equal("turk", (string?)attributes[0]!["field"]);
        Assert.Equal(220, (int?)((JsonArray)attributes[0]!["color"]!)[0]);
    }

    // ---------------------------------------------------------------- the geometry itself

    [Fact]
    public void The_wedges_add_up_to_a_whole_circle_and_start_at_twelve_o_clock()
    {
        PixelPath path = new();

        IReadOnlyList<int> figures = PieSlices.Wedges(path, [1, 1, 2], 100, 100, 50);

        Assert.Equal(3, figures.Count);
        Assert.Equal(3, path.Figures.Count);

        // <b>Twelve o'clock.</b> The first slice's first point is directly above the centre; a
        // pie that starts at three o'clock is a plausible chart of a different arrangement, and
        // nothing on screen says which one it is.
        PixelPath.Figure first = path.Figures[figures[0]];

        Assert.Equal(100, path.Coordinates[first.Start * 2], 6);
        Assert.Equal(50, path.Coordinates[(first.Start * 2) + 1], 6);

        // Shares, measured as area: 1, 1 and 2 of 4 is a quarter, a quarter and a half.
        double[] areas = [.. figures.Select(f => Math.Abs(Area(path, f)))];
        double whole = areas.Sum();
        double circle = Math.PI * 50 * 50;

        // <b>Half a percent, and the shortfall is the polygon inside the circle.</b> Every wedge
        // is inscribed, so the tessellation is always a little short; what matters is that the
        // slices are the whole of the pie between them, and the shares below are exact whatever
        // the segment count. Roundness is measured separately, and by sagitta rather than area.
        Assert.True(
            Math.Abs(whole - circle) / circle < 0.005,
            $"Three wedges cover {whole:0.#} and the circle is {circle:0.#}. The slices are "
            + "supposed to be the whole of it.");

        Assert.Equal(0.25, areas[0] / whole, 3);
        Assert.Equal(0.25, areas[1] / whole, 3);
        Assert.Equal(0.50, areas[2] / whole, 3);
    }

    [Fact]
    public void It_goes_clockwise_because_that_is_what_a_pie_chart_does()
    {
        PixelPath path = new();

        PieSlices.Wedges(path, [1, 1, 1, 1], 0, 0, 10);

        // Quarter past twelve is to the RIGHT of the centre on screen. Screen y grows downward,
        // so a pie built with the mathematician's positive direction is mirrored — a mistake
        // that looks like a chart of a different arrangement rather than like a bug.
        PixelPath.Figure first = path.Figures[0];
        int last = first.Start + first.Count - 2;

        Assert.True(
            path.Coordinates[last * 2] > 0,
            "The first quarter ends to the left of the centre, so the pie runs anticlockwise.");
    }

    [Fact]
    public void A_slice_of_nothing_gets_no_wedge_and_the_colours_stay_lined_up()
    {
        PixelPath path = new();

        // The middle field is zero. Its wedge must be absent AND the third field must still be
        // told it is the third, or every colour after a zero lands on the wrong slice.
        IReadOnlyList<int> figures = PieSlices.Wedges(path, [3, 0, 1], 50, 50, 20);

        Assert.True(figures[0] >= 0);
        Assert.Equal(-1, figures[1]);
        Assert.True(figures[2] >= 0);
        Assert.Equal(2, path.Figures.Count);

        Assert.Equal(0.75, Math.Abs(Area(path, figures[0])) / (Math.PI * 400), 2);
    }

    [Fact]
    public void A_feature_whose_values_are_all_zero_draws_no_chart_at_all()
    {
        PixelPath path = new();

        Assert.All(PieSlices.Wedges(path, [0, 0, 0], 50, 50, 20), f => Assert.Equal(-1, f));
        Assert.True(path.IsEmpty);
    }

    [Fact]
    public void A_doughnut_leaves_its_hole_empty()
    {
        PixelPath path = new();

        PieSlices.Wedges(path, [1], 100, 100, 50, hole: 0.5);

        // One ring, and its area is the band rather than the disc: pi(50^2 - 25^2).
        double area = Math.Abs(Area(path, 0));
        double band = Math.PI * ((50 * 50) - (25 * 25));

        Assert.True(
            Math.Abs(area - band) / band < 0.002,
            $"The ring covers {area:0.#} and the band is {band:0.#}.");
    }

    [Fact]
    public void A_bigger_pie_is_drawn_with_more_segments_so_its_edge_stays_round()
    {
        PixelPath small = new();
        PixelPath large = new();

        PieSlices.Wedges(small, [1], 0, 0, 6);
        PieSlices.Wedges(large, [1], 0, 0, 300);

        // <b>Sagitta, not area, and the difference matters.</b> A twenty-five-sided polygon
        // inscribed in a six-pixel circle is 1% short in AREA and 0.05 pixels short at the
        // middle of each chord — and the second number is the one an eye can see. Asserting the
        // area would either fail a pie that is visibly round or pass one that is not, depending
        // on the radius, which is how a tolerance ends up being tuned until it stops complaining.
        foreach ((PixelPath path, double radius) in
            (IEnumerable<(PixelPath, double)>)[(small, 6.0), (large, 300.0)])
        {
            double worst = Sagitta(path, 0, radius);

            Assert.True(
                worst < 0.5,
                $"A pie of radius {radius} has a chord {worst:0.###} pixels inside its own arc. "
                + "Above half a pixel the corners show.");
        }

        Assert.True(
            large.Figures[0].Count > small.Figures[0].Count,
            "The larger pie is drawn with no more points than the small one, so one of them is "
            + "wrong: either the small one is wasteful or the large one is a polygon.");
    }

    [Fact]
    public void A_chart_sized_by_its_sum_uses_the_same_curve_as_a_proportional_symbol()
    {
        // <b>Deliberately the same law.</b> A chart sized by its sum is a proportional symbol
        // whose symbol happens to be a chart; two renderers answering "how big is this"
        // differently would be two answers to one question.
        double four = PieSlices.SizeFor(400, 100, 10, flannery: false);

        Assert.Equal(20, four, 6);

        double bent = PieSlices.SizeFor(400, 100, 10, flannery: true);

        Assert.Equal(10 * Math.Pow(4, 0.5716), bent, 6);
        Assert.True(bent > four);

        // And a total below the smallest value never shrinks below the smallest size.
        Assert.Equal(10, PieSlices.SizeFor(1, 100, 10, flannery: false), 6);
    }

    [Fact]
    public void The_renderer_draws_one_filled_wedge_per_slice_with_the_slice_s_colour()
    {
        SymbologyPlan plan = SymbologyPlan.Compile(ByLanguage);

        PlanLayer.Pie pie = Assert.IsType<PlanLayer.Pie>(Assert.Single(plan.Layers));

        Assert.Equal(["turk", "kurt", "arap"], pie.Sliced);
        Assert.NotNull(pie.Under);

        Fills canvas = new(200, 200);
        MapRenderer renderer = new(
            canvas, new PixelTransform(new Envelope(0, 0, 100, 100), 200, 200), geographic: false);

        renderer.Draw(plan, [Feature(60, 30, 10)]);

        // The base fill, then one wedge per slice, each in its own colour.
        Assert.Equal(4, canvas.Colours.Count);
        Assert.Equal(new Rgba(235, 235, 235, 255), canvas.Colours[0]);
        Assert.Equal(new Rgba(220, 50, 40, 255), canvas.Colours[1]);
        Assert.Equal(new Rgba(40, 90, 200, 255), canvas.Colours[2]);
        Assert.Equal(new Rgba(240, 200, 60, 255), canvas.Colours[3]);
    }

    [Fact]
    public void A_feature_with_nothing_to_chart_draws_nothing_including_its_base()
    {
        SymbologyPlan plan = SymbologyPlan.Compile(ByLanguage);

        Fills canvas = new(200, 200);
        MapRenderer renderer = new(
            canvas, new PixelTransform(new Envelope(0, 0, 100, 100), 200, 200), geographic: false);

        renderer.Draw(plan, [Feature(0, 0, 0)]);

        // <b>The base goes with the chart.</b> A grey polygon with no chart on it says the
        // district exists and has no data, which is true — but this renderer's whole subject is
        // the chart, and a reader seeing only bases would think the charts had failed to draw.
        Assert.Empty(canvas.Colours);
    }

    private static Feature Feature(int turk, int kurt, int arap) =>
        new("1", Square(), new FeatureSchema(["turk", "kurt", "arap"]), [turk, kurt, arap]);

    /// <summary>
    /// The furthest any chord of a figure falls inside the circle it approximates.
    /// </summary>
    /// <remarks>
    /// <b>What an eye actually sees of a tessellated arc.</b> A polygon inscribed in a circle is
    /// short by r(1 - cos(θ/2)) at the middle of every chord; below half a pixel antialiasing
    /// hides it and the edge reads as round whatever the area says.
    /// </remarks>
    private static double Sagitta(PixelPath path, int figure, double radius)
    {
        PixelPath.Figure one = path.Figures[figure];
        double worst = 0;

        for (int i = 0; i < one.Count - 1; i++)
        {
            int a = one.Start + i;
            int b = one.Start + i + 1;

            double ax = path.Coordinates[a * 2];
            double ay = path.Coordinates[(a * 2) + 1];
            double bx = path.Coordinates[b * 2];
            double by = path.Coordinates[(b * 2) + 1];

            // Only the chords that are actually on the arc: a wedge also has two radii, whose
            // midpoints sit well inside the circle and are not an approximation of anything.
            if (Math.Abs(Math.Sqrt((ax * ax) + (ay * ay)) - radius) > 0.001
                || Math.Abs(Math.Sqrt((bx * bx) + (by * by)) - radius) > 0.001)
            {
                continue;
            }

            double midX = (ax + bx) / 2;
            double midY = (ay + by) / 2;

            worst = Math.Max(worst, radius - Math.Sqrt((midX * midX) + (midY * midY)));
        }

        return worst;
    }

    /// <summary>The shoelace area of one figure of a path.</summary>
    private static double Area(PixelPath path, int figure)
    {
        PixelPath.Figure one = path.Figures[figure];
        double sum = 0;

        for (int i = 0; i < one.Count; i++)
        {
            int a = one.Start + i;
            int b = one.Start + ((i + 1) % one.Count);

            sum += (path.Coordinates[a * 2] * path.Coordinates[(b * 2) + 1])
                - (path.Coordinates[b * 2] * path.Coordinates[(a * 2) + 1]);
        }

        return sum / 2;
    }

    /// <summary>A canvas that remembers the colours it was asked to fill with.</summary>
    private sealed class Fills(int width, int height) : IMapCanvas
    {
        public List<Rgba> Colours { get; } = [];

        public int Width => width;

        public int Height => height;

        public void Clear(Rgba colour)
        {
        }

        public void FillArea(PixelPath path, MapSymbol.Area symbol) =>
            Colours.Add(symbol.Colour);

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

        public void DrawImage(
            ReadOnlySpan<Rgba> pixels, int imageWidth, int imageHeight, PixelBox destination)
        {
        }

        public byte[] Encode(MapImageFormat format, int quality) => [];

        public void Dispose()
        {
        }
    }
}
