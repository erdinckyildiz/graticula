using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// A box against a point layer is decided in the database as a comparison of coordinates — and answers what the exact pass
/// answered: every point inside, every point on an edge or a corner, none outside. Checked against the same rows counted
/// here, on a grid that puts points exactly on the box's edges.
/// </summary>
[Trait("Needs", "MySql")]
public sealed class MySqlPointBoxTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly (int Id, double X, double Y)[] Grid =
        [.. Enumerable.Range(0, 21).SelectMany(i => Enumerable.Range(0, 21).Select(j => (Id: (i * 21) + j + 1, X: 30 + (i * 0.25), Y: 37 + (j * 0.25))))];

    private static async Task<MySqlFeatureSource> GridAsync()
    {
        await MySqlFixture.ExecuteAsync(
            "drop table if exists zzt_point_box",
            "create table zzt_point_box (objectid int primary key, shape point not null srid 4326, spatial index (shape))",
            "insert into zzt_point_box values " + string.Join(", ", Grid.Select(p => FormattableString.Invariant(
                $"({p.Id}, st_geomfromtext('POINT({p.X} {p.Y})', 4326, 'axis-order=long-lat'))"))));

        LayerDefinition layer = new("zzt_point_box", MySqlFixture.Source.Database, "zzt_point_box", "shape", 4326, "objectid", "objectid", isHosted: false);
        return new MySqlFeatureSource(MySqlFixture.Locator, layer, new NoProjector());
    }

    private static long[] Inside(double x0, double y0, double x1, double y1) =>
        [.. Grid.Where(p => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1).Select(p => (long)p.Id).Order()];

    [Fact]
    public async Task A_box_keeps_its_edges_and_corners_and_nothing_outside()
    {
        MySqlFeatureSource source = await GridAsync();
        Polygon box = new(new LinearRing(XySequence.Wrap([31, 38, 33.5, 38, 33.5, 39.75, 31, 39.75, 31, 38])));

        // A spatial filter with a rectangle, and a bounding box: both on edges that pass through grid points.
        Assert.Equal(Inside(31, 38, 33.5, 39.75), (await source.ObjectIdsAsync(
            new FeatureQuery(1000, spatial: new SpatialFilter(box, SpatialRelation.Intersects)), None)).Order().ToArray());
        Assert.Equal(Inside(31, 38, 33.5, 39.75), (await source.ObjectIdsAsync(
            new FeatureQuery(1000, boundingBox: new Envelope(31, 38, 33.5, 39.75)), None)).Order().ToArray());
        Assert.Equal(Inside(31, 38, 33.5, 39.75).Length, await source.CountAsync(
            new FeatureQuery(1, spatial: new SpatialFilter(box, SpatialRelation.Intersects)), None));

        // A box that is a single point's corner.
        Assert.Equal([Grid.Single(p => p.X == 32 && p.Y == 38).Id], (await source.ObjectIdsAsync(
            new FeatureQuery(1000, boundingBox: new Envelope(31.9, 37.9, 32, 38)), None)).Select(i => (int)i).ToArray());

        // A triangle is not a box, and still answers exactly through the other path.
        Polygon triangle = new(new LinearRing(XySequence.Wrap([30, 37, 35, 37, 30, 42, 30, 37])));
        long[] under = [.. Grid.Where(p => p.X + p.Y <= 72.0000001).Select(p => (long)p.Id).Order()];
        Assert.Equal(under, (await source.ObjectIdsAsync(
            new FeatureQuery(1000, spatial: new SpatialFilter(triangle, SpatialRelation.Intersects)), None)).Order().ToArray());
    }

    private sealed class NoProjector : IProjector
    {
        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to project.");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked for a written reference.");

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to generalize.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to describe a projection.");
    }
}
