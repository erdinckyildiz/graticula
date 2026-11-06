using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// A portal search's <c>bbox</c>: the items whose extent meets the box, of those <c>q</c> and <c>categories</c> already
/// matched — ADR-271.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each item's extent is the one its own document says</b>, read by the same code: a service's from its layers
/// (<see cref="PortalEndpoints.ExtentAsync"/>, cached per layer as every request reads it), a web map's from its initial
/// view, an image service's from its raster, a scene layer package's from its layer document. Nothing new is computed,
/// so an item is found by a box exactly when its page would show it there.
/// </para>
/// <para>
/// <b>An item with no extent is not found by a box</b> — a PDF, a web app, a layer whose source cannot be read.
/// <c>INFERRED</c> from ArcGIS's search, where a spatial search matches against the item's extent and an item without
/// one has nothing to match; for the owner to confirm. A web scene is found by its initial view's target extent, or by
/// where its camera is (ADR-271, amended 2026-11-07). A box that touches an extent's edge meets it.
/// </para>
/// </remarks>
internal static class PortalSpatialSearch
{
    /// <summary>The listed items whose extent meets the box, in the order they were listed.</summary>
    public static async Task<List<PortalEndpoints.PortalListed>> WithinAsync(
        HttpContext context, IReadOnlyList<PortalEndpoints.PortalListed> listed, double[] box, CancellationToken cancellation)
    {
        ServiceContexts contexts = context.RequestServices.GetRequiredService<ServiceContexts>();
        Graticula.Geometries.IProjector projector = context.RequestServices.GetRequiredService<Graticula.Geometries.IProjector>();

        // A service lists one item per face, and every face has the service's extent: read it once.
        Dictionary<Guid, double[][]> services = [];
        List<PortalEndpoints.PortalListed> found = [];

        foreach (PortalEndpoints.PortalListed entry in listed)
        {
            double[][] extent;

            if (entry.Service is { } service)
            {
                if (!services.TryGetValue(service.Id, out extent!))
                {
                    extent = await PortalEndpoints.ExtentAsync(service, contexts, projector, cancellation).ConfigureAwait(false);
                    services[service.Id] = extent;
                }
            }
            else if (entry.Map is { } map)
            {
                // A listing does not read a map's document (WebMap.Document); its own read does.
                string? read = map.Document ?? (await context.RequestServices.GetRequiredService<IWebMapStore>()
                    .FindAsync(map.Id, cancellation).ConfigureAwait(false))?.Document;
                extent = PortalEndpoints.MapExtent(read);
            }
            else if (entry.Coverage is { } coverage)
            {
                extent = await PortalEndpoints.CoverageExtentAsync(coverage, projector, cancellation).ConfigureAwait(false);
            }
            else if (entry.Document is { } document && Documents.IsScenePackage(document.Kind))
            {
                extent = await SceneServerEndpoints.PackageExtentAsync(context, document, projector, cancellation).ConfigureAwait(false);
            }
            else if (entry.Document is { } scene && scene.Kind.Name == "webscene")
            {
                // A web scene's initial view: its target extent when it keeps one, or where its camera is.
                byte[]? json = await context.RequestServices.GetRequiredService<IDocumentStore>()
                    .ContentAsync(scene.Id, cancellation).ConfigureAwait(false);
                extent = SceneExtent(json is null ? null : System.Text.Encoding.UTF8.GetString(json));
            }
            else if (entry.Document is { } archive && Documents.IsTilesArchive(archive.Kind))
            {
                // ADR-278: a 3D Tiles archive's root region.
                extent = await SceneServerEndpoints.TilesetExtentAsync(context, archive, cancellation).ConfigureAwait(false);
            }
            else
            {
                extent = [];
            }

            if (Meets(extent, box))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    /// <summary>
    /// A web scene's extent in degrees: its initial viewpoint's target extent, read as a web map's is
    /// (<see cref="PortalEndpoints.MapExtent"/>), or else its camera's position as a point. Empty when it says neither.
    /// </summary>
    internal static double[][] SceneExtent(string? document)
    {
        if (PortalEndpoints.MapExtent(document) is [_, _] box)
        {
            return box;
        }

        try
        {
            using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(document ?? "{}");
            if (parsed.RootElement.TryGetProperty("initialState", out var state)
                && state.TryGetProperty("viewpoint", out var viewpoint)
                && viewpoint.TryGetProperty("camera", out var camera)
                && camera.TryGetProperty("position", out var position)
                && position.TryGetProperty("x", out var x) && position.TryGetProperty("y", out var y)
                && x.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                int wkid = position.TryGetProperty("spatialReference", out var reference)
                    && (reference.TryGetProperty("latestWkid", out var w) || reference.TryGetProperty("wkid", out w)) && w.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? w.GetInt32() : 4326;
                double lon = x.GetDouble(), lat = y.GetDouble();
                if (wkid is 3857 or 102100 or 102113 or 900913)
                {
                    const double Radius = 6378137.0;
                    lon = lon / Radius * 180.0 / Math.PI;
                    lat = ((2 * Math.Atan(Math.Exp(lat / Radius))) - (Math.PI / 2)) * 180.0 / Math.PI;
                }
                else if (wkid is not (4326 or 4490))
                {
                    return [];
                }

                return double.IsFinite(lon) && double.IsFinite(lat) ? [[lon, lat], [lon, lat]] : [];
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return [];
    }

    /// <summary>Whether an item's <c>[[xmin, ymin], [xmax, ymax]]</c> meets a box of minx, miny, maxx, maxy.</summary>
    internal static bool Meets(double[][] extent, double[] box) =>
        extent is [[double xmin, double ymin], [double xmax, double ymax]]
        && xmin <= box[2] && box[0] <= xmax && ymin <= box[3] && box[1] <= ymax;
}
