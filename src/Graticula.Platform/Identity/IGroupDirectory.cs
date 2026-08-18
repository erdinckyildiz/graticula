using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// What a group may confer on the items shared with it — ADR-036 §4b, fixed at creation.
/// </summary>
public enum GroupItemUpdate
{
    /// <summary>
    /// Members read what is shared with the group and nothing more. The default.
    /// </summary>
    /// <remarks>
    /// <b>A narrowing the reference does not appear to offer, and ADR-036 §4c argues for it:</b> a
    /// group whose purpose is *these people may read this* should not have to declare an editing
    /// posture it will never use.
    /// </remarks>
    None,

    /// <summary>Members may edit items they themselves shared into the group.</summary>
    OwnItems,

    /// <summary>Members may edit every item shared with the group.</summary>
    AllItems,
}

/// <summary>Where a principal stands in relation to one group — ADR-036 §3's second axis.</summary>
public enum GroupStanding
{
    /// <summary>Not in it. Reads nothing shared with it.</summary>
    Outside,

    /// <summary>In it. Reads what is shared with it.</summary>
    Member,

    /// <summary>
    /// In it, and holds the group's operations inside it without owning it.
    /// </summary>
    /// <remarks>
    /// The owner: *"bir grubun sahibi olmayabilirsin ama yönetici olarak atanırsan, sen de grupta
    /// yetkili işlemler yapabilirsin."*
    /// </remarks>
    Manager,

    /// <summary>Owns it. May delete and transfer it, which a manager may not.</summary>
    /// <remarks>
    /// <b>Distinct from <see cref="Manager"/> on purpose — ADR-036 §3.</b> It is the difference
    /// between delegating work and delegating control, and a model that conflates them makes every
    /// helper a potential deleter.
    /// </remarks>
    Owner,
}

/// <summary>A group, and what the caller needs to know about it.</summary>
/// <param name="Id">Its identity.</param>
/// <param name="Name">Its name, unique case-insensitively.</param>
/// <param name="Title">A display title, or null.</param>
/// <param name="Description">What it is for, or null.</param>
/// <param name="Owner">Who owns it, by name.</param>
/// <param name="ItemUpdate">What it confers on its items. Fixed at creation.</param>
/// <param name="Members">How many principals belong.</param>
/// <param name="Items">How many services are shared with it.</param>
/// <param name="Standing">Where the asking principal stands, if one was named.</param>
public sealed record GroupSummary(
    Guid Id,
    string Name,
    string? Title,
    string? Description,
    string? Owner,
    GroupItemUpdate ItemUpdate,
    int Members,
    int Items,
    GroupStanding Standing);

/// <summary>Why a group operation was refused.</summary>
public enum GroupChange
{
    /// <summary>It happened.</summary>
    Done,

    /// <summary>No group by that name or id.</summary>
    Absent,

    /// <summary>A group by that name already exists.</summary>
    Exists,

    /// <summary>
    /// The principal neither owns nor manages this group.
    /// </summary>
    /// <remarks>
    /// <b>ADR-036 §3 and condition 2.</b> `groups:manageMembers` is not *manage anybody's group*; a
    /// privilege that turned out to be global is the escalation this decision would otherwise have
    /// introduced.
    /// </remarks>
    NotYours,

    /// <summary>Only the owner or an administrator may do this — deleting or transferring.</summary>
    OwnerOnly,

    /// <summary>The named member or service does not exist.</summary>
    NoSuchTarget,

    /// <summary>
    /// The capability cannot be changed after creation — ADR-036 §4c.
    /// </summary>
    Immutable,
}

/// <summary>
/// Groups, their members and what is shared with them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fourth port over the platform store, for the reason the third one gave.</b> Every request
/// touches <see cref="IIdentityStore"/> and <see cref="IRoleGrants"/>; only a caller with a
/// <c>groups:*</c> privilege touches this.
/// </para>
/// <para>
/// <b>Every method takes the acting principal, and that is what makes the second axis real.</b> A
/// directory whose methods took only a group id would leave the membership check to each caller —
/// ADR-036 condition 2 is precisely that this must not be possible to forget.
/// </para>
/// </remarks>
public interface IGroupDirectory
{
    /// <summary>Groups this principal can see, with where they stand in each.</summary>
    /// <param name="principal">Who is asking.</param>
    /// <param name="all">
    /// True to list every group regardless of membership, which needs
    /// <c>admin:manageAllContent</c> and is the caller's to check.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The groups, by name.</returns>
    Task<IReadOnlyList<GroupSummary>> ListAsync(
        Guid principal, bool all, CancellationToken cancellationToken);

    /// <summary>Creates a group owned by this principal.</summary>
    /// <param name="owner">Who will own it.</param>
    /// <param name="name">Its name.</param>
    /// <param name="title">A display title, or null.</param>
    /// <param name="description">What it is for, or null.</param>
    /// <param name="itemUpdate">What it confers, fixed here and never again.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and the group's id when it was created.</returns>
    Task<(GroupChange Outcome, Guid Id)> CreateAsync(
        Guid owner,
        string name,
        string? title,
        string? description,
        GroupItemUpdate itemUpdate,
        CancellationToken cancellationToken);

    /// <summary>Removes a group. The owner's act, or an administrator's.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <b>Members and shares go with it, and nothing else does.</b> The services shared into it keep
    /// existing and revert to whatever their own scope says — deleting a group must not unpublish
    /// somebody's data, which is the difference between this and ADR-015 §6c's `delete` disposition.
    /// </remarks>
    Task<GroupChange> RemoveAsync(
        Guid acting, bool administrator, string name, CancellationToken cancellationToken);

    /// <summary>Adds or re-grades a member.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="asManager">Whether they are being made a manager.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<GroupChange> SetMemberAsync(
        Guid acting,
        bool administrator,
        string name,
        string member,
        bool asManager,
        CancellationToken cancellationToken);

    /// <summary>Removes a member.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<GroupChange> RemoveMemberAsync(
        Guid acting,
        bool administrator,
        string name,
        string member,
        CancellationToken cancellationToken);

    /// <summary>Shares a service with a group, or stops.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="service">The service's name.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="wanted">True to share, false to unshare.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <b>Sharing into a group does not change the service's own scope.</b> A service reaches
    /// `group` scope because somebody set it there; this table says *which* groups. Setting the scope
    /// and choosing the groups are two acts because either without the other is a state somebody
    /// would misread — and the console says so on the screen.
    /// </remarks>
    Task<GroupChange> ShareAsync(
        Guid acting,
        bool administrator,
        string name,
        string service,
        string? folder,
        bool wanted,
        CancellationToken cancellationToken);

    /// <summary>Which members a group has, and what each is.</summary>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Member name to standing, empty when there is no such group.</returns>
    Task<IReadOnlyList<(string Member, GroupStanding Standing)>> MembersAsync(
        string name, CancellationToken cancellationToken);

    /// <summary>Which services are shared with a group.</summary>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Qualified service names.</returns>
    Task<IReadOnlyList<string>> ItemsAsync(string name, CancellationToken cancellationToken);
}
