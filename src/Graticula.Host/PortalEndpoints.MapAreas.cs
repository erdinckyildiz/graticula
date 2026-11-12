using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>
/// A preplanned map area and its packages as portal items — ADR-288 §11a: the shape research note §5a read on an
/// Enterprise portal, with the sharing of the map they belong to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Visible exactly when the map is.</b> Neither an area nor a package carries a sharing of its own that anything reads:
/// each is found through its map with <see cref="ReadableMapAsync"/>, the rule a web map is opened by, so a map made
/// public or taken private shows or hides its areas without anything being rewritten. The answer for an item the caller may
/// not open is the answer for an item that does not exist.
/// </para>
/// <para>
/// <b>The owner is the map's</b> (INFERRED): the item says who the caller is only when it is their map, as every item
/// does. The replica behind a package is owned by whoever packaged the area.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The type ArcGIS gives a preplanned map area.</summary>
    internal const string MapAreaType = "Map Area";

    /// <summary>The type ArcGIS gives a package of a feature service's data taken for offline use.</summary>
    internal const string MapAreaPackageType = "SQLite Geodatabase";

    /// <summary>The type of a package of a basemap's vector tiles (ADR-288 §11a).</summary>
    internal const string MapAreaTilePackageType = "Vector Tile Package";

    /// <summary>The item type a package is, by its format.</summary>
    /// <param name="package">The package.</param>
    /// <returns>The type.</returns>
    internal static string PackageTypeOf(MapAreaPackage package) =>
        package.Format == MapAreas.VtpkFormat ? MapAreaTilePackageType : MapAreaPackageType;

    /// <summary>The relationship types a map area keeps, in addition to a service's.</summary>
    private static readonly string[] MapAreaRelationships = [MapAreas.Map2Area, MapAreas.Area2Package];

    /// <summary>The service address a package states in its properties, or empty.</summary>
    internal static string PackageUrlOf(MapAreaPackage package) => MapAreaPackaging.PackageUrlOf(package);

    /// <summary>An area's extent as ArcGIS's envelope, as it was kept.</summary>
    internal static object EnvelopeOf(MapArea area) => new
    {
        xmin = area.XMin,
        ymin = area.YMin,
        xmax = area.XMax,
        ymax = area.YMax,
        spatialReference = new { wkid = area.Wkid },
    };

    /// <summary>
    /// An area's extent as ArcGIS's envelope <b>in its map's spatial reference</b> when the two differ only between
    /// geographic WGS 84 and Web Mercator, which move by a formula; as it was kept otherwise.
    /// </summary>
    /// <remarks>
    /// <b>Measured 2026-11-12.</b> Field Maps took a preplanned area of a Web Mercator map whose extent was kept in degrees,
    /// then refused every location in it — <i>"The location is outside the map area"</i> — because it compared metres with
    /// degrees. The area Enterprise packaged (research note §5a) stated its extent in its map's reference, 102100.
    /// </remarks>
    internal static object EnvelopeOf(MapArea area, WebMap map)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(map);

        int? mapWkid = MapWkidOf(map.Document);
        const double radius = 6378137.0;
        bool areaMercator = area.Wkid is 3857 or 102100 or 102113;
        bool mapMercator = mapWkid is 3857 or 102100 or 102113;

        if (area.Wkid == 4326 && mapMercator)
        {
            static double X(double lon) => lon * Math.PI / 180.0 * radius;
            static double Y(double lat) => Math.Log(Math.Tan((Math.PI / 4.0) + (Math.Clamp(lat, -85.05112878, 85.05112878) * Math.PI / 360.0))) * radius;
            return new
            {
                xmin = X(area.XMin), ymin = Y(area.YMin), xmax = X(area.XMax), ymax = Y(area.YMax),
                spatialReference = new { wkid = 102100, latestWkid = 3857 },
            };
        }

        if (areaMercator && mapWkid == 4326)
        {
            double[][] degrees = DegreesOf(area);
            return new
            {
                xmin = degrees[0][0], ymin = degrees[0][1], xmax = degrees[1][0], ymax = degrees[1][1],
                spatialReference = new { wkid = 4326 },
            };
        }

        return EnvelopeOf(area);
    }

    /// <summary>The wkid a web map document states for itself, or null.</summary>
    internal static int? MapWkidOf(string? document)
    {
        try
        {
            return JsonNode.Parse(document ?? "{}") is JsonObject root
                && root["spatialReference"] is JsonObject sr
                && (sr["wkid"] ?? sr["latestWkid"]) is JsonValue v && v.TryGetValue(out int wkid)
                ? wkid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An area's extent as the item's own <c>extent</c>, <c>[[xmin, ymin], [xmax, ymax]]</c> in degrees, or empty when the
    /// area is in a reference this does not move by a formula (geographic WGS 84 and Web Mercator are).
    /// </summary>
    /// <param name="area">The area.</param>
    /// <returns>The corners, or none.</returns>
    internal static double[][] DegreesOf(MapArea area)
    {
        ArgumentNullException.ThrowIfNull(area);

        switch (area.Wkid)
        {
            case 4326:
                return [[area.XMin, area.YMin], [area.XMax, area.YMax]];

            case 3857 or 102100 or 102113:
                const double radius = 6378137.0;
                static double Lon(double x) => x / radius * 180.0 / Math.PI;
                static double Lat(double y) => ((2.0 * Math.Atan(Math.Exp(y / radius))) - (Math.PI / 2.0)) * 180.0 / Math.PI;
                return [[Lon(area.XMin), Lat(area.YMin)], [Lon(area.XMax), Lat(area.YMax)]];

            default:
                return [];
        }
    }

    /// <summary>The kinds of item a package is, as ArcGIS lists them (research note §5a).</summary>
    /// <param name="package">The package.</param>
    /// <returns>The keywords.</returns>
    internal static string[] PackageKeywords(MapAreaPackage package) =>
        package.Format == MapAreas.VtpkFormat

            // INFERRED: the keywords of a map area's tile package were read only for a raster `Tile Package` (research note
            // §5a: its source as `ReferenceURL#`); a vector tile package takes the same two marks and the keywords this
            // server's own VTPK carries (`Vector Tile Package`, `vtpk`).
            ? ["MapAreaPackage", "Package", "ReferenceURL#" + PackageUrlOf(package), "Vector Tile Package", "vtpk"]
            : ["Bidirectional", "Data", "MapAreaPackage", "Package", "ReferenceURL#" + PackageUrlOf(package), "Replica", "SQLite Geodatabase", "Syncable"];

    /// <summary>A map area as a portal item of type <c>Map Area</c>.</summary>
    internal static object MapAreaItem(HttpContext context, WebMap map, MapArea area)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool mine = !current.Principal.IsAnonymous && map.Owner == current.Principal.Id;

        return new
        {
            id = area.Id.ToString("N"),
            orgId = PortalId(context),
            owner = mine ? current.Principal.Name : "graticula",
            itemControl = mine ? "admin" : null,
            isOrgItem = true,
            title = area.Title,
            name = (string?)null,
            type = MapAreaType,
            typeKeywords = new[] { "Map", "Map Area" },
            description = (string?)null,
            thumbnail = (string?)null,
            snippet = (string?)null,
            tags = Array.Empty<string>(),
            categories = Annotations(context).CategoriesOf(area.Id),
            licenseInfo = Annotations(context).LicenseOf(area.Id),
            url = (string?)null,
            access = Access(map.Sharing),
            @protected = false,
            spatialReference = (string?)null,
            extent = DegreesOf(area),
            properties = new { extent = EnvelopeOf(area, map), status = area.Status },
            numViews = 0,
            size = -1,
            created = area.Created.ToUnixTimeMilliseconds(),
            modified = area.Refreshed.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>A package as a portal item of type <c>SQLite Geodatabase</c>.</summary>
    internal static object MapAreaPackageItem(HttpContext context, WebMap map, MapArea area, MapAreaPackage package, string title)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool mine = !current.Principal.IsAnonymous && map.Owner == current.Principal.Id;

        // What the replica (or the tile package) was made of, as it was kept, and the area's extent beside it.
        JsonObject properties = JsonNode.Parse(package.Properties) as JsonObject ?? [];
        properties["extent"] = JsonSerializer.SerializeToNode(EnvelopeOf(area, map));

        return new
        {
            id = package.Id.ToString("N"),
            orgId = PortalId(context),
            owner = mine ? current.Principal.Name : "graticula",
            itemControl = mine ? "admin" : null,
            isOrgItem = true,
            title,
            name = package.FileName,
            type = PackageTypeOf(package),
            typeKeywords = PackageKeywords(package),
            description = (string?)null,
            thumbnail = (string?)null,
            snippet = (string?)null,
            tags = Array.Empty<string>(),
            categories = Annotations(context).CategoriesOf(package.Id),
            licenseInfo = Annotations(context).LicenseOf(package.Id),
            url = (string?)null,
            access = Access(map.Sharing),
            @protected = false,
            spatialReference = (string?)null,
            extent = DegreesOf(area),
            properties,
            numViews = 0,
            size = package.Size,
            created = package.Created.ToUnixTimeMilliseconds(),
            modified = package.Refreshed.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>What an id names among the map areas: an area or a package, found through a map the caller may open.</summary>
    /// <param name="Item">The item as the portal writes it.</param>
    /// <param name="Id">The item's id.</param>
    /// <param name="Area">The area, or the area the package belongs to.</param>
    /// <param name="Package">The package, or null for an area.</param>
    internal sealed record MapAreaFound(object Item, Guid Id, MapArea Area, MapAreaPackage? Package);

    /// <summary>
    /// The map area or package an id names, if the caller may open its map; null otherwise — which is also the answer for
    /// an id that names neither.
    /// </summary>
    internal static async Task<MapAreaFound?> ReadableMapAreaAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (!Guid.TryParse(id, out Guid guid)
            || context.RequestServices.GetService<IMapAreaStore>() is not { } areas
            || context.RequestServices.GetService<IWebMapStore>() is not { } maps)
        {
            return null;
        }

        string? kind = await areas.ItemKindAsync(guid, cancellation).ConfigureAwait(false);

        if (kind == MapAreas.AreaKind
            && await areas.FindAsync(guid, cancellation).ConfigureAwait(false) is { } area
            && await ReadableMapAsync(context, maps, area.MapId, cancellation).ConfigureAwait(false) is { } areaMap)
        {
            return new MapAreaFound(MapAreaItem(context, areaMap, area), guid, area, null);
        }

        if (kind == MapAreas.PackageKind
            && await areas.FindPackageAsync(guid, cancellation).ConfigureAwait(false) is { } package
            && await areas.FindAsync(package.AreaId, cancellation).ConfigureAwait(false) is { } owning
            && await ReadableMapAsync(context, maps, package.MapId, cancellation).ConfigureAwait(false) is { } packageMap)
        {
            return new MapAreaFound(MapAreaPackageItem(context, packageMap, owning, package, MapAreaPackaging.TitleOf(owning.Title, PackageUrlOf(package))),
                guid, owning, package);
        }

        return null;
    }

    /// <summary>
    /// Every map area and package of these maps as portal items, for a listing: one query for the areas and one for the
    /// packages.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="maps">The maps the caller may open (and so whose areas they may).</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The items with their ids.</returns>
    private static async Task<IReadOnlyList<(object Item, Guid Id)>> MapAreaItemsAsync(
        HttpContext context, WebMap[] maps, CancellationToken cancellation)
    {
        if (maps.Length == 0 || context.RequestServices.GetService<IMapAreaStore>() is not { } areas)
        {
            return [];
        }

        IReadOnlyList<MapArea> held = await areas.ListAsync([.. maps.Select(m => m.Id)], cancellation).ConfigureAwait(false);

        if (held.Count == 0)
        {
            return [];
        }

        IReadOnlyList<MapAreaPackage> packages = await areas.PackagesAsync([.. held.Select(a => a.Id)], cancellation).ConfigureAwait(false);
        Dictionary<string, WebMap> byId = maps.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        List<(object Item, Guid Id)> items = [];

        foreach (MapArea area in held)
        {
            WebMap map = byId[area.MapId];
            items.Add((MapAreaItem(context, map, area), area.Id));

            foreach (MapAreaPackage package in packages.Where(p => p.AreaId == area.Id))
            {
                items.Add((MapAreaPackageItem(context, map, area, package, MapAreaPackaging.TitleOf(area.Title, PackageUrlOf(package))), package.Id));
            }
        }

        return items;
    }

    /// <summary>
    /// A package's file, streamed with the range support a large download needs, or a refusal when the file is not held.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="package">The package.</param>
    /// <returns>The result.</returns>
    internal static IResult MapAreaPackageFile(HttpContext context, MapAreaPackage package)
    {
        MapAreaPackageFiles files = context.RequestServices.GetRequiredService<MapAreaPackageFiles>();

        if (files.PathOf(package.FilePath) is not { } path || !System.IO.File.Exists(path))
        {
            // A real 404 with the portal's body, not the portal's 200: a client downloading a file reads the status first.
            return Results.Json(
                new { error = new { code = 404, message = "This package's file is no longer held. Refresh the map area to write it again.", details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.File(
            path,
            package.Format == MapAreas.VtpkFormat ? TileExportPackage.MediaType(Graticula.Platform.Jobs.TileExportFormat.Vtpk) : "application/x-sqlite3",
            package.FileName,
            lastModified: package.Refreshed,
            entityTag: new EntityTagHeaderValue("\"" + package.Sha256 + "\""),
            enableRangeProcessing: true);
    }

    /// <summary>The item an id names among web maps, map areas and packages, for <c>relatedItems</c>; null when none the caller may open.</summary>
    internal static async Task<Guid?> MapAreaRelatableAsync(HttpContext context, string id, CancellationToken cancellation)
    {
        if (context.RequestServices.GetService<IWebMapStore>() is { } maps
            && await ReadableMapAsync(context, maps, id, cancellation).ConfigureAwait(false) is { } map)
        {
            return Guid.Parse(map.Id);
        }

        return await ReadableMapAreaAsync(context, id, cancellation).ConfigureAwait(false) is { } found ? found.Id : null;
    }

    /// <summary>
    /// One end of a map area relationship as an item, if the caller may open it: a web map, an area or a package.
    /// </summary>
    internal static async Task<object?> MapAreaRelatedItemAsync(HttpContext context, Guid other, CancellationToken cancellation)
    {
        string id = other.ToString("N");

        if (context.RequestServices.GetService<IWebMapStore>() is { } maps
            && await ReadableMapAsync(context, maps, id, cancellation).ConfigureAwait(false) is { } map)
        {
            return MapItem(
                context, map, MapExtent(map.Document),
                tasks: (await TaskedMapsAsync(context, [map], cancellation).ConfigureAwait(false)).Count > 0,
                offline: (await MapFactsAsync(context, [map], cancellation).ConfigureAwait(false)).Offline.Count > 0);
        }

        return (await ReadableMapAreaAsync(context, id, cancellation).ConfigureAwait(false))?.Item;
    }

    /// <summary>The words of the relationship types a map area keeps, matched without regard to case.</summary>
    /// <param name="type">What was asked.</param>
    /// <returns>The type, or null when it is not one.</returns>
    internal static string? MapAreaRelationshipNamed(string type) =>
        MapAreaRelationships.FirstOrDefault(r => r.Equals(type, StringComparison.OrdinalIgnoreCase));
}
