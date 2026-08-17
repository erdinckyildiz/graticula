using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// The first-start bootstrap.
/// </summary>
/// <remarks>
/// <para>
/// ADR-015 §6. On first start with no user accounts, the server issues a
/// single-use setup token, writes it to the log, and refuses everything else
/// until it is redeemed.
/// </para>
/// <para>
/// <b>What was rejected, and why it matters that it stays rejected:</b> a
/// default username and password, which survives into production and is the
/// single most reliable way to be compromised; and an unauthenticated setup
/// window, which is a race against whoever scans the network first — a race the
/// scanner wins, because it is already running.
/// </para>
/// <para>
/// <b>Separate from <see cref="IIdentityStore"/></b> because it is used exactly
/// once in a deployment's life and must not be reachable from the request path
/// afterwards. Folding it in would put "create the first administrator" one
/// method call away from every handler.
/// </para>
/// </remarks>
public interface ISetupStore
{
    /// <summary>Whether an unused, unexpired setup token already exists.</summary>
    /// <remarks>
    /// Asked before issuing, so a restart during setup does not print a second
    /// valid token. Two live tokens is two credentials for the same one-time
    /// act, which is what condition 4 is about.
    /// </remarks>
    Task<bool> HasUsableTokenAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Issues a setup token.</summary>
    /// <param name="expiresAt">When it stops working.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The token, in plaintext. Only the hash is stored.</returns>
    Task<string> IssueAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>
    /// Redeems a token and creates the first administrator, atomically.
    /// </summary>
    /// <param name="token">The token as presented.</param>
    /// <param name="name">The administrator's principal name.</param>
    /// <param name="displayName">A human label, or null.</param>
    /// <param name="password">The already-hashed password.</param>
    /// <param name="role">
    /// The role to grant, in the same transaction. ADR-018 §4: a setup flow that
    /// creates an account and no grant produces a server with exactly one
    /// account, which can do nothing, and no way to grant anything to it. The
    /// recovery is hand-written SQL, which is what this work exists to remove.
    /// </param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The administrator, or null if the token was unknown, expired or already used.</returns>
    /// <remarks>
    /// <b>One transaction, and the token is marked used by a conditional update
    /// rather than by a read-then-write.</b> Two requests arriving together with
    /// the same token must produce one administrator, and a check followed by an
    /// update has a window between them wide enough for both to pass. The
    /// database decides, once.
    /// </remarks>
    Task<Principal?> RedeemAsync(
        string token,
        string name,
        string? displayName,
        PasswordHash password,
        string role,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
