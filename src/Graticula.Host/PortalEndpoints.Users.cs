using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The portal's users — searched, listed, invited, given a role, disabled, enabled and deleted — as the ArcGIS API for
/// Python's <c>gis.users</c> reaches them, through Studio's own member directory and handlers (ADR-262, the ArcGIS
/// veteran's review of v1.0.499, #3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Who sees whom.</b> Anybody not signed in sees nobody, as before ([Q-127](../../docs/open-questions.md): this surface
/// does not publish usernames to whoever can reach it). A signed-in member sees the organisation's members by their
/// public profile — name and full name — as a Portal member finds a colleague to share with. Whoever may manage members
/// (<c>admin:manageMembers</c>) sees the rest: role, user type, e-mail, whether disabled, last sign-in.
/// </para>
/// <para>
/// <b>Every change is Studio's</b>, run inside the request (<see cref="InnerAsync"/>), so its privilege, its refusals and
/// its audit line are the Organization screen's. An invitation needs a password: this server sends no e-mail, and the
/// password given is one the member must change when they first sign in, as every password an administrator hands out
/// here is.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>ArcGIS's built-in role ids for the two roles it names by id rather than by <c>org_*</c>.</summary>
    private const string ViewerRoleId = "iAAAAAAAAAAAAAAA";

    private static void MapUsers(WebApplication app)
    {
        Discoverable(app, $"{Path}/community/users", SearchUsersAsync).Governed(SharingGovernedExtensions.ByFiltering);
        Discoverable(app, $"{Path}/portals/self/users", OrganizationUsersAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        Discoverable(app, $"{Path}/portals/self/users/search", OrganizationUsersAsync).Governed(SharingGovernedExtensions.ByPrivilege);

        app.MapPost($"{Path}/portals/self/invite", InviteUsersAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{Path}/portals/self/updateuserrole", UpdateUserRoleAsync).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{Path}/community/users/{{username}}/disable", (HttpContext c, string username, CancellationToken t) =>
            DisableUserAsync(c, username, true, t)).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{Path}/community/users/{{username}}/enable", (HttpContext c, string username, CancellationToken t) =>
            DisableUserAsync(c, username, false, t)).Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        app.MapPost($"{Path}/community/users/{{username}}/delete", DeleteUserAsync)
            .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
    }

    /// <summary><c>community/users?q=</c>: the members a signed-in caller may find, by their public profile.</summary>
    private static async Task<IResult> SearchUsersAsync(HttpContext context, IMemberDirectory directory, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        (int start, int num) = UserPage(context);

        if (current.Principal == Principal.Anonymous)
        {
            return Results.Ok(new { query = context.Request.Query["q"].ToString(), total = 0, start, num, nextStart = -1, results = Array.Empty<object>() });
        }

        return await UsersPageAsync(context, directory, start, num, "results",
            full: current.Authorization.Allows(Privilege.AdminManageMembers), cancellation).ConfigureAwait(false);
    }

    /// <summary><c>portals/self/users</c> and its <c>search</c>: every member, in full, for whoever manages members.</summary>
    private static async Task<IResult> OrganizationUsersAsync(HttpContext context, IMemberDirectory directory, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!current.Authorization.Allows(Privilege.AdminManageMembers))
        {
            return PortalError(403, "The organization's users are listed to whoever may manage members.");
        }

        (int start, int num) = UserPage(context);
        return await UsersPageAsync(context, directory, start, num, "users", full: true, cancellation).ConfigureAwait(false);
    }

    /// <summary>The portal's paging: <c>start</c> counts from 1, <c>num</c> is at most 100.</summary>
    private static (int Start, int Num) UserPage(HttpContext context)
    {
        int start = int.TryParse(context.Request.Query["start"], out int s) && s > 0 ? s : 1;
        int num = int.TryParse(context.Request.Query["num"], out int n) ? Math.Clamp(n, 1, 100) : 10;
        return (start, num);
    }

    /// <summary>One page of members as portal users, under the key the asking resource uses.</summary>
    private static async Task<IResult> UsersPageAsync(
        HttpContext context, IMemberDirectory directory, int start, int num, string key, bool full, CancellationToken cancellation)
    {
        string q = context.Request.Query["q"].ToString();
        MemberPage page = await directory.SearchMembersAsync(
            new MemberQuery(Text: UserText(q), Disabled: full ? null : false, Start: start - 1, Count: num), cancellation)
            .ConfigureAwait(false);

        int next = start - 1 + page.Members.Count < page.Total ? start + page.Members.Count : -1;
        object[] users = [.. page.Members.Select(m => PortalUser(context, m, full))];

        return Results.Ok(new Dictionary<string, object?>
        {
            ["query"] = q,
            ["total"] = page.Total,
            ["start"] = start,
            ["num"] = num,
            ["nextStart"] = next,
            [key] = users,
        });
    }

    /// <summary>
    /// What a portal query's text narrows to: a <c>username:</c> or <c>fullname:</c> term's value, or the words as they
    /// came. The organisation is this portal's one, so an <c>orgid:</c> term narrows nothing and is dropped.
    /// </summary>
    private static string? UserText(string q)
    {
        string[] terms = [.. q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("orgid:", StringComparison.OrdinalIgnoreCase) && t != "AND" && t != "*")
            .Select(t => t.Contains(':', StringComparison.Ordinal) ? t[(t.IndexOf(':', StringComparison.Ordinal) + 1)..] : t)
            .Select(t => t.Trim('"', '*'))
            .Where(t => t.Length > 0)];

        return terms.Length == 0 ? null : string.Join(' ', terms);
    }

    /// <summary>A member as a portal user: the public profile, or all of it for whoever manages members.</summary>
    private static object PortalUser(HttpContext context, MemberListing listing, bool full)
    {
        Member member = listing.Member;
        long created = member.CreatedAt.ToUnixTimeMilliseconds();

        if (!full)
        {
            return new
            {
                username = member.Name,
                fullName = member.DisplayName ?? member.Name,
                description = (string?)null,
                created,
                thumbnail = (string?)null,
                orgId = PortalId(context),
                provider = "arcgis",
            };
        }

        (string role, string roleId) = PortalRoleOf(member.Roles);

        return new
        {
            username = member.Name,
            fullName = member.DisplayName ?? member.Name,
            email = listing.Email,
            description = (string?)null,
            created,
            modified = created,
            lastLogin = listing.LastSignIn?.ToUnixTimeMilliseconds() ?? -1,
            thumbnail = (string?)null,
            orgId = PortalId(context),
            provider = "arcgis",
            role,
            roleId,
            userType = "arcgisonly",
            userLicenseTypeId = LicenseTypeIdOf(member.UserType),
            disabled = member.IsDisabled,
            access = "org",
            level = "2",
        };
    }

    /// <summary>The ArcGIS user type id for a member's user type (ADR-284).</summary>
    /// <remarks>
    /// <b><c>editor</c> is ArcGIS's Contributor (<c>editorUT</c>) and <c>fieldworker</c> its Mobile Worker
    /// (<c>fieldWorkerUT</c>)</b>: both edit data and make nothing, and ArcGIS tells them apart by the apps each licence
    /// includes. <b>Unrestricted is a creator</b>: ArcGIS has no <c>unrestrictedUT</c>.
    /// </remarks>
    internal static string LicenseTypeIdOf(string? userType) => userType switch
    {
        UserTypes.Viewer => "viewerUT",
        UserTypes.Editor => "editorUT",
        UserTypes.FieldWorker => "fieldWorkerUT",
        _ => "creatorUT",
    };

    /// <summary>The name ArcGIS gives a user type id.</summary>
    internal static string LicenseTypeNameOf(string id) => id switch
    {
        "viewerUT" => "Viewer",
        "editorUT" => "Contributor",
        "fieldWorkerUT" => "Mobile Worker",
        _ => "Creator",
    };

    /// <summary>
    /// This server's user type for what an ArcGIS client sends as <c>userLicenseType</c> — an id such as
    /// <c>fieldWorkerUT</c>, or the ArcGIS API for Python's word for one (<c>contributor</c>, <c>fieldworker</c>) —
    /// or null when it names none. A professional type is a creator: a creator's licence with desktop apps beside it.
    /// </summary>
    internal static string? UserTypeOfLicense(string? sent) => sent?.Trim().ToLowerInvariant() switch
    {
        "viewerut" or "viewer" => UserTypes.Viewer,
        "editorut" or "editor" or "contributor" => UserTypes.Editor,
        "fieldworkerut" or "fieldworker" or "mobileworker" => UserTypes.FieldWorker,
        "creatorut" or "creator" => UserTypes.Creator,
        { } professional when professional.StartsWith("gisprofessional", StringComparison.Ordinal) => UserTypes.Creator,
        _ => null,
    };

    /// <summary>
    /// The apps a member's licence carries, as <c>userLicenseType</c> lists them and <c>community/self</c>'s
    /// <c>userLicensedItems</c> repeats them: the field apps, which are what a client of this server asks about.
    /// </summary>
    /// <remarks>
    /// <b>Names and item ids measured on a real ArcGIS Enterprise portal on 2026-11-08</b> (read only, owner's
    /// permission): its <c>userLicenseType</c> lists ArcGIS Field Maps as a <c>Mobile Application</c> with the entitlement
    /// <c>fieldmaps</c> in a <c>Field Apps</c> bundle, and <c>community/self?returnUserLicensedItems=true</c> repeats every
    /// entitlement as <c>{itemId, entitlements}</c>. Esri's REST reference shows an older example (<c>Collector for
    /// ArcGIS</c>, <c>collector</c>) that the measured portal no longer lists. <b>No SDK licence level is claimed</b>:
    /// the measured portal lists one (<c>runtimeadvanced</c>), and what that unlocks in an app is Esri's to grant.
    /// </remarks>
    internal static readonly (string ItemId, string Title, string Type, string Entitlement)[] FieldApps =
    [
        ("ff54441c792546379cfd3c8f87d9a0b6", "ArcGIS Workforce", "Web Mapping Application", "workforce"),
        ("7ddebbf72c6f4568a7631c4df3804c1c", "ArcGIS Field Maps", "Mobile Application", "fieldmaps"),
        ("d95d1f5dafcb461d97493a2d37ba9e78", "ArcGIS Survey123", "Web Mapping Application", "survey123"),
        ("9c570c552ba54228bd8aee4ed4a79f2c", "ArcGIS QuickCapture", "Mobile Application", "QuickCapture"),
    ];

    /// <summary><c>community/self</c>'s <c>userLicensedItems</c>, asked for with <c>returnUserLicensedItems=true</c>.</summary>
    internal static object[] LicensedItems() =>
        [.. FieldApps.Select(a => (object)new { itemId = a.ItemId, entitlements = new[] { a.Entitlement } })];

    /// <summary><c>community/users/{u}/userLicenseType</c> for the caller, in the shape Esri's REST reference documents.</summary>
    private static object LicenseType(HttpContext context)
    {
        string id = LicenseTypeIdOf(context.Features.Get<RequestPrincipal>()!.Authorization.UserType);
        string name = LicenseTypeNameOf(id);

        return new
        {
            id,
            name,
            apps = new object[]
            {
                new
                {
                    id = "fieldApps",
                    title = "Field Apps",
                    type = "App Bundle",
                    isAppBundle = true,
                    apps = FieldApps.Select(a => new { id = a.ItemId, title = a.Title, type = a.Type, entitlements = new[] { a.Entitlement } }).ToArray(),
                },
            },
            extensions = Array.Empty<object>(),
        };
    }

    /// <summary>The portal's role and role id for a member's roles: built-in names are said as ArcGIS says them.</summary>
    private static (string Role, string RoleId) PortalRoleOf(IReadOnlyList<string> roles)
    {
        if (roles.Contains(Roles.Administrator, StringComparer.OrdinalIgnoreCase))
        {
            return ("org_admin", "org_admin");
        }

        if (roles.Contains(Roles.Publisher, StringComparer.OrdinalIgnoreCase))
        {
            return ("org_publisher", "org_publisher");
        }

        string? custom = roles.FirstOrDefault(r => !string.Equals(r, Roles.User, StringComparison.OrdinalIgnoreCase));
        return custom is null ? ("org_user", "org_user")
            : string.Equals(custom, Roles.Viewer, StringComparison.OrdinalIgnoreCase) ? ("org_user", ViewerRoleId)
            : ("org_user", custom);
    }

    /// <summary>This server's role for a portal role or role id, or the name as given for one of its own.</summary>
    private static string OurRoleOf(string role) => role switch
    {
        "org_admin" or "account_admin" => Roles.Administrator,
        "org_publisher" or "account_publisher" => Roles.Publisher,
        "org_user" or "account_user" => Roles.User,
        ViewerRoleId => Roles.Viewer,
        _ => role,
    };

    /// <summary>
    /// <c>portals/self/invite</c>: each invitation is Studio's New member, then its password — which the member changes
    /// when they first sign in. One that cannot be made is named in <c>notInvited</c>, and the rest are made.
    /// </summary>
    private static async Task<IResult> InviteUsersAsync(HttpContext context, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!current.Authorization.Allows(Privilege.AdminManageMembers))
        {
            return PortalError(403, "Inviting users is for whoever may manage members.");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        List<JsonElement> invitations = [];

        try
        {
            using JsonDocument list = JsonDocument.Parse(form["invitationList"].ToString() is { Length: > 0 } text ? text : "{}");
            if (list.RootElement.TryGetProperty("invitations", out JsonElement each) && each.ValueKind == JsonValueKind.Array)
            {
                invitations.AddRange(each.EnumerateArray().Select(e => e.Clone()));
            }
        }
        catch (JsonException)
        {
            return PortalError(400, "invitationList is not JSON.");
        }

        if (invitations.Count == 0)
        {
            return PortalError(400, "invitationList names no invitations.");
        }

        IServiceProvider services = context.RequestServices;
        IMemberDirectory directory = services.GetRequiredService<IMemberDirectory>();
        List<string> notInvited = [];
        List<string> reasons = [];

        foreach (JsonElement invitation in invitations)
        {
            string? Text(string key) =>
                invitation.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            string username = Text("username")?.Trim() ?? string.Empty;
            string? password = Text("password");
            string? fullName = Text("fullname") ?? string.Join(' ', new[] { Text("firstname"), Text("lastname") }.Where(n => n is { Length: > 0 }));

            if (username.Length == 0 || password is null || password.Length < AuthEndpoints.MinimumPasswordLength)
            {
                notInvited.Add(username);
                reasons.Add(username.Length == 0
                    ? "An invitation names no username."
                    : $"'{username}' was not invited: this server sends no e-mail, so an invitation needs a password of at least "
                      + $"{AuthEndpoints.MinimumPasswordLength} characters, which the member changes when they first sign in.");
                continue;
            }

            string role = OurRoleOf(Text("role") ?? "org_user");
            // ADR-284: ArcGIS's ids and the Python API's words, to this server's types; anything else is a creator, as before.
            string userType = UserTypeOfLicense(Text("userLicenseType") ?? Text("userType")) ?? UserTypes.Creator;

            (int status, string? message, JsonElement _) = await InnerAnswerAsync(context, () => AdminEndpoints.CreateMemberAsync(
                    context, new MemberRequest(username, string.IsNullOrWhiteSpace(fullName) ? null : fullName, role, userType),
                    directory, services.GetRequiredService<IRoleDirectory>(), services.GetRequiredService<IPasswordHasher>(),
                    services.GetRequiredService<IIdentityProviderStore>(), services.GetRequiredService<IAuditLog>(), cancellation))
                .ConfigureAwait(false);

            // The one-time password Studio issued is in the answer just read, and goes no further than this frame.
            if (status >= 400)
            {
                notInvited.Add(username);
                reasons.Add($"'{username}' was not invited: {message ?? $"the server answered {status}"}");
                continue;
            }

            await directory.SetPasswordAsync(username, services.GetRequiredService<IPasswordHasher>().Hash(password), cancellation)
                .ConfigureAwait(false);
            await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "member.password", username,
                AdminEndpoints.Detail(new { through = "portals/self/invite" }), succeeded: true, cancellation).ConfigureAwait(false);
        }

        return Results.Ok(new { success = notInvited.Count < invitations.Count, notInvited, messages = reasons });
    }

    /// <summary><c>portals/self/updateuserrole</c>: Studio's role change, with the portal's role names read as ours.</summary>
    private static async Task<IResult> UpdateUserRoleAsync(HttpContext context, CancellationToken cancellation)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string user = form["user"].ToString().Trim();
        string role = form["role"].ToString().Trim();

        if (user.Length == 0 || role.Length == 0)
        {
            return PortalError(400, "updateuserrole takes a user and a role.");
        }

        IServiceProvider services = context.RequestServices;
        (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.SetMemberRoleAsync(
                context, user, new MemberRoleRequest(OurRoleOf(role)), services.GetRequiredService<IMemberDirectory>(),
                services.GetRequiredService<IIdentityStore>(), services.GetRequiredService<IRoleDirectory>(),
                services.GetRequiredService<IIdentityProviderStore>(), services.GetRequiredService<IAuditLog>(), cancellation))
            .ConfigureAwait(false);

        return status >= 400 ? PortalError(status, message ?? "The role was not changed.") : Results.Ok(new { success = true, username = user });
    }

    /// <summary><c>community/users/{u}/disable</c> and <c>enable</c>: Studio's.</summary>
    private static async Task<IResult> DisableUserAsync(HttpContext context, string username, bool disabled, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;
        (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.SetMemberDisabledAsync(
                context, username, disabled, services.GetRequiredService<IMemberDirectory>(),
                services.GetRequiredService<IIdentityStore>(), services.GetRequiredService<IAuditLog>(), cancellation))
            .ConfigureAwait(false);

        return status >= 400 ? PortalError(status, message ?? "Nothing was changed.") : Results.Ok(new { status = "success" });
    }

    /// <summary>
    /// <c>community/users/{u}/delete</c>: Studio's Remove member, which refuses a member who still owns content until it
    /// is said what becomes of it — as a Portal refuses to delete a user who owns items.
    /// </summary>
    private static async Task<IResult> DeleteUserAsync(HttpContext context, string username, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!current.Authorization.Allows(Privilege.AdminManageMembers))
        {
            return PortalError(403, "Deleting a user is for whoever may manage members.");
        }

        (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.RemoveMemberRequestAsync(context, username, cancellation))
            .ConfigureAwait(false);

        return status >= 400 ? PortalError(status, message ?? "The user was not deleted.") : Results.Ok(new { success = true, username });
    }
}
