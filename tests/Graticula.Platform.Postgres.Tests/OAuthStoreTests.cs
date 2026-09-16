using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Codes and refresh tokens against PostgreSQL — ADR-076 condition 2.
/// </summary>
/// <remarks>
/// The refusals are the point: a protocol whose happy path works and whose second use of a code also
/// works is a protocol that hands a leaked code a long-lived token.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class OAuthStoreTests : PostgresFixture
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private async Task<(PostgresOAuthStore Store, Guid Principal)> ReadyAsync(string client = "probe")
    {
        await MigrateAsync();

        Guid principal = Guid.NewGuid();

        await using (NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name) values (@id, 'user', @name)"))
        {
            command.Parameters.AddWithValue("id", principal);
            command.Parameters.AddWithValue("name", "oauth_" + principal.ToString("N")[..8]);
            await command.ExecuteNonQueryAsync();
        }

        PostgresOAuthStore store = new(DataSource);
        Assert.True(await store.CreateAppAsync(client, "Probe", ["https://app.example/cb"], principal, CancellationToken.None));

        return (store, principal);
    }

    private static byte[] Hash(string value) => SessionToken.HashOf(value);

    [Fact]
    public async Task Field_Maps_ships_registered_under_Esri_s_client_id()
    {
        await MigrateAsync();

        OAuthApp? fieldMaps = await new PostgresOAuthStore(DataSource).FindAppAsync("fieldmaps", CancellationToken.None);

        Assert.NotNull(fieldMaps);
        Assert.True(fieldMaps.Builtin);
        Assert.Contains("arcgis-fieldmaps://auth/", fieldMaps.RedirectUris);
        Assert.Contains(OAuthRules.OutOfBand, fieldMaps.RedirectUris);
    }

    [Fact]
    public async Task A_code_is_redeemed_once_and_its_second_use_revokes_what_the_first_issued()
    {
        (PostgresOAuthStore store, Guid principal) = await ReadyAsync();

        await store.IssueCodeAsync(Hash("code-1"), "probe", principal, "https://app.example/cb", "challenge", "S256",
            Now.AddMinutes(5), CancellationToken.None);

        (CodeRedemption first, OAuthCode? code) = await store.RedeemCodeAsync(Hash("code-1"), Now, CancellationToken.None);

        Assert.Equal(CodeRedemption.Redeemed, first);
        Assert.Equal("probe", code!.ClientId);
        Assert.Equal(principal, code.Principal!.Id);
        Assert.Equal("challenge", code.Challenge);
        Assert.Equal("S256", code.ChallengeMethod);

        await store.IssueRefreshAsync(Hash("refresh-1"), "probe", principal, Now.AddDays(14), Hash("code-1"), CancellationToken.None);
        Assert.NotNull(await store.FindRefreshAsync(Hash("refresh-1"), Now, CancellationToken.None));

        (CodeRedemption second, OAuthCode? again) = await store.RedeemCodeAsync(Hash("code-1"), Now, CancellationToken.None);

        Assert.Equal(CodeRedemption.Replayed, second);
        Assert.Null(again);

        // <b>The refresh token the first use issued is dead</b>: a code held twice has leaked.
        Assert.Null(await store.FindRefreshAsync(Hash("refresh-1"), Now, CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_or_unknown_code_is_unknown_and_revokes_nothing()
    {
        (PostgresOAuthStore store, Guid principal) = await ReadyAsync();

        await store.IssueCodeAsync(Hash("old"), "probe", principal, "https://app.example/cb", null, null,
            Now.AddMinutes(-1), CancellationToken.None);

        Assert.Equal(CodeRedemption.Unknown, (await store.RedeemCodeAsync(Hash("old"), Now, CancellationToken.None)).Outcome);
        Assert.Equal(CodeRedemption.Unknown, (await store.RedeemCodeAsync(Hash("never"), Now, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_disabled_account_redeems_to_no_principal_and_its_refresh_token_is_refused()
    {
        (PostgresOAuthStore store, Guid principal) = await ReadyAsync();

        await store.IssueCodeAsync(Hash("code-d"), "probe", principal, "https://app.example/cb", null, null,
            Now.AddMinutes(5), CancellationToken.None);
        await store.IssueRefreshAsync(Hash("refresh-d"), "probe", principal, Now.AddDays(14), null, CancellationToken.None);

        await using (NpgsqlCommand disable = DataSource.CreateCommand("update principal set disabled_at = now() where id = @id"))
        {
            disable.Parameters.AddWithValue("id", principal);
            await disable.ExecuteNonQueryAsync();
        }

        (CodeRedemption outcome, OAuthCode? code) = await store.RedeemCodeAsync(Hash("code-d"), Now, CancellationToken.None);

        Assert.Equal(CodeRedemption.Redeemed, outcome);
        Assert.Null(code!.Principal);

        Assert.Null(await store.FindRefreshAsync(Hash("refresh-d"), Now, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_an_app_ends_its_codes_and_refresh_tokens()
    {
        (PostgresOAuthStore store, Guid principal) = await ReadyAsync("doomed");

        await store.IssueCodeAsync(Hash("code-x"), "doomed", principal, "https://app.example/cb", null, null,
            Now.AddMinutes(5), CancellationToken.None);
        await store.IssueRefreshAsync(Hash("refresh-x"), "doomed", principal, Now.AddDays(14), null, CancellationToken.None);

        Assert.True(await store.DeleteAppAsync("doomed", CancellationToken.None));
        Assert.False(await store.DeleteAppAsync("doomed", CancellationToken.None));

        Assert.Equal(CodeRedemption.Unknown, (await store.RedeemCodeAsync(Hash("code-x"), Now, CancellationToken.None)).Outcome);
        Assert.Null(await store.FindRefreshAsync(Hash("refresh-x"), Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_refresh_token_revoked_or_expired_is_refused()
    {
        (PostgresOAuthStore store, Guid principal) = await ReadyAsync();

        await store.IssueRefreshAsync(Hash("live"), "probe", principal, Now.AddDays(1), null, CancellationToken.None);
        await store.IssueRefreshAsync(Hash("stale"), "probe", principal, Now.AddDays(-1), null, CancellationToken.None);

        Assert.NotNull(await store.FindRefreshAsync(Hash("live"), Now, CancellationToken.None));
        Assert.Null(await store.FindRefreshAsync(Hash("stale"), Now, CancellationToken.None));

        await store.RevokeRefreshAsync(Hash("live"), CancellationToken.None);
        Assert.Null(await store.FindRefreshAsync(Hash("live"), Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_client_id_is_registered_once()
    {
        (PostgresOAuthStore store, Guid principal) = await ReadyAsync("taken");

        Assert.False(await store.CreateAppAsync("taken", "Again", ["https://other.example/cb"], principal, CancellationToken.None));
    }
}
