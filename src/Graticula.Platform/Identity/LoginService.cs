using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>Why a login did not succeed.</summary>
public enum LoginFailure
{
    /// <summary>It did succeed.</summary>
    None,

    /// <summary>Wrong name, wrong password, or a disabled account.</summary>
    /// <remarks>
    /// One value for all three, deliberately. Distinguishing them tells an
    /// attacker which names exist, and account enumeration is the step before
    /// every credential-stuffing run.
    /// </remarks>
    InvalidCredentials,

    /// <summary>Too many recent failures against this account.</summary>
    /// <remarks>
    /// Reachable only with a <em>wrong</em> password — see
    /// <see cref="LoginThrottle"/>.
    /// </remarks>
    AccountThrottled,

    /// <summary>Too many recent failures from this address.</summary>
    AddressThrottled,
}

/// <summary>The outcome of a login attempt.</summary>
/// <param name="Failure">Why it failed, or <see cref="LoginFailure.None"/>.</param>
/// <param name="Token">The session token, on success. Returned once and never retrievable again.</param>
/// <param name="Session">The session, on success.</param>
public readonly record struct LoginResult(
    LoginFailure Failure, string? Token, AuthenticatedSession? Session)
{
    /// <summary>Whether it worked.</summary>
    public bool Succeeded => Failure == LoginFailure.None;
}

/// <summary>
/// Authenticates a local password and issues a session.
/// </summary>
/// <remarks>
/// Tier 1, and free of any database or HTTP type, so the sequence below can be
/// tested exhaustively against an in-memory store. That was the point of
/// splitting <see cref="IIdentityStore"/> out: the ordering in
/// <see cref="AuthenticateAsync"/> is a security property, and a security
/// property that needs a container to test is one that gets tested once.
/// </remarks>
public sealed class LoginService
{
    private readonly IIdentityStore _store;
    private readonly IPasswordHasher _hasher;
    private readonly LoginThrottle _throttle;
    private readonly TimeSpan _sessionLifetime;
    private readonly TimeProvider _time;

    /// <summary>A hash of a password nobody knows, to verify against when the name is unknown.</summary>
    /// <remarks>
    /// <para>
    /// <b>Built once, here, at the parameters this process hashes with.</b>
    /// [D-13](../../../docs/architecture-debt.md) said closing the timing gap properly needed a
    /// fixed decoy of the same parameters as the real ones, and called that impossible because
    /// the parameters are stored per credential and are not known before the account is found.
    /// The premise is true and the conclusion does not follow: a credential is rehashed at the
    /// current cost on the way through every successful login, so *the current parameters* are
    /// what an account in use has. A decoy built from the same hasher matches it.
    /// </para>
    /// <para>
    /// <b>Perfect equality was never the target, because real accounts do not have it.</b> An
    /// account that has not logged in since the cost was raised verifies at the old cost, and
    /// that difference exists between two real accounts. What has to be true is that an unknown
    /// name is indistinguishable from a known one at the current parameters, and that is what a
    /// fixed decoy gives.
    /// </para>
    /// <para>
    /// <b>Eager rather than lazy, and it costs one hash at construction.</b> Lazily building it
    /// would put a lock on the unknown-name path, and lock contention is itself a timing signal
    /// — the one this field exists to remove. This service is a singleton; the cost is paid once
    /// per process, before it serves anything.
    /// </para>
    /// </remarks>
    private readonly PasswordHash _decoy;

    /// <summary>Creates the service.</summary>
    /// <param name="store">Where identity lives.</param>
    /// <param name="hasher">The password hasher.</param>
    /// <param name="throttle">The rate limit policy.</param>
    /// <param name="sessionLifetime">How long an issued session lasts.</param>
    /// <param name="time">The clock. Injected so expiry is testable without waiting.</param>
    public LoginService(
        IIdentityStore store,
        IPasswordHasher hasher,
        LoginThrottle throttle,
        TimeSpan sessionLifetime,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(throttle);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionLifetime, TimeSpan.Zero);

        _store = store;
        _hasher = hasher;
        _throttle = throttle;
        _sessionLifetime = sessionLifetime;
        _time = time;

        // <b>A password nobody has and nobody can guess</b>, so the decoy can never accidentally
        // be the one somebody typed. Thirty-two random bytes; what matters is only that the
        // hash exists and was made by this hasher.
        _decoy = hasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>
    /// Verifies a password and issues a session token.
    /// </summary>
    /// <param name="name">The principal name offered.</param>
    /// <param name="password">The password offered.</param>
    /// <param name="address">The source address, or null if it cannot be determined.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <para>
    /// <b>The order of the four steps is the security design</b>, not an
    /// implementation detail, and each one is placed where it is for a stated
    /// reason:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The <b>address</b> limit is checked first, before any expensive work, so
    /// the endpoint cannot be used to burn CPU on Argon2id.
    /// </description></item>
    /// <item><description>
    /// The password is <b>always verified</b> when the address limit permits,
    /// regardless of how many failures the account has. This is what stops the
    /// account limit becoming a lockout weapon.
    /// </description></item>
    /// <item><description>
    /// The <b>account</b> limit is consulted only after verification has already
    /// failed, so it slows guessing and never blocks someone who knows the
    /// password.
    /// </description></item>
    /// <item><description>
    /// A hash is computed even when the name does not exist, so the response
    /// time does not reveal which names are real.
    /// </description></item>
    /// </list>
    /// </remarks>
    public async Task<LoginResult> AuthenticateAsync(
        string name, string password, IPAddress? address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length == 0)
        {
            // No credential can ever have been created from an empty password,
            // so this can never succeed. Handled before the hasher because
            // hashing one is refused, and a request must not become a 500 by
            // taking a path the endpoint happens to guard today.
            await _store.RecordAttemptAsync(name, address, succeeded: false, cancellationToken)
                .ConfigureAwait(false);

            return new LoginResult(LoginFailure.InvalidCredentials, null, null);
        }

        DateTimeOffset now = _time.GetUtcNow();
        FailureCounts counts = await _store
            .CountRecentFailuresAsync(name, address, now - _throttle.Window, cancellationToken)
            .ConfigureAwait(false);

        if (_throttle.RefuseBeforeVerifying(counts))
        {
            // Not recorded as an attempt. Recording it would let a blocked
            // address extend its own block indefinitely, and worse, would let it
            // keep inflating the count for any account name it names.
            return new LoginResult(LoginFailure.AddressThrottled, null, null);
        }

        (Principal Principal, PasswordHash? Credential)? found = await _store
            .FindForLoginAsync(name, cancellationToken)
            .ConfigureAwait(false);

        // <b>Verified before disabled is consulted, and the order is the point.</b> Written the
        // other way round, a disabled account skipped the verification entirely and answered in
        // a fraction of the time an enabled one took — while `LoginFailure.InvalidCredentials`
        // exists precisely so that *wrong name*, *wrong password* and *disabled* are one answer.
        // The enum said so and the timing did not. [D-13](../../../docs/architecture-debt.md).
        bool matches = found is { Credential: { } credential }
            && _hasher.Verify(password, credential);

        bool verified = matches && !found!.Value.Principal.IsDisabled;

        if (!verified)
        {
            if (found is not { Credential: not null })
            {
                // No stored hash to compare against, so verification cost was
                // never paid. Pay something similar anyway: without this, an
                // unknown name returns measurably faster than a known one and
                // the endpoint becomes an account-enumeration oracle.
                SpendComparableTime(password);
            }

            await _store.RecordAttemptAsync(name, address, succeeded: false, cancellationToken)
                .ConfigureAwait(false);

            return new LoginResult(
                _throttle.ThrottleAfterFailure(counts)
                    ? LoginFailure.AccountThrottled
                    : LoginFailure.InvalidCredentials,
                null,
                null);
        }

        Principal principal = found!.Value.Principal;

        // Re-hash on the way through, at the current cost. This is the whole
        // reason the parameters are stored per credential: raising the cost then
        // costs one login per user rather than a password reset per user.
        if (_hasher.NeedsRehash(found.Value.Credential!.Value))
        {
            await _store.SetPasswordAsync(principal.Id, _hasher.Hash(password), cancellationToken)
                .ConfigureAwait(false);
        }

        string token = SessionToken.Generate();
        DateTimeOffset expiresAt = now + _sessionLifetime;

        Guid sessionId = await _store
            .CreateSessionAsync(principal.Id, SessionToken.HashOf(token), expiresAt, address, cancellationToken)
            .ConfigureAwait(false);

        await _store.RecordAttemptAsync(name, address, succeeded: true, cancellationToken)
            .ConfigureAwait(false);

        return new LoginResult(
            LoginFailure.None, token, new AuthenticatedSession(sessionId, principal, expiresAt));
    }

    /// <summary>
    /// Does what a real verification does, for names that do not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same call the real path makes, against <see cref="_decoy"/>.</b> Until 2026-08-23
    /// this hashed the password and then verified against what it had just made — one hash and
    /// one verification where the real path does one verification, so an unknown name took
    /// roughly twice as long as a known one and the endpoint was an enumeration oracle in the
    /// opposite direction from the one the code was guarding against.
    /// </para>
    /// <para>
    /// <b>Still not constant-time, and the remaining difference is stated rather than claimed
    /// away.</b> Argon2 verification cost depends on the stored parameters, so an account that
    /// has not logged in since the cost was last raised verifies at the old cost and differs
    /// from this decoy. That difference is between two *real* accounts as much as between a real
    /// one and an absent one, so it does not say which names exist.
    /// </para>
    /// </remarks>
    private void SpendComparableTime(string password) => _ = _hasher.Verify(password, _decoy);

    /// <summary>Compares two token hashes without leaking length or position.</summary>
    /// <remarks>
    /// Not used by the flow above — session lookup is by indexed equality in the
    /// store — but kept beside it because any future in-process session cache
    /// will need it, and reaching for <c>SequenceEqual</c> there would be the
    /// natural mistake.
    /// </remarks>
    public static bool TokenHashesMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);
}
