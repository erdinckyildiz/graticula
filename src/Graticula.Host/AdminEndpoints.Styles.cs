using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>Which style a service serves as its default.</summary>
/// <param name="Style">A stored style's name, or null to serve the generated style again.</param>
internal sealed record DefaultStyleRequest(string? Style);

/// <summary>
/// A vector tile service's named styles — ADR-094.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <c>/style</c>, which stays.</b> <c>/admin/services/{name}/style</c> is the default style,
/// whatever it is called — the address every console, script and test already uses, which ADR-020 §5c
/// keeps. <c>…/styles</c> lists them all, <c>…/styles/{style}</c> reads, stores and removes one, and
/// <c>…/default-style</c> chooses which one <c>resources/styles/root.json</c> serves.
/// </para>
/// <para>
/// <b>Addressed as the style and sprite routes are</b> — by <c>?folder=</c> and name, with an absent
/// folder meaning the root (D-275) — and written by the service's id after the ownership check has
/// asked about that same service (D-276), behind <c>content:publishFeatures</c> and ADR-075's manage
/// check, audited like the default style.
/// </para>
/// <para>
/// <b>In its own file for the reason <c>AdminEndpoints.VisibleRange.cs</c> gives.</b>
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapStyles(WebApplication app)
    {
        app.MapGet("/admin/services/{name}/styles", ListStylesAsync);
        app.MapGet("/admin/services/{name}/styles/{style}", GetNamedStyleAsync);
        app.MapPut("/admin/services/{name}/styles/{style}", SetNamedStyleAsync);
        app.MapDelete("/admin/services/{name}/styles/{style}", DeleteNamedStyleAsync);
        app.MapPut("/admin/services/{name}/default-style", SetDefaultStyleAsync);
    }

    /// <summary>
    /// Why a style may not be stored on — or served by — this service now, or null when it may.
    /// </summary>
    /// <remarks>
    /// <b>One check, three callers.</b> Storing, reading back and listing all ask it, with the same
    /// layers, the same 1x sprite sheet and the same allowed origins that <c>resources/styles</c> asks
    /// with, so what an author is told on the way in and on the way back is what a client gets.
    /// </remarks>
    private static async Task<string?> StyleRefusalAsync(
        string? style,
        StyledService service,
        IAdminCatalog catalog,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        IReadOnlyList<string>? icons =
            await SpriteIconsAsync(catalog, service, cancellation).ConfigureAwait(false);

        IReadOnlyList<StyleOrigin> allowed = StyleDocument.MayNameAnotherHost(style)
            ? await origins.CurrentAsync(cancellation).ConfigureAwait(false)
            : [];

        return StyleDocument.TryValidate(style, service.SourceLayers, icons, allowed, out string? error)
            ? null
            : error;
    }

    /// <summary>
    /// The service a read names, when the caller may read it; the refusal is written when not.
    /// </summary>
    /// <remarks>Sharing governs reading on this surface too — ADR-018 §3b — as the default style's read does.</remarks>
    private static async Task<StyledService?> ReadableStyledServiceAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        PostgresLayerCatalog owners,
        CancellationToken cancellation)
    {
        string? at = FolderOf(folder);

        if (await catalog.FindServiceForStyleAsync(at, name, cancellation).ConfigureAwait(false) is not { } service
            || await owners.FindServiceAsync(service.Folder, service.Name, cancellation).ConfigureAwait(false) is not { } readable
            || !LayerAccess.Evaluate(
                    readable.Sharing, readable.Owner,
                    context.Features.Get<RequestPrincipal>()!.Principal,
                    context.Features.Get<RequestPrincipal>()!.Authorization,
                    readable.SharedWith).IsAllowed())
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return null;
        }

        return service;
    }

    /// <summary>
    /// The service a write names, when the caller manages it; the refusal is written when not.
    /// </summary>
    private static async Task<StyledService?> ManagedStyledServiceAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        PostgresLayerCatalog owners,
        string what,
        CancellationToken cancellation)
    {
        string? at = FolderOf(folder);

        if (await catalog.FindServiceForStyleAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return null;
        }

        // ADR-075: whose service it is — the privilege never asked.
        return await ManagesServiceAsync(context, owners, service.Folder, service.Name, what, cancellation)
            .ConfigureAwait(false)
            ? service
            : null;
    }

    /// <summary>Every style the service carries, which is the default, and whether each is served.</summary>
    /// <remarks>
    /// <b>The list lives here and in the console, not in the ArcGIS documents.</b> A VectorTileServer's
    /// JSON has <c>defaultStyles</c> and nothing that lists others; adding a field there would be a
    /// member no ArcGIS client reads and a claim about the protocol Q-17 does not let this server make.
    /// </remarks>
    private static async Task ListStylesAsync(
        HttpContext context,
        string name,
        string? folder,
        IAdminCatalog catalog,
        PostgresLayerCatalog owners,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await ReadableStyledServiceAsync(context, name, folder, catalog, owners, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        IReadOnlyList<StoredStyle> styles = await catalog.ListStylesAsync(service.Id, cancellation).ConfigureAwait(false);

        List<object> rows = [];

        foreach (StoredStyle style in styles)
        {
            rows.Add(new
            {
                name = style.Name,
                isDefault = style.IsDefault,
                bytes = style.Style.Length,
                updated = style.UpdatedAt,

                // Why it is not served now, or null — the same sentence the read-back's header carries.
                stale = await StyleRefusalAsync(style.Style, service, catalog, origins, cancellation).ConfigureAwait(false),

                // Relative to the service's VectorTileServer, as every resource in its documents is.
                url = style.IsDefault ? "resources/styles/root.json" : $"resources/styles/{style.Name}.json",
            });
        }

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            @default = styles.FirstOrDefault(s => s.IsDefault)?.Name,
            styles = rows,
            most = StyleNames.MostPerService,
            note = styles.Any(s => s.IsDefault)
                ? null
                : "No style is the default, so resources/styles/root.json serves the generated one"
                  + (styles.Count > 0 ? "; make one of these the default with PUT …/default-style." : "."),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One named style, byte for byte, with why it is not served when it is not.</summary>
    private static async Task GetNamedStyleAsync(
        HttpContext context,
        string name,
        string style,
        string? folder,
        IAdminCatalog catalog,
        PostgresLayerCatalog owners,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await ReadableStyledServiceAsync(context, name, folder, catalog, owners, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        IReadOnlyList<StoredStyle> styles = await catalog.ListStylesAsync(service.Id, cancellation).ConfigureAwait(false);

        if (styles.FirstOrDefault(s => StyleNames.Same(s.Name, style)) is not { } found)
        {
            await Refuse(context, 404, NoStyle(service, style, styles)).ConfigureAwait(false);
            return;
        }

        if (await StyleRefusalAsync(found.Style, service, catalog, origins, cancellation).ConfigureAwait(false)
            is { } stale)
        {
            context.Response.Headers["Graticula-Style-Stale"] =
                Uri.EscapeDataString("Not served: the generated style is, because " + stale);
        }

        context.Response.Headers["Graticula-Style-Default"] = found.IsDefault ? "true" : "false";

        await Results.Content(found.Style, "application/json; charset=utf-8").ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Stores a named style, having checked the name and the document.</summary>
    /// <remarks>
    /// <b>A service with no default takes this one as its default</b> — which is what <c>/style</c>
    /// always did with the first style it stored, and what makes that route an alias rather than a
    /// second meaning. The answer says so, because the author asked to store a style and got a change
    /// to <c>root.json</c> as well.
    /// </remarks>
    private static async Task SetNamedStyleAsync(
        HttpContext context,
        string name,
        string style,
        string? folder,
        IAdminCatalog catalog,
        IAuditLog audit,
        PostgresLayerCatalog owners,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (!StyleNames.TryValidate(style, out string? badName))
        {
            await Refuse(context, 400, badName!).ConfigureAwait(false);
            return;
        }

        if (await ManagedStyledServiceAsync(context, name, folder, catalog, owners, "restyle", cancellation)
                .ConfigureAwait(false) is not { } service)
        {
            return;
        }

        await StoreStyleAsync(context, service, style, catalog, audit, origins, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads, checks and stores a style under a name, and answers — shared by <c>/styles/{style}</c>
    /// and the <c>/style</c> alias.
    /// </summary>
    private static async Task StoreStyleAsync(
        HttpContext context,
        StyledService service,
        string style,
        IAdminCatalog catalog,
        IAuditLog audit,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        // <b>Bounded before it is read, not after.</b> Reading an unbounded body and then measuring it is an
        // accounting exercise: the memory is already spent. <b>Refused when it hits the bound rather than passed
        // on in part</b>, for the reason `SetSymbologyAsync` gives: a truncated document is reported as a
        // malformed one, which sends the reader to look for a bracket they never left out.
        (string body, bool tooLong) =
            await BoundedBodyAsync(context, StyleDocument.MaximumBytes, cancellation).ConfigureAwait(false);

        if (tooLong)
        {
            await Refuse(
                context, 413,
                $"A style document may be at most {StyleDocument.MaximumBytes:N0} characters and this request is "
                + "longer. It was not read rather than being read in part.")
                .ConfigureAwait(false);
            return;
        }

        if (await StyleRefusalAsync(body, service, catalog, origins, cancellation).ConfigureAwait(false) is { } error)
        {
            await Refuse(context, 400, error).ConfigureAwait(false);
            return;
        }

        StyleWrite written = await catalog
            .SetNamedStyleAsync(service.Id, style, body, StyleNames.MostPerService, cancellation)
            .ConfigureAwait(false);

        switch (written)
        {
            case StyleWrite.NoService:
                await Refuse(context, 404, NoService(service.Name, service.Folder)).ConfigureAwait(false);
                return;

            case StyleWrite.TooMany:
                await Refuse(
                    context, 409,
                    $"This service already carries {StyleNames.MostPerService} styles, the most one service may. "
                    + "Remove one first, or replace one by storing under its name.")
                    .ConfigureAwait(false);
                return;
        }

        bool replaced = written is StyleWrite.Replaced or StyleWrite.ReplacedAsDefault;
        bool isDefault = written is StyleWrite.AddedAsDefault or StyleWrite.ReplacedAsDefault
                         || (written is StyleWrite.Replaced && StyleNames.Same(service.StyleName, style));

        // The document is not in the audit record, for the reason the default style's is not.
        await AuditAsync(
            context, audit, "service.style", service.Name,
            Detail(new { folder = service.Folder, style, bytes = body.Length, replaced, isDefault }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            style,
            stored = true,
            bytes = body.Length,
            replaced,
            isDefault,
            note = written is StyleWrite.AddedAsDefault or StyleWrite.ReplacedAsDefault
                ? "The service had no default style, so this one is it now: resources/styles/root.json serves it."
                : null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Removes a named style; the default's removal puts the generated style back at <c>root.json</c>.</summary>
    private static async Task DeleteNamedStyleAsync(
        HttpContext context,
        string name,
        string style,
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

        if (await ManagedStyledServiceAsync(context, name, folder, catalog, owners, "remove a style of", cancellation)
                .ConfigureAwait(false) is not { } service)
        {
            return;
        }

        bool wasDefault = StyleNames.Same(service.StyleName, style);

        if (!await catalog.DeleteNamedStyleAsync(service.Id, style, cancellation).ConfigureAwait(false))
        {
            IReadOnlyList<StoredStyle> styles = await catalog.ListStylesAsync(service.Id, cancellation).ConfigureAwait(false);
            await Refuse(context, 404, NoStyle(service, style, styles)).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.style.clear", service.Name,
            Detail(new { folder = service.Folder, style, wasDefault }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            style,
            removed = true,
            wasDefault,
            note = wasDefault
                ? "That was the default, so resources/styles/root.json serves the generated style again. The "
                  + "service's other styles are kept; make one of them the default to serve it there."
                : null,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Chooses which stored style <c>resources/styles/root.json</c> serves, or the generated one.</summary>
    /// <remarks>
    /// <b>Refused for a style that would not be served</b>, with the reason: promoting a style whose
    /// layers, icons or origins no longer fit makes <c>root.json</c> serve the generated style while the
    /// listing says the chosen one is the default, which is the silent state ADR-028 condition 3 exists
    /// to name. 409, because the request is well formed and what it conflicts with is the service.
    /// </remarks>
    private static async Task SetDefaultStyleAsync(
        HttpContext context,
        string name,
        string? folder,
        DefaultStyleRequest request,
        IAdminCatalog catalog,
        IAuditLog audit,
        PostgresLayerCatalog owners,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        if (await ManagedStyledServiceAsync(context, name, folder, catalog, owners, "choose the default style of", cancellation)
                .ConfigureAwait(false) is not { } service)
        {
            return;
        }

        string? chosen = string.IsNullOrWhiteSpace(request.Style) ? null : request.Style.Trim();

        if (chosen is not null)
        {
            IReadOnlyList<StoredStyle> styles = await catalog.ListStylesAsync(service.Id, cancellation).ConfigureAwait(false);

            if (styles.FirstOrDefault(s => StyleNames.Same(s.Name, chosen)) is not { } found)
            {
                await Refuse(context, 404, NoStyle(service, chosen, styles)).ConfigureAwait(false);
                return;
            }

            if (await StyleRefusalAsync(found.Style, service, catalog, origins, cancellation).ConfigureAwait(false)
                is { } stale)
            {
                await Refuse(
                    context, 409,
                    $"'{found.Name}' cannot be the default, because it would not be served: {stale}")
                    .ConfigureAwait(false);
                return;
            }
        }

        DefaultStyleChange change =
            await catalog.SetDefaultStyleAsync(service.Id, chosen, cancellation).ConfigureAwait(false);

        switch (change)
        {
            case DefaultStyleChange.NoService:
                await Refuse(context, 404, NoService(service.Name, service.Folder)).ConfigureAwait(false);
                return;

            case DefaultStyleChange.NoSuchStyle:
                await Refuse(context, 404, $"No style '{chosen}' on service '{service.Name}'.").ConfigureAwait(false);
                return;
        }

        if (change == DefaultStyleChange.Changed)
        {
            await AuditAsync(
                context, audit, "service.style.default", service.Name,
                Detail(new { folder = service.Folder, from = service.StyleName, to = chosen }),
                succeeded: true, cancellation).ConfigureAwait(false);
        }

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            @default = chosen,
            changed = change == DefaultStyleChange.Changed,
            note = chosen is null
                ? "resources/styles/root.json serves the generated style"
                  + (service.StyleName is { } was ? $"; '{was}' is kept as an ordinary style." : ".")
                : $"resources/styles/root.json serves '{chosen}'"
                  + (service.StyleName is { } before && !StyleNames.Same(before, chosen)
                      ? $"; '{before}' is kept as an ordinary style."
                      : "."),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The refusal for a style name the service does not carry, naming the ones it does.</summary>
    private static string NoStyle(StyledService service, string style, IReadOnlyList<StoredStyle> styles) =>
        $"Service '{service.Name}' has no style '{style}'. "
        + (styles.Count == 0
            ? "It has no stored style at all, and serves the generated one."
            : "It has: " + string.Join(", ", styles.Select(s => s.IsDefault ? s.Name + " (default)" : s.Name)) + ".");

    /// <summary>
    /// The literal icons any of a service's styles draws — what its sprite sheet may not take away (ADR-092,
    /// every style since ADR-094).
    /// </summary>
    /// <param name="styles">The service's stored styles.</param>
    /// <returns>Each icon once, in the order the styles use them, with the styles that draw it.</returns>
    /// <remarks>
    /// <b>Every style, not only the default.</b> A sheet that loses an icon the <c>dark</c> style names leaves
    /// <c>dark</c> drawing nothing where the icon was, and nothing says so — exactly what the check was for,
    /// arriving through a style the check did not read.
    /// <b>Not the generated icons — ADR-099 §5.4.</b> An icon named with <see cref="MarkerPicture.NamePrefix"/> is
    /// packed into the served sheet from a layer's picture marker, never from the uploaded sheet, so no upload can
    /// take it away and no upload is held for it; whether the layers still draw it is checked where the style is
    /// served.
    /// </remarks>
    internal static IReadOnlyList<(string Icon, IReadOnlyList<string> Styles)> IconsTheStylesDraw(
        IReadOnlyList<StoredStyle> styles)
    {
        ArgumentNullException.ThrowIfNull(styles);

        List<(string Icon, List<string> Styles)> uses = [];

        foreach (StoredStyle style in styles)
        {
            foreach (string icon in StyleDocument.LiteralIcons(style.Style))
            {
                if (icon.StartsWith(MarkerPicture.NamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                int at = uses.FindIndex(u => string.Equals(u.Icon, icon, StringComparison.Ordinal));

                if (at < 0)
                {
                    uses.Add((icon, [style.Name]));
                }
                else if (!uses[at].Styles.Contains(style.Name))
                {
                    uses[at].Styles.Add(style.Name);
                }
            }
        }

        return [.. uses.Select(u => (u.Icon, (IReadOnlyList<string>)u.Styles))];
    }

    /// <summary>The icons a sheet lacks that some style draws, with the styles that draw each.</summary>
    /// <param name="uses">What <see cref="IconsTheStylesDraw"/> found.</param>
    /// <param name="icons">The sheet's icon names.</param>
    /// <returns>The missing ones, in the order the styles use them.</returns>
    internal static IReadOnlyList<(string Icon, IReadOnlyList<string> Styles)> IconsTheSheetLacks(
        IReadOnlyList<(string Icon, IReadOnlyList<string> Styles)> uses, IReadOnlyCollection<string> icons)
    {
        HashSet<string> has = new(icons, StringComparer.Ordinal);

        return [.. uses.Where(use => !has.Contains(use.Icon))];
    }

    /// <summary>Names icons and the styles drawing them, for a refusal.</summary>
    private static string Drawn(IReadOnlyList<(string Icon, IReadOnlyList<string> Styles)> uses)
    {
        IEnumerable<string> styles = uses.SelectMany(u => u.Styles).Distinct(StringComparer.OrdinalIgnoreCase);

        return Quoted([.. uses.Select(u => u.Icon)])
               + " by name, in the style" + (styles.Count() == 1 ? " " : "s ")
               + string.Join(", ", styles.Select(n => $"'{n}'"));
    }
}
