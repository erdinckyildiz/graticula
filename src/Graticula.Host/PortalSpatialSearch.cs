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
/// one has nothing to match; for the owner to confirm. A web scene is one of these for now: its view is a camera, not a
/// box, and its document is not read here. A box that touches an extent's edge meets it.
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

    /// <summary>Whether an item's <c>[[xmin, ymin], [xmax, ymax]]</c> meets a box of minx, miny, maxx, maxy.</summary>
    internal static bool Meets(double[][] extent, double[] box) =>
        extent is [[double xmin, double ymin], [double xmax, double ymax]]
        && xmin <= box[2] && box[0] <= xmax && ymin <= box[3] && box[1] <= ymax;
}
