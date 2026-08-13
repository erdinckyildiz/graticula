using System;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Schema;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Exercises the adapter against a real PostgreSQL, because the things it can
/// get wrong — transactional DDL, the bootstrap SQL state, the single-row upsert
/// — are precisely the things a fake cannot check.
/// </summary>
/// <remarks>See <see cref="PostgresFixture"/> for how to run these.</remarks>
[Trait("Category", "Integration")]
public sealed class PostgresPlatformSchemaStoreTests : PostgresFixture
{
    [Fact]
    public async Task An_empty_store_reports_no_stamp_rather_than_throwing()
    {
        // The bootstrap case. Detected by SQL state 42P01, not by message text,
        // because message text is localised and version-dependent.
        Assert.Null(await Store().ReadStampAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_shipped_migrations_apply_and_stamp_the_store()
    {
        MigrationReport report = await MigrateAsync();

        Assert.False(report.IsUpToDate);

        SchemaStamp? stamp = await Store().ReadStampAsync(CancellationToken.None);
        Assert.Equal(PlatformMigrations.All.Latest, stamp!.Applied);
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await MigrateAsync();

        MigrationReport again = await MigrateAsync();

        Assert.True(again.IsUpToDate);
    }

    [Fact]
    public async Task A_failing_migration_rolls_back_its_statements_and_its_stamp_together()
    {
        // The contract that matters. PostgreSQL has transactional DDL, so a
        // migration whose last statement fails must leave no trace of its first
        // — and must not stamp a level it did not reach.
        MigrationSet broken = new(
        [
            Migration.Expand(new SchemaVersion(1), "Half-valid.",
                "create table platform_schema (only_row boolean not null primary key, "
                + "applied_version integer not null, minimum_reader_version integer not null, "
                + "applied_at timestamptz not null default now())",
                "create table good ()",
                "this is not sql"),
        ]);

        await Assert.ThrowsAnyAsync<PostgresException>(
            () => new SchemaMigrator(Store(), broken).ApplyAsync(CancellationToken.None));

        // Nothing survived: no stamp table, so the store still reads as empty.
        Assert.Null(await Store().ReadStampAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_stamp_table_with_no_row_is_refused_rather_than_treated_as_empty()
    {
        // Otherwise the migrator would re-run migration 1 over populated data.
        await MigrateAsync();

        await using (NpgsqlCommand clear = DataSource.CreateCommand("delete from platform_schema"))
        {
            await clear.ExecuteNonQueryAsync();
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store().ReadStampAsync(CancellationToken.None));

        Assert.Contains("needs an operator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stamp_is_a_single_row_by_constraint()
    {
        await MigrateAsync();

        await using NpgsqlCommand insert = DataSource.CreateCommand(
            "insert into platform_schema (only_row, applied_version, minimum_reader_version) "
            + "values (true, 99, 99)");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync());

        Assert.Equal("23505", error.SqlState);   // unique_violation on the fixed key
    }

    [Fact]
    public async Task A_partly_migrated_store_resumes_rather_than_repeating()
    {
        // Applies migration 1 only, then the full set, and checks that migration
        // 1 does not run twice — which against a real database would fail with
        // "relation already exists" rather than passing quietly.
        MigrationSet firstOnly = new([PlatformMigrations.All.All[0]]);
        await new SchemaMigrator(Store(), firstOnly).ApplyAsync(CancellationToken.None);

        MigrationReport rest = await MigrateAsync();

        Assert.Single(rest.Pending);
        Assert.Equal(new SchemaVersion(2), rest.Pending[0].Version);
    }
}
