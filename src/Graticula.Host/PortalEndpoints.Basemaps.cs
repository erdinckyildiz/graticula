using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The basemap gallery, as a portal client reads it — ADR-184.
/// </summary>
/// <remarks>
/// <para>
/// <b>A vector tile service shared with the gallery group is answered as a Web Map</b>, because that is the only
/// item type an ArcGIS client's gallery keeps: the Maps SDK's <c>fetchBasemaps</c> asks the group for
/// <c>type:"Web Map"</c> and drops everything else. The item's id is the service's <see cref="BasemapFace"/> face
/// id and its data is a Web Map whose <c>baseMap</c> is that one service.
/// </para>
/// <para>
/// <b>Found by looking in the gallery.</b> The entries are listed only by a search that names the gallery group, so
/// the portal's other listings — Pro's content, OGC API Records — do not show every gallery service twice.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The face name a gallery entry's item id is derived from.</summary>
    internal const string BasemapFace = "Basemap";

    /// <summary>The gallery as chosen, read once per request.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The reading.</returns>
    internal static async Task<BasemapGallery.Reading> GalleryAsync(HttpContext context, CancellationToken cancellation)
    {
        if (context.Items[typeof(BasemapGallery.Reading)] is BasemapGallery.Reading held)
        {
            return held;
        }

        BasemapGallery.Reading reading = context.RequestServices.GetService<BasemapGallery>() is { } gallery
            ? await gallery.ReadAsync(cancellation).ConfigureAwait(false)
            : new BasemapGallery.Reading(null, null, null);

        context.Items[typeof(BasemapGallery.Reading)] = reading;
        return reading;
    }

    /// <summary>Whether a service is a basemap of a gallery: shared with it, running, and drawable as one.</summary>
    /// <remarks>
    /// <b>Web Mercator only</b>, for ADR-086's reason (ADR-096): a basemap is drawn beneath Web Mercator maps, and a
    /// client cannot move a vector tile from one grid to another.
    /// </remarks>
    /// <param name="service">The service.</param>
    /// <param name="gallery">The gallery group.</param>
    /// <returns>Whether it is an entry.</returns>
    internal static bool IsBasemap(PublishedService service, Guid gallery) =>
        service.SharedWith.Contains(gallery)
        && service.IsRunning
        && service.Limits.AllowsTiles(dataSupportsIt: true)
        && ServiceFaces.Tileable(service)
        && ServiceFaces.AnswersTiles(service)
        && service.TileScheme.IsWebMercator;

    /// <summary>Whether a search asks for the gallery's contents: its query names the gallery group.</summary>
    private static bool AsksForGallery(string? query, Guid gallery) =>
        query is { Length: > 0 }
        && (query.Contains(gallery.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || query.Contains(gallery.ToString("D"), StringComparison.OrdinalIgnoreCase));

    /// <summary>A service's <c>baseMap</c>: its one vector tile layer.</summary>
    /// <param name="context">The request, for the origin.</param>
    /// <param name="service">The service.</param>
    /// <returns>The Web Map <c>baseMap</c> object.</returns>
    internal static object BaseMapOf(HttpContext context, PublishedService service)
    {
        string url = $"{Origin(context)}/rest/services/{service.QualifiedName}/VectorTileServer";

        return new
        {
            id = ItemIdOf(service, BasemapFace),
            title = service.Name,
            baseMapLayers = new object[]
            {
                new
                {
                    // The qualified name, as ADR-086's ground spells it, so the pages' ground reader takes it as is.
                    id = service.QualifiedName,
                    layerType = "VectorTileLayer",
                    title = service.Name,
                    url,
                    styleUrl = $"{url}/resources/styles/root.json",
                    visibility = true,
                    opacity = 1,
                },
            },
        };
    }

    /// <summary>A gallery service as the Web Map item a gallery reads.</summary>
    /// <param name="context">The request.</param>
    /// <param name="service">The service.</param>
    /// <returns>The item.</returns>
    private static object BasemapItem(HttpContext context, PublishedService service)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        return new
        {
            id = ItemIdOf(service, BasemapFace),
            orgId = PortalId(context),
            owner = service.Owner is { } id && id == current.Principal.Id ? current.Principal.Name : "graticula",
            ownerFolder = (string?)null,
            title = service.Name,
            name = (string?)null,
            type = "Web Map",
            typeKeywords = WebMapKeywords,
            description = service.Description,
            snippet = service.Description,
            accessInformation = service.CopyrightText,
            tags = service.Tags.ToArray(),
            categories = Array.Empty<string>(),
            url = (string?)null,
            thumbnail = Pictured(service) is null ? null : $"thumbnail/{ThumbnailFile}",
            access = Access(service.Sharing),
            spatialReference = (string?)null,
            extent = Array.Empty<double[]>(),
            numViews = 0,
            size = -1,
            created = service.Created?.ToUnixTimeMilliseconds(),
            modified = service.Modified?.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>A gallery entry's data: a Web Map with nothing over its basemap.</summary>
    private static object BasemapData(HttpContext context, PublishedService service) => new
    {
        operationalLayers = Array.Empty<object>(),
        baseMap = BaseMapOf(context, service),
        spatialReference = new { wkid = 102100, latestWkid = 3857 },
        version = "2.31",
        authoringApp = "Graticula",
        authoringAppVersion = PortalVersion,
    };

    /// <summary>The gallery service a basemap item id names, among those the caller may see, or null.</summary>
    private static async Task<PublishedService?> BasemapServiceAsync(
        HttpContext context, IReadOnlyList<PublishedService> visible, string id, CancellationToken cancellation)
    {
        if ((await GalleryAsync(context, cancellation).ConfigureAwait(false)).Group is not { } gallery)
        {
            return null;
        }

        return visible.FirstOrDefault(service =>
            IsBasemap(service, gallery)
            && string.Equals(ItemIdOf(service, BasemapFace), id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The gallery's entries a search lists, when the search asks for the gallery.</summary>
    private static async Task<IEnumerable<PortalListed>> BasemapsListedAsync(
        HttpContext context, IReadOnlyList<PublishedService> visible, string? query, CancellationToken cancellation)
    {
        if ((await GalleryAsync(context, cancellation).ConfigureAwait(false)).Group is not { } gallery
            || !AsksForGallery(query, gallery))
        {
            return [];
        }

        return
        [
            .. visible
                .Where(service => IsBasemap(service, gallery))
                .Select(service => (service, item: BasemapItem(context, service)))
                .Where(pair => PortalQuery.Matches(pair.item, query, pair.service.SharedWith))
                .Select(pair => new PortalListed(ItemIdOf(pair.service, BasemapFace), pair.item, Service: pair.service, Face: BasemapFace)),
        ];
    }

    /// <summary>
    /// The default basemap the gallery chose, as this caller may draw it, or null to fall to ADR-086's order.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="reading">The gallery as chosen.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A <c>baseMap</c>, or null.</returns>
    private static async Task<object?> ChosenBasemapAsync(
        HttpContext context, BasemapGallery.Reading reading, CancellationToken cancellation)
    {
        if (reading.Default is not { } chosen)
        {
            return null;
        }

        if (string.Equals(chosen, BasemapGallery.OpenStreetMap, StringComparison.OrdinalIgnoreCase))
        {
            return OpenStreetMapBasemap;
        }

        if (reading.Group is not { } gallery)
        {
            return null;
        }

        IServiceProvider services = context.RequestServices;

        if (await ReadableMapAsync(context, services.GetRequiredService<IWebMapStore>(), chosen, cancellation)
                .ConfigureAwait(false) is { } map
            && map.SharedWith?.Contains(gallery) == true
            && BaseMapOfDocument(map.Document) is { } fromMap)
        {
            return fromMap;
        }

        IReadOnlyList<PublishedService>? visible = await VisibleAsync(
            context, services.GetRequiredService<Graticula.Platform.Postgres.CatalogFallback>(), cancellation)
            .ConfigureAwait(false);

        return visible is not null
            && await BasemapServiceAsync(context, visible, chosen, cancellation).ConfigureAwait(false) is { } service
            ? BaseMapOf(context, service)
            : null;
    }

    /// <summary>A saved web map's <c>baseMap</c>, or null when it has none with a layer in it.</summary>
    private static JsonNode? BaseMapOfDocument(string? document)
    {
        try
        {
            return JsonNode.Parse(document ?? "{}")?["baseMap"] is JsonObject baseMap
                && baseMap["baseMapLayers"] is JsonArray { Count: > 0 }
                ? baseMap.DeepClone()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static readonly object OpenStreetMapBasemap = new
    {
        id = "graticula-osm",
        title = "OpenStreetMap",
        baseMapLayers = new object[]
        {
            new { id = "osm", layerType = "OpenStreetMap", title = "OpenStreetMap", visibility = true, opacity = 1 },
        },
    };

    /// <summary>
    /// The gallery group, for a signed-in caller outside it, so that a portal naming it can be followed — ADR-184 §5.4.
    /// </summary>
    /// <remarks>
    /// <b>Never to a signed-out caller — owner decision, 2026-10-15, Q-174:</b> *"giriş yapmamış kullanıcı basemap
    /// seçemesin"*. Q-119's rule that no group is found by anybody stands; a signed-out caller is given the default
    /// basemap and no gallery to choose from.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="directory">Where groups live.</param>
    /// <param name="already">The groups the caller is already given.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The gallery group, or null when there is none or the caller already has it.</returns>
    private static async Task<object?> GalleryGroupAsync(
        HttpContext context, IGroupDirectory directory, IReadOnlyList<object> already, CancellationToken cancellation)
    {
        if (context.Features.Get<RequestPrincipal>()?.Principal.IsAnonymous != false
            || (await GalleryAsync(context, cancellation).ConfigureAwait(false)).Group is not { } gallery)
        {
            return null;
        }

        string id = gallery.ToString("N");

        if (already.Any(group => PortalQuery.Matches(group, $"id:{id}")))
        {
            return null;
        }

        // Every group, read by the server rather than by the caller: the caller is shown this one only.
        GroupSummary? summary = (await directory.ListAsync(Guid.Empty, all: true, cancellation).ConfigureAwait(false))
            .FirstOrDefault(group => group.Id == gallery);

        return summary is null ? null : Group(context, summary with { Standing = GroupStanding.Outside });
    }
}
