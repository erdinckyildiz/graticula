using System;
using System.Threading.Tasks;
using Graticula.Platform.Jobs;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The job pollers hold their own pool, and it is bounded by the number of job kinds.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-110](../../docs/architecture-debt.md): a pool that prunes correctly cannot prune one
/// somebody keeps knocking on.</b> The workers used to claim on the pool that serves requests,
/// round-robin, so the shared pool could never reach the floor of zero
/// [ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 claims — measured at sixteen
/// backends, eight of which last ran the claim.
/// </para>
/// <para>
/// <b>Asked of the database rather than of the container.</b> The wiring is two lines in
/// `Program.cs` and a test that read them back would assert that a registration exists, not that
/// the pollers are using it. `pg_stat_activity` knows which pool each session belongs to because
/// the pollers' pool names itself, and that is the same evidence
/// [the benchmark](../../benchmarks/connection-budget/RESULTS.md) is written from.
/// </para>
/// <para>
/// <b>It fails rather than skips when the database is absent</b>, following `PostgresFixture`'s
/// rule and for its reason: this project has four times written a test that went green with its
/// subject missing.
/// </para>
/// </remarks>
/// <remarks>
/// <b>This class needs a Graticula host to be running, not just a database —
/// [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md) §5a.</b>
/// It reads <c>pg_stat_activity</c> looking for the sessions the background pollers
/// hold, so with no host there are no sessions and every assertion here is about an
/// absence. It passed locally because a development server happened to be up, and
/// failed on the first CI run this repository ever completed.
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class PollerPoolTests
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    /// <summary>What the pollers' pool calls itself, from `Program.cs`.</summary>
    private const string PollerPool = "graticula-jobs";

    private static string Connection =>
        Environment.GetEnvironmentVariable(ConnectionVariable)
        ?? throw new InvalidOperationException(
            $"{ConnectionVariable} is not set, so nothing can be asked of the database. Set it or "
            + "filter this suite out deliberately; do not let it pass by default.");

    private static async Task<long> CountAsync(string where)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(Connection);

        await using NpgsqlCommand command = source.CreateCommand(
            "select count(*) from pg_stat_activity where datname = current_database() "
            + "and pid <> pg_backend_pid() and " + where);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// The pollers are on a pool of their own, and the database can say so.
    /// </summary>
    /// <remarks>
    /// <b>The half that catches the wiring being undone.</b> Take the keyed store back out of
    /// `Program.cs` and no session is named at all — measured, and the shared pool then settles at
    /// two instead of zero.
    /// </remarks>
    [Fact]
    public async Task The_pollers_hold_a_pool_that_names_itself()
    {
        long named = await CountAsync($"application_name = '{PollerPool}'");

        Assert.True(
            named > 0,
            $"No session calls itself '{PollerPool}', so the background workers are claiming on "
            + "the pool that serves requests. That is D-110: the shared pool cannot prune to zero "
            + "while somebody keeps knocking on it. Program.cs registers the keyed pool; the "
            + "hosted services have to be given it.");
    }

    /// <summary>
    /// One connection per job kind, and the ceiling is the enumeration rather than a constant.
    /// </summary>
    /// <remarks>
    /// <b>Why the enumeration and not a number.</b> The row's complaint is that the floor *grows
    /// with the number of job kinds*, each background service polling independently. Sizing the
    /// pool from `JobKind` makes a third kind cost exactly one more connection, which is the
    /// arithmetic §4.8 asks for; a constant here would drift from the enumeration on the day
    /// somebody adds one.
    /// </remarks>
    [Fact]
    public async Task The_pollers_pool_never_holds_more_than_one_connection_per_job_kind()
    {
        int kinds = Enum.GetValues<JobKind>().Length;

        long held = await CountAsync($"application_name = '{PollerPool}'");

        Assert.True(
            held <= kinds,
            $"The pollers' pool is holding {held} connections for {kinds} job kind(s). MaxPoolSize "
            + "is set from the enumeration, so this means either the pool was sized from something "
            + "else or a second process is running against this database.");
    }

    /// <summary>
    /// Nothing claims a job on the shared pool.
    /// </summary>
    /// <remarks>
    /// <b>The claim is recognisable, and that is what makes this checkable.</b> `for update skip
    /// locked` appears in one statement in this repository — ADR-011 §3.2's claim — and the
    /// request path never runs it: enqueuing is an insert and watching is a select. So a session
    /// on the unnamed pool whose last statement is the claim means a poller reached for the wrong
    /// pool, which is the defect this row is about, in its original form.
    /// </remarks>
    [Fact]
    public async Task No_session_on_the_shared_pool_last_ran_the_job_claim()
    {
        long knocking = await CountAsync(
            "coalesce(application_name, '') <> '" + PollerPool + "' "
            + "and query ilike '%for update skip locked%'");

        Assert.True(
            knocking == 0,
            $"{knocking} session(s) outside the pollers' pool last ran the job claim. Only the "
            + "background workers claim, and they are supposed to do it on their own pool — this "
            + "is D-110's original measurement reappearing: eight of sixteen shared-pool "
            + "connections whose last statement was ClaimAsync, none of which could ever age out.");
    }
}
