using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// PostgreSQL's <em>insufficient resources</em> class arrives as a state this server can read.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-61](../../docs/open-questions.md) asks what happens when a datastore fills up, and
/// the answer was that the operator is sent to look at the network.</b> Class <c>53</c> —
/// <c>53100</c> disk full, <c>53200</c> out of memory, <c>53300</c> too many connections,
/// <c>53400</c> configuration limit — had no arm in <c>ErrorResponse</c>, and
/// <c>PostgresException</c> derives from <c>NpgsqlException</c>, so all four fell to the
/// general branch and were answered <em>a database this server depends on is unreachable.
/// Check /healthz/ready</em>. The database is running, connected and answering; it has run
/// out of something, and the readiness probe is green.
/// </para>
/// <para>
/// <b>This test exists because the repair could have been dead code.</b> The four states are
/// raised at different moments — <c>53100</c> while a statement runs, <c>53300</c> during
/// connection startup, before any command exists — and a startup failure could plausibly
/// reach this server as a transport error with no <c>SQLSTATE</c> at all, in which case an arm
/// keyed on the state would never fire. So the reachable one is measured rather than assumed:
/// <c>53300</c> is inducible without touching the server's configuration, because a database
/// carries its own <c>CONNECTION LIMIT</c>.
/// </para>
/// <para>
/// <b>Why a role and not the configured user.</b> A connection limit does not apply to a
/// superuser, and the account these tests run as is one — the first attempt at this
/// measurement connected straight through a limit of zero and proved nothing.
/// </para>
/// </remarks>
public sealed class OutOfResourceIsNotUnreachableTests
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    private const string Database = "graticula_out_of_resource_probe";
    private const string Role = "graticula_out_of_resource_role";
    private const string Password = "probe";

    /// <summary>
    /// A refused connection carries <c>53300</c>, not a transport error.
    /// </summary>
    [Fact]
    public async Task Too_many_connections_arrives_as_a_state_and_not_as_an_outage()
    {
        string? configured = Environment.GetEnvironmentVariable(ConnectionVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ConnectionVariable} is not set, so this test FAILS rather than skips.");

        NpgsqlConnectionStringBuilder admin = new(configured) { Database = "postgres" };

        await using NpgsqlDataSource maintenance = NpgsqlDataSource.Create(admin.ConnectionString);

        await DropAsync(maintenance);

        try
        {
            // A database of its own, so nothing else in the suite can be locked out by this.
            await ExecuteAsync(maintenance, $"create database {Database}");
            await ExecuteAsync(
                maintenance, $"create role {Role} login password '{Password}'");
            await ExecuteAsync(
                maintenance, $"grant connect on database {Database} to {Role}");
            await ExecuteAsync(maintenance, $"alter database {Database} connection limit 0");

            NpgsqlConnectionStringBuilder limited = new(configured)
            {
                Database = Database,
                Username = Role,
                Password = Password,
            };

            await using NpgsqlDataSource source = NpgsqlDataSource.Create(limited.ConnectionString);

            PostgresException refused = await Assert.ThrowsAsync<PostgresException>(
                async () => await source.OpenConnectionAsync());

            // <b>The whole point.</b> If this came back as a bare NpgsqlException, an arm keyed
            // on the state could never fire and the repair would be decoration.
            Assert.Equal("53300", refused.SqlState);
        }
        finally
        {
            await DropAsync(maintenance);
        }
    }

    private static async Task DropAsync(NpgsqlDataSource maintenance)
    {
        await ExecuteAsync(maintenance, $"drop database if exists {Database} with (force)");
        await ExecuteAsync(maintenance, $"drop role if exists {Role}");
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using NpgsqlCommand command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
