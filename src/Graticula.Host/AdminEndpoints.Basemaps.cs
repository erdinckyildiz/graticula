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

/// <summary>The basemap gallery and the default basemap — ADR-184.</summary>
/// <param name="Gallery">The gallery group's id or name; null or empty for no gallery.</param>
/// <param name="Default">
/// <c>osm</c>, a gallery entry's item id, or null or empty for ADR-086's order: the ground, then OpenStreetMap.
/// </param>
internal sealed record BasemapsRequest(string? Gallery, string? Default);

/// <summary>
/// The basemap gallery an administrator chooses — ADR-184: a group whose vector tile services and web maps are the
/// basemaps every map offers, and the one a new map starts on.
/// </summary>
/// <remarks>In its own file for the reason <c>AdminEndpoints.FieldOverrides.cs</c> gives.</remarks>
internal static partial class AdminEndpoints
{
    private static void MapBasemapGallery(WebApplication app)
    {
        app.MapGet("/admin/settings/basemaps", BasemapsAsync);
        app.MapPut("/admin/settings/basemaps", SetBasemapsAsync);
    }

    /// <summary>The gallery, its entries, the default, and the groups that could be the gallery.</summary>
    private static async Task BasemapsAsync(
        HttpContext context,
        BasemapGallery gallery,
        IGroupDirectory directory,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        IWebMapStore maps,
        Graticula.Platform.Catalog.ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        BasemapGallery.Reading reading = await gallery.ReadAsync(cancellation).ConfigureAwait(false);
        IReadOnlyList<GroupSummary> groups = await directory.ListAsync(Guid.Empty, all: true, cancellation)
            .ConfigureAwait(false);

        // <b>`?gallery=` shows another group's entries without storing it</b>, so the screen can say what a group
        // would offer before the administrator saves it as the gallery.
        if (Guid.TryParse(context.Request.Query["gallery"].ToString(), out Guid preview)
            && groups.Any(g => g.Id == preview))
        {
            reading = reading with { Group = preview };
        }

        await Results.Json(await DescribeBasemapsAsync(context, reading, groups, catalog, maps, coverages, cancellation)
                .ConfigureAwait(false))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the gallery and the default together, since a default is an entry <em>of</em> a gallery.
    /// </summary>
    /// <remarks>
    /// <b>The default has to be an entry when it is saved</b>, as ADR-086 requires of a ground's services: a default
    /// naming nothing is stored silently and every map starts on OpenStreetMap without anybody being told why.
    /// </remarks>
    private static async Task SetBasemapsAsync(
        HttpContext context,
        BasemapsRequest request,
        BasemapGallery gallery,
        IGroupDirectory directory,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        IWebMapStore maps,
        Graticula.Platform.Catalog.ICoverageCatalog coverages,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<GroupSummary> groups = await directory.ListAsync(Guid.Empty, all: true, cancellation)
            .ConfigureAwait(false);

        string asked = request.Gallery?.Trim() ?? string.Empty;
        GroupSummary? group = null;

        if (asked.Length > 0)
        {
            group = groups.FirstOrDefault(g =>
                (Guid.TryParse(asked, out Guid id) && g.Id == id)
                || string.Equals(g.Name, asked, StringComparison.OrdinalIgnoreCase));

            if (group is null)
            {
                await Refuse(context, 400, $"There is no group '{asked}' on this server.").ConfigureAwait(false);
                return;
            }
        }

        string? chosen = string.IsNullOrWhiteSpace(request.Default) ? null : request.Default.Trim();

        if (chosen is not null && !string.Equals(chosen, BasemapGallery.OpenStreetMap, StringComparison.OrdinalIgnoreCase))
        {
            if (group is null)
            {
                await Refuse(context, 400,
                    "A default basemap other than OpenStreetMap is one of the gallery's; choose a gallery group first.")
                    .ConfigureAwait(false);
                return;
            }

            IReadOnlyList<PortalEndpoints.PortalListed> entries = await GalleryEntriesAsync(
                context, group.Id, catalog, maps, coverages, cancellation).ConfigureAwait(false);

            if (!entries.Any(e => string.Equals(e.Id, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                await Refuse(context, 400,
                    $"'{chosen}' is not in the gallery '{group.Title ?? group.Name}'. A basemap is a vector tile "
                    + "service tiled in Web Mercator, or a saved web map, shared with that group.").ConfigureAwait(false);
                return;
            }

            chosen = entries.First(e => string.Equals(e.Id, chosen, StringComparison.OrdinalIgnoreCase)).Id;
        }
        else if (chosen is not null)
        {
            chosen = BasemapGallery.OpenStreetMap;
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();
        BasemapGallery.Reading before = await gallery.SetAsync(group?.Id, chosen, current?.Principal.Id, cancellation)
            .ConfigureAwait(false);

        await AuditAsync(context, audit, "server.settings", BasemapGallery.GalleryName,
            Detail(new
            {
                from = new { gallery = before.Group?.ToString("N"), @default = before.Default },
                to = new { gallery = group?.Id.ToString("N"), @default = chosen },
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        BasemapGallery.Reading after = await gallery.ReadAsync(cancellation).ConfigureAwait(false);
        await Results.Json(await DescribeBasemapsAsync(context, after, groups, catalog, maps, coverages, cancellation)
                .ConfigureAwait(false))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The gallery's entries as this administrator sees them: the portal's own search of the group.</summary>
    private static async Task<IReadOnlyList<PortalEndpoints.PortalListed>> GalleryEntriesAsync(
        HttpContext context,
        Guid group,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        IWebMapStore maps,
        Graticula.Platform.Catalog.ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        // The reading this request's portal helpers use is the one just stored or about to be — not the old one.
        context.Items[typeof(BasemapGallery.Reading)] = new BasemapGallery.Reading(group, null, null);

        IReadOnlyList<PortalEndpoints.PortalListed>? listed = await PortalEndpoints
            .ListAsync(context, catalog, maps, coverages, $"group:{group:N} AND type:\"Web Map\"", cancellation)
            .ConfigureAwait(false);

        context.Items.Remove(typeof(BasemapGallery.Reading));
        return listed ?? [];
    }

    private static async Task<object> DescribeBasemapsAsync(
        HttpContext context,
        BasemapGallery.Reading reading,
        IReadOnlyList<GroupSummary> groups,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        IWebMapStore maps,
        Graticula.Platform.Catalog.ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        GroupSummary? group = reading.Group is { } id ? groups.FirstOrDefault(g => g.Id == id) : null;

        IReadOnlyList<PortalEndpoints.PortalListed> entries = group is null
            ? []
            : await GalleryEntriesAsync(context, group.Id, catalog, maps, coverages, cancellation).ConfigureAwait(false);

        return new
        {
            gallery = group is null ? null : new { id = group.Id.ToString("N"), name = group.Name, title = group.Title ?? group.Name },

            // A gallery group that has since been deleted is said, rather than shown as no gallery.
            galleryMissing = reading.Group is not null && group is null,
            @default = reading.Default,
            defaultMissing = reading.Default is { } d
                && !string.Equals(d, BasemapGallery.OpenStreetMap, StringComparison.OrdinalIgnoreCase)
                && !entries.Any(e => string.Equals(e.Id, d, StringComparison.OrdinalIgnoreCase)),
            entries = entries.Select(e => new
            {
                id = e.Id,
                title = e.Service?.Name ?? e.Map?.Title,
                kind = e.Service is not null ? "service" : "map",
                service = e.Service?.QualifiedName,
                sharing = (e.Service?.Sharing ?? e.Map?.Sharing)?.ToString().ToLowerInvariant(),
            }),
            groups = groups.OrderBy(g => g.Title ?? g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { id = g.Id.ToString("N"), name = g.Name, title = g.Title ?? g.Name }),
            changedAt = reading.ChangedAt,
        };
    }
}
