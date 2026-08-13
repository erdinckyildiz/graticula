using System;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Postgres;
using GisServer.Platform.Schema;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Exercises the adapter against a real PostgreSQL, because the things it can
/// get wrong — transactional DDL, the bootstrap SQL state, the single-row
/// upsert — are precisely the things a fake cannot check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not run by default.</b> These are traited <c>Integration</c> and need a
/// database:
/// </para>
/// <code>
/// setx GISSERVER_TEST_PG "Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis"
/// dotnet test --filter Category=Integration
/// </code>
/// <para>
/// The default developer run is <c>dotnet test --filter Category!=Integration</c>.
/// </para>
/// <para>
/// <b>They fail rather than skip when the variable is set and the database is
/// not reachable.</b> A test that quietly passes when its subject is absent is
/// the third instrument this project has caught lying — after a PowerShell
/// header read that returned ASCII codes and an architecture test that could not
/// fail. Absent configuration means <em>not run</em>; present configuration and
/// an absent database means <em>broken</em>.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostgresPlatformSchemaStoreTests : IAsyncLifetime
{
    private const string ConnectionVariable = "GISSERVER_TEST_PG";

    private NpgsqlDataSource? _dataSource;
    private string _schema = string.Empty;

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable);

    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        // Each run gets its own schema, so a failure cannot poison the next run
        // and two runs cannot collide.
        _schema = "gisserver_test_" + Guid.NewGuid().ToString("n")[..12];

        NpgsqlDataSourceBuilder builder = new(ConnectionString);
        builder.ConnectionStringBuilder.SearchPath = _schema;
        _dataSource = builder.Build();

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand create = new($"create schema \"{_schema}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is null)
        {
            return;
        }

        await using (NpgsqlConnection connection = await _dataSource.OpenConnectionAsync())
        {
            await using NpgsqlCommand drop = new($"drop schema if exists \"{_schema}\" cascade", connection);
            await drop.ExecuteNonQueryAsync();
        }

        await _dataSource.DisposeAsync();
    }

    private PostgresPlatformSchemaStore Store()
    {
        Assert.True(
            ConnectionString is not null,
            $"{ConnectionVariable} is not set. These tests are traited Integration and are "
            + "excluded from the default run; asking for them without configuring a database "
            + "is an error rather than a pass, because a test that goes green with its subject "
            + "absent is worse than no test.");

        return new PostgresPlatformSchemaStore(_dataSource!);
    }

    [Fact]
    public async Task An_empty_store_reports_no_stamp_rather_than_throwing()
    {
        // The bootstrap case. Detected by SQL state 42P01, not by message text,
        // because message text is localised and version-dependent.
        PostgresPlatformSchemaStore store = Store();

        Assert.Null(await store.ReadStampAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_shipped_migrations_apply_and_stamp_the_store()
    {
        PostgresPlatformSchemaStore store = Store();
        SchemaMigrator migrator = new(store, PlatformMigrations.All);

        MigrationReport report = await migrator.ApplyAsync();

        Assert.False(report.IsUpToDate);

        SchemaStamp? stamp = await store.ReadStampAsync(CancellationToken.None);
        Assert.Equal(PlatformMigrations.All.Latest, stamp!.Applied);
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        PostgresPlatformSchemaStore store = Store();
        await new SchemaMigrator(store, PlatformMigrations.All).ApplyAsync();

        MigrationReport again = await new SchemaMigrator(store, PlatformMigrations.All).ApplyAsync();

        Assert.True(again.IsUpToDate);
    }

    [Fact]
    public async Task A_failing_migration_rolls_back_its_statements_and_its_stamp_together()
    {
        // The contract that matters. PostgreSQL has transactional DDL, so a
        // migration whose second statement fails must leave no trace of its
        // first — and must not stamp a level it did not reach.
        PostgresPlatformSchemaStore store = Store();

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
            () => new SchemaMigrator(store, broken).ApplyAsync());

        // Nothing survived: no stamp table, so the store still reads as empty.
        Assert.Null(await store.ReadStampAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_stamp_table_with_no_row_is_refused_rather_than_treated_as_empty()
    {
        // Otherwise the migrator would re-run migration 1 over populated data.
        PostgresPlatformSchemaStore store = Store();
        await new SchemaMigrator(store, PlatformMigrations.All).ApplyAsync();

        await using (NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync())
        {
            await using NpgsqlCommand clear = new("delete from platform_schema", connection);
            await clear.ExecuteNonQueryAsync();
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadStampAsync(CancellationToken.None));

        Assert.Contains("needs an operator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stamp_is_a_single_row_by_constraint()
    {
        PostgresPlatformSchemaStore store = Store();
        await new SchemaMigrator(store, PlatformMigrations.All).ApplyAsync();

        await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync();
        await using NpgsqlCommand insert = new(
            "insert into platform_schema (only_row, applied_version, minimum_reader_version) "
            + "values (true, 99, 99)", connection);

        await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
    }
}
