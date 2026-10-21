using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>ADR-210 as amended by Q-188: a SQL Server table edited under EditColumns' rules, as MySQL's and Oracle's are.</summary>
[Trait("Needs", "SqlServer")]
public sealed class SqlServerFeatureWriterTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(SqlServerFeatureSource Source, SqlServerFeatureWriter Writer, LayerDefinition Layer)> TableAsync(
        string table, string type = "geometry", bool identity = true, bool globalId = false)
    {
        await SqlServerFixture.ExecuteAsync(
            $"drop table if exists dbo.{table}",
            $"create table dbo.{table} (objectid int {(identity ? "identity(1, 1) " : string.Empty)}primary key, label nvarchar(20), "
            + $"size_m float{(globalId ? ", globalid uniqueidentifier" : string.Empty)}, shape {type})");

        LayerDefinition layer = new(table, "dbo", table, "shape", 4326, "objectid", "objectid", isHosted: false);
        SqlServerFeatureSource source = new(SqlServerFixture.Locator, layer, new StillProjector());
        LayerDescription described = await source.DescribeAsync(None);

        return (source, new SqlServerFeatureWriter(SqlServerFixture.Locator, layer, described.Fields, new StillProjector()), layer);
    }

    private static FeatureAdd Add(string label, Geometry shape) =>
        new(new Dictionary<string, object?> { ["label"] = label, ["size_m"] = 1.5 }, shape);

    private static async Task<List<Feature>> ReadAsync(SqlServerFeatureSource source, FeatureQuery? query = null)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(query ?? new FeatureQuery(100, fields: ["label"]), None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task A_feature_is_added_updated_and_deleted_with_its_shape_longitude_first()
    {
        (SqlServerFeatureSource source, SqlServerFeatureWriter writer, _) = await TableAsync("zzw_edit");

        Assert.True((await source.DescribeAsync(None)).Writable);

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("one", new Point(32.85, 39.93))], [], []), None)).Adds);
        Assert.True(added.Succeeded, added.Error);
        Assert.Equal(1, added.Identity);

        Feature read = Assert.Single(await ReadAsync(source));
        Assert.Equal(("one", 32.85, 39.93), ((string?)read[0], ((Point)read.Geometry!).X, ((Point)read.Geometry!).Y));

        EditOutcome updated = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(added.Identity, new Dictionary<string, object?> { ["label"] = "two" }, new Point(30, 40))], []), None);
        Assert.True(Assert.Single(updated.Updates).Succeeded, updated.Updates[0].Error);

        read = Assert.Single(await ReadAsync(source));
        Assert.Equal(("two", 30d, 40d), ((string?)read[0], ((Point)read.Geometry!).X, ((Point)read.Geometry!).Y));

        EditOutcome same = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(added.Identity, new Dictionary<string, object?> { ["label"] = "two" }, null)], []), None);
        Assert.True(Assert.Single(same.Updates).Succeeded, same.Updates[0].Error);

        EditOutcome deleted = await writer.ApplyAsync(new EditBatch([], [], [added.Identity, added.Identity + 100], RollbackOnFailure: false), None);
        Assert.True(deleted.Deletes[0].Succeeded);
        Assert.True(deleted.Deletes[1].NoSuchFeature);
        Assert.Empty(await ReadAsync(source));
    }

    [Fact]
    public async Task A_clockwise_polygon_in_a_geography_column_is_the_polygon_drawn_not_the_rest_of_the_earth()
    {
        (SqlServerFeatureSource source, SqlServerFeatureWriter writer, _) = await TableAsync("zzw_geo", type: "geography");

        // ArcGIS's outer ring, clockwise: a square degree over Ankara.
        Polygon square = new(new LinearRing(XySequence.Wrap([32, 39, 32, 40, 33, 40, 33, 39, 32, 39])));
        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("square", square)], [], []), None)).Adds);
        Assert.True(added.Succeeded, added.Error);

        await using SqlConnection connection = await SqlServerConnections.OpenAsync(SqlServerConnections.For(SqlServerFixture.Locator, pooling: true), None);
        await using SqlCommand area = new("select shape.STArea() from dbo.zzw_geo", connection);
        double squareMetres = Convert.ToDouble(await area.ExecuteScalarAsync(None), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(squareMetres, 9.0e9, 9.8e9);

        Envelope box = Assert.Single(await ReadAsync(source)).Geometry!.Envelope;
        Assert.Equal((32d, 39d, 33d, 40d), (box.MinX, box.MinY, box.MaxX, box.MaxY));
    }

    [Fact]
    public async Task A_shape_with_an_elevation_and_a_table_that_makes_no_key_are_refused_by_name()
    {
        (_, SqlServerFeatureWriter writer, _) = await TableAsync("zzw_refused");

        EditResult z = Assert.Single((await writer.ApplyAsync(
            new EditBatch([new FeatureAdd(new Dictionary<string, object?>(), Point.Create(30, 40, 5, null))], [], []), None)).Adds);
        Assert.False(z.Succeeded);
        Assert.Contains("Z", z.Error!, StringComparison.Ordinal);

        (_, SqlServerFeatureWriter plain, _) = await TableAsync("zzw_plain_key", identity: false);
        EditResult add = Assert.Single((await plain.ApplyAsync(new EditBatch([Add("x", new Point(30, 40))], [], []), None)).Adds);
        Assert.Contains("IDENTITY", add.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_column_and_a_long_value_are_said_by_their_column()
    {
        await SqlServerFixture.ExecuteAsync(
            "drop table if exists dbo.zzw_rules",
            "create table dbo.zzw_rules (objectid int identity primary key, label nvarchar(5) not null, shape geometry)");

        LayerDefinition layer = new("zzw_rules", "dbo", "zzw_rules", "shape", 4326, "objectid", "objectid", isHosted: false);
        SqlServerFeatureSource source = new(SqlServerFixture.Locator, layer, new StillProjector());
        SqlServerFeatureWriter writer = new(SqlServerFixture.Locator, layer, (await source.DescribeAsync(None)).Fields, new StillProjector());

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch(
            [
                new FeatureAdd(new Dictionary<string, object?>(), new Point(30, 40)),
                new FeatureAdd(new Dictionary<string, object?> { ["label"] = "much too long" }, new Point(30, 40)),
                new FeatureAdd(new Dictionary<string, object?> { ["label"] = "fine" }, new Point(30, 40)),
            ],
            [], [], RollbackOnFailure: false), None);

        Assert.Contains("'label' is required", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Contains("longer than", outcome.Adds[1].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Adds[2].Succeeded, outcome.Adds[2].Error);
        Assert.Single(await ReadAsync(source));
    }

    [Fact]
    public async Task A_globalid_is_minted_answered_filtered_and_looked_up()
    {
        (SqlServerFeatureSource source, SqlServerFeatureWriter writer, LayerDefinition layer) = await TableAsync("zzw_guid", globalId: true);

        Assert.Equal("globalid", GlobalIds.FieldOf((await source.DescribeAsync(None)).Fields));

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("g", new Point(30, 40))], [], []), None)).Adds);
        Guid minted = Assert.NotNull(added.GlobalId);

        Feature read = Assert.Single(await ReadAsync(source, new FeatureQuery(10, fields: ["globalid"])));
        Assert.Equal(minted, Assert.IsType<Guid>(read[0]));

        Assert.True(WhereClause.TryParse(
            $"globalid = '{minted:B}'", ["objectid", "globalid"], LayerDefinition.Quote, out ParsedWhere where, out string? error,
            new Dictionary<string, FieldType>(StringComparer.OrdinalIgnoreCase) { ["objectid"] = FieldType.Integer, ["globalid"] = FieldType.Guid }),
            error);
        Assert.Equal(1, await source.CountAsync(new FeatureQuery(1, where: where), None));

        IReadOnlyDictionary<Guid, long> ids = await SqlServerFeatureWriter.ObjectIdsOfAsync(SqlServerFixture.Locator, layer, "globalid", [minted, Guid.NewGuid()], None);
        Assert.Equal(added.Identity, Assert.Single(ids).Value);

        Assert.Equal(minted, Assert.Single((await writer.ApplyAsync(new EditBatch([], [], [added.Identity]), None)).Deletes).GlobalId);
    }

    [Fact]
    public async Task An_edit_conditional_on_a_version_is_refused_once_the_row_has_moved()
    {
        (SqlServerFeatureSource source, SqlServerFeatureWriter writer, _) = await TableAsync("zzw_version");

        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("v", new Point(30, 40))], [], []), None)).Adds).Identity;
        string? before = await source.VersionOfAsync(id, None);
        Assert.NotNull(before);

        EditResult first = Assert.Single((await writer.ApplyAsync(new EditBatch(
            [], [new FeatureUpdate(id, new Dictionary<string, object?> { ["label"] = "w" }, null)], [],
            Expects: new Dictionary<long, IReadOnlyList<string>> { [id] = [before] }), None)).Updates);
        Assert.True(first.Succeeded, first.Error);

        string? after = await source.VersionOfAsync(id, None);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);

        EditResult stale = Assert.Single((await writer.ApplyAsync(new EditBatch(
            [], [new FeatureUpdate(id, new Dictionary<string, object?> { ["label"] = "x" }, null)], [],
            Expects: new Dictionary<long, IReadOnlyList<string>> { [id] = [before] }), None)).Updates);
        Assert.True(stale.VersionMoved);
    }

    private sealed class StillProjector : IProjector
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

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Asked to describe a projection.");
    }
}
