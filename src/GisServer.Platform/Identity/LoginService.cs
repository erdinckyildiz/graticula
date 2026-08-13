using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Platform.Identity;

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

        bool verified = found is { Credential: { } credential }
            && !found.Value.Principal.IsDisabled
            && _hasher.Verify(password, credential);

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
    /// Burns roughly the cost of a verification, for names that do not exist.
    /// </summary>
    /// <remarks>
    /// <b>This narrows the timing gap; it does not close it.</b> The work here is
    /// not the same work, and a patient attacker measuring enough samples can
    /// still separate the distributions. Closing it properly means verifying
    /// against a fixed decoy hash of the same parameters as the real ones, which
    /// requires knowing what those are before the account is found. Recorded as
    /// what it is rather than described as constant-time.
    /// </remarks>
    private void SpendComparableTime(string password) =>
        _ = _hasher.Verify(password, _hasher.Hash(password));

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
