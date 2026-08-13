using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GisServer.Api.ArcGis;
using GisServer.Geometries;
using Xunit;

namespace GisServer.Api.ArcGis.Tests;

public sealed class ArcGisGeometryWriterTests
{
    private const int Wkid = 3857;

    /// <summary>Counter-clockwise unit square in a y-up system.</summary>
    private static LinearRing Ccw(double size = 1) => new(XySequence.Wrap(
        [0, 0, size, 0, size, size, 0, size, 0, 0]));

    /// <summary>The same square, wound the other way.</summary>
    private static LinearRing Cw(double size = 1) => new(XySequence.Wrap(
        [0, 0, 0, size, size, size, size, 0, 0, 0]));

    private static JsonElement Write(Geometry geometry)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            ArcGisGeometryWriter.Write(writer, geometry, Wkid);
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static (double X, double Y) At(JsonElement ring, int index) =>
        (ring[index][0].GetDouble(), ring[index][1].GetDouble());

    [Fact]
    public void A_point_writes_x_y_and_a_spatial_reference()
    {
        JsonElement json = Write(new Point(3, 4));

        Assert.Equal(3, json.GetProperty("x").GetDouble());
        Assert.Equal(4, json.GetProperty("y").GetDouble());
        Assert.Equal(Wkid, json.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void An_empty_point_writes_nulls_rather_than_omitting_the_fields()
    {
        // A client that sees neither x nor y treats the geometry as malformed
        // rather than absent.
        JsonElement json = Write(Point.Empty);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("x").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("y").ValueKind);
    }

    [Fact]
    public void A_line_string_becomes_a_polyline_with_one_path()
    {
        JsonElement json = Write(new LineString(XySequence.Wrap([0, 0, 1, 1, 2, 0])));

        JsonElement paths = json.GetProperty("paths");
        Assert.Equal(1, paths.GetArrayLength());
        Assert.Equal(3, paths[0].GetArrayLength());
        Assert.Equal((1d, 1d), At(paths[0], 1));
    }

    [Fact]
    public void A_multi_line_string_becomes_a_polyline_with_several_paths()
    {
        // The collapse ADR-005 §3.3c describes: two of ours, one of theirs.
        MultiLineString multi = new(
        [
            new LineString(XySequence.Wrap([0, 0, 1, 1])),
            new LineString(XySequence.Wrap([5, 5, 6, 6])),
        ]);

        Assert.Equal(2, Write(multi).GetProperty("paths").GetArrayLength());
        Assert.Equal("esriGeometryPolyline", ArcGisGeometryWriter.TypeName(GeometryKind.LineString));
        Assert.Equal("esriGeometryPolyline", ArcGisGeometryWriter.TypeName(GeometryKind.MultiLineString));
    }

    [Fact]
    public void A_shell_is_written_clockwise_whatever_way_it_arrived()
    {
        // ArcGIS requires exterior rings clockwise. PostGIS guarantees nothing,
        // and OSM data contains both. A shell written the wrong way renders as a
        // hole and raises no error anywhere — which is why this is pinned in
        // both directions.
        foreach (LinearRing shell in new[] { Ccw(), Cw() })
        {
            JsonElement rings = Write(new Polygon(shell)).GetProperty("rings");

            Assert.Equal(1, rings.GetArrayLength());

            // Clockwise in y-up: from the origin, the next vertex goes up rather
            // than right.
            Assert.Equal((0d, 0d), At(rings[0], 0));
            Assert.Equal((0d, 1d), At(rings[0], 1));
        }
    }

    [Fact]
    public void A_hole_is_written_counter_clockwise_whatever_way_it_arrived()
    {
        LinearRing hole = new(XySequence.Wrap([1, 1, 2, 1, 2, 2, 1, 2, 1, 1]));   // ccw
        LinearRing holeReversed = new(XySequence.Wrap([1, 1, 1, 2, 2, 2, 2, 1, 1, 1]));

        foreach (LinearRing candidate in new[] { hole, holeReversed })
        {
            JsonElement rings = Write(new Polygon(Cw(10), [candidate])).GetProperty("rings");

            Assert.Equal(2, rings.GetArrayLength());

            // Counter-clockwise in y-up: from (1,1) the next vertex goes right.
            Assert.Equal((1d, 1d), At(rings[1], 0));
            Assert.Equal((2d, 1d), At(rings[1], 1));
        }
    }

    [Fact]
    public void Reversing_a_ring_preserves_every_vertex()
    {
        // The reversal is written in place rather than by copying, so it is
        // worth checking it does not drop or duplicate an endpoint.
        JsonElement rings = Write(new Polygon(Ccw())).GetProperty("rings");

        Assert.Equal(5, rings[0].GetArrayLength());
        Assert.Equal(At(rings[0], 0), At(rings[0], 4));
    }

    [Fact]
    public void A_multi_polygon_flattens_into_one_ring_array()
    {
        // The lossy direction, made explicit: two parts become four rings with
        // nothing but winding to say where one part ends.
        Polygon first = new(Cw(1), [new LinearRing(XySequence.Wrap(
            [0.2, 0.2, 0.4, 0.2, 0.4, 0.4, 0.2, 0.4, 0.2, 0.2]))]);
        Polygon second = new(Cw(2));

        JsonElement rings = Write(new MultiPolygon([first, second])).GetProperty("rings");

        Assert.Equal(3, rings.GetArrayLength());
        Assert.Equal("esriGeometryPolygon", ArcGisGeometryWriter.TypeName(GeometryKind.MultiPolygon));
    }

    [Fact]
    public void A_multi_point_writes_a_points_array()
    {
        JsonElement json = Write(new MultiPoint([new Point(1, 2), new Point(3, 4)]));

        Assert.Equal(2, json.GetProperty("points").GetArrayLength());
        Assert.Equal((3d, 4d), At(json.GetProperty("points"), 1));
    }
}
