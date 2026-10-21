using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// ADR-211: a table of an attached DuckDB file edited under EditColumns' rules, as every registered engine's is — through
/// the source's own sandboxed instance, without savepoints.
/// </summary>
public sealed class DuckDbFeatureWriterTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryFolder _temporary = new();
    private readonly string _file;

    public DuckDbFeatureWriterTests()
    {
        _file = _temporary.File("edits.duckdb").Replace('\\', '/');

        using DuckDBConnection writer = new($"DataSource={_file}");
        writer.Open();
        Execute(writer, "create sequence places_id start 1");
        Execute(writer, "create table places (objectid bigint primary key default nextval('places_id'), label varchar not null, "
            + "size_m double, globalid uuid, shape GEOMETRY)");
        Execute(writer, "create table fixed_key (objectid bigint primary key, label varchar, shape GEOMETRY)");
        Execute(writer, "checkpoint");
    }

    public void Dispose() => _temporary.Dispose();

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private GeoParquetFolder Open() => new(new AttachedDuckDb(_file, null, null, 4326), new GeoParquetOptions { MemoryLimit = "256MB", Threads = 2 });

    private static LayerDefinition Layer(string table) => new(table, "main", table, "shape", 4326, "objectid", "objectid", isHosted: false);

    private static async Task<(GeoParquetFeatureSource Source, DuckDbFeatureWriter Writer)> LayerAsync(GeoParquetFolder database, string table)
    {
        GeoParquetFeatureSource source = new(database, Layer(table), new ShiftingProjector());
        LayerDescription described = await source.DescribeAsync(None);
        return (source, new DuckDbFeatureWriter(database, Layer(table), described.Fields, new ShiftingProjector()));
    }

    private static FeatureAdd Add(string? label, double x, double y) =>
        new(new Dictionary<string, object?> { ["label"] = label, ["size_m"] = 1.5 }, new Point(x, y));

    private static async Task<List<Feature>> ReadAsync(GeoParquetFeatureSource source)
    {
        List<Feature> features = [];

        await foreach (Feature feature in source.ReadAsync(new FeatureQuery(100, fields: ["label"]), None))
        {
            features.Add(feature);
        }

        return features;
    }

    [Fact]
    public async Task A_feature_is_added_updated_and_deleted_and_the_table_s_version_moves_with_each()
    {
        using GeoParquetFolder database = Open();
        (GeoParquetFeatureSource source, DuckDbFeatureWriter writer) = await LayerAsync(database, "places");

        Assert.True((await source.DescribeAsync(None)).Writable);
        string? before = await source.CacheValidatorAsync(None);

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("one", 32.85, 39.93)], [], []), None)).Adds);
        Assert.True(added.Succeeded, added.Error);
        Assert.Equal(1, added.Identity);
        Assert.NotNull(added.GlobalId);

        Feature read = Assert.Single(await ReadAsync(source));
        Assert.Equal(("one", 32.85, 39.93), ((string?)read[0], ((Point)read.Geometry!).X, ((Point)read.Geometry!).Y));
        string? afterAdd = await source.CacheValidatorAsync(None);
        Assert.NotEqual(before, afterAdd);

        EditOutcome updated = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(added.Identity, new Dictionary<string, object?> { ["label"] = "two" }, new Point(30, 40))], []), None);
        Assert.True(Assert.Single(updated.Updates).Succeeded, updated.Updates[0].Error);

        read = Assert.Single(await ReadAsync(source));
        Assert.Equal(("two", 30d, 40d), ((string?)read[0], ((Point)read.Geometry!).X, ((Point)read.Geometry!).Y));
        Assert.NotEqual(afterAdd, await source.CacheValidatorAsync(None));

        EditOutcome deleted = await writer.ApplyAsync(new EditBatch([], [], [added.Identity, added.Identity + 100], RollbackOnFailure: false), None);
        Assert.True(deleted.Deletes[0].Succeeded);
        Assert.True(deleted.Deletes[1].NoSuchFeature);
        Assert.Empty(await ReadAsync(source));
    }

    [Fact]
    public async Task All_or_nothing_keeps_nothing_after_a_failure_and_says_why_the_rest_were_not_tried()
    {
        using GeoParquetFolder database = Open();
        (GeoParquetFeatureSource source, DuckDbFeatureWriter writer) = await LayerAsync(database, "places");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([Add("kept?", 30, 40), Add(null, 31, 41), Add("third", 32, 42)], [], []), None);

        Assert.True(outcome.RolledBack);
        Assert.True(outcome.Adds[0].Succeeded);
        Assert.Contains("'label' is required", outcome.Adds[1].Error!, StringComparison.Ordinal);
        Assert.Contains("savepoints", outcome.Adds[2].Error!, StringComparison.Ordinal);
        Assert.Empty(await ReadAsync(source));

        // Each alone: the failure costs only itself.
        EditOutcome alone = await writer.ApplyAsync(new EditBatch([Add("a", 30, 40), Add(null, 31, 41), Add("c", 32, 42)], [], [], RollbackOnFailure: false), None);
        Assert.False(alone.RolledBack);
        Assert.Equal([true, false, true], alone.Adds.Select(a => a.Succeeded));
        Assert.Equal(["a", "c"], (await ReadAsync(source)).Select(f => (string?)f[0]));
    }

    [Fact]
    public async Task An_edit_conditional_on_a_version_is_refused_once_the_row_has_moved()
    {
        using GeoParquetFolder database = Open();
        (GeoParquetFeatureSource source, DuckDbFeatureWriter writer) = await LayerAsync(database, "places");

        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("v", 30, 40)], [], []), None)).Adds).Identity;
        string? before = await source.VersionOfAsync(id, None);
        Assert.NotNull(before);

        EditResult first = Assert.Single((await writer.ApplyAsync(new EditBatch(
            [], [new FeatureUpdate(id, new Dictionary<string, object?> { ["label"] = "w" }, null)], [],
            Expects: new Dictionary<long, IReadOnlyList<string>> { [id] = [before] }), None)).Updates);
        Assert.True(first.Succeeded, first.Error);
        Assert.NotEqual(before, await source.VersionOfAsync(id, None));

        EditResult stale = Assert.Single((await writer.ApplyAsync(new EditBatch(
            [], [new FeatureUpdate(id, new Dictionary<string, object?> { ["label"] = "x" }, null)], [],
            Expects: new Dictionary<long, IReadOnlyList<string>> { [id] = [before] }), None)).Updates);
        Assert.True(stale.VersionMoved);
    }

    [Fact]
    public async Task A_globalid_is_minted_and_looked_up()
    {
        using GeoParquetFolder database = Open();
        (_, DuckDbFeatureWriter writer) = await LayerAsync(database, "places");

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add("g", 30, 40)], [], []), None)).Adds);
        Guid minted = Assert.NotNull(added.GlobalId);

        IReadOnlyDictionary<Guid, long> ids = DuckDbFeatureWriter.ObjectIdsOf(database, Layer("places"), "globalid", [minted, Guid.NewGuid()]);
        Assert.Equal(added.Identity, Assert.Single(ids).Value);
    }

    [Fact]
    public async Task A_key_the_database_does_not_make_and_a_row_number_identity_are_refused_by_name()
    {
        using GeoParquetFolder database = Open();
        (_, DuckDbFeatureWriter writer) = await LayerAsync(database, "fixed_key");

        EditResult add = Assert.Single((await writer.ApplyAsync(new EditBatch([new FeatureAdd(new Dictionary<string, object?> { ["label"] = "x" }, new Point(30, 40))], [], []), None)).Adds);
        Assert.Contains("nextval", add.Error!, StringComparison.Ordinal);

        LayerDefinition byRow = new("places", "main", "places", "shape", 4326, GeoParquetFolder.RowNumberColumn, GeoParquetFolder.RowNumberColumn, isHosted: false);
        Assert.Throws<QueryNotSupportedException>(() => new DuckDbFeatureWriter(database, byRow, [], new ShiftingProjector()));
    }

    [Fact]
    public async Task A_file_this_process_may_not_write_is_opened_to_read_and_says_it_cannot_be_edited()
    {
        File.SetAttributes(_file, FileAttributes.ReadOnly);

        try
        {
            using GeoParquetFolder database = Open();

            Assert.False(database.IsWritable);
            GeoParquetFeatureSource source = new(database, Layer("places"), new ShiftingProjector());
            Assert.False((await source.DescribeAsync(None)).Writable);
            Assert.Throws<QueryNotSupportedException>(() => new DuckDbFeatureWriter(database, Layer("places"), [], new ShiftingProjector()));
        }
        finally
        {
            File.SetAttributes(_file, FileAttributes.Normal);
        }
    }
}
