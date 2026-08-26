using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Whether anything else is holding this database while the suite runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-166](../../docs/architecture-debt.md), measured 2026-08-26.</b> `OneSuiteAtATime`
/// closed [D-60](../../docs/architecture-debt.md) by giving each test host an exclusive file
/// handle, which serialises the suites against each other. A `dotnet run` development server
/// takes no such handle, holds its own pool against the same PostgreSQL, and contends exactly
/// as a second suite would. On one commit, minutes apart: this suite with the server up was
/// **3 failed of 369 in 2 m 34 s** — three `DataSourceProbeTests` timing out at 10, 11 and 12
/// seconds — and with it stopped was **369 of 369 in 1 m 22 s**.
/// </para>
/// <para>
/// <b>The false failures cost more than the minute.</b> Each passes in isolation, which is
/// the shape that reads as flakiness and earns a retry rather than a cause. It happened three
/// times in one day before anybody wrote it down.
/// </para>
/// <para>
/// <b>Why this is a test and not a refusal.</b> D-166 lists the cheap repairs and none is
/// clean: a fixture that refuses to run while a server holds the database stops a developer
/// working the way they work, and a warning has nowhere to go that anybody reads. The one
/// measured to help is a message that names the possibility — which is what
/// `PollerPoolTests` does on the other side of the same problem, and it is why that failure
/// was diagnosed in one reading and this one took three.
/// </para>
/// <para>
/// <b>Tagged <c>QuietMachine</c> so it can be filtered out on purpose.</b> A developer who
/// wants one test while their server runs should be able to say so; what they should not have
/// to do is guess why three unrelated tests timed out.
/// </para>
/// </remarks>
[Trait("Needs", "QuietMachine")]
public sealed class QuietDatabaseTests : PostgresFixture
{
    [Fact]
    public async Task Nothing_else_is_holding_this_database_while_the_suite_runs()
    {
        // <b>Counted by application name, not by connection count.</b> This suite has
        // connections of its own and so does any other test host; what makes a run
        // untrustworthy is a *server* — its request pool and its job pollers — which names
        // itself and can be told apart. An unnamed connection is another test host, and
        // OneSuiteAtATime already handles those.
        const string Sql = """
            select count(*)
              from pg_stat_activity
             where datname = current_database()
               and pid <> pg_backend_pid()
               and application_name in ('graticula-jobs', 'graticula')
            """;

        await using NpgsqlCommand command = DataSource.CreateCommand(Sql);

        long held = (long)((await command.ExecuteScalarAsync(CancellationToken.None)) ?? 0L);

        Assert.True(
            held == 0,
            $"{held} connection(s) on this database belong to a running Graticula server. Every "
            + "database-backed timeout in this run is contention rather than a defect — that is "
            + "D-166, measured at three false failures and a doubled wall time. Stop the server "
            + "and run again before believing anything that failed, or filter this suite out "
            + "deliberately with Needs!=QuietMachine.");
    }
}
