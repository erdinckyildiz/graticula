using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// ADR-208 condition 4: a MySQL layer's history, kept by triggers beside its table — every version, the editor this
/// server names and the user another tool connects as, a read at a moment, a restore, and switching it off.
/// </summary>
[Trait("Needs", "MySql")]
public sealed class MySqlFeatureHistoryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_history_records_every_version_answers_a_moment_and_restores_a_deleted_feature()
    {
        const string Table = "zzt_history";

        await MySqlFixture.ExecuteAsync(
            $"drop table if exists {Table}__history",
            $"drop table if exists {Table}",
            $"create table {Table} (objectid int auto_increment primary key, label varchar(20), size_m double, made datetime(6), "
            + "globalid char(38), shape point not null srid 4326, spatial index (shape))",
            $"insert into {Table} (label, size_m, made, globalid, shape) values "
            + "('one', 1.5, '2026-01-02 03:04:05.5', '{1F0C0D2E-0000-4000-8000-000000000001}', st_geomfromtext('POINT(30 40)', 4326, 'axis-order=long-lat')), "
            + "('two', 2.5, null, null, st_geomfromtext('POINT(31 41)', 4326, 'axis-order=long-lat'))");

        LayerDefinition layer = new(Table, MySqlFixture.Source.Database, Table, "shape", 4326, "objectid", "objectid", isHosted: false);
        MySqlFeatureHistory history = new(MySqlFixture.Locator, layer);
        MySqlFeatureSource source = new(MySqlFixture.Locator, layer, new FailingProjector());

        Assert.Equal(2, await history.EnableAsync(None));
        Assert.True(await history.IsOnAsync(None));
        Assert.True((await source.DescribeAsync(None)).Archived);

        // Switching it on again is not a second seed.
        Assert.Equal(2, await history.EnableAsync(None));

        await Task.Delay(1_100);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await Task.Delay(1_100);

        LayerDescription described = await source.DescribeAsync(None);
        MySqlFeatureWriter writer = new(MySqlFixture.Locator, layer, described.Fields, new FailingProjector());

        EditOutcome edited = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(1, new Dictionary<string, object?> { ["label"] = "changed" }, new Point(35, 45))], [2], Editor: "ayse"),
            None);
        Assert.True(edited.AllSucceeded);

        await MySqlFixture.ExecuteAsync($"update {Table} set label = 'by sql' where objectid = 1");

        IReadOnlyList<FeatureChange> changes = await history.ChangesAsync(null, 10, None);
        Assert.Contains(changes, c => c is { ObjectId: 1, Kind: "updated", Editor: "ayse", Direct: false });
        Assert.Contains(changes, c => c is { ObjectId: 2, Kind: "deleted", Editor: "ayse", Direct: false });
        Assert.Contains(changes, c => c.ObjectId == 1 && c.Kind == "updated" && c.Direct && c.Editor == MySqlFixture.Source.User);
        Assert.DoesNotContain(changes, c => c.Kind == "existed");

        // The layer as it was: both features, with their labels, sizes, dates, GlobalIDs and shapes then.
        List<Feature> then = [];
        await foreach (Feature f in source.ReadAsync(
            new FeatureQuery(10, fields: ["label", "size_m", "made", "globalid"], orderBy: [new Graticula.Features.SortKey("objectid", false)]) { HistoricMoment = before }, None))
        {
            then.Add(f);
        }

        Assert.Equal(["one", "two"], then.Select(f => (string?)f[0]));
        Assert.Equal([1.5, 2.5], then.Select(f => Convert.ToDouble(f[1], CultureInfo.InvariantCulture)));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc), Assert.IsType<DateTime>(then[0][2]));
        Assert.Null(then[1][2]);
        Assert.Equal(Guid.Parse("1F0C0D2E-0000-4000-8000-000000000001"), then[0][3]);
        Assert.Equal((30d, 40d), (((Point)then[0].Geometry!).X, ((Point)then[0].Geometry!).Y));

        Assert.Equal(1, await source.CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(new Point(30, 40), SpatialRelation.Intersects)) { HistoricMoment = before }, None));
        Assert.Equal(0, await source.CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(new Point(30, 40), SpatialRelation.Intersects)), None));

        IReadOnlyList<FeatureVersion> ones = await history.VersionsAsync(1, None);
        Assert.Equal(["seed", "update", "update"], ones.Select(v => v.OpenedBy));
        Assert.Equal("one", ones[0].Attributes.GetProperty("label").GetString());
        Assert.Contains("[30", ones[0].GeoJson!, StringComparison.Ordinal);
        Assert.Contains("[35", ones[1].GeoJson!, StringComparison.Ordinal);

        FeatureVersion deleted = Assert.Single(await history.VersionsAsync(2, None));
        Assert.Equal("delete", deleted.ClosedBy);

        RestoreOutcome restored = await history.RestoreAsync(2, deleted.HistoryId, "ayse", null, None);
        Assert.Equal(RestoreResult.Recreated, restored.Result);
        Assert.Equal(1, await source.CountAsync(new FeatureQuery(1, identities: [2]), None));
        Assert.Contains(await history.ChangesAsync(null, 10, None), c => c is { ObjectId: 2, Kind: "restored", Editor: "ayse" });

        // A restore of an existing feature writes back every recorded column, the date and the GlobalID included.
        RestoreOutcome back = await history.RestoreAsync(1, ones[0].HistoryId, "ayse", null, None);
        Assert.Equal(RestoreResult.Updated, back.Result);
        Feature now = Assert.Single(await ListAsync(source, new FeatureQuery(1, fields: ["label", "made", "globalid"], identities: [1])));
        Assert.Equal(("one", new DateTime(2026, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc)), ((string?)now[0], (DateTime)now[1]!));
        Assert.Equal(Guid.Parse("1F0C0D2E-0000-4000-8000-000000000001"), now[2]);
        Assert.Equal((30d, 40d), (((Point)now.Geometry!).X, ((Point)now.Geometry!).Y));
        Assert.Equal(ones[0].HistoryId, (await history.VersionsAsync(1, None))[^1].RestoredFrom);

        Assert.Equal(RestoreResult.NoSuchVersion, (await history.RestoreAsync(1, deleted.HistoryId, "ayse", null, None)).Result);

        Assert.True(await history.SetKeepAsync(30, None));
        Assert.Equal(30, (await history.KeptAsync(None))?.KeepDays);
        Assert.Empty(await MySqlFeatureHistory.PruneAsync(MySqlFixture.Locator, None));

        await history.DisableAsync(None);
        Assert.False(await history.IsOnAsync(None));
        Assert.False((await source.DescribeAsync(None)).Archived);
        Assert.False(await history.SetKeepAsync(30, None));
    }

    private static async Task<List<Feature>> ListAsync(MySqlFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, None))
        {
            features.Add(feature);
        }

        return features;
    }

    private sealed class FailingProjector : IProjector
    {
        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Asked to move EPSG:{fromSrid} to EPSG:{toSrid}.");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked for a written reference.");

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to generalize.");

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to describe a projection.");
    }
}
