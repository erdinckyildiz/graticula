using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Graticula.Host;

/// <summary>
/// Documents — ADR-188: files filed as items, under <c>/content/documents</c> beside <c>/content/webmaps</c>.
/// </summary>
/// <remarks>
/// <b>A web map's rules, and the routes laid out as its are.</b> Reading follows the item's scope through
/// <see cref="LayerAccess.Evaluate"/>, and a document the caller may not read is a 404, the same answer as one that
/// does not exist; changing and deleting are <see cref="LayerAccess.MayManage"/>. What is new is only the file: it is
/// recognised before it is kept (<see cref="Documents.Recognise"/>) and served by <see cref="DocumentDownload"/>.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapDocuments(WebApplication app)
    {
        app.MapGet("/content/documents", ListDocumentsAsync);
        app.MapPost("/content/documents", CreateDocumentAsync).DisableAntiforgery();
        app.MapGet("/content/documents/{id}", GetDocumentAsync);
        app.MapPut("/content/documents/{id}", UpdateDocumentAsync);
        app.MapDelete("/content/documents/{id}", DeleteDocumentAsync);
        app.MapGet("/content/documents/{id}/data", DocumentDataAsync);
        app.MapPut("/content/documents/{id}/data", ReplaceDocumentAsync).DisableAntiforgery();
        app.MapGet("/content/documents/{id}/thumbnail", DocumentThumbnailAsync);
        app.MapPut("/content/documents/{id}/thumbnail", SetDocumentThumbnailAsync).DisableAntiforgery();
        app.MapDelete("/content/documents/{id}/thumbnail", RemoveDocumentThumbnailAsync);
        app.MapPut("/content/documents/{id}/protection", SetDocumentProtectionAsync);
        app.MapPut("/content/documents/{id}/description", SetDocumentDescriptionAsync);
        app.MapPut("/content/documents/{id}/tags", SetDocumentTagsAsync);
        app.MapPut("/content/documents/{id}/owner", ChangeDocumentOwnerAsync);
        app.MapPut("/admin/groups/{name}/documents/{id}", ShareDocumentWithGroupAsync);
        app.MapDelete("/admin/groups/{name}/documents/{id}", UnshareDocumentFromGroupAsync);
    }

    /// <summary>The documents this caller may open: their own first, then what is shared with them.</summary>
    private static async Task ListDocumentsAsync(HttpContext context, IDocumentStore documents, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401,
                "This lists the documents you can open, so it needs to know who you are. Sign in at /rest/auth/login. "
                + "A public document downloads without signing in, from its own link.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<Document> all = await documents.ListAsync(cancellation).ConfigureAwait(false);

        object[] readable =
        [
            .. all
                .Where(document => Readable(current, document))
                .OrderByDescending(document => document.Owner == current.Principal.Id)
                .ThenByDescending(document => document.Modified)
                .Select(document => Describe(current, document)),
        ];

        await Results.Json(new
        {
            documents = readable,
            mayCreate = current.Authorization.Allows(Privilege.ContentCreate),
            accepted = Documents.Accepted.Select(kind => kind.Extension).Distinct().ToArray(),
            maximumBytes = MaximumDocumentBytes(context),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One document's description, if this caller may read it.</summary>
    private static async Task GetDocumentAsync(
        HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await documents.FindAsync(id, cancellation).ConfigureAwait(false) is not { } document || !Readable(current, document))
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return;
        }

        await Results.Json(Describe(current, document)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The file itself, if this caller may read it — always as a download.</summary>
    private static async Task DocumentDataAsync(
        HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await documents.FindAsync(id, cancellation).ConfigureAwait(false) is not { } document
            || !Readable(current, document))
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return;
        }

        // <b>`check=1` asks, and sends nothing</b> — Studio's Download asks first, as its other downloads do, so a refusal
        // is a sentence on the page rather than a broken file in the browser's list (ADR-188 condition 4).
        if (context.Request.Query["check"] == "1")
        {
            await Results.Json(new { fileName = document.FileName, bytes = document.Size }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (await documents.ContentAsync(id, cancellation).ConfigureAwait(false) is not { } content)
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return;
        }

        await DocumentDownload.WriteAsync(context, document, content, cancellation).ConfigureAwait(false);
    }

    /// <summary>A document's thumbnail, to whoever may open the document — D-321.</summary>
    internal static async Task DocumentThumbnailAsync(
        HttpContext context, string id, IDocumentStore documents, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await documents.FindAsync(id, cancellation).ConfigureAwait(false) is not { } document || !Readable(current, document))
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return;
        }

        if (await documents.ThumbnailAsync(id, cancellation).ConfigureAwait(false) is not { } picture
            || Documents.RecognisePicture(picture) is not { } kind)
        {
            await Refuse(context, 404, $"'{document.Title}' has no thumbnail of its own.").ConfigureAwait(false);
            return;
        }

        await WritePictureAsync(context, picture, kind, cancellation).ConfigureAwait(false);
    }

    /// <summary>Writes a kept picture with the type its own bytes say, and nothing a browser could take for a page.</summary>
    internal static async Task WritePictureAsync(HttpContext context, byte[] picture, DocumentKind kind, CancellationToken cancellation)
    {
        context.Response.ContentType = kind.MediaType;
        context.Response.ContentLength = picture.Length;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "private, no-cache";
        await context.Response.Body.WriteAsync(picture, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the picture a multipart <c>file</c> carries as a document's thumbnail — a PNG, JPEG, GIF or WebP of at most
    /// 1 MB, as an ArcGIS item's is. D-321: a document had only its kind's tile, or itself when it is a picture.
    /// </summary>
    private static async Task SetDocumentThumbnailAsync(
        HttpContext context, string id, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "change the thumbnail of", documents, audit, cancellation)
            .ConfigureAwait(false) is not { } before)
        {
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            await Refuse(context, 400, "Upload the picture as multipart/form-data with a 'file' part.").ConfigureAwait(false);
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

        if (form.Files.GetFile("file") is not { Length: > 0 } file)
        {
            await Refuse(context, 400, "Choose a picture to use as the thumbnail.").ConfigureAwait(false);
            return;
        }

        if (file.Length > Documents.MaximumThumbnailBytes)
        {
            await Refuse(context, 413,
                $"The picture is {DocumentSize(file.Length)} and a thumbnail is at most 1 MB. A picture 600 by 400 pixels is plenty.")
                .ConfigureAwait(false);
            return;
        }

        byte[] picture = new byte[file.Length];

        await using (System.IO.Stream stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(picture, cancellation).ConfigureAwait(false);
        }

        if (Documents.RecognisePicture(picture) is not { } kind)
        {
            await Refuse(context, 415, "A thumbnail is a PNG, JPEG, GIF or WebP picture, and this file is none of these.")
                .ConfigureAwait(false);
            return;
        }

        await documents.SetThumbnailAsync(id, picture, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "document.thumbnail", id, Detail(new { kind = kind.Name, bytes = picture.Length }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(current, before with { HasThumbnail = true })).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Forgets a document's own thumbnail, so it shows its kind's tile — or itself, when it is a picture — again.</summary>
    private static async Task RemoveDocumentThumbnailAsync(
        HttpContext context, string id, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "change the thumbnail of", documents, audit, cancellation)
            .ConfigureAwait(false) is not { } before)
        {
            return;
        }

        await documents.SetThumbnailAsync(id, null, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "document.thumbnail", id, Detail(new { removed = true }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(Describe(current, before with { HasThumbnail = false })).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A size as a person reads it.</summary>
    private static string DocumentSize(long bytes) =>
        bytes >= 1 << 20
            ? $"{bytes / (double)(1 << 20):0.0} MB"
            : $"{Math.Max(1, bytes / 1024):0} KB";

    /// <summary>Files a new document, owned by whoever uploads it.</summary>
    /// <remarks>
    /// <b>Multipart</b>: <c>file</c>, and optionally <c>title</c> (the file's name without its extension otherwise),
    /// <c>snippet</c> and <c>sharing</c> (private otherwise).
    /// </remarks>
    private static async Task CreateDocumentAsync(
        HttpContext context, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ReadDocumentUploadAsync(context, cancellation).ConfigureAwait(false) is not { } upload)
        {
            return;
        }

        if (!await MayShareAsync(context, upload.Sharing, SharingScope.Private).ConfigureAwait(false))
        {
            return;
        }

        Document made = await documents.CreateAsync(
            upload.Title, upload.Snippet, current.Principal.Id, upload.Sharing, upload.FileName, upload.Kind, upload.Content,
            cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "document.create", made.Id,
            Detail(new { title = made.Title, file = made.FileName, kind = made.Kind.Name, bytes = made.Size, sharing = PostgresSharing(made.Sharing) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.Headers.Location = $"/content/documents/{made.Id}";
        await Results.Json(Describe(current, made), statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces a document's file, keeping its id, its description and its sharing.</summary>
    private static async Task ReplaceDocumentAsync(
        HttpContext context, string id, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "replace", documents, audit, cancellation).ConfigureAwait(false)
            is not { } before)
        {
            return;
        }

        if (await ReadDocumentUploadAsync(context, cancellation).ConfigureAwait(false) is not { } upload)
        {
            return;
        }

        if (!await documents.ReplaceAsync(id, upload.FileName, upload.Kind, upload.Content, cancellation).ConfigureAwait(false)
            || await documents.FindAsync(id, cancellation).ConfigureAwait(false) is not { } after)
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "document.replace", id,
            Detail(new { file = after.FileName, fileBefore = before.FileName, kind = after.Kind.Name, bytes = after.Size }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(current, after)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What a document's update carries: its title, snippet and scope.</summary>
    internal sealed record DocumentUpdateRequest(string? Title, string? Snippet, string? Sharing);

    /// <summary>Replaces a document's title, snippet and scope.</summary>
    private static async Task UpdateDocumentAsync(
        HttpContext context, string id, DocumentUpdateRequest request, IDocumentStore documents, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "change", documents, audit, cancellation).ConfigureAwait(false)
            is not { } before)
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(request.Title) ? before.Title : request.Title.Trim();
        string? snippet = request.Snippet is null ? before.Snippet : request.Snippet.Trim() is { Length: > 0 } s ? s : null;

        if (DescribedRefusal(title, snippet) is { } refusal)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        SharingScope sharing = before.Sharing;

        if (request.Sharing is { Length: > 0 } wanted)
        {
            if (ScopeNamed(wanted) is not { } scope)
            {
                await Refuse(context, 400, "A document's sharing is 'private', 'group', 'organization' or 'public'.").ConfigureAwait(false);
                return;
            }

            sharing = scope;
        }

        if (!await MayShareAsync(context, sharing, before.Sharing).ConfigureAwait(false))
        {
            return;
        }

        if (await documents.UpdateAsync(id, title, snippet, sharing, cancellation).ConfigureAwait(false) is not { } after)
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "document.update", id,
            Detail(new { title = after.Title, sharing = PostgresSharing(after.Sharing), sharingBefore = PostgresSharing(before.Sharing), owner = before.OwnerName }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(Describe(current, after)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Removes a document and its file.</summary>
    internal static async Task DeleteDocumentAsync(
        HttpContext context, string id, IDocumentStore documents, IAuditLog audit, RecycleBinService recycle, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "delete", documents, audit, cancellation).ConfigureAwait(false)
            is not { } before)
        {
            return;
        }

        if (before.DeleteProtected)
        {
            await Refuse(context, 409,
                $"'{before.Title}' is protected from deletion. Its owner or an administrator turns that off on the document's page first.")
                .ConfigureAwait(false);
            return;
        }

        // ADR-203: into the recycle bin, its bytes with it, until it expires.
        (Guid? entry, string? refused) = await recycle.RecycleRowsAsync(
            "item", Guid.Parse(id), Guid.Parse(id), "document", before.Title, before.Owner, current.Principal.Id, cancellation)
            .ConfigureAwait(false);
        bool gone = entry is not null;

        await AuditAsync(context, audit, "document.delete", id,
            Detail(new { title = before.Title, owner = before.OwnerName, recycled = entry }),
            succeeded: gone, cancellation).ConfigureAwait(false);

        if (!gone)
        {
            await Refuse(context, refused is null ? 404 : 409, refused ?? NoDocument(id)).ConfigureAwait(false);
            return;
        }

        await Results.Json(new { deleted = id, title = before.Title, recycled = entry, until = DateTimeOffset.UtcNow + recycle.Retention })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Turns a document's delete protection on or off.</summary>
    private static async Task SetDocumentProtectionAsync(
        HttpContext context, string id, ProtectionRequest request, IDocumentStore documents, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "protect", documents, audit, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        await documents.SetDeleteProtectedAsync(id, request.Protected, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "document.protection", id, Detail(new { @protected = request.Protected }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { id, deleteProtected = request.Protected }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces a document's description.</summary>
    private static async Task SetDocumentDescriptionAsync(
        HttpContext context, string id, WebMapDescriptionRequest request, IDocumentStore documents, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "describe", documents, audit, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        string? text = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (text is { Length: > 64_000 })
        {
            await Refuse(context, 400, "A description is at most 64,000 characters.").ConfigureAwait(false);
            return;
        }

        await documents.SetDescriptionAsync(id, text, cancellation).ConfigureAwait(false);
        await Results.Json(new { id, description = text }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces a document's tags.</summary>
    private static async Task SetDocumentTagsAsync(
        HttpContext context, string id, TagsRequest request, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedDocumentAsync(context, current, id, "tag", documents, audit, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        if (ItemTags.Normalise(request.Tags, out string? why) is not { } tags)
        {
            await Refuse(context, 400, why!).ConfigureAwait(false);
            return;
        }

        await documents.SetTagsAsync(id, tags, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "document.tags", id, Detail(new { tags }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { id, tags }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Gives one document to another member — an administrator's act, as a map's is.</summary>
    private static async Task ChangeDocumentOwnerAsync(
        HttpContext context, string id, OwnerRequest request, IDocumentStore documents, IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageAllContent).ConfigureAwait(false))
        {
            return;
        }

        if (request.To is not { Length: > 0 } receiver)
        {
            await Refuse(context, 400, "'to' names the member who receives the document.").ConfigureAwait(false);
            return;
        }

        switch (await documents.ChangeOwnerAsync(id, receiver, cancellation).ConfigureAwait(false))
        {
            case OwnerChange.NoMember:
                await Refuse(context, 404, $"No member '{receiver}' to give it to.").ConfigureAwait(false);
                return;
            case OwnerChange.NoItem:
                await Refuse(context, 404, "No such document.").ConfigureAwait(false);
                return;
        }

        await AuditAsync(context, audit, "document.owner", id, Detail(new { to = receiver }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new { id, owner = receiver }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static Task ShareDocumentWithGroupAsync(
        HttpContext context, string name, string id, IGroupDirectory groups, IAuditLog audit, CancellationToken cancellation) =>
        ShareDocumentAsync(context, name, id, groups, audit, true, cancellation);

    private static Task UnshareDocumentFromGroupAsync(
        HttpContext context, string name, string id, IGroupDirectory groups, IAuditLog audit, CancellationToken cancellation) =>
        ShareDocumentAsync(context, name, id, groups, audit, false, cancellation);

    /// <summary>Shares a document with a group, or stops — a map's rule (ADR-079 condition 4) for a document.</summary>
    private static async Task ShareDocumentAsync(
        HttpContext context, string name, string id, IGroupDirectory groups, IAuditLog audit, bool wanted,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.GroupsShareTo).ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        GroupChange outcome = await groups.ShareDocumentAsync(
            current.Principal.Id, current.Authorization.Allows(Privilege.AdminManageAllContent), name, id, wanted, cancellation)
            .ConfigureAwait(false);

        if (outcome != GroupChange.Done)
        {
            await RefuseGroupChangeAsync(context, name, outcome, "the document").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, wanted ? "group.share.document" : "group.unshare.document", name,
            Detail(new { document = id }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            group = name,
            document = id,
            shared = wanted,
            note = wanted
                ? "The document's own sharing must be 'group' as well before the group's members can open it."
                : "Its sharing is unchanged.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The document, when this caller may change it; otherwise writes the refusal and answers null.</summary>
    /// <remarks>404 when they cannot read it, 403 when they can read it and not change it — a map's rule.</remarks>
    private static async Task<Document?> ManagedDocumentAsync(
        HttpContext context,
        RequestPrincipal current,
        string id,
        string verb,
        IDocumentStore documents,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await documents.FindAsync(id, cancellation).ConfigureAwait(false) is not { } document || !Readable(current, document))
        {
            await Refuse(context, 404, NoDocument(id)).ConfigureAwait(false);
            return null;
        }

        if (!LayerAccess.MayManage(document.Owner, current.Principal, current.Authorization))
        {
            await AuditAsync(
                context, audit, $"document.{(verb == "delete" ? "delete" : "update")}", id,
                Detail(new { refused = "not the owner", owner = document.OwnerName }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 403,
                $"Only the document's owner, {document.OwnerName}, or an administrator can {verb} '{document.Title}'.")
                .ConfigureAwait(false);
            return null;
        }

        return document;
    }

    private static bool Readable(RequestPrincipal current, Document document) =>
        LayerAccess.Evaluate(document.Sharing, document.Owner, current.Principal, current.Authorization, document.SharedWith).IsAllowed();

    private static string NoDocument(string id) =>
        $"No document '{id}' that you can open. It may have been deleted, or it is not shared with you.";

    /// <summary>A document's description, as every answer here carries it.</summary>
    private static object Describe(RequestPrincipal current, Document document)
    {
        bool manages = LayerAccess.MayManage(document.Owner, current.Principal, current.Authorization);

        return new
        {
            id = document.Id,
            title = document.Title,
            snippet = document.Snippet,
            owner = current.Principal.IsAnonymous ? null : document.OwnerName,
            sharing = PostgresSharing(document.Sharing),
            fileName = document.FileName,
            kind = document.Kind.Name,
            type = document.Kind.ArcGisType,
            size = document.Size,
            created = document.Created,
            modified = document.Modified,
            mine = !current.Principal.IsAnonymous && document.Owner == current.Principal.Id,
            manages,
            deleteProtected = document.DeleteProtected,
            tags = document.Tags ?? [],
            contentFolder = document.ContentFolder,
            description = document.Description,
            data = $"/content/documents/{document.Id}/data",
            thumbnail = document.HasThumbnail ? $"/content/documents/{document.Id}/thumbnail" : null,
            groups = manages ? document.SharedWithNames ?? [] : null,
        };
    }

    /// <summary>What an upload carries, once it has been read and recognised.</summary>
    private sealed record DocumentUpload(
        string Title, string? Snippet, SharingScope Sharing, string FileName, DocumentKind Kind, byte[] Content);

    /// <summary>This deployment's document limit.</summary>
    internal static long MaximumDocumentBytes(HttpContext context) =>
        (context.RequestServices?.GetService(typeof(HostSettings)) as HostSettings)?.DocumentMaximumBytes
        ?? Documents.DefaultMaximumBytes;

    /// <summary>
    /// Reads a multipart upload, recognises its file and checks the fields around it; writes the refusal and answers
    /// null when any of it fails.
    /// </summary>
    private static async Task<DocumentUpload?> ReadDocumentUploadAsync(HttpContext context, CancellationToken cancellation)
    {
        long limit = MaximumDocumentBytes(context);

        if (!context.Request.HasFormContentType)
        {
            await Refuse(context, 400, "Upload the document as multipart/form-data with a 'file' part.").ConfigureAwait(false);
            return null;
        }

        // The document's own limit, not the data upload's: the two are separate settings.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = limit + (1024 * 1024);
        }

        IFormCollection form;

        try
        {
            form = await context.Request.ReadFormAsync(
                new FormOptions { MultipartBodyLengthLimit = limit + (1024 * 1024), ValueLengthLimit = 64 * 1024 },
                cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            await Refuse(context, 413, DocumentTooLarge(limit)).ConfigureAwait(false);
            return null;
        }

        if (form.Files.GetFile("file") is not { } file || file.Length == 0)
        {
            await Refuse(context, 400, "A 'file' part is required, and it may not be empty.").ConfigureAwait(false);
            return null;
        }

        if (file.Length > limit)
        {
            await Refuse(context, 413, DocumentTooLarge(limit)).ConfigureAwait(false);
            return null;
        }

        byte[] content = new byte[file.Length];

        await using (Stream stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, cancellation).ConfigureAwait(false);
        }

        if (Documents.Recognise(file.FileName, content.AsSpan(0, Math.Min(content.Length, 4096)), out string? why) is not { } kind)
        {
            await Refuse(context, 415, why!).ConfigureAwait(false);
            return null;
        }

        string fileName = Documents.CleanFileName(file.FileName, kind);
        string title = form["title"].ToString().Trim() is { Length: > 0 } named ? named : Path.GetFileNameWithoutExtension(fileName);
        string? snippet = form["snippet"].ToString().Trim() is { Length: > 0 } said ? said : null;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = fileName;
        }

        if (DescribedRefusal(title, snippet) is { } refusal)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return null;
        }

        SharingScope sharing = SharingScope.Private;

        if (form["sharing"].ToString() is { Length: > 0 } wanted)
        {
            if (ScopeNamed(wanted) is not { } scope)
            {
                await Refuse(context, 400, "A document's sharing is 'private', 'group', 'organization' or 'public'.").ConfigureAwait(false);
                return null;
            }

            sharing = scope;
        }

        return new DocumentUpload(title, snippet, sharing, fileName, kind, content);
    }

    internal static string? DescribedRefusal(string title, string? snippet) =>
        title.Length > WebMaps.MaximumTitleLength ? $"A title is at most {WebMaps.MaximumTitleLength} characters."
        : snippet is { Length: > WebMaps.MaximumSnippetLength } ? $"A snippet is at most {WebMaps.MaximumSnippetLength} characters."
        : null;

    private static SharingScope? ScopeNamed(string text) => text.Trim().ToLowerInvariant() switch
    {
        "private" => SharingScope.Private,
        "group" or "shared" => SharingScope.Group,
        "organization" or "org" => SharingScope.Organization,
        "allorganizations" => SharingScope.AllOrganizations,
        "public" => SharingScope.Public,
        _ => null,
    };

    internal static string DocumentTooLarge(long limit) =>
        $"A document may be at most {limit / 1048576} MB on this server. A deployment raises the limit with Graticula:DocumentMaxMB.";
}
