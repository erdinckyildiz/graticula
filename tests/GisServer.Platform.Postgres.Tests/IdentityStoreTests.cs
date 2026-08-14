using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// <see cref="PostgresIdentityStore"/> against a real PostgreSQL.
/// </summary>
/// <remarks>
/// The sequencing that makes login safe is tested in memory, where it belongs.
/// What can only be checked here is that the SQL means what it was written to
/// mean: that the four reasons to refuse a session are all in the where clause,
/// that the failure counts are windowed, and that creating a user is one
/// transaction.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class IdentityStoreTests : PostgresFixture
{
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Other = IPAddress.Parse("198.51.100.9");

    private static PasswordHash SomeHash(string marker = "x") =>
        new("fake", """{"v":1}""", System.Text.Encoding.UTF8.GetBytes(marker.PadRight(48, '.')));

    private PostgresIdentityStore Identity() => new(DataSource);

    [Fact]
    public async Task Creating_a_user_writes_the_principal_and_the_credential_together()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        Principal created = await store.CreateUserAsync("ada", "Ada", SomeHash(), CancellationToken.None);

        (Principal Principal, PasswordHash? Credential)? found =
            await store.FindForLoginAsync("ada", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Value.Principal.Id);
        Assert.Equal(PrincipalKind.User, found.Value.Principal.Kind);
        Assert.NotNull(found.Value.Credential);
        Assert.Equal("fake", found.Value.Credential!.Value.Algorithm);
    }

    [Fact]
    public async Task The_anonymous_principal_exists_from_the_migration_and_has_no_credential()
    {
        // ADR-015 §2a made physical. If this row is missing, every authorization
        // check has to handle a null principal, which is the shape the ADR
        // rejected.
        await MigrateAsync();

        (Principal Principal, PasswordHash? Credential)? found =
            await Identity().FindForLoginAsync("anonymous", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(PrincipalKind.Anonymous, found!.Value.Principal.Kind);
        Assert.Null(found.Value.Credential);
        Assert.Equal(Principal.AnonymousId, found.Value.Principal.Id);
    }

    [Fact]
    public async Task A_user_created_before_any_other_is_what_AnyUserExists_reports()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        // Anonymous is seeded, so a naive "any principals?" would be true from
        // the start and the bootstrap in ADR-015 §6 would never fire.
        Assert.False(await store.AnyUserExistsAsync(CancellationToken.None));

        await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        Assert.True(await store.AnyUserExistsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_session_resolves_by_token_hash_and_carries_its_principal()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", "Ada", SomeHash(), CancellationToken.None);

        string token = SessionToken.Generate();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid sessionId = await store.CreateSessionAsync(
            ada.Id, SessionToken.HashOf(token), now.AddHours(1), Address, CancellationToken.None);

        AuthenticatedSession? resolved = await store.FindSessionAsync(
            SessionToken.HashOf(token), now, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(sessionId, resolved!.Value.SessionId);
        Assert.Equal("ada", resolved.Value.Principal.Name);
    }

    [Fact]
    public async Task A_revoked_session_does_not_resolve()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        string token = SessionToken.Generate();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid id = await store.CreateSessionAsync(
            ada.Id, SessionToken.HashOf(token), now.AddHours(1), null, CancellationToken.None);

        await store.RevokeSessionAsync(id, CancellationToken.None);

        Assert.Null(await store.FindSessionAsync(SessionToken.HashOf(token), now, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_twice_does_not_move_the_first_revocation_timestamp()
    {
        // coalesce, not an unconditional set. The first revocation is the one an
        // audit trail is about.
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        Guid id = await store.CreateSessionAsync(
            ada.Id, SessionToken.HashOf(SessionToken.Generate()),
            DateTimeOffset.UtcNow.AddHours(1), null, CancellationToken.None);

        await store.RevokeSessionAsync(id, CancellationToken.None);
        DateTime first = await RevokedAtAsync(id);

        await store.RevokeSessionAsync(id, CancellationToken.None);

        Assert.Equal(first, await RevokedAtAsync(id));
    }

    [Fact]
    public async Task An_expired_session_does_not_resolve()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        string token = SessionToken.Generate();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.CreateSessionAsync(
            ada.Id, SessionToken.HashOf(token), now.AddMinutes(1), null, CancellationToken.None);

        Assert.Null(await store.FindSessionAsync(
            SessionToken.HashOf(token), now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task A_session_belonging_to_a_disabled_principal_does_not_resolve()
    {
        // The fourth reason, and the one most easily left out: disabling an
        // account must take effect on the next request, not when its sessions
        // happen to expire. That is ADR-015 §3's argument against JWT, and it
        // only holds if this clause is here.
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        string token = SessionToken.Generate();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.CreateSessionAsync(
            ada.Id, SessionToken.HashOf(token), now.AddHours(1), null, CancellationToken.None);

        await ExecuteAsync($"update principal set disabled_at = now() where id = '{ada.Id}'");

        Assert.Null(await store.FindSessionAsync(SessionToken.HashOf(token), now, CancellationToken.None));
    }

    [Fact]
    public async Task Failure_counts_separate_the_account_from_the_address()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        await store.RecordAttemptAsync("ada", Address, succeeded: false, CancellationToken.None);
        await store.RecordAttemptAsync("ada", Other, succeeded: false, CancellationToken.None);
        await store.RecordAttemptAsync("grace", Address, succeeded: false, CancellationToken.None);

        FailureCounts counts = await store.CountRecentFailuresAsync(
            "ada", Address, DateTimeOffset.UtcNow.AddMinutes(-15), CancellationToken.None);

        Assert.Equal(2, counts.ForAccount);
        Assert.Equal(2, counts.ForAddress);
    }

    [Fact]
    public async Task A_successful_attempt_is_not_counted_as_a_failure()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        await store.RecordAttemptAsync("ada", Address, succeeded: true, CancellationToken.None);

        FailureCounts counts = await store.CountRecentFailuresAsync(
            "ada", Address, DateTimeOffset.UtcNow.AddMinutes(-15), CancellationToken.None);

        Assert.Equal(0, counts.ForAccount);
    }

    [Fact]
    public async Task Failures_are_counted_against_a_name_that_does_not_exist()
    {
        // Counting only real accounts would make the endpoint a free enumeration
        // oracle: guesses at names that exist would be throttled and guesses at
        // names that do not would not be, which is the answer the attacker wants.
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        await store.RecordAttemptAsync("no-such-person", Address, false, CancellationToken.None);

        FailureCounts counts = await store.CountRecentFailuresAsync(
            "no-such-person", Address, DateTimeOffset.UtcNow.AddMinutes(-15), CancellationToken.None);

        Assert.Equal(1, counts.ForAccount);
    }

    [Fact]
    public async Task Failures_before_the_window_are_not_counted()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        await store.RecordAttemptAsync("ada", Address, false, CancellationToken.None);
        await ExecuteAsync("update login_attempt set attempted_at = now() - interval '1 hour'");

        FailureCounts counts = await store.CountRecentFailuresAsync(
            "ada", Address, DateTimeOffset.UtcNow.AddMinutes(-15), CancellationToken.None);

        Assert.Equal(0, counts.ForAccount);
        Assert.Equal(0, counts.ForAddress);
    }

    [Fact]
    public async Task An_attempt_with_no_address_is_recorded_and_counts_for_the_account_only()
    {
        // A null source address must not be a null-propagating comparison that
        // silently matches every other null.
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        await store.RecordAttemptAsync("ada", null, false, CancellationToken.None);
        await store.RecordAttemptAsync("grace", null, false, CancellationToken.None);

        FailureCounts counts = await store.CountRecentFailuresAsync(
            "ada", null, DateTimeOffset.UtcNow.AddMinutes(-15), CancellationToken.None);

        Assert.Equal(1, counts.ForAccount);
        Assert.Equal(0, counts.ForAddress);
    }

    [Fact]
    public async Task Setting_a_password_replaces_the_previous_one()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash("first"), CancellationToken.None);

        await store.SetPasswordAsync(ada.Id, SomeHash("second"), CancellationToken.None);

        (Principal, PasswordHash? Credential)? found =
            await store.FindForLoginAsync("ada", CancellationToken.None);

        Assert.StartsWith(
            "second",
            System.Text.Encoding.UTF8.GetString(found!.Value.Credential!.Value.Hash),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_does_not_exist_returns_null_rather_than_throwing()
    {
        await MigrateAsync();

        Assert.Null(await Identity().FindForLoginAsync("nobody at all", CancellationToken.None));
    }

    [Fact]
    public async Task Granting_a_role_twice_keeps_the_first_grant()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        await store.GrantRoleAsync(ada.Id, Roles.Viewer, null, CancellationToken.None);
        await store.GrantRoleAsync(ada.Id, Roles.Viewer, ada.Id, CancellationToken.None);

        Assert.Equal([Roles.Viewer], await store.RolesOfAsync(ada.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_grant_naming_a_role_that_does_not_exist_is_refused_by_the_database()
    {
        // The foreign key is what stops a typo becoming a grant that confers
        // nothing and looks like a grant. Roles.PermissionsOf answers "nothing"
        // for an unknown role, which is the safe reading at read time — but the
        // write should never have been allowed in the first place.
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        Npgsql.PostgresException error = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => store.GrantRoleAsync(ada.Id, "superuser", null, CancellationToken.None));

        Assert.Equal("23503", error.SqlState);
    }

    [Fact]
    public async Task Revoking_a_role_takes_it_away_and_is_idempotent()
    {
        await MigrateAsync();
        PostgresIdentityStore store = Identity();
        Principal ada = await store.CreateUserAsync("ada", null, SomeHash(), CancellationToken.None);

        await store.GrantRoleAsync(ada.Id, Roles.Publisher, null, CancellationToken.None);
        await store.RevokeRoleAsync(ada.Id, Roles.Publisher, CancellationToken.None);
        await store.RevokeRoleAsync(ada.Id, Roles.Publisher, CancellationToken.None);

        Assert.Empty(await store.RolesOfAsync(ada.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_can_hold_a_role_which_is_how_an_open_portal_is_configured()
    {
        // ADR-015 §2a's whole point, and ADR-018 §3's escape hatch: whether a
        // server is public is a row, not a branch in the code.
        await MigrateAsync();
        PostgresIdentityStore store = Identity();

        Assert.False(await store.AnyPrincipalHoldingAsync(Roles.Viewer, CancellationToken.None));

        await store.GrantRoleAsync(Principal.AnonymousId, Roles.Viewer, null, CancellationToken.None);

        Assert.Equal(
            [Roles.Viewer],
            await store.RolesOfAsync(Principal.AnonymousId, CancellationToken.None));
        Assert.True(await store.AnyPrincipalHoldingAsync(Roles.Viewer, CancellationToken.None));
    }

    [Fact]
    public async Task The_setup_flow_leaves_the_first_administrator_able_to_administer()
    {
        // ADR-018 §4. An upgraded store showed what the other outcome looks
        // like: an administrator with no grant, on a server with nobody able to
        // give them one. That is now D-14, and this is the test that the fresh
        // path does not produce it.
        await MigrateAsync();
        PostgresSetupStore setup = new(DataSource);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await setup.IssueAsync(now.AddHours(1), CancellationToken.None);
        Principal? admin = await setup.RedeemAsync(
            token, "root", null, SomeHash(), Roles.PlatformAdministrator, now, CancellationToken.None);

        Assert.Equal(
            [Roles.PlatformAdministrator],
            await Identity().RolesOfAsync(admin!.Id, CancellationToken.None));
    }

    private async Task<DateTime> RevokedAtAsync(Guid sessionId)
    {
        // DateTime, not DateTimeOffset: ExecuteScalar boxes a timestamptz as a
        // DateTime with Kind=Utc, and the unboxing cast to DateTimeOffset fails
        // even though the reader's GetFieldValue<DateTimeOffset> succeeds.
        await using Npgsql.NpgsqlCommand command =
            DataSource.CreateCommand($"select revoked_at from session where id = '{sessionId}'");

        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using Npgsql.NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
