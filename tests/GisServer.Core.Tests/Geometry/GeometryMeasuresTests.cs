using System;
using GisServer.Geometries;
using Xunit;

namespace GisServer.Core.Tests.Geometry;

/// <summary>
/// Area, length and the point that goes inside a polygon.
/// </summary>
/// <remarks>
/// The shapes are chosen so the right answer can be worked out by hand. A test
/// that asserts what the code returned is a change detector; one that asserts
/// what the geometry is worth catches the code being wrong from the start.
/// </remarks>
public sealed class GeometryMeasuresTests
{
    private static LinearRing Ring(params (double X, double Y)[] points) =>
        new(Xy(points));

    private static XySequence Xy(params (double X, double Y)[] points)
    {
        double[] interleaved = new double[points.Length * 2];

        for (int i = 0; i < points.Length; i++)
        {
            interleaved[i * 2] = points[i].X;
            interleaved[(i * 2) + 1] = points[i].Y;
        }

        return XySequence.Wrap(interleaved);
    }

    /// <summary>A 100 by 100 square with its corner at the origin.</summary>
    private static LinearRing Square =>
        Ring((0, 0), (100, 0), (100, 100), (0, 100), (0, 0));

    /// <summary>A 20 by 20 square in the middle of it.</summary>
    private static LinearRing Hole =>
        Ring((40, 40), (60, 40), (60, 60), (40, 60), (40, 40));

    // ---------- area ----------

    [Fact]
    public void A_square_has_the_area_of_a_square()
    {
        Assert.Equal(10_000, GeometryMeasures.Area(new Polygon(Square)));
    }

    [Fact]
    public void A_hole_is_subtracted()
    {
        Assert.Equal(9_600, GeometryMeasures.Area(new Polygon(Square, [Hole])));
    }

    [Fact]
    public void Winding_does_not_change_the_area()
    {
        // The shoelace sum is signed and the sign is the winding. Returning a
        // negative area for a clockwise ring would be arithmetically honest and
        // useless: ArcGIS winds shells clockwise, so every polygon arriving
        // through the API would report negative area.
        Polygon clockwise = new(Ring((0, 0), (0, 100), (100, 100), (100, 0), (0, 0)));

        Assert.Equal(10_000, GeometryMeasures.Area(clockwise));
    }

    [Fact]
    public void A_line_has_no_area()
    {
        Assert.Equal(0, GeometryMeasures.Area(
            new LineString(Xy((0, 0), (10, 10)))));
    }

    [Fact]
    public void The_parts_of_a_multipolygon_add_up()
    {
        Polygon first = new(Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)));
        Polygon second = new(Ring((100, 100), (120, 100), (120, 120), (100, 120), (100, 100)));

        Assert.Equal(500, GeometryMeasures.Area(new MultiPolygon([first, second])));
    }

    // ---------- length ----------

    [Fact]
    public void A_squares_length_is_its_perimeter()
    {
        Assert.Equal(400, GeometryMeasures.Length(new Polygon(Square)));
    }

    [Fact]
    public void A_holes_boundary_is_part_of_the_perimeter()
    {
        // ArcGIS reports it this way, and a perimeter that ignored the interior
        // ring would understate the boundary of a field with a lake in it.
        Assert.Equal(480, GeometryMeasures.Length(new Polygon(Square, [Hole])));
    }

    [Fact]
    public void A_line_is_as_long_as_its_segments()
    {
        // 3-4-5, so the answer is checkable without a calculator.
        Assert.Equal(5, GeometryMeasures.Length(
            new LineString(Xy((0, 0), (3, 4)))));
    }

    [Fact]
    public void A_point_has_no_length()
    {
        Assert.Equal(0, GeometryMeasures.Length(new Point(1, 2)));
    }

    // ---------- label point ----------

    [Fact]
    public void A_label_point_sits_inside_a_square()
    {
        Point label = GeometryMeasures.LabelPoint(new Polygon(Square))!;

        Assert.InRange(label.X, 0, 100);
        Assert.InRange(label.Y, 0, 100);
    }

    [Fact]
    public void A_label_point_avoids_the_notch_of_a_C_shape()
    {
        // <b>The test the whole method exists for.</b> This C-shape's centroid
        // is near (43, 50), which is in the notch — outside the polygon. A label
        // placed there sits in the sea, which is the most visible way a map can
        // look wrong. PostGIS's ST_PointOnSurface gives (15, 50) for the same
        // shape.
        Polygon c = new(Ring(
            (0, 0), (100, 0), (100, 30), (30, 30), (30, 70), (100, 70), (100, 100), (0, 100), (0, 0)));

        Point label = GeometryMeasures.LabelPoint(c)!;

        Assert.True(
            label.X < 30,
            $"the label landed at x={label.X}, which is inside the notch between x=30 and x=100");
    }

    [Fact]
    public void A_label_point_avoids_a_hole()
    {
        // The hole is 40..60 in both axes and covers the middle of the square,
        // which is exactly where a naive answer would put the label.
        Point label = GeometryMeasures.LabelPoint(new Polygon(Square, [Hole]))!;

        bool insideHole = label.X is > 40 and < 60 && label.Y is > 40 and < 60;

        Assert.False(insideHole, $"the label landed in the hole at ({label.X}, {label.Y})");
    }

    [Fact]
    public void The_biggest_part_of_a_multipolygon_gets_the_label()
    {
        // A country with an island puts its name on the mainland, which is where
        // somebody reading the map looks for it.
        Polygon island = new(Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)));
        Polygon mainland = new(Ring((500, 500), (700, 500), (700, 700), (500, 700), (500, 500)));

        Point label = GeometryMeasures.LabelPoint(new MultiPolygon([island, mainland]))!;

        Assert.InRange(label.X, 500, 700);
    }

    [Fact]
    public void A_geometry_with_no_area_has_no_label_point()
    {
        // Null rather than an invented coordinate. There is nowhere inside a
        // line to put a label, and returning its midpoint would be a guess
        // indistinguishable from an answer.
        Assert.Null(GeometryMeasures.LabelPoint(new LineString(Xy((0, 0), (10, 10)))));
        Assert.Null(GeometryMeasures.LabelPoint(new Point(1, 2)));
    }

    [Fact]
    public void A_degenerate_polygon_gives_a_vertex_rather_than_a_nonsense_coordinate()
    {
        // Zero height: no interior for a scanline to find. A vertex is at least
        // a point the caller supplied.
        Polygon flat = new(Ring((0, 5), (100, 5), (100, 5), (0, 5), (0, 5)));

        Point label = GeometryMeasures.LabelPoint(flat)!;

        Assert.Equal(5, label.Y);
        Assert.False(double.IsNaN(label.X));
    }

    // ---------- the shape of the guarantee ----------

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(64)]
    public void A_label_point_is_inside_every_convex_polygon_tried(int sides)
    {
        // A regular polygon's label point must be inside it for any number of
        // sides. Cheap coverage of the scanline arithmetic against off-by-one
        // errors that only appear at particular vertex counts.
        (double, double)[] points = new (double, double)[sides + 1];

        for (int i = 0; i < sides; i++)
        {
            double angle = 2 * Math.PI * i / sides;
            points[i] = (100 + (50 * Math.Cos(angle)), 100 + (50 * Math.Sin(angle)));
        }

        // Copied, not recomputed. cos(2*pi) and cos(0) differ by an epsilon, so
        // computing the closing vertex leaves the ring a hair open — which
        // LinearRing correctly refuses, and which is how this test first failed.
        points[sides] = points[0];

        Point label = GeometryMeasures.LabelPoint(new Polygon(Ring(points)))!;

        double distance = Math.Sqrt(
            ((label.X - 100) * (label.X - 100)) + ((label.Y - 100) * (label.Y - 100)));

        Assert.True(distance < 50, $"the label is {distance:F1} from the centre of a radius-50 shape");
    }
}
