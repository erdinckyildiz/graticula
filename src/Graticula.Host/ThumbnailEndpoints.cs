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

        // <b>`layer` is the id in the URL, not a position in a list.</b> `PublishedLayer`
        // says so itself: *its number within that service — the `{id}` in the URL. Assigned
        // once and never reused. Gaps in the sequence are correct.* A group layer takes an id
        // and is not drawable, so on a service that has one the two stop agreeing — measured
        // 2026-09-03 on `ci_EarlyAlert`, whose ids are 0, 1, 2 (a group) and 3: asking for
        // layer 3, which every caller means, indexed past the end and answered 404, and asking
        // for the group's id 2 answered 200 with the *third drawable layer's* picture. A wrong
        // picture with a 200 is the worse half.
        PublishedLayer? match = null;

        foreach (PublishedLayer candidate in found.Layers)
        {
            if (candidate.LayerIndex == index)
            {
                match = candidate;
                break;
            }
        }

        if (match is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        PublishedLayer drawn = match;

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
            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(drawn, cancellation).ConfigureAwait(false);

            if (described.Extent is not { } extent || extent.MinX > extent.MaxX)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // <b>Framed on the features that will be drawn, not on the layer's declared extent
            // — [D-199](../../docs/architecture-debt.md).</b> That extent comes from
            // `ST_EstimatedExtent`, which reads the GiST index: it grows with every insert and
            // shrinks only under `VACUUM` or `REINDEX`, so it is an upper bound over everything
            // the layer has *ever* held. Measured on `ci_editable`, three features left after a
            // conformance suite: the declared box is 4,611 × 6,042 units and the data occupies
            // 600 × 0, so the picture was three dots in the corner of an empty frame — which is
            // exactly the *this layer is nearly empty* reading [D-58](../../docs/architecture-debt.md)
            // replaced the sampled canvas to end, reached by a different route.
            //
            // <b>Correct by construction rather than by approximation.</b> A thumbnail draws at
            // most `MaximumRecordCount` features; framing on the envelope of the features it
            // will draw is the right frame for the picture it will produce. When a layer fills
            // its own extent the two agree and nothing changes.
            byte[] bytes = await PictureAsync(
                contexts, canvases, source, drawn, extent, settings, null, cancellation)
                .ConfigureAwait(false);

            picture = held.Keep(key, bytes, now);
        }

        await AnswerAsync(context, picture, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// One layer's picture, framed on the features it draws.
    /// </summary>
    /// <remarks>
    /// <b>Shared with the symbology editor's preview, deliberately.</b> A preview drawn by a
    /// second path would be a picture of the second path: the value of showing somebody what
    /// their change looks like depends entirely on it being the same renderer, the same frame
    /// and the same record ceiling as the thing they are changing.
    /// </remarks>
    /// <param name="contexts">Where a layer's source comes from.</param>
    /// <param name="canvases">The canvas factory.</param>
    /// <param name="source">The layer's features, already resolved.</param>
    /// <param name="layer">What to draw.</param>
    /// <param name="declared">The layer's declared extent, used when it has no features.</param>
    /// <param name="settings">For the record ceiling.</param>
    /// <param name="symbology">A candidate document, or null for the stored one.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>The encoded picture.</returns>
    internal static async Task<byte[]> PictureAsync(
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        IFeatureSource source,
        PublishedLayer layer,
        Envelope declared,
        HostSettings settings,
        string? symbology,
        CancellationToken cancellationToken)
    {
        Envelope frame = Framed(
            await DrawnExtentAsync(source, settings, cancellationToken).ConfigureAwait(false)
            ?? declared);

        return await RenderAsync(
            contexts, canvases, layer, frame, settings, symbology, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The envelope of the features this thumbnail will draw, or null when there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One bounded read, and it is the same bound the render uses.</b> `MaximumRecordCount`
    /// caps both, so this reads exactly the set that will be drawn — not a sample of it. On a
    /// cold thumbnail it doubles the work, which the cache makes once per layer every five
    /// minutes; on a warm one it costs nothing because nothing runs.
    /// </para>
    /// <para>
    /// <b>Geometry only.</b> No attributes are asked for, because a frame is made of
    /// coordinates and reading the columns as well would double the bytes for nothing.
    /// </para>
    /// <para>
    /// <b>Null rather than an empty envelope when the layer has no features.</b> The caller
    /// falls back to the declared extent, which is what an empty layer had before and is the
    /// only thing left to frame on.
    /// </para>
    /// </remarks>
    /// <param name="source">The layer's features.</param>
    /// <param name="settings">For the record ceiling.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>The envelope, or null.</returns>
    private static async Task<Envelope?> DrawnExtentAsync(
        IFeatureSource source, HostSettings settings, CancellationToken cancellationToken)
    {
        FeatureQuery query = new(
            limit: settings.MaximumRecordCount,
            fields: [],
            includeGeometry: true);

        Envelope found = Envelope.Empty;
        bool any = false;

        await foreach (Feature feature in
            source.ReadAsync(query, cancellationToken).ConfigureAwait(false))
        {
            if (feature.Geometry is not { IsEmpty: false } geometry)
            {
                continue;
            }

            found = any ? found.Union(geometry.Envelope) : geometry.Envelope;
            any = true;
        }

        return any ? found : null;
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
    /// <param name="symbology">A candidate document, or null for the stored one.</param>
    /// <param name="cancellation">The caller's.</param>
    /// <param name="width">How wide to draw, or null for a thumbnail's width.</param>
    /// <param name="height">How tall to draw, or null for a thumbnail's height.</param>
    /// <returns>The encoded picture.</returns>
    internal static async Task<byte[]> RenderAsync(
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        PublishedLayer layer,
        Envelope extent,
        HostSettings settings,
        string? symbology,
        CancellationToken cancellation,
        int? width = null,
        int? height = null)
    {
        // <b>The thumbnail's size unless a caller names one.</b> The symbology preview asks for
        // its own when it is drawing into a map viewport rather than into a fixed slot; every
        // other caller wants the two constants above and passes nothing.
        int wide = width ?? Width;
        int tall = height ?? Height;

        PixelTransform transform = new(extent, wide, tall);

        using IMapCanvas canvas = canvases.Create(wide, tall);

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
                settings.MaximumRecordCount, cancellation, log: null, symbology: symbology)
            .ConfigureAwait(false);

        // <b>No labels.</b> `FinishLabels` is what draws them and it is not called: text at 104
        // pixels across is a smear, and the picture is about shape and density.
        //
        // <b>Still none at map size, and that is a question rather than a decision.</b> A
        // preview drawn into a viewport is a map, and a map has labels; whether this path should
        // start drawing them is part of whatever decides the editor's shape, not something to
        // slip in with a size parameter.
        //
        // <b>But the density surface, which is exactly what this picture is about.</b> A heat map
        // accumulates while the features go past and is composited at the end, and this path skips
        // the method that would have done it — so a heat map previewed here came back fully
        // transparent while every unit test passed. Measured 2026-09-04, on the first live draw.
        // `FinishHeat` is separate from `FinishLabels` for this caller.
        renderer.FinishHeat();

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
