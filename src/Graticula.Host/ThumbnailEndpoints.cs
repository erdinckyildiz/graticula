using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// A small rendered picture of one layer, for the console's lists.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-58](../../docs/architecture-debt.md), and it is the owner's original ask.</b> Looking at
/// their reference's service list: *"bir de thumbnailler var. girmeden görebiliyoruz."* — there
/// are thumbnails too, you can see it without going in. Theirs are rendered map images. Ours were
/// a sample of up to 800 features that the browser painted onto a canvas, which for a layer of
/// 46,041 roads drew **1.7% of it** and read as *this layer is nearly empty* — a picture that
/// under-reports is taken as a fact about the data.
/// </para>
/// <para>
/// <b>What changed is that this server can draw.</b> `Graticula.Render.Skia` and
/// [ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) render every feature, from the layer's
/// own symbology, at the size the picture will actually be shown. The measurement is
/// [benchmarks/thumbnail](../../benchmarks/thumbnail/RESULTS.md): 17–23 ms and 139.5 kB for the
/// sample against 70–76 ms and 1.8 kB for the render, per request — and the render is drawn
/// once per layer while the 139.5 kB was paid by every viewer every time.
/// </para>
/// <para>
/// <b>Our own route rather than a segment on the ArcGIS one.</b> `MapServer/export` would draw
/// this, and it is the compatibility face: adding a path Esri does not define to it would make
/// this server's ArcGIS surface claim something no ArcGIS client expects, which is the argument
/// [ADR-049](../../docs/adr/ADR-049-a-face-refuses-in-its-own-vocabulary.md) §2 makes about
/// invented vocabulary and [CLAUDE.md](../../CLAUDE.md) §51 makes about adapters. This is a
/// console resource and lives on the console's own root.
/// </para>
/// <para>
/// <b>It refuses exactly what the faces refuse.</b> The service is resolved through
/// <see cref="ServiceLookup"/>, so a service the caller cannot see is not there; the capability
/// ceiling is honoured, so a service configured not to answer `Query` does not answer with a
/// picture of its features either. Neither is a new rule — a thumbnail is a read, and it goes
/// through the same two gates every other read goes through.
/// </para>
/// </remarks>
internal static class ThumbnailEndpoints
{
    /// <summary>
    /// How large the picture is rendered.
    /// </summary>
    /// <remarks>
    /// <b>336×224, which is neither slot's size, and that is the point.</b> The list shows it at
    /// 104×70 and a service's own page at 168×112; rendering at twice the larger keeps it sharp
    /// on a high-density screen and lets one cached picture serve both. A PNG of a simplified
    /// map at this size is a couple of kilobytes, so the cost of the extra pixels is nothing on
    /// the wire.
    /// </remarks>
    public const int Width = 336;

    /// <summary>How tall the picture is rendered.</summary>
    public const int Height = 224;

    /// <summary>Registers the route.</summary>
    /// <param name="app">The route builder.</param>
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Marked, though nothing under `/admin` is required to be — and `/admin/routes`
        // does not list it.</b> That listing walks the public faces, which is where a route
        // answering anonymously without anybody deciding it should was actually found; this
        // marker is for the reader of the code rather than for the audit page. It is here
        // because the property is true — the sharing decision behind this route is
        // `ServiceLookup`'s — and a true thing worth enumerating should say so where it is,
        // even on the day the enumerator does not reach it.
        app.MapGet("/admin/thumbnail", DrawAsync)
            .Governed(SharingGovernedExtensions.ByService);
    }

    /// <summary>
    /// Draws one layer, or says why it will not.
    /// </summary>
    /// <remarks>
    /// <b>The status is the answer, and it is the accurate one rather than a blanket 404.</b>
    /// The caller is the console, which fetches this and reads the code: 403 becomes *configured
    /// not to answer queries*, 503 becomes *stopped*, anything else becomes *no map to show*, and
    /// each of those is what the reader sees on hover over the hatched slot that replaces the
    /// picture. A refusal that said only *no* would put the console back to guessing, which is
    /// the mistake `preview.js` made and corrected by reading `status` instead of the prose.
    /// </remarks>
    private static async Task DrawAsync(
        HttpContext context,
        string? service,
        int? layer,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        ServiceThumbnails held,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string qualified = service.Trim().Trim('/');
        int slash = qualified.LastIndexOf('/');

        string? folder = slash < 0 ? null : qualified[..slash];
        string name = slash < 0 ? qualified : qualified[(slash + 1)..];

        int index = layer ?? 0;

        // <b>The cache is asked before the catalogue, and that is safe rather than a leak.</b>
        // A key is a qualified name a caller already had to know to ask; what it does not do is
        // reveal whether a service exists, because a miss and a refusal are the same 404 to the
        // caller either way. What it saves is the catalogue read as well as the render.
        //
        // <b>Except it is not asked before authorisation.</b> Serving a held picture to somebody
        // who may not see the service would be the sharing model defeated by a cache, so the
        // lookup below runs on every request and only the drawing is skipped.
        PublishedService? found = await ServiceLookup
            .ServiceAsync(context, catalog, name, cancellation, inFolder: folder)
            .ConfigureAwait(false);

        if (found is null)
        {
            // ServiceLookup has already written the refusal.
            return;
        }

        if (index < 0 || index >= found.Layers.Count)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        PublishedLayer drawn = found.Layers[index];

        // The same ceiling every other read honours — D-180, ADR-049. A service configured to
        // offer Create only must not hand out a picture of the features it will not query.
        if (CapabilityCeilings.Refuses(drawn, "Query"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (drawn.Definition.GeometryColumn is not { Length: > 0 })
        {
            // A table with no geometry has no picture. 404 rather than an empty image, so the
            // console shows its own placeholder instead of a white rectangle that looks broken.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string key = ServiceThumbnails.KeyFor(qualified, index, Width, Height);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ServiceThumbnails.Held? picture = held.Find(key, now);

        if (picture is null)
        {
            (_, LayerDescription described) =
                await contexts.GetAsync(drawn, cancellation).ConfigureAwait(false);

            if (described.Extent is not { } extent || extent.MinX > extent.MaxX)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            byte[] bytes = await RenderAsync(
                contexts, canvases, drawn, Framed(extent), settings, cancellation)
                .ConfigureAwait(false);

            picture = held.Keep(key, bytes, now);
        }

        await AnswerAsync(context, picture, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The extent to draw, with the layer's own extent given room and a degenerate one a size.
    /// </summary>
    /// <remarks>
    /// <b>A layer of one point has an extent of zero width</b>, and a transform built on that
    /// divides by nothing and draws nothing. Giving it a margin is not cosmetic: it is what
    /// makes a single-feature layer produce a picture at all. The margin on an ordinary extent
    /// is 4%, so the outermost feature is not painted on the frame.
    /// </remarks>
    /// <param name="extent">The layer's own extent.</param>
    /// <returns>What the picture covers.</returns>
    private static Envelope Framed(Envelope extent)
    {
        double width = extent.MaxX - extent.MinX;
        double height = extent.MaxY - extent.MinY;

        // Degenerate in either direction: a vertical line, a horizontal one, or one point.
        double padX = width > 0 ? width * 0.04 : Math.Max(height * 0.04, 1e-4);
        double padY = height > 0 ? height * 0.04 : Math.Max(width * 0.04, 1e-4);

        return new Envelope(
            extent.MinX - padX, extent.MinY - padY, extent.MaxX + padX, extent.MaxY + padY);
    }

    /// <summary>Draws the layer into a PNG.</summary>
    /// <param name="contexts">Where a layer's source comes from.</param>
    /// <param name="canvases">The canvas factory.</param>
    /// <param name="layer">What to draw.</param>
    /// <param name="extent">What the picture covers, in the layer's own reference.</param>
    /// <param name="settings">For the record ceiling.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The encoded picture.</returns>
    private static async Task<byte[]> RenderAsync(
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        PublishedLayer layer,
        Envelope extent,
        HostSettings settings,
        CancellationToken cancellation)
    {
        PixelTransform transform = new(extent, Width, Height);

        using IMapCanvas canvas = canvases.Create(Width, Height);

        // <b>Transparent, not white.</b> The slot has a rounded corner and a background that
        // follows the viewer's theme; a white rectangle would sit inside it as a white
        // rectangle, in dark mode most obviously.
        MapRenderer renderer = new(canvas, transform, geographic: IsGeographic(layer.Definition.Srid));

        renderer.Clear(Rgba.Transparent);

        // <b>The layer's own reference, not WGS 84.</b> The extent came out of the layer's
        // description in the layer's own coordinates, so drawing in the same reference asks the
        // source for no projection at all — the cheapest correct thing, and a thumbnail is not
        // a map anybody measures off.
        await WmsEndpoints
            .DrawLayerAsync(
                contexts, renderer, transform, layer, layer.Definition.Srid, null,
                settings.MaximumRecordCount, cancellation)
            .ConfigureAwait(false);

        // <b>No labels.</b> `FinishLabels` is what draws them and it is not called: text at 104
        // pixels across is a smear, and the picture is about shape and density.
        return canvas.Encode(MapImageFormat.Png, settings.JpegQuality);
    }

    /// <summary>Writes the picture, or a 304 when the browser already has it.</summary>
    /// <param name="context">The request.</param>
    /// <param name="picture">What to send.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <returns>The task.</returns>
    private static async Task AnswerAsync(
        HttpContext context, ServiceThumbnails.Held picture, CancellationToken cancellation)
    {
        context.Response.Headers.ETag = picture.ETag;

        // <b>`private`, because a thumbnail is only visible to callers who can see the service.</b>
        // A shared proxy holding one would serve it to somebody the sharing model refuses.
        context.Response.Headers.CacheControl = string.Create(
            CultureInfo.InvariantCulture,
            $"private, max-age={(int)ServiceThumbnails.Age.TotalSeconds}");

        if (context.Request.Headers.IfNoneMatch.Count > 0
            && context.Request.Headers.IfNoneMatch.ToString().Contains(
                picture.ETag, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentType = "image/png";
        context.Response.ContentLength = picture.Bytes.Length;

        await context.Response.Body.WriteAsync(picture.Bytes, cancellation).ConfigureAwait(false);
    }

    /// <summary>Whether a reference is in degrees.</summary>
    /// <param name="srid">The code.</param>
    /// <returns><see langword="true"/> for a geographic reference.</returns>
    private static bool IsGeographic(int srid) =>
        Graticula.Geometries.AxisOrder.IsGeographic(srid);
}
