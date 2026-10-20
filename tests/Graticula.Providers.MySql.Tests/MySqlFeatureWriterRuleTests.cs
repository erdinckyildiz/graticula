using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using MySqlConnector;
using Xunit;

namespace Graticula.Providers.MySql.Tests;

/// <summary>
/// ADR-208 condition 3, as ADR-205 condition 6 held Oracle to it: the MySQL writer against the PostGIS writer's own rule
/// tests — <c>PostGisFeatureWriterTests</c>, <c>DomainWriterTests</c> and <c>EditorTrackingWriterTests</c> — ported from
/// <c>MySqlFeatureWriterTests</c> case by case to MySQL tables made for them, asserting what they assert.
/// </summary>
/// <remarks>
/// <b>Where MySQL differs, the case says so rather than being dropped</b>: a spatially indexed column cannot be null, so an
/// add that sends no shape is given one; MySQL stores no Z, so the two three-dimensional cases become one refusal; and a
/// key a trigger fills is refused like a plain one, because MySQL reports no id for it.
/// </remarks>
[Trait("Needs", "MySql")]
public sealed class MySqlFeatureWriterRuleTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private const string Me = "me_publisher";
    private const string Them = "somebody_else";

    private static string Owner => MySqlFixture.Source.Database;

    private static readonly FieldDomain Materials = FieldDomain.Coded(
        "Material",
        [
            new CodedValue(DomainValue.Of("CU"), "Copper"),
            new CodedValue(DomainValue.Of("PVC"), "PVC"),
            new CodedValue(DomainValue.Of("DI"), "Ductile iron"),
        ]);

    private static readonly FieldDomain Diameters = FieldDomain.Range("Diameter", DomainValue.Of(50), DomainValue.Of(1200));

    // ------------------------------------------------------------------ PostGisFeatureWriterTests

    [Fact]
    public async Task A_required_field_left_empty_is_named_and_the_table_is_not()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_required", extra: "req varchar(20) not null");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([Add(("label", "x"))], [], []), None);

        string error = Assert.Single(outcome.Adds).Error!;
        Assert.Contains("'req' is required", error, StringComparison.Ordinal);
        Assert.DoesNotContain("zzw_required", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_add_inserts_the_row_and_returns_its_new_object_id()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_add");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(new Point(32.85, 39.93), ("label", "Ankara"))], [], []), None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);
        Assert.True(added.Identity > 0);
        Assert.Equal("Ankara", await LabelAsync("zzw_add", added.Identity));
        Assert.Equal((32.85, 39.93), await PointAsync("zzw_add", added.Identity));
    }

    [Fact]
    public async Task An_attribute_only_update_leaves_the_geometry_alone()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_update");
        long id = Assert.Single((await writer.ApplyAsync(
            new EditBatch([Add(new Point(29, 41), ("label", "before"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [Change(id, ("label", "after"))], []), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded);
        Assert.Equal("after", await LabelAsync("zzw_update", id));
        Assert.Equal((29.0, 41.0), await PointAsync("zzw_update", id));
    }

    [Fact]
    public async Task A_delete_removes_the_row()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_delete");
        long id = Assert.Single((await writer.ApplyAsync(
            new EditBatch([Add(("label", "gone"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [], [id]), None);

        Assert.True(Assert.Single(outcome.Deletes).Succeeded);
        Assert.Equal(0L, await CountAsync("zzw_delete"));
    }

    /// <summary>MySQL stores x and y only: an update sending a Z is refused by name, and the row keeps its shape.</summary>
    [Fact]
    public async Task An_update_sending_a_z_ordinate_is_refused_and_the_row_keeps_its_shape()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_z_update");
        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add(new Point(29, 41), ("label", "flat"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(id, new Dictionary<string, object?>(), Point.Create(30, 40, 1000, null))], []), None);

        EditResult refused = Assert.Single(outcome.Updates);
        Assert.False(refused.Succeeded);
        Assert.Contains("Z", refused.Error!, StringComparison.Ordinal);
        Assert.Equal((29.0, 41.0), await PointAsync("zzw_z_update", id));
    }

    [Fact]
    public async Task A_column_that_does_not_exist_is_refused_rather_than_reaching_SQL()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_no_column");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("label\"; drop table x; --", "x"))], [], []), None);

        Assert.Contains("is not a column of this layer", Assert.Single(outcome.Adds).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_geometry_column_cannot_be_set_as_an_attribute()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_shape_attr");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([Add(("shape", "POINT(1 1)"))], [], []), None);

        Assert.Contains("is the geometry column", Assert.Single(outcome.Adds).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_object_id_in_an_update_is_ignored_rather_than_refused()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_oid");
        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add(("label", "a"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("objectid", id + 1000), ("label", "b"))], []), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded, outcome.Updates[0].Error);
        Assert.Equal("b", await LabelAsync("zzw_oid", id));
    }

    [Fact]
    public async Task Updating_or_deleting_a_feature_that_does_not_exist_fails_that_feature_only()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_missing");
        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add(("label", "a"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("label", "b")), Change(999_999, ("label", "c"))], [999_998],
                RollbackOnFailure: false),
            None);

        Assert.True(outcome.Updates[0].Succeeded);
        Assert.True(outcome.Updates[1].NoSuchFeature);
        Assert.True(outcome.Deletes[0].NoSuchFeature);
        Assert.Equal("b", await LabelAsync("zzw_missing", id));
    }

    [Fact]
    public async Task A_constraint_violation_becomes_a_result_not_an_exception()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_unique", extra: "code varchar(10) unique");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("code", "A")), Add(("code", "A"))], [], [], RollbackOnFailure: false), None);

        Assert.True(outcome.Adds[0].Succeeded);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("must be unique", outcome.Adds[1].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackOnFailure_abandons_the_whole_batch_and_still_says_which_one_failed()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_rollback", extra: "code varchar(10) unique");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("code", "A")), Add(("code", "A")), Add(("code", "B"))], [], []), None);

        Assert.True(outcome.RolledBack);
        Assert.True(outcome.Adds[0].Succeeded);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Equal(0L, await CountAsync("zzw_rollback"));
    }

    [Fact]
    public async Task Without_rollback_the_good_edits_are_kept()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_partial", extra: "code varchar(10) unique");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("code", "A")), Add(("code", "A")), Add(("code", "B"))], [], [], RollbackOnFailure: false),
            None);

        Assert.False(outcome.RolledBack);
        Assert.Equal(2L, await CountAsync("zzw_partial"));
    }

    // ------------------------------------------------------------------ DomainWriterTests

    [Fact]
    public async Task An_add_outside_a_column_s_domain_is_refused_by_name_and_writes_nothing()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_adds", domains: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add(("material", "CU"), ("diameter", 300)), Add(("material", "LEAD"), ("diameter", 300))],
                [], [], RollbackOnFailure: false),
            None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("'Material'", outcome.Adds[1].Error!, StringComparison.Ordinal);
        Assert.Contains("'CU' (Copper)", outcome.Adds[1].Error!, StringComparison.Ordinal);

        EditOutcome range = await writer.ApplyAsync(
            new EditBatch([Add(("material", "PVC"), ("diameter", 5000))], [], []), None);

        Assert.False(range.Adds[0].Succeeded);
        Assert.Contains("outside the domain 'Diameter'", range.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync("zzw_dm_adds"));
    }

    [Fact]
    public async Task An_update_outside_a_domain_is_refused_and_the_row_is_unchanged()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_updates", domains: true);
        long id = await RowAsync("zzw_dm_updates", kind: null, material: "CU", diameter: 100);

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [Change(id, ("diameter", 40))], []), None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Equal(100L, await NumberAsync("zzw_dm_updates", "diameter", id));

        EditOutcome cleared = await writer.ApplyAsync(new EditBatch([], [Change(id, ("diameter", null))], []), None);
        Assert.True(cleared.Updates[0].Succeeded, cleared.Updates[0].Error);
    }

    [Fact]
    public async Task The_subtype_column_takes_only_subtype_codes()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_kinds", domains: true, subtypes: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("kind", (short)9), ("material", "CU"))], [], []), None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("not one of this layer's subtypes", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("zzw_dm_kinds"));
    }

    [Fact]
    public async Task A_subtype_s_domain_governs_its_features_and_only_its_features()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_sub_adds", domains: true, subtypes: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [
                    Add(("kind", (short)1), ("material", "DI")),
                    Add(("kind", (short)1), ("material", "PVC")),
                    Add(("kind", (short)2), ("material", "PVC")),
                    Add(("material", "PVC")),
                ],
                [], [], RollbackOnFailure: false),
            None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("for subtype 1 (Main)", outcome.Adds[1].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Adds[2].Succeeded, outcome.Adds[2].Error);
        Assert.True(outcome.Adds[3].Succeeded, outcome.Adds[3].Error);
    }

    [Fact]
    public async Task An_update_that_does_not_say_its_subtype_is_checked_against_the_one_it_is()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_sub_upd", domains: true, subtypes: true);
        long main = await RowAsync("zzw_dm_sub_upd", kind: 1, material: "CU", diameter: 300);
        long lateral = await RowAsync("zzw_dm_sub_upd", kind: 2, material: "CU", diameter: 300);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(main, ("material", "PVC")), Change(lateral, ("material", "PVC"))], [],
                RollbackOnFailure: false),
            None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Contains("for subtype 1 (Main)", outcome.Updates[0].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Updates[1].Succeeded, outcome.Updates[1].Error);

        EditOutcome moved = await writer.ApplyAsync(
            new EditBatch([], [Change(main, ("kind", (short)2), ("material", "PVC"))], []), None);

        Assert.True(moved.Updates[0].Succeeded, moved.Updates[0].Error);
    }

    [Fact]
    public async Task A_move_to_a_subtype_that_disallows_a_kept_value_is_refused_by_name()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_sub_move", domains: true, subtypes: true);
        long lateral = await RowAsync("zzw_dm_sub_move", kind: 2, material: "PVC", diameter: 300);

        EditOutcome refused = await writer.ApplyAsync(new EditBatch([], [Change(lateral, ("kind", (short)1))], []), None);

        Assert.False(refused.Updates[0].Succeeded);
        Assert.Contains("'material'", refused.Updates[0].Error!, StringComparison.Ordinal);
        Assert.Contains("Send 'material'", refused.Updates[0].Error!, StringComparison.Ordinal);

        EditOutcome moved = await writer.ApplyAsync(
            new EditBatch([], [Change(lateral, ("kind", (short)1), ("material", "CU"))], []), None);
        Assert.True(moved.Updates[0].Succeeded, moved.Updates[0].Error);

        long legacy = await RowAsync("zzw_dm_sub_move", kind: 1, material: "PVC", diameter: 300);
        EditOutcome same = await writer.ApplyAsync(
            new EditBatch([], [Change(legacy, ("kind", (short)1), ("diameter", 400))], []), None);
        Assert.True(same.Updates[0].Succeeded, same.Updates[0].Error);
    }

    [Fact]
    public async Task A_refused_value_in_an_all_or_nothing_batch_takes_the_batch_with_it()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_dm_batch", domains: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("material", "CU")), Add(("material", "LEAD"))], [], []), None);

        Assert.True(outcome.RolledBack);
        Assert.Equal(0L, await CountAsync("zzw_dm_batch"));
    }

    // ------------------------------------------------------------------ EditorTrackingWriterTests

    [Fact]
    public async Task An_add_is_signed_by_the_account_and_dated_by_the_database()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_tr_adds", tracked: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add(("label", "first"), ("created_user", "an impostor"), ("last_edited_date", "1999-01-01"))],
                [], [], Editor: Me),
            None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);

        (string? creator, string? editor, bool created, bool edited) = await TrackedRowAsync("zzw_tr_adds", added.Identity);
        Assert.Equal(Me, creator);
        Assert.Equal(Me, editor);
        Assert.True(created, "The created column was not written.");
        Assert.True(edited, "The edited column was not written, or kept the client's 1999.");
    }

    [Fact]
    public async Task An_update_signs_the_editor_and_leaves_the_creator()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_tr_updates", tracked: true);
        long id = await RowWrittenByAsync("zzw_tr_updates", Them);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("label", "changed"), ("created_user", Me))], [], Editor: Me), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded, outcome.Updates[0].Error);

        (string? creator, string? editor, _, bool edited) = await TrackedRowAsync("zzw_tr_updates", id);
        Assert.Equal(Them, creator);
        Assert.Equal(Me, editor);
        Assert.True(edited, "The edited column was not written.");
    }

    [Fact]
    public async Task Own_only_changes_and_deletes_your_own_and_refuses_the_rest()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_tr_own", tracked: true);
        long mine = await RowWrittenByAsync("zzw_tr_own", Me);
        long theirs = await RowWrittenByAsync("zzw_tr_own", Them);
        long nobodys = await RowWrittenByAsync("zzw_tr_own", null);

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
            None);

        Assert.True(updated.Updates[0].Succeeded, updated.Updates[0].Error);
        Assert.False(updated.Updates[1].Succeeded);
        Assert.False(updated.Updates[1].NoSuchFeature);
        Assert.Contains("somebody else", updated.Updates[1].Error!, StringComparison.Ordinal);
        Assert.False(updated.Updates[2].Succeeded);
        Assert.False(updated.Updates[2].NoSuchFeature);
        Assert.True(updated.Updates[3].NoSuchFeature);
        Assert.Equal("not mine-original", await LabelAsync("zzw_tr_own", theirs));

        EditOutcome deleted = await writer.ApplyAsync(
            new EditBatch([], [], [mine, theirs], RollbackOnFailure: false, Editor: Me, OwnOnly: true), None);

        Assert.True(deleted.Deletes[0].Succeeded, deleted.Deletes[0].Error);
        Assert.False(deleted.Deletes[1].Succeeded);
        Assert.Contains("somebody else", deleted.Deletes[1].Error!, StringComparison.Ordinal);
        Assert.NotNull(await LabelAsync("zzw_tr_own", theirs));
    }

    [Fact]
    public async Task Without_own_only_anybody_s_feature_can_be_changed()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_tr_full", tracked: true);
        long theirs = await RowWrittenByAsync("zzw_tr_full", Them);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(theirs, ("label", "full edit"))], [], Editor: Me), None);

        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);

        (string? creator, string? editor, _, _) = await TrackedRowAsync("zzw_tr_full", theirs);
        Assert.Equal(Them, creator);
        Assert.Equal(Me, editor);
    }

    // ------------------------------------------------------------------ MySQL's own

    /// <summary>A table whose key the database does not make cannot take an add, and says why; it still updates.</summary>
    [Fact]
    public async Task A_table_whose_key_is_not_made_by_the_database_refuses_adds_and_takes_updates()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_plain_key", keyMade: false);
        await ExecuteAsync("insert into zzw_plain_key (objectid, label, shape) values (7, 'seven', st_geomfromtext('POINT(40 30)', 4326))");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("label", "new"))], [Change(7, ("label", "changed"))], [], RollbackOnFailure: false), None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("does not make it", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);
        Assert.Equal("changed", await LabelAsync("zzw_plain_key", 7));
    }

    /// <summary>
    /// A key a before-insert trigger fills is refused like a plain one: MySQL reports the id only of an AUTO_INCREMENT
    /// column, so the add's object id could not be answered — Oracle's trigger case, which returns it, does not port.
    /// </summary>
    [Fact]
    public async Task A_table_whose_key_a_trigger_fills_still_refuses_adds()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_trigger_key", keyMade: false);
        await ExecuteAsync(
            "create trigger zzw_trigger_key_bi before insert on zzw_trigger_key for each row "
            + "set new.objectid = coalesce(new.objectid, 500)");

        EditResult added = Assert.Single((await writer.ApplyAsync(new EditBatch([Add(("label", "new"))], [], []), None)).Adds);

        Assert.False(added.Succeeded);
        Assert.Contains("does not make it", added.Error!, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("zzw_trigger_key"));
    }

    /// <summary>A geometry sent in another reference is moved into the layer's before it is stored.</summary>
    [Fact]
    public async Task A_geometry_sent_in_another_reference_is_moved_into_the_layer_s()
    {
        MySqlFeatureWriter writer = await WriterAsync("zzw_projected");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [new FeatureAdd(new Dictionary<string, object?> { ["label"] = "moved" }, new Point(3_657_290.6, 4_854_439.8), 3857)],
                [], []),
            None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);

        (double x, double y) = await PointAsync("zzw_projected", added.Identity);
        Assert.Equal(32.854, x, 2);
        Assert.Equal(39.920, y, 2);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An add; with no shape given, one is, since a spatially indexed MySQL column cannot be null.</summary>
    private static FeatureAdd Add(params (string Column, object? Value)[] values) => Add(new Point(30, 40), values);

    private static FeatureAdd Add(Geometry? shape, params (string Column, object? Value)[] values) =>
        new(values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), shape);

    private static FeatureUpdate Change(long id, params (string Column, object? Value)[] values) =>
        new(id, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), null);

    private static async Task<MySqlFeatureWriter> WriterAsync(
        string table,
        string? extra = null,
        bool domains = false,
        bool subtypes = false,
        bool tracked = false,
        bool keyMade = true)
    {
        await ExecuteAsync($"drop table if exists {table}");

        string key = keyMade ? "objectid int auto_increment primary key" : "objectid int primary key";

        await ExecuteAsync(
            $"create table {table} ({key}, kind smallint, material varchar(20), diameter int, "
            + "label varchar(100), created_user varchar(100), created_date datetime, last_edited_user varchar(100), "
            + "last_edited_date datetime, shape point not null srid 4326, spatial index (shape)"
            + (extra is null ? string.Empty : ", " + extra) + ")");

        LayerDefinition layer = new(table, Owner, table, "shape", 4326, "objectid", "objectid", isHosted: false);

        LayerDescription described = await new MySqlFeatureSource(MySqlFixture.Locator, layer, new MercatorProjector())
            .DescribeAsync(None);

        List<FieldOverride> overrides = [];

        if (domains)
        {
            overrides.Add(new FieldOverride("material", null, false, Domain: Materials));
            overrides.Add(new FieldOverride("diameter", null, false, Domain: Diameters));
        }

        if (subtypes)
        {
            overrides.Add(new FieldOverride("kind", null, false, Subtypes: new LayerSubtypes(
                "kind",
                1,
                [
                    new Subtype(
                        1,
                        "Main",
                        new Dictionary<string, DomainValue>(),
                        new Dictionary<string, FieldDomain>
                        {
                            ["material"] = FieldDomain.Coded(
                                "MainMaterial",
                                [new CodedValue(DomainValue.Of("CU"), "Copper"), new CodedValue(DomainValue.Of("DI"), "Ductile iron")]),
                        }),
                    new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
                ])));
        }

        if (tracked)
        {
            overrides.Add(new FieldOverride("created_user", null, false, EditRole.Creator));
            overrides.Add(new FieldOverride("created_date", null, false, EditRole.Created));
            overrides.Add(new FieldOverride("last_edited_user", null, false, EditRole.Editor));
            overrides.Add(new FieldOverride("last_edited_date", null, false, EditRole.Edited));
        }

        LayerDescription applied = FieldOverrides.Apply(described, overrides);

        return new MySqlFeatureWriter(
            MySqlFixture.Locator, layer, applied.Fields, new MercatorProjector(), applied.Tracking, applied.Subtypes);
    }

    private static Task ExecuteAsync(string sql) => MySqlFixture.ExecuteAsync(sql);

    private static async Task<object?> ScalarAsync(string sql, long id)
    {
        await using MySqlConnection connection = await MySqlConnections.OpenAsync(
            MySqlConnections.For(MySqlFixture.Locator, pooling: true), None);
        await using MySqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        object? value = await command.ExecuteScalarAsync(None);
        return value is DBNull ? null : value;
    }

    private static async Task<string?> LabelAsync(string table, long id) =>
        (string?)await ScalarAsync($"select label from {table} where objectid = @id", id);

    private static async Task<long?> NumberAsync(string table, string column, long id) =>
        await ScalarAsync($"select {column} from {table} where objectid = @id", id) is { } value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private static async Task<long> CountAsync(string table) =>
        Convert.ToInt64(await ScalarAsync($"select count(*) from {table} where @id = @id", 0), CultureInfo.InvariantCulture);

    private static async Task<(double X, double Y)> PointAsync(string table, long id)
    {
        double x = Convert.ToDouble(await ScalarAsync($"select st_longitude(shape) from {table} where objectid = @id", id), CultureInfo.InvariantCulture);
        double y = Convert.ToDouble(await ScalarAsync($"select st_latitude(shape) from {table} where objectid = @id", id), CultureInfo.InvariantCulture);
        return (x, y);
    }

    private static async Task<long> RowAsync(string table, short? kind, string material, int diameter)
    {
        await ExecuteAsync(
            $"insert into {table} (kind, material, diameter, shape) values ("
            + (kind is null ? "null" : kind.Value.ToString(CultureInfo.InvariantCulture))
            + $", '{material}', {diameter.ToString(CultureInfo.InvariantCulture)}, st_geomfromtext('POINT(40 30)', 4326))");
        return Convert.ToInt64(await ScalarAsync($"select max(objectid) from {table} where @id = @id", 0), CultureInfo.InvariantCulture);
    }

    private static async Task<long> RowWrittenByAsync(string table, string? creator)
    {
        string label = (creator == Them ? "not mine" : creator ?? "none") + "-original";
        await ExecuteAsync(
            $"insert into {table} (label, created_user, shape) values ('{label}', "
            + (creator is null ? "null" : $"'{creator}'") + ", st_geomfromtext('POINT(40 30)', 4326))");
        return Convert.ToInt64(await ScalarAsync($"select max(objectid) from {table} where @id = @id", 0), CultureInfo.InvariantCulture);
    }

    private static async Task<(string? Creator, string? Editor, bool Created, bool Edited)> TrackedRowAsync(string table, long id)
    {
        await using MySqlConnection connection = await MySqlConnections.OpenAsync(
            MySqlConnections.For(MySqlFixture.Locator, pooling: true), None);
        await using MySqlCommand command = new(
            "select created_user, last_edited_user, case when created_date is not null then 1 else 0 end, "
            + "case when last_edited_date > utc_timestamp() - interval 1 hour then 1 else 0 end "
            + $"from {table} where objectid = @id", connection);
        command.Parameters.AddWithValue("@id", id);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(None);
        Assert.True(await reader.ReadAsync(None), $"No row {id} in {table}.");

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1,
            Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1);
    }

    /// <summary>Spherical Web Mercator to degrees, the only move these tests ask for.</summary>
    private sealed class MercatorProjector : IProjector
    {
        private const double R = 6_378_137;

        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken)
        {
            Assert.Equal((3857, 4326), (fromSrid, toSrid));
            List<Geometry> moved = [.. geometries.Cast<Point>().Select(p => (Geometry)new Point(
                p.X / R * 180 / Math.PI,
                ((2 * Math.Atan(Math.Exp(p.Y / R))) - (Math.PI / 2)) * 180 / Math.PI))];
            return Task.FromResult<(IReadOnlyList<Geometry>, ProjectionProvenance)>((moved, new ProjectionProvenance("test", null)));
        }

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
