using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.Wms;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// WMS at a service's own address, as ArcGIS gives it — <c>…/MapServer/WMSServer</c>, <c>…/ImageServer/WMSServer</c>
/// — and image services as WMS layers — ADR-162.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same surface, narrowed.</b> A request at a service's address is answered by the handler <c>/wms</c> uses,
/// with the layers limited to that service and the capabilities document naming that address, so a QGIS project or a
/// portal item that pointed at ArcGIS's address points at this one. <c>/wms</c> stays, listing everything.
/// </para>
/// <para>
/// <b>An image service is a layer named by its service</b> — <c>hosted/elevation</c> — drawn as its <c>exportImage</c>
/// draws it, with its stored style, raster function and classes, warped into the map's reference when that differs.
/// </para>
/// </remarks>
internal static partial class WmsEndpoints
{
    /// <summary>Where a request at a service's own address keeps which service it is narrowed to.</summary>
    private const string ScopeKey = "wms.scope";

    /// <summary>A service a request is narrowed to: its folder, its name, and which kind of service it is.</summary>
    internal sealed record ServiceScope(string? Folder, string Name, string Kind);

    /// <summary>Maps the per-service addresses beside <c>/wms</c>.</summary>
    private static void MapServiceAddresses(WebApplication app)
    {
        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            foreach (string kind in (string[])["MapServer", "ImageServer"])
            {
                string serviceKind = kind;
                app.MapGet($"{prefix}/{{serviceName}}/{kind}/WMSServer", (
                        HttpContext context, string serviceName, CatalogFallback catalog, ServiceContexts contexts,
                        IMapCanvasFactory canvases, IProjector projector, HostSettings settings, ICoverageCatalog coverages,
                        ICoverageReaderFactory readers, CancellationToken cancellation) =>
                    {
                        context.Items[ScopeKey] = new ServiceScope(Folder(context), serviceName, serviceKind);
                        return GetAsync(context, catalog, contexts, canvases, projector, settings, coverages, readers, cancellation);
                    })
                    .Governed(SharingGovernedExtensions.ByFiltering);
            }
        }
    }

    private static string? Folder(HttpContext context) =>
        context.Request.RouteValues.TryGetValue("folder", out object? folder) && folder is string text && text.Length > 0
            ? text
            : null;

    /// <summary>The service a request is narrowed to, or null at <c>/wms</c>.</summary>
    private static ServiceScope? ScopeOf(HttpContext context) =>
        context.Items.TryGetValue(ScopeKey, out object? scope) ? scope as ServiceScope : null;

    private static bool InScope(ServiceScope? scope, string? folder, string name, string kind) =>
        scope is null
        || (string.Equals(scope.Name, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(scope.Folder ?? string.Empty, folder ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && (scope.Kind == kind || (scope.Kind == "MapServer" && kind == "FeatureServer")));

    /// <summary>
    /// The image services this caller may see, started unless they may manage the server — as the vector layers are
    /// chosen — and within the request's service when it has one.
    /// </summary>
    private static async Task<List<PublishedCoverage>> VisibleCoveragesAsync(
        HttpContext context, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);
        ServiceScope? scope = ScopeOf(context);

        if (scope is { Kind: not "ImageServer" })
        {
            return [];
        }

        return [.. (await coverages.ListAsync(cancellation).ConfigureAwait(false))
            .Where(c => seesStopped || c.Status == ServiceStatus.Started)
            .Where(c => InScope(scope, c.Folder, c.Name, "ImageServer"))
            .Where(c => LayerAccess.Evaluate(c.Sharing, c.Owner, current.Principal, current.Authorization, c.SharedWith).IsAllowed())
            .OrderBy(c => c.QualifiedName, StringComparer.OrdinalIgnoreCase)];
    }

    private static PublishedCoverage? FindCoverage(IReadOnlyList<PublishedCoverage> coverages, string name) =>
        coverages.FirstOrDefault(c => string.Equals(c.QualifiedName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>An image service as a WMS layer: named by its service, in its own reference and extent.</summary>
    private static WmsLayer DescribeCoverage(PublishedCoverage coverage) =>
        new(
            coverage.QualifiedName,
            coverage.QualifiedName,
            Abstract: coverage.Description,
            coverage.Info.Srid,
            GeometryKind.Polygon,
            Drawable(coverage.Info.Extent),
            Geographic: null,
            Queryable: false,
            Time: null);

    /// <summary>
    /// A legend for an image service: its colours from darkest to lightest across the swatch, since its picture is
    /// its own values in its own colours rather than symbols.
    /// </summary>
    private static async Task CoverageLegendAsync(
        HttpContext context, IMapCanvasFactory canvases, WmsRequest request, CancellationToken cancellation)
    {
        int width = Math.Max(request.Width, 2), height = Math.Max(request.Height, 2);
        Rgba[] pixels = new Rgba[width * height];

        for (int x = 0; x < width; x++)
        {
            byte v = (byte)(255 * x / (width - 1));

            for (int y = 0; y < height; y++)
            {
                pixels[(y * width) + x] = new Rgba(v, v, v, 255);
            }
        }

        using IMapCanvas canvas = canvases.Create(width, height);
        canvas.Clear(request.Transparent ? Rgba.Transparent : Rgba.White);
        canvas.DrawImage(pixels, width, height, new PixelBox(0, 0, width, height));
        byte[] image = canvas.Encode(request.Format, 90);
        context.Response.ContentType = WmsNames.MediaTypeOf(request.Format);
        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }
}
