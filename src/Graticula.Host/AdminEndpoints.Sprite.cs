using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>
/// A vector tile service's sprite sheet — ADR-092.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside the style, because the two are one document's halves.</b> A style names icons and
/// the sheet draws them; each checks the other. A style naming an icon the sheet lacks is refused
/// by <c>StyleDocument</c>, and a sheet that would take away an icon the stored style names is
/// refused here — so the pair can only move between states where every named icon draws.
/// </para>
/// <para>
/// <b>In its own file for the reason <c>AdminEndpoints.VisibleRange.cs</c> gives</b>: the routes
/// are registered from here, and the style handlers call <see cref="SpriteIconsAsync"/>.
/// </para>
/// <para>
/// <b>Nothing to forget after a write.</b> The tile face reads the sheet from the store on each
/// request, as it reads the style; <c>ServiceContexts</c> remembers layer descriptions and
/// connections, and neither a sheet nor a style is in it.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    /// <summary>
    /// The most a sprite upload may carry: both files at their bounds, plus room for the framing.
    /// </summary>
    private const long SpriteRequestBytes = SpriteSheet.MaximumImageBytes + SpriteSheet.MaximumIndexBytes + 64 * 1024;

    private static void MapSprite(WebApplication app)
    {
        app.MapGet("/admin/services/{name}/sprite", GetSpriteAsync);
        app.MapPut("/admin/services/{name}/sprite", SetSpriteAsync);
        app.MapDelete("/admin/services/{name}/sprite", DeleteSpriteAsync);
    }

    /// <summary>
    /// The icon names of a service's 1x sheet, or null when it has none — what a style is checked
    /// against.
    /// </summary>
    /// <remarks>
    /// <b>The 1x sheet, because it is the one every client can reach.</b> A client asking for
    /// <c>@2x</c> falls back to it, and a client asking for 1x never sees the other.
    /// </remarks>
    private static async Task<IReadOnlyList<string>?> SpriteIconsAsync(
        IAdminCatalog catalog, string? folder, string name, CancellationToken cancellation) =>
        await catalog.FindSpriteAsync(folder, name, 1, withImage: false, cancellation).ConfigureAwait(false) is { } sheet
            ? SpriteSheet.IconNames(sheet.Index)
            : null;

    /// <summary>What is stored: for each sheet, how many icons, how large, and since when.</summary>
    /// <remarks>
    /// <b>Behind the service's sharing as well as the privilege</b>, as the style's read-back is:
    /// ADR-018 §3b governs reading on this surface too.
    /// </remarks>
    private static async Task GetSpriteAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        PostgresLayerCatalog owners,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await catalog.FindServiceForStyleAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        if (await owners.FindServiceAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false) is not { } readable
            || !LayerAccess.Evaluate(
                    readable.Sharing, readable.Owner,
                    context.Features.Get<RequestPrincipal>()!.Principal,
                    context.Features.Get<RequestPrincipal>()!.Authorization,
                    readable.SharedWith).IsAllowed())
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<StoredSprite> sheets =
            await catalog.ListSpritesAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            stored = sheets.Count > 0,
            sheets = sheets.Select(sheet => new
            {
                ratio = sheet.PixelRatio,
                icons = SpriteSheet.IconNames(sheet.Index).Count,
                width = sheet.Width,
                height = sheet.Height,
                bytes = sheet.ImageBytes,
                updated = sheet.UpdatedAt,
            }),

            // What the stored style draws by name, which is what a replacement or a removal may not take away.
            styleUses = StyleDocument.LiteralIcons(service.Style),
            note = sheets.Count == 0
                ? "This service has no sprite sheet, so it serves an empty one and a style on it cannot draw icons. "
                  + "Upload sprite.json and sprite.png as the parts 'index' and 'image'."
                : sheets.All(sheet => sheet.PixelRatio != 2)
                    ? "No @2x sheet is stored, so a high-density screen draws the 1x icons, scaled."
                    : null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores one sheet — the index and the picture together — having checked each against the other and
    /// both against the stored style.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>multipart/form-data</c> with the parts <c>index</c> and <c>image</c></b>, because a sheet is two
    /// files that only mean something together, and storing one without the other would cut old rectangles
    /// out of a new picture. <c>?ratio=2</c> uploads the <c>@2x</c> pair; it defaults to 1.
    /// </para>
    /// <para>
    /// <b>Read into memory, bounded before it is read.</b> The attachment route streams because an attachment
    /// can be 128 MB; a sheet is at most nine, and it has to be whole in hand to be checked at all — the index
    /// is measured against the picture. Kestrel's limit is set to that bound here, beside it, for the reason
    /// <c>AttachmentEndpoints</c> gives: a framework limit firing first blames the wrong party.
    /// </para>
    /// <para>
    /// <b>409 for the two refusals that are about what is already stored</b> — a <c>@2x</c> sheet with no 1x
    /// sheet under it, and a sheet that lacks an icon the stored style names. The request is well formed in
    /// both; what it conflicts with is the service, and the message says which half to change.
    /// </para>
    /// </remarks>
    private static async Task SetSpriteAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        IAuditLog audit,
        PostgresLayerCatalog owners,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        string asked = context.Request.Query["ratio"].ToString();
        int ratio = asked switch
        {
            "" or "1" => 1,
            "2" => 2,
            _ => 0,
        };

        if (ratio == 0)
        {
            await Refuse(
                context, 400,
                $"'ratio' is 1 or 2, not '{asked}': those are the two sheets a client asks for, sprite and "
                + "sprite@2x. Leave it out for 1.")
                .ConfigureAwait(false);
            return;
        }

        string? at = FolderOf(folder);

        // <b>[D-275](../../docs/architecture-debt.md): by folder and name, as the style is.</b> ADR-092
        // copied the style routes' lookup, and with it their defect: the owner of `a/roads` passed
        // the check below and then wrote a sheet into every service called `roads`.
        if (await catalog.FindServiceForStyleAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        // ADR-075: whose service it is — the privilege above never asked.
        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "change the sprite sheet of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        if (context.Request.ContentLength > SpriteRequestBytes)
        {
            await Refuse(context, 413, SpriteTooLarge()).ConfigureAwait(false);
            return;
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
            is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = SpriteRequestBytes;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? media)
            || !media.Boundary.HasValue)
        {
            await Refuse(
                context, 400,
                "Send the sheet as multipart/form-data with two parts: 'index', the sprite.json, and 'image', the "
                + "sprite.png.")
                .ConfigureAwait(false);
            return;
        }

        byte[]? index = null;
        byte[]? image = null;

        try
        {
            MultipartReader reader = new(HeaderUtilities.RemoveQuotes(media.Boundary).Value!, context.Request.Body);
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(cancellation).ConfigureAwait(false)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition, out ContentDispositionHeaderValue? disposition))
                {
                    continue;
                }

                string part = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;

                if (part is not ("index" or "image"))
                {
                    // Anything else is not ours to interpret; the body limit still bounds it.
                    continue;
                }

                if ((part == "index" ? index : image) is not null)
                {
                    await Refuse(context, 400, $"The part '{part}' was sent twice. A sheet is one index and one image.")
                        .ConfigureAwait(false);
                    return;
                }

                int bound = part == "index" ? SpriteSheet.MaximumIndexBytes : SpriteSheet.MaximumImageBytes;

                if (await BoundedPartAsync(section.Body, bound, cancellation).ConfigureAwait(false) is not { } bytes)
                {
                    await Refuse(context, 413, SpriteTooLarge()).ConfigureAwait(false);
                    return;
                }

                if (part == "index")
                {
                    index = bytes;
                }
                else
                {
                    image = bytes;
                }
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await Refuse(context, 413, SpriteTooLarge()).ConfigureAwait(false);
            return;
        }

        if (index is null || image is null)
        {
            await Refuse(
                context, 400,
                "A sheet is two parts and this request is missing "
                + (index is null && image is null ? "both" : index is null ? "'index' (the sprite.json)" : "'image' (the sprite.png)")
                + ". Send them together, so the index is checked against the picture it describes.")
                .ConfigureAwait(false);
            return;
        }

        string text;

        try
        {
            // A byte order mark is not content: the index is stored from the first character after it.
            ReadOnlySpan<byte> utf8 = index.AsSpan();
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(utf8.StartsWith("﻿"u8) ? utf8[3..] : utf8);
        }
        catch (DecoderFallbackException)
        {
            await Refuse(context, 400, "The sprite index is not UTF-8 text, which JSON has to be.").ConfigureAwait(false);
            return;
        }

        if (!SpriteSheet.TryValidate(text, image, ratio, out IReadOnlyList<string> icons, out string? error))
        {
            await Refuse(context, 400, error!).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<StoredSprite> stored =
            await catalog.ListSpritesAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false);

        if (ratio == 2 && stored.All(sheet => sheet.PixelRatio != 1))
        {
            await Refuse(
                context, 409,
                "Upload the 1x sheet first. A style is checked against the 1x sheet's icons, and it is the one a "
                + "client falls back to; a @2x sheet with nothing under it would draw on high-density screens only.")
                .ConfigureAwait(false);
            return;
        }

        if (MissingFromSheet(service.Style, icons) is { Count: > 0 } missing)
        {
            await Refuse(
                context, 409,
                $"This service's stored style draws {Quoted(missing)} by name, and this {(ratio == 1 ? "1x" : "@2x")} "
                + "sheet does not have " + (missing.Count == 1 ? "it" : "them") + ". Storing it would leave those "
                + "layers drawing nothing. Add " + (missing.Count == 1 ? "the icon" : "the icons") + " to the sheet, "
                + "or store a style that does not use " + (missing.Count == 1 ? "it" : "them") + " first.")
                .ConfigureAwait(false);
            return;
        }

        if (!await catalog.SetSpriteAsync(service.Folder, service.Name, ratio, text, image, cancellation)
                .ConfigureAwait(false))
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        bool replaced = stored.Any(sheet => sheet.PixelRatio == ratio);

        SpriteSheet.TryReadSize(image, out int width, out int height, out _);

        // The files are not in the audit record, for the reason the style is not: they are readable
        // through the service, and a log that copies its subject is a second place to keep it right.
        await AuditAsync(
            context, audit, "service.sprite", service.Name,
            Detail(new { folder = service.Folder, ratio, icons = icons.Count, bytes = image.Length, replaced }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            stored = true,
            ratio,
            icons = icons.Count,
            width,
            height,
            bytes = image.Length,
            replaced,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Removes both sheets, returning the service to the empty one.</summary>
    /// <remarks>
    /// <b>Refused while the stored style names an icon</b>, for the reason a replacement lacking one is: the
    /// layers drawing it would go blank. An <c>icon-image</c> expression does not hold the sheet here, because
    /// which names it produces is not known without the data — the serving check is what catches that one.
    /// </remarks>
    private static async Task DeleteSpriteAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        IAuditLog audit,
        PostgresLayerCatalog owners,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await catalog.FindServiceForStyleAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        // ADR-075: whose service it is — the privilege above never asked.
        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "remove the sprite sheet of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<StoredSprite> stored =
            await catalog.ListSpritesAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false);

        if (stored.Count > 0 && StyleDocument.LiteralIcons(service.Style) is { Count: > 0 } used)
        {
            await Refuse(
                context, 409,
                $"This service's stored style draws {Quoted(used)} by name, so its sprite sheet cannot be removed: "
                + "those layers would draw nothing. Store a style that uses no icons first, or reset the style.")
                .ConfigureAwait(false);
            return;
        }

        int removed = await catalog.DeleteSpritesAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false);

        await AuditAsync(
            context, audit, "service.sprite.clear", service.Name,
            Detail(new { folder = service.Folder, removed }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            stored = false,
            had = removed > 0,
            removed,
            note = removed > 0
                ? "The sprite sheet is removed; the service serves an empty one again."
                : "This service had no sprite sheet; nothing changed.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The literal icons a stored style draws that a sheet does not have.</summary>
    /// <param name="style">The stored style, or null.</param>
    /// <param name="icons">The sheet's icon names.</param>
    /// <returns>The missing names, in the order the style uses them.</returns>
    internal static IReadOnlyList<string> MissingFromSheet(string? style, IReadOnlyCollection<string> icons)
    {
        HashSet<string> has = new(icons, StringComparer.Ordinal);

        return [.. StyleDocument.LiteralIcons(style).Where(icon => !has.Contains(icon))];
    }

    /// <summary>Reads one part, or returns null when it is longer than the bound.</summary>
    /// <remarks>
    /// <b>Refused at the bound rather than truncated</b>, for the reason <c>BoundedBodyAsync</c> gives: a picture
    /// cut short is reported as a damaged one, and the uploader goes looking for the wrong problem.
    /// </remarks>
    private static async Task<byte[]?> BoundedPartAsync(Stream body, int bound, CancellationToken cancellation)
    {
        using MemoryStream into = new();
        byte[] buffer = new byte[81920];
        int read;

        while ((read = await body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (into.Length + read > bound)
            {
                return null;
            }

            into.Write(buffer, 0, read);
        }

        return into.ToArray();
    }

    private static string SpriteTooLarge() =>
        $"A sprite sheet may be at most {SpriteSheet.MaximumImageBytes / (1024 * 1024)} MB of image and "
        + $"{SpriteSheet.MaximumIndexBytes / 1024} KB of index, and this request is larger. It was not read rather than "
        + "being read in part.";

    private static string Quoted(IReadOnlyList<string> names) =>
        (names.Count == 1 ? "the icon " : "the icons ")
        + string.Join(", ", names.Take(20).Select(n => $"'{n}'"))
        + (names.Count > 20 ? $" and {names.Count - 20} more" : string.Empty);
}
