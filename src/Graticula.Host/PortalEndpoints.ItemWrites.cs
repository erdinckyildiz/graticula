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
/// Portal's item writes — <c>update</c>, <c>updateItems</c>, <c>delete</c>, <c>deleteItems</c>, <c>unshare</c> and
/// <c>unshareItems</c> — answered by what this portal actually keeps, and refused, by name, for what it does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's second review, N2 (2026-10-21): the write API reported success for changes it never
/// made.</b> <c>update</c> answered <c>success:true</c> for any id that was not a web map — a service, a document, an
/// id that does not exist — and a web map's <c>text</c> was never read, so the JS SDK's <c>WebMap.save()</c> and the
/// Python API's <c>item.update(data=…)</c> reported success and lost the change. A silent success is worse than a
/// refusal: the script believes it worked. Delete and unshare were absent, so a client was refused by a 404 that named
/// nothing.
/// </para>
/// <para>
/// <b>An update is all or nothing.</b> Each field is either one this portal keeps for that kind of item — and is
/// written — or one a client echoes back from the item it read and this portal derives itself (<c>type</c>,
/// <c>typeKeywords</c>, <c>url</c>, <c>extent</c> …), which is passed over, or neither, and then nothing is written and
/// the answer names it. The echo list exists because the JS SDK's <c>PortalItem.update</c> sends every property it
/// read; refusing those would refuse every save.
/// </para>
/// <para>
/// <b>Delete is the console's delete</b>, called rather than rewritten, so delete protection, a source's views, what
/// this server keeps beside a registered layer and the recycle bin (ADR-203) hold through this door as through the
/// other. A <c>permanentDelete</c> was recycled all the same until the bin had a screen; since ADR-214 condition 1 it
/// is purged from the bin straight after, as Enterprise's is.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>
    /// What a client sends back from an item it read and this portal derives, so an update passes it over rather than
    /// refusing a save that merely echoed it; and the request's own controls.
    /// </summary>
    private static readonly HashSet<string> EchoedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "f", "token", "clearEmptyFields", "async",
        "type", "typeKeywords", "url", "extent", "spatialReference", "culture", "properties", "name", "id", "owner",
        "orgId", "access", "created", "modified", "size", "numViews", "numRatings", "avgRating", "numComments",
        "thumbnailUrl", "largeThumbnail", "banner", "screenshots", "listed", "commentsEnabled", "protected",
        "contentStatus", "groupDesignations", "apiToken1ExpirationDate", "apiToken2ExpirationDate", "industries",
        "languages", "proxyFilter", "documentation", "appCategories", "ownerFolder", "isOrgItem", "itemControl",
        "scoreCompleteness", "guid", "lastViewed", "classification",
    };

    /// <summary>How long after Pro published a service its delete of that service is read as Pro's rollback.</summary>
    private static readonly TimeSpan ProRollbackWindow = TimeSpan.FromMinutes(15);

    private static void MapItemWrites(WebApplication app)
    {
        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/items/{{id}}/update", UpdateUserItemAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapPost($"{user}/items/{{id}}/unshare", UnshareItemAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

            // While Pro's calls are recorded, its delete is recorded instead (ProCapture), as it was.
            if (!RecordingPro(app.Configuration))
            {
                app.MapPost($"{user}/items/{{id}}/delete", DeleteUserItemAsync)
                    .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            }
        }

        app.MapPost($"{Path}/content/users/{{username}}/deleteItems", DeleteItemsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/unshareItems", UnshareItemsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
    }

    /// <summary>What an item id names, as this portal can change it: a service, a web map or a document (ADR-214 condition 3).</summary>
    private sealed record WritableItem(PublishedService? Service, WebMap? Map, Document? Document = null)
    {
        public Guid ItemGuid => Service?.Id ?? Guid.Parse(Map?.Id ?? Document!.Id);

        public Guid? Owner => Service is not null ? Service.Owner : Map is not null ? Map.Owner : Document!.Owner;

        public string Title => Service?.Name ?? Map?.Title ?? Document!.Title;

        public string Kind => Service is not null ? "service" : Map is not null ? "web map" : "document";
    }

    /// <summary>The service — by any of its faces' item ids — or the web map an id names; null when it names neither.</summary>
    private static async Task<WritableItem?> WritableItemAsync(
        HttpContext context, string id, CancellationToken cancellation)
    {
        PostgresLayerCatalog published = context.RequestServices.GetRequiredService<PostgresLayerCatalog>();

        foreach (PublishedService service in await published.ListServicesAsync(cancellation).ConfigureAwait(false))
        {
            if (string.Equals(ItemId(service), id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(FaceItemId(service, "FeatureServer"), id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(FaceItemId(service, "VectorTileServer"), id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(FaceItemId(service, "MapServer"), id, StringComparison.OrdinalIgnoreCase))
            {
                return new WritableItem(service, null);
            }
        }

        IWebMapStore maps = context.RequestServices.GetRequiredService<IWebMapStore>();

        if (WebMaps.IsId(id) && await maps.FindAsync(id, cancellation).ConfigureAwait(false) is { } map)
        {
            return new WritableItem(null, map);
        }

        // A file a member uploaded (ADR-188): it answered "does not exist" here until ADR-214 condition 3.
        IDocumentStore documents = context.RequestServices.GetRequiredService<IDocumentStore>();

        return Guid.TryParse(id, out _) && await documents.FindAsync(id, cancellation).ConfigureAwait(false) is { } document
            ? new WritableItem(null, null, document)
            : null;
    }

    /// <summary>Whether the caller may change an item: its owner, or whoever may manage all content.</summary>
    private static bool Manages(RequestPrincipal current, WritableItem item) =>
        item.Owner == current.Principal.Id || current.Authorization.Allows(Privilege.AdminManageAllContent);

    /// <summary>
    /// Portal's item <c>update</c>: for a service, its description, tags, credits, terms of use and categories; for a web
    /// map, its title, snippet, description, tags, picture, terms, categories and — as <c>text</c> — its document.
    /// </summary>
    private static async Task<IResult> UpdateUserItemAsync(
        HttpContext context, IAdminCatalog catalog, IWebMapStore maps, IDocumentStore documents, IItemAnnotationStore annotations,
        ItemAnnotations held, IAuditLog audit, string username, string id, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        if (await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return Unknown("Item");
        }

        if (!Manages(current, item))
        {
            return PortalError(403, $"'{item.Title}' is not yours to change.");
        }

        // Read once the item is known: a document's file may be as large as a document may be (ADR-188), past the
        // request body's ordinary limit.
        long documentLimit = AdminEndpoints.MaximumDocumentBytes(context);
        IFormCollection form;

        if (!context.Request.HasFormContentType)
        {
            form = FormCollection.Empty;
        }
        else
        {
            if (item.Document is not null
                && context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            {
                bodyLimit.MaxRequestBodySize = documentLimit + (1024 * 1024);
            }

            try
            {
                form = await context.Request.ReadFormAsync(
                    new Microsoft.AspNetCore.Http.Features.FormOptions
                    {
                        MultipartBodyLengthLimit = item.Document is not null ? documentLimit + (1024 * 1024) : 128L * 1024 * 1024,
                    },
                    cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            {
                return PortalError(413, $"'{item.Title}' was not changed: {AdminEndpoints.DocumentTooLarge(documentLimit)}");
            }
        }

        Dictionary<string, string> sent = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> field in form)
        {
            sent[field.Key] = field.Value.ToString();
        }

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> field in context.Request.Query)
        {
            sent.TryAdd(field.Key, field.Value.ToString());
        }

        HashSet<string> kept = item.Service is not null
            ? new(StringComparer.OrdinalIgnoreCase) { "title", "snippet", "description", "tags", "accessInformation", "licenseInfo", "categories" }
            : item.Map is not null
                ? new(StringComparer.OrdinalIgnoreCase) { "title", "snippet", "description", "tags", "licenseInfo", "categories", "text", "thumbnail" }
                : new(StringComparer.OrdinalIgnoreCase) { "title", "snippet", "description", "tags", "licenseInfo", "categories", "file", "thumbnail" };

        string[] refused = [.. sent.Keys.Where(k => !kept.Contains(k) && !EchoedFields.Contains(k)).Order(StringComparer.Ordinal)];

        // A picture is a file, not a field; for a service it is drawn from its layers, never kept. A document keeps its
        // file and, since D-321, a picture of its own.
        if (item.Service is not null && form.Files.Count > 0)
        {
            refused = [.. refused, .. form.Files.Select(f => f.Name)];
        }
        else if (item.Document is not null)
        {
            refused = [.. refused, .. form.Files
                .Where(f => f.Name != "file" && !f.Name.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Name)];
        }

        if (refused.Length > 0)
        {
            return PortalError(400,
                $"'{item.Title}' was not changed: this portal does not keep {string.Join(", ", refused.Select(r => $"'{r}'"))} "
                + $"for a {item.Kind} item, and an update is made whole or not at all.");
        }

        // Categories and terms are checked before anything is written, so a bad one changes nothing.
        IReadOnlyList<Guid>? categories = null;

        if (sent.TryGetValue("categories", out string? named))
        {
            if (CategoriesFrom(named, held, out string? unknown) is not { } found)
            {
                return PortalError(400, $"'{item.Title}' was not changed: {unknown}");
            }

            categories = found;
        }

        if (sent.TryGetValue("licenseInfo", out string? license) && license.Length > AdminEndpoints.MaximumLicenseLength)
        {
            return PortalError(400, $"'{item.Title}' was not changed: terms of use are at most {AdminEndpoints.MaximumLicenseLength:N0} characters.");
        }

        string? Given(string key) => sent.TryGetValue(key, out string? value) ? value.Trim() : null;

        if (item.Document is { } file)
        {
            if (await UpdateDocumentAsync(file, Given, form, documentLimit, documents, cancellation).ConfigureAwait(false) is { } why)
            {
                return why;
            }
        }
        else if (item.Service is { } service)
        {
            // A service's title is its name, which an update does not rename; the same title echoed back is no change.
            if (Given("title") is { Length: > 0 } title && !string.Equals(title, service.Name, StringComparison.Ordinal))
            {
                return PortalError(400,
                    $"'{service.Name}' was not changed: a service item's title is its service's name here, and renaming a "
                    + "service is not something an item update does.");
            }

            // One text is both the summary and the description of a service here (Item: snippet = description), so the
            // description wins where both are sent and differ from what is held, and a changed summary otherwise.
            string? text = Given("description") is { } description && description != (service.Description ?? string.Empty)
                ? description
                : Given("snippet") is { } snippet && snippet != (service.Description ?? string.Empty) ? snippet : null;

            if (text is { Length: > AdminEndpoints.MaximumDescriptionLength })
            {
                return PortalError(400, $"'{service.Name}' was not changed: a description is at most {AdminEndpoints.MaximumDescriptionLength:N0} characters.");
            }

            if (Given("accessInformation") is { Length: > AdminEndpoints.MaximumCreditsLength })
            {
                return PortalError(400, $"'{service.Name}' was not changed: credits are at most {AdminEndpoints.MaximumCreditsLength:N0} characters.");
            }

            IReadOnlyList<string>? tags = null;

            if (Given("tags") is { } tagText)
            {
                tags = ItemTags.Normalise(tagText.Split(','), out string? why);

                if (tags is null)
                {
                    return PortalError(400, $"'{service.Name}' was not changed: {why}");
                }

                // The folder is shown as a tag (Item); it is not one of the service's own.
                tags = [.. tags.Where(t => service.Folder is null || !string.Equals(t, service.Folder, StringComparison.OrdinalIgnoreCase))];
            }

            if (text is not null)
            {
                await catalog.SetServiceDescriptionAsync(service.Name, service.Folder, text.Length == 0 ? null : text, cancellation)
                    .ConfigureAwait(false);
            }

            if (tags is not null)
            {
                await catalog.SetServiceTagsAsync(service.Name, service.Folder, tags, cancellation).ConfigureAwait(false);
            }

            if (Given("accessInformation") is { } credits && credits != (service.CopyrightText ?? string.Empty))
            {
                await catalog.SetServiceCreditsAsync(service.Name, service.Folder, credits.Length == 0 ? null : credits, cancellation)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            WebMap map = item.Map!;
            string? title = Given("title");
            string? snippet = Given("snippet");

            if (title is { Length: > WebMaps.MaximumTitleLength } || snippet is { Length: > WebMaps.MaximumSnippetLength })
            {
                return PortalError(400,
                    $"'{map.Title}' was not changed: a title is 1 to {WebMaps.MaximumTitleLength} characters and a summary at most "
                    + $"{WebMaps.MaximumSnippetLength}.");
            }

            // <b>The document, which is what WebMap.save() sends</b> — an ArcGIS Web Map, held as Studio holds one.
            string document = map.Document ?? "{}";

            if (Given("text") is { Length: > 0 } sentText)
            {
                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(sentText);

                    if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return PortalError(400, $"'{map.Title}' was not changed: 'text' is an ArcGIS Web Map, a JSON object.");
                    }
                }
                catch (JsonException e)
                {
                    return PortalError(400, $"'{map.Title}' was not changed: 'text' is not JSON ({e.Message}).");
                }

                if (Encoding.UTF8.GetByteCount(sentText) > WebMaps.MaximumDocumentBytes)
                {
                    return PortalError(400, $"'{map.Title}' was not changed: the document is larger than this portal keeps.");
                }

                document = sentText;
            }

            if (title is { Length: > 0 } || snippet is not null || !ReferenceEquals(document, map.Document ?? "{}"))
            {
                await maps.UpdateAsync(map.Id,
                    title is { Length: > 0 } ? title : map.Title,
                    snippet is null ? map.Snippet : snippet.Length == 0 ? null : snippet,
                    map.Sharing, document, cancellation).ConfigureAwait(false);
            }

            if (Given("tags") is { Length: > 0 } tagText)
            {
                if (ItemTags.Normalise(tagText.Split(','), out string? why) is not { } tags)
                {
                    return PortalError(400, $"'{map.Title}': {why}");
                }

                await maps.SetTagsAsync(map.Id, tags, cancellation).ConfigureAwait(false);
            }

            if (Given("description") is { } description)
            {
                await maps.SetDescriptionAsync(map.Id, description.Length == 0 ? null : description, cancellation).ConfigureAwait(false);
            }

            await KeepThumbnailAsync(form, maps, map.Id, cancellation).ConfigureAwait(false);
        }

        if (license is not null)
        {
            await annotations.SetLicenseAsync(item.ItemGuid, license.Trim().Length == 0 ? null : license.Trim(), cancellation)
                .ConfigureAwait(false);
        }

        if (categories is not null)
        {
            await annotations.SetCategoriesAsync(item.ItemGuid, categories, cancellation).ConfigureAwait(false);
        }

        if (license is not null || categories is not null)
        {
            await held.RefreshAsync(cancellation).ConfigureAwait(false);
        }

        await AdminEndpoints.AuditAsync(
            context, audit, item.Service is not null ? "service.update" : item.Map is not null ? "webmap.update" : "document.update", id,
            AdminEndpoints.Detail(new { from = "portal", fields = sent.Keys.Where(kept.Contains).Order(StringComparer.Ordinal) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true, id });
    }

    /// <summary>
    /// A document's part of an item update — ADR-214 condition 3: its title, summary, description, tags and file, checked
    /// whole before any is written, by the console's own rules (ADR-188). Null when it is done, or the refusal.
    /// </summary>
    private static async Task<IResult?> UpdateDocumentAsync(
        Document document, Func<string, string?> given, IFormCollection form, long limit, IDocumentStore documents,
        CancellationToken cancellation)
    {
        string title = given("title") is { Length: > 0 } named ? named : document.Title;
        string? snippet = given("snippet") is { } said ? (said.Length == 0 ? null : said) : document.Snippet;

        if (AdminEndpoints.DescribedRefusal(title, snippet) is { } described)
        {
            return PortalError(400, $"'{document.Title}' was not changed: {described}");
        }

        string? description = given("description");

        if (description is { Length: > 64_000 })
        {
            return PortalError(400, $"'{document.Title}' was not changed: a description is at most 64,000 characters.");
        }

        IReadOnlyList<string>? tags = null;

        if (given("tags") is { } tagText && (tags = ItemTags.Normalise(tagText.Split(','), out string? why)) is null)
        {
            return PortalError(400, $"'{document.Title}' was not changed: {why}");
        }

        (string FileName, DocumentKind Kind, byte[] Content)? replacement = null;

        if (form.Files.GetFile("file") is { } file)
        {
            if (file.Length == 0 || file.Length > limit)
            {
                return PortalError(file.Length == 0 ? 400 : 413,
                    $"'{document.Title}' was not changed: "
                    + (file.Length == 0 ? "the file is empty." : AdminEndpoints.DocumentTooLarge(limit)));
            }

            byte[] content = new byte[file.Length];

            await using (Stream stream = file.OpenReadStream())
            {
                await stream.ReadExactlyAsync(content, cancellation).ConfigureAwait(false);
            }

            if (Documents.Recognise(file.FileName, content.AsSpan(0, Math.Min(content.Length, 4096)), out string? unknown) is not { } kind)
            {
                return PortalError(415, $"'{document.Title}' was not changed: {unknown}");
            }

            replacement = (Documents.CleanFileName(file.FileName, kind), kind, content);
        }

        // D-321: the picture ArcGIS's update takes as the file 'thumbnail', checked with the rest before anything is written.
        byte[]? picture = null;

        if ((form.Files.GetFile("thumbnail")
                ?? form.Files.FirstOrDefault(f => f.Name.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))) is { } pictured)
        {
            if (pictured.Length is 0 or > Documents.MaximumThumbnailBytes)
            {
                return PortalError(pictured.Length == 0 ? 400 : 413,
                    $"'{document.Title}' was not changed: a thumbnail is a picture of at most 1 MB.");
            }

            picture = new byte[pictured.Length];

            await using (Stream stream = pictured.OpenReadStream())
            {
                await stream.ReadExactlyAsync(picture, cancellation).ConfigureAwait(false);
            }

            if (Documents.RecognisePicture(picture) is null)
            {
                return PortalError(415, $"'{document.Title}' was not changed: a thumbnail is a PNG, JPEG, GIF or WebP picture.");
            }
        }

        if (title != document.Title || snippet != document.Snippet)
        {
            await documents.UpdateAsync(document.Id, title, snippet, document.Sharing, cancellation).ConfigureAwait(false);
        }

        if (description is not null)
        {
            await documents.SetDescriptionAsync(document.Id, description.Length == 0 ? null : description, cancellation)
                .ConfigureAwait(false);
        }

        if (tags is not null)
        {
            await documents.SetTagsAsync(document.Id, tags, cancellation).ConfigureAwait(false);
        }

        if (replacement is { } replaced)
        {
            await documents.ReplaceAsync(document.Id, replaced.FileName, replaced.Kind, replaced.Content, cancellation)
                .ConfigureAwait(false);
        }

        if (picture is not null)
        {
            await documents.SetThumbnailAsync(document.Id, picture, cancellation).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// The categories a portal client names — paths under <c>/Categories</c>, as the item's own JSON gives them, sent as
    /// a JSON array or a comma-separated list; null, with the one it does not know, when any is not this portal's.
    /// </summary>
    private static List<Guid>? CategoriesFrom(string sent, ItemAnnotations held, out string? unknown)
    {
        unknown = null;
        IEnumerable<string> paths;

        if (sent.TrimStart().StartsWith('['))
        {
            try
            {
                paths = JsonSerializer.Deserialize<string[]>(sent) ?? [];
            }
            catch (JsonException)
            {
                unknown = "'categories' is a JSON array of category paths, such as [\"/Categories/Water\"].";
                return null;
            }
        }
        else
        {
            paths = sent.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        List<Guid> found = [];

        foreach (string path in paths)
        {
            if (held.Resolve(path) is not { } category)
            {
                unknown = $"'{path}' is not one of this organisation's categories.";
                return null;
            }

            found.Add(category);
        }

        return found;
    }

    /// <summary>
    /// Portal's <c>updateItems</c>, which Pro sends after an overwrite with the item's categories: each item's categories
    /// are set, and any other change is refused by name for that item.
    /// </summary>
    private static async Task<IResult> UpdateItemsAsync(
        HttpContext context, IItemAnnotationStore annotations, ItemAnnotations held, CancellationToken cancellation)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        List<object> results = [];
        bool changed = false;

        try
        {
            using JsonDocument items = JsonDocument.Parse(First(form, context, "items") is { Length: > 0 } text ? text : "[]");

            foreach (JsonElement entry in items.RootElement.EnumerateArray())
            {
                foreach (JsonProperty sent in entry.EnumerateObject())
                {
                    string? error = null;

                    if (current.Principal.IsAnonymous || await WritableItemAsync(context, sent.Name, cancellation).ConfigureAwait(false) is not { } item)
                    {
                        error = "Item does not exist or is inaccessible.";
                    }
                    else if (!Manages(current, item))
                    {
                        error = $"'{item.Title}' is not yours to change.";
                    }
                    else if (sent.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = "Each item's change is a JSON object, such as {\"categories\": [\"/Categories/Water\"]}.";
                    }
                    else if (sent.Value.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n != "categories") is { } other)
                    {
                        error = $"Only an item's categories are changed here; '{other}' is not.";
                    }
                    else if (sent.Value.TryGetProperty("categories", out JsonElement named))
                    {
                        if (CategoriesFrom(named.GetRawText(), held, out string? unknown) is not { } found)
                        {
                            error = unknown;
                        }
                        else
                        {
                            await annotations.SetCategoriesAsync(item.ItemGuid, found, cancellation).ConfigureAwait(false);
                            changed = true;
                        }
                    }

                    results.Add(error is null
                        ? new { itemId = sent.Name, success = true }
                        : new { itemId = sent.Name, success = false, error = new { code = 400, message = error, details = Array.Empty<string>() } });
                }
            }
        }
        catch (JsonException)
        {
            return PortalError(400, "'items' is not the JSON the portal API describes.");
        }

        if (changed)
        {
            await held.RefreshAsync(cancellation).ConfigureAwait(false);
        }

        return Results.Ok(new { results });
    }

    /// <summary>Portal's item <c>delete</c>.</summary>
    private static async Task<IResult> DeleteUserItemAsync(HttpContext context, string username, string id, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;
        (bool ok, string? error, int status) = await DeleteOneAsync(
            context, current, id, Permanent(form, context), cancellation).ConfigureAwait(false);

        return ok ? Results.Ok(new { success = true, itemId = id }) : PortalError(status, error!);
    }

    /// <summary>Portal's <c>deleteItems</c>: several, each answered on its own.</summary>
    private static async Task<IResult> DeleteItemsAsync(HttpContext context, string username, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        bool permanent = Permanent(form, context);
        List<object> results = [];

        foreach (string id in First(form, context, "items").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (bool ok, string? error, int status) = await DeleteOneAsync(context, current, id, permanent, cancellation).ConfigureAwait(false);

            results.Add(ok
                ? new { itemId = id, success = true }
                : new { itemId = id, success = false, error = new { code = status, message = error, details = Array.Empty<string>() } });
        }

        return Results.Ok(new { results });
    }

    /// <summary>Whether a delete asked to skip the recycle bin, as ArcGIS Enterprise 11.4 and later take it.</summary>
    private static bool Permanent(IFormCollection form, HttpContext context) =>
        string.Equals(First(form, context, "permanentDelete"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes one item through the console's own delete, so every rule it keeps holds here; what it answered, in the
    /// portal's terms. A <paramref name="permanent"/> delete then takes the item's entry out of the recycle bin, through
    /// the bin's own purge (ADR-214 condition 1: the bin has a screen, so a delete that asks to skip it is not the only
    /// way back).
    /// </summary>
    private static async Task<(bool Ok, string? Error, int Status)> DeleteOneAsync(
        HttpContext context, RequestPrincipal current, string id, bool permanent, CancellationToken cancellation)
    {
        // <b>A service definition Pro uploaded is its own item</b>: deleting it forgets the upload, never the service it
        // was published to — which is what Pro deletes after a publish it misread.
        if (Staged.TryGetValue(id, out StagedDefinition? staged) && staged.Owner == current.Principal.Id)
        {
            Staged.TryRemove(id, out _);
            return (true, null, 200);
        }

        if (await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return (false, "Item does not exist or is inaccessible.", 400);
        }

        if (!Manages(current, item))
        {
            return (false, $"'{item.Title}' is not yours to delete.", 403);
        }

        // <b>Pro's rollback is not an owner's delete — INFERRED, for the owner to confirm.</b> Pro reads a publish answer
        // it does not understand as a failure and deletes the service item it was given (dev, 2026-10-17); with a real
        // delete behind this door that would put a service just published into the recycle bin. A service Pro published
        // in the last minutes is refused here, with the way to delete it on purpose.
        if (Staged.Values.FirstOrDefault(s => string.Equals(s.ServiceItemId, id, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.UtcNow - s.Created < ProRollbackWindow) is not null)
        {
            return (false,
                $"'{item.Title}' was published from ArcGIS Pro minutes ago, and a delete this soon is how Pro undoes a publish "
                + "it misread, so it was kept. Delete it from Studio, or again after a quarter of an hour.", 409);
        }

        IServiceProvider services = context.RequestServices;
        (int status, string? message) = await InnerAsync(context, () => item.Document is { } document
            ? AdminEndpoints.DeleteDocumentAsync(
                context, document.Id,
                services.GetRequiredService<IDocumentStore>(),
                services.GetRequiredService<IAuditLog>(),
                services.GetRequiredService<RecycleBinService>(),
                cancellation)
            : item.Service is { } service
            ? AdminEndpoints.DeleteServiceAsync(
                context, service.Name, service.Folder, drop: true,
                services.GetRequiredService<IAdminCatalog>(),
                services.GetRequiredService<PostgresLayerCatalog>(),
                services.GetRequiredService<Graticula.Providers.PostGis.PostGisImporter>(),
                services.GetRequiredService<ServiceContexts>(),
                services.GetRequiredService<Graticula.Tiles.ITileCache>(),
                services.GetRequiredService<IAuditLog>(),
                services.GetRequiredService<SourceQuiesce>(),
                services.GetRequiredService<RecycleBinService>(),
                services.GetRequiredService<LayerConnections>(),
                cancellation)
            : AdminEndpoints.DeleteWebMapAsync(
                context, item.Map!.Id,
                services.GetRequiredService<IWebMapStore>(),
                services.GetRequiredService<IAuditLog>(),
                services.GetRequiredService<RecycleBinService>(),
                cancellation)).ConfigureAwait(false);

        if (status is < 200 or >= 300)
        {
            return (false, message ?? $"'{item.Title}' was not deleted.", status);
        }

        if (permanent)
        {
            RecycleBinService recycle = services.GetRequiredService<RecycleBinService>();

            foreach (RecycleEntry entry in (await recycle.Bin.ListAsync(cancellation).ConfigureAwait(false))
                .Where(e => e.ItemId == item.ItemGuid))
            {
                await recycle.PurgeAsync(entry, cancellation).ConfigureAwait(false);
                await AdminEndpoints.AuditPurgeAsync(context, services.GetRequiredService<IAuditLog>(), entry, cancellation)
                    .ConfigureAwait(false);
            }
        }

        return (true, null, 200);
    }

    /// <summary>
    /// Runs one of the console's handlers inside this request and reads what it answered, without sending it: the status,
    /// and the message of a refusal. The response is left as it was, for the portal's own answer.
    /// </summary>
    private static async Task<(int Status, string? Message)> InnerAsync(HttpContext context, Func<Task> handler)
    {
        Stream original = context.Response.Body;
        using MemoryStream captured = new();
        context.Response.Body = captured;

        try
        {
            await handler().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        int status = context.Response.StatusCode;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = null;
        context.Response.Headers.Location = default;

        string? message = null;

        try
        {
            using JsonDocument answer = JsonDocument.Parse(captured.ToArray());

            if (answer.RootElement.ValueKind == JsonValueKind.Object
                && answer.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement said))
            {
                message = said.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: the status says what happened.
        }

        return (status, message);
    }

    /// <summary>Portal's item <c>unshare</c>: from the groups named.</summary>
    private static async Task<IResult> UnshareItemAsync(
        HttpContext context, IGroupDirectory groups, string username, string id, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : FormCollection.Empty;

        (bool ok, List<string> notUnshared, string? error, int status) = await UnshareOneAsync(
            context, current, groups, id, GroupIdsOf(form, context), cancellation).ConfigureAwait(false);

        return ok ? Results.Ok(new { notUnsharedFrom = notUnshared, itemId = id }) : PortalError(status, error!);
    }

    /// <summary>Portal's <c>unshareItems</c>: several items, from the same groups.</summary>
    private static async Task<IResult> UnshareItemsAsync(
        HttpContext context, IGroupDirectory groups, string username, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string[] groupIds = GroupIdsOf(form, context);
        List<object> results = [];

        foreach (string id in First(form, context, "items").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (bool ok, List<string> notUnshared, string? error, int status) = await UnshareOneAsync(
                context, current, groups, id, groupIds, cancellation).ConfigureAwait(false);

            results.Add(ok
                ? new { itemId = id, success = true, notUnsharedFrom = notUnshared }
                : new { itemId = id, success = false, notUnsharedFrom = groupIds.ToList(), error = new { code = status, message = error, details = Array.Empty<string>() } });
        }

        return Results.Ok(new { results });
    }

    private static string[] GroupIdsOf(IFormCollection form, HttpContext context) =>
        [.. First(form, context, "groups").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(g => Guid.TryParseExact(g, "N", out _))];

    private static async Task<(bool Ok, List<string> NotUnshared, string? Error, int Status)> UnshareOneAsync(
        HttpContext context, RequestPrincipal current, IGroupDirectory groups, string id, string[] groupIds,
        CancellationToken cancellation)
    {
        if (await WritableItemAsync(context, id, cancellation).ConfigureAwait(false) is not { } item)
        {
            return (false, [], "Item does not exist or is inaccessible.", 400);
        }

        bool administrator = current.Authorization.Allows(Privilege.AdminManageAllContent);

        if (!Manages(current, item))
        {
            return (false, [], $"'{item.Title}' is not yours to unshare.", 403);
        }

        if (groupIds.Length == 0)
        {
            return (false, [], "'groups' names the groups to unshare from, by id.", 400);
        }

        IReadOnlyList<GroupSummary> mine = await groups.ListAsync(current.Principal.Id, administrator, cancellation).ConfigureAwait(false);
        List<string> notUnshared = [];

        foreach (string groupId in groupIds)
        {
            GroupSummary? group = mine.FirstOrDefault(g => string.Equals(g.Id.ToString("N"), groupId, StringComparison.OrdinalIgnoreCase));
            GroupChange outcome = group is null
                ? GroupChange.Absent
                : item.Service is { } service
                    ? await groups.ShareAsync(current.Principal.Id, administrator, group.Name, service.Name, service.Folder, false, cancellation)
                        .ConfigureAwait(false)
                    : item.Map is { } map
                        ? await groups.ShareMapAsync(current.Principal.Id, administrator, group.Name, map.Id, false, cancellation)
                            .ConfigureAwait(false)
                        : await groups.ShareDocumentAsync(current.Principal.Id, administrator, group.Name, item.Document!.Id, false, cancellation)
                            .ConfigureAwait(false);

            if (outcome != GroupChange.Done)
            {
                notUnshared.Add(groupId);
            }
        }

        return (true, notUnshared, null, 200);
    }
}
