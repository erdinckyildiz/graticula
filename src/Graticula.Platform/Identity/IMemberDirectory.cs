using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>A person with an account, as an administrator sees them.</summary>
/// <param name="Id">Their principal identity.</param>
/// <param name="Name">Their sign-in name.</param>
/// <param name="DisplayName">What to show, or null.</param>
/// <param name="Roles">The roles they hold — usually one (ADR-018 §2a).</param>
/// <param name="UserType">Their ceiling, which caps whatever a role grants.</param>
/// <param name="IsDisabled">Whether they may sign in.</param>
/// <param name="CreatedAt">When the account was made.</param>
/// <param name="OwnsServices">
/// How many services they own. <b>Reported because it is the reason there is no delete:</b>
/// removing the row would orphan every one of them, and an administrator about to disable somebody
/// should see what is attached to them first.
/// </param>
public readonly record struct Member(
    Guid Id,
    string Name,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    string UserType,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    int OwnsServices);

/// <summary>
/// Creating and administering the people with accounts.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second port rather than more of <see cref="IIdentityStore"/>, and that interface's own
/// remarks are the reason.</b> They say it *"is not a repository over the identity tables —
/// administration will need a much wider surface (ADR-017), and putting it here would mean the
/// request path depends on an interface mostly concerned with things it must never do."* These
/// five methods were written into that file first and moved out on reading the paragraph, which
/// is the value of writing the reasoning down beside the code rather than only in an ADR.
/// </para>
/// <para>
/// <b>The split is not cosmetic.</b> <see cref="IIdentityStore"/> is what a request needs to
/// authenticate; this is what an administrator needs to make somebody. Every request in the
/// server touches the first and only <c>admin:manageMembers</c> touches the second, so keeping
/// them apart is what stops a login path from having a route to member creation at all.
/// </para>
/// <para>
/// <b>This is what <see href="../../../docs/architecture-debt.md">D-56</see> was about.</b> Until
/// 2026-08-17 a deployment had exactly one account for ever: the first-run setup created the
/// administrator and nothing created a second. <c>admin:manageMembers</c> was a privilege with
/// nothing behind it, ADR-034 built Studio for a publisher who could not exist, and its condition
/// 1 asked for a test that signs in <em>without</em> <c>admin:manageServer</c> — which needed a
/// reader nobody could create.
/// </para>
/// </remarks>
public interface IMemberDirectory
{
    /// <summary>Creates a member with a role, a user type and a first password, in one transaction.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="displayName">What to show, or null for the name.</param>
    /// <param name="password">Their first password, already hashed.</param>
    /// <param name="role">The role to grant — <see cref="Roles"/>.</param>
    /// <param name="userType">Their ceiling — <see cref="UserTypes"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The member, or null when the name is taken.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the endpoint <see href="../../../docs/architecture-debt.md">D-56</see> is
    /// about.</b> Until 2026-08-17 a deployment had exactly one account for ever: the first-run
    /// setup created the administrator and nothing created a second. <c>admin:manageMembers</c>
    /// existed as a privilege with nothing behind it, [ADR-034](../../../docs/adr/ADR-034-server-and-studio.md)
    /// built Studio for a publisher who could not be created, and its condition 1 — *no screen
    /// appears that its reader cannot use* — asked for a test that signs in **without**
    /// <c>admin:manageServer</c>, which needed a reader who could not exist.
    /// </para>
    /// <para>
    /// <b>Three writes in one transaction, and the reason is the one <c>RedeemAsync</c> gives.</b>
    /// A principal without its role is an account that can do nothing; a principal without a
    /// credential is an account nobody can sign in to; and a credential without a principal is a
    /// row that violates its own foreign key. Committing them together means the member exists
    /// exactly when they are usable. This method is deliberately shaped like <c>RedeemAsync</c>,
    /// which had to solve the same problem for the first account, rather than being three calls a
    /// caller sequences and has to unwind by hand.
    /// </para>
    /// <para>
    /// <b>Null for a taken name rather than an exception.</b> A duplicate is the ordinary case —
    /// somebody typing a name that is already in use — and it deserves a 409 with the name in it,
    /// not a 500 from a unique-constraint violation surfacing three layers up.
    /// </para>
    /// </remarks>
    Task<Principal?> CreateMemberAsync(
        string name,
        string? displayName,
        PasswordHash password,
        string role,
        string userType,
        CancellationToken cancellationToken);

    /// <summary>Every member, with what they hold.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The members, ordered by name.</returns>
    /// <remarks>
    /// <b>Service and anonymous principals are excluded</b>, because this answers *who are the
    /// people*. Anonymous is a principal by design (ADR-015 §2a) and listing it beside accounts
    /// somebody administers would invite an attempt to disable it.
    /// </remarks>
    Task<IReadOnlyList<Member>> ListMembersAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the roles a member holds.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="role">The role they should hold, or null to hold none.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What they held before, or null when there is no such member.</returns>
    /// <remarks>
    /// <b>Replaces rather than adds, because the surface offers one role.</b> The schema allows
    /// several and ADR-018 §2a is written for several; the console and this method offer one,
    /// which is the ArcGIS Portal shape and the one an administrator can reason about. Said here
    /// because a method named <c>Set</c> that added would be a trap.
    /// </remarks>
    Task<IReadOnlyList<string>?> SetRoleAsync(
        string name, string? role, CancellationToken cancellationToken);

    /// <summary>Disables or re-enables a member.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="disabled">Whether they should be disabled.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether they were disabled before, or null when there is no such member.</returns>
    /// <remarks>
    /// <b>Disabling rather than deleting, and there is no delete here.</b> A member owns content:
    /// deleting the row would orphan every service whose <c>owner_principal_id</c> points at it,
    /// and the sharing evaluator reads that column to decide who may read what. Disabling stops
    /// the sign-in and leaves the ownership intact, which is the reversible half of the same
    /// intent.
    /// </remarks>
    Task<bool?> SetDisabledAsync(string name, bool disabled, CancellationToken cancellationToken);

    /// <summary>Replaces a member's password, as an administrator rather than as themselves.</summary>
    /// <param name="name">Their sign-in name.</param>
    /// <param name="password">The new password, already hashed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether there was such a member.</returns>
    /// <remarks>
    /// <b>Separate from the self-service change, because it cannot verify the old one.</b>
    /// <c>/rest/auth/password</c> requires the current password and this does not — an
    /// administrator resetting a forgotten password does not know it. That makes this a stronger
    /// act than it looks: it hands somebody a working credential for an account that owns content,
    /// so it takes <c>admin:manageMembers</c> and it is audited under its own name.
    /// </remarks>
    Task<bool> SetPasswordAsync(
        string name, PasswordHash password, CancellationToken cancellationToken);
}
