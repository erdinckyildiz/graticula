using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// What a statement bound costs, depending on which end enforces it.
/// </summary>
/// <remarks>
/// <para>
/// <b><see href="../../docs/architecture-debt.md">D-68</see> asserts a behaviour, and a row
/// that asserts a behaviour can be wrong.</b> It says <c>NpgsqlCommand.CommandTimeout</c>
/// gives up on the socket read, leaves the connector in an unknown state, and makes Npgsql
/// discard the physical connection — so every firing of a per-service bound costs a
/// connection — while a server-side <c>statement_timeout</c> raises <c>57014</c> and leaves
/// the connection usable. Both halves are measurable against a real database, and until
/// this they had not been measured against one.
/// </para>
/// <para>
/// <b>The instrument is the backend's own process id.</b> <c>pg_backend_pid()</c> names the
/// server process on the other end of this physical connection. If the pool returns the same
/// connection the id is the same; if it threw the connection away and opened another, the id
/// changes. Nothing about that depends on reading Npgsql's internals.
/// </para>
/// <para>
/// <b>A pool of exactly one, so the question has an answer.</b> With a larger pool the second
/// query may legitimately land on a different connection and the measurement would say
/// nothing. <c>MaxPoolSize=1</c> makes a changed id mean one thing only.
/// </para>
/// </remarks>
public sealed class CommandTimeoutCostTests
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    private static string ConnectionString(int? serverSideMilliseconds)
    {
        string? configured = Environment.GetEnvironmentVariable(ConnectionVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ConnectionVariable} is not set, so these tests FAIL rather than skip. A test that "
            + "goes green with its subject absent is worse than no test.");

        NpgsqlConnectionStringBuilder builder = new(configured)
        {
            // One connection, so a changed backend id means the pool replaced it.
            MaxPoolSize = 1,
            MinPoolSize = 0,
        };

        if (serverSideMilliseconds is { } ms)
        {
            builder.Options = $"-c statement_timeout={ms}";
        }

        return builder.ConnectionString;
    }

    private static async Task<int> BackendIdAsync(NpgsqlDataSource source)
    {
        await using NpgsqlCommand command = source.CreateCommand("select pg_backend_pid()");

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    /// <summary>
    /// Neither a client-side nor a server-side statement bound replaces the connection.
    /// </summary>
    /// <remarks>
    /// <b>Both halves in one test, deliberately.</b> D-68's claim is a comparison — *this end
    /// is worse than that end* — and two tests that each measure one end can both pass while
    /// the comparison is false. Measuring them together is what showed the comparison had no
    /// difference in it: the same backend process answers before and after, at both ends.
    /// What is left between them is the exception a caller meets and the resolution a bound
    /// can have, and this asserts the first of those too.
    /// </remarks>
    [Fact]
    public async Task Neither_end_of_a_statement_bound_costs_the_physical_connection()
    {
        // <b>Client-side: Npgsql gives up on the socket read.</b>
        await using NpgsqlDataSource client = NpgsqlDataSource.Create(ConnectionString(null));

        int before = await BackendIdAsync(client);

        await using (NpgsqlCommand slow = client.CreateCommand("select pg_sleep(5)"))
        {
            slow.CommandTimeout = 1;

            NpgsqlException failure = await Assert.ThrowsAsync<NpgsqlException>(
                () => slow.ExecuteNonQueryAsync(CancellationToken.None));

            Assert.IsType<TimeoutException>(failure.InnerException);
        }

        int afterClientSide = await BackendIdAsync(client);

        // <b>Server-side: PostgreSQL cancels its own statement and says so.</b>
        await using NpgsqlDataSource server = NpgsqlDataSource.Create(ConnectionString(1000));

        int serverBefore = await BackendIdAsync(server);

        await using (NpgsqlCommand slow = server.CreateCommand("select pg_sleep(5)"))
        {
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
                () => slow.ExecuteNonQueryAsync(CancellationToken.None));

            Assert.Equal("57014", failure.SqlState);
        }

        int serverAfter = await BackendIdAsync(server);

        // <b>Measured 2026-08-24, and D-68's premise did not survive it.</b> The row says a
        // CommandTimeout "gives up on the socket read", leaves the connector in an unknown
        // state, and costs the physical connection. It does not: Npgsql sends the backend a
        // cancellation request and keeps the connector, so the pool hands back the same server
        // process. Both ends of the bound preserve the connection, and what actually separates
        // them is the exception a caller sees and the resolution a bound can have.
        Assert.True(
            afterClientSide == before,
            $"A CommandTimeout replaced the physical connection: backend {before} became "
            + $"{afterClientSide}. That is D-68's premise, and if it is true again the row is "
            + "right after all and the command factory should move to a server-side bound. "
            + "Npgsql's cancellation behaviour is the thing to look at.");

        Assert.True(
            serverAfter == serverBefore,
            $"A server-side statement_timeout replaced the physical connection: backend "
            + $"{serverBefore} became {serverAfter}. That would be worse than the client-side "
            + "bound rather than better, and it is the opposite of what D-68 assumes.");
    }
}
