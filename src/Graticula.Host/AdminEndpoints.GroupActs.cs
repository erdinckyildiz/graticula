using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>What a group write came to, before either door words it.</summary>
/// <param name="Change">The store's outcome; <see cref="GroupChange.NotYours"/> also for a privilege the role lacks.</param>
/// <param name="Status">The HTTP status Studio answers a refusal with.</param>
/// <param name="Refusal">The sentence, or null when it was done.</param>
internal readonly record struct GroupAct(GroupChange Change, int Status, string? Refusal)
{
    /// <summary>It happened.</summary>
    public bool Done => Refusal is null;

    /// <summary>Done.</summary>
    public static GroupAct Ok { get; } = new(GroupChange.Done, 200, null);
}

/// <summary>
/// Studio's group writes, each as one act that both of its doors call — <c>/admin/groups</c> and the portal's
/// <c>community/createGroup</c> and <c>community/groups/{id}/…</c> (ADR-256).
/// </summary>
/// <remarks>
/// <para>
/// <b>One authorization story for one table, which was ADR-040 §4a's whole reason.</b> §4a kept portal groups
/// read-only because a second write path with its own privilege checks is how two surfaces come to disagree. The owner
/// opened the writes on 2026-11-02 with Studio's privilege (Q-224), and the way that holds is that there is still one
/// path: each act here checks the privilege, asks the store, and writes the audit row, and returns what happened
/// instead of writing a response. The admin handler words it as Studio always has; the portal words it as ArcGIS does.
/// </para>
/// <para>
/// <b>Groups have no recycle bin.</b> A deleted group goes, as it does from Studio; what was shared with it stays and is
/// read under its own sharing (DeleteGroupAsync's note).
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>Makes a group: <c>groups:create</c>, a name, an item-update capability fixed for good.</summary>
    internal static async Task<(GroupAct Act, Guid Id)> CreateGroupActAsync(
        HttpContext context,
        IGroupDirectory groups,
        IAuditLog audit,
        string? name,
        string? title,
        string? description,
        string? itemUpdate,
        string door,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (Lacks(current, Privilege.GroupsCreate) is { } refused)
        {
            return (refused, Guid.Empty);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (new GroupAct(GroupChange.NoSuchTarget, 400, "'name' is required."), Guid.Empty);
        }

        if (!TryReadItemUpdate(itemUpdate, out GroupItemUpdate update, out string? bad))
        {
            return (new GroupAct(GroupChange.NoSuchTarget, 400, bad!), Guid.Empty);
        }

        (GroupChange outcome, Guid id) = await groups.CreateAsync(
            current.Principal.Id, name.Trim(), title?.Trim(), description?.Trim(), update, cancellation)
            .ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            return (Refused(name, outcome, null), Guid.Empty);
        }

        await AuditAsync(
            context, audit, "group.create", name.Trim(), Detail(new { itemUpdate = Wire(update), door }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return (GroupAct.Ok, id);
    }

    /// <summary>Removes a group: its owner, or an administrator; never while it is protected.</summary>
    internal static async Task<GroupAct> DeleteGroupActAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string name, string door, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return new GroupAct(GroupChange.NotYours, 401, "Sign in.");
        }

        GroupChange outcome = await groups.RemoveAsync(
            current.Principal.Id, current.Authorization.Allows(Privilege.AdminManageAllContent), name, cancellation)
            .ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            return Refused(name, outcome, null);
        }

        await AuditAsync(
            context, audit, "group.delete", name, Detail(new { removed = name, door }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return GroupAct.Ok;
    }

    /// <summary>
    /// Replaces a group's settings: its owner or a manager, or an administrator; public takes the privilege an item's
    /// public sharing takes (ADR-199).
    /// </summary>
    internal static async Task<GroupAct> SetGroupSettingsActAsync(
        HttpContext context,
        IGroupDirectory groups,
        IAuditLog audit,
        string name,
        GroupSettingsRequest request,
        string door,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return new GroupAct(GroupChange.NotYours, 401, "Sign in.");
        }

        if (!TryReadVisibility(request.Visibility, out GroupVisibility visibility, out string? bad)
            || !TryReadJoinPolicy(request.JoinPolicy, out GroupJoinPolicy join, out bad)
            || !TryReadContribute(request.Contribute, out GroupContribute contribute, out bad)
            || !TryReadMemberList(request.MemberList, out GroupMemberList memberList, out bad))
        {
            return new GroupAct(GroupChange.NoSuchTarget, 400, bad!);
        }

        if (visibility == GroupVisibility.Public && Lacks(current, Privilege.SharingShareToPublic) is { } refused)
        {
            return refused;
        }

        GroupChange outcome = await groups.SetSettingsAsync(
            current.Principal.Id,
            current.Authorization.Allows(Privilege.AdminManageAllContent),
            name,
            request.Title?.Trim(),
            request.Summary?.Trim(),
            request.Description?.Trim(),
            visibility,
            join,
            contribute,
            request.DeleteLocked,
            memberList,
            request.MembersMayLeave,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            // One unbuilt setting left, so the refusal has one thing to name (SetGroupSettingsAsync's note).
            return Refused(name, outcome, request.JoinPolicy);
        }

        await AuditAsync(
            context, audit, "group.settings", name,
            Detail(new
            {
                visibility = request.Visibility,
                joinPolicy = request.JoinPolicy,
                contribute = request.Contribute,
                deleteLocked = request.DeleteLocked,
                memberList = Wire(memberList),
                membersMayLeave = request.MembersMayLeave,
                door,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return GroupAct.Ok;
    }

    /// <summary>Adds a member or makes one a manager: <c>groups:manageMembers</c>, and the group's owner or a manager.</summary>
    internal static async Task<GroupAct> SetGroupMemberActAsync(
        HttpContext context,
        IGroupDirectory groups,
        IAuditLog audit,
        string name,
        string member,
        bool manager,
        string door,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (Lacks(current, Privilege.GroupsManageMembers) is { } refused)
        {
            return refused;
        }

        GroupChange outcome = await groups.SetMemberAsync(
            current.Principal.Id, current.Authorization.Allows(Privilege.AdminManageAllContent), name, member, manager,
            cancellation).ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            return Refused(name, outcome, member);
        }

        await AuditAsync(
            context, audit, "group.member", name, Detail(new { member, manager, door }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return GroupAct.Ok;
    }

    /// <summary>Removes a member: <c>groups:manageMembers</c>, and the group's owner or a manager.</summary>
    internal static async Task<GroupAct> RemoveGroupMemberActAsync(
        HttpContext context,
        IGroupDirectory groups,
        IAuditLog audit,
        string name,
        string member,
        string door,
        CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (Lacks(current, Privilege.GroupsManageMembers) is { } refused)
        {
            return refused;
        }

        GroupChange outcome = await groups.RemoveMemberAsync(
            current.Principal.Id, current.Authorization.Allows(Privilege.AdminManageAllContent), name, member, cancellation)
            .ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            return Refused(name, outcome, member);
        }

        await AuditAsync(
            context, audit, "group.member.remove", name, Detail(new { member, door }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return GroupAct.Ok;
    }

    /// <summary>The caller leaves: no privilege, because it is their own membership (LeaveGroupAsync's note).</summary>
    internal static async Task<GroupAct> LeaveGroupActAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string name, string door, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return new GroupAct(GroupChange.NotYours, 401, "Sign in.");
        }

        GroupChange outcome = await groups.LeaveAsync(current.Principal.Id, name, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "group.leave", name, Detail(new { left = true, door }),
            succeeded: outcome == GroupChange.Done, cancellation).ConfigureAwait(false);

        return outcome switch
        {
            GroupChange.Done => GroupAct.Ok,

            // A group forbidding this is its own refusal, and it names the way out.
            GroupChange.Locked => new GroupAct(outcome, 409,
                $"'{name}' is an administrative group: its members cannot leave it. Its owner or a manager can remove "
                + "you, or turn the setting off."),

            // The owner is refused differently, because their way out is different.
            GroupChange.OwnerOnly => new GroupAct(outcome, 409,
                $"You own '{name}', so leaving would be walking out of a group nobody can administer. Transfer it or "
                + "delete it."),

            // Absent covers *no such group* and *you were not in it*, and must: two answers would let somebody
            // enumerate groups they are outside.
            _ => new GroupAct(outcome, 404, $"You are not a member of '{name}'."),
        };
    }

    /// <summary>
    /// The caller joins a group that lets people add themselves — ADR-256. No privilege, for leaving's reason: it is the
    /// caller's own membership, and the group's owner decided who may by choosing the policy.
    /// </summary>
    internal static async Task<GroupAct> JoinGroupActAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string name, string door, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return new GroupAct(GroupChange.NotYours, 401, "Sign in.");
        }

        GroupChange outcome = await groups.JoinAsync(current.Principal.Id, name, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "group.join", name, Detail(new { joined = true, door }),
            succeeded: outcome == GroupChange.Done, cancellation).ConfigureAwait(false);

        return outcome == GroupChange.Done ? GroupAct.Ok : Refused(name, outcome, null);
    }

    /// <summary>The refusal for a privilege the role does not hold, as <see cref="Authorize.RequireAsync"/> words it.</summary>
    private static GroupAct? Lacks(RequestPrincipal current, Privilege privilege)
    {
        if (current.Authorization.Allows(privilege))
        {
            return null;
        }

        (int status, string message) = Authorize.Refusal(current, privilege);

        return new GroupAct(GroupChange.NotYours, status, message);
    }

    private static GroupAct Refused(string name, GroupChange outcome, string? target)
    {
        (int status, string message) = GroupChangeRefusal(name, outcome, target);

        return new GroupAct(outcome, status, message);
    }

    /// <summary>Writes a refusal the way Studio answers one.</summary>
    private static Task RefuseAsync(HttpContext context, GroupAct act) => Refuse(context, act.Status, act.Refusal!);
}
