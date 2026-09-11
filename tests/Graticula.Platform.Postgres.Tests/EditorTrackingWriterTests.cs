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
/// The writer's half of editor tracking — ADR-064, and what closes D-20.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the overrides, not around them.</b> Each writer here is built from the table's own
/// description with the roles applied by <see cref="FieldOverrides.Apply"/>, which is the path the
/// server takes — so a role that failed to reach the writer fails here rather than in a client.
/// </para>
/// <para>
/// <b>What a client sends for a tracked column is replaced, and that is asserted rather than
/// assumed</b> (condition 2): the column is advertised not editable, and an ArcGIS web client
/// echoes every attribute back on an update anyway.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EditorTrackingWriterTests : PostgresFixture
{
    private const int Srid = 3857;
    private const string Me = "me_publisher";
    private const string Them = "somebody_else";

    [Fact]
    public async Task An_add_is_signed_by_the_account_and_dated_by_the_database()
    {
        PostGisFeatureWriter writer = await TrackedAsync("tr_adds");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add("first", ("created_user", "an impostor"), ("last_edited_date", "1999-01-01"))],
                [], [], Editor: Me),
            CancellationToken.None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);

        (string? creator, string? editor, bool created, bool edited) = await RowAsync("tr_adds", added.Identity);

        Assert.Equal(Me, creator);
        Assert.Equal(Me, editor);
        Assert.True(created, "The created column was not written.");
        Assert.True(edited, "The edited column was not written, or kept the client's 1999.");
    }

    [Fact]
    public async Task An_update_signs_the_editor_and_leaves_the_creator()
    {
        PostGisFeatureWriter writer = await TrackedAsync("tr_updates");
        long id = await RowWrittenByAsync("tr_updates", Them);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [], [Change(id, ("label", "changed"), ("created_user", Me))], [], Editor: Me),
            CancellationToken.None);

        EditResult changed = Assert.Single(outcome.Updates);
        Assert.True(changed.Succeeded, changed.Error);

        (string? creator, string? editor, _, bool edited) = await RowAsync("tr_updates", id);

        Assert.Equal(Them, creator);
        Assert.Equal(Me, editor);
        Assert.True(edited, "The edited column was not written.");
    }

    [Fact]
    public async Task Own_only_changes_and_deletes_your_own_and_refuses_the_rest()
    {
        PostGisFeatureWriter writer = await TrackedAsync("tr_own");

        long mine = await RowWrittenByAsync("tr_own", Me);
        long theirs = await RowWrittenByAsync("tr_own", Them);
        long nobodys = await RowWrittenByAsync("tr_own", null);

        EditOutcome updated = await writer.ApplyAsync(
            new EditBatch(
                [],
                [
                    Change(mine, ("label", "mine")),
                    Change(theirs, ("label", "not mine")),
                    Change(nobodys, ("label", "nobody's")),
                    Change(999_999, ("label", "missing")),
                ],
                [],
                RollbackOnFailure: false,
                Editor: Me,
                OwnOnly: true),
            CancellationToken.None);

        Assert.True(updated.Updates[0].Succeeded, updated.Updates[0].Error);

        Assert.False(updated.Updates[1].Succeeded);
        Assert.False(updated.Updates[1].NoSuchFeature);
        Assert.Contains("somebody else", updated.Updates[1].Error!, StringComparison.Ordinal);

        // <b>A row nobody created is nobody's</b>, so it needs features:fullEdit too.
        Assert.False(updated.Updates[2].Succeeded);
        Assert.False(updated.Updates[2].NoSuchFeature);

        // And a row that is not there is still *not there*, not *not yours*.
        Assert.True(updated.Updates[3].NoSuchFeature);

        Assert.Equal("not mine-original", await LabelAsync("tr_own", theirs));

        EditOutcome deleted = await writer.ApplyAsync(
            new EditBatch([], [], [mine, theirs], RollbackOnFailure: false, Editor: Me, OwnOnly: true),
            CancellationToken.None);

        Assert.True(deleted.Deletes[0].Succeeded, deleted.Deletes[0].Error);
        Assert.False(deleted.Deletes[1].Succeeded);
        Assert.Contains("somebody else", deleted.Deletes[1].Error!, StringComparison.Ordinal);
        Assert.NotNull(await LabelAsync("tr_own", theirs));
    }

    [Fact]
    public async Task Without_own_only_anybody_s_feature_can_be_changed()
    {
        PostGisFeatureWriter writer = await TrackedAsync("tr_full");
        long theirs = await RowWrittenByAsync("tr_full", Them);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(theirs, ("label", "full edit"))], [], Editor: Me),
            CancellationToken.None);

        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);

        (string? creator, string? editor, _, _) = await RowAsync("tr_full", theirs);

        Assert.Equal(Them, creator);
        Assert.Equal(Me, editor);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<PostGisFeatureWriter> TrackedAsync(string name)
    {
        await ExecuteAsync(
            $"""
            create table "{SchemaName}"."{name}" (
                objectid         serial primary key,
                label            text,
                geom             geometry(Point, {Srid}),
                created_user     text,
                created_date     timestamptz,
                last_edited_user text,
                last_edited_date timestamptz
            )
            """);

        LayerDefinition layer = new(
            name: name,
            schemaName: SchemaName,
            tableName: name,
            geometryColumn: "geom",
            srid: Srid,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

        LayerDescription table =
            await new PostGisFeatureSource(DataSource, layer).DescribeAsync(CancellationToken.None);

        LayerDescription described = FieldOverrides.Apply(
            table,
            [
                new FieldOverride("created_user", null, false, EditRole.Creator),
                new FieldOverride("created_date", null, false, EditRole.Created),
                new FieldOverride("last_edited_user", null, false, EditRole.Editor),
                new FieldOverride("last_edited_date", null, false, EditRole.Edited),
            ]);

        Assert.True(described.Tracking.IsOn, "The roles did not reach the description.");

        return new PostGisFeatureWriter(DataSource, layer, described.Fields, described.Tracking);
    }

    private async Task<long> RowWrittenByAsync(string table, string? creator)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"insert into \"{SchemaName}\".\"{table}\" (label, created_user) "
            + "values (@label, @creator) returning objectid");
        command.Parameters.AddWithValue("label", (creator == Them ? "not mine" : creator ?? "none") + "-original");
        command.Parameters.Add(new NpgsqlParameter("creator", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)creator ?? DBNull.Value,
        });

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string? Creator, string? Editor, bool Created, bool Edited)> RowAsync(
        string table, long id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select created_user, last_edited_user, created_date is not null, "
            + $"last_edited_date > now() - interval '1 hour' "
            + $"from \"{SchemaName}\".\"{table}\" where objectid = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No row {id} in {table}.");

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2),
            !reader.IsDBNull(3) && reader.GetBoolean(3));
    }

    private async Task<string?> LabelAsync(string table, long id)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            $"select label from \"{SchemaName}\".\"{table}\" where objectid = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteScalarAsync() as string;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static FeatureAdd Add(string label, params (string Column, object? Value)[] also)
    {
        Dictionary<string, object?> attributes = new() { ["label"] = label };

        foreach ((string column, object? value) in also)
        {
            attributes[column] = value;
        }

        return new FeatureAdd(attributes, new Point(1, 2));
    }

    private static FeatureUpdate Change(long id, params (string Column, object? Value)[] values)
    {
        Dictionary<string, object?> attributes = [];

        foreach ((string column, object? value) in values)
        {
            attributes[column] = value;
        }

        return new FeatureUpdate(id, attributes, null);
    }
}
