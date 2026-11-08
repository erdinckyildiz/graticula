using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
/// Apps and links as items — a Dashboard, an Experience Builder app, a Survey123 form, a StoryMap, a service or a
/// document elsewhere registered by its address — and the resources any item keeps beside itself (ADR-264, the ArcGIS
/// veteran's review of v1.0.499, #1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept, not run.</b> This server holds an app's item, its JSON and its resources, as a Portal does for the app's own
/// client to read; it does not serve Experience Builder, Dashboards or Survey123 themselves.
/// </para>
/// <para>
/// <b>An app is a document</b> whose content is its JSON (<see cref="Documents.Apps"/>), so it is listed, shared, owned,
/// moved, deleted and recycled as one. Resources are any item's, written by whoever may change the item and read by
/// whoever may read it — a <c>private</c> one only by them.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The most one item's resources may hold together: 100 MB.</summary>
    private const long MaximumResourceBytes = 100L * 1024 * 1024;

    private static void MapAppItems(WebApplication app)
    {
        Discoverable(app, $"{Path}/content/items/{{id}}/resources", ListResourcesAsync).Governed(SharingGovernedExtensions.ByOwnership);
        Discoverable(app, $"{Path}/content/items/{{id}}/resources/{{**resource}}", GetResourceAsync).Governed(SharingGovernedExtensions.ByOwnership);

        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/items/{{id}}/addResources", (HttpContext c, string username, string id, CancellationToken t) =>
                WriteResourcesAsync(c, username, id, replace: false, t)).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapPost($"{user}/items/{{id}}/updateResources", (HttpContext c, string username, string id, CancellationToken t) =>
                WriteResourcesAsync(c, username, id, replace: true, t)).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapPost($"{user}/items/{{id}}/removeResources", RemoveResourcesAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }
    }

    /// <summary>Why an app's JSON cannot be kept — it is not JSON, or larger than a document may be — or null.</summary>
    internal static string? AppTextRefusal(string text, long limit)
    {
        if (Encoding.UTF8.GetByteCount(text) > limit)
        {
            return AdminEndpoints.DocumentTooLarge(limit);
        }

        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return null;
        }
        catch (JsonException)
        {
            return "its text is not JSON.";
        }
    }

    /// <summary>Why an address cannot be an item's — not an absolute http or https address — or null.</summary>
    internal static string? AppUrlRefusal(string url) =>
        url.Length > 2048 ? "a URL is at most 2,048 characters."
        : Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.Scheme is "https" or "http" ? null
        : "a URL is an absolute http or https address.";

    /// <summary>
    /// <c>addItem</c> of an app or a link: its <c>text</c>, its <c>url</c>, or both, kept as a document of the caller's
    /// with Studio's create privilege and audit line; then its tags, description and folder, as a file's are.
    /// </summary>
    internal static async Task<IResult> AddAppItemAsync(
        HttpContext context, RequestPrincipal current, IFormCollection form, DocumentKind kind, Guid? folder, CancellationToken cancellation)
    {
        if (!current.Authorization.Allows(Privilege.ContentCreate))
        {
            return PortalError(403, "Adding content needs the privilege to create content.");
        }

        IServiceProvider services = context.RequestServices;
        long limit = services.GetRequiredService<HostSettings>().DocumentMaximumBytes;
        string text = First(form, context, "text") ?? string.Empty;
        string? url = First(form, context, "url") is { Length: > 0 } given ? given.Trim() : null;

        if (text.Length == 0 && url is null)
        {
            return PortalError(400, $"A {kind.ArcGisType} item is added with its 'text', its 'url', or both.");
        }

        if ((AppTextRefusal(text, limit) ?? (url is null ? null : AppUrlRefusal(url))) is { } why)
        {
            return PortalError(400, $"The {kind.ArcGisType} was not added: {why}");
        }

        string title = First(form, context, "title") is { Length: > 0 } named ? named.Trim() : kind.ArcGisType;
        string? snippet = First(form, context, "snippet") is { Length: > 0 } said ? said : null;

        if (AdminEndpoints.DescribedRefusal(title, snippet) is { } described)
        {
            return PortalError(400, $"The {kind.ArcGisType} was not added: {described}");
        }

        IReadOnlyList<string>? tags = TagsOf(First(form, context, "tags"), out string? badTags);
        if (tags is null && badTags is not null)
        {
            return PortalError(400, badTags);
        }

        IDocumentStore documents = services.GetRequiredService<IDocumentStore>();
        Document made = await documents.CreateAsync(title, snippet, current.Principal.Id, SharingScope.Private,
            Documents.CleanFileName(title + kind.Extension, kind), kind, Encoding.UTF8.GetBytes(text), cancellation).ConfigureAwait(false);

        IReadOnlyList<string>? keywords = First(form, context, "typeKeywords") is { Length: > 0 } words
            ? [.. words.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(128)]
            : null;
        if (url is not null || keywords is not null)
        {
            await documents.SetLinkAsync(made.Id, url, keywords, cancellation).ConfigureAwait(false);
        }

        if (tags is { Count: > 0 })
        {
            await documents.SetTagsAsync(made.Id, tags, cancellation).ConfigureAwait(false);
        }

        if (First(form, context, "description") is { Length: > 0 } description)
        {
            await documents.SetDescriptionAsync(made.Id, description.Length > 64_000 ? description[..64_000] : description, cancellation)
                .ConfigureAwait(false);
        }

        if (folder is { } into)
        {
            await services.GetRequiredService<IContentFolderStore>().MoveDocumentAsync(made.Id, into, cancellation).ConfigureAwait(false);
        }

        await AdminEndpoints.AuditAsync(context, services.GetRequiredService<IAuditLog>(), "document.create", made.Id,
            AdminEndpoints.Detail(new { title, kind = kind.Name, url, through = "portal addItem" }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        return Results.Ok(new { success = true, id = made.Id, folder = folder?.ToString("N") });
    }

    /// <summary>
    /// The item an id names, if this caller may read it, with its owner: a web map, a document or app, or a service by any
    /// of its faces' ids. Null when it names nothing the caller may see — the same answer either way.
    /// </summary>
    private static async Task<(Guid Item, Guid? Owner)?> ReadableItemAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;

        if (await ReadableMapAsync(context, services.GetRequiredService<IWebMapStore>(), id, cancellation).ConfigureAwait(false) is { } map)
        {
            return (Guid.Parse(map.Id), map.Owner);
        }

        if (await ReadableDocumentAsync(context, id, cancellation).ConfigureAwait(false) is { } document)
        {
            return (Guid.Parse(document.Id), document.Owner);
        }

        IReadOnlyList<Graticula.Platform.Catalog.PublishedService>? visible =
            await VisibleAsync(context, services.GetRequiredService<CatalogFallback>(), cancellation).ConfigureAwait(false);

        if (visible?.FirstOrDefault(s => ItemsOf(context, s).Any(face => string.Equals(face.Id, id, StringComparison.OrdinalIgnoreCase)))
            is { } service)
        {
            return (service.Id, service.Owner);
        }

        // An image service's item (ADR-267 condition 1): its comments and metadata are kept under its service id, as a
        // listing reads them.
        IReadOnlyList<Graticula.Platform.Catalog.PublishedCoverage>? images =
            await VisibleCoveragesAsync(context, services.GetRequiredService<ICoverageCatalog>(), cancellation).ConfigureAwait(false);

        return images?.FirstOrDefault(c => string.Equals(CoverageItemId(c), id, StringComparison.OrdinalIgnoreCase))
            is { } image ? (image.ServiceId, image.Owner) : null;
    }

    /// <summary>Whether this caller may read an item's private resources: its owner, or whoever may manage all content.</summary>
    private static bool SeesPrivateResources(RequestPrincipal current, Guid? owner) =>
        (!current.Principal.IsAnonymous && owner == current.Principal.Id) || current.Authorization.Allows(Privilege.AdminManageAllContent);

    /// <summary><c>items/{id}/resources</c>: a page of what the item keeps, as Portal lists it.</summary>
    private static async Task<IResult> ListResourcesAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ReadableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        int start = int.TryParse(context.Request.Query["start"], out int s) && s > 0 ? s : 1;
        int num = int.TryParse(context.Request.Query["num"], out int n) ? Math.Clamp(n, 1, 100) : 10;
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool all = SeesPrivateResources(current, item.Owner);

        (IReadOnlyList<ItemResource> page, int total) = await context.RequestServices.GetRequiredService<IItemResourceStore>()
            .ListAsync(item.Item, start - 1, num, cancellation).ConfigureAwait(false);

        // A private resource is not listed to a reader who may not open it, as if it were not there.
        ItemResource[] shown = [.. page.Where(r => all || r.Access != "private")];

        return Results.Ok(new
        {
            total = all ? total : total - (page.Count - shown.Length),
            start,
            num,
            nextStart = start - 1 + page.Count < total ? start + page.Count : -1,
            resources = shown.Select(r => new
            {
                resource = r.Path,
                created = r.Created.ToUnixTimeMilliseconds(),
                size = r.Size,
                access = r.Access,
            }),
        });
    }

    /// <summary><c>items/{id}/resources/{path}</c>: one resource's bytes.</summary>
    private static async Task<IResult> GetResourceAsync(HttpContext context, string id, string resource, CancellationToken cancellation)
    {
        if (await ReadableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Resource");
        }

        // <b>A readable item without the resource answers HTTP 200 with the error in the body, since 2026-11-08</b>, as
        // ArcGIS Online does (measured: `{"error":{"code":400,"messageCode":"CONT_009431","message":"Item resource
        // doesn't exist"}}` on a 200). ArcGIS Field Maps asks every map for `field-maps-settings.json` and
        // `tasks-configuration.json`, which most maps do not have, and this answered both with an HTTP 400.
        if (await context.RequestServices.GetRequiredService<IItemResourceStore>()
                .FindAsync(item.Item, resource, cancellation).ConfigureAwait(false) is not { } found
            || (found.Resource.Access == "private" && !SeesPrivateResources(context.Features.Get<RequestPrincipal>()!, item.Owner)))
        {
            return Results.Json(new
            {
                error = new { code = 400, messageCode = "CONT_009431", message = "Item resource doesn't exist", details = Array.Empty<string>() },
            });
        }

        // Never a page: a resource is whatever its writer said it was, so it is not sniffed or framed.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return Results.File(found.Content, found.Resource.MediaType);
    }

    /// <summary>
    /// <c>addResources</c> and <c>updateResources</c>: a <c>file</c> or a <c>text</c>, at <c>resourcesPrefix</c>/<c>fileName</c>,
    /// by whoever may change the item. An add does not replace, and an update does not add, as Portal's do.
    /// </summary>
    private static async Task<IResult> WriteResourcesAsync(
        HttpContext context, string username, string id, bool replace, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { Caller: var current })
        {
            return Unknown("User");
        }

        if (await ResourceItemAsync(context, id, current, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        IFormFile? file = form.Files.GetFile("file");
        string? text = form["text"].FirstOrDefault();
        string name = form["fileName"].ToString().Trim() is { Length: > 0 } given ? given : file?.FileName ?? string.Empty;

        if (ResourcePath(form["resourcesPrefix"].ToString(), name) is not { } path)
        {
            return PortalError(400, "A resource is named by its fileName, and its prefix and name are folders and a file: no '..', no leading '/'.");
        }

        byte[] content;
        if (file is not null)
        {
            content = new byte[file.Length];
            await using Stream stream = file.OpenReadStream();
            await stream.ReadExactlyAsync(content, cancellation).ConfigureAwait(false);
        }
        else if (text is not null)
        {
            content = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            return PortalError(400, "A resource is sent as its 'file' or its 'text'.");
        }

        string access = string.Equals(form["access"].ToString(), "private", StringComparison.OrdinalIgnoreCase) ? "private" : "inherit";
        string media = MediaTypeOf(path, file?.ContentType);

        ItemResourceWrite done = await context.RequestServices.GetRequiredService<IItemResourceStore>()
            .WriteAsync(item, path, media, content, access, replace, MaximumResourceBytes, cancellation).ConfigureAwait(false);

        return done switch
        {
            ItemResourceWrite.Added or ItemResourceWrite.Replaced => Results.Ok(new { success = true, itemId = id, owner = username, folder = (string?)null }),
            ItemResourceWrite.AlreadyThere => PortalError(409, $"A resource '{path}' is already there; updateResources replaces one."),
            ItemResourceWrite.NotThere => PortalError(404, $"There is no resource '{path}' to update; addResources adds one."),
            _ => PortalError(413, $"An item's resources hold at most {MaximumResourceBytes / 1048576} MB together."),
        };
    }

    /// <summary><c>removeResources</c>: one <c>resource</c>, or every one with <c>deleteAll</c>.</summary>
    private static async Task<IResult> RemoveResourcesAsync(HttpContext context, string username, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change your content from another site.");
        }

        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { Caller: var current })
        {
            return Unknown("User");
        }

        if (await ResourceItemAsync(context, id, current, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        bool all = string.Equals(form["deleteAll"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        string resource = form["resource"].ToString().Trim();

        if (!all && resource.Length == 0)
        {
            return PortalError(400, "removeResources names a 'resource', or says deleteAll=true.");
        }

        int removed = await context.RequestServices.GetRequiredService<IItemResourceStore>()
            .RemoveAsync(item, all ? null : resource, cancellation).ConfigureAwait(false);

        return removed == 0 && !all ? PortalError(404, $"There is no resource '{resource}'.") : Results.Ok(new { success = true });
    }

    /// <summary>The item an id names, if this caller may change it: its owner, or whoever may manage all content.</summary>
    private static async Task<Guid?> ResourceItemAsync(HttpContext context, string id, RequestPrincipal current, CancellationToken cancellation) =>
        await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is { } item && Manages(current, item) ? item.ItemGuid : null;

    /// <summary>A resource's path from its prefix and name, or null when either would leave the item.</summary>
    private static string? ResourcePath(string prefix, string name)
    {
        string path = (prefix.Trim().Trim('/') is { Length: > 0 } folder ? folder + "/" : string.Empty) + name.Trim();

        return path.Length is 0 or > 512 || path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal)
            || path.Split('/').Any(part => part is "" or "." or "..")
            ? null : path;
    }

    /// <summary>What a resource is, by its name, or what its sender said, or bytes.</summary>
    private static string MediaTypeOf(string path, string? said) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => said is { Length: > 0 } && !said.Contains("html", StringComparison.OrdinalIgnoreCase) ? said : "application/octet-stream",
        };
}
