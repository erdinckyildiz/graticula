using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

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

    /// <summary>
    /// Revokes every session of a principal except one.
    /// </summary>
    /// <param name="principalId">Whose sessions.</param>
    /// <param name="keep">The session to leave alive, or null to revoke all.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many were revoked.</returns>
    /// <remarks>
    /// <b>What a password change is for.</b> If the password was changed because
    /// it was compromised, leaving the attacker's session alive makes the change
    /// theatre — ADR-015 §3 chose server-side sessions precisely so revocation
    /// takes effect on the next request, and this is the case that most needs
    /// it. The current session is kept so that changing a password does not sign
    /// you out of the screen you changed it on.
    /// </remarks>
    Task<int> RevokeOtherSessionsAsync(
        Guid principalId, Guid? keep, CancellationToken cancellationToken);

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

    /// <summary>Reads the role names granted to a principal.</summary>
    /// <remarks>
    /// <para>
    /// Names, not permissions. What a role carries is a decision in ADR-018 §2a
    /// and lives in <see cref="Roles.Grants"/>; the store records only who holds
    /// what. Resolving permissions in SQL would put the same table in two
    /// places, and the copy in the database would be the one nobody reviews.
    /// </para>
    /// <para>
    /// A role the server does not recognise confers nothing rather than
    /// throwing — see <see cref="Roles.PrivilegesOf"/>.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> RolesOfAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a principal's user type and roles in one round trip.
    /// </summary>
    /// <remarks>
    /// Together, because ADR-018 §3a resolves capability as the intersection of
    /// the two and there is never a reason to have one without the other. Two
    /// queries would be two chances to resolve a ceiling against the wrong roles.
    /// </remarks>
    Task<(string UserType, IReadOnlyList<string> Roles)> GrantsOfAsync(
        Guid principalId, CancellationToken cancellationToken);

    /// <summary>Grants a role. Idempotent.</summary>
    /// <param name="principalId">Who receives it.</param>
    /// <param name="role">The role name.</param>
    /// <param name="grantedBy">Who granted it, or null when the server did.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task GrantRoleAsync(
        Guid principalId, string role, Guid? grantedBy, CancellationToken cancellationToken);

    /// <summary>Revokes a role. Idempotent.</summary>
    Task RevokeRoleAsync(Guid principalId, string role, CancellationToken cancellationToken);

    /// <summary>Whether any principal holds a role.</summary>
    /// <remarks>
    /// For the startup check that warns when nothing is readable by anyone. A
    /// count would be more informative and is not needed: the question is
    /// whether the answer is zero.
    /// </remarks>
    Task<bool> AnyPrincipalHoldingAsync(string role, CancellationToken cancellationToken);
}
