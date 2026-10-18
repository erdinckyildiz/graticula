using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>An item's terms of use, ArcGIS's <c>licenseInfo</c>.</summary>
internal sealed record ItemLicenseRequest(string? LicenseInfo);

/// <summary>An item's categories, each a path (<c>/Categories/Water</c>) or an id.</summary>
internal sealed record ItemCategoriesRequest(string[]? Categories);

/// <summary>A content category made or renamed.</summary>
internal sealed record ItemCategoryRequest(string? Name, Guid? Parent);

/// <summary>
/// Terms of use, content categories and favourites — ADR-202, from the ArcGIS veteran's review (V-96).
/// </summary>
/// <remarks>
/// <b>By item id, for every kind at once</b> — a service, a web map, a document — since migration 87 made the item one
/// record. Changed by the item's owner or by whoever manages all content, as its description is.
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>The longest terms of use kept: a licence's text, not a book.</summary>
    internal const int MaximumLicenseLength = 64_000;

    private static void MapItemAnnotations(WebApplication app)
    {
        app.MapPut("/content/items/{id}/license", SetItemLicenseAsync);
        app.MapPut("/content/items/{id}/categories", SetItemCategoriesAsync);
        app.MapPut("/content/items/{id}/favorite", (HttpContext context, string id, IItemAnnotationStore store, CancellationToken cancellation) =>
            SetFavoriteAsync(context, id, true, store, cancellation));
        app.MapDelete("/content/items/{id}/favorite", (HttpContext context, string id, IItemAnnotationStore store, CancellationToken cancellation) =>
            SetFavoriteAsync(context, id, false, store, cancellation));
        app.MapGet("/content/favorites", ListFavoritesAsync);

        app.MapGet("/admin/item-categories", ListItemCategoriesAsync);
        app.MapPost("/admin/item-categories", CreateItemCategoryAsync);
        app.MapPut("/admin/item-categories/{id:guid}", RenameItemCategoryAsync);
        app.MapDelete("/admin/item-categories/{id:guid}", DeleteItemCategoryAsync);
    }

    /// <summary>The item, when the caller may change it; otherwise refused, and null.</summary>
    private static async Task<Guid?> ManagedItemAsync(
        HttpContext context, string id, IItemAnnotationStore store, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to change an item.").ConfigureAwait(false);
            return null;
        }

        // A 404 for an item that does not exist and for one the caller may not change alike, as elsewhere.
        if (!Guid.TryParse(id, out Guid item)
            || await store.FindAsync(item, cancellation).ConfigureAwait(false) is not { } found
            || (found.Owner != current.Principal.Id && !current.Authorization.Allows(Privilege.AdminManageAllContent)))
        {
            await Refuse(context, 404, $"No item '{id}' that you may change.").ConfigureAwait(false);
            return null;
        }

        return item;
    }

    private static async Task SetItemLicenseAsync(
        HttpContext context, string id, ItemLicenseRequest request, IItemAnnotationStore store, ItemAnnotations held,
        IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ManagedItemAsync(context, id, store, cancellation).ConfigureAwait(false) is not { } item)
        {
            return;
        }

        string? text = string.IsNullOrWhiteSpace(request.LicenseInfo) ? null : request.LicenseInfo.Trim();

        if (text is { Length: > MaximumLicenseLength })
        {
            await Refuse(context, 400, $"Terms of use are at most {MaximumLicenseLength:N0} characters.").ConfigureAwait(false);
            return;
        }

        await store.SetLicenseAsync(item, text, cancellation).ConfigureAwait(false);
        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "item.license", id, Detail(new { length = text?.Length ?? 0 }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        await Results.Json(new { id, licenseInfo = text }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetItemCategoriesAsync(
        HttpContext context, string id, ItemCategoriesRequest request, IItemAnnotationStore store, ItemAnnotations held,
        IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await ManagedItemAsync(context, id, store, cancellation).ConfigureAwait(false) is not { } item)
        {
            return;
        }

        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        List<Guid> chosen = [];

        foreach (string named in request.Categories ?? [])
        {
            if (held.Resolve(named) is not { } category)
            {
                await Refuse(context, 400, $"'{named}' is not a category. They are listed at /admin/item-categories.")
                    .ConfigureAwait(false);
                return;
            }

            chosen.Add(category);
        }

        if (!await store.SetCategoriesAsync(item, chosen, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 409, "The item or a category went while this was being set; try again.").ConfigureAwait(false);
            return;
        }

        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "item.categories", id, Detail(new { categories = chosen }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        await Results.Json(new { id, categories = held.CategoriesOf(item) }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetFavoriteAsync(
        HttpContext context, string id, bool favorite, IItemAnnotationStore store, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to keep favourites.").ConfigureAwait(false);
            return;
        }

        // Marking is not reading: the item itself is still read through its own route, which applies its sharing.
        if (!Guid.TryParse(id, out Guid item)
            || !await store.SetFavoriteAsync(current.Principal.Id, item, favorite, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No item '{id}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new { id, favorite }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListFavoritesAsync(HttpContext context, IItemAnnotationStore store, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to see your favourites.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<(Guid Item, string Kind, DateTimeOffset Added)> favorites =
            await store.FavoritesAsync(current.Principal.Id, cancellation).ConfigureAwait(false);

        // Ids and kinds, not titles: an item whose sharing has since narrowed is read, or refused, by its own route.
        await Results.Json(new
        {
            favorites = favorites.Select(f => new { id = f.Item.ToString("N"), kind = f.Kind, added = f.Added }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListItemCategoriesAsync(HttpContext context, ItemAnnotations held, CancellationToken cancellation)
    {
        if (context.Features.Get<RequestPrincipal>()!.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to see the content categories.").ConfigureAwait(false);
            return;
        }

        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        await Results.Json(new
        {
            categories = held.Tree.Select(c => new { id = c.Id, parent = c.ParentId, name = c.Name, path = held.PathOf(c.Id), items = c.Items }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateItemCategoryAsync(
        HttpContext context, ItemCategoryRequest request, IItemAnnotationStore store, ItemAnnotations held, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent).ConfigureAwait(false))
        {
            return;
        }

        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > 100 || name.Contains('/', StringComparison.Ordinal))
        {
            await Refuse(context, 400, "A category's name is up to 100 characters, without '/', which separates a path.")
                .ConfigureAwait(false);
            return;
        }

        if (await store.CreateCategoryAsync(request.Parent, name, cancellation).ConfigureAwait(false) is not { } made)
        {
            await Refuse(context, 409, "The parent does not exist, or a category of that name is already beside it.")
                .ConfigureAwait(false);
            return;
        }

        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "itemcategory.create", name, Detail(new { made.Id, made.ParentId }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        await Results.Json(new { id = made.Id, parent = made.ParentId, name, path = held.PathOf(made.Id) }, statusCode: 201)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RenameItemCategoryAsync(
        HttpContext context, Guid id, ItemCategoryRequest request, IItemAnnotationStore store, ItemAnnotations held,
        IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent).ConfigureAwait(false))
        {
            return;
        }

        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length is 0 or > 100 || name.Contains('/', StringComparison.Ordinal)
            || !await store.RenameCategoryAsync(id, name, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 409, "No such category, a category of that name is already beside it, or the name is "
                + "empty, over 100 characters or holds '/'.").ConfigureAwait(false);
            return;
        }

        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "itemcategory.rename", name, Detail(new { id }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        await Results.Json(new { id, name, path = held.PathOf(id) }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteItemCategoryAsync(
        HttpContext context, Guid id, IItemAnnotationStore store, ItemAnnotations held, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent).ConfigureAwait(false))
        {
            return;
        }

        if (!await store.DeleteCategoryAsync(id, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No category {id}.").ConfigureAwait(false);
            return;
        }

        await held.RefreshAsync(cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "itemcategory.delete", id.ToString(), Detail(new { }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = 204;
    }
}
