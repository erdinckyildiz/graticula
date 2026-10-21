using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// Points within a distance of a point are measured in process, on the WGS 84 ellipsoid for EPSG:4326 and on the plane
/// otherwise, and agree with PostGIS's geography to the centimetre — the engine and the projector are never asked, and
/// throw if they are. Distances are PostGIS 3.4's <c>ST_Distance(geography, geography)</c> for the same pairs.
/// </summary>
public sealed class EngineRelationsPointDistanceTests
{
    private static Task<List<long>> WithinAsync(Point centre, double metres, int srid, params Point[] points)
    {
        long[] ids = new long[points.Length];

        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = i + 1;
        }

        return EngineRelations.SatisfyingAsync(
            ids, points, new SpatialFilter(centre, SpatialRelation.Intersects, null, metres), srid,
            new NoProjector(), new NoEngine(), "a test", CancellationToken.None);
    }

    [Theory]
    [InlineData(32.85, 39.93, 33.3, 40.3, 56209.80342445)]
    [InlineData(32.85, 39.93, 29.0, 41.0, 347432.69433467)]
    [InlineData(0.0, 0.0, 0.5, 0.0, 55659.74539664)]
    [InlineData(179.5, -10.0, -179.5, -10.0, 109639.32210546)]
    public async Task A_point_is_within_exactly_where_PostGIS_geography_says(double x0, double y0, double x1, double y1, double postgis)
    {
        Assert.Empty(await WithinAsync(new Point(x0, y0), postgis - 0.01, 4326, new Point(x1, y1)));
        Assert.Equal([1L], await WithinAsync(new Point(x0, y0), postgis + 0.01, 4326, new Point(x1, y1)));
    }

    [Fact]
    public async Task A_projected_layer_measures_on_the_plane()
    {
        Assert.Equal([2L], await WithinAsync(new Point(0, 0), 5, 3857, new Point(3, 4.0001), new Point(3, 4)));
    }

    [Fact]
    public async Task Anything_but_points_around_a_point_is_left_to_the_engine()
    {
        Polygon square = new(new LinearRing(XySequence.Wrap([0, 0, 1, 0, 1, 1, 0, 1, 0, 0])));

        // A polygon among the candidates: the engine is asked, and this one says so by throwing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => EngineRelations.SatisfyingAsync(
            [1], [square], new SpatialFilter(new Point(0, 0), SpatialRelation.Intersects, null, 10), 3857,
            new NoProjector(), new NoEngine(), "a test", CancellationToken.None));

        // Another geographic reference may not sit on WGS 84's ellipsoid, so it is not measured as if it did.
        await Assert.ThrowsAnyAsync<Exception>(() => WithinAsync(new Point(0, 0), 10, 4230, new Point(0, 0.0001)));
    }

    private sealed class NoEngine : IGeometryEngine
    {
        public Task<EngineResult> ComputeAsync(EngineRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The engine was asked.");
    }

    private sealed class NoProjector : IProjector
    {
        public Task<(IReadOnlyList<Graticula.Geometries.Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The projector was asked.");

        public Task<IReadOnlyList<Graticula.Geometries.Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The projector was asked.");

        public Task<IReadOnlyList<Graticula.Geometries.Geometry>> GeneralizeAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The projector was asked.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The projector was asked.");
    }
}
