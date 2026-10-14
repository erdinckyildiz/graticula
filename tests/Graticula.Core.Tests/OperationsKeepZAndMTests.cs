using System.Linq;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ADR-077 condition 4: the in-process operations follow §5 before any surface feeds them a geometry with Z or M.
/// </summary>
/// <remarks>
/// §5: a vertex an operation keeps keeps its Z and M; a vertex it creates gets them interpolated along its edge; an
/// operation that builds a new shape returns x and y and says so. Today a filter and every GeometryServer operation
/// still refuse a geometry that declares Z or M, so these are the operations' own rules, written down before a
/// surface relies on them.
/// </remarks>
public sealed class OperationsKeepZAndMTests
{
    private static LineString Line(double[] xy, double[] z, double[] m) => new(XySequence.Wrap(xy, z, m));

    [Fact]
    public void Densify_keeps_every_vertex_s_z_and_m_and_interpolates_the_ones_it_adds()
    {
        // One edge 10 long, from z 100 / m 0 to z 200 / m 50, densified to segments of 2.5: three new vertices.
        LineString line = Line([0, 0, 10, 0], [100, 200], [0, 50]);

        XySequence dense = ((LineString)GeometryOperations.Densify(line, 2.5)).Coordinates;

        Assert.Equal(5, dense.Count);
        Assert.Equal(GeometryOrdinates.Z | GeometryOrdinates.M, dense.Ordinates);
        Assert.Equal([0, 2.5, 5, 7.5, 10], Enumerable.Range(0, 5).Select(dense.X));
        Assert.Equal([100, 125, 150, 175, 200], Enumerable.Range(0, 5).Select(dense.Z));
        Assert.Equal([0, 12.5, 25, 37.5, 50], Enumerable.Range(0, 5).Select(dense.M));
    }

    [Fact]
    public void Densify_of_a_flat_line_stays_flat()
    {
        XySequence dense = ((LineString)GeometryOperations.Densify(new LineString(XySequence.Wrap([0, 0, 10, 0])), 2.5)).Coordinates;

        Assert.Equal(GeometryOrdinates.None, dense.Ordinates);
        Assert.Equal(5, dense.Count);
    }

    [Fact]
    public void Generalize_keeps_the_z_and_m_of_the_vertices_it_keeps()
    {
        // The middle vertex is 0.01 off the chord, so a tolerance of 1 drops it and keeps the ends.
        LineString line = Line([0, 0, 5, 0.01, 10, 0], [10, 20, 30], [1, 2, 3]);

        XySequence kept = ((LineString)GeometryOperations.Generalize(line, 1)).Coordinates;

        Assert.Equal(2, kept.Count);
        Assert.Equal([10, 30], Enumerable.Range(0, 2).Select(kept.Z));
        Assert.Equal([1, 3], Enumerable.Range(0, 2).Select(kept.M));
    }

    [Fact]
    public void A_ring_densified_closes_on_its_first_vertex_with_its_z()
    {
        LinearRing ring = new(XySequence.Wrap([0, 0, 10, 0, 10, 10, 0, 0], [5, 6, 7, 5], null));

        XySequence dense = ((Polygon)GeometryOperations.Densify(new Polygon(ring), 4)).Shell.Coordinates;

        Assert.Equal(dense.X(0), dense.X(dense.Count - 1));
        Assert.Equal(dense.Y(0), dense.Y(dense.Count - 1));
        Assert.Equal(5, dense.Z(0));
        Assert.Equal(5, dense.Z(dense.Count - 1));
    }

    [Fact]
    public void A_convex_hull_is_two_dimensional_and_says_which_ordinates_it_left()
    {
        Graticula.Geometries.Geometry hull = GeometryOperations.ConvexHull(
            [Line([0, 0, 10, 0, 5, 8], [1, 2, 3], [4, 5, 6])], out GeometryOrdinates dropped);

        Assert.Equal(GeometryOrdinates.Z | GeometryOrdinates.M, dropped);
        Assert.Equal(GeometryOrdinates.None, Ordinates.Of(hull));

        GeometryOperations.ConvexHull([new LineString(XySequence.Wrap([0, 0, 1, 1]))], out GeometryOrdinates flat);
        Assert.Equal(GeometryOrdinates.None, flat);
    }
}
