using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Portal group writes — <c>community/createGroup</c> and <c>community/groups/{id}/update</c>, <c>delete</c>,
/// <c>addUsers</c>, <c>removeUsers</c>, <c>join</c> and <c>leave</c> — each a door onto Studio's own act (ADR-256).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found 2026-11-01, sharing a layer to a group from Pro at a second organisation (ADR-214 condition 2).</b> The
/// portal listed groups and shared into them but could not make one: <c>createGroup</c> answered 404, so the Python
/// API's <c>gis.groups.create</c> and any script that sets up a group before sharing stopped there, and the only door
/// was <c>/admin/groups</c>, which a token from <c>generateToken</c> does not open (ADR-015 §4). The ArcGIS veteran's
/// review of the same night put membership next: *make the group, add the users, share* breaks at line two.
/// </para>
/// <para>
/// <b>Opened by owner decision 2026-11-02, with Studio's privilege (Q-224).</b> ADR-040 §4a kept portal groups
/// read-only because a second write path with its own authorization would let two surfaces disagree. None is written
/// here: every door below parses ArcGIS's parameters, calls the act in <c>AdminEndpoints.GroupActs</c> that
/// <c>/admin/groups</c> calls — the same privilege, the same store call, the same audit row — and words the answer the
/// way ArcGIS's REST API documents it.
/// </para>
/// <para>
/// <b>Our group has a name and a title; a portal group has only a title and an id.</b> The name is made from the title
/// — lower case, letters, digits and hyphens — with a number after it when another group holds it, and the title is
/// kept as sent. A group is addressed here by its id, the 32 hex characters <see cref="PortalGroup"/> publishes, and
/// found among the groups the caller can see. <c>tags</c>, <c>sortField</c>, <c>sortOrder</c> and a thumbnail are not
/// held and are passed over, as <see cref="PortalGroup"/> says it publishes none of them.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private const string Door = "portal";

    private static void MapGroupWrites(WebApplication app)
    {
        app.MapPost($"{Path}/community/createGroup", CreateGroupAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

        string group = $"{Path}/community/groups/{{id:length(32)}}";

        app.MapPost($"{group}/update", UpdateGroupAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{group}/delete", DeleteGroupAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{group}/addUsers", AddGroupUsersAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{group}/removeUsers", RemoveGroupUsersAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{group}/join", JoinGroupAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{group}/leave", LeaveGroupAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    /// <summary>Portal's <c>createGroup</c>: Studio's create, then Studio's settings, as one request.</summary>
    private static async Task<IResult> CreateGroupAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to make a group from another site.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        IFormCollection form = await FormOf(context, cancellation).ConfigureAwait(false);
        string title = First(form, context, "title");

        if (title.Length is 0 or > 250)
        {
            return PortalError(400, "A group's title is between 1 and 250 characters.");
        }

        if (!TryGroupAccess(First(form, context, "access"), out string visibility))
        {
            return PortalError(400, "'access' is private, org or public.");
        }

        string itemUpdate = Capabilities(form, context).Contains("updateitemcontrol", StringComparer.OrdinalIgnoreCase)
            ? "allItems"
            : "none";

        string stem = GroupNameOf(title);
        string name = stem;
        GroupAct act = default;
        Guid id = Guid.Empty;

        for (int n = 2; n < 100; n++)
        {
            (act, id) = await AdminEndpoints.CreateGroupActAsync(
                context, groups, audit, name, title, Optional(First(form, context, "description")), itemUpdate, Door,
                cancellation).ConfigureAwait(false);

            if (act.Change != GroupChange.Exists)
            {
                break;
            }

            name = $"{stem}-{n}";
        }

        if (!act.Done)
        {
            return PortalError(act.Status, act.Refusal!);
        }

        // The settings a portal group is made with, written through Studio's settings act — which is where making a
        // group public asks for the public-sharing privilege (ADR-199).
        GroupSettingsRequest settings = new()
        {
            Title = title,
            Summary = Optional(First(form, context, "snippet")),
            Description = Optional(First(form, context, "description")),
            Visibility = visibility,
            JoinPolicy = JoinPolicyOf(Flag(form, context, "isInvitationOnly"), Flag(form, context, "autoJoin"), null),
            Contribute = Flag(form, context, "isViewOnly") ? "managers" : "members",
            DeleteLocked = Flag(form, context, "protected"),
            MemberList = Flag(form, context, "hiddenMembers") ? "managers" : "members",
            MembersMayLeave = !Flag(form, context, "leavingDisallowed"),
        };

        GroupAct set = await AdminEndpoints.SetGroupSettingsActAsync(context, groups, audit, name, settings, Door, cancellation)
            .ConfigureAwait(false);

        if (!set.Done)
        {
            // Not left half-made: the group goes again, through Studio's delete, and both acts are in the audit log.
            await AdminEndpoints.DeleteGroupActAsync(context, groups, audit, name, Door, cancellation).ConfigureAwait(false);
            return PortalError(set.Status, set.Refusal!);
        }

        GroupSummary made = (await groups.ListAsync(current.Principal.Id, false, cancellation).ConfigureAwait(false))
            .First(g => g.Id == id);

        return Results.Ok(new { success = true, group = PortalGroup.Of(made, PortalId(context), current.Principal.Name) });
    }

    /// <summary>
    /// Portal's <c>update</c>: the fields sent, over the group's present settings, through Studio's settings act.
    /// </summary>
    /// <remarks>
    /// ArcGIS updates only the parameters sent, and clears an empty one only with <c>clearEmptyFields</c>; Studio's
    /// settings are the whole set. So the present settings are read, the sent ones laid over them, and the whole set
    /// written — Studio's own replace, with what was not sent unchanged.
    /// </remarks>
    private static async Task<IResult> UpdateGroupAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change a group from another site.");
        }

        if (await GroupByIdAsync(context, groups, id, cancellation).ConfigureAwait(false) is not { } group)
        {
            return NoGroup(id);
        }

        IFormCollection form = await FormOf(context, cancellation).ConfigureAwait(false);
        bool clear = Flag(form, context, "clearEmptyFields");

        string? Text(string field, string? present) =>
            !Sent(form, context, field) ? present
            : First(form, context, field) is { Length: > 0 } said ? said
            : clear ? null : present;

        string? title = Text("title", group.Title ?? group.Name);

        if (string.IsNullOrEmpty(title) || title.Length > 250)
        {
            return PortalError(400, "A group's title is between 1 and 250 characters.");
        }

        string visibility = PresentVisibility(group.Visibility);

        if (Sent(form, context, "access") && !TryGroupAccess(First(form, context, "access"), out visibility))
        {
            return PortalError(400, "'access' is private, org or public.");
        }

        // What a group lets its members do to items was fixed when it was made (ADR-036 §4c). Sending the same is no
        // change; sending a different one is Studio's refusal. Own items publishes as no capability, so none is the same.
        if (Sent(form, context, "capabilities"))
        {
            bool wants = Capabilities(form, context).Contains("updateitemcontrol", StringComparer.OrdinalIgnoreCase);

            if (wants != (group.ItemUpdate == GroupItemUpdate.AllItems))
            {
                (int status, string message) = AdminEndpoints.GroupChangeRefusal(group.Name, GroupChange.Immutable, null);
                return PortalError(status, message);
            }
        }

        GroupSettingsRequest settings = new()
        {
            Title = title,
            Summary = Text("snippet", group.Summary),
            Description = Text("description", group.Description),
            Visibility = visibility,
            JoinPolicy = JoinPolicyOf(
                Sent(form, context, "isInvitationOnly") ? Flag(form, context, "isInvitationOnly") : null,
                Sent(form, context, "autoJoin") ? Flag(form, context, "autoJoin") : null,
                group.JoinPolicy),
            Contribute = (Sent(form, context, "isViewOnly")
                ? Flag(form, context, "isViewOnly")
                : group.Contribute == GroupContribute.Managers) ? "managers" : "members",
            DeleteLocked = Sent(form, context, "protected") ? Flag(form, context, "protected") : group.DeleteLocked,
            MemberList = (Sent(form, context, "hiddenMembers")
                ? Flag(form, context, "hiddenMembers")
                : group.MemberList == GroupMemberList.Managers) ? "managers" : "members",
            MembersMayLeave = Sent(form, context, "leavingDisallowed")
                ? !Flag(form, context, "leavingDisallowed")
                : group.MembersMayLeave,
        };

        GroupAct act = await AdminEndpoints.SetGroupSettingsActAsync(context, groups, audit, group.Name, settings, Door, cancellation)
            .ConfigureAwait(false);

        return act.Done ? Results.Ok(new { success = true, groupId = id }) : PortalError(act.Status, act.Refusal!);
    }

    /// <summary>Portal's <c>delete</c>: Studio's delete — the owner or an administrator, never while protected.</summary>
    private static Task<IResult> DeleteGroupAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string id, CancellationToken cancellation) =>
        OnGroupAsync(context, groups, id, "delete", name =>
            AdminEndpoints.DeleteGroupActAsync(context, groups, audit, name, Door, cancellation), cancellation);

    /// <summary>Portal's <c>join</c>: Studio's join — a group that lets people add themselves.</summary>
    private static Task<IResult> JoinGroupAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string id, CancellationToken cancellation) =>
        OnGroupAsync(context, groups, id, "join", name =>
            AdminEndpoints.JoinGroupActAsync(context, groups, audit, name, Door, cancellation), cancellation);

    /// <summary>Portal's <c>leave</c>: Studio's leave — not the owner, and not from an administrative group.</summary>
    private static Task<IResult> LeaveGroupAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string id, CancellationToken cancellation) =>
        OnGroupAsync(context, groups, id, "leave", name =>
            AdminEndpoints.LeaveGroupActAsync(context, groups, audit, name, Door, cancellation), cancellation);

    /// <summary>
    /// Portal's <c>addUsers</c>: Studio's add-member act once for each name — <c>users</c> as members and <c>admins</c>
    /// as managers — answered as ArcGIS does, with the names that were not added.
    /// </summary>
    private static async Task<IResult> AddGroupUsersAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change a group from another site.");
        }

        if (await GroupByIdAsync(context, groups, id, cancellation).ConfigureAwait(false) is not { } group)
        {
            return NoGroup(id);
        }

        IFormCollection form = await FormOf(context, cancellation).ConfigureAwait(false);

        // Somebody already in the group keeps their standing when named in `users`: ArcGIS adds, and does not demote a
        // manager or make the owner a member of their own group (INFERRED — its documentation does not say).
        Dictionary<string, GroupStanding> present = (await groups.MembersAsync(group.Name, cancellation).ConfigureAwait(false))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(m => m.Key, m => m.Max(x => x.Standing), StringComparer.OrdinalIgnoreCase);

        List<string> notAdded = [];

        foreach ((string member, bool manager) in Names(form, context, "users").Select(u => (u, false))
                     .Concat(Names(form, context, "admins").Select(a => (a, true))))
        {
            if (present.TryGetValue(member, out GroupStanding standing)
                && (standing == GroupStanding.Owner || (!manager && standing == GroupStanding.Manager)))
            {
                continue;
            }

            GroupAct act = await AdminEndpoints.SetGroupMemberActAsync(
                context, groups, audit, group.Name, member, manager, Door, cancellation).ConfigureAwait(false);

            if (!act.Done)
            {
                // A refusal about the caller or the group is the whole request's; one about the name is that name's.
                if (act.Change != GroupChange.NoSuchTarget)
                {
                    return PortalError(act.Status, act.Refusal!);
                }

                notAdded.Add(member);
            }
        }

        return Results.Ok(new { notAdded });
    }

    /// <summary>Portal's <c>removeUsers</c>: Studio's remove-member act once for each name in <c>users</c>.</summary>
    private static async Task<IResult> RemoveGroupUsersAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change a group from another site.");
        }

        if (await GroupByIdAsync(context, groups, id, cancellation).ConfigureAwait(false) is not { } group)
        {
            return NoGroup(id);
        }

        IFormCollection form = await FormOf(context, cancellation).ConfigureAwait(false);
        List<string> notRemoved = [];

        foreach (string member in Names(form, context, "users").Concat(Names(form, context, "admins")))
        {
            GroupAct act = await AdminEndpoints.RemoveGroupMemberActAsync(
                context, groups, audit, group.Name, member, Door, cancellation).ConfigureAwait(false);

            if (!act.Done)
            {
                if (act.Change is GroupChange.NotYours or GroupChange.Absent)
                {
                    return PortalError(act.Status, act.Refusal!);
                }

                notRemoved.Add(member);
            }
        }

        return Results.Ok(new { notRemoved });
    }

    /// <summary>A door that finds the group by id, runs one of Studio's acts on it, and answers ArcGIS's success.</summary>
    private static async Task<IResult> OnGroupAsync(
        HttpContext context, IGroupDirectory groups, string id, string verb, Func<string, Task<GroupAct>> act,
        CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, $"Send a token to {verb} a group from another site.");
        }

        if (await GroupByIdAsync(context, groups, id, cancellation).ConfigureAwait(false) is not { } group)
        {
            return NoGroup(id);
        }

        GroupAct done = await act(group.Name).ConfigureAwait(false);

        return done.Done ? Results.Ok(new { success = true, groupId = id }) : PortalError(done.Status, done.Refusal!);
    }

    /// <summary>
    /// The group with this portal id among those the caller can see — every group for an administrator, as
    /// <c>/admin/groups</c> lists them, since an administrator may change a group they are not in.
    /// </summary>
    private static async Task<GroupSummary?> GroupByIdAsync(
        HttpContext context, IGroupDirectory groups, string id, CancellationToken cancellation)
    {
        if (!Guid.TryParseExact(id, "N", out Guid wanted))
        {
            return null;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return null;
        }

        IReadOnlyList<GroupSummary> seen = await groups.ListAsync(
            current.Principal.Id, current.Authorization.Allows(Privilege.AdminManageAllContent), cancellation)
            .ConfigureAwait(false);

        return seen.FirstOrDefault(g => g.Id == wanted);
    }

    // The same answer for a group that is not there and one the caller cannot see, as the group read answers.
    private static IResult NoGroup(string id) =>
        PortalError(400, $"Group '{id}' does not exist or is inaccessible.");

    private static async Task<IFormCollection> FormOf(HttpContext context, CancellationToken cancellation) =>
        context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

    private static bool Sent(IFormCollection form, HttpContext context, string name) =>
        form.ContainsKey(name) || context.Request.Query.ContainsKey(name);

    private static IEnumerable<string> Names(IFormCollection form, HttpContext context, string name) =>
        First(form, context, name)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string[] Capabilities(IFormCollection form, HttpContext context) =>
        First(form, context, "capabilities").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// ArcGIS's two join flags as Studio's join policy, over the present one when a flag is not sent.
    /// </summary>
    /// <remarks>
    /// <b>Neither flag set is ArcGIS's *ask and be approved*, which this server does not build</b> — Studio refuses
    /// <c>request</c> rather than store a policy nothing honours (D-67). A portal client asking for it is given the
    /// nearer closed policy, invitation, and the group it is answered with says so (<c>isInvitationOnly: true</c>).
    /// The ArcGIS API for Python sends both flags false by default, so refusing would have refused every
    /// <c>gis.groups.create</c> that did not name a policy. INFERRED (ADR-256 §5.3).
    /// </remarks>
    private static string JoinPolicyOf(bool? invitationOnly, bool? autoJoin, GroupJoinPolicy? present)
    {
        if (invitationOnly is true)
        {
            return "invitation";
        }

        if (autoJoin is true)
        {
            return "self";
        }

        // autoJoin false closes a self-joined group; isInvitationOnly false alone leaves the present policy, since the
        // request policy it would open is the one not built.
        return autoJoin is not false && present == GroupJoinPolicy.Self ? "self" : "invitation";
    }

    // enum-default-is-deliberate: private — a group's visibility, not an item's sharing scope: ArcGIS makes a group private, org or public, and an absent access is private, as it is there.
    private static bool TryGroupAccess(string access, out string visibility)
    {
        visibility = access.ToLowerInvariant() switch
        {
            "" or "private" => "members",
            "org" => "organization",
            "public" => "public",
            _ => string.Empty,
        };

        return visibility.Length > 0;
    }

    // enum-default-is-deliberate: private — Studio's word for each visibility, to be written back unchanged by an update that did not send access.
    private static string PresentVisibility(GroupVisibility visibility) => visibility switch
    {
        GroupVisibility.Organization => "organization",
        GroupVisibility.Public => "public",
        _ => "members",
    };

    /// <summary>A group's name made from its title: lower case, letters, digits and hyphens, at most 60.</summary>
    internal static string GroupNameOf(string title)
    {
        StringBuilder name = new();

        // Accents come off rather than the letter going with them — *Ölçüm* is olcum, not l-m. A table rather than
        // Unicode decomposition, because the tests run with invariant globalization and the server does not, and the
        // two would make different names from one title.
        foreach (char c in title.Trim().ToLowerInvariant())
        {
            int folded = Accented.IndexOf(c, StringComparison.Ordinal);

            if (char.IsAsciiLetterOrDigit(c))
            {
                name.Append(c);
            }
            else if (folded >= 0)
            {
                name.Append(Plain[folded]);
            }
            else if (name.Length > 0 && name[^1] != '-')
            {
                name.Append('-');
            }
        }

        string made = name.ToString().Trim('-');
        made = made.Length > 60 ? made[..60].TrimEnd('-') : made;

        return made.Length == 0 ? "group" : made;
    }

    // Each accented letter, lower case, and the plain one at the same place in Plain.
    private const string Accented = "çğıöşüâîûáàäåãéèêëíìïóòôõøúùñý";

    private const string Plain = "cgiosuaiuaaaaaeeeeiiiooooouuny";

    private static bool Flag(IFormCollection form, HttpContext context, string name) =>
        bool.TryParse(First(form, context, name), out bool value) && value;

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}
