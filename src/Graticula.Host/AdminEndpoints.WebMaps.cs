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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Saved web maps — ADR-079 §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Under <c>/content</c>, beside <c>/content/items</c> and <c>/content/layers</c></b>, which is
/// where Studio's own content API already is (ADR-034 §5f): answered for whoever is signed in rather
/// than behind an <c>admin:</c> privilege. ADR-079 first named <c>/rest/webmaps</c>; <c>/rest</c> is the
/// ArcGIS face, and a Graticula-shaped document route there would be the one thing under that prefix
/// no ArcGIS client knows. ArcGIS clients reach a map through the portal instead.
/// </para>
/// <para>
/// <b>Reading follows the map's own scope through <see cref="LayerAccess.Evaluate"/>, and a map the
/// caller may not read is a 404</b> — the same answer as a map that does not exist, which is the rule
/// every other surface here keeps. Changing and deleting are <see cref="LayerAccess.MayManage"/>: the
/// owner, or <c>admin:manageAllContent</c>.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>The largest request body accepted: the document's bound and room for the fields around it.</summary>
    private const int MaximumWebMapBody = WebMaps.MaximumDocumentBytes + (64 * 1024);

    private static void MapWebMaps(WebApplication app)
    {
        app.MapGet("/content/webmaps", ListWebMapsAsync);
        app.MapPost("/content/webmaps", CreateWebMapAsync);
        app.MapGet("/content/webmaps/{id}", GetWebMapAsync);
        app.MapPut("/content/webmaps/{id}", UpdateWebMapAsync);
        app.MapDelete("/content/webmaps/{id}", DeleteWebMapAsync);
    }

    /// <summary>The maps this caller may open: their own first, then what is shared with them.</summary>
    private static async Task ListWebMapsAsync(
        HttpContext context, IWebMapStore maps, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401,
                "This lists the maps you can open, so it needs to know who you are. Sign in at "
                + "/rest/auth/login. A public map opens without signing in, from its own link.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<WebMap> all = await maps.ListAsync(cancellation).ConfigureAwait(false);

        object[] readable =
        [
            .. all
                .Where(map => Readable(current, map))
                .OrderByDescending(map => map.Owner == current.Principal.Id)
                .ThenByDescending(map => map.Modified)
                .Select(map => Describe(current, map)),
        ];

        await Results.Json(new
        {
            webMaps = readable,
            mayCreate = current.Authorization.Allows(Privilege.ContentCreate),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One map with its document, if this caller may read it.</summary>
    private static async Task GetWebMapAsync(
        HttpContext context, string id, IWebMapStore maps, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map
            || !Readable(current, map))
        {
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return;
        }

        await WriteMapAsync(context, current, map, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    /// <summary>Saves a new map, owned by whoever saves it.</summary>
    private static async Task CreateWebMapAsync(
        HttpContext context, IWebMapStore maps, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentCreate).ConfigureAwait(false))
        {
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ReadWebMapRequestAsync(context, cancellation).ConfigureAwait(false) is not { } request)
        {
            return;
        }

        if (!await MayShareAsync(context, request.Sharing, SharingScope.Private).ConfigureAwait(false))
        {
            return;
        }

        WebMap made = await maps.CreateAsync(
            request.Title, request.Snippet, current.Principal.Id, request.Sharing, request.Document, cancellation)
            .ConfigureAwait(false);

        await AuditAsync(
            context, audit, "webmap.create", made.Id,
            Detail(new { title = made.Title, sharing = PostgresSharing(made.Sharing), bytes = request.Bytes }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.Headers.Location = $"/content/webmaps/{made.Id}";
        await WriteMapAsync(context, current, made, StatusCodes.Status201Created).ConfigureAwait(false);
    }

    /// <summary>Replaces a map's title, snippet, scope and document.</summary>
    private static async Task UpdateWebMapAsync(
        HttpContext context, string id, IWebMapStore maps, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapAsync(context, current, id, "change", maps, audit, cancellation).ConfigureAwait(false)
            is not { } before)
        {
            return;
        }

        if (await ReadWebMapRequestAsync(context, cancellation).ConfigureAwait(false) is not { } request)
        {
            return;
        }

        if (!await MayShareAsync(context, request.Sharing, before.Sharing).ConfigureAwait(false))
        {
            return;
        }

        if (await maps.UpdateAsync(id, request.Title, request.Snippet, request.Sharing, request.Document, cancellation)
                .ConfigureAwait(false) is not { } after)
        {
            // Deleted between the read above and this write.
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "webmap.update", id,
            Detail(new
            {
                title = after.Title,
                sharing = PostgresSharing(after.Sharing),
                sharingBefore = PostgresSharing(before.Sharing),
                bytes = request.Bytes,
                owner = before.OwnerName,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await WriteMapAsync(context, current, after, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    /// <summary>Removes a map.</summary>
    private static async Task DeleteWebMapAsync(
        HttpContext context, string id, IWebMapStore maps, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapAsync(context, current, id, "delete", maps, audit, cancellation).ConfigureAwait(false)
            is not { } before)
        {
            return;
        }

        bool gone = await maps.DeleteAsync(id, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "webmap.delete", id,
            Detail(new { title = before.Title, owner = before.OwnerName }),
            succeeded: gone, cancellation).ConfigureAwait(false);

        if (!gone)
        {
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return;
        }

        await Results.Json(new { deleted = id, title = before.Title }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The map, when this caller may change it; otherwise writes the refusal and answers null.
    /// </summary>
    /// <remarks>
    /// <b>404 when they cannot read it, 403 when they can read it and not change it.</b> The second
    /// tells them nothing they did not know — they can open the map — and saying <em>not yours</em>
    /// is the sentence that sends them to <em>Save as</em>. The refused attempt is audited, because
    /// §5d audits every mutating call including the ones that fail.
    /// </remarks>
    private static async Task<WebMap?> ManagedMapAsync(
        HttpContext context,
        RequestPrincipal current,
        string id,
        string verb,
        IWebMapStore maps,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map
            || !Readable(current, map))
        {
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return null;
        }

        if (!LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization))
        {
            await AuditAsync(
                context, audit, $"webmap.{(verb == "delete" ? "delete" : "update")}", id,
                Detail(new { refused = "not the owner", owner = map.OwnerName }),
                succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 403,
                $"Only the map's owner, {map.OwnerName}, or an administrator can {verb} '{map.Title}'. "
                + "Save it as a new map to keep your own copy.").ConfigureAwait(false);
            return null;
        }

        return map;
    }

    /// <summary>Whether the caller holds what setting this scope asks for, writing the refusal when not.</summary>
    /// <remarks>
    /// <b>Asked only when the scope changes</b>, as a service's sharing is: an owner saving a map that
    /// somebody else already made public is not re-publishing it. Private asks for nothing.
    /// </remarks>
    private static Task<bool> MayShareAsync(HttpContext context, SharingScope wanted, SharingScope current)
    {
        if (wanted == current || wanted == SharingScope.Private)
        {
            return Task.FromResult(true);
        }

        return Authorize.RequireAsync(
            context,
            wanted == SharingScope.Public ? Privilege.SharingShareToPublic : Privilege.SharingShareToOrganization);
    }

    private static bool Readable(RequestPrincipal current, WebMap map) =>
        LayerAccess.Evaluate(map.Sharing, map.Owner, current.Principal, current.Authorization).IsAllowed();

    /// <summary>The owner's account name, or null for an anonymous caller.</summary>
    /// <remarks>
    /// <b>Q-127's rule, kept on this face too.</b> A public map opens without signing in, and naming its
    /// owner there would publish account names to whoever has the link — the reason the portal says
    /// <c>graticula</c> for anybody else's item. A signed-in member already sees owners in
    /// <c>/content/items</c>, so they see them here.
    /// </remarks>
    private static string? OwnerFor(RequestPrincipal current, WebMap map) =>
        current.Principal.IsAnonymous ? null : map.OwnerName;

    private static string NoMap(string id) =>
        $"No web map '{id}' that you can open. It may have been deleted, or it is not shared with you.";

    /// <summary>A map's description without its document, as a listing carries it.</summary>
    private static object Describe(RequestPrincipal current, WebMap map) => new
    {
        id = map.Id,
        title = map.Title,
        snippet = map.Snippet,
        owner = OwnerFor(current, map),
        sharing = PostgresSharing(map.Sharing),
        created = map.Created,
        modified = map.Modified,
        mine = !current.Principal.IsAnonymous && map.Owner == current.Principal.Id,
        manages = LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization),
    };

    /// <summary>Writes a map with its document, which is written as the JSON it is rather than as a string.</summary>
    private static async Task WriteMapAsync(HttpContext context, RequestPrincipal current, WebMap map, int status)
    {
        using JsonDocument document = JsonDocument.Parse(map.Document ?? "{}");

        await Results.Json(
            new
            {
                id = map.Id,
                title = map.Title,
                snippet = map.Snippet,
                owner = OwnerFor(current, map),
                sharing = PostgresSharing(map.Sharing),
                created = map.Created,
                modified = map.Modified,
                mine = !current.Principal.IsAnonymous && map.Owner == current.Principal.Id,
                manages = LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization),
                document = document.RootElement,
            },
            statusCode: status).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>What a create or an update carries, once it has been checked.</summary>
    private sealed record WebMapRequest(string Title, string? Snippet, SharingScope Sharing, string Document, int Bytes);

    /// <summary>
    /// Reads and checks a create or update body; writes the refusal and answers null when it fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read by hand rather than bound to a type</b>, because the document has to arrive as the text
    /// it was: binding it to a model would drop every field the model does not name, which is exactly
    /// what ADR-079 §5.2 forbids — a map saved by Pro and saved again here loses nothing.
    /// </para>
    /// <para>
    /// <b>Two rules about the document and no more</b>: it is a JSON object, and it is at most
    /// <see cref="WebMaps.MaximumDocumentBytes"/>. What a layer is, the viewer decides.
    /// </para>
    /// </remarks>
    private static async Task<WebMapRequest?> ReadWebMapRequestAsync(HttpContext context, CancellationToken cancellation)
    {
        if (context.Request.ContentLength is > MaximumWebMapBody)
        {
            await Refuse(context, 413, TooLarge()).ConfigureAwait(false);
            return null;
        }

        byte[] body;

        using (MemoryStream buffer = new())
        {
            byte[] chunk = new byte[64 * 1024];
            int read;

            while ((read = await context.Request.Body.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumWebMapBody)
                {
                    await Refuse(context, 413, TooLarge()).ConfigureAwait(false);
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            await Refuse(context, 400, $"The body is not JSON: {e.Message}").ConfigureAwait(false);
            return null;
        }

        using (parsed)
        {
            JsonElement root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                await Refuse(context, 400,
                    "The body is a JSON object with title, snippet, sharing and document.").ConfigureAwait(false);
                return null;
            }

            string? title = Text(root, "title")?.Trim();

            if (string.IsNullOrEmpty(title) || title.Length > WebMaps.MaximumTitleLength)
            {
                await Refuse(context, 400,
                    $"A web map needs a title of 1 to {WebMaps.MaximumTitleLength} characters.").ConfigureAwait(false);
                return null;
            }

            string? snippet = Text(root, "snippet")?.Trim();

            if (snippet is { Length: > WebMaps.MaximumSnippetLength })
            {
                await Refuse(context, 400,
                    $"The snippet is at most {WebMaps.MaximumSnippetLength} characters.").ConfigureAwait(false);
                return null;
            }

            SharingScope sharing = SharingScope.Private;

            if (Text(root, "sharing") is { Length: > 0 } scope)
            {
                switch (scope.ToLowerInvariant())
                {
                    case "private": sharing = SharingScope.Private; break;

                    // The portal's word as well as this server's, because a client that read `access`
                    // from an item will send it back.
                    case "organization" or "org": sharing = SharingScope.Organization; break;
                    case "public": sharing = SharingScope.Public; break;

                    // enum-default-is-deliberate: `group` is refused, not mapped — a web map has three
                    // scopes until ADR-079 condition 4 decides groups, and WebMaps.Allows says the same.
                    default:
                        await Refuse(context, 400,
                            "A web map's sharing is 'private', 'organization' or 'public'. Sharing a map "
                            + "with a group is not built (ADR-079 condition 4).").ConfigureAwait(false);
                        return null;
                }
            }

            if (!root.TryGetProperty("document", out JsonElement document)
                || document.ValueKind != JsonValueKind.Object)
            {
                await Refuse(context, 400,
                    "document must be a JSON object: an ArcGIS Web Map, with operationalLayers and baseMap.")
                    .ConfigureAwait(false);
                return null;
            }

            string raw = document.GetRawText();
            int bytes = Encoding.UTF8.GetByteCount(raw);

            if (bytes > WebMaps.MaximumDocumentBytes)
            {
                await Refuse(context, 413, TooLarge()).ConfigureAwait(false);
                return null;
            }

            return new WebMapRequest(title, string.IsNullOrEmpty(snippet) ? null : snippet, sharing, raw, bytes);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string TooLarge() =>
        $"A web map's document is at most {WebMaps.MaximumDocumentBytes / 1024 / 1024} MB. A map is a list of "
        + "layers by address, a basemap and a view; data belongs in a layer.";
}
