using System;
using System.Collections.Generic;

namespace Graticula.Platform.Identity;

/// <summary>
/// Whether a caller may read an item, and why.
/// </summary>
/// <remarks>
/// <para>
/// ADR-018 §3b. This is the whole of read authorization: there is no read
/// privilege, so the answer comes from the item's owner and scope together with
/// who is asking.
/// </para>
/// <para>
/// <b>A separate type rather than a method on the layer</b>, because the same
/// rule will govern styles, hosted items and anything else that gets an owner —
/// and because the one place this decision is made should be findable by name.
/// </para>
/// </remarks>
public static class LayerAccess
{
    /// <summary>How a read was allowed, for audit and for diagnosis.</summary>
    public enum Reason
    {
        /// <summary>Not allowed.</summary>
        Denied,

        /// <summary>The item is public.</summary>
        Public,

        /// <summary>The item is shared with the organisation and the caller is authenticated.</summary>
        Organization,

        /// <summary>The caller owns it.</summary>
        Owner,

        /// <summary>
        /// The caller holds <see cref="Privilege.AdminViewAllContent"/>.
        /// </summary>
        /// <remarks>
        /// Distinguished from the others because ADR-018 condition 3 requires it
        /// to be auditable: an administrator reading a private layer is
        /// legitimate and must leave a record, or the sharing model is
        /// decorative. A single boolean answer could not support that.
        /// </remarks>
        AdministrativeOverride,

        /// <summary>
        /// Shared with a group this caller belongs to — ADR-036 §4a.
        /// </summary>
        /// <remarks>
        /// <b>Its own reason, for the same argument that separates owner from organisation:</b> the
        /// audit trail should say *they read it because they are in the planning group*, not *because
        /// it was shared with somebody*. When the question later becomes *why could they see this*,
        /// the group is the answer somebody is looking for.
        /// </remarks>
        Group,
    }

    /// <summary>Decides whether a caller may read an item.</summary>
    /// <param name="scope">The item's sharing scope.</param>
    /// <param name="owner">The item's owner, or null if it has none.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="authorization">What they may do.</param>
    /// <param name="itemGroups">
    /// Which groups this item is shared with — ADR-036. Empty for anything that is not
    /// <c>group</c>-scoped, and unread unless the scope is.
    /// </param>
    /// <remarks>
    /// <b>The item's groups are a parameter and the caller's are on the authorization.</b> One is a
    /// property of the thing being read and changes when somebody shares it; the other is a property
    /// of the reader and is resolved once per request. Putting both in one place would make a
    /// per-request value carry a per-item one.
    /// </remarks>
    public static Reason Evaluate(
        SharingScope scope,
        Guid? owner,
        Principal caller,
        Authorization authorization,
        IReadOnlyCollection<Guid>? itemGroups = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(authorization);

        if (scope == SharingScope.Public)
        {
            return Reason.Public;
        }

        // Ownership is checked before the organisation scope so that an owner
        // reading their own private item is reported as the owner, not as a
        // member of the organisation. The distinction matters only in the audit
        // trail, which is where it matters.
        if (owner is { } id && !caller.IsAnonymous && id == caller.Id)
        {
            return Reason.Owner;
        }

        if (scope == SharingScope.Organization && !caller.IsAnonymous)
        {
            return Reason.Organization;
        }

        // <b>Group, and it is never open to an anonymous caller.</b> A group is a set of members;
        // somebody who has not signed in is in no group, so the intersection is empty and the check
        // is a formality — stated anyway, because *"the empty set intersects nothing"* is the kind of
        // reasoning that stops being true when somebody adds a default group.
        if (scope == SharingScope.Group
            && !caller.IsAnonymous
            && itemGroups is { Count: > 0 })
        {
            foreach (Guid group in itemGroups)
            {
                if (authorization.Groups.Contains(group))
                {
                    return Reason.Group;
                }
            }
        }

        // Last, so that an administrator reading something they could have read
        // anyway is not recorded as having used the override.
        if (authorization.Allows(Privilege.AdminViewAllContent))
        {
            return Reason.AdministrativeOverride;
        }

        return Reason.Denied;
    }

    /// <summary>Whether a reason permits the read.</summary>
    public static bool IsAllowed(this Reason reason) => reason != Reason.Denied;

    /// <summary>Why a caller may write to a layer, or that it may not.</summary>
    public enum EditRight
    {
        /// <summary>It may not.</summary>
        None,

        /// <summary>It owns the layer and holds <c>features:edit</c>.</summary>
        Owner,

        /// <summary>A shared-update group the layer is shared with — ADR-036 §4a.</summary>
        Group,

        /// <summary><c>admin:manageAllContent</c>, which reaches every layer, with an owner or without.</summary>
        Administrator,
    }

    /// <summary>
    /// Whether this caller may write to this layer at all, and on what grounds — owner decision
    /// 2026-09-16, [ADR-075](../../../docs/adr/ADR-075-a-layer-is-edited-by-its-owner.md).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's words: <i>"herkese açık bile olsa katman, bir sahibi olmalı. Düzenleme hakkı
    /// ona ve admin olan kullanıcılara ait. Adminler her şeyi düzenleyebilir."</i></b> Until this,
    /// writing was a privilege: a member whose role held <c>features:edit</c> could add to every
    /// layer they could read, and on a layer that records its creators could change their own
    /// features in it — so a public layer was writable by every member with an editor's role, and
    /// the person answerable for it had no say. Asked whether that closes the owner's own
    /// delegation too, the owner kept it: a group the owner shares the layer with, and marks
    /// <em>shared update</em>, still edits.
    /// </para>
    /// <para>
    /// <b>The owner still needs <c>features:edit</c>.</b> Ownership decides <em>which</em> layers;
    /// the role still decides <em>whether this account edits at all</em>, so a publisher whose role
    /// was reduced to viewer stops writing to what they published rather than keeping a right the
    /// organisation took away.
    /// </para>
    /// <para>
    /// <b>A layer with no owner is edited by administrators alone</b> — owner decision, same day.
    /// Such layers predate ownership; nothing assigns one silently, because an owner nobody chose is
    /// a fact the audit trail would then repeat (<c>PublishedLayer.Owner</c>).
    /// </para>
    /// <para>
    /// <b>Anonymous never writes</b>, by construction: it owns nothing, belongs to no group and
    /// holds no privilege. Stated because a public layer is exactly where somebody will expect it to.
    /// </para>
    /// </remarks>
    /// <param name="owner">The layer's owner, or null.</param>
    /// <param name="scope">The layer's sharing scope.</param>
    /// <param name="itemGroups">The groups it is shared with.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="authorization">What they hold.</param>
    /// <returns>The ground the write stands on, or <see cref="EditRight.None"/>.</returns>
    public static EditRight MayEdit(
        Guid? owner,
        SharingScope scope,
        IReadOnlyCollection<Guid>? itemGroups,
        Principal caller,
        Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(authorization);

        if (caller.IsAnonymous)
        {
            return EditRight.None;
        }

        if (authorization.Allows(Privilege.AdminManageAllContent))
        {
            return EditRight.Administrator;
        }

        if (owner is { } id && id == caller.Id && authorization.Allows(Privilege.FeaturesEdit))
        {
            return EditRight.Owner;
        }

        return GroupConfersEditing(scope, authorization, itemGroups)
            ? EditRight.Group
            : EditRight.None;
    }

    /// <summary>
    /// Whether this caller may change what an item <em>is</em> — its sharing, its fields, its
    /// symbology, its style, its definition, or empty it — rather than the features in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its owner or an administrator, and not a shared-update group</b> — owner decision
    /// 2026-09-16, [ADR-075](../../../docs/adr/ADR-075-a-layer-is-edited-by-its-owner.md). A group
    /// the owner shares a layer with for editing writes its features (<see cref="MayEdit"/>);
    /// changing the item itself stays with the person answerable for it, which is ArcGIS's line too.
    /// </para>
    /// <para>
    /// <b>No privilege is asked for here.</b> The endpoint still asks for its own —
    /// <c>content:publishFeatures</c> to restyle, <c>sharing:shareToPublic</c> to make public — and
    /// this is the second half those checks never had: until this, a publisher could restyle, empty
    /// or make public any layer on the server by naming it, and a member with
    /// <c>sharing:shareToOrganization</c> could open anybody's private layer to the whole
    /// organisation.
    /// </para>
    /// </remarks>
    /// <param name="owner">The item's owner, or null.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="authorization">What they hold.</param>
    /// <returns>Whether they may.</returns>
    public static bool MayManage(Guid? owner, Principal caller, Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(authorization);

        return !caller.IsAnonymous
            && (authorization.Allows(Privilege.AdminManageAllContent)
                || (owner is { } id && id == caller.Id));
    }

    /// <summary>
    /// Whether a group the caller belongs to confers editing what is shared with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared update — owner decision 2026-08-25,
    /// [ADR-036](../../../docs/adr/ADR-036-groups.md) §4a as amended.</b> §4a said a group confers
    /// reading and that editing through one was not built, *because the owner's requirement did not
    /// ask for it*. It asks now. §4b had already decided where the capability would live — on the
    /// group rather than on each share — so this is the addition that decision was written to
    /// allow, rather than a redesign.
    /// </para>
    /// <para>
    /// <b>What was actually wrong before this.</b> <c>item_update</c> was stored, was editable
    /// through the admin API, and was shown in the listing, and no code path consulted it. A
    /// setting the server keeps and does not honour is [D-67](../../../docs/architecture-debt.md),
    /// and it is the same shape the removed <c>public</c> visibility had: the screen promised
    /// something the server never did.
    /// </para>
    /// <para>
    /// <b>This widens writing and does not touch reading.</b> ADR-018 §3b's invariant is that
    /// <em>sharing governs reading</em>, and it still does: <see cref="Evaluate"/> is unchanged,
    /// and nothing here can make an item readable that was not. What moves is the other half —
    /// editing was <em>only</em> a privilege, and for a layer shared with a group whose members
    /// share update rights, it is now a privilege <em>or</em> that membership. Saying which half
    /// moved is the point of writing it this way: a reader who remembers §3b should be able to see
    /// immediately that their memory is still correct.
    /// </para>
    /// <para>
    /// <b>Reading is required first, and the ordering is not decorative.</b> A caller who cannot
    /// see the layer never reaches an edit endpoint — <c>ServiceLookup</c> answers 404 before the
    /// privilege check — so this can only ever widen what somebody already reads. It is asserted
    /// rather than assumed: <c>EditableGroups</c> is a subset of <c>Groups</c> by construction, so
    /// a group here is a group that passed <see cref="Evaluate"/>.
    /// </para>
    /// <para>
    /// <b>Scoped to the item, not to the server.</b> This grants nothing globally. It answers *may
    /// this caller edit <em>this</em> layer*, and a caller with no privileges who belongs to one
    /// sharing-update group can edit exactly what that group holds.
    /// </para>
    /// </remarks>
    /// <param name="scope">The item's sharing scope.</param>
    /// <param name="authorization">The caller's grants.</param>
    /// <param name="itemGroups">Which groups the item is shared with.</param>
    /// <returns>Whether group membership alone permits editing this item.</returns>
    public static bool GroupConfersEditing(
        SharingScope scope,
        Authorization authorization,
        IReadOnlyCollection<Guid>? itemGroups)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        if (scope != SharingScope.Group || itemGroups is not { Count: > 0 })
        {
            return false;
        }

        foreach (Guid group in itemGroups)
        {
            if (authorization.EditableGroups.Contains(group))
            {
                return true;
            }
        }

        return false;
    }
}
