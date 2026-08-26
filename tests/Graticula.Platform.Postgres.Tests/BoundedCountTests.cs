using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Counting to a ceiling stops there, and costs what the ceiling costs rather than what the table does.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-118](../../docs/architecture-debt.md): every `GetFeature` counted the whole result before
/// writing a page of it.</b> WFS 2.0 makes <c>numberReturned</c> a required attribute on the
/// collection element, so it has to be known before the first feature is written — and the two
/// ways to know it were to buffer the page, which
/// [A-037](../../docs/architecture-assumptions.md) rules out, or to ask how many rows match.
/// </para>
/// <para>
/// <b>Measured before it was changed, because the row said to</b> —
/// [benchmarks/wfs-count](../../benchmarks/wfs-count/RESULTS.md). On the 6.5-million-row corpus an
/// unfiltered count is 577 ms where the page beside it is 7.6 ms; bounded at 100,000 it is 17.9 ms.
/// </para>
/// <para>
/// <b>These run against the corpus and fail rather than skip when it is absent</b>, following this
/// suite's rule: a timing claim that passes with nothing to time is worse than no claim.
/// </para>
/// </remarks>
/// <remarks>
/// <b>Excluded from CI, deliberately and out loud — [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md).</b>
/// This class reads a real OpenStreetMap extract, which a developer machine has and
/// CI does not. It fails rather than skips when the table is absent, which is the
/// right behaviour and is why CI cannot simply run it. The trait is what CI filters
/// on, and the workflow prints what it excluded so a green run never claims more
/// than it proved.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Needs", "RealCorpus")]
public sealed class BoundedCountTests : PostgresFixture
{
    private readonly ITestOutputHelper _output;

    public BoundedCountTests(ITestOutputHelper output) => _output = output;

    private static LayerDefinition OsmPolygons => new(
        name: "osm-polygons",
        schemaName: "public",
        tableName: "planet_osm_polygon",
        geometryColumn: "way",
        srid: 3857,
        identityColumn: "osm_id",
        integerIdentityColumn: null,
        isHosted: false);

    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select to_regclass('public.planet_osm_polygon') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            "public.planet_osm_polygon is not loaded. These tests measure a count against real "
            + "data and fail rather than skip; load the corpus with experiments/_env, or exclude "
            + "this class by name.");
    }

    private PostGisFeatureSource Source() => new(DataSource, OsmPolygons);

    /// <summary>
    /// The bounded count stops at the ceiling instead of counting the table.
    /// </summary>
    /// <remarks>
    /// <b>Equality with the ceiling is the whole contract.</b> The number means *at least this
    /// many* when it equals the ceiling, and every caller has to treat it so — which is what lets
    /// the WFS writer say <c>numberMatched="unknown"</c> rather than a number that is wrong.
    /// </remarks>
    [Fact]
    public async Task Counting_to_a_ceiling_stops_at_it()
    {
        await RequireCorpusAsync();

        long counted = await Source().CountUpToAsync(
            new FeatureQuery(1000), 5_000, CancellationToken.None);

        Assert.Equal(5_000, counted);
    }

    /// <summary>
    /// Below the ceiling it is the exact total, which is what keeps `numberMatched` a number.
    /// </summary>
    /// <remarks>
    /// <b>A bounding box small enough to be under any ceiling.</b> The filtered case was always
    /// cheap — the row said so — and it is the case that must keep answering exactly, because it
    /// is what a client pans across.
    /// </remarks>
    [Fact]
    public async Task Below_the_ceiling_it_is_the_exact_total()
    {
        await RequireCorpusAsync();

        Envelope box = new(3_000_000, 4_500_000, 3_100_000, 4_600_000);

        FeatureQuery query = new(1000, box);

        long whole = await Source().CountAsync(query, CancellationToken.None);

        Assert.True(
            whole is > 0 and < 1_000_000,
            $"The probe box holds {whole} features, which is not a useful size for this check.");

        long bounded = await Source().CountUpToAsync(
            query, whole + 1, CancellationToken.None);

        Assert.Equal(whole, bounded);
    }

    /// <summary>
    /// The ceiling is what it costs, and the table is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim under test is the shape of the cost, not a millisecond figure.</b> Two
    /// bounded counts on the same 6.5-million-row table, one ten times the other: if the count
    /// were reading the table, they would cost the same. The assertion is loose — the small one
    /// is under half the large one — because a tight bound on a shared machine is a flaky test,
    /// and the measurement that carries the real numbers is in the benchmark.
    /// </para>
    /// <para>
    /// <b>Interleaved and taking the fastest of three</b>, which is this repository's habit after
    /// a timing pair was wrong by 2× measured in sequence, and flaked once on a median.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_bounded_count_costs_the_ceiling_rather_than_the_table()
    {
        await RequireCorpusAsync();

        PostGisFeatureSource source = Source();
        FeatureQuery query = new(1000);

        // Warm: the first statement against a table pays for pages nobody has read yet.
        await source.CountUpToAsync(query, 10_000, CancellationToken.None);

        double small = double.MaxValue;
        double large = double.MaxValue;

        for (int round = 0; round < 3; round++)
        {
            small = Math.Min(small, await MillisecondsAsync(source, query, 10_000));
            large = Math.Min(large, await MillisecondsAsync(source, query, 1_000_000));
        }

        _output.WriteLine($"ceiling 10,000: {small:0.0} ms   ceiling 1,000,000: {large:0.0} ms");

        Assert.True(
            small < large / 2,
            $"Counting to 10,000 took {small:0.0} ms and counting to 1,000,000 took {large:0.0} ms "
            + "on the same table. If the ceiling did not bound the work these would be the same, "
            + "which is D-118: the count was O(table) beside a page that is O(page).");
    }

    private static async Task<double> MillisecondsAsync(
        PostGisFeatureSource source, FeatureQuery query, long ceiling)
    {
        long started = Stopwatch.GetTimestamp();

        await source.CountUpToAsync(query, ceiling, CancellationToken.None);

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
