using System;
using Graticula.Platform.Identity;

namespace Graticula.Host;

/// <summary>
/// One of [ADR-036](../../docs/adr/ADR-036-groups.md)'s groups, in the shape an
/// ArcGIS portal client reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner decision, 2026-09-09 — <em>gruplarımızı göster</em>.</b> The portal face
/// answered <c>/sharing/rest/community/groups</c> with an empty list until then, and
/// the emptiness was the fault: our groups are real, a service can be shared with
/// one, and <em>this portal has no groups</em> was therefore false. What was missing
/// was never code but the decision this type carries — that ours <b>are</b> portal
/// groups rather than something that resembles them, published read-only, with the
/// fields this server holds and none invented to fill a shape.
/// [Q-127](../../docs/open-questions.md).
/// </para>
/// <para>
/// <b>A type of its own so the mapping can be asserted without a web host</b>, which
/// is <see cref="PortalQuery"/>'s reason for existing separately too. It needs only
/// the caller's name and the portal's id from a request, and the four places the
/// mapping is imperfect are worth holding still with a test rather than a comment.
/// </para>
/// <para>
/// <b>The four imperfections, listed here and in
/// [ADR-040](../../docs/adr/ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md)
/// §4a.</b> <c>modified</c> is the creation time, because nothing records when a
/// group was last changed. <c>capabilities</c> cannot express
/// <see cref="GroupItemUpdate.OwnItems"/>, which has no portal equivalent and
/// publishes as no capability — an understatement rather than a false claim, and the
/// server goes on honouring it. <c>tags</c> is empty because our groups carry none.
/// And every portal field this server does not hold is left out rather than
/// defaulted, because a defaulted field is a claim.
/// </para>
/// <para>
/// <b>What it does not publish is the other half of ADR-036 §4g.</b> Seeing that a
/// group exists is not reading what is in it, so there is no member list, no item
/// list and no count of either here — a caller whose standing is
/// <see cref="GroupStanding.Outside"/> reached the group through its
/// organisation-wide visibility and learns only that it exists.
/// </para>
/// </remarks>
internal static class PortalGroup
{
    /// <summary>The portal group for one of ours.</summary>
    /// <param name="group">The group, as the directory returned it.</param>
    /// <param name="orgId">This portal's id.</param>
    /// <param name="username">Who is asking, for the membership block.</param>
    /// <returns>
    /// The portal group, as an anonymous object so <see cref="PortalQuery"/> filters
    /// the same object that is serialised.
    /// </returns>
    public static object Of(GroupSummary group, string orgId, string username)
    {
        ArgumentNullException.ThrowIfNull(group);

        // <b>Null when it is not known, rather than a date that is not true.</b> A
        // group written before the column existed has no creation time, and epoch
        // zero would put it in 1970 for anything that sorts by it.
        long? created = group.CreatedAt == default
            ? null
            : group.CreatedAt.ToUnixTimeMilliseconds();

        return new
        {
            // The group's own id, spelled the way portal ids are — the same rule
            // PortalEndpoints.ItemId follows, and for the same reason: a GUID in
            // "N" format is exactly the 32 hexadecimal characters an Esri id is, so
            // nothing new is minted and there is nothing to keep in step.
            id = group.Id.ToString("N"),

            // <b>A portal group has one name and ours has two.</b> `name` is unique
            // and is what the admin API addresses; `title` is a label. The title is
            // published when there is one because that is the field a person reads,
            // and the name stands in when there is not.
            title = group.Title ?? group.Name,

            // The product's name when the owning principal is gone, which is what an
            // item does when nobody owns it.
            owner = group.Owner ?? "graticula",
            description = group.Description,
            snippet = group.Summary,

            // Ours carry none. Empty rather than absent: a portal group always has
            // the field, and a client that reads tags reads a list.
            tags = Array.Empty<string>(),
            thumbnail = (string?)null,
            created,
            modified = created,

            // <b>There is no `public`, and that is a fact rather than a gap.</b>
            // Q-119 removed the *anybody, including anonymous* visibility on
            // 2026-08-25 because nothing honoured it.
            access = Visibility(group.Visibility),
            isInvitationOnly = group.JoinPolicy == GroupJoinPolicy.Invitation,
            autoJoin = group.JoinPolicy == GroupJoinPolicy.Self,

            // The reference's word for *only the owner and managers may put things
            // in*, which is what GroupContribute.Managers means.
            isViewOnly = group.Contribute == GroupContribute.Managers,
            hiddenMembers = group.MemberList == GroupMemberList.Managers,
            leavingDisallowed = !group.MembersMayLeave,
            @protected = group.DeleteLocked,

            // <b>The shared-update flag, which is all-or-nothing there and is not
            // here.</b> `updateitemcontrol` is the reference's name for ADR-036
            // §4a-i's *allItems*. `ownItems` has no equivalent, so it publishes as no
            // capability — an understatement rather than a false claim, and the
            // server goes on honouring it.
            capabilities = group.ItemUpdate == GroupItemUpdate.AllItems
                ? new[] { "updateitemcontrol" }
                : Array.Empty<string>(),
            orgId,

            // Where this caller stands, which is what a client uses to decide
            // whether to offer *leave* or *join*.
            userMembership = new
            {
                username,
                memberType = MemberType(group.Standing),
                applications = 0,
            },
        };
    }

    /// <summary>
    /// A group's visibility, in the word a portal client uses for it.
    /// </summary>
    /// <remarks>
    /// <b>No catch-all</b>, for the reason <c>PortalEndpoints.Access</c> gives: a
    /// visibility this does not know must be a build-time surprise rather than a
    /// run-time downgrade, which is what D-74 was.
    /// </remarks>
    /// <param name="visibility">Who may discover the group.</param>
    /// <returns>The portal access level.</returns>
    private static string Visibility(GroupVisibility visibility) => visibility switch
    {
        GroupVisibility.Members => "private",
        GroupVisibility.Organization => "org",
        _ => throw new ArgumentOutOfRangeException(
            nameof(visibility),
            visibility,
            "This group visibility has no portal access level. Adding one means deciding what a "
            + "portal client should be told about it, not defaulting it to private."),
    };

    /// <summary>
    /// Where a principal stands in a group, in the word a portal client uses for it.
    /// </summary>
    /// <remarks>
    /// <b>An exact map, which is worth saying because most of this file's are not.</b>
    /// The reference's four member types are the four standings ADR-036 §3 already
    /// distinguishes; its <c>admin</c> is our manager, and the difference between a
    /// manager and an owner is the difference between delegating work and delegating
    /// control in both models.
    /// </remarks>
    /// <param name="standing">Where the caller stands.</param>
    /// <returns>The portal member type.</returns>
    private static string MemberType(GroupStanding standing) => standing switch
    {
        GroupStanding.Owner => "owner",
        GroupStanding.Manager => "admin",
        GroupStanding.Member => "member",
        GroupStanding.Outside => "none",
        _ => throw new ArgumentOutOfRangeException(
            nameof(standing),
            standing,
            "This group standing has no portal member type. Adding one means deciding what a "
            + "portal client should be told about it, not defaulting it to none."),
    };
}
