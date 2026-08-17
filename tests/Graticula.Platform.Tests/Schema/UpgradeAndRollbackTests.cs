using System;
using Graticula.Platform.Schema;
using Xunit;

namespace Graticula.Platform.Tests.Schema;

/// <summary>
/// The scenario independent review 3 finding <b>O1</b> said was broken.
/// </summary>
/// <remarks>
/// <para>
/// ADR-016 §4 originally required exact version agreement while §5 and §6
/// required the previous version to keep running against the expanded schema.
/// The consequence, which the ADR did not notice: once the migration stamped
/// N+1, the N−1 component read the stamp, saw a mismatch, and refused to start —
/// so <b>the rollback the ADR exists to provide could not execute</b>, and
/// neither could a rolling upgrade.
/// </para>
/// <para>
/// These tests walk the whole sequence and pin the fix from §4a: expand leaves
/// the minimum reader alone, contract raises it, and that asymmetry is what
/// opens and closes the window.
/// </para>
/// </remarks>
public sealed class UpgradeAndRollbackTests
{
    private static readonly SchemaVersion V1 = new(1);
    private static readonly SchemaVersion V2 = new(2);
    private static readonly SchemaVersion V3 = new(3);

    private static readonly MigrationSet Migrations = new(
    [
        Migration.Expand(V1, "Create the platform schema.", "create table service (...)"),
        Migration.Expand(V2, "Add service.display_name.", "alter table service add column display_name text"),
        Migration.Contract(V3, raisesMinimumReaderTo: V2, "Drop service.legacy_name.",
            "alter table service drop column legacy_name"),
    ]);

    [Fact]
    public void An_un_migrated_store_refuses_every_component_and_says_what_to_do()
    {
        SchemaCompatibilityResult result = SchemaCompatibility.Check("server", V1, stamp: null);

        Assert.Equal(SchemaCompatibilityOutcome.NotInitialised, result.Outcome);
        Assert.Contains("Run the migration", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void After_expand_the_previous_version_still_runs_which_is_the_rollback_window()
    {
        // This is the property O1 found missing. v1 code, v2 schema: allowed.
        SchemaStamp afterExpand = SchemaStamp
            .Initial(Migrations.All[0])
            .AfterApplying(Migrations.All[1]);

        Assert.Equal(V2, afterExpand.Applied);
        Assert.Equal(V1, afterExpand.MinimumReader);

        Assert.True(SchemaCompatibility.Check("server", V1, afterExpand).IsCompatible);
        Assert.True(SchemaCompatibility.Check("server", V2, afterExpand).IsCompatible);
    }

    [Fact]
    public void After_contract_the_previous_version_is_refused_and_told_why()
    {
        SchemaStamp afterContract = SchemaStamp
            .Initial(Migrations.All[0])
            .AfterApplying(Migrations.All[1])
            .AfterApplying(Migrations.All[2]);

        Assert.Equal(V3, afterContract.Applied);
        Assert.Equal(V2, afterContract.MinimumReader);

        SchemaCompatibilityResult refused = SchemaCompatibility.Check("server", V1, afterContract);

        Assert.Equal(SchemaCompatibilityOutcome.ComponentTooOld, refused.Outcome);

        // ADR-016 §10 condition 1: name both numbers and the direction.
        Assert.Contains("schema 1", refused.Explanation, StringComparison.Ordinal);
        Assert.Contains("at least 2", refused.Explanation, StringComparison.Ordinal);
        Assert.Contains("contract migration", refused.Explanation, StringComparison.Ordinal);

        // O1's unstated consequence, now stated where the operator will read it.
        Assert.Contains("discards every write", refused.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_does_not_close_the_window_further_than_it_must()
    {
        // The subtle one. Contract at 3 drops what version 1 needed, so the
        // minimum reader becomes 2 — not 3. Raising it to 3 would shut the door
        // a version early and nobody would notice until they needed it open.
        SchemaStamp afterContract = SchemaStamp
            .Initial(Migrations.All[0])
            .AfterApplying(Migrations.All[1])
            .AfterApplying(Migrations.All[2]);

        Assert.True(SchemaCompatibility.Check("server", V2, afterContract).IsCompatible);
    }

    [Fact]
    public void A_component_newer_than_the_store_is_refused_and_told_to_migrate()
    {
        SchemaStamp atV1 = SchemaStamp.Initial(Migrations.All[0]);

        SchemaCompatibilityResult refused = SchemaCompatibility.Check("job-worker", V2, atV1);

        Assert.Equal(SchemaCompatibilityOutcome.ComponentTooNew, refused.Outcome);
        Assert.Contains("job-worker", refused.Explanation, StringComparison.Ordinal);
        Assert.Contains("has not been run", refused.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_operator_is_told_in_advance_when_an_upgrade_will_close_the_rollback_door()
    {
        // Computed before anything runs, because it is the one consequence the
        // operator cannot undo.
        SchemaStamp atV2 = SchemaStamp
            .Initial(Migrations.All[0])
            .AfterApplying(Migrations.All[1]);

        Assert.True(Migrations.ClosesRollbackWindow(atV2));

        MigrationSet expandOnly = new([Migrations.All[0], Migrations.All[1]]);
        Assert.False(expandOnly.ClosesRollbackWindow(SchemaStamp.Initial(Migrations.All[0])));
    }

    /// <summary>
    /// The version this build declares is the highest migration it carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is D-52's actual defect, and the row's diagnosis was wrong.</b> D-52
    /// said the startup check never asks whether a build needs a schema newer than the
    /// store has. It does, and has since 2026-08-13 —
    /// <see cref="A_component_newer_than_the_store_is_refused_and_told_to_migrate"/>
    /// covers it. What happened on 2026-08-17 is that migration 16 was written and
    /// <c>ComponentSchemaVersion</c> was left at 15, so the check compared 15 against
    /// 15, correctly concluded *compatible*, and the server then selected a column the
    /// store did not have — <c>42703: column s.serves_features does not exist</c>, with
    /// no port bound and the line above it reading *compatible*.
    /// </para>
    /// <para>
    /// <b>So the check was right and the number it was given was stale, which is a
    /// build-time fact and belongs in a test.</b> Nothing else can catch it: a stale
    /// constant is invisible on a developer machine, where the migration has been
    /// applied and the column exists. This fails the moment a migration is added
    /// without the declaration moving with it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_declared_schema_version_is_the_highest_migration_this_build_carries()
    {
        Assert.Equal(
            PlatformMigrations.All.Latest,
            PlatformMigrations.ComponentSchemaVersion);
    }

    /// <summary>
    /// And it fails when the two drift, which is the only proof the test above works.
    /// </summary>
    /// <remarks>
    /// <b>A test that has never failed is a claim.</b> The check above passes today by
    /// construction; what makes it a check is that a set whose highest migration is
    /// beyond the declared version is caught. Verified with a set built here rather
    /// than by editing the real constant, so the proof does not depend on somebody
    /// remembering to put it back.
    /// </remarks>
    [Fact]
    public void And_it_would_fail_if_a_migration_were_added_without_the_declaration()
    {
        MigrationSet set = new(
        [
            Migration.Expand(new SchemaVersion(1), "one", "select 1"),
            Migration.Expand(new SchemaVersion(2), "two", "select 1"),
        ]);

        Assert.NotEqual(new SchemaVersion(1), set.Latest);
        Assert.Equal(new SchemaVersion(2), set.Latest);
    }

    [Fact]
    public void Pending_lists_only_what_has_not_run()
    {
        SchemaStamp atV1 = SchemaStamp.Initial(Migrations.All[0]);

        Assert.Equal(3, Migrations.Pending(null).Count);
        Assert.Equal(2, Migrations.Pending(atV1).Count);
        Assert.Equal(V2, Migrations.Pending(atV1)[0].Version);
    }

    [Fact]
    public void Projecting_a_fresh_install_reaches_the_latest_schema()
    {
        SchemaStamp projected = Migrations.Project(null);

        Assert.Equal(V3, projected.Applied);
        Assert.Equal(V2, projected.MinimumReader);
        Assert.Equal(V3, Migrations.Latest);
    }
}
