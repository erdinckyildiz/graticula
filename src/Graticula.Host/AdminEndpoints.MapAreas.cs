using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>A new map area: its title and its extent as an ArcGIS envelope.</summary>
/// <param name="Title">What the area is called.</param>
/// <param name="Extent"><c>{"xmin","ymin","xmax","ymax","spatialReference":{"wkid"}}</c>.</param>
internal sealed record MapAreaRequest(string? Title, JsonNode? Extent);

/// <summary>
/// Preplanned map areas of a saved web map — ADR-288 §11a: <c>POST /admin/maps/{id}/areas</c> makes an area and packages it,
/// <c>refresh</c> writes its packages again, <c>DELETE</c> removes it, and <c>GET</c> lists them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The map's owner or an administrator</b> adds, refreshes and deletes (<see cref="LayerAccess.MayManage"/>), as they
/// change the map; anybody who may open the map lists its areas. A map the caller cannot open is a 404, the answer for a
/// map that does not exist.
/// </para>
/// <para>
/// <b>One call, not ArcGIS's two.</b> Enterprise splits making an area (<c>CreateMapArea</c>) from packaging it
/// (<c>SetupMapArea</c>) in a packaging service this server does not have (D-404); here one request does both, so an area
/// is never left without its packages.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapMapAreas(WebApplication app)
    {
        app.MapGet("/admin/maps/{id}/areas", ListMapAreasAsync);
        app.MapPost("/admin/maps/{id}/areas", CreateMapAreaAsync);
        app.MapPost("/admin/maps/{id}/areas/{areaId:guid}/refresh", RefreshMapAreaAsync);
        app.MapDelete("/admin/maps/{id}/areas/{areaId:guid}", DeleteMapAreaAsync);
    }

    private static async Task ListMapAreasAsync(
        HttpContext context, string id, IWebMapStore maps, IMapAreaStore areas, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map || !Readable(current, map))
        {
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<MapArea> held = await areas.ListAsync([map.Id], cancellation).ConfigureAwait(false);
        IReadOnlyList<MapAreaPackage> packages = await areas.PackagesAsync([.. held.Select(a => a.Id)], cancellation).ConfigureAwait(false);
        IOfflineSyncStore sync = context.RequestServices.GetRequiredService<IOfflineSyncStore>();
        List<object> described = [];

        foreach (MapArea area in held)
        {
            described.Add(await DescribeAreaAsync(area, packages.Where(p => p.AreaId == area.Id), sync, [], cancellation).ConfigureAwait(false));
        }

        await Results.Json(new { areas = described, mayManage = LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization) })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateMapAreaAsync(
        HttpContext context, string id, MapAreaRequest? request, IWebMapStore maps, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapForAreasAsync(context, current, id, "add an area to", maps, audit, cancellation).ConfigureAwait(false)
            is not { } map)
        {
            return;
        }

        string title = (request?.Title ?? string.Empty).Trim();

        if (title.Length == 0 || title.Length > WebMaps.MaximumTitleLength)
        {
            await Refuse(context, 400, $"title is required, and at most {WebMaps.MaximumTitleLength} characters.").ConfigureAwait(false);
            return;
        }

        if (!TryReadAreaExtent(request?.Extent, out Envelope extent, out int wkid, out string? why))
        {
            await Refuse(context, 400, why!).ConfigureAwait(false);
            return;
        }

        (PackagedArea? packaged, PackageRefusal? refusal) = await MapAreaPackaging.CreateAsync(
            context, map, title, extent, wkid, cancellation).ConfigureAwait(false);

        if (packaged is null)
        {
            await AuditAsync(context, audit, "webmap.area.create", id,
                Detail(new { title, refused = refusal!.Message }), succeeded: false, cancellation).ConfigureAwait(false);
            await Refuse(context, refusal.Status, refusal.Message).ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "webmap.area.create", id,
            Detail(new { area = packaged.Area.Id, title, packages = packaged.Packages.Count, skipped = packaged.Skipped.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.Headers.Location = $"/admin/maps/{id}/areas";
        object area = await DescribeAreaAsync(
            packaged.Area, packaged.Packages, context.RequestServices.GetRequiredService<IOfflineSyncStore>(), packaged.Skipped,
            cancellation).ConfigureAwait(false);

        await Results.Json(area, statusCode: StatusCodes.Status201Created).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RefreshMapAreaAsync(
        HttpContext context, string id, Guid areaId, IWebMapStore maps, IMapAreaStore areas, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapForAreasAsync(context, current, id, "refresh an area of", maps, audit, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        if (await areas.FindAsync(areaId, cancellation).ConfigureAwait(false) is not { } area
            || !string.Equals(area.MapId, id, StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 404, NoArea(areaId)).ConfigureAwait(false);
            return;
        }

        (PackagedArea? packaged, PackageRefusal? refusal) = await MapAreaPackaging.RefreshAsync(context, area, cancellation).ConfigureAwait(false);

        if (packaged is null)
        {
            await AuditAsync(context, audit, "webmap.area.refresh", id,
                Detail(new { area = areaId, refused = refusal!.Message }), succeeded: false, cancellation).ConfigureAwait(false);
            await Refuse(context, refusal.Status, refusal.Message).ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "webmap.area.refresh", id,
            Detail(new { area = areaId, packages = packaged.Packages.Count, skipped = packaged.Skipped.Count }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(await DescribeAreaAsync(
            packaged.Area, packaged.Packages, context.RequestServices.GetRequiredService<IOfflineSyncStore>(), packaged.Skipped,
            cancellation).ConfigureAwait(false)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteMapAreaAsync(
        HttpContext context, string id, Guid areaId, IWebMapStore maps, IMapAreaStore areas, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (await ManagedMapForAreasAsync(context, current, id, "delete an area of", maps, audit, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        if (await areas.FindAsync(areaId, cancellation).ConfigureAwait(false) is not { } area
            || !string.Equals(area.MapId, id, StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 404, NoArea(areaId)).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<MapAreaPackage> packages = await areas.PackagesAsync([areaId], cancellation).ConfigureAwait(false);

        if (!await areas.DeleteAsync(areaId, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, NoArea(areaId)).ConfigureAwait(false);
            return;
        }

        // The rows are gone: now what they stood for. A replica that cannot be unregistered is reported by the next sync
        // as an unknown replica; a file that cannot be removed is the sweep's (D-407).
        await MapAreaPackaging.DiscardAsync(context.RequestServices, packages, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "webmap.area.delete", id,
            Detail(new { area = areaId, title = area.Title, packages = packages.Count }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { deleted = areaId, title = area.Title, packages = packages.Count }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The map, when this caller may change it; otherwise writes the refusal and answers null. 404 when they cannot open
    /// it, 403 when they can and may not change it.
    /// </summary>
    private static async Task<WebMap?> ManagedMapForAreasAsync(
        HttpContext context, RequestPrincipal current, string id, string verb, IWebMapStore maps, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await maps.FindAsync(id, cancellation).ConfigureAwait(false) is not { } map || !Readable(current, map))
        {
            await Refuse(context, 404, NoMap(id)).ConfigureAwait(false);
            return null;
        }

        if (!LayerAccess.MayManage(map.Owner, current.Principal, current.Authorization))
        {
            await AuditAsync(context, audit, "webmap.area.refused", id,
                Detail(new { refused = "not the owner", owner = map.OwnerName }), succeeded: false, cancellation).ConfigureAwait(false);
            await Refuse(context, 403,
                $"Only the map's owner, {map.OwnerName}, or an administrator can {verb} '{map.Title}'.").ConfigureAwait(false);
            return null;
        }

        return map;
    }

    private static string NoArea(Guid id) => $"No area '{id:N}' of this map.";

    /// <summary>
    /// Reads an ArcGIS envelope with its spatial reference: four finite numbers, west of east and south of north, and a
    /// positive <c>wkid</c> (or <c>latestWkid</c>).
    /// </summary>
    /// <param name="node">The envelope.</param>
    /// <param name="extent">The extent, when this returns true.</param>
    /// <param name="wkid">Its reference.</param>
    /// <param name="why">Why not, when it returns false.</param>
    /// <returns>Whether it is a usable envelope.</returns>
    internal static bool TryReadAreaExtent(JsonNode? node, out Envelope extent, out int wkid, out string? why)
    {
        extent = default;
        wkid = 0;
        why = "extent is required: an envelope {\"xmin\", \"ymin\", \"xmax\", \"ymax\", \"spatialReference\": {\"wkid\"}}.";

        if (node is not JsonObject box)
        {
            return false;
        }

        double[] numbers = new double[4];
        string[] names = ["xmin", "ymin", "xmax", "ymax"];

        for (int i = 0; i < 4; i++)
        {
            if (box[names[i]] is not JsonValue value || !value.TryGetValue(out double number) || !double.IsFinite(number))
            {
                why = $"extent.{names[i]} must be a finite number.";
                return false;
            }

            numbers[i] = number;
        }

        if (numbers[0] >= numbers[2] || numbers[1] >= numbers[3])
        {
            why = "extent must have xmin < xmax and ymin < ymax: an area has some width and some height.";
            return false;
        }

        if (box["spatialReference"] is not JsonObject reference
            || !(ReadWkid(reference["wkid"], out wkid) || ReadWkid(reference["latestWkid"], out wkid)))
        {
            why = "extent.spatialReference.wkid is required: the reference the numbers are in.";
            return false;
        }

        extent = new Envelope(numbers[0], numbers[1], numbers[2], numbers[3]);
        why = null;
        return true;

        static bool ReadWkid(JsonNode? value, out int wkid)
        {
            wkid = 0;
            return value is JsonValue v && v.TryGetValue(out int w) && w > 0 && (wkid = w) > 0;
        }
    }

    /// <summary>An area as the admin routes describe it: its extent, status and packages.</summary>
    private static async Task<object> DescribeAreaAsync(
        MapArea area, IEnumerable<MapAreaPackage> packages, IOfflineSyncStore sync, IReadOnlyList<SkippedService> skipped,
        CancellationToken cancellation)
    {
        List<object> described = [];

        foreach (MapAreaPackage package in packages)
        {
            ReplicaRecord? replica = package.ReplicaId is { } held ? await sync.FindAsync(held, cancellation).ConfigureAwait(false) : null;
            string service = MapAreaPackaging.ServiceNameOf(MapAreaPackaging.PackageUrlOf(package)) ?? package.ServiceId.ToString("D");

            described.Add(new
            {
                id = package.Id.ToString("N"),
                service,
                fileName = package.FileName,
                size = package.Size,
                sha256 = package.Sha256,
                format = package.Format,
                replicaId = package.ReplicaId is { } id ? OfflineReplicaRequests.Wire(id) : null,
                replicaRegistered = package.ReplicaId is not null ? (bool?)(replica is not null) : null,
                created = package.Created.ToUnixTimeMilliseconds(),
                refreshed = package.Refreshed.ToUnixTimeMilliseconds(),
            });
        }

        return new
        {
            id = area.Id.ToString("N"),
            mapId = area.MapId,
            title = area.Title,
            extent = new
            {
                xmin = area.XMin,
                ymin = area.YMin,
                xmax = area.XMax,
                ymax = area.YMax,
                spatialReference = new { wkid = area.Wkid },
            },
            status = area.Status,
            creator = area.CreatorName,
            created = area.Created.ToUnixTimeMilliseconds(),
            refreshed = area.Refreshed.ToUnixTimeMilliseconds(),
            packages = described,
            skipped = skipped.Select(s => new { service = s.Service, reason = s.Reason }),
        };
    }
}
