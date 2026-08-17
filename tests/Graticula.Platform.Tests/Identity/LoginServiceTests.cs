using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// The login sequence.
/// </summary>
/// <remarks>
/// The order of the steps in <see cref="LoginService.AuthenticateAsync"/> is the
/// security design, and every one of these tests exists to hold one step in
/// place. A refactor that reorders them will still return the right answers for
/// the happy path.
/// </remarks>
public sealed class LoginServiceTests
{
    private static readonly IPAddress Address = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress OtherAddress = IPAddress.Parse("198.51.100.9");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePasswordHasher _hasher = new();
    private readonly InMemoryIdentityStore _store;

    public LoginServiceTests()
    {
        _store = new InMemoryIdentityStore(_time);
        _store.Add(User("alice"), _hasher.Hash("correct horse battery"));
    }

    private static Principal User(string name, bool disabled = false) =>
        new(Guid.NewGuid(), PrincipalKind.User, name, name, disabled);

    private LoginService Service(LoginThrottle? throttle = null) =>
        new(_store, _hasher, throttle ?? LoginThrottle.Default, TimeSpan.FromHours(12), _time);

    private Task<LoginResult> Login(string name, string password, IPAddress? address = null) =>
        Service().AuthenticateAsync(name, password, address ?? Address, CancellationToken.None);

    [Fact]
    public async Task A_correct_password_issues_a_session()
    {
        LoginResult result = await Login("alice", "correct horse battery");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Token);
        Assert.Equal("alice", result.Session!.Value.Principal.Name);
        Assert.Equal(_time.GetUtcNow().AddHours(12), result.Session.Value.ExpiresAt);
    }

    [Fact]
    public async Task The_issued_token_resolves_to_the_session_and_is_not_stored_in_the_clear()
    {
        LoginResult result = await Login("alice", "correct horse battery");

        AuthenticatedSession? resolved = await _store.FindSessionAsync(
            SessionToken.HashOf(result.Token!), _time.GetUtcNow(), CancellationToken.None);

        Assert.NotNull(resolved);

        // The store was keyed by the hash, so a lookup by the raw token finds
        // nothing. That is the property that makes a dump of the session table
        // useless rather than a set of live credentials.
        Assert.Null(await _store.FindSessionAsync(
            System.Text.Encoding.UTF8.GetBytes(result.Token!), _time.GetUtcNow(), CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_session_stops_working_immediately()
    {
        // ADR-015 §3's central claim over JWT. If this ever fails, the argument
        // for opaque tokens has failed with it.
        LoginResult result = await Login("alice", "correct horse battery");
        await _store.RevokeSessionAsync(result.Session!.Value.SessionId, CancellationToken.None);

        Assert.Null(await _store.FindSessionAsync(
            SessionToken.HashOf(result.Token!), _time.GetUtcNow(), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_session_stops_working()
    {
        LoginResult result = await Login("alice", "correct horse battery");
        _time.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));

        Assert.Null(await _store.FindSessionAsync(
            SessionToken.HashOf(result.Token!), _time.GetUtcNow(), CancellationToken.None));
    }

    [Theory]
    [InlineData("alice", "wrong password entirely")]
    [InlineData("nobody", "correct horse battery")]
    public async Task A_wrong_name_and_a_wrong_password_are_indistinguishable(string name, string password)
    {
        LoginResult result = await Login(name, password);

        // Same failure value for both. Any difference here is an account
        // enumeration oracle, which is the step before credential stuffing.
        Assert.Equal(LoginFailure.InvalidCredentials, result.Failure);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task An_unknown_name_still_costs_a_verification()
    {
        // Otherwise the endpoint answers "no such account" measurably faster
        // than "wrong password", and the timing difference is the oracle the
        // test above tries to close.
        int before = _hasher.VerifyCalls;
        await Login("nobody", "anything at all");

        Assert.True(_hasher.VerifyCalls > before, "an unknown name skipped the hash entirely.");
    }

    [Fact]
    public async Task A_disabled_account_cannot_log_in_even_with_the_right_password()
    {
        _store.Add(User("bob", disabled: true), _hasher.Hash("correct horse battery"));

        LoginResult result = await Login("bob", "correct horse battery");

        Assert.Equal(LoginFailure.InvalidCredentials, result.Failure);
    }

    [Fact]
    public async Task The_account_limit_cannot_be_used_to_lock_someone_out()
    {
        // ADR-015 condition 3, and the reason LoginThrottle is shaped the way it
        // is. An attacker burns the whole per-account budget; the person who
        // knows the password must still get in. If this fails, we have shipped a
        // denial-of-service tool with a login form on it.
        LoginThrottle throttle = new(TimeSpan.FromMinutes(15), perAccount: 3, perAddress: 100);
        _store.AddFailures("alice", OtherAddress, count: 10);

        LoginResult attacker = await Service(throttle)
            .AuthenticateAsync("alice", "guess", OtherAddress, CancellationToken.None);
        Assert.Equal(LoginFailure.AccountThrottled, attacker.Failure);

        LoginResult alice = await Service(throttle)
            .AuthenticateAsync("alice", "correct horse battery", Address, CancellationToken.None);

        Assert.True(alice.Succeeded, "the account limit locked out a user who knew their password.");
    }

    [Fact]
    public async Task The_address_limit_refuses_before_spending_a_verification()
    {
        // The other half of the shape: this one must fire *before* the hash, or
        // the login endpoint is a CPU amplifier — Argon2id is expensive by
        // design and an attacker gets us to pay for every guess.
        LoginThrottle throttle = new(TimeSpan.FromMinutes(15), perAccount: 3, perAddress: 5);
        _store.AddFailures("whoever", Address, count: 5);

        int before = _hasher.VerifyCalls;
        LoginResult result = await Service(throttle)
            .AuthenticateAsync("alice", "correct horse battery", Address, CancellationToken.None);

        Assert.Equal(LoginFailure.AddressThrottled, result.Failure);
        Assert.Equal(before, _hasher.VerifyCalls);
    }

    [Fact]
    public async Task An_address_that_is_already_blocked_cannot_extend_its_own_block()
    {
        // Recording the refused attempt would let a blocked address keep its own
        // counter topped up forever — and, worse, keep inflating the count for
        // any account name it cares to name.
        LoginThrottle throttle = new(TimeSpan.FromMinutes(15), perAccount: 3, perAddress: 5);
        _store.AddFailures("whoever", Address, count: 5);

        int before = _store.Attempts.Count;
        await Service(throttle).AuthenticateAsync("alice", "no", Address, CancellationToken.None);

        Assert.Equal(before, _store.Attempts.Count);
    }

    [Fact]
    public async Task Failures_outside_the_window_do_not_count()
    {
        LoginThrottle throttle = new(TimeSpan.FromMinutes(15), perAccount: 3, perAddress: 100);
        _store.AddFailures("alice", Address, count: 5);

        _time.Advance(TimeSpan.FromMinutes(16));

        LoginResult result = await Service(throttle)
            .AuthenticateAsync("alice", "still wrong", Address, CancellationToken.None);

        Assert.Equal(LoginFailure.InvalidCredentials, result.Failure);
    }

    [Fact]
    public async Task A_password_hashed_at_a_weaker_cost_is_rehashed_on_the_way_through()
    {
        // This is what the per-credential parameters column is for: raising the
        // cost later costs one login per user instead of a password reset per
        // user.
        _hasher.CurrentParameters = "v2";

        LoginResult result = await Login("alice", "correct horse battery");

        Assert.True(result.Succeeded);
        PasswordHash written = Assert.Single(_store.PasswordsWritten);
        Assert.Equal("v2", written.Parameters);
    }

    [Fact]
    public async Task A_password_already_at_the_current_cost_is_not_rewritten()
    {
        LoginResult result = await Login("alice", "correct horse battery");

        Assert.True(result.Succeeded);
        Assert.Empty(_store.PasswordsWritten);
    }

    [Fact]
    public async Task Both_successful_and_failed_attempts_are_recorded()
    {
        await Login("alice", "wrong");
        await Login("alice", "correct horse battery");

        Assert.Equal(2, _store.Attempts.Count);
        Assert.False(_store.Attempts[0].Succeeded);
        Assert.True(_store.Attempts[1].Succeeded);
    }

    [Fact]
    public async Task Two_logins_produce_two_different_tokens()
    {
        LoginResult first = await Login("alice", "correct horse battery");
        LoginResult second = await Login("alice", "correct horse battery");

        Assert.NotEqual(first.Token, second.Token);

        // And both stay live: signing in on a second device must not sign you
        // out of the first.
        Assert.NotNull(await _store.FindSessionAsync(
            SessionToken.HashOf(first.Token!), _time.GetUtcNow(), CancellationToken.None));
    }
}
