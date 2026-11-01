using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A PostGIS table's cache validator comes from PostgreSQL's cumulative statistics, and every kind of write moves it —
/// D-266.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is that the tag cannot stay the same across a change</b>, which is the one failure that would be a
/// stale <c>304</c> rather than a slower answer. So each kind of write is made and the version is asked for again.
/// </para>
/// <para>
/// <b>The writes end with <c>pg_stat_force_next_flush()</c></b>, which sends the session's counts when it next goes idle
/// instead of up to ten seconds later. The delay is the validator's measured price (its remarks), not what this test is
/// about; without the call the test would be a ten-second wait per write.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ATablesVersionComesFromItsStatisticsTests : PostgresFixture
{
    [Fact]
    public async Task Every_kind_of_write_moves_the_version_and_reading_does_not()
    {
        await RunAsync($"""create table "{SchemaName}".parcels (objectid integer primary key, name text, geom geometry(Point, 3857))""");
        await RunAsync($"""insert into "{SchemaName}".parcels select g, 'p' || g, st_setsrid(st_point(g, g), 3857) from generate_series(1, 20) g""", flush: true);

        PostGisFeatureSource source = new(DataSource, Layer("parcels"));
        string first = await SettledAsync(source, null);

        // Reading, analysing and vacuuming are not changes.
        await RunAsync($"""select count(*) from "{SchemaName}".parcels""", flush: true);
        await RunAsync($"""analyze "{SchemaName}".parcels""", flush: true);
        await RunAsync($"""vacuum "{SchemaName}".parcels""", flush: true);
        await Task.Delay(1200);
        Assert.Equal(first, await source.CacheValidatorAsync(CancellationToken.None));

        string inserted = await AfterAsync(source, first, $"""insert into "{SchemaName}".parcels values (100, 'new', null)""");
        string updated = await AfterAsync(source, inserted, $"""update "{SchemaName}".parcels set name = 'changed' where objectid = 1""");
        string deleted = await AfterAsync(source, updated, $"""delete from "{SchemaName}".parcels where objectid = 2""");

        // TRUNCATE counts no rows: the file node is what moves.
        string truncated = await AfterAsync(source, deleted, $"""truncate "{SchemaName}".parcels""");
        Assert.NotEqual(first, truncated);
    }

    [Fact]
    public async Task A_view_and_an_absent_table_have_no_version()
    {
        await RunAsync($"""create table "{SchemaName}".base (objectid integer primary key, geom geometry(Point, 3857))""");
        await RunAsync($"""create view "{SchemaName}".seen as select * from "{SchemaName}".base""");

        Assert.NotNull(await new PostGisFeatureSource(DataSource, Layer("base")).CacheValidatorAsync(CancellationToken.None));
        Assert.Null(await new PostGisFeatureSource(DataSource, Layer("seen")).CacheValidatorAsync(CancellationToken.None));
        Assert.Null(await new PostGisFeatureSource(DataSource, Layer("absent")).CacheValidatorAsync(CancellationToken.None));
    }

    private LayerDefinition Layer(string name) => new(
        name: name,
        schemaName: SchemaName,
        tableName: name,
        geometryColumn: "geom",
        srid: 3857,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: false);

    private async Task RunAsync(string sql, bool flush = false)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync();
        await using (NpgsqlCommand command = new(sql, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        if (flush)
        {
            await using NpgsqlCommand force = new("select pg_stat_force_next_flush()", connection);
            await force.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Makes a write and waits, at most five seconds, for the version to move from <paramref name="before"/>.</summary>
    private async Task<string> AfterAsync(PostGisFeatureSource source, string before, string write)
    {
        await RunAsync(write, flush: true);
        string after = await SettledAsync(source, before);
        Assert.NotEqual(before, after);
        return after;
    }

    private static async Task<string> SettledAsync(PostGisFeatureSource source, string? before)
    {
        Stopwatch waited = Stopwatch.StartNew();
        string? version;
        do
        {
            await Task.Delay(100);
            version = await source.CacheValidatorAsync(CancellationToken.None);
        }
        while ((version is null || version == before) && waited.Elapsed < TimeSpan.FromSeconds(5));

        Assert.NotNull(version);
        return version!;
    }
}
