using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Where the per-request catalogue read actually spends its time.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-167](../../docs/architecture-debt.md) had subtracted its way to about 1.1 ms that is
/// neither the database nor the cipher, and named what was left without measuring it:</b>
/// Npgsql's parameter binding and reader materialisation, connection acquisition from the
/// pool, and building a <c>PublishedLayer</c>. This measures the two halves against each
/// other on the same connection, the same statement and the same machine — which is what the
/// earlier accounting could not do, because psql and Npgsql are different clients.
/// </para>
/// <para>
/// <b>Two timings, one difference.</b> The catalogue's own <c>FindAsync</c> does everything;
/// a raw command over the same SQL reads every column into locals and builds nothing. What
/// separates them is the object — the definition, the decrypted connection string, the
/// arrays. What they share is the driver and the pool.
/// </para>
/// <para>
/// <b>It reports rather than asserts a budget.</b> A latency threshold on a machine that also
/// runs a database, a browser and a development server is a test that fails on a busy
/// afternoon; the assertion is only that the work happened and the numbers are printed for
/// the row to quote. `Needs: RealCorpus` is wrong for it — it makes its own row — but it is
/// database-backed and belongs in this suite.
/// </para>
/// <para>
/// <b>And the sanity check it does assert is a budget in disguise —
/// [D-175](../../docs/architecture-debt.md), 2026-08-26.</b> *The whole must not be faster
/// than the part it contains* needs no threshold and is still a comparison of two timings
/// with no headroom at all: on a GitHub runner it read 2,028 µs against 2,270 µs and failed
/// twice, correctly refusing a measurement the machine had moved under. The paragraph above
/// was written about absolute thresholds and did not notice that its own guard has the same
/// dependence. `UnprotectCostTests` is the contrast that makes the rule readable: it asserts
/// **100 µs against a measured 2.18** — forty-six times the headroom — and a noisy runner
/// cannot reach it.
/// </para>
/// <para>
/// <b>Two repairs, and they answer different questions.</b> `Needs=QuietMachine` — the axis
/// [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md) defines, already
/// excluded in this job — is about the **numbers**: a latency printed for a register row to
/// quote is wrong on a busy machine whether or not any assertion notices. The tolerance on
/// the sanity check is about the **check**: the two figures it compares differ by about
/// three per cent, so with no tolerance the assertion inverts roughly half the time at the
/// true value, and an assertion that is a coin flip where the answer is correct proves
/// nothing either way. Neither repair is a substitute for the other, and neither is the
/// third thing that was available — widening it until it always passes, which is how a
/// measurement becomes decoration.
/// </para>
/// </remarks>
[Trait("Needs", "QuietMachine")]
public sealed class CatalogReadCostTests(ITestOutputHelper output) : PostgresFixture
{
    /// <summary>Reads to time. Enough that one scheduling hiccup does not move the median.</summary>
    private const int Iterations = 300;

    [Fact]
    public async Task The_catalogue_read_splits_into_driver_and_object()
    {
        await MigrateAsync();

        SecretProtector secrets = new(1, new byte[32]);
        PostgresAdminCatalog admin = new(DataSource, secrets);

        Guid source = await admin.RegisterDataSourceAsync(
            "cost-source", "postgis", "Host=nowhere;Database=none", CancellationToken.None);

        Guid owner = Guid.NewGuid();

        await using (NpgsqlCommand principal = DataSource.CreateCommand(
            "insert into principal (id, name, kind, user_type) "
            + "values (@id, 'cost-publisher', 'user', 'unrestricted')"))
        {
            principal.Parameters.AddWithValue("id", owner);
            await principal.ExecuteNonQueryAsync();
        }

        await admin.PublishLayerAsync(
            new LayerPublication(
                "cost_layer", source, "public", "cost_layer", "geom", "objectid", "objectid",
                3857, GeometryKind.Point, SharingScope.Private),
            owner,
            CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, secrets);

        // Warm the pool, the plan cache and the JIT. The first read of anything is not the
        // question, and on this path it is the one that opens a connection.
        for (int i = 0; i < 20; i++)
        {
            Assert.NotNull(await catalog.FindAsync("cost_layer", CancellationToken.None));
        }

        Stopwatch clock = Stopwatch.StartNew();

        for (int i = 0; i < Iterations; i++)
        {
            _ = await catalog.FindAsync("cost_layer", CancellationToken.None);
        }

        clock.Stop();
        double whole = clock.Elapsed.TotalMilliseconds / Iterations;

        // The same shape of work with nothing built: every column is read, and dropped.
        const string Sql = """
            select l.*, d.*, s.*,
                   (select coalesce(array_agg(gi.group_id), '{}')
                      from sharing_group_item gi where gi.service_id = s.id)
              from layer l
              join data_source d on d.id = l.data_source_id
              join service s on s.id = l.service_id
             where l.name = @name
             order by s.name
             limit 1
            """;

        for (int i = 0; i < 20; i++)
        {
            await ReadRawAsync(Sql);
        }

        clock.Restart();

        for (int i = 0; i < Iterations; i++)
        {
            await ReadRawAsync(Sql);
        }

        clock.Stop();
        double driver = clock.Elapsed.TotalMilliseconds / Iterations;

        // <b>And the read the request path actually makes.</b> `ServiceLookup` resolves a
        // *service* with all its layers, not one layer, so `FindAsync` is not what the
        // server's lookup phase spends — which is the gap between this test's first number
        // and D-30's 2,164 µs.
        for (int i = 0; i < 20; i++)
        {
            _ = await catalog.FindServiceAsync(null, "cost_layer", CancellationToken.None);
        }

        clock.Restart();

        for (int i = 0; i < Iterations; i++)
        {
            _ = await catalog.FindServiceAsync(null, "cost_layer", CancellationToken.None);
        }

        clock.Stop();
        double service_ = clock.Elapsed.TotalMilliseconds / Iterations;

        output.WriteLine(
            $"FindService:{service_ * 1000:F0} µs each — what the request path calls.");

        output.WriteLine(
            $"FindAsync:  {whole * 1000:F0} µs each over {Iterations} reads.");

        output.WriteLine(
            $"raw read:   {driver * 1000:F0} µs each — driver, pool and column reads, no object.");

        output.WriteLine(
            $"difference: {(whole - driver) * 1000:F0} µs — building the PublishedLayer, "
            + "including unsealing the connection string.");

        Assert.True(whole > 0 && driver > 0, "Neither measurement ran.");

        // <b>A tenth, because the two are separate samples of the same noisy quantity —
        // [D-175](../../docs/architecture-debt.md).</b> This read `whole >= driver`, with no
        // tolerance at all, and the true difference is about **30 µs on 950** — three per
        // cent. Two 300-iteration medians straddling a value that close invert about as
        // often as not: it failed at 923 against 956 locally and at 2,028 against 2,270 in
        // CI, neither of which is a defect in anything. **A check whose outcome at the true
        // value is a coin flip is not a check.** Ten per cent still catches what this exists
        // to catch — a decomposition that came out backwards, where the containing read is
        // *materially* faster than the read it contains and the printed difference is a
        // negative cost somebody would quote.
        const double Tolerance = 0.9;

        Assert.True(
            whole >= driver * Tolerance,
            $"The catalogue read ({whole * 1000:F0} µs) came out more than a tenth faster than "
            + $"the raw read it contains ({driver * 1000:F0} µs). That is not measurement "
            + "noise: the whole cannot be materially cheaper than its part, so either the "
            + "machine moved under the measurement or the two are no longer reading the same "
            + "thing.");
    }

    private async Task ReadRawAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("name", "cost_layer");

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                // Read every value so the driver does the same materialisation work the
                // catalogue makes it do; discard it so nothing is built on top.
                _ = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
        }
    }
}
