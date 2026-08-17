using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Schema;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A throwaway PostgreSQL schema per test, for the integration suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not run by default.</b> Derived classes are traited <c>Integration</c> and
/// need a database:
/// </para>
/// <code>
/// GRATICULA_TEST_PG="Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis" \
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
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

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
                $"{ConnectionVariable} is not set, so these tests FAIL rather than skip. A test "
                + "that goes green with its subject absent is worse than no test — this project "
                + "has written that trap four times. Set it to a PostGIS connection string, e.g. "
                + "'Host=127.0.0.1;Port=5432;Database=gis;Username=gis;Password=gis'. Filter them "
                + "out deliberately with --filter Category!=Integration if you mean to.");

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

        // <b>D-60: one database-backed suite at a time.</b> This suite is the one
        // that reports the contention — its commands time out at 30 seconds while
        // the conformance and console suites drive a server against the same
        // PostgreSQL — so it is also the one that most needs to wait its turn.
        Graticula.Testing.OneSuiteAtATime.Enter();

        // A private schema per test, so a failure cannot poison the next run and
        // two runs cannot collide.
        _schema = "gisserver_test_" + Guid.NewGuid().ToString("n")[..12];

        NpgsqlDataSourceBuilder builder = new(ConnectionString);
        // Private schema FIRST so our tables land in it, but public must stay on
        // the path: PostGIS installs its functions there, and replacing the path
        // outright makes st_asbinary vanish with a confusing "function does not
        // exist" rather than an obvious configuration error.
        builder.ConnectionStringBuilder.SearchPath = $"{_schema},public";

        // <b>Two minutes, not Npgsql's thirty seconds, and the number comes from a
        // measurement rather than from caution.</b> D-43: three of these classes failed
        // on the first run after a build and passed on every rerun, always at 31–33
        // seconds — which is the default `CommandTimeout` and not an assertion
        // disagreeing.
        //
        // <b>The mechanism, measured 2026-08-18.</b> The oracle classes read
        // `public.planet_osm_polygon` — 6,499,215 rows, 2,240 MB — with
        // `order by osm_id limit 500`. There is no index on `osm_id`, only a spatial
        // one on `way`, so that is a parallel sequential scan with a top-N sort:
        // `explain (analyze, buffers)` reports **166,335 blocks read**, which is
        // 1,362 MB. The container's virtual disk reads at **40.8 MB/s** uncached
        // (`dd iflag=direct`). 1,362 ÷ 40.8 = **33.4 s** — the observed failure, to the
        // second. Warm, the same query is 1.4 s, which is why a rerun passes.
        //
        // <b>And why a build triggers it.</b> The whole 5 GB database normally sits in
        // the WSL2 virtual machine's page cache. A build's allocations put Windows
        // under memory pressure, WSL2 hands memory back and drops that cache, and the
        // next read comes off the virtual disk.
        //
        // <b>Why a bound rather than a smaller sample.</b> D-43 said raising this would
        // hide the contention — written while the cause was believed to be contention
        // between suites. It is not: it is a known, bounded, one-off physical read whose
        // size and rate are both measured above. The alternative repairs each cost
        // something real — restricting the read spatially would use the `way` index and
        // cut the I/O by 8.7×, but it narrows what an oracle test compares against and
        // makes the sample depend on which extract is loaded. So the sample stays whole
        // and the bound fits the physics. **Two minutes is 3.6× the measured cold read**,
        // which still fails fast on a query that is genuinely not returning.
        //
        // <b>The permanent fix is not this repository's to make.</b> An index on
        // `osm_id` makes the query instant — about 140 MB on a table this server does
        // not own and did not create. That is the operator's call, and D-43 records it.
        builder.ConnectionStringBuilder.CommandTimeout = 120;
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
