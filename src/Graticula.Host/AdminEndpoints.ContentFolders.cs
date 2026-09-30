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

namespace Graticula.Host;

/// <summary>
/// A member's content folders, and Move — ADR-114. Not the service folder: nothing here changes a URL.
/// </summary>
internal static partial class AdminEndpoints
{
    private static void MapContentFolders(WebApplication app)
    {
        app.MapGet("/content/folders", ListContentFoldersAsync);
        app.MapPost("/content/folders", CreateContentFolderAsync);
        app.MapPut("/content/folders/{id:guid}", RenameContentFolderAsync);
        app.MapDelete("/content/folders/{id:guid}", DeleteContentFolderAsync);
        app.MapPost("/content/move", MoveContentAsync);
    }

    /// <summary>What a folder write reads.</summary>
    /// <param name="Title">The folder's title.</param>
    internal sealed record ContentFolderRequest(string? Title);

    /// <summary>One item to move: a service by its qualified name, or a web map by its id.</summary>
    /// <param name="Service">The service, as <c>folder/name</c> or <c>name</c>.</param>
    /// <param name="Webmap">The web map's id.</param>
    internal sealed record MoveItem(string? Service, string? Webmap);

    /// <summary>What <c>POST /content/move</c> reads.</summary>
    /// <param name="Items">The items.</param>
    /// <param name="To">The folder's id, or null for the root.</param>
    internal sealed record MoveRequest(MoveItem[]? Items, Guid? To);

    private static bool SignedIn(HttpContext context, out RequestPrincipal current)
    {
        current = context.Features.Get<RequestPrincipal>()!;
        return current.Principal != Principal.Anonymous;
    }

    private static string? FolderTitle(string? title, out string? why)
    {
        string trimmed = (title ?? string.Empty).Trim();
        why = trimmed.Length is 0 or > 128 ? "A folder's title is between 1 and 128 characters." : null;
        return why is null ? trimmed : null;
    }

    /// <summary>The caller's folders — Portal's left column in My content.</summary>
    private static async Task ListContentFoldersAsync(HttpContext context, IContentFolderStore folders, CancellationToken cancellation)
    {
        if (!SignedIn(context, out RequestPrincipal current))
        {
            await Refuse(context, 401, "Sign in to see your folders.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ContentFolder> mine = await folders.ListAsync(current.Principal.Id, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            folders = mine.Select(f => new { id = f.Id, title = f.Title, created = f.Created, items = f.Items }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateContentFolderAsync(
        HttpContext context, ContentFolderRequest request, IContentFolderStore folders, IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SignedIn(context, out RequestPrincipal current))
        {
            await Refuse(context, 401, "Sign in to make a folder.").ConfigureAwait(false);
            return;
        }

        if (FolderTitle(request.Title, out string? why) is not { } title)
        {
            await Refuse(context, 400, why!).ConfigureAwait(false);
            return;
        }

        if (await folders.CreateAsync(current.Principal.Id, title, cancellation).ConfigureAwait(false) is not { } made)
        {
            await Refuse(context, 409, $"You already have a folder called '{title}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "content.folder.create", title, Detail(new { id = made.Id }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new { id = made.Id, title = made.Title, created = made.Created, items = 0 }, statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The folder, when the caller owns it or administers content; otherwise the refusal is written.</summary>
    private static async Task<ContentFolder?> OwnFolderAsync(
        HttpContext context, IContentFolderStore folders, Guid id, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await folders.FindAsync(id, cancellation).ConfigureAwait(false) is not { } folder
            || !LayerAccess.MayManage(folder.Owner, current.Principal, current.Authorization))
        {
            // Not saying whether a folder that is not yours exists.
            await Refuse(context, 404, "No such folder, or not one you may change.").ConfigureAwait(false);
            return null;
        }

        return folder;
    }

    private static async Task RenameContentFolderAsync(
        HttpContext context, Guid id, ContentFolderRequest request, IContentFolderStore folders, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await OwnFolderAsync(context, folders, id, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        if (FolderTitle(request.Title, out string? why) is not { } title)
        {
            await Refuse(context, 400, why!).ConfigureAwait(false);
            return;
        }

        if (await folders.RenameAsync(id, title, cancellation).ConfigureAwait(false) == ContentFolderWrite.Taken)
        {
            await Refuse(context, 409, $"There is already a folder called '{title}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "content.folder.rename", title, Detail(new { id }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new { id, title }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteContentFolderAsync(
        HttpContext context, Guid id, IContentFolderStore folders, IAuditLog audit, CancellationToken cancellation)
    {
        if (await OwnFolderAsync(context, folders, id, cancellation).ConfigureAwait(false) is not { } folder)
        {
            return;
        }

        if (await folders.DeleteAsync(id, cancellation).ConfigureAwait(false) == ContentFolderWrite.NotEmpty)
        {
            await Refuse(context, 409,
                $"'{folder.Title}' holds {folder.Items} item{(folder.Items == 1 ? "" : "s")}. Move them out first; a folder "
                + "is deleted only when it is empty, so deleting one never deletes an item.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "content.folder.delete", folder.Title, Detail(new { id }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new { id, deleted = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves items into one of their owner's folders, or to the root — ADR-114 §5.2. Each item answers for itself.
    /// </summary>
    private static async Task MoveContentAsync(
        HttpContext context,
        MoveRequest request,
        IContentFolderStore folders,
        PostgresLayerCatalog published,
        IWebMapStore maps,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SignedIn(context, out RequestPrincipal current))
        {
            await Refuse(context, 401, "Sign in to move items.").ConfigureAwait(false);
            return;
        }

        if (request.Items is not { Length: > 0 } items)
        {
            await Refuse(context, 400, "'items' names what to move.").ConfigureAwait(false);
            return;
        }

        ContentFolder? target = null;

        if (request.To is { } to && (target = await folders.FindAsync(to, cancellation).ConfigureAwait(false)) is null)
        {
            await Refuse(context, 404, "No such folder.").ConfigureAwait(false);
            return;
        }

        List<object> results = [];

        foreach (MoveItem item in items)
        {
            (string label, Guid? owner, Func<Task<bool>>? move) = await ResolveMoveAsync(
                item, request.To, folders, published, maps, cancellation).ConfigureAwait(false);

            string? error =
                move is null ? "No such item."
                : !LayerAccess.MayManage(owner, current.Principal, current.Authorization) ? "Not an item you may move."
                : target is not null && target.Owner != owner
                    ? "That folder is not the item's owner's. An item is moved only among its owner's folders; Change owner gives it to somebody else."
                : null;

            if (error is null && !await move!().ConfigureAwait(false))
            {
                error = "No such item.";
            }

            results.Add(new { item = label, success = error is null, error });
        }

        await AuditAsync(context, audit, "content.move", target?.Title ?? "(root)",
            Detail(new { to = request.To, items = results }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { to = request.To, results }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>An item's label, owner and the move itself, or a null move when there is no such item.</summary>
    internal static async Task<(string Label, Guid? Owner, Func<Task<bool>>? Move)> ResolveMoveAsync(
        MoveItem item,
        Guid? to,
        IContentFolderStore folders,
        PostgresLayerCatalog published,
        IWebMapStore maps,
        CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(item.Service))
        {
            string qualified = item.Service.Trim();
            int slash = qualified.LastIndexOf('/');
            string? folder = slash < 0 ? null : qualified[..slash];
            string name = slash < 0 ? qualified : qualified[(slash + 1)..];

            return await published.FindServiceAsync(folder, name, cancellation).ConfigureAwait(false) is { } service
                ? (qualified, service.Owner, () => folders.MoveServiceAsync(service.Id, to, cancellation))
                : (qualified, null, null);
        }

        if (!string.IsNullOrWhiteSpace(item.Webmap))
        {
            string id = item.Webmap.Trim();

            return await maps.FindAsync(id, cancellation).ConfigureAwait(false) is { } map
                ? (id, map.Owner, () => folders.MoveMapAsync(map.Id, to, cancellation))
                : (id, null, null);
        }

        return ("(nothing)", null, null);
    }
}
