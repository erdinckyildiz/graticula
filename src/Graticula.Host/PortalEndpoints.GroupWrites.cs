using System;
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
/// Portal's <c>community/createGroup</c> — a group made through the door every ArcGIS client knows, with the settings
/// a portal group carries mapped onto ADR-036's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found 2026-11-01, sharing a layer to a group from Pro at a second organisation (ADR-214 condition 2).</b> The
/// portal listed groups and shared into them but could not make one: <c>createGroup</c> answered 404, so the Python
/// API's <c>gis.groups.create</c> and any script that sets up a group before sharing stopped there, and the only door
/// was <c>/admin/groups</c>, which a token from <c>generateToken</c> does not open (ADR-015 §4).
/// </para>
/// <para>
/// <b>Our group has a name and a title; a portal group has only a title.</b> The name is made from the title — lower
/// case, letters, digits and hyphens — with a number after it when another group holds it, and the title is kept as
/// sent. Every setting a portal group carries that ADR-036 holds is honoured; <c>tags</c>, <c>sortField</c>,
/// <c>sortOrder</c> and a thumbnail are not held and are passed over, as <see cref="PortalGroup"/> says it publishes
/// none of them.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static void MapGroupWrites(WebApplication app)
    {
        app.MapPost($"{Path}/community/createGroup", CreateGroupAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    /// <summary>Portal's <c>createGroup</c>: the caller owns what it makes.</summary>
    private static async Task<IResult> CreateGroupAsync(
        HttpContext context, IGroupDirectory groups, IAuditLog audit, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to make a group from another site.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return PortalError(403, "You do not have permissions to access this resource or perform this operation.");
        }

        if (!current.Authorization.Allows(Privilege.GroupsCreate))
        {
            return PortalError(403, "Your role does not let you create groups ('groups:create').");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

        string title = First(form, context, "title");

        if (title.Length is 0 or > 250)
        {
            return PortalError(400, "A group's title is between 1 and 250 characters.");
        }

        if (!TryGroupAccess(First(form, context, "access"), out GroupVisibility visibility))
        {
            return PortalError(400, "'access' is private, org or public.");
        }

        GroupItemUpdate itemUpdate = First(form, context, "capabilities")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("updateitemcontrol", StringComparer.OrdinalIgnoreCase)
                ? GroupItemUpdate.AllItems
                : GroupItemUpdate.None;

        string? description = Optional(First(form, context, "description"));
        string? snippet = Optional(First(form, context, "snippet"));
        string stem = GroupNameOf(title);
        (GroupChange outcome, Guid id) = (GroupChange.Exists, Guid.Empty);
        string name = stem;

        for (int n = 2; outcome == GroupChange.Exists && n < 100; n++)
        {
            (outcome, id) = await groups.CreateAsync(current.Principal.Id, name, title, description, itemUpdate, cancellation)
                .ConfigureAwait(false);

            if (outcome == GroupChange.Exists)
            {
                name = $"{stem}-{n}";
            }
        }

        if (outcome != GroupChange.Done)
        {
            return PortalError(400, $"The group '{title}' could not be made ({outcome}).");
        }

        // The settings a portal group is made with, written as the whole set that SetSettingsAsync asks for.
        GroupJoinPolicy join = Flag(form, context, "isInvitationOnly") ? GroupJoinPolicy.Invitation
            : Flag(form, context, "autoJoin") ? GroupJoinPolicy.Self
            : GroupJoinPolicy.Request;
        GroupChange set = await groups.SetSettingsAsync(
            current.Principal.Id,
            false,
            name,
            title,
            snippet,
            description,
            visibility,
            join,
            Flag(form, context, "isViewOnly") ? GroupContribute.Managers : GroupContribute.Members,
            Flag(form, context, "protected"),
            Flag(form, context, "hiddenMembers") ? GroupMemberList.Managers : GroupMemberList.Members,
            !Flag(form, context, "leavingDisallowed"),
            cancellation).ConfigureAwait(false);

        if (set != GroupChange.Done)
        {
            await groups.RemoveAsync(current.Principal.Id, false, name, cancellation).ConfigureAwait(false);
            return PortalError(400, $"The group '{title}' could not be given the settings asked for ({set}).");
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "group.create", name, AdminEndpoints.Detail(new { title, door = "portal" }),
            succeeded: true, cancellation).ConfigureAwait(false);

        GroupSummary made = (await groups.ListAsync(current.Principal.Id, false, cancellation).ConfigureAwait(false))
            .First(g => g.Id == id);

        return Results.Ok(new { success = true, group = PortalGroup.Of(made, PortalId(context), current.Principal.Name) });
    }

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

    // enum-default-is-deliberate: private — a group's visibility, not an item's sharing scope: ArcGIS makes a group private, org or public, and an absent access is private, as it is there.
    private static bool TryGroupAccess(string access, out GroupVisibility visibility)
    {
        visibility = access.ToLowerInvariant() switch
        {
            "" or "private" => GroupVisibility.Members,
            "org" => GroupVisibility.Organization,
            "public" => GroupVisibility.Public,
            _ => (GroupVisibility)(-1),
        };

        return Enum.IsDefined(visibility);
    }

    private static bool Flag(IFormCollection form, HttpContext context, string name) =>
        bool.TryParse(First(form, context, name), out bool value) && value;

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}
