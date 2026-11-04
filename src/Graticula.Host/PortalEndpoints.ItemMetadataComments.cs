using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// An item's metadata document and the comments members leave on it (ADR-267, the ArcGIS veteran's review of v1.0.499,
/// #18): Portal's <c>info/metadata/metadata.xml</c> and <c>update</c>'s <c>metadata</c> part, and its <c>comments</c>,
/// <c>addComment</c>, <c>comments/{id}</c>, <c>update</c> and <c>delete</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Metadata is stored and served, never generated.</b> The ArcGIS API for Python reads an item's metadata as absent when
/// this address refuses, and an item with metadata says <c>Metadata</c> in its type keywords; a document made up from the
/// item's facts would tell every client that metadata had been written when nobody wrote any.
/// </para>
/// <para>
/// <b>Uploaded XML is parsed with DTDs prohibited and no resolver</b>, at most <see cref="MaximumMetadataBytes"/>, and kept
/// byte for byte as sent once it parses. It is served with <c>nosniff</c> and a sandboxing CSP, so an XHTML-bearing document
/// is never a page of this site.
/// </para>
/// <para>
/// <b>Comments follow the item</b>: whoever may read it reads them; whoever may read it and is signed in may add one, while
/// the organisation has comments on; the comment's author, the item's owner and whoever may manage all content may delete
/// one. Text is kept as written and escaped wherever Studio shows it.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The largest metadata document an item keeps: 4 MB, as migration 131's check says.</summary>
    internal const int MaximumMetadataBytes = 4 * 1024 * 1024;

    /// <summary>The longest comment: 2,048 characters, as migration 131's check says.</summary>
    internal const int MaximumCommentLength = 2048;

    /// <summary>The most comments one item holds.</summary>
    internal const int MaximumCommentsPerItem = 10_000;

    /// <summary>The organisation's setting that turns comments off (ADR-267); on when it is not set.</summary>
    internal const string CommentsSetting = "item_comments";

    /// <summary>What a portal says when an item has no metadata, which the ArcGIS API for Python reads as none.</summary>
    private const string NoMetadata = "Metadata for item not found.";

    private static void MapItemMetadataAndComments(WebApplication app)
    {
        Discoverable(app, $"{Path}/content/items/{{id}}/info/metadata/metadata.xml", ItemMetadataAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        Discoverable(app, $"{Path}/content/items/{{id}}/comments", ItemCommentsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        Discoverable(app, $"{Path}/content/items/{{id}}/comments/{{commentId}}", ItemCommentAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapPost($"{Path}/content/items/{{id}}/addComment", AddItemCommentAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/items/{{id}}/comments/{{commentId}}/update", UpdateItemCommentAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/items/{{id}}/comments/{{commentId}}/delete", DeleteItemCommentAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    /// <summary>
    /// Why an uploaded metadata document is refused — empty, too large, not well-formed XML, a DTD, or a root that is not
    /// an ArcGIS, FGDC or ISO 19139 metadata element — or null when it is kept as sent.
    /// </summary>
    internal static string? MetadataRefusal(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            return "the metadata file is empty.";
        }

        if (content.Length > MaximumMetadataBytes)
        {
            return $"a metadata file is at most {MaximumMetadataBytes / (1024 * 1024)} MB.";
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = (long)MaximumMetadataBytes * 2,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };

        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(content, writable: false), settings);

            if (reader.MoveToContent() != XmlNodeType.Element
                || reader.LocalName is not ("metadata" or "MD_Metadata" or "MI_Metadata"))
            {
                return "metadata is an ArcGIS or FGDC <metadata> document or an ISO 19139 <MD_Metadata> one.";
            }

            while (reader.Read())
            {
                // Read to the end, so a document that is not well-formed further on is refused here and not kept.
            }

            return null;
        }
        catch (XmlException e)
        {
            // One sentence per cause, and none of the parser's own text: it named settings only this server can reach
            // (ADR-267's design review, 2026-11-04).
            string text = System.Text.Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 4096));
            if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
            {
                return "it declares a DTD (a <!DOCTYPE …> line), which is not accepted. Remove that line and upload it again.";
            }

            return text.TrimStart((char)0xFEFF).TrimStart().StartsWith('<')
                ? $"it is not well-formed XML: line {e.LineNumber}, column {e.LinePosition}."
                : "it is not an XML file. Choose the .xml file ArcGIS Pro exports for the item.";
        }
    }

    /// <summary>The <c>metadata</c> an update was sent — as a file part or a text field — or null when it was sent none.</summary>
    internal static async Task<(byte[]? Content, string? Refusal)> MetadataSentAsync(IFormCollection form, CancellationToken cancellation)
    {
        byte[] content;

        if (form.Files.GetFile("metadata") is { } file)
        {
            if (file.Length > MaximumMetadataBytes)
            {
                return (null, $"a metadata file is at most {MaximumMetadataBytes / (1024 * 1024)} MB.");
            }

            content = new byte[file.Length];
            await using Stream stream = file.OpenReadStream();
            await stream.ReadExactlyAsync(content, cancellation).ConfigureAwait(false);
        }
        else if (form.TryGetValue("metadata", out Microsoft.Extensions.Primitives.StringValues text) && text.ToString().Length > 0)
        {
            content = System.Text.Encoding.UTF8.GetBytes(text.ToString());
        }
        else
        {
            return (null, null);
        }

        return MetadataRefusal(content) is { } why ? (null, why) : (content, null);
    }

    /// <summary>
    /// The item document with what this file adds: <c>numComments</c>, and <c>Metadata</c> among its type keywords when it
    /// carries metadata, as Portal says it. The item as it was when the stores are not here or not reachable.
    /// </summary>
    internal static async Task<object> WithCommentsAndMetadataAsync(
        HttpContext context, object item, Guid itemGuid, CancellationToken cancellation) =>
        (await WithCommentsAndMetadataAsync(context, [(item, itemGuid)], cancellation).ConfigureAwait(false))[0];

    /// <summary>
    /// Many item documents with what this file adds, read in two queries whatever their number — ADR-267 condition 2. A
    /// listing calls this before it filters and sorts, so <c>typekeywords:Metadata</c> and <c>sortField=numComments</c>
    /// see what the item's own page says. An entry without an id is returned as it was.
    /// </summary>
    internal static async Task<IReadOnlyList<object>> WithCommentsAndMetadataAsync(
        HttpContext context, IReadOnlyList<(object Item, Guid? Id)> items, CancellationToken cancellation)
    {
        if (items.Count == 0
            || context.RequestServices.GetService<IItemCommentStore>() is not { } comments
            || context.RequestServices.GetService<IItemMetadataStore>() is not { } metadata)
        {
            return [.. items.Select(entry => entry.Item)];
        }

        Guid[] ids = [.. items.Where(entry => entry.Id is not null).Select(entry => entry.Id!.Value).Distinct()];

        IReadOnlyDictionary<Guid, int> counted;
        IReadOnlySet<Guid> described;

        try
        {
            counted = await comments.CountAsync(ids, cancellation).ConfigureAwait(false);
            described = await metadata.WhichHaveAsync(ids, cancellation).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            return [.. items.Select(entry => entry.Item)];
        }

        List<object> result = new(items.Count);

        foreach ((object item, Guid? id) in items)
        {
            if (id is not { } itemGuid)
            {
                result.Add(item);
                continue;
            }

            JsonObject node = item as JsonObject ?? System.Text.Json.JsonSerializer.SerializeToNode(item, ItemJson)!.AsObject();
            node["numComments"] = counted.GetValueOrDefault(itemGuid);

            if (described.Contains(itemGuid))
            {
                JsonArray keywords = node["typeKeywords"] as JsonArray ?? [];
                if (!keywords.Any(k => string.Equals(k?.GetValue<string>(), "Metadata", StringComparison.Ordinal)))
                {
                    keywords.Add("Metadata");
                }

                node["typeKeywords"] = keywords.DeepClone();
            }

            result.Add(node);
        }

        return result;
    }

    /// <summary>An item id as a listing carries it, or null for one that is not a stored item's (a gallery basemap).</summary>
    internal static Guid? ItemGuidOf(string? id) => Guid.TryParse(id, out Guid parsed) ? parsed : null;

    /// <summary><c>info/metadata/metadata.xml</c>: the item's metadata as it was written, to whoever may read the item.</summary>
    private static async Task<IResult> ItemMetadataAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ReadableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        if (await context.RequestServices.GetRequiredService<IItemMetadataStore>().FindAsync(item.Item, cancellation)
                .ConfigureAwait(false) is not { } found)
        {
            return Results.Json(new { error = new { code = 400, message = NoMetadata, details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Studio's Download asks first, as its other downloads do (AdminEndpoints.Documents).
        if (context.Request.Query["check"] == "1")
        {
            return Results.Json(new { fileName = "metadata.xml", bytes = found.Content.Length });
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return Results.File(found.Content, "application/xml", "metadata.xml", lastModified: found.Modified);
    }

    /// <summary>Whether the organisation has comments on — the setting, or on when nobody set it.</summary>
    internal static async Task<bool> CommentsEnabledAsync(HttpContext context, CancellationToken cancellation)
    {
        if (context.RequestServices.GetService<IServerSettingStore>() is not { } settings)
        {
            return true;
        }

        try
        {
            return (await settings.ReadAsync(CommentsSetting, cancellation).ConfigureAwait(false))?.Value is not "off";
        }
        catch (System.Data.Common.DbException)
        {
            return true;
        }
    }

    /// <summary>A comment as Portal writes one.</summary>
    private static object CommentJson(ItemComment comment) => new
    {
        id = comment.Id.ToString("N"),
        owner = comment.AuthorName,
        created = comment.Created.ToUnixTimeMilliseconds(),
        comment = comment.Text,
    };

    /// <summary><c>items/{id}/comments</c>: a page of the item's comments, oldest first, to whoever may read the item.</summary>
    private static async Task<IResult> ItemCommentsAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (await ReadableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        int start = int.TryParse(await FormFieldAsync(context, "start", cancellation).ConfigureAwait(false), out int s) && s > 0 ? s : 1;
        int num = int.TryParse(await FormFieldAsync(context, "num", cancellation).ConfigureAwait(false), out int n) ? Math.Clamp(n, 1, 100) : 10;

        (IReadOnlyList<ItemComment> page, int total) = await context.RequestServices.GetRequiredService<IItemCommentStore>()
            .ListAsync(item.Item, start - 1, num, cancellation).ConfigureAwait(false);

        return Results.Ok(new
        {
            total,
            start,
            num,
            nextStart = start - 1 + page.Count < total ? start + page.Count : -1,
            comments = page.Select(CommentJson),
        });
    }

    /// <summary>One comment, and the item it is on, if the caller may read the item; null otherwise.</summary>
    private static async Task<((Guid Item, Guid? Owner) Item, ItemComment Comment)?> ReadableCommentAsync(
        HttpContext context, string id, string commentId, CancellationToken cancellation)
    {
        if (!Guid.TryParse(commentId, out Guid comment)
            || await ReadableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item
            || await context.RequestServices.GetRequiredService<IItemCommentStore>().FindAsync(item.Item, comment, cancellation)
                .ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        return (item, found);
    }

    /// <summary><c>items/{id}/comments/{commentId}</c>: one comment.</summary>
    private static async Task<IResult> ItemCommentAsync(HttpContext context, string id, string commentId, CancellationToken cancellation) =>
        await ReadableCommentAsync(context, id, commentId, cancellation).ConfigureAwait(false) is { } found
            ? Results.Ok(CommentJson(found.Comment))
            : Unknown("Comment");

    /// <summary>Why this text cannot be a comment, or null.</summary>
    internal static string? CommentRefusal(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "A comment needs its 'comment', the text."
        : text.Length > MaximumCommentLength ? $"A comment is at most {MaximumCommentLength:N0} characters."
        : null;

    /// <summary>
    /// <c>addComment</c>: a signed-in member who may read the item leaves a comment on it, while the organisation has
    /// comments on.
    /// </summary>
    private static async Task<IResult> AddItemCommentAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to comment from another site.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ReadableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        if (current.Principal.IsAnonymous)
        {
            return PortalError(403, "Sign in to comment on an item.");
        }

        if (!await CommentsEnabledAsync(context, cancellation).ConfigureAwait(false))
        {
            return PortalError(403, "Comments are turned off in this organization.");
        }

        string? text = await FormFieldAsync(context, "comment", cancellation).ConfigureAwait(false);

        if (CommentRefusal(text) is { } why)
        {
            return PortalError(400, why);
        }

        ItemComment? made = await context.RequestServices.GetRequiredService<IItemCommentStore>()
            .AddAsync(item.Item, current.Principal.Id, current.Principal.Name, text!, MaximumCommentsPerItem, cancellation)
            .ConfigureAwait(false);

        return made is null
            ? PortalError(400, $"An item holds at most {MaximumCommentsPerItem:N0} comments.")
            : Results.Ok(new { success = true, commentId = made.Id.ToString("N") });
    }

    /// <summary><c>comments/{commentId}/update</c>: its author changes its text.</summary>
    private static async Task<IResult> UpdateItemCommentAsync(
        HttpContext context, string id, string commentId, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to change a comment from another site.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ReadableCommentAsync(context, id, commentId, cancellation).ConfigureAwait(false) is not { } found)
        {
            return Unknown("Comment");
        }

        if (current.Principal.IsAnonymous || found.Comment.Author != current.Principal.Id)
        {
            return PortalError(403, "Only the comment's author may change it.");
        }

        string? text = await FormFieldAsync(context, "comment", cancellation).ConfigureAwait(false);

        if (CommentRefusal(text) is { } why)
        {
            return PortalError(400, why);
        }

        await context.RequestServices.GetRequiredService<IItemCommentStore>()
            .UpdateAsync(found.Item.Item, found.Comment.Id, text!, cancellation).ConfigureAwait(false);
        return Results.Ok(new { success = true, commentId = found.Comment.Id.ToString("N") });
    }

    /// <summary>
    /// <c>comments/{commentId}/delete</c>: by the comment's author, the item's owner, or whoever may manage all content.
    /// </summary>
    private static async Task<IResult> DeleteItemCommentAsync(
        HttpContext context, string id, string commentId, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to delete a comment from another site.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ReadableCommentAsync(context, id, commentId, cancellation).ConfigureAwait(false) is not { } found)
        {
            return Unknown("Comment");
        }

        if (!MayDeleteComment(current, found.Item.Owner, found.Comment))
        {
            return PortalError(403, "A comment is deleted by its author, the item's owner or an administrator.");
        }

        await context.RequestServices.GetRequiredService<IItemCommentStore>()
            .RemoveAsync(found.Item.Item, found.Comment.Id, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, context.RequestServices.GetRequiredService<IAuditLog>(), "item.comment.delete",
            found.Item.Item.ToString("N"), AdminEndpoints.Detail(new { comment = found.Comment.Id.ToString("N"), author = found.Comment.AuthorName }),
            succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Ok(new { success = true, commentId = found.Comment.Id.ToString("N") });
    }

    /// <summary>Whether a caller may delete a comment: its author, the item's owner, or whoever may manage all content.</summary>
    internal static bool MayDeleteComment(RequestPrincipal current, Guid? itemOwner, ItemComment comment) =>
        !current.Principal.IsAnonymous
        && (comment.Author == current.Principal.Id || itemOwner == current.Principal.Id
            || current.Authorization.Allows(Privilege.AdminManageAllContent));

    /// <summary>A field from the form or, failing that, the query.</summary>
    private static async Task<string?> FormFieldAsync(HttpContext context, string name, CancellationToken cancellation)
    {
        if (context.Request.HasFormContentType)
        {
            IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
            if (form.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value))
            {
                return value.ToString();
            }
        }

        return context.Request.Query.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues query) ? query.ToString() : null;
    }
}
