using System;
using System.Collections.Generic;
using System.Globalization;
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
/// ADR-205 condition 6: the Oracle writer against the PostGIS writer's own rule tests — <c>PostGisFeatureWriterTests</c>,
/// <c>DomainWriterTests</c> and <c>EditorTrackingWriterTests</c> — each case ported to an Oracle table made for it in
/// the test schema, and asserting what the PostGIS one asserts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Oracle names its columns in capitals</b>, so the attributes here do too: the layer's fields are what the
/// database describes, and the whitelist compares them exactly.
/// </para>
/// <para>
/// <b>Not ported, and why.</b> <c>Two_layers_in_one_transaction_are_rolled_back_together</c>: the Oracle writer has no
/// shared-transaction entry, so a service-level edit across Oracle layers is applied layer by layer
/// (<c>OneTransactionHolds</c> asks for PostgreSQL). Its own all-or-nothing is tested below.
/// </para>
/// </remarks>
[Trait("Needs", "Oracle")]
public sealed class OracleFeatureWriterTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private const string Me = "me_publisher";
    private const string Them = "somebody_else";

    private static string Owner => OracleFixture.Source.User.ToUpperInvariant();

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
        OracleFeatureWriter writer = await WriterAsync("ZZW_REQUIRED", extra: "req varchar2(20) not null");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([Add(("LABEL", "x"))], [], []), None);

        string error = Assert.Single(outcome.Adds).Error!;
        Assert.Contains("'REQ' is required", error, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZW_REQUIRED", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_add_inserts_the_row_and_returns_its_new_object_id()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_ADD");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(new Point(32.85, 39.93), ("LABEL", "Ankara"))], [], []), None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);
        Assert.True(added.Identity > 0);
        Assert.Equal("Ankara", await LabelAsync("ZZW_ADD", added.Identity));
        Assert.Equal((32.85, 39.93), await PointAsync("ZZW_ADD", added.Identity));
    }

    [Fact]
    public async Task An_attribute_only_update_leaves_the_geometry_alone()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_UPDATE");
        long id = Assert.Single((await writer.ApplyAsync(
            new EditBatch([Add(new Point(29, 41), ("LABEL", "before"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [Change(id, ("LABEL", "after"))], []), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded);
        Assert.Equal("after", await LabelAsync("ZZW_UPDATE", id));
        Assert.Equal((29.0, 41.0), await PointAsync("ZZW_UPDATE", id));
    }

    [Fact]
    public async Task A_delete_removes_the_row()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DELETE");
        long id = Assert.Single((await writer.ApplyAsync(
            new EditBatch([Add(("LABEL", "gone"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [], [id]), None);

        Assert.True(Assert.Single(outcome.Deletes).Succeeded);
        Assert.Equal(0L, await CountAsync("ZZW_DELETE"));
    }

    [Fact]
    public async Task Writing_two_dimensions_over_a_three_dimensional_feature_is_refused()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_THREE_D", dimensions: 3);
        long id = await ThreeDimensionalRowAsync("ZZW_THREE_D");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [new FeatureUpdate(id, new Dictionary<string, object?>(), new Point(30, 40))], []), None);

        EditResult refused = Assert.Single(outcome.Updates);
        Assert.False(refused.Succeeded);
        Assert.Contains("Z ordinate", refused.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attribute_only_edit_to_a_three_dimensional_feature_is_still_allowed()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_THREE_D_ATTR", dimensions: 3);
        long id = await ThreeDimensionalRowAsync("ZZW_THREE_D_ATTR");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [Change(id, ("LABEL", "kept"))], []), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded, outcome.Updates[0].Error);
    }

    [Fact]
    public async Task A_column_that_does_not_exist_is_refused_rather_than_reaching_SQL()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_NO_COLUMN");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("LABEL\"; drop table x; --", "x"))], [], []), None);

        Assert.Contains("is not a column of this layer", Assert.Single(outcome.Adds).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_geometry_column_cannot_be_set_as_an_attribute()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_SHAPE_ATTR");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([Add(("SHAPE", "POINT(1 1)"))], [], []), None);

        Assert.Contains("is the geometry column", Assert.Single(outcome.Adds).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_object_id_in_an_update_is_ignored_rather_than_refused()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_OID");
        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add(("LABEL", "a"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("OBJECTID", id + 1000), ("LABEL", "b"))], []), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded, outcome.Updates[0].Error);
        Assert.Equal("b", await LabelAsync("ZZW_OID", id));
    }

    [Fact]
    public async Task Updating_or_deleting_a_feature_that_does_not_exist_fails_that_feature_only()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_MISSING");
        long id = Assert.Single((await writer.ApplyAsync(new EditBatch([Add(("LABEL", "a"))], [], []), None)).Adds).Identity;

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("LABEL", "b")), Change(999_999, ("LABEL", "c"))], [999_998],
                RollbackOnFailure: false),
            None);

        Assert.True(outcome.Updates[0].Succeeded);
        Assert.True(outcome.Updates[1].NoSuchFeature);
        Assert.True(outcome.Deletes[0].NoSuchFeature);
        Assert.Equal("b", await LabelAsync("ZZW_MISSING", id));
    }

    [Fact]
    public async Task A_constraint_violation_becomes_a_result_not_an_exception()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_UNIQUE", extra: "code varchar2(10) unique");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("CODE", "A")), Add(("CODE", "A"))], [], [], RollbackOnFailure: false), None);

        Assert.True(outcome.Adds[0].Succeeded);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("must be unique", outcome.Adds[1].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackOnFailure_abandons_the_whole_batch_and_still_says_which_one_failed()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_ROLLBACK", extra: "code varchar2(10) unique");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("CODE", "A")), Add(("CODE", "A")), Add(("CODE", "B"))], [], []), None);

        Assert.True(outcome.RolledBack);
        Assert.True(outcome.Adds[0].Succeeded);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Equal(0L, await CountAsync("ZZW_ROLLBACK"));
    }

    [Fact]
    public async Task Without_rollback_the_good_edits_are_kept()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_PARTIAL", extra: "code varchar2(10) unique");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("CODE", "A")), Add(("CODE", "A")), Add(("CODE", "B"))], [], [], RollbackOnFailure: false),
            None);

        Assert.False(outcome.RolledBack);
        Assert.Equal(2L, await CountAsync("ZZW_PARTIAL"));
    }

    // ------------------------------------------------------------------ DomainWriterTests

    [Fact]
    public async Task An_add_outside_a_column_s_domain_is_refused_by_name_and_writes_nothing()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_ADDS", domains: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add(("MATERIAL", "CU"), ("DIAMETER", 300)), Add(("MATERIAL", "LEAD"), ("DIAMETER", 300))],
                [], [], RollbackOnFailure: false),
            None);

        Assert.True(outcome.Adds[0].Succeeded, outcome.Adds[0].Error);
        Assert.False(outcome.Adds[1].Succeeded);
        Assert.Contains("'Material'", outcome.Adds[1].Error!, StringComparison.Ordinal);
        Assert.Contains("'CU' (Copper)", outcome.Adds[1].Error!, StringComparison.Ordinal);

        EditOutcome range = await writer.ApplyAsync(
            new EditBatch([Add(("MATERIAL", "PVC"), ("DIAMETER", 5000))], [], []), None);

        Assert.False(range.Adds[0].Succeeded);
        Assert.Contains("outside the domain 'Diameter'", range.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync("ZZW_DM_ADDS"));
    }

    [Fact]
    public async Task An_update_outside_a_domain_is_refused_and_the_row_is_unchanged()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_UPDATES", domains: true);
        long id = await RowAsync("ZZW_DM_UPDATES", kind: null, material: "CU", diameter: 100);

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([], [Change(id, ("DIAMETER", 40))], []), None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Equal(100L, await NumberAsync("ZZW_DM_UPDATES", "diameter", id));

        EditOutcome cleared = await writer.ApplyAsync(new EditBatch([], [Change(id, ("DIAMETER", null))], []), None);
        Assert.True(cleared.Updates[0].Succeeded, cleared.Updates[0].Error);
    }

    [Fact]
    public async Task The_subtype_column_takes_only_subtype_codes()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_KINDS", domains: true, subtypes: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("KIND", (short)9), ("MATERIAL", "CU"))], [], []), None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("not one of this layer's subtypes", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync("ZZW_DM_KINDS"));
    }

    [Fact]
    public async Task A_subtype_s_domain_governs_its_features_and_only_its_features()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_SUB_ADDS", domains: true, subtypes: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [
                    Add(("KIND", (short)1), ("MATERIAL", "DI")),
                    Add(("KIND", (short)1), ("MATERIAL", "PVC")),
                    Add(("KIND", (short)2), ("MATERIAL", "PVC")),
                    Add(("MATERIAL", "PVC")),
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
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_SUB_UPD", domains: true, subtypes: true);
        long main = await RowAsync("ZZW_DM_SUB_UPD", kind: 1, material: "CU", diameter: 300);
        long lateral = await RowAsync("ZZW_DM_SUB_UPD", kind: 2, material: "CU", diameter: 300);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(main, ("MATERIAL", "PVC")), Change(lateral, ("MATERIAL", "PVC"))], [],
                RollbackOnFailure: false),
            None);

        Assert.False(outcome.Updates[0].Succeeded);
        Assert.Contains("for subtype 1 (Main)", outcome.Updates[0].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Updates[1].Succeeded, outcome.Updates[1].Error);

        EditOutcome moved = await writer.ApplyAsync(
            new EditBatch([], [Change(main, ("KIND", (short)2), ("MATERIAL", "PVC"))], []), None);

        Assert.True(moved.Updates[0].Succeeded, moved.Updates[0].Error);
    }

    [Fact]
    public async Task A_move_to_a_subtype_that_disallows_a_kept_value_is_refused_by_name()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_SUB_MOVE", domains: true, subtypes: true);
        long lateral = await RowAsync("ZZW_DM_SUB_MOVE", kind: 2, material: "PVC", diameter: 300);

        EditOutcome refused = await writer.ApplyAsync(new EditBatch([], [Change(lateral, ("KIND", (short)1))], []), None);

        Assert.False(refused.Updates[0].Succeeded);
        Assert.Contains("'MATERIAL'", refused.Updates[0].Error!, StringComparison.Ordinal);
        Assert.Contains("Send 'MATERIAL'", refused.Updates[0].Error!, StringComparison.Ordinal);

        EditOutcome moved = await writer.ApplyAsync(
            new EditBatch([], [Change(lateral, ("KIND", (short)1), ("MATERIAL", "CU"))], []), None);
        Assert.True(moved.Updates[0].Succeeded, moved.Updates[0].Error);

        long legacy = await RowAsync("ZZW_DM_SUB_MOVE", kind: 1, material: "PVC", diameter: 300);
        EditOutcome same = await writer.ApplyAsync(
            new EditBatch([], [Change(legacy, ("KIND", (short)1), ("DIAMETER", 400))], []), None);
        Assert.True(same.Updates[0].Succeeded, same.Updates[0].Error);
    }

    [Fact]
    public async Task A_refused_value_in_an_all_or_nothing_batch_takes_the_batch_with_it()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_DM_BATCH", domains: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("MATERIAL", "CU")), Add(("MATERIAL", "LEAD"))], [], []), None);

        Assert.True(outcome.RolledBack);
        Assert.Equal(0L, await CountAsync("ZZW_DM_BATCH"));
    }

    // ------------------------------------------------------------------ EditorTrackingWriterTests

    [Fact]
    public async Task An_add_is_signed_by_the_account_and_dated_by_the_database()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_TR_ADDS", tracked: true);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [Add(("LABEL", "first"), ("CREATED_USER", "an impostor"), ("LAST_EDITED_DATE", "1999-01-01"))],
                [], [], Editor: Me),
            None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);

        (string? creator, string? editor, bool created, bool edited) = await TrackedRowAsync("ZZW_TR_ADDS", added.Identity);
        Assert.Equal(Me, creator);
        Assert.Equal(Me, editor);
        Assert.True(created, "The created column was not written.");
        Assert.True(edited, "The edited column was not written, or kept the client's 1999.");
    }

    [Fact]
    public async Task An_update_signs_the_editor_and_leaves_the_creator()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_TR_UPDATES", tracked: true);
        long id = await RowWrittenByAsync("ZZW_TR_UPDATES", Them);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(id, ("LABEL", "changed"), ("CREATED_USER", Me))], [], Editor: Me), None);

        Assert.True(Assert.Single(outcome.Updates).Succeeded, outcome.Updates[0].Error);

        (string? creator, string? editor, _, bool edited) = await TrackedRowAsync("ZZW_TR_UPDATES", id);
        Assert.Equal(Them, creator);
        Assert.Equal(Me, editor);
        Assert.True(edited, "The edited column was not written.");
    }

    [Fact]
    public async Task Own_only_changes_and_deletes_your_own_and_refuses_the_rest()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_TR_OWN", tracked: true);
        long mine = await RowWrittenByAsync("ZZW_TR_OWN", Me);
        long theirs = await RowWrittenByAsync("ZZW_TR_OWN", Them);
        long nobodys = await RowWrittenByAsync("ZZW_TR_OWN", null);

        EditOutcome updated = await writer.ApplyAsync(
            new EditBatch(
                [],
                [
                    Change(mine, ("LABEL", "mine")),
                    Change(theirs, ("LABEL", "not mine")),
                    Change(nobodys, ("LABEL", "nobody's")),
                    Change(999_999, ("LABEL", "missing")),
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
        Assert.Equal("not mine-original", await LabelAsync("ZZW_TR_OWN", theirs));

        EditOutcome deleted = await writer.ApplyAsync(
            new EditBatch([], [], [mine, theirs], RollbackOnFailure: false, Editor: Me, OwnOnly: true), None);

        Assert.True(deleted.Deletes[0].Succeeded, deleted.Deletes[0].Error);
        Assert.False(deleted.Deletes[1].Succeeded);
        Assert.Contains("somebody else", deleted.Deletes[1].Error!, StringComparison.Ordinal);
        Assert.NotNull(await LabelAsync("ZZW_TR_OWN", theirs));
    }

    [Fact]
    public async Task Without_own_only_anybody_s_feature_can_be_changed()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_TR_FULL", tracked: true);
        long theirs = await RowWrittenByAsync("ZZW_TR_FULL", Them);

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([], [Change(theirs, ("LABEL", "full edit"))], [], Editor: Me), None);

        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);

        (string? creator, string? editor, _, _) = await TrackedRowAsync("ZZW_TR_FULL", theirs);
        Assert.Equal(Them, creator);
        Assert.Equal(Me, editor);
    }

    // ------------------------------------------------------------------ Oracle's own

    /// <summary>A table whose key the database does not make cannot take an add, and says why; it still updates.</summary>
    [Fact]
    public async Task A_table_whose_key_is_not_made_by_the_database_refuses_adds_and_takes_updates()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_PLAIN_KEY", keyMade: false);
        await ExecuteAsync("insert into ZZW_PLAIN_KEY (objectid, label) values (7, 'seven')");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch([Add(("LABEL", "new"))], [Change(7, ("LABEL", "changed"))], [], RollbackOnFailure: false), None);

        Assert.False(outcome.Adds[0].Succeeded);
        Assert.Contains("does not make it", outcome.Adds[0].Error!, StringComparison.Ordinal);
        Assert.True(outcome.Updates[0].Succeeded, outcome.Updates[0].Error);
        Assert.Equal("changed", await LabelAsync("ZZW_PLAIN_KEY", 7));
    }

    /// <summary>A key filled by a sequence in a before-insert trigger — how older Oracle tables number rows — takes adds.</summary>
    [Fact]
    public async Task A_table_whose_key_a_trigger_fills_takes_adds()
    {
        await ExecuteAsync("drop sequence if exists zzw_trigger_key_seq");
        OracleFeatureWriter writer = await WriterAsync("ZZW_TRIGGER_KEY", keyMade: false);
        await ExecuteAsync("create sequence zzw_trigger_key_seq start with 500");
        await ExecuteAsync(
            "create or replace trigger zzw_trigger_key_bi before insert on zzw_trigger_key for each row "
            + "begin if :new.objectid is null then :new.objectid := zzw_trigger_key_seq.nextval; end if; end;");

        EditOutcome outcome = await writer.ApplyAsync(new EditBatch([Add(("LABEL", "new"))], [], []), None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);
        Assert.Equal(500, added.Identity);
        Assert.Equal("new", await LabelAsync("ZZW_TRIGGER_KEY", 500));
    }

    /// <summary>A geometry sent in another reference is moved into the layer's before it is stored.</summary>
    [Fact]
    public async Task A_geometry_sent_in_another_reference_is_moved_into_the_layer_s()
    {
        OracleFeatureWriter writer = await WriterAsync("ZZW_PROJECTED");

        EditOutcome outcome = await writer.ApplyAsync(
            new EditBatch(
                [new FeatureAdd(new Dictionary<string, object?> { ["LABEL"] = "moved" }, new Point(3_657_290.6, 4_854_439.8), 3857)],
                [], []),
            None);

        EditResult added = Assert.Single(outcome.Adds);
        Assert.True(added.Succeeded, added.Error);

        (double x, double y) = await PointAsync("ZZW_PROJECTED", added.Identity);
        Assert.Equal(32.854, x, 2);
        Assert.Equal(39.920, y, 2);
    }

    // ------------------------------------------------------------------ helpers

    private static FeatureAdd Add(params (string Column, object? Value)[] values) => Add(null, values);

    private static FeatureAdd Add(Geometry? shape, params (string Column, object? Value)[] values) =>
        new(values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), shape);

    private static FeatureUpdate Change(long id, params (string Column, object? Value)[] values) =>
        new(id, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), null);

    private static async Task<OracleFeatureWriter> WriterAsync(
        string table,
        string? extra = null,
        int dimensions = 2,
        bool domains = false,
        bool subtypes = false,
        bool tracked = false,
        bool keyMade = true)
    {
        await ExecuteAsync($"drop table if exists {table} purge");
        await ExecuteAsync($"delete from user_sdo_geom_metadata where table_name = '{table}'");

        string key = keyMade ? "objectid number(10) generated by default as identity primary key" : "objectid number(10) primary key";

        await ExecuteAsync(
            $"create table {table} ({key}, kind number(5), material varchar2(20), diameter number(10), "
            + "label varchar2(100), created_user varchar2(100), created_date date, last_edited_user varchar2(100), "
            + "last_edited_date date, shape sdo_geometry" + (extra is null ? string.Empty : ", " + extra) + ")");

        string dims = dimensions == 3
            ? "sdo_dim_element('X', -180, 180, 0.05), sdo_dim_element('Y', -90, 90, 0.05), sdo_dim_element('Z', -10000, 10000, 0.05)"
            : "sdo_dim_element('X', -180, 180, 0.05), sdo_dim_element('Y', -90, 90, 0.05)";

        await ExecuteAsync(
            $"insert into user_sdo_geom_metadata values ('{table}', 'SHAPE', sdo_dim_array({dims}), 4326)");

        LayerDefinition layer = new(table, Owner, table, "SHAPE", 4326, "OBJECTID", "OBJECTID", isHosted: false);

        LayerDescription described = await new OracleFeatureSource(OracleFixture.Locator, layer, new MercatorProjector())
            .DescribeAsync(None);

        List<FieldOverride> overrides = [];

        if (domains)
        {
            overrides.Add(new FieldOverride("MATERIAL", null, false, Domain: Materials));
            overrides.Add(new FieldOverride("DIAMETER", null, false, Domain: Diameters));
        }

        if (subtypes)
        {
            overrides.Add(new FieldOverride("KIND", null, false, Subtypes: new LayerSubtypes(
                "KIND",
                1,
                [
                    new Subtype(
                        1,
                        "Main",
                        new Dictionary<string, DomainValue>(),
                        new Dictionary<string, FieldDomain>
                        {
                            ["MATERIAL"] = FieldDomain.Coded(
                                "MainMaterial",
                                [new CodedValue(DomainValue.Of("CU"), "Copper"), new CodedValue(DomainValue.Of("DI"), "Ductile iron")]),
                        }),
                    new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
                ])));
        }

        if (tracked)
        {
            overrides.Add(new FieldOverride("CREATED_USER", null, false, EditRole.Creator));
            overrides.Add(new FieldOverride("CREATED_DATE", null, false, EditRole.Created));
            overrides.Add(new FieldOverride("LAST_EDITED_USER", null, false, EditRole.Editor));
            overrides.Add(new FieldOverride("LAST_EDITED_DATE", null, false, EditRole.Edited));
        }

        LayerDescription applied = FieldOverrides.Apply(described, overrides);

        return new OracleFeatureWriter(
            OracleFixture.Locator, layer, applied.Fields, new MercatorProjector(), applied.Tracking, applied.Subtypes);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(None);
    }

    private static async Task<object?> ScalarAsync(string sql, long id)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(sql, connection) { BindByName = true };
        command.Parameters.Add("id", id);
        object? value = await command.ExecuteScalarAsync(None);
        return value is DBNull ? null : value;
    }

    private static async Task<string?> LabelAsync(string table, long id) =>
        (string?)await ScalarAsync($"select label from {table} where objectid = :id", id);

    private static async Task<long?> NumberAsync(string table, string column, long id) =>
        await ScalarAsync($"select {column} from {table} where objectid = :id", id) is { } value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private static async Task<long> CountAsync(string table) =>
        Convert.ToInt64(await ScalarAsync($"select count(*) from {table} where :id = :id", 0), CultureInfo.InvariantCulture);

    private static async Task<(double X, double Y)> PointAsync(string table, long id)
    {
        double x = Convert.ToDouble(await ScalarAsync($"select t.shape.sdo_point.x from {table} t where t.objectid = :id", id), CultureInfo.InvariantCulture);
        double y = Convert.ToDouble(await ScalarAsync($"select t.shape.sdo_point.y from {table} t where t.objectid = :id", id), CultureInfo.InvariantCulture);
        return (x, y);
    }

    private static async Task<long> ThreeDimensionalRowAsync(string table)
    {
        await ExecuteAsync(
            $"insert into {table} (label, shape) values ('3d', sdo_geometry(3001, 4326, sdo_point_type(30, 40, 1000), null, null))");
        return Convert.ToInt64(await ScalarAsync($"select max(objectid) from {table} where :id = :id", 0), CultureInfo.InvariantCulture);
    }

    private static async Task<long> RowAsync(string table, short? kind, string material, int diameter)
    {
        await ExecuteAsync(
            $"insert into {table} (kind, material, diameter) values ("
            + (kind is null ? "null" : kind.Value.ToString(CultureInfo.InvariantCulture))
            + $", '{material}', {diameter.ToString(CultureInfo.InvariantCulture)})");
        return Convert.ToInt64(await ScalarAsync($"select max(objectid) from {table} where :id = :id", 0), CultureInfo.InvariantCulture);
    }

    private static async Task<long> RowWrittenByAsync(string table, string? creator)
    {
        string label = (creator == Them ? "not mine" : creator ?? "none") + "-original";
        await ExecuteAsync(
            $"insert into {table} (label, created_user) values ('{label}', "
            + (creator is null ? "null" : $"'{creator}'") + ")");
        return Convert.ToInt64(await ScalarAsync($"select max(objectid) from {table} where :id = :id", 0), CultureInfo.InvariantCulture);
    }

    private static async Task<(string? Creator, string? Editor, bool Created, bool Edited)> TrackedRowAsync(string table, long id)
    {
        await using OracleConnection connection = await OracleConnections.OpenAsync(
            OracleConnections.For(OracleFixture.Locator, pooling: true), None);
        await using OracleCommand command = new(
            "select created_user, last_edited_user, case when created_date is not null then 1 else 0 end, "
            + "case when last_edited_date > sys_extract_utc(systimestamp) - interval '1' hour then 1 else 0 end "
            + $"from {table} where objectid = :id", connection) { BindByName = true };
        command.Parameters.Add("id", id);

        await using OracleDataReader reader = await command.ExecuteReaderAsync(None);
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
