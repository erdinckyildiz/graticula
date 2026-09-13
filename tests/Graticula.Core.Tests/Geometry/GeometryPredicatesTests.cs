using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Geometry;

/// <summary>
/// ADR-066: the exact <c>intersects</c> a GeoParquet layer's candidates are checked with.
/// </summary>
/// <remarks>
/// <b>The cases a hand-written predicate gets wrong</b>: touching at one point, a line wholly
/// inside a polygon with no boundary crossing, a polygon inside another's hole, collinear
/// overlap, and the boxes-overlap-but-shapes-do-not case that a box test alone would answer yes.
/// PostGIS is the oracle for real data in <c>GeometryPredicatesAgainstPostgisTests</c>.
/// </remarks>
public sealed class GeometryPredicatesTests
{
    private static LinearRing Ring(params double[] xy) => new(XySequence.Wrap(xy));

    private static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        new(Ring(minX, minY, maxX, minY, maxX, maxY, minX, maxY, minX, minY));

    private static LineString Line(params double[] xy) => new(XySequence.Wrap(xy));

    [Fact]
    public void Points_intersect_only_when_equal()
    {
        Assert.True(GeometryPredicates.Intersects(new Point(1, 2), new Point(1, 2)));
        Assert.False(GeometryPredicates.Intersects(new Point(1, 2), new Point(1, 2.0000001)));
    }

    [Fact]
    public void A_point_on_a_polygon_edge_or_vertex_intersects_it()
    {
        Polygon square = Square(0, 0, 10, 10);

        Assert.True(GeometryPredicates.Intersects(new Point(5, 0), square));
        Assert.True(GeometryPredicates.Intersects(new Point(10, 10), square));
        Assert.True(GeometryPredicates.Intersects(new Point(5, 5), square));
        Assert.False(GeometryPredicates.Intersects(new Point(11, 5), square));
    }

    [Fact]
    public void A_point_in_a_hole_is_outside_and_on_the_hole_edge_is_on_the_boundary()
    {
        Polygon holed = new(
            Ring(0, 0, 10, 0, 10, 10, 0, 10, 0, 0),
            [Ring(4, 4, 6, 4, 6, 6, 4, 6, 4, 4)]);

        Assert.False(GeometryPredicates.Intersects(new Point(5, 5), holed));
        Assert.True(GeometryPredicates.Intersects(new Point(4, 5), holed));
        Assert.True(GeometryPredicates.Intersects(new Point(2, 2), holed));
    }

    [Fact]
    public void A_line_wholly_inside_a_polygon_intersects_it_without_crossing_its_boundary()
    {
        Assert.True(GeometryPredicates.Intersects(Line(2, 2, 3, 3), Square(0, 0, 10, 10)));
    }

    [Fact]
    public void A_line_wholly_inside_a_hole_does_not()
    {
        Polygon holed = new(
            Ring(0, 0, 10, 0, 10, 10, 0, 10, 0, 0),
            [Ring(3, 3, 7, 3, 7, 7, 3, 7, 3, 3)]);

        Assert.False(GeometryPredicates.Intersects(Line(4, 4, 6, 6), holed));
    }

    [Fact]
    public void Boxes_that_overlap_are_not_shapes_that_do()
    {
        // An L-shaped polygon and a point in the notch: the envelopes overlap, the shapes do not.
        Polygon ell = new(Ring(0, 0, 10, 0, 10, 2, 2, 2, 2, 10, 0, 10, 0, 0));

        Assert.False(GeometryPredicates.Intersects(new Point(6, 6), ell));
        Assert.False(GeometryPredicates.Intersects(Square(5, 5, 8, 8), ell));
        Assert.True(GeometryPredicates.Intersects(Square(5, 1, 8, 8), ell));
    }

    [Fact]
    public void Polygons_touching_at_one_corner_intersect()
    {
        Assert.True(GeometryPredicates.Intersects(Square(0, 0, 1, 1), Square(1, 1, 2, 2)));
    }

    [Fact]
    public void A_polygon_inside_another_s_hole_does_not_intersect_it_and_one_containing_it_does()
    {
        Polygon holed = new(
            Ring(0, 0, 10, 0, 10, 10, 0, 10, 0, 0),
            [Ring(2, 2, 8, 2, 8, 8, 2, 8, 2, 2)]);

        Assert.False(GeometryPredicates.Intersects(Square(3, 3, 7, 7), holed));
        Assert.True(GeometryPredicates.Intersects(Square(-5, -5, 20, 20), holed));
        Assert.True(GeometryPredicates.Intersects(Square(1, 1, 9, 9), holed));
    }

    [Fact]
    public void Collinear_segments_that_overlap_intersect_and_those_that_do_not_do_not()
    {
        Assert.True(GeometryPredicates.Intersects(Line(0, 0, 5, 0), Line(3, 0, 8, 0)));
        Assert.False(GeometryPredicates.Intersects(Line(0, 0, 2, 0), Line(3, 0, 8, 0)));
        Assert.True(GeometryPredicates.Intersects(Line(0, 0, 3, 0), Line(3, 0, 8, 0)));
    }

    [Fact]
    public void Multi_geometries_intersect_when_any_part_does()
    {
        MultiPolygon islands = new([Square(0, 0, 1, 1), Square(10, 10, 11, 11)]);

        Assert.True(GeometryPredicates.Intersects(new Point(10.5, 10.5), islands));
        Assert.False(GeometryPredicates.Intersects(new Point(5, 5), islands));

        MultiPoint points = new([new Point(5, 5), new Point(0.5, 0.5)]);
        Assert.True(GeometryPredicates.Intersects(points, islands));
    }

    [Fact]
    public void Empty_intersects_nothing()
    {
        Assert.False(GeometryPredicates.Intersects(Point.Empty, Square(0, 0, 1, 1)));
        Assert.False(GeometryPredicates.Intersects(Square(0, 0, 1, 1), Polygon.Empty));
    }
}
