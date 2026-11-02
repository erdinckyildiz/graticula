using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// Four portal doors onto what Studio already does — an item's <c>protect</c> and <c>unprotect</c>, its
/// <c>reassign</c>, a folder's <c>updateFolder</c>, and the groups an item is shared with (ADR-214 §5a).
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's review of 2026-11-02: four things Studio does that a script could not.</b> The ArcGIS API
/// for Python's <c>item.protect()</c>, <c>item.reassign_to()</c>, <c>Folder.rename()</c> and <c>item.shared_with</c>
/// each met a 404 here, although delete protection, Change owner, folder rename and group sharing all exist behind
/// Studio's own routes.
/// </para>
/// <para>
/// <b>A door, not a second implementation.</b> Each one finds the item as the other item writes find it
/// (<see cref="WritableItemAsync"/>), refuses who may not, and then runs Studio's own handler inside this request
/// (<see cref="InnerAsync"/>), so its privilege check, its audit line and its store call are the ones Studio's carry;
/// only its refusal is reworded into the portal's envelope. The groups an item is shared with are read from the
/// directory and the item's own share list, and published through <see cref="PortalGroup.Of"/> as every other portal
/// group is. The answers' shapes are the portal API's as documented; that this is how they map onto ours is INFERRED,
/// for the owner to confirm.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static void MapItemControls(WebApplication app)
    {
        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/items/{{id}}/protect", (HttpContext c, string username, string id, CancellationToken t) =>
                    ProtectItemAsync(c, username, id, true, t))
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapPost($"{user}/items/{{id}}/unprotect", (HttpContext c, string username, string id, CancellationToken t) =>
                    ProtectItemAsync(c, username, id, false, t))
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapPost($"{user}/items/{{id}}/reassign", ReassignItemAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }

        app.MapPost($"{Path}/content/users/{{username}}/{{folderId:length(32)}}/updateFolder", UpdateFolderAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

        Discoverable(app, $"{Path}/content/items/{{id}}/groups", ItemGroupsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
    }

    /// <summary>
    /// Portal's item <c>protect</c> and <c>unprotect</c>: the delete protection Studio's Settings › General sets, for a
    /// service, a web map or a document (ADR-102 condition 2).
    /// </summary>
    private static async Task<IResult> ProtectItemAsync(
        HttpContext context, string username, string id, bool wanted, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { Caller: var current })
        {
            return Unknown("User");
        }

        if (await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        if (!Manages(current, item))
        {
            return PortalError(403, $"'{item.Title}' is not yours to {(wanted ? "protect" : "unprotect")}.");
        }

        IServiceProvider services = context.RequestServices;
        ProtectionRequest request = new(wanted);
        IAuditLog audit = services.GetRequiredService<IAuditLog>();

        (int status, string? message) = await InnerAsync(context, () => item.Service is { } service
            ? AdminEndpoints.SetDeleteProtectedAsync(
                context, service.Name, service.Folder, request,
                services.GetRequiredService<IAdminCatalog>(), audit,
                services.GetRequiredService<PostgresLayerCatalog>(), cancellation)
            : item.Map is { } map
                ? AdminEndpoints.SetWebMapProtectionAsync(
                    context, map.Id, request, services.GetRequiredService<IWebMapStore>(), audit, cancellation)
                : AdminEndpoints.SetDocumentProtectionAsync(
                    context, item.Document!.Id, request, services.GetRequiredService<IDocumentStore>(), audit, cancellation))
            .ConfigureAwait(false);

        return status is >= 200 and < 300
            ? Results.Ok(new { success = true })
            : PortalError(status, message ?? $"'{item.Title}' was not {(wanted ? "protected" : "unprotected")}.");
    }

    /// <summary>
    /// Why a <c>targetUsername</c> cannot receive an item before anybody is looked up, or null when it may be asked for.
    /// </summary>
    /// <remarks>
    /// <b>A bare name, inside the request's organisation</b> — the rule <see cref="MayActForAnother"/> applies to the
    /// owner's path, for the same reason: the store resolves the name through <c>graticula_principal_named</c>
    /// (ADR-204 §5e item 16), where <c>organisation\name</c> reaches an outsider, and an administrator of this
    /// organisation does not hand its content to another one through this door (INFERRED).
    /// </remarks>
    internal static string? ReassignTargetRefusal(string? target) =>
        string.IsNullOrWhiteSpace(target)
            ? "'targetUsername' names the member who receives the item."
            : target.Contains('\\', StringComparison.Ordinal)
                ? $"'{target.Trim()}' names a member of another organisation; an item is reassigned to a member of this one."
                : null;

    /// <summary>
    /// Portal's item <c>reassign</c>: Studio's Change owner (an administrator's act, <see cref="Privilege.AdminManageAllContent"/>),
    /// and, when <c>targetFoldername</c> names one of the receiver's folders, a move into it.
    /// </summary>
    private static async Task<IResult> ReassignItemAsync(
        HttpContext context, IContentFolderStore folders, IAuditLog audit, string username, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { Caller: var current })
        {
            return Unknown("User");
        }

        if (await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        // Studio's rule, said before anything else is read: giving an item away is an administrator's act there too.
        if (!current.Authorization.Allows(Privilege.AdminManageAllContent))
        {
            return PortalError(403,
                $"'{item.Title}' was not reassigned: giving an item to another member is for an administrator who may manage all content.");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;
        string target = First(form, context, "targetUsername");
        string folderName = First(form, context, "targetFoldername");

        if (ReassignTargetRefusal(target) is { } refusal)
        {
            return PortalError(400, refusal);
        }

        // <b>The folder is found before the owner changes</b>, so a folder the receiver does not have refuses the whole
        // request rather than leaving the item given away and filed at their root.
        Guid? into = null;

        if (folderName.Length > 0 && folderName != "/")
        {
            if (context.RequestServices.GetService<IIdentityStore>() is not { } identity
                || await identity.FindForLoginAsync(target, cancellation).ConfigureAwait(false) is not { } receiver
                || receiver.Principal.Kind == PrincipalKind.Anonymous)
            {
                return PortalError(400, $"No member '{target}' to give it to.");
            }

            if ((await folders.ListAsync(receiver.Principal.Id, cancellation).ConfigureAwait(false))
                .FirstOrDefault(f => string.Equals(f.Title, folderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Id.ToString("N"), folderName, StringComparison.OrdinalIgnoreCase)) is not { } folder)
            {
                return PortalError(400, $"'{target}' has no folder '{folderName}', so '{item.Title}' was not reassigned.");
            }

            into = folder.Id;
        }

        IServiceProvider services = context.RequestServices;
        AdminEndpoints.OwnerRequest request = new(target);

        (int status, string? message) = await InnerAsync(context, () => item.Service is { } service
            ? AdminEndpoints.ChangeServiceOwnerAsync(
                context, service.Name, service.Folder, request, services.GetRequiredService<IAdminCatalog>(), audit, cancellation)
            : item.Map is { } map
                ? AdminEndpoints.ChangeWebMapOwnerAsync(
                    context, map.Id, request, services.GetRequiredService<IWebMapStore>(), audit, cancellation)
                : AdminEndpoints.ChangeDocumentOwnerAsync(
                    context, item.Document!.Id, request, services.GetRequiredService<IDocumentStore>(), audit, cancellation))
            .ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            return PortalError(status == StatusCodes.Status404NotFound ? 400 : status, message ?? $"'{item.Title}' was not reassigned.");
        }

        // Change owner files the item at the receiver's root, as Studio's does; the folder asked for is the store's move.
        if (into is { } folderId
            && !(item.Service is { } moved
                ? await folders.MoveServiceAsync(moved.Id, folderId, cancellation).ConfigureAwait(false)
                : item.Map is { } movedMap
                    ? await folders.MoveMapAsync(movedMap.Id, folderId, cancellation).ConfigureAwait(false)
                    : await folders.MoveDocumentAsync(item.Document!.Id, folderId, cancellation).ConfigureAwait(false)))
        {
            return PortalError(400,
                $"'{item.Title}' now belongs to '{target}', but it could not be put in '{folderName}' and is at the root of their content.");
        }

        return Results.Ok(new { success = true, itemId = id });
    }

    /// <summary>Portal's <c>updateFolder</c>: Studio's folder rename, under the title rules <c>createFolder</c> keeps.</summary>
    private static async Task<IResult> UpdateFolderAsync(
        HttpContext context, IContentFolderStore folders, IAuditLog audit, string username, string folderId, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { } acting)
        {
            return Unknown("User");
        }

        if (!Guid.TryParse(folderId, out Guid id)
            || await folders.FindAsync(id, cancellation).ConfigureAwait(false) is not { } folder
            || folder.Owner != acting.Owner)
        {
            return Unknown("Folder");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;
        string title = First(form, context, "title");

        if (title.Length is 0 or > 128)
        {
            return PortalError(400, "A folder's title is between 1 and 128 characters.");
        }

        (int status, string? message) = await InnerAsync(context, () => AdminEndpoints.RenameContentFolderAsync(
            context, id, new AdminEndpoints.ContentFolderRequest(title), folders, audit, cancellation)).ConfigureAwait(false);

        if (status == StatusCodes.Status409Conflict)
        {
            return PortalError(409, $"Folder '{title}' already exists.");
        }

        if (status is < 200 or >= 300)
        {
            return PortalError(status, message ?? $"Folder '{folder.Title}' was not renamed.");
        }

        return Results.Ok(new
        {
            success = true,
            folder = Folder(await folders.FindAsync(id, cancellation).ConfigureAwait(false) ?? folder with { Title = title }, acting.OwnerName),
        });
    }

    /// <summary>
    /// The groups an item is shared with, among those the caller may see, in the portal's three lists: those they own
    /// or manage, those they are a member of, and the rest.
    /// </summary>
    /// <param name="visible">The groups the caller may see, with where they stand in each.</param>
    /// <param name="sharedWith">The groups the item is shared with.</param>
    /// <returns>The three lists, each in the directory's order.</returns>
    internal static (List<GroupSummary> Admin, List<GroupSummary> Member, List<GroupSummary> Other) SharedGroupsOf(
        IEnumerable<GroupSummary> visible, IEnumerable<Guid> sharedWith)
    {
        HashSet<Guid> shared = [.. sharedWith];
        List<GroupSummary> admin = [], member = [], other = [];

        foreach (GroupSummary group in visible.Where(g => shared.Contains(g.Id)))
        {
            (group.Standing switch
            {
                GroupStanding.Owner or GroupStanding.Manager => admin,
                GroupStanding.Member => member,
                _ => other,
            }).Add(group);
        }

        return (admin, member, other);
    }

    /// <summary>
    /// Portal's item <c>groups</c> — what <c>item.shared_with</c> and the Share dialog read: the groups the item is
    /// shared with that the caller may see. Its owner's question, or an administrator's; anybody else is told the item
    /// does not exist, as any other read of what it is not theirs to manage.
    /// </summary>
    private static async Task<IResult> ItemGroupsAsync(
        HttpContext context, IGroupDirectory directory, string id, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous
            || await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item
            || !Manages(current, item))
        {
            return Unknown("Item");
        }

        bool administrator = current.Authorization.Allows(Privilege.AdminManageAllContent);
        IReadOnlyList<GroupSummary> visible = await directory.ListAsync(current.Principal.Id, administrator, cancellation).ConfigureAwait(false);
        IEnumerable<Guid> sharedWith = item.Service is { } service
            ? service.SharedWith
            : item.Map is { } map
                ? map.SharedWith ?? []
                : item.Document!.SharedWith ?? [];

        (List<GroupSummary> admin, List<GroupSummary> member, List<GroupSummary> other) = SharedGroupsOf(visible, sharedWith);

        return Results.Ok(new
        {
            admin = admin.Select(g => Group(context, g)),
            member = member.Select(g => Group(context, g)),
            other = other.Select(g => Group(context, g)),
        });
    }
}
