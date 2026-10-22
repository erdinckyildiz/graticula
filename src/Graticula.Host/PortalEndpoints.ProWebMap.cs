using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Pro's <i>Share As Web Map</i>: the web map Pro uploads after publishing its layers, kept as a Studio web map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found 2026-10-17 by another session reading Pro's own log</b> (<c>SharingJobLog.log</c>): the layers published,
/// then <i>"Uploading the web item and the configuration … Failed to add web item"</i>. Pro sends the map to
/// <c>addItem</c> as <c>type=Web Map</c> with its JSON in <c>text</c>, its title, tags and snippet, into the folder in
/// the path; <c>addItem</c> answered every type but a service definition with an error.
/// </para>
/// <para>
/// <b>A Studio web map, not a second kind of item</b>: the same store, owner, folder, tags, description and picture
/// Studio's own Save writes (ADR-079), private until shared — which Pro then does through <c>share</c>, and that now
/// reaches a map as well as a service.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Portal's <c>addItem</c> for a web map: stored, and answered with its id and folder.</summary>
    private static async Task<IResult> AddWebMapAsync(
        HttpContext context, IFormCollection form, string username, IWebMapStore maps, IContentFolderStore folders,
        Graticula.Platform.Admin.IAuditLog audit, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current || !current.Authorization.Allows(Privilege.ContentCreate))
        {
            return PortalError(403, "Adding a web map needs the privilege to create content.");
        }

        string title = First(form, context, "title").Trim();

        if (title.Length is 0 or > WebMaps.MaximumTitleLength)
        {
            return PortalError(400, $"A web map needs a title of 1 to {WebMaps.MaximumTitleLength} characters.");
        }

        string text = First(form, context, "text");

        if (Encoding.UTF8.GetByteCount(text) > WebMaps.MaximumDocumentBytes)
        {
            return PortalError(413, $"A web map's document is at most {WebMaps.MaximumDocumentBytes / 1024 / 1024} MB.");
        }

        string document;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text.Length > 0 ? text : "null");

            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return PortalError(400, "'text' is the web map's JSON: an object with operationalLayers and baseMap.");
            }

            document = parsed.RootElement.GetRawText();
        }
        catch (JsonException e)
        {
            return PortalError(400, $"'text' is not JSON: {e.Message}");
        }

        // The folder in the path is the caller's own, or the item is not added anywhere.
        Guid? folder = null;

        if (context.Request.RouteValues["folderId"] is string folderText)
        {
            if (!Guid.TryParseExact(folderText, "N", out Guid folderId)
                || await folders.FindAsync(folderId, cancellation).ConfigureAwait(false) is not { } found
                || found.Owner != current.Principal.Id)
            {
                return Unknown("Folder");
            }

            folder = folderId;
        }

        string snippet = First(form, context, "snippet").Trim();
        WebMap made = await maps.CreateAsync(
            title, snippet.Length is > 0 and <= WebMaps.MaximumSnippetLength ? snippet : null,
            current.Principal.Id, SharingScope.Private, document, cancellation).ConfigureAwait(false);

        if (ItemTags.Normalise(First(form, context, "tags").Split(','), out _) is { Count: > 0 } tags)
        {
            await maps.SetTagsAsync(made.Id, tags, cancellation).ConfigureAwait(false);
        }

        if (First(form, context, "description") is { Length: > 0 } description)
        {
            await maps.SetDescriptionAsync(made.Id, description, cancellation).ConfigureAwait(false);
        }

        await KeepThumbnailAsync(form, maps, made.Id, cancellation).ConfigureAwait(false);

        if (folder is { } into)
        {
            await folders.MoveMapAsync(made.Id, into, cancellation).ConfigureAwait(false);
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "webmap.create", made.Id,
            AdminEndpoints.Detail(new { title = made.Title, from = "Pro", folder = folder?.ToString("N") }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true, id = made.Id, folder = folder?.ToString("N") });
    }

    /// <summary>
    /// Keeps the picture a form carries — the file <c>thumbnail</c>, a PNG of at most 1 MB, as ArcGIS's
    /// <c>addItem</c> and <c>update</c> take it — as the map's thumbnail; whether one was kept.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-10-18: a map published from Pro has its picture from the first moment.</b> Pro wrote a 105 KB
    /// <c>Thumbnail.png</c> into its staging folder before the upload, and the Deneme map came up without one.
    /// Which of the two calls carries it was not on the record (dev's capture had restarted), so both read it.
    /// </remarks>
    private static async Task<bool> KeepThumbnailAsync(
        IFormCollection form, IWebMapStore maps, string id, CancellationToken cancellation)
    {
        IFormFile? picture = form.Files.GetFile("thumbnail")
            ?? form.Files.FirstOrDefault(f => f.Name.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase));

        if (picture is not { Length: > 0 and <= 1 << 20 })
        {
            return false;
        }

        using System.IO.MemoryStream bytes = new();
        await picture.CopyToAsync(bytes, cancellation).ConfigureAwait(false);
        byte[] png = bytes.ToArray();

        if (!png.AsSpan().StartsWith(Png))
        {
            return false;
        }

        await maps.SetThumbnailAsync(id, png, cancellation).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sets a web map's scope and groups from a share form — the map half of <c>ShareOneAsync</c>, by the rules Studio
    /// shares a map by; null when <paramref name="id"/> is not a map.
    /// </summary>
    private static async Task<(bool Ok, System.Collections.Generic.List<string> NotShared, string? Error)?> ShareMapAsync(
        RequestPrincipal current, IWebMapStore maps, IGroupDirectory groups, string id, SharingScope scope,
        string[] groupIds, CancellationToken cancellation)
    {
        if (!WebMaps.IsId(id) || await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map)
        {
            return null;
        }

        Privilege needed = scope switch
        {
            SharingScope.Public => Privilege.SharingShareToPublic,
            SharingScope.Organization => Privilege.SharingShareToOrganization,
            SharingScope.Group => Privilege.GroupsShareTo,
            _ => Privilege.ContentCreate,
        };

        bool administrator = current.Authorization.Allows(Privilege.AdminManageAllContent);

        if (!current.Authorization.Allows(needed) || (map.Owner != current.Principal.Id && !administrator))
        {
            return (false, [], $"Sharing '{map.Title}' {scope.ToString().ToLowerInvariant()} is not yours to do.");
        }

        if (map.Sharing != scope)
        {
            await maps.UpdateAsync(map.Id, map.Title, map.Snippet, scope, map.Document ?? "{}", cancellation)
                .ConfigureAwait(false);
        }

        System.Collections.Generic.List<string> notShared = [];

        if (groupIds.Length > 0)
        {
            var mine = await groups.ListAsync(current.Principal.Id, administrator, cancellation).ConfigureAwait(false);

            foreach (string groupId in groupIds)
            {
                GroupSummary? group = mine.FirstOrDefault(g => string.Equals(g.Id.ToString("N"), groupId, StringComparison.OrdinalIgnoreCase));
                GroupChange outcome = group is null || !current.Authorization.Allows(Privilege.GroupsShareTo)
                    ? GroupChange.Absent
                    : await groups.ShareMapAsync(current.Principal.Id, administrator, group.Name, map.Id, true, cancellation)
                        .ConfigureAwait(false);

                if (outcome != GroupChange.Done)
                {
                    notShared.Add(groupId);
                }
            }
        }

        return (true, notShared, null);
    }
}
