using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
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
    /// <summary>The methods every read operation on this face answers.</summary>
    /// <remarks>
    /// <b>`GET` and `POST`, and nothing else.</b> The REST specification documents both for
    /// every operation; `PUT` and `DELETE` are not read operations and this face has none.
    /// </remarks>
    private static readonly string[] Read = ["GET", "POST"];

    private const string Capabilities = "Image,Tilemap";

    /// <summary>The most tiles one <c>tilemap</c> call answers about.</summary>
    /// <remarks>
    /// 4096, which is a 64 by 64 block and far more than any client asks for in one call —
    /// a screenful at 256 pixels a tile is nearer 40. It exists so that a request naming
    /// its own array size cannot name an unbounded one.
    /// </remarks>
    private const int MaximumTilemapBlock = 4096;

    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            /*
              <b>`MapMethods` rather than `MapGet`, because the REST specification documents
              both and this face answered a bare 405 to one of them.</b>
              [D-139](../../docs/architecture-debt.md): a client whose request does not fit in
              a URL — a long `where`, a drawing geometry, a rendering rule — had no way to send
              it at all, and the refusal had no body to explain itself.

              <b>Accepting a posted parameter does not make a cookie work for POST.</b>
              `Authentication.CookieToken` refuses anything but GET and HEAD, deliberately and
              at length: a forged cross-site request can only ever read. That property is
              untouched here — see `ArcGisParameters` for why a token still has to travel in
              the header or the query rather than the body.
            */
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer", Read, ServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/exportImage", Read, ExportAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/identify", Read, IdentifyAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods(
                    $"{prefix}/{{serviceName}}/ImageServer/tile/{{level:int}}/{{row:int}}"
                        + "/{column:int}",
                    Read,
                    TileAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods(
                    $"{prefix}/{{serviceName}}/ImageServer/tilemap/{{level:int}}/{{row:int}}"
                        + "/{column:int}/{across:int}/{down:int}",
                    Read,
                    TilemapAsync)
                .Governed(SharingGovernedExtensions.ByService);

            /*
              <b>Every other path under this face answers in its own language rather than
              falling through to an empty 404, and *every other path* has to mean any
              number of segments.</b> The first version of this was one segment wide, so
              it caught `keyProperties` and missed `tile/0/0` — a `tile` request one
              segment short matched no template at all and got the bare, bodiless 404 this
              route exists to abolish. Found by a review that asked for the malformed
              spellings of a route rather than the malformed spellings of a parameter.

              <b>`{**rest}` rather than a second single-segment route</b>, because the
              shapes that go wrong are not only *one too few*: `tile/abc/0/0`,
              `tile/0/0/0/extra` and `tile` alone are all the same mistake from a client's
              side, and enumerating them is how the next one gets missed.

              <b>Registered last, and the constrained routes above still win.</b> Routing
              prefers a literal to a constrained parameter and a constrained parameter to a
              catch-all, so `tile/0/0/0` reaches TileAsync and `tile/0/0` reaches this.
            */
            app.MapMethods(
                    $"{prefix}/{{serviceName}}/ImageServer/{{operation}}", Read, UnknownAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods(
                    $"{prefix}/{{serviceName}}/ImageServer/{{operation}}/{{**rest}}",
                    Read,
                    UnknownAsync)
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    /// <summary>Answers an operation this face does not serve.</summary>
    /// <param name="context">The request.</param>
    /// <param name="serviceName">The service.</param>
    /// <param name="operation">What was asked for.</param>
    /// <param name="coverages">The catalogue.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>An unserved operation returned an empty-bodied 404, and ArcGIS Pro asked for
    /// one of them forty times in a single workflow.</b> A proxy trace of Pro against
    /// this face — the server's own request log records almost nothing, so the trace was
    /// taken from outside — shows forty `multidimensionalInfo` requests and fifteen
    /// `keyProperties`, each answered with nothing at all. A client cannot tell *no such
    /// operation* from *the server broke* when the body is empty, so it retries.
    /// </para>
    /// <para>
    /// <b>The shape is Esri's own, checked against their server rather than assumed.</b>
    /// `elevation3d.arcgis.com` answers `multidimensionalInfo` on a service that has no
    /// multidimensional data with HTTP 200 and
    /// <c>{"error":{"code":400,"message":"Unable to complete operation.","details":[]}}</c>.
    /// The status line is 200 and the refusal is in the body: that is the REST
    /// convention this whole face is written to, and it is
    /// [ADR-009](../../docs/adr/ADR-009-arcgis-rest-compatibility.md)'s rule, not a
    /// concession made here.
    /// </para>
    /// <para>
    /// <b>The message names the operation and what this face does serve</b>, because a
    /// refusal that does not say what would have worked sends the reader to the
    /// documentation for something this server may not implement at all.
    /// </para>
    /// </remarks>
    private static async Task UnknownAsync(
        HttpContext context,
        string serviceName,
        string operation,
        ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        // <b>The remainder of the path is deliberately not read.</b> Both routes reach here
        // and only the operation decides what to say; echoing the rest back would put
        // client-supplied text of unbounded length into a message, and the message is more
        // useful naming the shape that was wanted than the one that arrived.

        ArgumentNullException.ThrowIfNull(context);

        // Resolved first so that an unknown operation on a service that does not exist
        // is reported as the missing service, which is the more useful of the two.
        PublishedCoverage? coverage =
            await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false);

        if (coverage is null)
        {
            return;
        }

        await RefuseAsync(context, 400, Unserved(operation)).ConfigureAwait(false);
    }

    /// <summary>What to say about a path this face did not route.</summary>
    /// <param name="operation">The first segment after <c>ImageServer</c>.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <para>
    /// <b>A served operation asked for in the wrong shape gets told the shape, and an
    /// unserved one gets told it is unserved.</b> One message for both said
    /// <i>`tile` is not an operation this image service serves. It serves exportImage,
    /// identify, tile and tilemap</i> — denying and listing the same word in one sentence,
    /// which is what a client saw for <c>.../ImageServer/tile</c> with no segments after it.
    /// </para>
    /// <para>
    /// <b>The shape is spelled out rather than the count given</b>, because *three more
    /// segments* leaves the reader to guess which three and in what order, and the order is
    /// the part that is easy to get wrong.
    /// </para>
    /// </remarks>
    private static string Unserved(string operation) => operation switch
    {
        "tile" => "`tile` is asked for as `tile/{level}/{row}/{column}`, three whole numbers, "
            + "and this request did not have that shape.",

        "tilemap" => "`tilemap` is asked for as "
            + "`tilemap/{level}/{row}/{column}/{across}/{down}`, five whole numbers, and this "
            + "request did not have that shape.",

        "exportImage" or "identify" =>
            $"`{operation}` takes its arguments in the query string rather than in the path. "
                + "Nothing follows the operation name.",

        _ => $"`{operation}` is not an operation this image service serves. It serves "
            + "exportImage, identify, tile and tilemap.",
    };

    private static async Task ServiceAsync(
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

        CoverageInfo info = coverage.Info;
        TilingScheme scheme = TilingScheme.For(info);

        BandStatistics[] statistics =
            await MeasureAsync(coverage, readers, cancellation).ConfigureAwait(false);

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

            /*
              <b>Everything below is here because ArcGIS Pro's own raster reader refused
              the service without it</b>
              ([ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)
              condition 1). `arcpy.Raster` opens Esri's public Terrain3D image service
              from this machine and answered ERROR 000732 — *does not exist or is not
              supported* — for ours, in 0.01 s, before a byte crossed the network. A
              document short of what the reader parses is refused locally, and the
              refusal names nothing.

              <b>Every value is a fact about this service rather than a shape to satisfy
              a parser.</b> The temptation with a list this long is to fill it with
              plausible numbers; band statistics are deliberately still absent below
              rather than invented, because a made-up minimum is a stretch applied to
              somebody's data on a lie.
            */

            // Unnamed, because a GeoTIFF carries no band names and inventing
            // Red/Green/Blue would claim a three-band raster is optical.
            bandNames = BandNames(info.Bands.Count),

            // The stored tile, which is what a range read fetches. Zero when the file
            // is striped rather than tiled, and zero is the honest answer there.
            blockWidth = info.TileWidth,
            blockHeight = info.TileHeight,

            // <b>What this server does to the pixels, not what the file does.</b> The
            // source may be DEFLATE or LZW inside; by the time anything leaves here it
            // has been decoded, coloured and re-encoded as the requested format.
            compressionType = "None",
            defaultCompressionQuality = 90,

            // True: the warp resamples, and `CoverageWarp.Resample` says why it is
            // nearest neighbour.
            resampling = true,

            // One raster, not a mosaic — ADR-043 §3.2 scopes the first cut that way,
            // so there is no method to choose between and no operator to apply.
            serviceSourceType = "esriImageServiceSourceTypeRasterDataset",
            defaultMosaicMethod = "None",
            allowedMosaicMethods = string.Empty,
            mosaicOperator = "First",
            maxMosaicImageCount = 0,

            /*
              <b>A scheme, and still no cache, and those are two different facts.</b>
              `tileInfo` says how a client may name a piece of ground; `singleFusedMapCache`
              says whether this server has kept a picture of it. Esri's own documents tie
              the two so closely that they read as one, and separating them is what lets
              this face serve `tile` and `tilemap` honestly: a tile is rendered when it is
              asked for, out of the same coverage `exportImage` reads, so there is a scheme
              and there is no cache.

              <b>`exportTilesAllowed` is false and is the flag that would claim otherwise.</b>
              That is bulk export of a cache to a client, which needs a cache to export.
            */
            singleFusedMapCache = false,
            exportTilesAllowed = false,
            tileInfo = TileInfo(scheme),

            hasHistograms = false,
            hasRasterAttributeTable = false,

            // A raster dataset has no attribute rows, so the field list is empty and
            // says so rather than being omitted.
            objectIdField = "OBJECTID",
            fields = Array.Empty<object>(),
            maxRecordCount = 1000,

            minScale = 0,
            maxScale = 0,
            meanPixelSize = (info.PixelWidth + info.PixelHeight) / 2,

            // <b>Not offered, and each of these is a capability this face does not
            // answer</b> — correctness gate 2's fifth finding, applied to the flags
            // rather than only to the capabilities string.
            allowCopy = false,

            /*
              <b>True, and setting it false was the single thing that made every ArcGIS
              Pro raster workflow refuse this service.</b> `arcpy.Raster(url)` answered
              *does not exist or is not supported* for ours and opened Esri's own
              Terrain3D from the same machine; a bisect — serve Esri's document from our
              host, then replace one differing value at a time — narrowed forty-odd
              differences to this one field. With `allowAnalysis` true and nothing else
              changed, Pro opens it.

              <b>It was false because the flag was misread, which is the part to keep.</b>
              It sounds like *this server performs analysis* and it means *this service
              may be used as input to analysis* — that is, its pixels can be read for an
              arbitrary extent, size and reference. `exportImage` does exactly that, so
              true is the honest answer and false was a cautious guess about somebody
              else's vocabulary.

              <b>`allowRasterFunction` stays false and is the real limit.</b> That is the
              flag that would claim server-side function chains, which ADR-043 §3.2
              leaves out of the first cut.
            */
            allowAnalysis = true,

            allowComputeTiePoints = false,
            maxDownloadImageCount = 0,
            maxDownloadSizeLimit = 0,

            uncompressedSize = (long)info.Width * info.Height * info.Bands.Count
                * BytesPer(info.Bands[0].Kind),

            /*
              <b>Measured, not invented, and that distinction is the reason this is the
              last field to arrive.</b> ArcGIS Pro's raster reader needs band statistics
              to construct a raster at all — without them `arcpy.Raster` answers *does
              not exist or is not supported* — and the tempting fix for a list of
              missing fields is to fill it with plausible numbers. A made-up minimum is
              a stretch applied to somebody's data on a lie: every default rendering
              downstream, in Pro and here, is computed from these.

              <b>Read from the coarsest overview.</b> That is a few thousand samples
              rather than the whole raster, it is what a pyramid is for, and it is what
              every implementation of this does. The numbers are therefore approximate
              in the way a sample is approximate — and they are approximate about real
              pixels, which is a different thing from being made up.
            */
            minValues = Array.ConvertAll(statistics, b => b.Minimum),
            maxValues = Array.ConvertAll(statistics, b => b.Maximum),
            meanValues = Array.ConvertAll(statistics, b => b.Mean),
            stdvValues = Array.ConvertAll(statistics, b => b.StandardDeviation),
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
                await ArcGisParameters.LookupAsync(context, cancellation)
                    .ConfigureAwait(false),
                coverage.Info,
                new WidthHeight(settings.MaximumImageWidth, settings.MaximumImageHeight),
                out ImageServerExportParameters? asked,
                out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        await ExportOnceAsync(
                context, coverage, asked!, readers, canvases, projector, budget, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Draws a coverage over one extent at one size and writes the image.</summary>
    /// <param name="context">The request.</param>
    /// <param name="coverage">The coverage.</param>
    /// <param name="asked">What to draw and where.</param>
    /// <param name="readers">Opens the file.</param>
    /// <param name="canvases">Makes the picture.</param>
    /// <param name="projector">Reprojects when the reference is not the coverage's own.</param>
    /// <param name="budget">Admission control.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <b>Shared by <c>exportImage</c> and <c>tile</c>, and that is the point of it.</b>
    /// The two differ only in where their extent and size come from — a query string in one
    /// case, a tiling scheme in the other — and everything after that is the same decision
    /// about which overview to read, whether to warp, and what to do at the coverage's
    /// edge. Two copies of it would be two pictures of the same ground that agree until
    /// somebody changes one.
    /// </remarks>
    private static async Task ExportOnceAsync(
        HttpContext context,
        PublishedCoverage coverage,
        ImageServerExportParameters asked,
        ICoverageReaderFactory readers,
        IMapCanvasFactory canvases,
        IProjector projector,
        ConnectionBudget budget,
        CancellationToken cancellation)
    {
        /*
          <b>A reference this server does not have is refused before anything is drawn, and
          the sibling WFS face has asked this question since it shipped.</b>
          `WfsEndpoints` calls `KnowsAsync` on exactly this port; this face did not, and the
          consequence was measurable: `bboxSR=1000000000` answered a 91-byte transparent PNG
          with a 200 on it. A correctly-framed map over empty ground, which is this
          project's most-repeated failure mode and the one a client cannot distinguish from
          *there is nothing there*.

          <b>Some bad references raised and some did not, which is why asking is better than
          catching.</b> `bboxSR=999` and `bboxSR=999999` made PostGIS raise, and the raise
          reached the client as a refusal naming the code. `bboxSR=1000000000` did not raise
          at all and produced finite coordinates somewhere off the coverage. Relying on the
          projection database to complain meant relying on which of its several failure
          modes a given code happens to hit.

          <b>Asked only when the request names a reference other than the coverage's own</b>,
          so the ordinary case costs nothing. The answer is cached in the projector.
        */
        if (asked.Srid != coverage.Info.Srid
            && !await projector.KnowsAsync(asked.Srid, cancellation).ConfigureAwait(false))
        {
            await RefuseAsync(
                    context,
                    400,
                    "EPSG:" + asked.Srid.ToString(CultureInfo.InvariantCulture) + " is not a "
                        + "coordinate reference this server's projection database has. This "
                        + "service's coverage is stored in EPSG:"
                        + coverage.Info.Srid.ToString(CultureInfo.InvariantCulture)
                        + ", and an image can be asked for in any reference that database "
                        + "knows.")
                .ConfigureAwait(false);

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

        using IMapCanvas canvas = canvases.Create(asked.Width, asked.Height);

        canvas.Clear(asked.Format == MapImageFormat.Png ? Rgba.Transparent : Rgba.White);

        if (asked.Srid == coverage.Info.Srid)
        {
            await DrawAlignedAsync(canvas, coverage, asked, readers, cancellation)
                .ConfigureAwait(false);
        }
        else
        {
            string? refused = await DrawWarpedAsync(
                    canvas, coverage, asked, readers, projector, cancellation)
                .ConfigureAwait(false);

            if (refused is not null)
            {
                await RefuseAsync(context, 400, refused).ConfigureAwait(false);
                return;
            }
        }

        /*
          <b>`f=json` returns where the picture is, not the picture — and this face ignored
          the parameter completely.</b> `f=json`, `f=pjson`, `f=html` and outright rubbish
          all got PNG bytes back with a 200 on them. The sibling `MapServer/export` has
          answered the descriptor correctly since it shipped, so this was two faces on one
          server disagreeing about a parameter both of them document: a client that works
          against one breaks against the other for no reason it can discover.

          <b>The descriptor is `MapServerMetadataWriter.Export`, the same one the map face
          uses</b>, so the two answers have the same shape and the same field names. The
          JavaScript API places an image element from this and then fetches the `href`,
          which is why the href has to be this very request with `f=image` — every other
          parameter carried over, because an href that dropped the extent would name a
          different picture and the client would place it in the right frame.

          <b>Written after the drawing, not before it.</b> The image is not sent, but the
          admission lease, the plan and the encode all still happen: a descriptor that
          promised an href the server could not then honour would be worse than a slower
          one. It also means a request refused for its size or its extent is refused the
          same way whichever `f` it asked for.
        */
        byte[] image = canvas.Encode(asked.Format, 90);

        if (ArcGisResponseFormat.WantsJson(context))
        {
            string href = $"{context.Request.Scheme}://{context.Request.Host}"
                + context.Request.Path
                + ArcGisResponseFormat.WithFormat(context.Request.QueryString.Value, "image");

            await Results.Ok(MapServerMetadataWriter.Export(
                    href,
                    asked.Width,
                    asked.Height,
                    asked.Extent,
                    asked.Srid,
                    MapServerMetadataWriter.Scale(asked.Extent, asked.Width, asked.Srid)))
                .ExecuteAsync(context)
                .ConfigureAwait(false);

            return;
        }

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
    /// <summary>Draws a coverage into a canvas whose reference is not the coverage's.</summary>
    /// <param name="canvas">The picture.</param>
    /// <param name="coverage">The coverage.</param>
    /// <param name="asked">What was asked for.</param>
    /// <param name="readers">Opens the file.</param>
    /// <param name="projector">Moves the control-point grid between references.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>Null when it drew; otherwise why it could not.</returns>
    /// <remarks>
    /// <para>
    /// <b>Returning a sentence rather than nothing, because returning nothing was answering
    /// a blank picture with a 200 on it.</b> `bboxSR=1000000000` came back as a 91-byte
    /// transparent PNG — a correctly-framed map over empty ground, which is this project's
    /// most-repeated failure and the one hardest to see. Some out-of-range references make
    /// PostGIS raise, and those already reached the client as refusals; others come back as
    /// coordinates that are not numbers, and those got this far.
    /// </para>
    /// <para>
    /// <b>The distinction that has to survive: a box that misses the coverage still draws
    /// an empty picture</b>, because that is a real answer to a real question and
    /// [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)'s conformance
    /// suite asserts it. Only a projection that produced no usable ground is a refusal. The
    /// two look identical in the response and are not the same event.
    /// </para>
    /// </remarks>
    private static async Task<string?> DrawWarpedAsync(
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
                return Unprojectable(asked.Srid, coverage.Info.Srid);
            }

            groundX[i] = point.X;
            groundY[i] = point.Y;

            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        // <b>Ground that is not a number is not ground.</b> Some out-of-range references
        // make PostGIS raise, and the raise already reaches the client as a refusal; others
        // come back as coordinates that are not numbers, and those arrive here looking like
        // a box. Every comparison with `NaN` is false, so such a box passes the planner's
        // checks, the plan comes back empty, and the code below used to answer a blank
        // picture with a 200 on it — a correctly-framed map over empty ground, which is
        // this project's most-repeated failure. Same rule as the request parser applies to
        // a client's own ordinates, at the other boundary where numbers enter.
        if (!double.IsFinite(minX) || !double.IsFinite(minY)
            || !double.IsFinite(maxX) || !double.IsFinite(maxY))
        {
            return Unprojectable(asked.Srid, coverage.Info.Srid);
        }

        CoveragePlan? plan = CoveragePlanner.Plan(
            coverage.Info, new Envelope(minX, minY, maxX, maxY), asked.Width, asked.Height);

        // <b>No overlap, and that is an answer rather than a failure.</b> A box that
        // misses the coverage draws an empty picture, which ADR-043's suite asserts.
        if (plan is not { } read)
        {
            return null;
        }

        using ICoverageReader reader =
            await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

        CoverageWindow window = await reader.ReadAsync(
            read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
            .ConfigureAwait(false);

        Rgba[] painted = CoverageStyle.Parse(coverage.Style).Paint(window, coverage.Info.Bands);

        // <b>Asked of the planner rather than worked out again.</b> This was the same
        // division with the level lookup written out longhand beside it — one calculation in
        // two places, and the other copy is the one that chose the read window.
        (double perPixelX, double perPixelY) =
            CoveragePlanner.PixelSize(coverage.Info, read.Overview);

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

        return null;
    }

    /// <summary>Why a request in another reference could not be drawn.</summary>
    /// <param name="asked">The reference the client wrote its box in.</param>
    /// <param name="own">The coverage's own reference.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// <b>It names both references and does not guess which one is wrong</b>, because from
    /// here the two are indistinguishable: a reference the projection database does not
    /// really have, and a pair it cannot convert between, arrive the same way. Naming both
    /// lets the reader check the one they chose.
    /// </remarks>
    private static string Unprojectable(int asked, int own) =>
        "This request asks for EPSG:" + asked.ToString(CultureInfo.InvariantCulture)
            + " and this service's coverage is stored in EPSG:"
            + own.ToString(CultureInfo.InvariantCulture)
            + ". The projection database could not convert between them: it returned no "
            + "usable ground rather than an error, which means one of the two is not a "
            + "reference it really has. Check the code you sent in `bboxSR` or `imageSR`.";

    /// <summary>Draws one tile of the scheme this service publishes.</summary>
    /// <param name="context">The request.</param>
    /// <param name="serviceName">The service.</param>
    /// <param name="level">Which resolution.</param>
    /// <param name="row">Its row, counting down from the origin.</param>
    /// <param name="column">Its column, counting right from the origin.</param>
    /// <param name="coverages">The catalogue.</param>
    /// <param name="readers">Opens the file.</param>
    /// <param name="canvases">Makes the picture.</param>
    /// <param name="projector">
    /// Passed to the shared export path, which reprojects when a request's reference is not
    /// the coverage's own. <b>A tile request never is</b>: <see cref="TilingScheme.For"/>
    /// always returns a scheme in the coverage's own reference, so the warp is unreachable
    /// from here today. Named rather than dropped, because the alternative is a second
    /// export path for the tile route, and the whole point of sharing one is that a tile and
    /// an export of the same ground cannot diverge.
    /// </param>
    /// <param name="budget">Admission control.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>The tile's ground comes from the scheme and everything after that is the export
    /// path.</b> Same overview choice, same warp, same edge behaviour — because it is
    /// literally <see cref="ExportOnceAsync"/>, which <c>exportImage</c> also calls. A
    /// second drawing path would be the same picture computed twice and would disagree
    /// with the first the day one of them was changed.
    /// </para>
    /// <para>
    /// <b>A tile off the edge of the coverage is a transparent PNG, not a 404.</b> A
    /// client walking a grid asks for the corners; answering an error there turns a
    /// perfectly ordinary map view into a screen of broken tiles. <c>tilemap</c> exists so
    /// that a client can avoid asking, and this is what happens when it does not.
    /// </para>
    /// </remarks>
    private static async Task TileAsync(
        HttpContext context,
        string serviceName,
        int level,
        int row,
        int column,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IMapCanvasFactory canvases,
        IProjector projector,
        ConnectionBudget budget,
        CancellationToken cancellation)
    {
        PublishedCoverage? coverage =
            await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false);

        if (coverage is null)
        {
            return;
        }

        TilingScheme scheme = TilingScheme.For(coverage.Info);

        if (await RefusedAsync(context, scheme, level, row, column, 1, 1).ConfigureAwait(false))
        {
            return;
        }

        ImageServerExportParameters asked = ImageServerExportParameters.ForTile(
            scheme.Tile(level, row, column), scheme.TileSize, scheme.Srid);

        await ExportOnceAsync(
                context, coverage, asked, readers, canvases, projector, budget, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Says which tiles of a block this coverage has ground for.</summary>
    /// <param name="context">The request.</param>
    /// <param name="serviceName">The service.</param>
    /// <param name="level">Which resolution.</param>
    /// <param name="row">The block's top row.</param>
    /// <param name="column">The block's left column.</param>
    /// <param name="across">How many columns wide the block is.</param>
    /// <param name="down">How many rows tall it is.</param>
    /// <param name="coverages">The catalogue.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>One request that saves a client many.</b> A client about to draw a screenful of
    /// tiles asks this first and skips the ones that would come back empty, which along
    /// the edge of a coverage is most of them.
    /// </para>
    /// <para>
    /// <b>The answer is *overlaps the coverage's extent*, and that is deliberately weaker
    /// than *has pixels*.</b> A coverage's extent is a rectangle and its no-data is not, so
    /// a tile reported present may still draw as transparent. Answering the stronger
    /// question would mean reading pixels for every tile in the block, which is the cost
    /// this operation exists to avoid.
    /// </para>
    /// <para>
    /// <b>The block is bounded because the request names its own size.</b> A client asking
    /// for a million tiles in one call would otherwise get a million-element array built
    /// in memory; the ceiling is stated rather than clamped, so a client that hits it
    /// knows to ask twice instead of silently receiving a smaller answer than it thinks.
    /// </para>
    /// </remarks>
    private static async Task TilemapAsync(
        HttpContext context,
        string serviceName,
        int level,
        int row,
        int column,
        int across,
        int down,
        ICoverageCatalog coverages,
        CancellationToken cancellation)
    {
        PublishedCoverage? coverage =
            await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false);

        if (coverage is null)
        {
            return;
        }

        TilingScheme scheme = TilingScheme.For(coverage.Info);

        if (await RefusedAsync(context, scheme, level, row, column, across, down)
                .ConfigureAwait(false))
        {
            return;
        }

        int[] data = new int[across * down];

        for (int y = 0; y < down; y++)
        {
            for (int x = 0; x < across; x++)
            {
                data[(y * across) + x] =
                    scheme.Covers(coverage.Info, level, row + y, column + x) ? 1 : 0;
            }
        }

        await Results.Ok(new
        {
            adjusted = false,
            location = new { top = row, left = column, width = across, height = down },
            data,
            valid = true,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Refuses a tile request that names ground the scheme does not have.</summary>
    /// <param name="context">The request, refused on it if this returns true.</param>
    /// <param name="scheme">The service's tiling scheme.</param>
    /// <param name="level">Which resolution.</param>
    /// <param name="row">The tile's, or the block's top, row.</param>
    /// <param name="column">The tile's, or the block's left, column.</param>
    /// <param name="across">Block width in tiles; one for a single tile.</param>
    /// <param name="down">Block height in tiles; one for a single tile.</param>
    /// <returns>Whether the request was refused.</returns>
    /// <remarks>
    /// <para>
    /// <b>One guard for both routes, because they had two and the two disagreed.</b>
    /// <c>tile</c> refused a negative row by name and <c>tilemap</c> did not, so
    /// <c>tile/0/-1/0</c> was a 400 and <c>tilemap/0/-1/0/2/2</c> was a 200 with data in it
    /// — two routes answering differently about the same tile, which is worse than either
    /// answer alone. The level-range check was copy-pasted between them, message and all,
    /// which is how the row check came to exist in only one copy.
    /// </para>
    /// <para>
    /// <b>Bounded by the level's own grid, not merely by zero.</b> A level covers a
    /// finite grid and a row past its last one names nothing; refusing only negatives left
    /// <c>tilemap/5/2147483647/0/2/2</c> answering about a block whose second row had
    /// wrapped to a negative number, so the two halves described opposite sides of the
    /// world and the answer said nothing about it. Checking the far edge removes the
    /// overflow with the same sentence that removes the nonsense, which is better than a
    /// second check about arithmetic.
    /// </para>
    /// <para>
    /// <b>The block size is checked before the array exists.</b> <c>across</c> and
    /// <c>down</c> come from the path, and their product is the length of an allocation.
    /// </para>
    /// </remarks>
    private static async Task<bool> RefusedAsync(
        HttpContext context,
        TilingScheme scheme,
        int level,
        int row,
        int column,
        int across,
        int down)
    {
        if (level < 0 || level >= scheme.Levels.Count)
        {
            await RefuseAsync(
                    context,
                    400,
                    "This service is tiled at levels 0 to "
                        + (scheme.Levels.Count - 1).ToString(CultureInfo.InvariantCulture)
                        + " and level " + level.ToString(CultureInfo.InvariantCulture)
                        + " is not one of them.")
                .ConfigureAwait(false);

            return true;
        }

        if (across <= 0 || down <= 0)
        {
            await RefuseAsync(
                    context, 400, "A tilemap block is at least one tile wide and one tall.")
                .ConfigureAwait(false);

            return true;
        }

        if ((long)across * down > MaximumTilemapBlock)
        {
            await RefuseAsync(
                    context,
                    400,
                    "A tilemap answers at most "
                        + MaximumTilemapBlock.ToString(CultureInfo.InvariantCulture)
                        + " tiles at once and this asked for "
                        + ((long)across * down).ToString(CultureInfo.InvariantCulture) + ".")
                .ConfigureAwait(false);

            return true;
        }

        int wide = scheme.TilesAcross(level);
        int tall = scheme.TilesDown(level);

        if (row < 0 || column < 0
            || (long)column + across > wide || (long)row + down > tall)
        {
            await RefuseAsync(
                    context,
                    400,
                    "Level " + level.ToString(CultureInfo.InvariantCulture)
                        + " of this service's tiling scheme is "
                        + wide.ToString(CultureInfo.InvariantCulture) + " tiles across and "
                        + tall.ToString(CultureInfo.InvariantCulture) + " down, counting from "
                        + "zero at the origin, and row "
                        + row.ToString(CultureInfo.InvariantCulture) + " column "
                        + column.ToString(CultureInfo.InvariantCulture)
                        + (across * down > 1
                            ? " for a block " + across.ToString(CultureInfo.InvariantCulture)
                                + " by " + down.ToString(CultureInfo.InvariantCulture)
                            : string.Empty)
                        + " is outside it.")
                .ConfigureAwait(false);

            return true;
        }

        return false;
    }

    /// <summary>The scheme, in the shape a client reads it in.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <returns>An <c>tileInfo</c> object.</returns>
    /// <remarks>
    /// <b><c>format</c> is PNG because a tile may be transparent</b>, which is the same
    /// reasoning that makes an <c>exportImage</c> with no format answer PNG: a tile at the
    /// edge of a coverage is mostly nothing, and JPEG has no way to say so.
    /// <c>compressionQuality</c> is stated as zero rather than omitted, which is how every
    /// ArcGIS scheme states *not applicable* for a lossless format.
    /// </remarks>
    private static object TileInfo(TilingScheme scheme) => new
    {
        rows = scheme.TileSize,
        cols = scheme.TileSize,
        dpi = (int)TilingScheme.Dpi,
        format = "PNG",
        compressionQuality = 0,
        origin = new { x = scheme.OriginX, y = scheme.OriginY },
        spatialReference = new { wkid = scheme.Srid, latestWkid = scheme.Srid },
        lods = scheme.Levels
            .Select(l => new { level = l.Level, resolution = l.Resolution, scale = l.Scale })
            .ToArray(),
    };

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

        Func<string, string?> parameter =
            await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

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

        // <b>`Math.Clamp` throws when its minimum exceeds its maximum, so a zero-width
        // coverage would fault here — and it cannot be one.</b> `CoverageInfo`'s constructor
        // refuses a width or height of zero, so `Width - 1` is never below zero for any
        // instance that exists. Written down because a review raised it as a suspicion it
        // could not confirm, and an invariant nobody can find reads as a missing guard.
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
            // <b>`latestWkid` beside `wkid`, because every other reference object on this
            // face carries both</b> and a client that reads one field on the service
            // document and a different one here has to special-case this response.
            location = new
            {
                x,
                y,
                spatialReference = new { wkid = info.Srid, latestWkid = info.Srid },
            },
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

        LayerAccess.Reason reason = coverage is null
            ? LayerAccess.Reason.Denied
            : LayerAccess.Evaluate(
                coverage.Sharing, coverage.Owner, principal.Principal, principal.Authorization);

        if (!reason.IsAllowed())
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

        // <b>ADR-018 condition 3, for the face that does not resolve through
        // <see cref="ServiceLookup"/>.</b> A coverage carries its own catalogue, so the record
        // the service resolver writes would never be written for an image service without this.
        if (reason == LayerAccess.Reason.AdministrativeOverride)
        {
            await SharingAudit
                .RecordOverrideAsync(context, coverage!.QualifiedName, coverage.Sharing)
                .ConfigureAwait(false);
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

    // <b>The case-insensitive parameter lookup this face used to carry is
    // `ArcGisParameters` now.</b> `MapServerEndpoints` had its own copy of the same loop, and
    // two copies of a lookup is how the two faces came to disagree about where a parameter
    // may live: neither read a form, so neither answered a POST.

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

    /// <summary>What a band's values look like, sampled.</summary>
    private readonly record struct BandStatistics(
        double Minimum, double Maximum, double Mean, double StandardDeviation);

    /// <summary>
    /// Samples a coverage's coarsest resolution and describes each band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Computed per request rather than stored, in this first cut.</b> The coarsest
    /// overview of a pyramid is a few thousand samples, so the read is small; storing
    /// them would be a migration and a staleness question — a raster registered in
    /// place can be overwritten underneath us, and statistics stored at registration
    /// would then describe a file that no longer exists. That is a real decision and it
    /// belongs in its own change rather than in this one.
    /// </para>
    /// <para>
    /// <b>No-data samples are excluded.</b> A raster whose absent pixels are stored as
    /// zero would otherwise report a minimum of zero and a mean pulled towards it, and
    /// every default stretch computed from that is wrong in the same direction.
    /// </para>
    /// <para>
    /// <b>A failure here is empty statistics, not a failed request.</b> The document is
    /// answerable without touching the file — that is what registering in place buys —
    /// and a storage hiccup should not take the service description down with it.
    /// </para>
    /// </remarks>
    private static async Task<BandStatistics[]> MeasureAsync(
        PublishedCoverage coverage,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;

        BandStatistics[] statistics = new BandStatistics[info.Bands.Count];

        try
        {
            int level = info.Overviews.Count;

            (int width, int height) = level == 0
                ? (info.Width, info.Height)
                : (info.Overviews[level - 1].Width, info.Overviews[level - 1].Height);

            // Bounded, so a file with no pyramid does not read a hundred megapixels to
            // describe itself.
            width = Math.Min(width, 512);
            height = Math.Min(height, 512);

            using ICoverageReader reader =
                await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

            CoverageWindow window = await reader
                .ReadAsync(level, 0, 0, width, height, cancellation)
                .ConfigureAwait(false);

            for (int band = 0; band < info.Bands.Count; band++)
            {
                double? noData = info.Bands[band].NoData;

                double min = double.MaxValue;
                double max = double.MinValue;
                double sum = 0;
                double squares = 0;
                long counted = 0;

                for (int i = band; i < window.Samples.Length; i += window.Bands)
                {
                    double value = window.Samples[i];

                    if (noData is { } absent && value == absent)
                    {
                        continue;
                    }

                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                    sum += value;
                    squares += value * value;
                    counted++;
                }

                if (counted == 0)
                {
                    statistics[band] = new BandStatistics(0, 0, 0, 0);
                    continue;
                }

                double mean = sum / counted;
                double variance = Math.Max(0, (squares / counted) - (mean * mean));

                statistics[band] = new BandStatistics(min, max, mean, Math.Sqrt(variance));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException
            or UnauthorizedAccessException)
        {
            for (int band = 0; band < statistics.Length; band++)
            {
                statistics[band] = new BandStatistics(0, 0, 0, 0);
            }
        }

        return statistics;
    }

    /// <summary>
    /// Band names, which a GeoTIFF does not carry.
    /// </summary>
    /// <remarks>
    /// <b>Numbered rather than guessed.</b> A three-band raster is very often red,
    /// green and blue and is sometimes near-infrared, and a service that named them
    /// wrongly would have every downstream analysis applied to the wrong channel. The
    /// format does not say, so neither does this.
    /// </remarks>
    private static string[] BandNames(int count)
    {
        string[] names = new string[count];

        for (int i = 0; i < count; i++)
        {
            names[i] = "Band_" + (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return names;
    }

    /// <summary>How many bytes one sample occupies.</summary>
    private static int BytesPer(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => 1,
        SampleKind.Signed16 or SampleKind.Unsigned16 => 2,
        SampleKind.Signed32 or SampleKind.Real32 => 4,
        SampleKind.Real64 => 8,
        _ => 1,
    };

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
