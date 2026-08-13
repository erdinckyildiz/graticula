using System;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Schema;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// A throwaway PostgreSQL schema per test, for the integration suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not run by default.</b> Derived classes are traited <c>Integration</c> and
/// need a database:
/// </para>
/// <code>
/// GISSERVER_TEST_PG="Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis" \
///   dotnet test --filter "Category=Integration"
/// </code>
/// <para>
/// The default developer run is <c>dotnet test --filter "Category!=Integration"</c>.
/// </para>
/// <para>
/// <b>These fail rather than skip when asked for without a database.</b> A test
/// that goes green with its subject absent is worse than no test — this project
/// has caught three instruments lying already (a PowerShell header read that
/// returned ASCII codes, a per-thread allocation counter that reported negative
/// bytes, and an architecture test that could not fail), and every one was found
/// by trying to break it rather than by reading it. Absent configuration means
/// <em>not run</em>; present configuration and an absent database means
/// <em>broken</em>.
/// </para>
/// </remarks>
public abstract class PostgresFixture : IAsyncLifetime
{
    private const string ConnectionVariable = "GISSERVER_TEST_PG";

    private NpgsqlDataSource? _dataSource;
    private string _schema = string.Empty;

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable);

    /// <summary>The data source, scoped to this test's private schema.</summary>
    protected NpgsqlDataSource DataSource
    {
        get
        {
            Assert.True(
                ConnectionString is not null,
                $"{ConnectionVariable} is not set. These tests are traited Integration and are "
                + "excluded from the default run; asking for them without configuring a database "
                + "is an error rather than a pass, because a test that goes green with its "
                + "subject absent is worse than no test.");

            return _dataSource!;
        }
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        // A private schema per test, so a failure cannot poison the next run and
        // two runs cannot collide.
        _schema = "gisserver_test_" + Guid.NewGuid().ToString("n")[..12];

        NpgsqlDataSourceBuilder builder = new(ConnectionString);
        // Private schema FIRST so our tables land in it, but public must stay on
        // the path: PostGIS installs its functions there, and replacing the path
        // outright makes st_asbinary vanish with a confusing "function does not
        // exist" rather than an obvious configuration error.
        builder.ConnectionStringBuilder.SearchPath = $"{_schema},public";
        _dataSource = builder.Build();

        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand create = new($"create schema \"{_schema}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    /// <inheritdoc/>
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

    /// <summary>The private schema this test owns.</summary>
    protected string SchemaName => _schema;

    /// <summary>The adapter under test.</summary>
    protected PostgresPlatformSchemaStore Store() => new(DataSource);

    /// <summary>Brings the private schema up to the shipped level.</summary>
    protected Task<MigrationReport> MigrateAsync() =>
        new SchemaMigrator(Store(), PlatformMigrations.All).ApplyAsync(CancellationToken.None);
}
