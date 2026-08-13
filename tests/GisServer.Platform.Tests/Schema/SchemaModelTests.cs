using System;
using GisServer.Platform.Schema;
using Xunit;

namespace GisServer.Platform.Tests.Schema;

public sealed class SchemaVersionTests
{
    [Fact]
    public void None_is_distinct_from_a_real_level()
    {
        Assert.True(SchemaVersion.None.IsNone);
        Assert.False(SchemaVersion.First.IsNone);
        Assert.True(SchemaVersion.None < SchemaVersion.First);
        Assert.Equal("none", SchemaVersion.None.ToString());
    }

    [Fact]
    public void A_negative_level_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaVersion(-1));
    }

    [Fact]
    public void Levels_order_and_compare()
    {
        SchemaVersion two = new(2);

        Assert.True(two > SchemaVersion.First);
        Assert.True(two >= new SchemaVersion(2));
        Assert.Equal(new SchemaVersion(3), two.Next());
        Assert.Equal(new SchemaVersion(2), two);
        Assert.Equal(new SchemaVersion(2).GetHashCode(), two.GetHashCode());
    }
}

public sealed class MigrationTests
{
    [Fact]
    public void An_expand_does_not_move_the_minimum_reader()
    {
        Migration expand = Migration.Expand(SchemaVersion.First, "Create.", "create table t ()");

        Assert.Equal(MigrationPhase.Expand, expand.Phase);
        Assert.True(expand.RaisesMinimumReaderTo.IsNone);
    }

    [Fact]
    public void A_contract_must_state_the_reader_it_requires()
    {
        // Leaving it unstated would close the rollback window silently, which is
        // the failure mode this whole model exists to prevent.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Migration.Contract(new SchemaVersion(2), SchemaVersion.None, "Drop.", "drop"));

        Assert.Contains("closes the rollback window", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_contract_cannot_require_a_reader_from_the_future()
    {
        Assert.Throws<ArgumentException>(
            () => Migration.Contract(new SchemaVersion(2), new SchemaVersion(3), "Drop.", "drop"));
    }

    [Fact]
    public void A_migration_needs_a_description_for_the_operator_report()
    {
        Assert.Throws<ArgumentException>(
            () => Migration.Expand(SchemaVersion.First, "  ", "create table t ()"));
    }

    [Fact]
    public void A_migration_must_do_something()
    {
        Assert.Throws<ArgumentException>(
            () => Migration.Expand(SchemaVersion.First, "Nothing."));
        Assert.Throws<ArgumentException>(
            () => Migration.Expand(SchemaVersion.First, "Blank.", "  "));
    }

    [Fact]
    public void Statements_are_copied_so_a_caller_cannot_mutate_them_later()
    {
        string[] statements = ["create table t ()"];
        Migration migration = Migration.Expand(SchemaVersion.First, "Create.", statements);

        statements[0] = "drop database production";

        Assert.Equal("create table t ()", migration.Statements[0]);
    }
}

public sealed class SchemaStampTests
{
    [Fact]
    public void A_stamp_cannot_require_a_reader_newer_than_itself()
    {
        Assert.Throws<ArgumentException>(
            () => new SchemaStamp(new SchemaVersion(2), new SchemaVersion(3)));
    }

    [Fact]
    public void A_stamp_needs_a_real_applied_level()
    {
        // Absence of a stamp is modelled as null, not as a stamp at level none.
        Assert.Throws<ArgumentException>(
            () => new SchemaStamp(SchemaVersion.None, SchemaVersion.None));
    }

    [Fact]
    public void A_migration_must_be_ahead_of_the_applied_level()
    {
        SchemaStamp atTwo = new(new SchemaVersion(2), SchemaVersion.First);

        Assert.Throws<ArgumentException>(
            () => atTwo.AfterApplying(Migration.Expand(new SchemaVersion(2), "Again.", "x")));
    }

    [Fact]
    public void The_first_migration_cannot_be_a_contract()
    {
        Assert.Throws<ArgumentException>(() => SchemaStamp.Initial(
            Migration.Contract(SchemaVersion.First, SchemaVersion.First, "Drop.", "drop")));
    }

    [Fact]
    public void A_contract_never_lowers_the_minimum_reader()
    {
        // Applying an older contract's requirement must not reopen a window a
        // later one closed.
        SchemaStamp stamp = new(new SchemaVersion(5), new SchemaVersion(4));

        SchemaStamp after = stamp.AfterApplying(
            Migration.Contract(new SchemaVersion(6), new SchemaVersion(2), "Late drop.", "drop"));

        Assert.Equal(new SchemaVersion(4), after.MinimumReader);
    }
}

public sealed class MigrationSetTests
{
    [Fact]
    public void Migrations_must_ascend()
    {
        Assert.Throws<ArgumentException>(() => new MigrationSet(
        [
            Migration.Expand(new SchemaVersion(2), "Second.", "x"),
            Migration.Expand(SchemaVersion.First, "First.", "x"),
        ]));
    }

    [Fact]
    public void Duplicate_levels_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new MigrationSet(
        [
            Migration.Expand(SchemaVersion.First, "One.", "x"),
            Migration.Expand(SchemaVersion.First, "One again.", "x"),
        ]));
    }

    [Fact]
    public void An_empty_set_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new MigrationSet([]));
    }

    [Fact]
    public void A_current_store_has_nothing_pending()
    {
        MigrationSet set = new([Migration.Expand(SchemaVersion.First, "One.", "x")]);
        SchemaStamp current = SchemaStamp.Initial(set.All[0]);

        Assert.Empty(set.Pending(current));
        Assert.Equal(current.Applied, set.Project(current).Applied);
        Assert.False(set.ClosesRollbackWindow(current));
    }
}
