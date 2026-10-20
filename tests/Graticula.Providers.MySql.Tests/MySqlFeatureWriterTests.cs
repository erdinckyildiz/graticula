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

/// <summary>ADR-208 condition 3: a MySQL table edited under EditColumns' rules, as Oracle's and PostGIS's are.</summary>
[Trait("Needs", "MySql")]
public sealed class MySqlFeatureWriterTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static string Database => MySqlFixture.Source.Database;

    private static async Task<(MySqlFeatureSource Source, MySqlFeatureWriter Writer, LayerDefinition Layer)> TableAsync(
        string table, string? globalId = null, bool autoIncrement = true)
    {
        await MySqlFixture.ExecuteAsync(
            $"drop table if exists {table}",
            $"create table {table} (objectid int {(autoIncrement ? "auto_increment " : string.Empty)}primary key, label varchar(20), "
            + $"size_m double{(globalId is null ? string.Empty : ", globalid " + globalId)}, shape point not null srid 4326, spatial index (shape))");

        LayerDefinition layer = new(table, Database, table, "shape", 4326, "objectid", "objectid", isHosted: false);
        MySqlFeatureSource source = new(MySqlFixture.Locator, layer, new FailingProjector());
        LayerDescription described = await source.DescribeAsync(None);

        return (source, new MySqlFeatureWriter(MySqlFixture.Locator, layer, described.Fields, new FailingProjector()), layer);
    }

    private static FeatureAdd Add(string label, double x, double y) =>
        new(new Dictionary<string, object?> { ["label"] = label, ["size_m"] = 1.5 }, new Point(x, y));

    private static Task<List<Feature>> ReadAsync(MySqlFeatureSource source) => ListAsync(source, new FeatureQuery(100, fields: ["label"]));

    private static async Task<List<Feature>> ListAsync(MySqlFeatureSource source, FeatureQuery query)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query, None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task A_feature_is_added_updated_and_deleted_with_its_shape_longitude_first()
    {
        (MySqlFeatureSource source, MySqlFeatureWriter writer, _) = await TableAsync("zzw_edit");

        Assert.True((await source.DescribeAsync(None)).Writable);

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("one", 32.85, 39.93)], [], []), None)).Adds);
        Assert.True(added.Succeeded, added.Error);

        Feature read = Assert.Single(await ReadAsync(source));
        Assert.Equal(("one", 32.85, 39.93), ((string?)read[0], ((Point)read.Geometry!).X, ((Point)read.Geometry!).Y));

        EditOutcome updated = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(added.Identity, new Dictionary<string, object?> { ["label"] = "two" }, new Point(30, 40))], []), None);
        Assert.True(Assert.Single(updated.Updates).Succeeded, updated.Updates[0].Error);

        read = Assert.Single(await ReadAsync(source));
        Assert.Equal(("two", 30d, 40d), ((string?)read[0], ((Point)read.Geometry!).X, ((Point)read.Geometry!).Y));

        // An update writing what the row already holds is still a success: MySqlConnector counts the rows matched.
        EditOutcome same = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(added.Identity, new Dictionary<string, object?> { ["label"] = "two" }, null)], []), None);
        Assert.True(Assert.Single(same.Updates).Succeeded, same.Updates[0].Error);

        EditOutcome deleted = await writer.ApplyAsync(new EditBatch([], [], [added.Identity, added.Identity + 100], RollbackOnFailure: false), None);
        Assert.True(deleted.Deletes[0].Succeeded);
        Assert.True(deleted.Deletes[1].NoSuchFeature);
        Assert.Empty(await ReadAsync(source));
    }

    [Fact]
    public async Task A_shape_with_an_elevation_and_a_table_without_auto_increment_are_refused_by_name()
    {
        (_, MySqlFeatureWriter writer, _) = await TableAsync("zzw_refused");

        EditResult z = Assert.Single((await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(new Dictionary<string, object?>(), Point.Create(30, 40, 5, null))], [], []), None)).Adds);
        Assert.False(z.Succeeded);
        Assert.Contains("Z", z.Error!, StringComparison.Ordinal);

        (_, MySqlFeatureWriter plain, _) = await TableAsync("zzw_plain_key", autoIncrement: false);
        EditResult add = Assert.Single((await plain.ApplyAsync(new EditBatch([Add("x", 30, 40)], [], []), None)).Adds);
        Assert.Contains("AUTO_INCREMENT", add.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zzw_guid_braced", "char(38)")]
    [InlineData("zzw_guid_bare", "char(36)")]
    [InlineData("zzw_guid_binary", "binary(16)")]
    public async Task A_globalid_column_is_minted_answered_filtered_and_looked_up(string table, string kept)
    {
        (MySqlFeatureSource source, MySqlFeatureWriter writer, LayerDefinition layer) = await TableAsync(table, kept);

        Assert.Equal("globalid", GlobalIds.FieldOf((await source.DescribeAsync(None)).Fields));

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("g", 30, 40)], [], []), None)).Adds);
        Guid minted = Assert.NotNull(added.GlobalId);

        Feature read = Assert.Single(await ListAsync(source, new FeatureQuery(10, fields: ["globalid"])));
        Assert.Equal(minted, Assert.IsType<Guid>(read[0]));

        Assert.True(WhereClause.TryParse(
            $"globalid = '{minted:D}'", ["objectid", "globalid"], LayerDefinition.Quote, out ParsedWhere where, out string? error,
            new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase) { ["objectid"] = FieldType.Integer, ["globalid"] = FieldType.Guid }),
            error);
        Assert.Equal(1, await source.CountAsync(new FeatureQuery(1, where: where), None));

        IReadOnlyDictionary<Guid, long> ids = await MySqlFeatureWriter.ObjectIdsOfAsync(MySqlFixture.Locator, layer, "globalid", [minted, Guid.NewGuid()], None);
        Assert.Equal(added.Identity, Assert.Single(ids).Value);

        Assert.Equal(minted, Assert.Single((await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(added.Identity, new Dictionary<string, object?> { ["label"] = "h" }, null)], []), None)).Updates).GlobalId);
        Assert.Equal(minted, Assert.Single((await writer.ApplyAsync(new EditBatch([], [], [added.Identity]), None)).Deletes).GlobalId);
    }

    /// <summary>ADR-209: MariaDB's own UUID column keeps a GlobalID too. MySQL has no such type, so there it is not asked.</summary>
    [Fact]
    public async Task A_globalid_in_a_mariadb_uuid_column_is_minted_answered_filtered_and_looked_up()
    {
        if (!MySqlFixture.IsMariaDb)
        {
            return;
        }

        await A_globalid_column_is_minted_answered_filtered_and_looked_up("zzw_guid_uuid", "uuid");
    }

    [Fact]
    public async Task An_edit_conditional_on_a_row_version_goes_through_at_that_version_and_not_after_it_moved()
    {
        (MySqlFeatureSource source, MySqlFeatureWriter writer, _) = await TableAsync("zzw_version");
        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("a", 30, 40)], [], []), None)).Adds).Identity;

        string held = Assert.IsType<string>(await source.VersionOfAsync(id, None));

        EditBatch Conditional(string label, string version) => new(
            [], [new FeatureUpdate(id, new Dictionary<string, object?> { ["label"] = label }, null)], [],
            Expects: new Dictionary<long, IReadOnlyList<string>> { [id] = [version] });

        Assert.True(Assert.Single((await writer.ApplyAsync(Conditional("b", held), None)).Updates).Succeeded);

        EditResult second = Assert.Single((await writer.ApplyAsync(Conditional("c", held), None)).Updates);
        Assert.False(second.Succeeded);
        Assert.True(second.VersionMoved);
    }

    [Fact]
    public async Task Two_layers_edits_are_kept_or_discarded_together()
    {
        (MySqlFeatureSource a, _, LayerDefinition layerA) = await TableAsync("zzw_together_a");
        await MySqlFixture.ExecuteAsync(
            "drop table if exists zzw_together_b",
            "create table zzw_together_b (objectid int auto_increment primary key, label varchar(20), req varchar(5) not null, "
            + "shape point not null srid 4326, spatial index (shape))");
        LayerDefinition layerB = new("zzw_together_b", Database, "zzw_together_b", "shape", 4326, "objectid", "objectid", isHosted: false);
        MySqlFeatureSource b = new(MySqlFixture.Locator, layerB, new FailingProjector());

        async Task<MySqlFeatureWriter.LayerBatch> PartAsync(LayerDefinition layer, MySqlFeatureSource source, EditBatch batch) =>
            new(layer, (await source.DescribeAsync(None)).Fields, null, null, batch);

        IReadOnlyList<EditOutcome> refused = await MySqlFeatureWriter.ApplyTogetherAsync(
            MySqlFixture.Locator,
            [
                await PartAsync(layerA, a, new EditBatch([Add("a", 30, 40)], [], [])),
                await PartAsync(layerB, b, new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["label"] = "b" }, new Point(30, 40))], [], [])),
            ],
            new FailingProjector(), null, None);

        Assert.All(refused, o => Assert.True(o.RolledBack));
        Assert.Equal(0, await a.CountAsync(new FeatureQuery(1), None));

        IReadOnlyList<EditOutcome> kept = await MySqlFeatureWriter.ApplyTogetherAsync(
            MySqlFixture.Locator,
            [
                await PartAsync(layerA, a, new EditBatch([Add("a", 30, 40)], [], [])),
                await PartAsync(layerB, b, new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["label"] = "b", ["req"] = "y" }, new Point(30, 40))], [], [])),
            ],
            new FailingProjector(), null, None);

        Assert.All(kept, o => Assert.False(o.RolledBack));
        Assert.Equal(1, await a.CountAsync(new FeatureQuery(1), None));
        Assert.Equal(1, await b.CountAsync(new FeatureQuery(1), None));
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
