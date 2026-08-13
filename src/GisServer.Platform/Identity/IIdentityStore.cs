using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Platform.Identity;

/// <summary>A live session and the principal it belongs to.</summary>
/// <param name="SessionId">The session's id, for revocation and audit.</param>
/// <param name="Principal">Who it is.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public readonly record struct AuthenticatedSession(
    Guid SessionId, Principal Principal, DateTimeOffset ExpiresAt);

/// <summary>
/// Everything the login and authentication paths read and write.
/// </summary>
/// <remarks>
/// <para>
/// A Tier 1 port. The implementation is PostgreSQL because Q-70 made the
/// platform store mandatory, but nothing in the identity logic knows that, and
/// the split is what lets the throttle and the login sequence be tested without
/// a database.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> This is not a repository over the identity
/// tables — administration will need a much wider surface (ADR-017), and putting
/// it here would mean the request path depends on an interface mostly concerned
/// with things it must never do.
/// </para>
/// </remarks>
public interface IIdentityStore
{
    /// <summary>Resolves a presented token to a session, or null.</summary>
    /// <param name="tokenHash">The SHA-256 of the token, from <see cref="SessionToken.HashOf"/>.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// Returns null for an unknown, expired, revoked, or disabled-principal
    /// session — all four are "not authenticated" and the caller must not be
    /// able to tell them apart, because the difference is only useful to someone
    /// probing.
    /// </remarks>
    Task<AuthenticatedSession?> FindSessionAsync(
        byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Finds a principal by name, with its local credential if it has one.</summary>
    /// <returns>Null if no principal has that name.</returns>
    Task<(Principal Principal, PasswordHash? Credential)?> FindForLoginAsync(
        string name, CancellationToken cancellationToken);

    /// <summary>Counts recent failed attempts for the throttle.</summary>
    /// <param name="name">The name offered, which need not exist.</param>
    /// <param name="address">The source address, or null if unknown.</param>
    /// <param name="since">The start of the window.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// Counted against the <em>name offered</em> rather than a resolved
    /// principal id, so that guesses at names which do not exist are still
    /// counted. Counting only real accounts would let an attacker enumerate for
    /// free.
    /// </remarks>
    Task<FailureCounts> CountRecentFailuresAsync(
        string name, IPAddress? address, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Records an attempt, successful or not.</summary>
    Task RecordAttemptAsync(
        string name, IPAddress? address, bool succeeded, CancellationToken cancellationToken);

    /// <summary>Creates a session and returns its id.</summary>
    Task<Guid> CreateSessionAsync(
        Guid principalId,
        byte[] tokenHash,
        DateTimeOffset expiresAt,
        IPAddress? address,
        CancellationToken cancellationToken);

    /// <summary>Revokes a session. Idempotent.</summary>
    Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Replaces a principal's stored password.</summary>
    /// <remarks>
    /// Used both to set a password and to re-hash one that verified against
    /// weaker parameters (<see cref="IPasswordHasher.NeedsRehash"/>).
    /// </remarks>
    Task SetPasswordAsync(Guid principalId, PasswordHash hash, CancellationToken cancellationToken);

    /// <summary>Whether any user principal exists, for the first-start check.</summary>
    /// <remarks>
    /// Asks about <em>user</em> principals specifically: anonymous is seeded by
    /// the migration and always present, so "are there any principals" is always
    /// true and would make the bootstrap in ADR-015 §6 never fire.
    /// </remarks>
    Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken);

    /// <summary>Creates a user principal with a password.</summary>
    Task<Principal> CreateUserAsync(
        string name, string? displayName, PasswordHash password, CancellationToken cancellationToken);
}
