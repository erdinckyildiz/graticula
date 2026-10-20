using System;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>SdoShape: an SDO_GEOMETRY's parts into ISO WKB and back, without a database.</summary>
public sealed class SdoShapeTests
{
    private static Geometry Read(SdoShape.Parts parts) =>
        WkbReader.Read(SdoShape.ToWkb(parts) ?? throw new InvalidOperationException("no WKB"), GeometryOrdinates.Z | GeometryOrdinates.M, out _);

    [Fact]
    public void A_line_with_an_elevation_keeps_it()
    {
        LineString line = Assert.IsType<LineString>(Read(new(3002, null, [1, 2, 1], [30, 40, 1, 31, 41, 2])));

        Assert.Equal(GeometryOrdinates.Z, line.Coordinates.Ordinates);
        Assert.Equal([30, 40, 31, 41], line.Coordinates.ToInterleavedArray());
        Assert.Equal([1.0, 2.0], line.Coordinates.ZSpan().ToArray());
    }

    [Fact]
    public void A_measure_in_the_third_place_is_a_measure()
    {
        LineString line = Assert.IsType<LineString>(Read(new(3302, null, [1, 2, 1], [30, 40, 0, 31, 41, 5])));

        Assert.Equal(GeometryOrdinates.M, line.Coordinates.Ordinates);
        Assert.Equal([0.0, 5.0], line.Coordinates.MSpan().ToArray());
    }

    [Fact]
    public void Four_ordinates_are_an_elevation_and_a_measure()
    {
        LineString line = Assert.IsType<LineString>(Read(new(4402, null, [1, 2, 1], [30, 40, 1, 0, 31, 41, 2, 5])));

        Assert.Equal(GeometryOrdinates.Z | GeometryOrdinates.M, line.Coordinates.Ordinates);
        Assert.Equal([1.0, 2.0], line.Coordinates.ZSpan().ToArray());
        Assert.Equal([0.0, 5.0], line.Coordinates.MSpan().ToArray());
    }

    [Fact]
    public void A_point_held_in_sdo_point_keeps_its_elevation()
    {
        Point point = Assert.IsAssignableFrom<Point>(Read(new(3001, [30, 40, 7], null, null)));

        Assert.Equal(7, point.Z);
        Assert.Null(point.M);
    }

    [Fact]
    public void A_polygon_with_a_hole_and_a_multipolygon_are_read()
    {
        double[] ordinates = [0, 0, 1, 4, 0, 1, 4, 4, 1, 0, 4, 1, 0, 0, 1, 1, 1, 2, 1, 2, 2, 2, 2, 2, 2, 1, 2, 1, 1, 2];
        Polygon polygon = Assert.IsType<Polygon>(Read(new(3003, null, [1, 1003, 1, 16, 2003, 1], ordinates)));

        Assert.Single(polygon.Holes);
        Assert.Equal(GeometryOrdinates.Z, polygon.Shell.Coordinates.Ordinates);

        Geometry many = Read(new(3007, null, [1, 1003, 1, 16, 1003, 1], ordinates));
        Assert.Equal(2, Assert.IsType<MultiPolygon>(many).Parts.Count);
    }

    [Fact]
    public void An_arc_is_left_to_oracle()
    {
        Assert.Null(SdoShape.ToWkb(new(3002, null, [1, 2, 2], [0, 0, 1, 1, 1, 1, 2, 0, 1])));
        Assert.Null(SdoShape.ToWkb(new(4401, [30, 40, 7], null, null)));
    }

    [Fact]
    public void A_shape_written_from_wkb_turns_its_rings_as_oracle_requires_and_reads_back_the_same()
    {
        // Clockwise outer ring, as ArcGIS sends it; Oracle wants it counterclockwise.
        Polygon sent = new(new LinearRing(XySequence.Wrap([0, 0, 0, 4, 4, 4, 4, 0, 0, 0], [1, 2, 3, 4, 1], null)));

        SdoShape.Parts parts = SdoShape.FromWkb(WkbWriter.ToArray(sent));

        Assert.Equal(3003, parts.GType);
        Assert.Equal([1, 1003, 1], parts.ElemInfo!);
        Assert.Equal([0, 0, 1, 4, 0, 4, 4, 4, 3, 0, 4, 2, 0, 0, 1], parts.Ordinates!);

        Polygon back = Assert.IsType<Polygon>(Read(parts));
        Assert.Equal(GeometryOrdinates.Z, back.Shell.Coordinates.Ordinates);
    }

    [Fact]
    public void A_measured_point_and_a_multipoint_are_written_with_oracle_codes()
    {
        Assert.Equal(3301, SdoShape.FromWkb(WkbWriter.ToArray(Point.Create(30, 40, null, 9))).GType);

        SdoShape.Parts many = SdoShape.FromWkb(WkbWriter.ToArray(new MultiPoint([Point.Create(1, 2, 3, 4), Point.Create(5, 6, 7, 8)])));
        Assert.Equal(4405, many.GType);
        Assert.Equal([1, 1, 2], many.ElemInfo!);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], many.Ordinates!);
    }
}
