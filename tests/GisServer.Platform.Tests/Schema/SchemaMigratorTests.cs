using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Schema;
using Xunit;

namespace GisServer.Platform.Tests.Schema;

/// <summary>
/// An in-memory <see cref="IPlatformSchemaStore"/> that records what it was
/// asked to do, so the migrator's behaviour is testable without a database.
/// </summary>
internal sealed class FakeSchemaStore : IPlatformSchemaStore
{
    private SchemaStamp? _stamp;

    public FakeSchemaStore(SchemaStamp? initial = null) => _stamp = initial;

    public List<(Migration Migration, SchemaStamp Stamp)> Applied { get; } = [];

    /// <summary>When set, <see cref="ApplyAsync"/> throws at this version.</summary>
    public SchemaVersion FailAt { get; set; } = SchemaVersion.None;

    public Task<SchemaStamp?> ReadStampAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_stamp);

    public Task ApplyAsync(Migration migration, SchemaStamp resultingStamp, CancellationToken cancellationToken)
    {
        if (!FailAt.IsNone && migration.Version == FailAt)
        {
            throw new InvalidOperationException($"Deliberate failure at {migration.Version}.");
        }

        Applied.Add((migration, resultingStamp));

        // The port's contract: statements and stamp are one atomic unit, so the
        // stamp is only visible once the migration has succeeded.
        _stamp = resultingStamp;
        return Task.CompletedTask;
    }
}

public sealed class SchemaMigratorTests
{
    private static readonly SchemaVersion V1 = new(1);
    private static readonly SchemaVersion V2 = new(2);
    private static readonly SchemaVersion V3 = new(3);

    private static MigrationSet ThreeStep() => new(
    [
        Migration.Expand(V1, "Create.", "create table a ()"),
        Migration.Expand(V2, "Add b.", "alter table a add column b text"),
        Migration.Contract(V3, V2, "Drop legacy.", "alter table a drop column legacy"),
    ]);

    [Fact]
    public async Task Planning_changes_nothing()
    {
        FakeSchemaStore store = new();
        SchemaMigrator migrator = new(store, ThreeStep());

        MigrationReport plan = await migrator.PlanAsync();

        Assert.Empty(store.Applied);
        Assert.Equal(3, plan.Pending.Count);
        Assert.Null(plan.From);
        Assert.Equal(V3, plan.To.Applied);
    }

    [Fact]
    public async Task A_fresh_store_gets_every_migration_in_order()
    {
        FakeSchemaStore store = new();
        SchemaMigrator migrator = new(store, ThreeStep());

        MigrationReport result = await migrator.ApplyAsync();

        Assert.Equal([V1, V2, V3], store.Applied.Select(a => a.Migration.Version));
        Assert.Equal(V3, result.To.Applied);
        Assert.Equal(V2, result.To.MinimumReader);
    }

    [Fact]
    public async Task Each_migration_is_handed_the_stamp_it_must_end_with()
    {
        // The port applies statements and stamp atomically, so the migrator has
        // to compute the resulting stamp up front rather than after the fact.
        FakeSchemaStore store = new();
        SchemaMigrator migrator = new(store, ThreeStep());

        await migrator.ApplyAsync();

        Assert.Equal(V1, store.Applied[0].Stamp.Applied);
        Assert.Equal(V1, store.Applied[0].Stamp.MinimumReader);

        Assert.Equal(V2, store.Applied[1].Stamp.Applied);
        Assert.Equal(V1, store.Applied[1].Stamp.MinimumReader);   // expand: unchanged

        Assert.Equal(V3, store.Applied[2].Stamp.Applied);
        Assert.Equal(V2, store.Applied[2].Stamp.MinimumReader);   // contract: raised
    }

    [Fact]
    public async Task A_failure_part_way_leaves_the_stamp_where_it_actually_got_to()
    {
        // So that re-running resumes rather than repeating. This is why the port
        // requires statements and stamp to be atomic.
        FakeSchemaStore store = new() { FailAt = V3 };
        SchemaMigrator migrator = new(store, ThreeStep());

        await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.ApplyAsync());

        SchemaStamp? stamp = await store.ReadStampAsync(CancellationToken.None);
        Assert.Equal(V2, stamp!.Applied);

        // Resuming applies only what is left.
        store.FailAt = SchemaVersion.None;
        MigrationReport resumed = await new SchemaMigrator(store, ThreeStep()).ApplyAsync();

        Assert.Single(resumed.Pending);
        Assert.Equal(V3, resumed.Pending[0].Version);
    }

    [Fact]
    public async Task An_up_to_date_store_is_left_alone()
    {
        FakeSchemaStore store = new();
        MigrationSet set = ThreeStep();
        await new SchemaMigrator(store, set).ApplyAsync();
        int appliedCount = store.Applied.Count;

        MigrationReport again = await new SchemaMigrator(store, set).ApplyAsync();

        Assert.True(again.IsUpToDate);
        Assert.Equal(appliedCount, store.Applied.Count);
        Assert.Contains("up to date", again.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_warns_before_the_rollback_window_closes()
    {
        // ADR-016 §4b: report what it will do before doing it. The rollback
        // warning is the part that matters, because it cannot be undone.
        FakeSchemaStore store = new();
        MigrationSet set = ThreeStep();
        await new SchemaMigrator(store, new MigrationSet([set.All[0], set.All[1]])).ApplyAsync();

        MigrationReport plan = await new SchemaMigrator(store, set).PlanAsync();

        Assert.True(plan.ClosesRollbackWindow);

        string description = plan.Describe();
        Assert.Contains("closes the rollback window", description, StringComparison.Ordinal);
        Assert.Contains("discards every write", description, StringComparison.Ordinal);
        Assert.Contains("Drop legacy.", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expand_only_upgrade_does_not_warn()
    {
        FakeSchemaStore store = new();
        MigrationSet set = ThreeStep();
        await new SchemaMigrator(store, new MigrationSet([set.All[0]])).ApplyAsync();

        MigrationReport plan = await new SchemaMigrator(
            store, new MigrationSet([set.All[0], set.All[1]])).PlanAsync();

        Assert.False(plan.ClosesRollbackWindow);
        Assert.DoesNotContain("WARNING", plan.Describe(), StringComparison.Ordinal);
    }
}

public sealed class PlatformMigrationsTests
{
    [Fact]
    public void The_shipped_history_is_a_valid_sequence()
    {
        // MigrationSet validates on construction, so simply touching it is the
        // assertion. A malformed history then fails here rather than half way
        // through an upgrade.
        Assert.NotEmpty(PlatformMigrations.All.All);
        Assert.Equal(PlatformMigrations.All.Latest, PlatformMigrations.ComponentSchemaVersion);
    }

    [Fact]
    public void The_first_migration_creates_the_stamp_table_it_will_be_read_from()
    {
        // Bootstrap: the migrator recognises an empty store by the absence of
        // this table, so migration 1 must be what creates it.
        Migration first = PlatformMigrations.All.All[0];

        Assert.Equal(MigrationPhase.Expand, first.Phase);
        Assert.Contains(first.Statements, s => s.Contains("create table platform_schema", StringComparison.Ordinal));
    }

    [Fact]
    public void This_build_can_run_against_a_store_it_has_just_migrated()
    {
        SchemaStamp stamp = PlatformMigrations.All.Project(null);

        Assert.True(SchemaCompatibility
            .Check("server", PlatformMigrations.ComponentSchemaVersion, stamp)
            .IsCompatible);
    }
}
