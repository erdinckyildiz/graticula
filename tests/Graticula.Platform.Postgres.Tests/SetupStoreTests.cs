using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// First-start bootstrap, ADR-015 §6.
/// </summary>
/// <remarks>
/// Condition 4 is the reason this file exists: <em>the bootstrap token cannot be
/// reused, tested, including after a restart that occurs mid-setup.</em> A
/// restart is simulated by constructing a new store over the same database,
/// which is exactly what a restart is from the token's point of view.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SetupStoreTests : PostgresFixture
{
    private static PasswordHash SomeHash() =>
        new("fake", """{"v":1}""", new byte[48]);

    private PostgresSetupStore Setup() => new(DataSource);

    [Fact]
    public async Task A_token_redeems_once_and_creates_the_administrator()
    {
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await Setup().IssueAsync(now.AddHours(1), CancellationToken.None);
        Principal? admin = await Setup().RedeemAsync(
            token, "root", "Root", SomeHash(), Roles.Administrator, now, CancellationToken.None);

        Assert.NotNull(admin);
        Assert.Equal("root", admin!.Name);
        Assert.Equal(PrincipalKind.User, admin.Kind);

        Assert.True(await new PostgresIdentityStore(DataSource).AnyUserExistsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_same_token_cannot_be_redeemed_twice()
    {
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await Setup().IssueAsync(now.AddHours(1), CancellationToken.None);
        await Setup().RedeemAsync(token, "root", null, SomeHash(), Roles.Administrator, now, CancellationToken.None);

        Assert.Null(await Setup().RedeemAsync(token, "second", null, SomeHash(), Roles.Administrator, now, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_survives_a_restart_and_is_still_single_use()
    {
        // Condition 4 exactly. A new store over the same database is what the
        // token sees when the process restarts; an in-memory token would have
        // been reissued here, which is a second valid credential for a one-time
        // act rather than the same one.
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await Setup().IssueAsync(now.AddHours(1), CancellationToken.None);

        PostgresSetupStore afterRestart = new(DataSource);
        Assert.True(await afterRestart.HasUsableTokenAsync(now, CancellationToken.None));

        Assert.NotNull(await afterRestart.RedeemAsync(token, "root", null, SomeHash(), Roles.Administrator, now, CancellationToken.None));

        PostgresSetupStore afterSecondRestart = new(DataSource);
        Assert.False(await afterSecondRestart.HasUsableTokenAsync(now, CancellationToken.None));
        Assert.Null(await afterSecondRestart.RedeemAsync(token, "again", null, SomeHash(), Roles.Administrator, now, CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_token_cannot_be_redeemed()
    {
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await Setup().IssueAsync(now.AddMinutes(5), CancellationToken.None);

        Assert.Null(await Setup().RedeemAsync(
            token, "root", null, SomeHash(), Roles.Administrator, now.AddMinutes(6), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_token_is_not_reported_as_usable_so_a_restart_issues_a_new_one()
    {
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await Setup().IssueAsync(now.AddMinutes(5), CancellationToken.None);

        Assert.False(await Setup().HasUsableTokenAsync(now.AddMinutes(6), CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_token_is_refused_without_creating_anything()
    {
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Null(await Setup().RedeemAsync(
            SessionToken.Generate(), "root", null, SomeHash(), Roles.Administrator, now, CancellationToken.None));

        Assert.False(await new PostgresIdentityStore(DataSource).AnyUserExistsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Two_concurrent_redemptions_produce_exactly_one_administrator()
    {
        // The conditional update is what guarantees this. A read-then-write
        // would let both requests see an unused token and both create an
        // administrator, and the second one would be an account nobody
        // authorised.
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await Setup().IssueAsync(now.AddHours(1), CancellationToken.None);

        Principal?[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => new PostgresSetupStore(DataSource)
                .RedeemAsync(token, $"admin{i}", null, SomeHash(), Roles.Administrator, now, CancellationToken.None)));

        Assert.Single(results, r => r is not null);
        Assert.Equal(1, await CountUsersAsync());
    }

    [Fact]
    public async Task A_failed_administrator_creation_leaves_the_token_unused()
    {
        // The recoverable direction. Spending the token without creating anybody
        // would leave a server that can never be administered — the transaction
        // exists for this case, not for the happy path.
        await MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await new PostgresIdentityStore(DataSource)
            .CreateUserAsync("taken", null, SomeHash(), CancellationToken.None);

        string token = await Setup().IssueAsync(now.AddHours(1), CancellationToken.None);

        // The principal name is unique, so this insert violates a constraint
        // after the token has already been marked used inside the transaction.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            Setup().RedeemAsync(token, "taken", null, SomeHash(), Roles.Administrator, now, CancellationToken.None));

        Assert.True(await Setup().HasUsableTokenAsync(now, CancellationToken.None));
        Assert.NotNull(await Setup().RedeemAsync(token, "root", null, SomeHash(), Roles.Administrator, now, CancellationToken.None));
    }

    [Fact]
    public async Task The_token_is_not_stored_in_the_clear()
    {
        await MigrateAsync();
        string token = await Setup().IssueAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        await using Npgsql.NpgsqlCommand command = DataSource.CreateCommand(
            "select count(*) from setup_token where token_hash = @raw");
        command.Parameters.AddWithValue("raw", System.Text.Encoding.UTF8.GetBytes(token));

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private async Task<int> CountUsersAsync()
    {
        await using Npgsql.NpgsqlCommand command =
            DataSource.CreateCommand("select count(*) from principal where kind = 'user'");

        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
