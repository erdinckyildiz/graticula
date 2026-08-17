using System;
using System.Collections.Generic;
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
/// The write path.
/// </summary>
/// <remarks>
/// The first code in this project that can destroy data, so most of these are
/// about what it refuses. Each test builds its own table in the fixture's
/// private schema, so a failure cannot leave anything behind for the next one.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostGisFeatureWriterTests : PostgresFixture
{
    private const int Srid = 3857;

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A 2D table with an integer id, a text column and a nullable one.</summary>
    private async Task<LayerDefinition> FlatTableAsync(string name)
    {
        await ExecuteAsync(
            $"""
            create table "{SchemaName}"."{name}" (
                objectid serial primary key,
                label     text,
                rating    integer not null default 1,
                geom      geometry(Point, {Srid})
            )
            """);

        return Layer(name);
    }

    private LayerDefinition Layer(string name) => new(
        name: name,
        schemaName: SchemaName,
        tableName: name,
        geometryColumn: "geom",
        srid: Srid,
        identityColumn: "objectid",
        objectIdColumn: "objectid",
        isHosted: true);

    private async Task<PostGisFeatureWriter> WriterFor(LayerDefinition layer)
    {
        LayerDescription description =
            await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);

        return new PostGisFeatureWriter(DataSource, layer, description.Fields);
    }

    private static Point At(double x, double y) => new(x, y);

    private static FeatureAdd Add(string? label, Geometry? geometry, int rating = 1) =>
        new(new Dictionary<string, object?> { ["label"] = label, ["rating"] = rating }, geometry);

    private async Task<long> CountAsync(string name)
    {
        await using NpgsqlCommand command =
            DataSource.CreateCommand($"select count(*) from \"{SchemaName}\".\"{name}\"");

        return (long)(await command.ExecuteScalarAsync())!;
    }

    // ---------- the happy paths ----------

    [Fact]
    public async Task An_add_inserts_the_row_and_returns_its_new_object_id()
    {
        LayerDefinition layer = await FlatTableAsync("adds");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add("first", At(10, 20))], [], []), CancellationToken.None);

        EditResult result = Assert.Single(outcome.Adds);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.ObjectId > 0);

        await using NpgsqlCommand check = DataSource.CreateCommand(
            $"select label, st_x(geom), st_y(geom) from \"{SchemaName}\".\"adds\" where objectid = @id");
        check.Parameters.AddWithValue("id", result.ObjectId);

        await using NpgsqlDataReader reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("first", reader.GetString(0));
        Assert.Equal(10, reader.GetDouble(1));
        Assert.Equal(20, reader.GetDouble(2));
    }

    [Fact]
    public async Task An_attribute_only_update_leaves_the_geometry_alone()
    {
        // Null geometry means unchanged, not cleared. Reading it the other way
        // erases a feature's location on every attribute edit.
        LayerDefinition layer = await FlatTableAsync("attrs");
        PostGisFeatureWriter writer = await WriterFor(layer);

        long id = (await writer.ApplyAsync(
            new EditBatch([Add("before", At(1, 2))], [], []), CancellationToken.None))
            .Adds[0].ObjectId;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(id, new Dictionary<string, object?> { ["label"] = "after" }, null)], []),
            CancellationToken.None);

        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);

        await using NpgsqlCommand check = DataSource.CreateCommand(
            $"select label, st_x(geom) from \"{SchemaName}\".\"attrs\" where objectid = {id}");
        await using NpgsqlDataReader reader = await check.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal("after", reader.GetString(0));
        Assert.Equal(1, reader.GetDouble(1));
    }

    [Fact]
    public async Task A_delete_removes_the_row()
    {
        LayerDefinition layer = await FlatTableAsync("dels");
        PostGisFeatureWriter writer = await WriterFor(layer);

        long id = (await writer.ApplyAsync(
            new EditBatch([Add("doomed", At(1, 2))], [], []), CancellationToken.None))
            .Adds[0].ObjectId;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [], [id]), CancellationToken.None);

        Assert.True(outcome.Deletes[0].Succeeded);
        Assert.Equal(0, await CountAsync("dels"));
    }

    // ---------- what it refuses ----------

    [Fact]
    public async Task Writing_two_dimensions_over_a_three_dimensional_feature_is_refused()
    {
        // ADR-008 §4.5a, and the reason it exists. The client read this feature
        // flat, because that is all our reader produces; letting it write back
        // would discard a Z it never knew about and report success.
        await ExecuteAsync(
            $"""
            create table "{SchemaName}"."solid" (
                objectid serial primary key,
                label    text,
                geom     geometry(PointZ, {Srid})
            )
            """);

        await ExecuteAsync(
            $"""
            insert into "{SchemaName}"."solid" (label, geom)
            values ('tower', st_setsrid(st_makepoint(1, 2, 30), {Srid}))
            """);

        LayerDefinition layer = Layer("solid");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [],
                [new FeatureUpdate(1, new Dictionary<string, object?> { ["label"] = "flattened" }, At(9, 9))],
                [],
                RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Contains("Z ordinate", outcome.Updates[0].Error!, StringComparison.Ordinal);

        // And the row is untouched: the label did not change either, because the
        // refusal happened before anything was written.
        await using NpgsqlCommand check = DataSource.CreateCommand(
            $"select label, st_z(geom) from \"{SchemaName}\".\"solid\" where objectid = 1");
        await using NpgsqlDataReader reader = await check.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal("tower", reader.GetString(0));
        Assert.Equal(30, reader.GetDouble(1));
    }

    [Fact]
    public async Task An_attribute_only_edit_to_a_three_dimensional_feature_is_still_allowed()
    {
        // The refusal is about geometry, not about the feature. Refusing every
        // edit to a 3D row would make the layer read-only for a reason the
        // client cannot see.
        await ExecuteAsync(
            $"""
            create table "{SchemaName}"."solid2" (
                objectid serial primary key,
                label    text,
                geom     geometry(PointZ, {Srid})
            );
            insert into "{SchemaName}"."solid2" (label, geom)
            values ('tower', st_setsrid(st_makepoint(1, 2, 30), {Srid}))
            """);

        PostGisFeatureWriter writer = await WriterFor(Layer("solid2"));

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [], [new FeatureUpdate(1, new Dictionary<string, object?> { ["label"] = "renamed" }, null)], []),
            CancellationToken.None);

        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);
    }

    [Fact]
    public async Task A_column_that_does_not_exist_is_refused_rather_than_reaching_SQL()
    {
        // ADR-008 §4.6. A column name cannot be parameterised, so the only safe
        // handling is a whitelist taken from the database.
        LayerDefinition layer = await FlatTableAsync("guard");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [new FeatureAdd(new Dictionary<string, object?> { ["label\" = '', \"rating"] = 9 }, null)],
                [], [], RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("not a column", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("guard"));
    }

    [Fact]
    public async Task The_geometry_column_cannot_be_set_as_an_attribute()
    {
        LayerDefinition layer = await FlatTableAsync("geomattr");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [new FeatureAdd(new Dictionary<string, object?> { ["geom"] = "POINT(1 2)" }, null)],
                [], [], RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("geometry column", outcome.Adds[0].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_object_id_in_an_update_is_ignored_rather_than_refused()
    {
        // Every ArcGIS client round-trips it in the attributes. Refusing would
        // fail every well-behaved edit.
        LayerDefinition layer = await FlatTableAsync("oid");
        PostGisFeatureWriter writer = await WriterFor(layer);

        long id = (await writer.ApplyAsync(
            new EditBatch([Add("x", At(1, 1))], [], []), CancellationToken.None)).Adds[0].ObjectId;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [],
                [new FeatureUpdate(id, new Dictionary<string, object?>
                {
                    ["objectid"] = id,
                    ["label"] = "y",
                }, null)],
                []),
            CancellationToken.None);

        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);
    }

    [Fact]
    public async Task Updating_or_deleting_a_feature_that_does_not_exist_fails_that_feature_only()
    {
        LayerDefinition layer = await FlatTableAsync("missing");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [],
                [new FeatureUpdate(9999, new Dictionary<string, object?> { ["label"] = "ghost" }, null)],
                [8888],
                RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.False(outcome.Deletes[0].Succeeded);
        Assert.Contains("9999", outcome.Updates[0].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_constraint_violation_becomes_a_result_not_an_exception()
    {
        LayerDefinition layer = await FlatTableAsync("notnull");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [new FeatureAdd(new Dictionary<string, object?> { ["rating"] = null }, At(1, 1))],
                [], [], RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("cannot be null", outcome.Adds[0].Error!, StringComparison.Ordinal);
    }

    // ---------- the transaction ----------

    [Fact]
    public async Task RollbackOnFailure_abandons_the_whole_batch_and_still_says_which_one_failed()
    {
        LayerDefinition layer = await FlatTableAsync("rollback");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add("good", At(1, 1)), new FeatureAdd(new Dictionary<string, object?> { ["rating"] = null }, null)],
                [], [], RollbackOnFailure: true),
            CancellationToken.None);

        Assert.True(outcome.RolledBack);
        Assert.True(outcome.Adds[0].Succeeded);
        Assert.False(outcome.Adds[1].Succeeded);

        // The good row is gone with the bad one, and the per-feature results
        // survive so the client can see which feature caused it.
        Assert.Equal(0, await CountAsync("rollback"));
    }

    [Fact]
    public async Task Without_rollback_the_good_edits_are_kept()
    {
        LayerDefinition layer = await FlatTableAsync("partial");
        PostGisFeatureWriter writer = await WriterFor(layer);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add("good", At(1, 1)), new FeatureAdd(new Dictionary<string, object?> { ["rating"] = null }, null)],
                [], [], RollbackOnFailure: false),
            CancellationToken.None);

        Assert.False(outcome.RolledBack);
        Assert.Equal(1, await CountAsync("partial"));
    }

    [Fact]
    public async Task A_layer_with_no_integer_object_id_cannot_be_constructed_as_writable()
    {
        // ADR-013 §2a: without a unique integer key there is no way to name a
        // row for update or delete, so editing is unexpressible rather than
        // unsupported.
        LayerDefinition noOid = new(
            name: "noid", schemaName: SchemaName, tableName: "noid", geometryColumn: "geom",
            srid: Srid, identityColumn: "uid", objectIdColumn: null, isHosted: true);

        Assert.Throws<ArgumentException>(
            () => new PostGisFeatureWriter(DataSource, noOid, []));
    }
}
