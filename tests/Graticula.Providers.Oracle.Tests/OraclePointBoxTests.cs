using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>
/// Where a spatial index promises points (LAYER_GTYPE=POINT), a box is decided in Oracle as a comparison of coordinates,
/// candidates are read as their numbers, and a page's shapes are read after the page is chosen — each answering what the
/// general path answered: edges and corners kept, nothing outside, and a page in its order with its shapes.
/// </summary>
[Trait("Needs", "Oracle")]
public sealed class OraclePointBoxTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly (int Id, double X, double Y)[] Grid =
        [.. Enumerable.Range(0, 21).SelectMany(i => Enumerable.Range(0, 21).Select(j => (Id: (i * 21) + j + 1, X: 30 + (i * 0.25), Y: 37 + (j * 0.25))))];

    private static async Task ExecuteAsync(string sql)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(None);
    }

    private static async Task<OracleFeatureSource> GridAsync()
    {
        await ExecuteAsync("drop table if exists ZZT_POINT_BOX purge");
        await ExecuteAsync("delete from user_sdo_geom_metadata where table_name = 'ZZT_POINT_BOX'");
        await ExecuteAsync("create table ZZT_POINT_BOX (objectid number(10) primary key, label varchar2(10), shape sdo_geometry)");
        await ExecuteAsync("insert into user_sdo_geom_metadata values ('ZZT_POINT_BOX', 'SHAPE', "
            + "sdo_dim_array(sdo_dim_element('X', -180, 180, 0.05), sdo_dim_element('Y', -90, 90, 0.05)), 4326)");

        foreach ((int id, double x, double y) in Grid)
        {
            await ExecuteAsync(FormattableString.Invariant(
                $"insert into ZZT_POINT_BOX values ({id}, 'p{id}', sdo_geometry(2001, 4326, sdo_point_type({x}, {y}, null), null, null))"));
        }

        await ExecuteAsync("create index ZZT_POINT_BOX_SX on ZZT_POINT_BOX (shape) indextype is mdsys.spatial_index_v2 "
            + "parameters ('layer_gtype=POINT')");

        LayerDefinition layer = new("ZZT_POINT_BOX", OracleFixture.Source.User.ToUpperInvariant(), "ZZT_POINT_BOX", "SHAPE", 4326,
            "OBJECTID", "OBJECTID", isHosted: false);
        return new OracleFeatureSource(OracleFixture.Locator, layer, new NoProjector());
    }

    private static long[] Inside(double x0, double y0, double x1, double y1) =>
        [.. Grid.Where(p => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1).Select(p => (long)p.Id).Order()];

    [Fact]
    public async Task A_box_keeps_its_edges_and_corners_and_a_page_keeps_its_order_and_shapes()
    {
        OracleFeatureSource source = await GridAsync();
        Polygon box = new(new LinearRing(XySequence.Wrap([31, 38, 33.5, 38, 33.5, 39.75, 31, 39.75, 31, 38])));

        Assert.Equal(Inside(31, 38, 33.5, 39.75), (await source.ObjectIdsAsync(
            new FeatureQuery(1000, spatial: new SpatialFilter(box, SpatialRelation.Intersects)), None)).Order().ToArray());
        Assert.Equal(Inside(31, 38, 33.5, 39.75), (await source.ObjectIdsAsync(
            new FeatureQuery(1000, boundingBox: new Envelope(31, 38, 33.5, 39.75)), None)).Order().ToArray());
        Assert.Equal(Inside(31, 38, 33.5, 39.75).Length, await source.CountAsync(
            new FeatureQuery(1, spatial: new SpatialFilter(box, SpatialRelation.Intersects)), None));

        // A box over most of the layer is answered without the spatial index, and keeps its edges as the narrow one does.
        Assert.Equal(Inside(29.9, 36.9, 34.5, 41.5), (await source.ObjectIdsAsync(
            new FeatureQuery(1000, boundingBox: new Envelope(29.9, 36.9, 34.5, 41.5)), None)).Order().ToArray());
        Assert.Equal(Grid.Length, await source.CountAsync(
            new FeatureQuery(1, spatial: new SpatialFilter(
                new Polygon(new LinearRing(XySequence.Wrap([30, 37, 35, 37, 35, 42, 30, 42, 30, 37]))), SpatialRelation.Intersects)), None));

        // A triangle is not a box: candidates read as coordinates, decided here.
        Polygon triangle = new(new LinearRing(XySequence.Wrap([30, 37, 35, 37, 30, 42, 30, 37])));
        long[] under = [.. Grid.Where(p => p.X + p.Y <= 72.0000001).Select(p => (long)p.Id).Order()];
        Assert.Equal(under, (await source.ObjectIdsAsync(
            new FeatureQuery(1000, spatial: new SpatialFilter(triangle, SpatialRelation.Intersects)), None)).Order().ToArray());

        // A page at an offset, ordered by label descending: the order is the first pass's, the shapes the second's.
        List<Feature> page = [];
        await foreach (Feature f in source.ReadAsync(new FeatureQuery(
            10, fields: ["LABEL"], orderBy: [new Graticula.Features.SortKey("LABEL", true)], offset: 5), None))
        {
            page.Add(f);
        }

        var expected = Grid.OrderByDescending(p => "p" + p.Id, StringComparer.Ordinal).Skip(5).Take(10).ToArray();
        Assert.Equal(expected.Select(p => "p" + p.Id), page.Select(f => (string?)f[0]));
        Assert.Equal(expected.Select(p => (p.X, p.Y)), page.Select(f => (((Point)f.Geometry!).X, ((Point)f.Geometry!).Y)));
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
