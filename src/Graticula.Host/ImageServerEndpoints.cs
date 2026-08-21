using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// ArcGIS ImageServer: a registered coverage, drawn here.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first cut of
/// [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md), and §3.2 says
/// what it is not.</b> The service document, <c>exportImage</c> and <c>identify</c>.
/// No raster function chains and no dynamic mosaicking: one raster, one rendering
/// rule, one request. A mosaic is a second decision with a dataset model behind it,
/// and bundling it here would repeat the mistake ADR-009 §0 exists to warn about.
/// </para>
/// <para>
/// <b>Requests are answered in the coverage's own reference and no other, in this
/// cut.</b> Warping needs per-pixel inverse projection and <c>IProjector</c> batches
/// geometries; the usual answer is a control-point grid, which is an approximation
/// with an error that ADR-043 condition 2 requires measuring rather than assuming. So
/// a request in another system is refused with a sentence naming the one that works,
/// and the service document advertises only that system — which is where an ArcGIS
/// client reads it from, so a well-behaved client never sends the wrong one.
/// </para>
/// <para>
/// <b>The path is never returned.</b> It is a filesystem location or a URL with a
/// credential in front of it, and either way it says more about this deployment than a
/// client is owed. ADR-043 §3.3's proxy exists so the bytes travel through here.
/// </para>
/// </remarks>
internal static class ImageServerEndpoints
{
    /// <summary>What every ImageServer document claims it can do.</summary>
    /// <remarks>
    /// <b>Only what is answered, which is correctness gate 2's fifth finding.</b> That
    /// gate found <c>Map,Query,Data</c> on a face with no query route, and it was
    /// repaired by making the claim true rather than the route. ADR-043's condition 5
    /// asks for the same discipline here before the near-free operations exist: this
    /// face images and identifies, and it does not catalogue, download or compute
    /// histograms.
    /// </remarks>
    private const string Capabilities = "Image";

    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            app.MapGet($"{prefix}/{{serviceName}}/ImageServer", ServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/ImageServer/exportImage", ExportAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/ImageServer/identify", IdentifyAsync)
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    private static async Task ServiceAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        PublishedCoverage? coverage =
            await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false);

        if (coverage is null)
        {
            return;
        }

        CoverageInfo info = coverage.Info;

        object document = new
        {
            currentVersion = 10.81,
            serviceDescription = string.Empty,
            name = coverage.QualifiedName,
            description = string.Empty,
            extent = Box(info.Extent, info.Srid),
            initialExtent = Box(info.Extent, info.Srid),
            fullExtent = Box(info.Extent, info.Srid),
            pixelSizeX = info.PixelWidth,
            pixelSizeY = info.PixelHeight,
            bandCount = info.Bands.Count,
            pixelType = PixelType(info.Bands[0].Kind),
            minPixelSize = 0,
            maxPixelSize = 0,
            copyrightText = string.Empty,
            serviceDataType = "esriImageServiceDataTypeGeneric",

            // <b>Named, because a client reads this before it asks.</b> `jpgpng` is
            // first for the same reason it is the SDK's default, and it is answered as
            // PNG — which is what the format means when the picture has transparency.
            supportedImageFormatTypes = "JPGPNG,PNG,PNG8,PNG24,PNG32,JPG,JPEG",

            // <b>Absent rather than zero when the file declares none.</b> Zero is a
            // legitimate measurement, so reporting it as the no-data value would tell a
            // client to discard real pixels.
            noDataValue = info.Bands[0].NoData,

            spatialReference = new { wkid = info.Srid, latestWkid = info.Srid },
            capabilities = Capabilities,
            defaultResamplingMethod = "Bilinear",
            maxImageHeight = 4096,
            maxImageWidth = 4096,
            allowRasterFunction = false,
            supportsStatistics = false,
            supportsAdvancedQueries = false,
            editFieldsInfo = (object?)null,
            hasColormap = false,
            hasMultidimensions = false,
        };

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            string path = context.Request.Path;

            await Results.Content(
                RestDirectory.Document(
                    path,
                    $"{coverage.QualifiedName} (ImageServer)",
                    document,
                    // <b>A viewer first, then the raw export.</b> The MapServer face
                    // shipped with only an export link and it was criticised for it the
                    // next day: a single PNG of the full extent is not a map, because a
                    // PNG does not zoom. `face=imageserver` points the ArcGIS SDK
                    // viewer at this service, which is also how ADR-043 condition 1 is
                    // paid — Esri's own client asking for the pixels.
                    links:
                    [
                        ("ArcGIS SDK", "/studio/map.html?face=imageserver"
                            + $"&service={Uri.EscapeDataString(coverage.QualifiedName)}"),
                        ("Export", $"{path}/exportImage?bbox={Extent(info.Extent)}"
                            + $"&bboxSR={info.Srid.ToString(CultureInfo.InvariantCulture)}"
                            + "&size=800,600&format=png&f=image"),
                    ],
                    linksLabel: "View in"),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ExportAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IMapCanvasFactory canvases,
        IProjector projector,
        ConnectionBudget budget,
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedCoverage? coverage =
            await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false);

        if (coverage is null)
        {
            return;
        }

        if (!ImageServerExportParameters.TryParse(
                Parameter(context),
                coverage.Info,
                new WidthHeight(settings.MaximumImageWidth, settings.MaximumImageHeight),
                out ImageServerExportParameters? asked,
                out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        /*
          <b>The same admission control a feature request meets, and
          [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) condition
          3 asks for it because a raster is not a vector at the same pixel count.</b> A
          `GetMap` over an empty extent draws nothing and costs nothing; an
          `exportImage` over the same extent still decompresses every tile the window
          touches. Two clients panning a large coverage can put more work through this
          face than a hundred through the feature one.

          <b>Keyed on the coverage rather than on a database.</b> `ConnectionBudget` is
          named for what it originally bounded and what it actually is is an admission
          gate with a per-source and a per-worker limit; a coverage is a source. The
          refusal, the five-second wait and the `Retry-After` are then the ones a client
          already knows, which is worth more than a bound of its own that behaves
          slightly differently.

          <b>Taken before the canvas is allocated.</b> A 4096² canvas is 64 MB of
          pixels, so admitting the request and then refusing it would have paid the
          largest single cost of serving it.
        */
        using ConnectionBudget.Lease lease =
            await budget.EnterAsync($"coverage:{coverage.Path}", cancellation)
                .ConfigureAwait(false);

        using IMapCanvas canvas = canvases.Create(asked!.Width, asked.Height);

        canvas.Clear(asked.Format == MapImageFormat.Png ? Rgba.Transparent : Rgba.White);

        if (asked.Srid == coverage.Info.Srid)
        {
            await DrawAlignedAsync(canvas, coverage, asked, readers, cancellation)
                .ConfigureAwait(false);
        }
        else
        {
            await DrawWarpedAsync(canvas, coverage, asked, readers, projector, cancellation)
                .ConfigureAwait(false);
        }

        byte[] image = canvas.Encode(asked.Format, 90);

        context.Response.ContentType =
            asked.Format == MapImageFormat.Png ? "image/png" : "image/jpeg";

        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Draws a coverage into a request written in its own reference.
    /// </summary>
    /// <remarks>
    /// <b>No projection at all, and that is worth keeping separate from the warped
    /// path.</b> A request in the coverage's own system needs one window read and one
    /// blit; routing it through the warp would cost a round trip to the projection
    /// engine and a per-pixel resample to arrive at the same picture.
    /// </remarks>
    private static async Task DrawAlignedAsync(
        IMapCanvas canvas,
        PublishedCoverage coverage,
        ImageServerExportParameters asked,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        CoveragePlan? plan =
            CoveragePlanner.Plan(coverage.Info, asked.Extent, asked.Width, asked.Height);

        // <b>No overlap is a valid empty image, not a refusal.</b> ADR-041 condition 5
        // asks the vector faces for this and the reason is the same here: a client
        // panning off the edge of its own data has not made a mistake.
        if (plan is not { } read)
        {
            return;
        }

        using ICoverageReader reader =
            await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

        CoverageWindow window = await reader.ReadAsync(
            read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
            .ConfigureAwait(false);

        Rgba[] pixels = CoverageStyle.Parse(coverage.Style).Paint(window, coverage.Info.Bands);

        canvas.DrawImage(pixels, window.Width, window.Height, read.Destination);
    }

    /// <summary>
    /// Draws a coverage into a request written in some other reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three steps, and the middle one is the only approximation.</b> The canvas's
    /// control grid is projected into the coverage's reference — one round trip, a few
    /// hundred points, <see cref="IProjector"/>'s work and not ours. The grid's own
    /// bounding box says which window to read. Then every canvas pixel finds its ground
    /// position by interpolating between control points, which is where the error lives
    /// and what the raster-warp benchmark measures.
    /// </para>
    /// <para>
    /// <b>The window is planned at the canvas's own pixel count.</b> That is what stops
    /// a reprojected image being read at full resolution when it will be drawn small —
    /// the saving the aligned path gets for free.
    /// </para>
    /// </remarks>
    private static async Task DrawWarpedAsync(
        IMapCanvas canvas,
        PublishedCoverage coverage,
        ImageServerExportParameters asked,
        ICoverageReaderFactory readers,
        IProjector projector,
        CancellationToken cancellation)
    {
        int steps = CoverageWarp.StepsFor(asked.Width, asked.Height);

        Point[] grid = CoverageWarp.ControlPoints(asked.Extent, asked.Width, asked.Height, steps);

        (IReadOnlyList<Geometry> projected, _) = await projector
            .ProjectAsync(grid, asked.Srid, coverage.Info.Srid, cancellation)
            .ConfigureAwait(false);

        double[] groundX = new double[projected.Count];
        double[] groundY = new double[projected.Count];

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        for (int i = 0; i < projected.Count; i++)
        {
            if (projected[i] is not Point point)
            {
                // A projector that returned something other than the points it was
                // given has broken its own contract; drawing a partial picture from the
                // rest would be a map with a hole nobody can see.
                return;
            }

            groundX[i] = point.X;
            groundY[i] = point.Y;

            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        CoveragePlan? plan = CoveragePlanner.Plan(
            coverage.Info, new Envelope(minX, minY, maxX, maxY), asked.Width, asked.Height);

        if (plan is not { } read)
        {
            return;
        }

        using ICoverageReader reader =
            await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

        CoverageWindow window = await reader.ReadAsync(
            read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
            .ConfigureAwait(false);

        Rgba[] painted = CoverageStyle.Parse(coverage.Style).Paint(window, coverage.Info.Bands);

        (int levelWidth, int levelHeight) = read.Overview == 0
            ? (coverage.Info.Width, coverage.Info.Height)
            : (coverage.Info.Overviews[read.Overview - 1].Width,
               coverage.Info.Overviews[read.Overview - 1].Height);

        double perPixelX = (coverage.Info.Extent.MaxX - coverage.Info.Extent.MinX) / levelWidth;
        double perPixelY = (coverage.Info.Extent.MaxY - coverage.Info.Extent.MinY) / levelHeight;

        CoverageWarp warp = new(asked.Width, asked.Height, steps, groundX, groundY);

        Rgba[] pixels = warp.Resample(
            painted,
            window.Width,
            window.Height,
            coverage.Info.Extent.MinX + (read.X * perPixelX),
            coverage.Info.Extent.MaxY - (read.Y * perPixelY),
            perPixelX,
            perPixelY);

        canvas.DrawImage(
            pixels, asked.Width, asked.Height, new PixelBox(0, 0, asked.Width, asked.Height));
    }

    private static async Task IdentifyAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        PublishedCoverage? coverage =
            await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false);

        if (coverage is null)
        {
            return;
        }

        Func<string, string?> parameter = Parameter(context);

        if (!ImageServerExportParameters.TryPoint(
                parameter, coverage.Info, out double x, out double y, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        CoverageInfo info = coverage.Info;

        if (x < info.Extent.MinX || x > info.Extent.MaxX
            || y < info.Extent.MinY || y > info.Extent.MaxY)
        {
            await Results.Ok(new { objectId = 0, name = "Pixel", value = (string?)null })
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        /*
          <b>Nudged before truncating, and the first end-to-end test caught why.</b>
          The pixel size is derived by dividing the extent, and the extent was itself
          built by multiplying that size — so `0.01` comes back as
          `0.010000000000000000208`, and a point exactly on the boundary between pixel
          49 and pixel 50 divides to `49.999999999999999` and truncates to 49. Asking
          this service for the value at 30.5, 40.5 returned the pixel up and to the
          left of the one GDAL reads there, by three units in every band.

          <b>A pixel is half-open — it owns its own left and top edge — so a point on a
          boundary belongs to the higher index.</b> The epsilon is relative to the index
          rather than absolute, because a coverage a hundred thousand pixels wide has
          proportionally larger error in the same division.
        */
        double columnAt = (x - info.Extent.MinX) / info.PixelWidth;
        double rowAt = (info.Extent.MaxY - y) / info.PixelHeight;

        int column = Math.Clamp(
            (int)Math.Floor(columnAt + (Math.Abs(columnAt) * 1e-12) + 1e-9),
            0,
            info.Width - 1);

        int row = Math.Clamp(
            (int)Math.Floor(rowAt + (Math.Abs(rowAt) * 1e-12) + 1e-9),
            0,
            info.Height - 1);

        using ICoverageReader reader =
            await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

        CoverageWindow window =
            await reader.ReadAsync(0, column, row, 1, 1, cancellation).ConfigureAwait(false);

        // <b>Space-separated, which is Esri's spelling for a multi-band pixel.</b> A
        // JSON array would be the better shape and would be a shape their clients do
        // not read; ADR-005's rule is that a compatibility surface speaks the other
        // product's dialect and the honesty lives in the documentation.
        string[] values = new string[window.Bands];

        for (int band = 0; band < window.Bands; band++)
        {
            values[band] = window.At(0, 0, band).ToString(CultureInfo.InvariantCulture);
        }

        await Results.Ok(new
        {
            objectId = 0,
            name = "Pixel",
            value = string.Join(' ', values),
            location = new { x, y, spatialReference = new { wkid = info.Srid } },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The coverage this request names, if the caller may see it.
    /// </summary>
    /// <remarks>
    /// <b>The same rule as every other face, applied to the service row rather than to
    /// a second one.</b> A coverage's sharing, status and owner live on <c>service</c>,
    /// so this is <see cref="LayerAccess.Evaluate"/> over the same three values that
    /// govern a feature service. **Private is answered as 404 and so is missing** —
    /// identical status and identical message, which is what the security gate checked
    /// pairwise across five faces on 2026-08-20 and found held.
    /// </remarks>
    private static async Task<PublishedCoverage?> FindAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(coverages);

        (string? folder, string name) = Split(context, serviceName);

        PublishedCoverage? coverage =
            await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false);

        RequestPrincipal principal = context.Features.Get<RequestPrincipal>()
            ?? new RequestPrincipal(Principal.Anonymous, null, Authorization.Nothing);

        bool visible = coverage is not null
            && LayerAccess.Evaluate(
                coverage.Sharing, coverage.Owner, principal.Principal, principal.Authorization)
                .IsAllowed();

        if (!visible)
        {
            await RefuseAsync(
                context,
                404,
                $"No image service '{serviceName}' is visible to you. It may not exist, or it "
                + "may not be shared with you — this response is deliberate: telling the two "
                + "apart would say whether something exists that you may not see.")
                .ConfigureAwait(false);

            return null;
        }

        /*
          <b>Stopped is its own answer, and it comes after the sharing check for a
          reason.</b> A caller who has already been allowed to see the service is owed
          the actual reason it is not answering; one who has not is owed nothing, which
          is why this cannot come first. That order is what keeps *stopped* from being
          an oracle for *exists*.

          <b>This face conflated the two until 2026-08-21</b>, so an operator who
          stopped a coverage and then asked for it was told it might not exist. The
          other faces have said so separately since D-123, and there was no reason for
          this one to differ except that it was written in an afternoon.
        */
        if (coverage!.Status != ServiceStatus.Started)
        {
            await RefuseAsync(
                context,
                503,
                $"The image service '{serviceName}' is stopped. It exists and you may see it; "
                + "an administrator switched it off. Start it with "
                + "`POST /admin/coverages/{name}/start`.")
                .ConfigureAwait(false);

            return null;
        }

        return coverage;
    }

    private static (string? Folder, string Name) Split(HttpContext context, string serviceName) =>
        context.Request.RouteValues.TryGetValue("folder", out object? folder)
            && folder is string text
            && !string.IsNullOrWhiteSpace(text)
                ? (text, serviceName)
                : (null, serviceName);

    private static Func<string, string?> Parameter(HttpContext context) =>
        name =>
        {
            foreach (var pair in context.Request.Query)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value.ToString();
                }
            }

            return null;
        };

    /// <summary>An ArcGIS error document, which is a 200 carrying a refusal.</summary>
    /// <remarks>
    /// Inherited from the other ArcGIS faces rather than chosen here, for the reason
    /// <c>MapServerEndpoints</c> gives: every Esri client reads <c>error.code</c> out
    /// of a successful response.
    /// </remarks>
    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Ok(new
        {
            error = new
            {
                code,
                message,
                details = Array.Empty<string>(),
            },
        }).ExecuteAsync(context);

    private static object Box(Envelope extent, int srid) => new
    {
        xmin = extent.MinX,
        ymin = extent.MinY,
        xmax = extent.MaxX,
        ymax = extent.MaxY,
        spatialReference = new { wkid = srid, latestWkid = srid },
    };

    private static string Extent(Envelope extent) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{extent.MinX},{extent.MinY},{extent.MaxX},{extent.MaxY}");

    /// <summary>Esri's name for a sample kind.</summary>
    private static string PixelType(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => "U8",
        SampleKind.Signed16 => "S16",
        SampleKind.Unsigned16 => "U16",
        SampleKind.Signed32 => "S32",
        SampleKind.Real32 => "F32",
        SampleKind.Real64 => "F64",
        _ => "UNKNOWN",
    };
}
