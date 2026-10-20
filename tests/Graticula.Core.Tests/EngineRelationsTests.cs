using System;
using System.Linq;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The relations and the distance the geometry engine decides for a GeoParquet or Oracle layer — the parts that are
/// arithmetic, checked by hand-worked numbers; what the engine answers is the parity run against PostGIS (ADR-205).
/// </summary>
public sealed class EngineRelationsTests
{
    /// <summary>
    /// The edge an envelope's top makes as a geodesic bows toward the pole — which is why a place 49 km from an envelope
    /// on the ellipsoid was left out when the edge was projected as a chord (2026-10-20).
    /// </summary>
    [Fact]
    public void A_long_edge_is_cut_along_its_great_circle_and_bows_toward_the_pole()
    {
        LineString edge = new(XySequence.Wrap([31.0, 41.0, 35.0, 41.0]));

        LineString dense = (LineString)EngineRelations.GeodesicDensified(edge, EngineRelations.MostGeodesicStepMetres);
        double[] xy = dense.Coordinates.ToInterleavedArray();

        // 4° of longitude at 41° is about 336 km, so at least 34 pieces of no more than 10 km.
        Assert.InRange(xy.Length / 2, 35, 40);
        Assert.Equal((31.0, 41.0), (xy[0], xy[1]));
        Assert.Equal((35.0, 41.0), (xy[^2], xy[^1]));

        // The middle of the great circle lies north of the parallel: tan φ = tan 41° / cos 2°, so φ = 41.0174°, about
        // 1.9 km north — the bow the projected chord missed.
        int middle = (xy.Length / 2) / 2;
        Assert.InRange(xy[(middle * 2) + 1], 41.016, 41.019);
    }

    [Fact]
    public void A_point_and_a_short_edge_are_left_as_they_are()
    {
        Point point = new(32.85, 39.93);
        Assert.Same(point, EngineRelations.GeodesicDensified(point, 10_000));

        LineString shortEdge = new(XySequence.Wrap([32.0, 39.0, 32.05, 39.0]));
        Assert.Equal(4, ((LineString)EngineRelations.GeodesicDensified(shortEdge, 10_000)).Coordinates.ToInterleavedArray().Length);
    }

    [Fact]
    public void A_polygon_keeps_its_rings_closed()
    {
        Polygon square = new(new LinearRing(XySequence.Wrap([31.0, 38.5, 35.0, 38.5, 35.0, 41.0, 31.0, 41.0, 31.0, 38.5])));

        double[] ring = ((Polygon)EngineRelations.GeodesicDensified(square, 10_000)).Shell.Coordinates.ToInterleavedArray();

        Assert.True(ring.Length > 10);
        Assert.Equal((ring[0], ring[1]), (ring[^2], ring[^1]));
    }

    [Theory]
    [InlineData(SpatialRelation.Within, "esriGeometryRelationWithin")]
    [InlineData(SpatialRelation.Contains, "T*****FF*")]
    [InlineData(SpatialRelation.Touches, "esriGeometryRelationTouch")]
    public void A_relation_is_the_engine_s_pattern_with_the_feature_on_the_left(SpatialRelation relation, string pattern) =>
        Assert.Equal(pattern, EngineRelations.PatternOf(new SpatialFilter(new Point(0, 0), relation)));

    [Fact]
    public void The_engine_decides_a_distance_and_the_relations_beyond_intersection_and_nothing_else()
    {
        Assert.True(EngineRelations.Decides(new SpatialFilter(new Point(0, 0), SpatialRelation.Intersects, null, 50)));
        Assert.True(EngineRelations.Decides(new SpatialFilter(new Point(0, 0), SpatialRelation.Within)));
        Assert.False(EngineRelations.Decides(new SpatialFilter(new Point(0, 0), SpatialRelation.Intersects)));
        Assert.False(EngineRelations.Decides(new SpatialFilter(new Point(0, 0), SpatialRelation.EnvelopeIntersects)));
    }

    [Fact]
    public void A_distance_in_metres_widens_a_box_in_degrees_by_at_least_that_much()
    {
        Envelope box = EngineRelations.SearchBox(new Point(32.85, 39.93), 50_000, 4326);

        Assert.InRange(39.93 - box.MinY, 50_000 / 111_700.0, 50_000 / 110_000.0);
        Assert.True(box.MaxX - 32.85 > 50_000 / (111_320.0 * Math.Cos(39.93 * Math.PI / 180)) - 1e-9);
    }
}
