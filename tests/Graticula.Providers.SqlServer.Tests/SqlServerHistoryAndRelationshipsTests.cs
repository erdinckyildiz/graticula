using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using SortKey = Graticula.Features.SortKey;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// ADR-210 condition 5's remainder, ADR-212 condition 2: a SQL Server layer's history, kept by a trigger beside its table —
/// every version, the editor this server names and the login another tool connects as, a read at a moment, a restore,
/// a keeping period and switching it off — and a relationship followed in one query, as MySQL's and Oracle's are.
/// </summary>
[Trait("Needs", "SqlServer")]
public sealed class SqlServerHistoryAndRelationshipsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly Guid Global = Guid.Parse("1F0C0D2E-0000-4000-8000-000000000001");

    [Fact]
    public async Task A_history_records_every_version_answers_a_moment_and_restores_a_deleted_feature()
    {
        const string Table = "zzt_history";

        await SqlServerFixture.ExecuteAsync(
            $"drop trigger if exists dbo.{Table}__hist",
            $"drop table if exists dbo.{Table}__history",
            $"drop table if exists dbo.{Table}",
            $"create table dbo.{Table} (objectid int identity primary key, label nvarchar(20), size_m float, made datetime2(3), "
            + "globalid uniqueidentifier, amount decimal(10, 2), flag bit, code varbinary(8), shape geometry)",
            $"insert into dbo.{Table} (label, size_m, made, globalid, amount, flag, code, shape) values "
            + $"(N'bir ğ', 1.5, '2026-01-02T03:04:05.5', '{Global}', 12.25, 1, 0x0A0B, geometry::STGeomFromText('POINT(30 40)', 4326)), "
            + "(N'two', 2.5, null, null, null, 0, null, geometry::STGeomFromText('POINT(31 41)', 4326))");

        LayerDefinition layer = new(Table, "dbo", Table, "shape", 4326, "objectid", "objectid", isHosted: false);
        SqlServerFeatureSource.Forget(SqlServerFixture.Locator, layer);
        SqlServerFeatureHistory history = new(SqlServerFixture.Locator, layer);
        SqlServerFeatureSource source = new(SqlServerFixture.Locator, layer, new NoProjector());

        Assert.False((await source.DescribeAsync(None)).Archived);
        Assert.Equal(2, await history.EnableAsync(None));
        Assert.True(await history.IsOnAsync(None));
        Assert.True((await source.DescribeAsync(None)).Archived);

        // Switching it on again is not a second seed.
        Assert.Equal(2, await history.EnableAsync(None));

        await Task.Delay(200);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await Task.Delay(200);

        LayerDescription described = await source.DescribeAsync(None);
        SqlServerFeatureWriter writer = new(SqlServerFixture.Locator, layer, described.Fields, new NoProjector());

        EditOutcome edited = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(1, new Dictionary<string, object?> { ["label"] = "changed" }, new Point(35, 45))], [2], Editor: "ayse"),
            None);
        Assert.True(edited.AllSucceeded, string.Join("; ", edited.Updates.Concat(edited.Deletes).Select(r => r.Error)));

        await SqlServerFixture.ExecuteAsync($"update dbo.{Table} set label = N'by sql' where objectid = 1");

        IReadOnlyList<FeatureChange> changes = await history.ChangesAsync(null, 10, None);
        Assert.Contains(changes, c => c is { ObjectId: 1, Kind: "updated", Editor: "ayse", Direct: false });
        Assert.Contains(changes, c => c is { ObjectId: 2, Kind: "deleted", Editor: "ayse", Direct: false });
        Assert.Contains(changes, c => c.ObjectId == 1 && c.Kind == "updated" && c.Direct && c.Editor.Length > 0);
        Assert.DoesNotContain(changes, c => c.Kind == "existed");

        // The layer as it was: both features, every attribute read back as its column is declared, and the shapes then.
        List<Feature> then = await ListAsync(source, new FeatureQuery(
            10, fields: ["label", "size_m", "made", "globalid", "amount", "flag"], orderBy: [new SortKey("objectid", false)]) { HistoricMoment = before });

        Assert.Equal(["bir ğ", "two"], then.Select(f => (string?)f[0]));
        Assert.Equal([1.5, 2.5], then.Select(f => Convert.ToDouble(f[1], CultureInfo.InvariantCulture)));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc), Assert.IsType<DateTime>(then[0][2]));
        Assert.Null(then[1][2]);
        Assert.Equal(Global, then[0][3]);
        Assert.Equal(12.25, Convert.ToDouble(then[0][4], CultureInfo.InvariantCulture));
        Assert.Equal((30d, 40d), (((Point)then[0].Geometry!).X, ((Point)then[0].Geometry!).Y));

        Assert.Equal(1, await source.CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(new Point(30, 40), SpatialRelation.Intersects)) { HistoricMoment = before }, None));
        Assert.Equal(0, await source.CountAsync(new FeatureQuery(1, spatial: new SpatialFilter(new Point(30, 40), SpatialRelation.Intersects)), None));
        Assert.Equal(2, await source.CountAsync(new FeatureQuery(10) { HistoricMoment = before }, None));
        Assert.Equal([1L, 2L], (await source.ObjectIdsAsync(new FeatureQuery(10) { HistoricMoment = before }, None)).Order());

        IReadOnlyList<FeatureVersion> ones = await history.VersionsAsync(1, None);
        Assert.Equal(["seed", "update", "update"], ones.Select(v => v.OpenedBy));
        Assert.Equal("bir ğ", ones[0].Attributes.GetProperty("label").GetString());
        Assert.Contains("[30", ones[0].GeoJson!, StringComparison.Ordinal);
        Assert.Contains("[35", ones[1].GeoJson!, StringComparison.Ordinal);

        FeatureVersion deleted = Assert.Single(await history.VersionsAsync(2, None));
        Assert.Equal("delete", deleted.ClosedBy);

        RestoreOutcome restored = await history.RestoreAsync(2, deleted.HistoryId, "ayse", null, None);
        Assert.Equal(RestoreResult.Recreated, restored.Result);
        Assert.Equal(1, await source.CountAsync(new FeatureQuery(1, identities: [2]), None));
        Assert.Contains(await history.ChangesAsync(null, 10, None), c => c is { ObjectId: 2, Kind: "restored", Editor: "ayse" });

        // A restore of an existing feature writes back every recorded column — the date, the GlobalID, the binary — and
        // the shape.
        RestoreOutcome back = await history.RestoreAsync(1, ones[0].HistoryId, "ayse", null, None);
        Assert.Equal(RestoreResult.Updated, back.Result);
        Feature now = Assert.Single(await ListAsync(source, new FeatureQuery(1, fields: ["label", "made", "globalid", "amount"], identities: [1])));
        Assert.Equal(("bir ğ", new DateTime(2026, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc)), ((string?)now[0], (DateTime)now[1]!));
        Assert.Equal(Global, now[2]);
        Assert.Equal((30d, 40d), (((Point)now.Geometry!).X, ((Point)now.Geometry!).Y));
        Assert.Equal(ones[0].HistoryId, (await history.VersionsAsync(1, None))[^1].RestoredFrom);

        await using (Microsoft.Data.SqlClient.SqlConnection connection = await SqlServerConnections.OpenAsync(
            SqlServerConnections.For(SqlServerFixture.Locator, pooling: true), None))
        await using (Microsoft.Data.SqlClient.SqlCommand code = new($"select code from dbo.{Table} where objectid = 1", connection))
        {
            Assert.Equal(new byte[] { 0x0A, 0x0B }, (byte[])(await code.ExecuteScalarAsync(None))!);
        }

        Assert.Equal(RestoreResult.NoSuchVersion, (await history.RestoreAsync(1, deleted.HistoryId, "ayse", null, None)).Result);

        Assert.True(await history.SetKeepAsync(30, None));
        Assert.Equal(30, (await history.KeptAsync(None))?.KeepDays);
        Assert.True(await history.SetKeepAsync(60, None));
        Assert.Equal(60, (await history.KeptAsync(None))?.KeepDays);
        Assert.Empty(await SqlServerFeatureHistory.PruneAsync(SqlServerFixture.Locator, None));

        // A version that ended long ago goes with the keeping period; a current one never does.
        await SqlServerFixture.ExecuteAsync(
            $"update dbo.{Table}__history set gdb_from_date = dateadd(day, -100, gdb_from_date), gdb_to_date = dateadd(day, -99, gdb_from_date) "
            + "where opened_by = 'seed' and objectid = 1");
        (string pruned, long versions) = Assert.Single(await SqlServerFeatureHistory.PruneAsync(SqlServerFixture.Locator, None));
        Assert.Equal($"dbo.{Table}__history", pruned);
        Assert.Equal(1, versions);

        await history.DisableAsync(None);
        Assert.False(await history.IsOnAsync(None));
        Assert.False((await source.DescribeAsync(None)).Archived);
        Assert.False(await history.SetKeepAsync(30, None));

        // Editing goes on after it is off.
        Assert.True((await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(1, new Dictionary<string, object?> { ["label"] = "after" }, null)], [], Editor: "ayse"), None)).AllSucceeded);
    }

    [Fact]
    public async Task A_geography_layer_keeps_a_history_and_a_table_is_refused_one()
    {
        const string Table = "zzt_history_geog";

        await SqlServerFixture.ExecuteAsync(
            $"drop trigger if exists dbo.{Table}__hist",
            $"drop table if exists dbo.{Table}__history",
            $"drop table if exists dbo.{Table}",
            $"create table dbo.{Table} (objectid int identity primary key, label nvarchar(max), shape geography)",
            $"insert into dbo.{Table} (label, shape) values (N'{new string('x', 5000)}', geography::STGeomFromText('POINT(30 40)', 4326))");

        LayerDefinition layer = new(Table, "dbo", Table, "shape", 4326, "objectid", "objectid", isHosted: false);
        SqlServerFeatureSource.Forget(SqlServerFixture.Locator, layer);
        SqlServerFeatureHistory history = new(SqlServerFixture.Locator, layer);
        SqlServerFeatureSource source = new(SqlServerFixture.Locator, layer, new NoProjector());

        Assert.Equal(1, await history.EnableAsync(None));
        await Task.Delay(200);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await Task.Delay(200);
        await SqlServerFixture.ExecuteAsync($"update dbo.{Table} set label = N'short', shape = geography::STGeomFromText('POINT(31 41)', 4326)");

        // A value past 4,000 characters reads back whole, and a geography shape as it was.
        Feature then = Assert.Single(await ListAsync(source, new FeatureQuery(1, fields: ["label"]) { HistoricMoment = before }));
        Assert.Equal(5000, ((string)then[0]!).Length);
        Assert.Equal((30d, 40d), (((Point)then.Geometry!).X, ((Point)then.Geometry!).Y));

        await history.DisableAsync(None);

        LayerDefinition table = new("zzt_history_table", "dbo", "zzt_history_table", null, 4326, "objectid", "objectid", isHosted: false);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqlServerFeatureHistory(SqlServerFixture.Locator, table).EnableAsync(None));
        Assert.Contains("ADR-213", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Related_rows_come_back_grouped_by_origin_feature_within_the_limit()
    {
        await SqlServerFixture.ExecuteAsync(
            "drop table if exists dbo.zzt_rel_child",
            "drop table if exists dbo.zzt_rel_parent",
            "create table dbo.zzt_rel_parent (objectid int primary key, code varchar(5), shape geometry)",
            "create table dbo.zzt_rel_child (objectid int primary key, parent_code varchar(5), reading float)",
            "insert into dbo.zzt_rel_parent values (1, 'A', geometry::STGeomFromText('POINT(30 40)', 4326)), "
            + "(2, 'B', geometry::STGeomFromText('POINT(30 40)', 4326)), (3, 'C', geometry::STGeomFromText('POINT(30 40)', 4326))",
            "insert into dbo.zzt_rel_child values (10, 'A', 1.5), (11, 'A', 2.5), (12, 'B', 3.5)");

        LayerDefinition parent = new("zzt_rel_parent", "dbo", "zzt_rel_parent", "shape", 4326, "objectid", "objectid", isHosted: false);
        LayerDefinition child = new("zzt_rel_child", "dbo", "zzt_rel_child", null, 4326, "objectid", "objectid", isHosted: false);

        SqlServerRelatedRecords related = new(SqlServerFixture.Locator, parent, child, sameDatabase: true);
        FieldDescription[] fields = [new("objectid", FieldType.Integer, false, null), new("reading", FieldType.Double, true, null)];

        IReadOnlyList<RelatedGroup> groups = await related.QueryAsync("code", "parent_code", [1, 2, 3], fields, 100, None);

        Assert.Equal([1L, 2L], groups.Select(g => g.ObjectId));
        Assert.Equal([1.5, 2.5], groups[0].Records.Select(r => (double)r["reading"]!).Order());
        Assert.Equal(12, Assert.Single(groups[1].Records)["objectid"]);

        Assert.Equal(2, (await related.QueryAsync("code", "parent_code", [1, 2], fields, 2, None)).Sum(g => g.Records.Count));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqlServerRelatedRecords(SqlServerFixture.Locator, parent, child, sameDatabase: false).QueryAsync("code", "parent_code", [1], fields, 10, None));
    }

    private static async Task<List<Feature>> ListAsync(SqlServerFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, None))
        {
            features.Add(feature);
        }

        return features;
    }

    private sealed class NoProjector : IProjector
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
