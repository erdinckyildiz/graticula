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
/// reach this server as a transport error with no <c>SQLSTATE</c> at all, in which case an
/// arm keyed on the state would never fire. So the one that is inducible is measured rather
/// than assumed.
/// </para>
/// <para>
/// <b>A role's connection limit, not a database's, and the first version of this test used
/// the database.</b> Creating a database with <c>CONNECTION LIMIT 0</c> works and costs
/// nineteen seconds — <c>CREATE DATABASE</c> copies a template, <c>DROP</c> waits for it —
/// and it failed inside the full suite while passing alone, which is the shape
/// <c>QuietDatabaseTests</c> exists to warn about. <c>ALTER ROLE … CONNECTION LIMIT 0</c>
/// raises the same <c>53300</c> in under half a second, creates nothing, and touches no
/// database any other test is using.
/// </para>
/// <para>
/// <b>And it must be a role that is not a superuser.</b> A connection limit does not apply to
/// one, and the first attempt at this measurement connected straight through a limit of zero
/// and proved nothing — the account this suite runs as is a superuser.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class OutOfResourceIsNotUnreachableTests
{
    private const string ConnectionVariable = "GRATICULA_TEST_PG";

    /// <summary>
    /// Named for the test rather than for a person, so nobody mistakes it for a real account.
    /// </summary>
    private const string Role = "graticula_out_of_resource_probe";

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

        await using NpgsqlDataSource maintenance = NpgsqlDataSource.Create(configured!);

        await DropAsync(maintenance);

        try
        {
            // <b>No grant, because `PUBLIC` already has `CONNECT`.</b> Granting it would mean
            // revoking it before the role can be dropped, and a cleanup with a step in it is
            // a cleanup that half-runs.
            await ExecuteAsync(
                maintenance,
                $"create role {Role} login password '{Password}' connection limit 0");

            NpgsqlConnectionStringBuilder limited = new(configured)
            {
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

    private static Task DropAsync(NpgsqlDataSource maintenance) =>
        ExecuteAsync(maintenance, $"drop role if exists {Role}");

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using NpgsqlCommand command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
