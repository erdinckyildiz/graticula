using System;
using System.Collections.Generic;
using GisServer.Geometries;
using Xunit;

namespace GisServer.Core.Tests.Geometries;

public sealed class PointTests
{
    [Fact]
    public void A_point_bounds_itself()
    {
        Point point = new(3, 4);

        Assert.Equal(GeometryKind.Point, point.Kind);
        Assert.False(point.IsEmpty);
        Assert.Equal(1, point.CoordinateCount);
        Assert.Equal(new Envelope(3, 4, 3, 4), point.Envelope);
        Assert.Equal(0, point.Envelope.Width);
    }

    [Fact]
    public void The_empty_point_has_no_envelope()
    {
        Assert.True(Point.Empty.IsEmpty);
        Assert.Equal(0, Point.Empty.CoordinateCount);
        Assert.True(Point.Empty.Envelope.IsEmpty);
    }
}

public sealed class LineStringTests
{
    [Fact]
    public void A_line_bounds_its_coordinates()
    {
        LineString line = new(XySequence.Wrap([0, 0, 10, 5, 2, -3]));

        Assert.Equal(GeometryKind.LineString, line.Kind);
        Assert.Equal(3, line.CoordinateCount);
        Assert.Equal(new Envelope(0, -3, 10, 5), line.Envelope);
        Assert.False(line.IsClosed);
    }

    [Fact]
    public void A_line_returning_to_its_start_is_closed()
    {
        LineString line = new(XySequence.Wrap([0, 0, 4, 0, 4, 3, 0, 0]));

        Assert.True(line.IsClosed);
    }

    [Fact]
    public void A_single_coordinate_is_rejected()
    {
        // Zero or two-plus. One is a modelling error, not a degenerate case
        // worth carrying through every downstream method.
        Assert.Throws<ArgumentException>(() => new LineString(XySequence.Wrap([1, 2])));
    }

    [Fact]
    public void An_empty_line_is_allowed()
    {
        Assert.True(LineString.Empty.IsEmpty);
        Assert.True(LineString.Empty.Envelope.IsEmpty);
    }
}

public sealed class LinearRingTests
{
    // A unit square wound counter-clockwise in a y-up coordinate system:
    // (0,0) → (1,0) → (1,1) → (0,1) → close.
    private static readonly double[] CounterClockwiseSquare = [0, 0, 1, 0, 1, 1, 0, 1, 0, 0];
    private static readonly double[] ClockwiseSquare = [0, 0, 0, 1, 1, 1, 1, 0, 0, 0];

    [Fact]
    public void Winding_is_pinned_counter_clockwise_is_positive()
    {
        // This test exists because a sign error here produces inside-out
        // polygons that render as holes, and nothing else would catch it.
        LinearRing ccw = new(XySequence.Wrap(CounterClockwiseSquare));

        Assert.True(ccw.SignedArea2() > 0);
        Assert.True(ccw.IsCounterClockwise);
        Assert.Equal(2, ccw.SignedArea2());  // twice the unit square's area
    }

    [Fact]
    public void Winding_is_pinned_clockwise_is_negative()
    {
        LinearRing cw = new(XySequence.Wrap(ClockwiseSquare));

        Assert.True(cw.SignedArea2() < 0);
        Assert.False(cw.IsCounterClockwise);
        Assert.Equal(-2, cw.SignedArea2());
    }

    [Fact]
    public void Reversing_a_ring_reverses_its_sign_and_preserves_magnitude()
    {
        LinearRing ccw = new(XySequence.Wrap(CounterClockwiseSquare));
        LinearRing cw = new(XySequence.Wrap(ClockwiseSquare));

        Assert.Equal(ccw.SignedArea2(), -cw.SignedArea2());
    }

    [Fact]
    public void An_unclosed_ring_is_rejected()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new LinearRing(XySequence.Wrap([0, 0, 1, 0, 1, 1, 0, 1])));

        Assert.Contains("closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ring_with_too_few_coordinates_is_rejected()
    {
        // Closed, but cannot bound an area.
        Assert.Throws<ArgumentException>(
            () => new LinearRing(XySequence.Wrap([0, 0, 1, 1, 0, 0])));
    }

    [Fact]
    public void An_empty_ring_is_allowed_and_has_no_area()
    {
        Assert.True(LinearRing.Empty.IsEmpty);
        Assert.Equal(0, LinearRing.Empty.SignedArea2());
        Assert.False(LinearRing.Empty.IsCounterClockwise);
    }

    [Fact]
    public void A_ring_is_a_line_string()
    {
        // Simple Features says a LinearRing is a closed LineString, and the
        // type hierarchy should say the same rather than duplicating it.
        LinearRing ring = new(XySequence.Wrap(CounterClockwiseSquare));

        Assert.IsAssignableFrom<LineString>(ring);
        Assert.True(ring.IsClosed);
    }
}

public sealed class PolygonTests
{
    private static LinearRing Square(double size) => new(XySequence.Wrap(
        [0, 0, size, 0, size, size, 0, size, 0, 0]));

    [Fact]
    public void A_solid_polygon_has_no_holes()
    {
        Polygon polygon = new(Square(10));

        Assert.Equal(GeometryKind.Polygon, polygon.Kind);
        Assert.Empty(polygon.Holes);
        Assert.Equal(5, polygon.CoordinateCount);
        Assert.Equal(new Envelope(0, 0, 10, 10), polygon.Envelope);
    }

    [Fact]
    public void Holes_count_toward_the_coordinate_total_but_not_the_envelope()
    {
        LinearRing hole = new(XySequence.Wrap([1, 1, 2, 1, 2, 2, 1, 2, 1, 1]));
        Polygon polygon = new(Square(10), [hole]);

        Assert.Single(polygon.Holes);
        Assert.Equal(10, polygon.CoordinateCount);
        Assert.Equal(new Envelope(0, 0, 10, 10), polygon.Envelope);
    }

    [Fact]
    public void A_hole_in_an_empty_shell_is_rejected()
    {
        LinearRing hole = new(XySequence.Wrap([1, 1, 2, 1, 2, 2, 1, 2, 1, 1]));

        Assert.Throws<ArgumentException>(() => new Polygon(LinearRing.Empty, [hole]));
    }

    [Fact]
    public void An_empty_hole_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Polygon(Square(10), [LinearRing.Empty]));
    }

    [Fact]
    public void Rings_can_share_one_buffer_without_copying()
    {
        // The property that keeps a many-holed polygon to a single allocation of
        // coordinate data.
        double[] buffer =
        [
            0, 0, 10, 0, 10, 10, 0, 10, 0, 0,   // shell
            1, 1, 2, 1, 2, 2, 1, 2, 1, 1,       // hole
        ];
        XySequence all = XySequence.Wrap(buffer);

        Polygon polygon = new(
            new LinearRing(all.Slice(0, 5)),
            [new LinearRing(all.Slice(5, 5))]);

        Assert.Equal(10, polygon.CoordinateCount);
        Assert.Equal(new Envelope(0, 0, 10, 10), polygon.Envelope);
        Assert.Equal(1, polygon.Holes[0].Coordinates.X(0));
    }
}

public sealed class MultiGeometryTests
{
    private static Polygon UnitSquareAt(double x, double y) => new(new LinearRing(
        XySequence.Wrap([x, y, x + 1, y, x + 1, y + 1, x, y + 1, x, y])));

    [Fact]
    public void A_multi_polygon_bounds_all_its_parts()
    {
        MultiPolygon multi = new([UnitSquareAt(0, 0), UnitSquareAt(5, 5)]);

        Assert.Equal(GeometryKind.MultiPolygon, multi.Kind);
        Assert.Equal(2, multi.Parts.Count);
        Assert.Equal(10, multi.CoordinateCount);
        Assert.Equal(new Envelope(0, 0, 6, 6), multi.Envelope);
    }

    [Fact]
    public void Parts_are_typed_so_callers_do_not_cast()
    {
        MultiPolygon multi = new([UnitSquareAt(0, 0)]);

        Polygon first = multi.Parts[0];
        Assert.Empty(first.Holes);
    }

    [Fact]
    public void An_empty_part_is_rejected()
    {
        // Otherwise IsEmpty would be false for a geometry that draws nothing.
        Assert.Throws<ArgumentException>(() => new MultiPolygon([Polygon.Empty]));
        Assert.Throws<ArgumentException>(() => new MultiPoint([Point.Empty]));
    }

    [Fact]
    public void Empty_multi_geometries_have_no_parts()
    {
        Assert.True(MultiPoint.Empty.IsEmpty);
        Assert.True(MultiLineString.Empty.IsEmpty);
        Assert.True(MultiPolygon.Empty.IsEmpty);
        Assert.True(MultiPolygon.Empty.Envelope.IsEmpty);
    }

    [Fact]
    public void A_multi_point_bounds_its_points()
    {
        MultiPoint multi = new([new Point(1, 1), new Point(-4, 7)]);

        Assert.Equal(2, multi.CoordinateCount);
        Assert.Equal(new Envelope(-4, 1, 1, 7), multi.Envelope);
    }

    [Fact]
    public void A_multi_line_string_bounds_its_lines()
    {
        MultiLineString multi = new(
        [
            new LineString(XySequence.Wrap([0, 0, 1, 1])),
            new LineString(XySequence.Wrap([5, 5, 6, 4])),
        ]);

        Assert.Equal(GeometryKind.MultiLineString, multi.Kind);
        Assert.Equal(new Envelope(0, 0, 6, 5), multi.Envelope);
    }

    [Fact]
    public void A_null_part_is_rejected()
    {
        List<Point> parts = [null!];

        Assert.Throws<ArgumentException>(() => new MultiPoint(parts));
    }
}
