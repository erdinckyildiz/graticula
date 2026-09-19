using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A hosted layer's history, kept by the database — ADR-078.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the hosted schema, because that is the only place history can exist.</b> The table is made
/// the way the importer makes one — an identity column <c>generated always</c> — under a name no
/// other run shares, and dropped with its history afterwards.
/// </para>
/// <para>
/// <b>Moments are read from the database's clock between transactions</b>, never from this
/// process's: the versions are stamped by PostgreSQL, and comparing them with a moment taken
/// elsewhere would test two clocks rather than the history.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FeatureHistoryTests : PostgresFixture
{
    private const int Srid = 3857;
    private const string Me = "history_editor";

    [Fact]
    public async Task An_edit_through_the_server_and_one_straight_to_the_database_are_both_kept()
    {
        await using Layer layer = await LayerAsync();
        long first = await layer.AddAsync("first");

        await layer.History.EnableAsync(CancellationToken.None);

        long second = await layer.AddAsync("second");
        await layer.UpdateAsync(first, "first, edited");

        // What QGIS does: a write that never passed through this server.
        await ExecuteAsync($"update {layer.Table} set label = 'edited in psql' where objectid = {second}");

        IReadOnlyList<FeatureChange> changes = await layer.History.ChangesAsync(null, 50, CancellationToken.None);

        Assert.Equal(3, changes.Count);

        FeatureChange direct = changes[0];
        Assert.Equal(second, direct.ObjectId);
        Assert.Equal("updated", direct.Kind);
        Assert.True(direct.Direct, "A write straight to the database was attributed to an account.");

        FeatureChange edited = changes[1];
        Assert.Equal(first, edited.ObjectId);
        Assert.Equal("updated", edited.Kind);
        Assert.Equal(Me, edited.Editor);
        Assert.False(edited.Direct);

        FeatureChange added = changes[2];
        Assert.Equal(second, added.ObjectId);
        Assert.Equal("added", added.Kind);
        Assert.Equal(Me, added.Editor);
    }

    [Fact]
    public async Task A_batch_that_is_rolled_back_leaves_no_version()
    {
        await using Layer layer = await LayerAsync();
        long id = await layer.AddAsync("kept");
        await layer.History.EnableAsync(CancellationToken.None);

        // One good update and one that fails, all or nothing: the good one must leave no trace.
        EditOutcome outcome = await layer.Writer.ApplyAsync(
            new EditBatch([], [Change(id, "never happened"), Change(987_654, "missing")], [], Editor: Me),
            CancellationToken.None);

        Assert.True(outcome.RolledBack);
        Assert.Empty(await layer.History.ChangesAsync(null, 50, CancellationToken.None));
        Assert.Single(await layer.History.VersionsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task A_historic_moment_reads_the_layer_as_it_was()
    {
        await using Layer layer = await LayerAsync();
        long a = await layer.AddAsync("a, before");
        long b = await layer.AddAsync("b");
        await layer.History.EnableAsync(CancellationToken.None);

        DateTimeOffset beforeEdits = await NowAsync();

        await layer.UpdateAsync(a, "a, after");
        await layer.DeleteAsync(b);
        long c = await layer.AddAsync("c, new");

        DateTimeOffset afterEdits = await NowAsync();

        PostGisFeatureSource source = layer.Source;

        Assert.Equal(
            ["a, before", "b"],
            await LabelsAsync(source, beforeEdits));

        Assert.Equal(
            ["a, after", "c, new"],
            await LabelsAsync(source, afterEdits));

        // The same moment through the other reads a query can make.
        Assert.Equal(2, await source.CountAsync(Query(beforeEdits), CancellationToken.None));
        Assert.Equal([a, b], await source.ObjectIdsAsync(Query(beforeEdits), CancellationToken.None));
        Assert.Equal([a, c], await source.ObjectIdsAsync(Query(afterEdits), CancellationToken.None));

        // And the present is untouched by any of it.
        Assert.Equal(["a, after", "c, new"], await LabelsAsync(source, null));
    }

    [Fact]
    public async Task Two_updates_in_one_batch_are_one_version()
    {
        await using Layer layer = await LayerAsync();
        long id = await layer.AddAsync("start");
        await layer.History.EnableAsync(CancellationToken.None);

        await using (NpgsqlConnection connection = await DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            foreach (string label in (string[])["middle", "end"])
            {
                await using NpgsqlCommand command = new(
                    $"update {layer.Table} set label = @label where objectid = @id", connection, transaction);
                command.Parameters.AddWithValue("label", label);
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        IReadOnlyList<FeatureVersion> versions = await layer.History.VersionsAsync(id, CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.Equal("start", versions[0].Attributes.GetProperty("label").GetString());
        Assert.Equal("end", versions[1].Attributes.GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_restored_feature_is_the_version_and_the_restore_is_itself_a_version()
    {
        await using Layer layer = await LayerAsync(tracked: true);
        long id = await layer.AddAsync("original");
        await layer.History.EnableAsync(CancellationToken.None);
        await layer.UpdateAsync(id, "vandalised");

        IReadOnlyList<FeatureVersion> before = await layer.History.VersionsAsync(id, CancellationToken.None);
        long original = before[0].HistoryId;

        RestoreOutcome outcome = await layer.History.RestoreAsync(
            id, original, "restorer", layer.Tracking, CancellationToken.None);

        Assert.Equal(RestoreResult.Updated, outcome.Result);
        Assert.Equal("original", await ScalarAsync<string>($"select label from {layer.Table} where objectid = {id}"));

        // The tracked editor is the one who restored it, not whoever wrote the version.
        Assert.Equal("restorer", await ScalarAsync<string>($"select last_edited_user from {layer.Table} where objectid = {id}"));

        IReadOnlyList<FeatureVersion> after = await layer.History.VersionsAsync(id, CancellationToken.None);
        Assert.Equal(3, after.Count);
        Assert.Equal("restorer", after[^1].Editor);

        // And it says it is a restore, and of which version, rather than passing for a hand edit.
        Assert.Equal(original, after[^1].RestoredFrom);
        Assert.Null(after[1].RestoredFrom);
        Assert.Equal("restored", (await layer.History.ChangesAsync(null, 1, CancellationToken.None))[0].Kind);
    }

    [Fact]
    public async Task A_deleted_feature_comes_back_under_its_own_object_id()
    {
        await using Layer layer = await LayerAsync();
        long id = await layer.AddAsync("gone");
        long other = await layer.AddAsync("stays");
        await layer.History.EnableAsync(CancellationToken.None);
        await layer.DeleteAsync(id);

        FeatureVersion last = (await layer.History.VersionsAsync(id, CancellationToken.None))[^1];
        Assert.Equal("delete", last.ClosedBy);

        RestoreOutcome outcome = await layer.History.RestoreAsync(
            id, last.HistoryId, "restorer", null, CancellationToken.None);

        Assert.Equal(RestoreResult.Recreated, outcome.Result);
        Assert.True(outcome.AttachmentsNotRestored);
        Assert.Equal("gone", await ScalarAsync<string>($"select label from {layer.Table} where objectid = {id}"));

        // The shape came back too.
        Assert.True(await ScalarAsync<bool>($"select st_equals(geom, st_setsrid(st_point(1, 2), {Srid})) from {layer.Table} where objectid = {id}"));

        // And the identity sequence did not move under the other row.
        long next = await layer.AddAsync("after");
        Assert.True(next > other && next != id);
    }

    [Fact]
    public async Task Emptying_the_table_ends_every_feature()
    {
        await using Layer layer = await LayerAsync();
        await layer.AddAsync("one");
        await layer.AddAsync("two");
        await layer.History.EnableAsync(CancellationToken.None);

        DateTimeOffset full = await NowAsync();
        await ExecuteAsync($"truncate {layer.Table}");

        IReadOnlyList<FeatureChange> changes = await layer.History.ChangesAsync(null, 50, CancellationToken.None);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal("emptied", change.Kind));

        Assert.Equal(2, await layer.Source.CountAsync(Query(full), CancellationToken.None));
    }

    [Fact]
    public async Task The_description_says_whether_the_layer_keeps_its_history_and_turning_it_off_removes_it()
    {
        await using Layer layer = await LayerAsync();
        await layer.AddAsync("one");

        Assert.False((await layer.Source.DescribeAsync(CancellationToken.None)).Archived);

        Assert.Equal(1, await layer.History.EnableAsync(CancellationToken.None));
        Assert.True((await layer.Source.DescribeAsync(CancellationToken.None)).Archived);

        await layer.History.DisableAsync(CancellationToken.None);

        Assert.False((await layer.Source.DescribeAsync(CancellationToken.None)).Archived);
        Assert.True(await ScalarAsync<bool>($"select to_regclass('hosted.\"{layer.Name}__history\"') is null"));
        Assert.Equal(0L, await ScalarAsync<long>(
            $"select count(*) from pg_trigger where tgrelid = 'hosted.\"{layer.Name}\"'::regclass and not tgisinternal"));

        // Writes still work with the trigger gone.
        await layer.AddAsync("two");
    }

    [Fact]
    public async Task A_table_outside_the_hosted_schema_is_refused()
    {
        await ExecuteAsync($"create table \"{SchemaName}\".registered (objectid integer generated always as identity primary key, geom geometry(Point, {Srid}))");

        LayerDefinition registered = new(
            name: "registered",
            schemaName: SchemaName,
            tableName: "registered",
            geometryColumn: "geom",
            srid: Srid,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: false);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PostGisFeatureHistory(DataSource, registered).EnableAsync(CancellationToken.None));

        Assert.Contains("ADR-002", refused.Message, StringComparison.Ordinal);
        Assert.False((await new PostGisFeatureSource(DataSource, registered).DescribeAsync(CancellationToken.None)).Archived);
    }

    // ---------- helpers ----------

    private static FeatureQuery Query(DateTimeOffset? moment) =>
        new(100, fields: ["label"], orderBy: [new SortKey("objectid", Descending: false)]) { HistoricMoment = moment };

    private static async Task<List<string?>> LabelsAsync(PostGisFeatureSource source, DateTimeOffset? moment)
    {
        List<string?> labels = [];

        await foreach (Feature feature in source.ReadAsync(Query(moment), CancellationToken.None))
        {
            labels.Add(feature["label"] as string);
        }

        return labels;
    }

    private async Task<DateTimeOffset> NowAsync()
    {
        // Past the end of the transaction that wrote last, and before the next one begins.
        // Npgsql reads a timestamptz as a UTC DateTime.
        DateTimeOffset now = new(await ScalarAsync<DateTime>("select clock_timestamp()"));
        await Task.Delay(5);
        return now;
    }

    private static FeatureUpdate Change(long id, string label) =>
        new(id, new Dictionary<string, object?> { ["label"] = label }, null);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Layer> LayerAsync(bool tracked = false)
    {
        string name = "history_" + Guid.NewGuid().ToString("n")[..10];

        await ExecuteAsync("create schema if not exists hosted");
        await ExecuteAsync(
            $"""
             create table hosted."{name}" (
                 objectid integer generated always as identity primary key,
                 label text,
                 geom geometry(Point, {Srid})
                 {(tracked ? ", created_user text, created_date timestamptz, last_edited_user text, last_edited_date timestamptz" : string.Empty)}
             )
             """);

        LayerDefinition definition = new(
            name: name,
            schemaName: PostGisImporter.HostedSchema,
            tableName: name,
            geometryColumn: "geom",
            srid: Srid,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

        PostGisFeatureSource source = new(DataSource, definition);
        LayerDescription described = await source.DescribeAsync(CancellationToken.None);

        if (tracked)
        {
            described = FieldOverrides.Apply(
                described,
                [
                    new FieldOverride("created_user", null, false, EditRole.Creator),
                    new FieldOverride("created_date", null, false, EditRole.Created),
                    new FieldOverride("last_edited_user", null, false, EditRole.Editor),
                    new FieldOverride("last_edited_date", null, false, EditRole.Edited),
                ]);
        }

        return new Layer(
            this,
            name,
            source,
            new PostGisFeatureWriter(DataSource, definition, described.Fields, described.Tracking),
            new PostGisFeatureHistory(DataSource, definition),
            described.Tracking);
    }

    private sealed class Layer(
        FeatureHistoryTests owner,
        string name,
        PostGisFeatureSource source,
        PostGisFeatureWriter writer,
        PostGisFeatureHistory history,
        EditorTracking tracking) : IAsyncDisposable
    {
        public string Name => name;

        public string Table => $"hosted.\"{name}\"";

        public PostGisFeatureSource Source => source;

        public PostGisFeatureWriter Writer => writer;

        public PostGisFeatureHistory History => history;

        public EditorTracking Tracking => tracking;

        public async Task<long> AddAsync(string label)
        {
            EditOutcome outcome = await writer.ApplyAsync(
                new EditBatch(
                    [new FeatureAdd(new Dictionary<string, object?> { ["label"] = label }, new Point(1, 2))],
                    [], [], Editor: Me),
                CancellationToken.None);

            EditResult added = Assert.Single(outcome.Adds);
            Assert.True(added.Succeeded, added.Error);
            return added.Identity;
        }

        public async Task UpdateAsync(long id, string label)
        {
            EditOutcome outcome = await writer.ApplyAsync(
                new EditBatch([], [Change(id, label)], [], Editor: Me), CancellationToken.None);

            EditResult changed = Assert.Single(outcome.Updates);
            Assert.True(changed.Succeeded, changed.Error);
        }

        public async Task DeleteAsync(long id)
        {
            EditOutcome outcome = await writer.ApplyAsync(
                new EditBatch([], [], [id], Editor: Me), CancellationToken.None);

            EditResult deleted = Assert.Single(outcome.Deletes);
            Assert.True(deleted.Succeeded, deleted.Error);
        }

        public async ValueTask DisposeAsync() =>
            await owner.ExecuteAsync($"drop table if exists hosted.\"{name}__history\", hosted.\"{name}\"");
    }
}
