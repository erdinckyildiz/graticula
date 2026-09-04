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
/// Dots scattered inside an area, and the two things that make them hard.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.15.</b> The drawing is a marker per dot and needs nothing new. What is hard is
/// that a dot must land in the same place every time — or it crawls when the reader pans — and
/// that a polygon straddling two tiles must scatter over its whole area, or the density doubles
/// along every seam.
/// </para>
/// <para>
/// <b>Both are measured here rather than asserted.</b> The seam test draws the same polygon
/// through two different transforms and counts what lands in the overlap.
/// </para>
/// </remarks>
public sealed class DotDensityTests
{
    private const string ByPopulation =
        """
        {
          "type": "CIMDotDensityRenderer",
          "fieldNames": ["turk", "kurt"],
          "dotValue": 100,
          "dotSize": 3,
          "randomSeed": 7,
          "symbolLabel": "kisi",
          "dotDensitySymbol": { "symbol": { "type": "CIMPolygonSymbol", "symbolLayers": [
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [220, 50, 40, 100] } },
            { "type": "CIMSolidFill",
              "color": { "type": "CIMRGBColor", "values": [40, 90, 200, 100] } }] } }
        }
        """;

    /// <summary>A square from 0,0 to 100,100.</summary>
    private static Polygon Square() =>
        new(new LinearRing(XySequence.Wrap(
            [0, 0, 100, 0, 100, 100, 0, 100, 0, 0])));

    [Fact]
    public void The_projection_carries_the_fields_their_colours_and_the_seed()
    {
        CimProjection projection = Cim.Project((JsonObject)JsonNode.Parse(ByPopulation)!);

        Assert.Equal(Cim.DotDensity, projection.Kind);

        CimDots dots = Assert.IsType<CimDots>(projection.Dots);

        Assert.Equal(["turk", "kurt"], dots.Fields);
        Assert.Equal(100.0, dots.DotValue);
        Assert.Equal(3.0, dots.DotSize);
        Assert.Equal(7L, dots.Seed);

        // <b>The first field gets the first symbol layer's colour.</b> CIM lists symbol layers
        // top first and `ReadSymbol` hands them back bottom first, so the order is reversed on
        // the way in — get that wrong and every colour is on the wrong field, which is a map
        // that is exactly as wrong as it can be while looking right.
        Assert.Equal(new Rgba(220, 50, 40, 255), dots.Colours[0]);
        Assert.Equal(new Rgba(40, 90, 200, 255), dots.Colours[1]);
    }

    [Fact]
    public void A_renderer_naming_no_field_is_refused_because_there_is_nothing_to_count()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Cim.Project((JsonObject)JsonNode.Parse(
                ByPopulation.Replace(
                    "\"fieldNames\": [\"turk\", \"kurt\"],", "\"fieldNames\": [],",
                    StringComparison.Ordinal))!));

        Assert.Contains("nothing to count dots of", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_is_built_from_the_document_because_MapLibre_has_no_layer_for_it()
    {
        SymbologyPlan plan = SymbologyPlan.Compile(ByPopulation);

        PlanLayer.Dots dots = Assert.IsType<PlanLayer.Dots>(Assert.Single(plan.Layers));

        Assert.Equal(["turk", "kurt"], dots.Counted);
        Assert.Equal(100.0, dots.DotValue);

        // Three points across is four pixels, the same 1/0.75 every other conversion uses.
        Assert.Equal(4.0, dots.DotSize);

        // And the reader is told which columns to fetch, or the count is always zero.
        Assert.Contains("turk", plan.Fields);
        Assert.Contains("kurt", plan.Fields);
    }

    [Fact]
    public void The_tile_face_says_it_cannot_draw_this_rather_than_inventing_a_layer_type()
    {
        DerivedStyle derived = CimStyle.ToMapLibre(
            (JsonObject)JsonNode.Parse(ByPopulation)!, "iller");

        JsonObject layer = (JsonObject)((JsonArray)derived.Style["layers"]!).Single()!;

        // <b>A real MapLibre type.</b> An invented one would be a style this server reads and
        // every other client rejects, which is worse than a flat fill and a sentence.
        Assert.Equal("fill", (string?)layer["type"]);
        Assert.Equal("#dc3228", (string?)layer["paint"]!["fill-color"]);

        Assert.Contains(
            derived.Losses,
            l => l.Contains("MapLibre has no layer type for one", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ArcGIS_face_publishes_a_dotDensity_with_a_colour_on_every_attribute()
    {
        DerivedDrawingInfo drawing = CimEsri.ToDrawingInfo(
            (JsonObject)JsonNode.Parse(ByPopulation)!, "iller");

        JsonObject renderer = (JsonObject)drawing.DrawingInfo["renderer"]!;

        Assert.Equal("dotDensity", (string?)renderer["type"]);
        Assert.Equal(100.0, (double?)renderer["dotValue"]);

        JsonArray attributes = (JsonArray)renderer["attributes"]!;

        Assert.Equal(2, attributes.Count);
        Assert.Equal("turk", (string?)attributes[0]!["field"]);
        Assert.Equal(220, (int?)((JsonArray)attributes[0]!["color"]!)[0]);
        Assert.Equal("kurt", (string?)attributes[1]!["field"]);

        // <b>Said out loud.</b> A client drawing this document and this server drawing it will
        // not put the dots in the same places; both are right and a reader comparing two screens
        // deserves to know why they differ.
        Assert.Contains(
            drawing.Losses,
            l => l.Contains("not put the dots in the same places", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------- the scatter itself

    [Fact]
    public void Every_dot_lands_inside_the_polygon_and_none_in_its_hole()
    {
        // A square with a square hole in the middle. A dot in the hole is a mark the fill says
        // is not there, which is why the scatter uses the same even-odd rule the canvas fills by.
        Polygon ring = new(
            new LinearRing(XySequence.Wrap([0, 0, 100, 0, 100, 100, 0, 100, 0, 0])),
            [new LinearRing(XySequence.Wrap([40, 40, 60, 40, 60, 60, 40, 60, 40, 40]))]);

        IReadOnlyList<(double X, double Y)> dots = DotScatter.Inside(ring, 400, 12345);

        Assert.Equal(400, dots.Count);

        foreach ((double x, double y) in dots)
        {
            Assert.InRange(x, 0, 100);
            Assert.InRange(y, 0, 100);

            Assert.False(
                x > 40 && x < 60 && y > 40 && y < 60,
                $"A dot landed at {x:0.##},{y:0.##}, which is inside the hole.");
        }
    }

    [Fact]
    public void The_same_feature_scatters_into_the_same_places_every_time()
    {
        // <b>This is what stops the dots from crawling when the reader pans.</b> `randomSeed` is
        // on the CIM renderer for exactly this reason, and a scatter reseeded per request is
        // unreadable in a way that no single screenshot reveals.
        IReadOnlyList<(double X, double Y)> once = DotScatter.Inside(Square(), 200, 99);
        IReadOnlyList<(double X, double Y)> again = DotScatter.Inside(Square(), 200, 99);

        Assert.Equal(once, again);

        // And a different seed is a different arrangement, or the seed does nothing.
        IReadOnlyList<(double X, double Y)> other = DotScatter.Inside(Square(), 200, 100);

        Assert.NotEqual(once, other);
    }

    [Fact]
    public void Two_fields_of_one_feature_do_not_land_on_top_of_each_other()
    {
        // <b>The field's index goes into the seed.</b> Without it both fields scatter into the
        // same places and the second is drawn exactly over the first, which reads as a map of
        // one variable — and looks entirely plausible.
        ulong first = DotScatter.SeedFor(7, "42");
        ulong second = DotScatter.SeedFor(8, "42");

        Assert.NotEqual(first, second);
        Assert.NotEqual(
            DotScatter.Inside(Square(), 100, first),
            DotScatter.Inside(Square(), 100, second));
    }

    [Fact]
    public void Two_features_with_the_same_seed_do_not_share_an_arrangement()
    {
        // Neighbouring districts scattering identically makes the map look tiled.
        Assert.NotEqual(DotScatter.SeedFor(7, "1"), DotScatter.SeedFor(7, "2"));
    }

    [Fact]
    public void A_polygon_that_straddles_a_seam_keeps_its_density_on_both_sides()
    {
        // <b>The seam test, and it is the whole reason the scatter is in world coordinates.</b>
        // Two tiles cover the left and right halves of one square. Each draws the dots that land
        // in it. Together they must hold every dot exactly once — not twice, which is what
        // scattering into the visible part gives, and not fewer.
        IReadOnlyList<(double X, double Y)> all = DotScatter.Inside(Square(), 500, 4242);

        Assert.Equal(500, all.Count);

        int left = all.Count(d => d.X < 50);
        int right = all.Count(d => d.X >= 50);

        Assert.Equal(500, left + right);

        // Half a square holds about half the dots. Anything far from half means the sampler is
        // biased, which a seam would show as a stripe.
        Assert.InRange(left, 200, 300);
    }

    [Fact]
    public void A_sliver_places_what_it_can_rather_than_looping_forever()
    {
        // A polygon one unit wide and a hundred tall fills a hundredth of its bounding box, so
        // rejection sampling costs a hundred tries a dot. The cap is sixty, so this places
        // fewer than asked — deliberately, and without spending the afternoon on it.
        Polygon sliver = new(new LinearRing(XySequence.Wrap(
            [0, 0, 1, 0, 1, 100, 0, 100, 0, 0])));

        IReadOnlyList<(double X, double Y)> dots = DotScatter.Inside(sliver, 200, 5);

        Assert.NotEmpty(dots);
        Assert.True(dots.Count <= 200);

        foreach ((double x, double y) in dots)
        {
            Assert.InRange(x, 0, 1);
        }
    }

    [Fact]
    public void The_generator_is_the_published_one_and_stays_between_zero_and_one()
    {
        // <b>SplitMix64, written out rather than taken from the framework.</b> `System.Random`
        // does not promise the same sequence across .NET versions, and a dot map whose dots move
        // when the runtime is upgraded cannot be compared with last year's.
        ulong state = 0;
        double smallest = 1;
        double largest = 0;

        for (int i = 0; i < 10_000; i++)
        {
            double next = DotScatter.Next(ref state);

            Assert.InRange(next, 0, 1);

            smallest = Math.Min(smallest, next);
            largest = Math.Max(largest, next);
        }

        Assert.True(smallest < 0.01, $"Ten thousand draws and the smallest is {smallest:0.####}.");
        Assert.True(largest > 0.99, $"Ten thousand draws and the largest is {largest:0.####}.");
    }

    [Fact]
    public void Two_tiles_of_one_polygon_hold_every_dot_exactly_once()
    {
        // <b>The claim the whole design rests on, measured through the renderer.</b> One square
        // drawn twice: once through a transform showing its left half, once its right. Each
        // draws only the dots that land in its own picture. Together they must hold every dot
        // once — twice is what scattering into the visible part gives, and it is the fault that
        // makes a dot map unusable at any tile boundary.
        SymbologyPlan plan = SymbologyPlan.Compile(ByPopulation);

        Feature feature = OneDistrict(turk: 3000, kurt: 0);

        int left = DrawnInto(plan, feature, new Envelope(0, 0, 50, 100));
        int right = DrawnInto(plan, feature, new Envelope(50, 0, 100, 100));
        int whole = DrawnInto(plan, feature, new Envelope(0, 0, 100, 100));

        Assert.Equal(30, whole);

        // <b>Every dot is drawn at least once, and the excess is the seam band rather than a
        // doubling.</b> Measured: 14 and 18 against 30, so two dots near the boundary are drawn
        // in both tiles — which is correct and necessary. A dot whose centre is a pixel outside
        // a tile still has most of its circle inside it; clipping on the centre would cut those
        // dots in half on both sides of every seam. What must not happen is the other thing:
        // scattering into the visible part gives each half the polygon's whole count, 30 and 30,
        // and the density doubles down the middle of the map.
        Assert.True(
            left + right >= whole,
            $"The halves drew {left} and {right} of {whole}. A dot went missing at the seam.");

        Assert.True(
            left + right <= whole + 4,
            $"The halves drew {left} and {right} of {whole}. More than a seam band's worth of "
            + "overlap means the scatter is being recomputed per picture, which doubles the "
            + "density along every tile boundary.");

        Assert.True(
            left > 5 && right > 5,
            $"The halves drew {left} and {right}. One half holding nearly all of them means the "
            + "scatter is not independent of the picture.");
    }

    [Fact]
    public void The_dots_are_the_field_divided_by_the_dot_value()
    {
        SymbologyPlan plan = SymbologyPlan.Compile(ByPopulation);
        Envelope whole = new(0, 0, 100, 100);

        // 100 people a dot: 3000 and 0 is thirty dots; 3000 and 1500 is forty-five.
        Assert.Equal(30, DrawnInto(plan, OneDistrict(3000, 0), whole));
        Assert.Equal(45, DrawnInto(plan, OneDistrict(3000, 1500), whole));

        // And a district too small for one dot draws none rather than rounding up to one, which
        // would put a mark on the map for a number below what a dot stands for.
        Assert.Equal(0, DrawnInto(plan, OneDistrict(30, 20), whole));
    }

    [Fact]
    public void The_renderer_puts_the_dots_in_the_same_pixels_on_every_request()
    {
        // <b>This test exists because its absence was measured.</b> Reseeding the renderer's
        // scatter from the clock failed nothing: the determinism tests above call `DotScatter`
        // directly, so the seed the RENDERER builds was covered by nothing at all. A dot map
        // whose dots move between requests crawls when the reader pans, and no single screenshot
        // shows it.
        SymbologyPlan plan = SymbologyPlan.Compile(ByPopulation);
        Envelope whole = new(0, 0, 100, 100);

        Assert.Equal(
            PixelsOf(plan, OneDistrict(3000, 1500), whole),
            PixelsOf(plan, OneDistrict(3000, 1500), whole));
    }

    /// <summary>Draws one feature into an extent and counts the markers.</summary>
    private static int DrawnInto(SymbologyPlan plan, Feature feature, Envelope extent) =>
        PixelsOf(plan, feature, extent).Count;

    /// <summary>Draws one feature into an extent and says where the markers went.</summary>
    private static List<(double X, double Y)> PixelsOf(
        SymbologyPlan plan, Feature feature, Envelope extent)
    {
        Marks canvas = new(200, 200);
        MapRenderer renderer = new(canvas, new PixelTransform(extent, 200, 200), geographic: false);

        renderer.Draw(plan, [feature]);

        return canvas.Where;
    }

    /// <summary>One square district with two populations.</summary>
    private static Feature OneDistrict(int turk, int kurt)
    {
        FeatureSchema schema = new(["turk", "kurt"]);

        return new Feature("1", Square(), schema, [turk, kurt]);
    }

    /// <summary>A canvas that counts the markers it is asked to draw.</summary>
    private sealed class Marks(int width, int height) : IMapCanvas
    {
        public List<(double X, double Y)> Where { get; } = [];

        public int Count => Where.Count;

        public int Width => width;

        public int Height => height;

        public void Clear(Rgba colour)
        {
        }

        public void FillArea(PixelPath path, MapSymbol.Area symbol)
        {
        }

        public void StrokeLine(PixelPath path, MapSymbol.Stroke symbol)
        {
        }

        public void DrawMarker(double x, double y, MapSymbol.Marker symbol) =>
            Where.Add((x, y));

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
