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
}
