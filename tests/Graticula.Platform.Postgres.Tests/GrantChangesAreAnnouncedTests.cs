using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A change to the anonymous caller's grants reaches a listening server, and nothing else does.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-249](../../docs/architecture-debt.md)</b>: migration 44's triggers and
/// <see cref="PostgresGrantsListener"/> together, against a real store. The unit tests pin what the
/// cache does with an announcement; this pins that the announcement arrives — for the change that
/// matters, which is an operator's hand-written SQL, since nothing in the API edits anonymous.
/// </para>
/// <para>
/// <b>The negative half is asserted too.</b> A listener that cleared the cache on every
/// announcement would pass the positive half and throw the cache away every time anybody's role
/// changed, which is the same as not having it on a busy server.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GrantChangesAreAnnouncedTests : PostgresFixture
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_hand_written_grant_to_anonymous_clears_what_a_listening_server_holds()
    {
        await MigrateAsync();

        AnonymousGrants cache = new();
        using CancellationTokenSource stop = new();

        PostgresGrantsListener listener = new(
            Connection(), cache, NullLogger<PostgresGrantsListener>.Instance);

        Task running = listener.RunAsync(stop.Token);

        try
        {
            Assert.True(
                await UntilAsync(() => cache.Listening),
                "The listener never subscribed, so nothing below would be tested.");

            Hold(cache);

            // <b>Somebody else's role changes, and that is not ours.</b> The first repair that
            // comes to mind clears on every announcement, and this is the line that refuses it.
            Guid ada = Guid.NewGuid();
            await ExecuteAsync(
                $"insert into principal (id, kind, name) values ('{ada}', 'user', 'zz_ada')");
            await ExecuteAsync(
                $"insert into principal_role (principal_id, role_name) values ('{ada}', 'viewer')");

            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.True(cache.TryGet(out _), "A change to another principal cleared anonymous.");

            // <b>The operator's statement, qualified by schema as a hand-written one would be</b>,
            // from a session whose search path is not the store's — which is why the trigger names
            // TG_TABLE_SCHEMA rather than current_schema().
            await using (NpgsqlConnection other = new(
                new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GRATICULA_TEST_PG"))
                {
                    SearchPath = "public",
                }.ConnectionString))
            {
                await other.OpenAsync();

                await using NpgsqlCommand grant = new(
                    $"insert into \"{SchemaName}\".principal_role (principal_id, role_name) "
                    + $"values ('{Principal.AnonymousId}', 'viewer')",
                    other);

                await grant.ExecuteNonQueryAsync();
            }

            Assert.True(
                await UntilAsync(() => !cache.TryGet(out _)),
                "A grant to anonymous committed and the listening server kept its old answer.");

            // And a revocation, which is the direction that matters for security.
            Hold(cache);
            await ExecuteAsync(
                $"delete from principal_role where principal_id = '{Principal.AnonymousId}'");

            Assert.True(
                await UntilAsync(() => !cache.TryGet(out _)),
                "A revocation from anonymous committed and the listening server kept its old answer.");
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }

        Assert.False(cache.Listening, "A stopped listener left the cache on.");
    }

    private static void Hold(AnonymousGrants cache)
    {
        cache.Offer(cache.Generation, new AnonymousGrants.Snapshot(UserTypes.Unrestricted, [], [], []));
        Assert.True(cache.TryGet(out _), "The cache would not hold an answer while listening.");
    }

    private string Connection() =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GRATICULA_TEST_PG"))
        {
            SearchPath = $"{SchemaName},public",
        }.ConnectionString;

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
