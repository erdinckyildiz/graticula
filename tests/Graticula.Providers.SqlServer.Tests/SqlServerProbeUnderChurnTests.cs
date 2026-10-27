using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Xunit;

namespace Graticula.Providers.SqlServer.Tests;

/// <summary>
/// A table dropped while the probe reads the database does not fail the probe — D-314.
/// </summary>
/// <remarks>
/// <para>
/// <b>The flaky probe tests were this.</b> The probe lists every geometry table and then reads each one; a table another
/// test class dropped between the two answered <i>Invalid object name</i>, which the probe took for a refusal of the
/// whole database and answered with no tables at all — <c>Assert.Single</c> then failed on an empty list, three times
/// in CI on commits that touched neither provider. A DBA dropping a table while an administrator registers the database
/// is the same race outside the tests.
/// </para>
/// <para>
/// <b>Made deterministic by churning on purpose</b>: one task creates and drops a geometry table every 50 ms or so while
/// the probe runs again and again.
/// </para>
/// </remarks>
public sealed class SqlServerProbeUnderChurnTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_table_dropped_during_the_probe_is_passed_over_rather_than_failing_it()
    {
        using CancellationTokenSource stop = new();

        Task churn = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await SqlServerFixture.ExecuteAsync(
                    "drop table if exists dbo.zzt_churn",
                    "create table dbo.zzt_churn (objectid int primary key, shape geometry)",
                    "insert into dbo.zzt_churn values (1, geometry::STGeomFromText('POINT (1 1)', 4326))",
                    "drop table dbo.zzt_churn");

                // A pause, so the churn is what parallel test classes do rather than a loop of nothing but DDL.
                await Task.Delay(50);
            }
        });

        List<string> failures = [];
        Stopwatch running = Stopwatch.StartNew();

        try
        {
            int probes = 0;

            while (running.Elapsed < TimeSpan.FromSeconds(10) || probes < 20)
            {
                ProbeResult probed = await new SqlServerDataSourceProbe().ProbeAsync(SqlServerFixture.Locator, None);
                probes++;

                if (probed.Outcome != ProbeOutcome.Usable)
                {
                    failures.Add($"{probed.Outcome}: {probed.Message}");
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await churn;
            await SqlServerFixture.ExecuteAsync("drop table if exists dbo.zzt_churn");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} probe(s) failed while a table was created and dropped beside them:\n  "
            + string.Join("\n  ", failures));
    }
}
