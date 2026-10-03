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
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
internal static partial class ImageServerEndpoints
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

    private const string Capabilities = "Image,Tilemap,Catalog,Mensuration";

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

            // ADR-125: what a legend widget, Pro's contents pane and a stretch dialog ask of an image service.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/legend", Read, LegendAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/keyProperties", Read, KeyPropertiesAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-136: the JS SDK's ImageryLayer reads the functions from here when the service allows them, and does
            // not load when this is refused.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/rasterFunctionInfos", Read, RasterFunctionInfosAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/statistics", Read, StatisticsOperationAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-128: what Pro's stretch dialog draws its curve from.
            // ADR-141: the pixels inside an area, and the values at points, along a line or across an area.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/computeStatisticsHistograms", Read, ComputeStatisticsHistogramsAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/getSamples", Read, GetSamplesAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-148: ArcGIS's Download capability, as its clients ask it — the files, then each file.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/download", Read, DownloadListAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/file", Read, (HttpContext context, string serviceName,
                    ICoverageCatalog coverages, CancellationToken cancellation) =>
                {
                    (string? folder, string name) = Split(context, serviceName);
                    return CoverageAdminEndpoints.DownloadAsync(context, name, folder, null, coverages, cancellation);
                })
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/histograms", Read, HistogramsAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-155: mensuration, and moving between an image's columns and rows and the map.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/measure", Read, MeasureOperationAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/computePixelLocation", Read, PixelLocationAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/imageToMap", Read, (HttpContext context, string serviceName,
                    ICoverageCatalog coverages, ICoverageReaderFactory readers, CancellationToken cancellation) =>
                    ImageMapAsync(context, serviceName, true, coverages, readers, cancellation))
                .Governed(SharingGovernedExtensions.ByService);
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/mapToImage", Read, (HttpContext context, string serviceName,
                    ICoverageCatalog coverages, ICoverageReaderFactory readers, CancellationToken cancellation) =>
                    ImageMapAsync(context, serviceName, false, coverages, readers, cancellation))
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-154: a classified image's classes, as ArcGIS lists them.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/rasterAttributeTable", Read, RasterAttributeTableAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-159: a multidimensional service's variables, and the values of their dimensions.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/multidimensionalInfo", Read, MultidimensionalInfoAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-152: the catalog — a mosaic's images as rows, with their footprints.
            app.MapMethods($"{prefix}/{{serviceName}}/ImageServer/query", Read, CatalogQueryAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // ADR-152: ArcGIS's own editing of an image service's images — by whoever may manage it, and by POST, as each
            // changes what the service is.
            app.MapPost($"{prefix}/{{serviceName}}/ImageServer/uploads/upload", (HttpContext context, string serviceName,
                    ICoverageCatalog coverages, CancellationToken cancellation) =>
                {
                    (string? folder, string name) = Split(context, serviceName);
                    return CoverageAdminEndpoints.RestUploadAsync(context, folder, name, coverages, cancellation);
                })
                .DisableAntiforgery()
                .Governed(SharingGovernedExtensions.ByService);

            foreach ((string operation, Func<HttpContext, string?, string, Func<string, string?>, ICoverageCatalog, ICoverageReaderFactory, IAuditLog, CancellationToken, Task> edit) in
                new (string, Func<HttpContext, string?, string, Func<string, string?>, ICoverageCatalog, ICoverageReaderFactory, IAuditLog, CancellationToken, Task>)[]
                {
                    ("add", CoverageAdminEndpoints.RestAddAsync),
                    ("delete", CoverageAdminEndpoints.RestDeleteAsync),
                    ("update", CoverageAdminEndpoints.RestUpdateAsync),
                })
            {
                app.MapPost($"{prefix}/{{serviceName}}/ImageServer/{operation}", async (HttpContext context, string serviceName,
                        ICoverageCatalog coverages, ICoverageReaderFactory readers, IAuditLog audit, CancellationToken cancellation) =>
                    {
                        (string? folder, string name) = Split(context, serviceName);
                        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
                        await edit(context, folder, name, parameter, coverages, readers, audit, cancellation).ConfigureAwait(false);
                    })
                    .DisableAntiforgery()
                    .Governed(SharingGovernedExtensions.ByService);
            }

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

        "exportImage" or "identify" or "getSamples" or "computeStatisticsHistograms" =>
            $"`{operation}` takes its arguments in the query string rather than in the path. "
                + "Nothing follows the operation name.",

        _ => $"`{operation}` is not an operation this image service serves. It serves "
            + "exportImage, identify, getSamples, computeStatisticsHistograms, legend, keyProperties, rasterFunctionInfos, "
            + "statistics, histograms, download, file, multidimensionalInfo, query, tile and tilemap.",
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
            // ADR-127: TIFF is the values themselves, which Pro's Export Raster and a client's own renderer read.
            supportedImageFormatTypes = "JPGPNG,PNG,PNG8,PNG24,PNG32,JPG,JPEG,TIFF,LERC",

            // <b>Absent rather than zero when the file declares none.</b> Zero is a
            // legitimate measurement, so reporting it as the no-data value would tell a
            // client to discard real pixels.
            noDataValue = info.Bands[0].NoData,

            spatialReference = new { wkid = info.Srid, latestWkid = info.Srid },
            // ADR-148: Download when its owner offers the file to everyone it is shared with.
            // ADR-152: Edit when its images can be added and removed — an upload's, not files registered in place.
            capabilities = Capabilities + (coverage.Download ? ",Download" : "")
                + (CoverageAdminEndpoints.Uploaded(context.RequestServices.GetRequiredService<HostSettings>(), coverage.Path) ? ",Edit" : ""),
            // ADR-142: the default it draws with, which a class image's is not.
            defaultResamplingMethod = Resampler.Name(DefaultResampling(info, RasterFunction.FromStyleText(coverage.Style), null)),
            maxImageHeight = 4096,
            maxImageWidth = 4096,
            // ADR-136: the functions served, by the names ArcGIS gives them; None first, as ArcGIS lists it.
            allowRasterFunction = true,
            // The function the service is drawn through by default, its owner's choice — not ArcGIS's field, read by
            // Studio's Map Viewer to name what "the service's own drawing" is; absent when it draws its values.
            defaultRasterFunction = RasterFunction.FromStyleText(coverage.Style) is { Kind: not RasterFunctionKind.None } shown
                ? shown.Name : null,
            rasterFunctionInfos = FunctionInfos,
            // ADR-125: `statistics` answers, from a sample of the image's coarsest resolution.
            supportsStatistics = true,
            supportsAdvancedQueries = false,
            editFieldsInfo = (object?)null,
            hasColormap = false,
            // ADR-159: its images are slices of variables over time, depth or a level.
            hasMultidimensions = IsMultidimensional(coverage),

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

            // ADR-152: a mosaic is a catalog of images, ordered by object id with the last on top, and a mosaic rule may
            // choose others; a service over one file is a catalog of one.
            serviceSourceType = Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path)
                ? "esriImageServiceSourceTypeMosaicDataset" : "esriImageServiceSourceTypeRasterDataset",
            defaultMosaicMethod = "None",
            allowedMosaicMethods = "None,LockRaster,NorthWest,Center,Nadir,Viewpoint,ByAttribute",
            mosaicOperator = "Last",
            sortField = string.Empty,
            sortValue = (string?)null,
            maxMosaicImageCount = 500,
            // ADR-153: when its images say when they were taken, the service has time — theirs.
            timeInfo = TimeInfoOf(coverage),

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

            // ADR-128: `histograms` answers, from the same sample as the statistics.
            hasHistograms = true,
            // ADR-155: measured on the ground; in 3D over an elevation model's heights.
            mensurationCapabilities = info.Bands.Count == 1 ? "Basic,3D" : "Basic",

            // ADR-154: its owner's classes, or the table GDAL or ArcGIS wrote beside its file.
            hasRasterAttributeTable = AttributeTableOf(coverage) is not null,

            // ADR-152: the catalog's fields, which `query` answers.
            objectIdField = "OBJECTID",
            fields = CatalogFields,
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

              <b>`allowRasterFunction` is true since ADR-136</b>: Hillshade, Slope and Aspect
              are applied server-side and named in `rasterFunctionInfos`; any other
              function is refused by name, never drawn as if applied.
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

        // `f` is a picture or the JSON that says where one is; a KMZ asked for was answered with a PNG (reviewer,
        // 2026-10-01) — refused rather than answered as something else.
        if (ArcGisResponseFormat.Asked(context) is { Length: > 0 } f
            && !(f.Equals("image", StringComparison.OrdinalIgnoreCase) || ArcGisResponseFormat.WantsJson(context)))
        {
            await RefuseAsync(context, 400,
                $"`f={f}` is not a format this image service answers; `f=image` returns the picture and `f=json` "
                + "says where it is.").ConfigureAwait(false);
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (!ImageServerExportParameters.TryParse(
                parameter,
                coverage.Info,
                new WidthHeight(settings.MaximumImageWidth, settings.MaximumImageHeight),
                out ImageServerExportParameters? asked,
                out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // ADR-152, ADR-153: the images a mosaic rule or a time chooses, in their order.
        (readers, error) = await MosaicReadersAsync(context, parameter, coverage, readers,
                asked!.Srid == coverage.Info.Srid ? asked.Extent : coverage.Info.Extent, cancellation)
            .ConfigureAwait(false);

        if (error is not null)
        {
            await RefuseAsync(context, 400, error).ConfigureAwait(false);
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
    /// <param name="style">A style to draw with in place of the stored one — the Display settings' preview — or null.</param>
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
        CancellationToken cancellation,
        string? style = null)
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

        // ADR-136: the raster function asked for by `renderingRule`, or the one the service is shown with — none when a
        // display rule (ADR-138) chooses the colours, which draws the image's own values.
        // ADR-151: a display rule draws the function under it, or bandIds' bands, when the request names one.
        RasterFunction function = asked.Function
            ?? (asked.Display is not null ? RasterFunction.None : RasterFunction.FromStyleText(style ?? coverage.Style));

        // ADR-138: drawn through the rule, with the service's statistics when the rule stretches by them.
        Func<CoverageWindow, IReadOnlyList<BandInfo>, Rgba[]>? painter = null;

        if (asked.Display is { } display)
        {
            IReadOnlyList<BandSummary>? summaries = null;

            if (display.NeedsStatistics && await SampleAsync(coverage, readers, cancellation).ConfigureAwait(false) is { } read)
            {
                // ADR-151: stretched by the statistics of what is drawn — the function's values, not the image's.
                CoverageWindow sample = function.Kind == RasterFunctionKind.None ? read : ThroughSample(coverage.Info, function, read);
                IReadOnlyList<BandInfo> sampled = function.ResultBandsFor(coverage.Info.Bands);
                summaries = [.. Enumerable.Range(0, sample.Bands)
                    .Select(b => BandSummary.Of(sample, b, b < sampled.Count ? sampled[b] : null))];
            }

            painter = (window, bands) => display.Paint(window, bands, summaries);
        }
        // Also under a style being tried in Display: with classes, the classes are what it draws (ux review 8).
        else if (function.Kind == RasterFunctionKind.None && AttributeTableOf(coverage) is { Colours: true } table)
        {
            // ADR-154: a classified image is drawn in its classes' colours, as ArcGIS draws one with a table.
            painter = table.Paint;
        }

        // ADR-142: between cells as the request asks; else the service's default — nearest for a picture that may be
        // classes, bilinear for the rest — and nearest for values, which are then ones the image holds.
        // ADR-154: classes are read at the nearest cell, never between two classes.
        Resampling how = asked.Interpolation
            ?? (painter is not null && asked.Display is null ? Resampling.Nearest : DefaultResampling(coverage.Info, function, asked.Display));

        // ADR-127: the values themselves, as a GeoTIFF in their own type — read through the same plan the picture is.
        if (asked.Raw)
        {
            (byte[]? file, string? refusal) = await RawAsync(coverage, asked, function, readers, projector,
                    asked.Interpolation ?? Resampling.Nearest, cancellation)
                .ConfigureAwait(false);

            if (refusal is not null)
            {
                await RefuseAsync(context, 400, refusal).ConfigureAwait(false);
                return;
            }

            await AnswerAsync(context, asked, file!, asked.Lerc ? "application/octet-stream" : "image/tiff", cancellation)
                .ConfigureAwait(false);
            return;
        }

        using IMapCanvas canvas = canvases.Create(asked.Width, asked.Height);

        canvas.Clear(asked.Format == MapImageFormat.Png ? Rgba.Transparent : Rgba.White);

        if (asked.Srid == coverage.Info.Srid)
        {
            await DrawAlignedAsync(canvas, coverage, asked, readers, style, function, painter, how, cancellation)
                .ConfigureAwait(false);
        }
        else
        {
            string? refused = await DrawWarpedAsync(
                    canvas, coverage, asked, readers, projector, style, function, painter, how, cancellation)
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

        await AnswerAsync(context, asked, image, asked.Format == MapImageFormat.Png ? "image/png" : "image/jpeg", cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Writes an export: the file, or with <c>f=json</c> where it is.</summary>
    private static async Task AnswerAsync(
        HttpContext context, ImageServerExportParameters asked, byte[] image, string contentType, CancellationToken cancellation)
    {
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

        context.Response.ContentType = contentType;

        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The coverage's values over the asked extent and size, as a GeoTIFF in their own type — ADR-127 — or as LERC — ADR-137. Nearest
    /// neighbour, as the picture is resampled, so a value in the file is one the coverage holds. Ground the coverage
    /// does not cover is its no-data value, or zero when it declares none.
    /// </summary>
    private static async Task<(byte[]? File, string? Refused)> RawAsync(
        PublishedCoverage coverage,
        ImageServerExportParameters asked,
        RasterFunction function,
        ICoverageReaderFactory readers,
        IProjector projector,
        Resampling how,
        CancellationToken cancellation)
    {
        (RawValues? read, string? refused) = await RawValuesAsync(coverage, asked, function, readers, projector, how, cancellation)
            .ConfigureAwait(false);

        if (read is not { } values)
        {
            return (null, refused);
        }

        int width = asked.Width;
        int height = asked.Height;

        // ADR-137: as LERC, a pixel with nothing in it is left out by the mask rather than given a value that could be
        // mistaken for one — ground outside the image, and the image's own no-data.
        if (asked.Lerc)
        {
            bool[] valid = new bool[width * height];
            bool derived = function.Derives;

            for (int i = 0; values.Taken is not null && i < valid.Length; i++)
            {
                valid[i] = values.Taken[i] >= 0
                    && (derived || values.NoData is not { } none || values.Samples[i * values.Bands] != none);
            }

            return (LercWriter.Write(values.Samples, valid, width, height, values.Bands, values.Kind, asked.Tolerance), null);
        }

        byte[] file = GeoTiffWriter.Write(
            values.Samples,
            width,
            height,
            values.Bands,
            values.Kind,
            asked.Extent.MinX,
            asked.Extent.MaxY,
            asked.Extent.Width / width,
            asked.Extent.Height / height,
            asked.Srid,
            AxisOrder.IsGeographic(asked.Srid),
            values.NoData);

        return (file, null);
    }

    /// <summary>
    /// What a raw read answers: the values, pixel-interleaved; how many a pixel; their type and no-data; and which
    /// source cell each output pixel took, or -1 where none — ADR-127, shared since ADR-147 with conforming a mosaic's
    /// image to its grid.
    /// </summary>
    internal sealed record RawValues(double[] Samples, int Bands, SampleKind Kind, double? NoData, int[]? Taken);

    /// <summary>The coverage's values over an extent and size, in their own type — the half of a raw export before the file.</summary>
    internal static async Task<(RawValues? Values, string? Refused)> RawValuesAsync(
        PublishedCoverage coverage,
        ImageServerExportParameters asked,
        RasterFunction function,
        ICoverageReaderFactory readers,
        IProjector projector,
        Resampling how,
        CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;
        int width = asked.Width;
        int height = asked.Height;
        // ADR-136: a function's values are one 32-bit band, NaN where there is none; ADR-151: bands chosen keep theirs.
        bool derived = function.Derives;
        IReadOnlyList<BandInfo> resultBands = function.ResultBandsFor(info.Bands);
        int bands = Math.Max(1, resultBands.Count);
        double? noData = derived ? double.NaN : resultBands.Count > 0 ? resultBands[0].NoData : null;

        double[] samples = new double[width * height * bands];
        Array.Fill(samples, noData ?? 0);

        int[]? taken = null;
        CoverageWindow? window = null;

        // ADR-142: where each output pixel falls in the window, for values read between cells; null for nearest.
        double[]? positions = how == Resampling.Nearest ? null : new double[width * height * 2];

        if (asked.Srid == info.Srid)
        {
            if (CoveragePlanner.Plan(info, asked.Extent, width, height) is { } read)
            {
                using ICoverageReader reader = await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);
                (window, _) = await ReadThroughAsync(reader, coverage, function, read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
                    .ConfigureAwait(false);

                PixelBox to = read.Destination;
                double across = to.MaxX - to.MinX;
                double down = to.MaxY - to.MinY;
                taken = new int[width * height];
                Array.Fill(taken, -1);

                for (int y = 0; y < height && across > 0 && down > 0; y++)
                {
                    double py = y + 0.5;

                    if (py < to.MinY || py >= to.MaxY)
                    {
                        continue;
                    }

                    int row = Math.Clamp((int)((py - to.MinY) / down * window.Height), 0, window.Height - 1);

                    for (int x = 0; x < width; x++)
                    {
                        double px = x + 0.5;

                        if (px < to.MinX || px >= to.MaxX)
                        {
                            continue;
                        }

                        int column = Math.Clamp((int)((px - to.MinX) / across * window.Width), 0, window.Width - 1);
                        taken[(y * width) + x] = (row * window.Width) + column;

                        if (positions is not null)
                        {
                            positions[((y * width) + x) * 2] = (px - to.MinX) / across * window.Width;
                            positions[(((y * width) + x) * 2) + 1] = (py - to.MinY) / down * window.Height;
                        }
                    }
                }
            }
        }
        else
        {
            (CoverageWarp? warp, CoveragePlan? planned, string? refused) =
                await WarpPlanAsync(coverage, asked, projector, cancellation).ConfigureAwait(false);

            if (refused is not null)
            {
                return (null, refused);
            }

            if (planned is { } read)
            {
                using ICoverageReader reader = await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);
                (window, _) = await ReadThroughAsync(reader, coverage, function, read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
                    .ConfigureAwait(false);

                (double perPixelX, double perPixelY) = CoveragePlanner.PixelSize(info, read.Overview);
                taken = warp!.Indices(
                    window.Width,
                    window.Height,
                    info.Extent.MinX + (read.X * perPixelX),
                    info.Extent.MaxY - (read.Y * perPixelY),
                    perPixelX,
                    perPixelY);

                if (positions is not null)
                {
                    positions = warp.Positions(
                        info.Extent.MinX + (read.X * perPixelX), info.Extent.MaxY - (read.Y * perPixelY), perPixelX, perPixelY);
                }
            }
        }

        if (taken is not null && window is not null)
        {
            int carried = Math.Min(bands, window.Bands);

            for (int i = 0; i < taken.Length; i++)
            {
                if (taken[i] < 0)
                {
                    continue;
                }

                for (int band = 0; band < carried; band++)
                {
                    samples[(i * bands) + band] = window.Samples[(taken[i] * window.Bands) + band];

                    // ADR-142: between cells, where every neighbour has a value; the nearest one otherwise.
                    if (positions is not null)
                    {
                        double? empty = derived ? null : band < resultBands.Count ? resultBands[band].NoData : null;

                        if (Resampler.TryValue(window.Samples, window.Width, window.Height, window.Bands, band,
                                positions[i * 2], positions[(i * 2) + 1], how,
                                v => double.IsNaN(v) || (empty is { } none && v == none), out double between))
                        {
                            samples[(i * bands) + band] = between;
                        }
                    }
                }
            }
        }

        SampleKind kind = derived ? SampleKind.Real32 : resultBands.Count > 0 ? resultBands[0].Kind : SampleKind.Unsigned8;

        return (new RawValues(samples, bands, kind, noData, taken), null);
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
        string? style,
        RasterFunction function,
        Func<CoverageWindow, IReadOnlyList<BandInfo>, Rgba[]>? painter,
        Resampling how,
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

        (CoverageWindow window, IReadOnlyList<BandInfo> bands) = await ReadThroughAsync(
            reader, coverage, function, read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
            .ConfigureAwait(false);

        Rgba[] pixels = painter is not null
            ? painter(window, bands)
            : (function.Kind != RasterFunctionKind.None && !function.KeepsImageStyle
                ? function.Style
                : await StyleOfAsync(coverage, style ?? coverage.Style, readers, cancellation).ConfigureAwait(false)).Paint(window, bands);

        canvas.DrawImage(pixels, window.Width, window.Height, read.Destination, how);
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
    /// <param name="style">A style in place of the stored one, or null.</param>
    /// <param name="function">The raster function to draw through — ADR-136.</param>
    /// <param name="painter">Colours the values in place of the style, under a display rule — ADR-138 — or null.</param>
    /// <param name="how">How the window is read between its pixels — ADR-142.</param>
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
        string? style,
        RasterFunction function,
        Func<CoverageWindow, IReadOnlyList<BandInfo>, Rgba[]>? painter,
        Resampling how,
        CancellationToken cancellation)
    {
        (CoverageWarp? warp, CoveragePlan? planned, string? refused) =
            await WarpPlanAsync(coverage, asked, projector, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        // <b>No overlap, and that is an answer rather than a failure.</b> A box that
        // misses the coverage draws an empty picture, which ADR-043's suite asserts.
        if (planned is not { } read)
        {
            return null;
        }

        using ICoverageReader reader =
            await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

        (CoverageWindow window, IReadOnlyList<BandInfo> bands) = await ReadThroughAsync(
            reader, coverage, function, read.Overview, read.X, read.Y, read.Width, read.Height, cancellation)
            .ConfigureAwait(false);

        Rgba[] painted = painter is not null
            ? painter(window, bands)
            : (function.Kind != RasterFunctionKind.None && !function.KeepsImageStyle
                ? function.Style
                : await StyleOfAsync(coverage, style ?? coverage.Style, readers, cancellation).ConfigureAwait(false)).Paint(window, bands);

        // <b>Asked of the planner rather than worked out again.</b> This was the same
        // division with the level lookup written out longhand beside it — one calculation in
        // two places, and the other copy is the one that chose the read window.
        (double perPixelX, double perPixelY) =
            CoveragePlanner.PixelSize(coverage.Info, read.Overview);

        Rgba[] pixels = warp!.Resample(
            painted,
            window.Width,
            window.Height,
            coverage.Info.Extent.MinX + (read.X * perPixelX),
            coverage.Info.Extent.MaxY - (read.Y * perPixelY),
            perPixelX,
            perPixelY,
            how);

        canvas.DrawImage(
            pixels, asked.Width, asked.Height, new PixelBox(0, 0, asked.Width, asked.Height));

        return null;
    }

    /// <summary>
    /// How a picture is read between cells when the request does not say — ADR-142: nearest for one band of bytes, which
    /// may be classes, and for a display rule's colours of values, which are classes; bilinear for the rest — heights,
    /// photographs, and anything a raster function made.
    /// </summary>
    internal static Resampling DefaultResampling(CoverageInfo info, RasterFunction function, DisplayRule? display) =>
        display is { Stretched: false } ? Resampling.Nearest
        : function.Kind != RasterFunctionKind.None ? Resampling.Bilinear
        : info.Bands.Count == 1 && info.Bands[0].Kind == SampleKind.Unsigned8 && display is null ? Resampling.Nearest
        : Resampling.Bilinear;

    /// <summary>
    /// Reads a window, through a raster function when one is asked for — ADR-136. The function needs each cell's eight
    /// neighbours, so the window is read a cell wider on every side the file allows and cut back after, which keeps a
    /// tile's edge from being shaded as if the ground stopped there.
    /// </summary>
    private static async Task<(CoverageWindow Window, IReadOnlyList<BandInfo> Bands)> ReadThroughAsync(
        ICoverageReader reader,
        PublishedCoverage coverage,
        RasterFunction function,
        int overview,
        int x,
        int y,
        int width,
        int height,
        CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;

        if (function.Kind == RasterFunctionKind.None)
        {
            return (await reader.ReadAsync(overview, x, y, width, height, cancellation).ConfigureAwait(false), info.Bands);
        }

        // ADR-151: NDVI, an expression and a choice of bands are pixel by pixel, and need no neighbours.
        (double perX, double perY) = CoveragePlanner.PixelSize(info, overview);

        // ADR-151, ADR-156: pixel by pixel, the window as read; a clip is told where it lies.
        if (function.Margin == 0)
        {
            CoverageWindow read = await reader.ReadAsync(overview, x, y, width, height, cancellation).ConfigureAwait(false);
            return (function.Apply(read, 0, 0, info.Bands, (info.Extent.MinX + (x * perX), info.Extent.MaxY - (y * perY), perX, perY)),
                function.ResultBandsFor(info.Bands));
        }

        (int levelWidth, int levelHeight) = overview == 0
            ? (info.Width, info.Height)
            : (info.Overviews[overview - 1].Width, info.Overviews[overview - 1].Height);
        // A surface needs one cell beyond the window, a neighbourhood half its kernel (ADR-156), where the file has them.
        int margin = function.Margin;
        int left = Math.Min(margin, x);
        int top = Math.Min(margin, y);
        int right = Math.Min(margin, levelWidth - (x + width));
        int bottom = Math.Min(margin, levelHeight - (y + height));

        CoverageWindow wide = await reader.ReadAsync(
            overview, x - left, y - top, width + left + right, height + top + bottom, cancellation).ConfigureAwait(false);

        double latitude = info.Extent.MaxY - ((y + (height / 2.0)) * perY);
        (double metresX, double metresY) = RasterFunction.Metres(perX, perY, AxisOrder.IsGeographic(info.Srid), latitude);
        CoverageWindow derived = function.Apply(wide, metresX, metresY, info.Bands);

        double[] cut = new double[width * height];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(derived.Samples, ((row + top) * wide.Width) + left, cut, row * width, width);
        }

        return (new CoverageWindow(width, height, 1, cut), RasterFunction.ResultBands);
    }

    /// <summary>
    /// Where a request written in another reference falls on the coverage: the warp from canvas to ground, and what to
    /// read — or why it cannot be answered. Shared by the picture and the raw values (ADR-127), so the two agree.
    /// </summary>
    private static async Task<(CoverageWarp? Warp, CoveragePlan? Plan, string? Refused)> WarpPlanAsync(
        PublishedCoverage coverage,
        ImageServerExportParameters asked,
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
                return (null, null, Unprojectable(asked.Srid, coverage.Info.Srid));
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
            return (null, null, Unprojectable(asked.Srid, coverage.Info.Srid));
        }

        CoveragePlan? plan = CoveragePlanner.Plan(
            coverage.Info, new Envelope(minX, minY, maxX, maxY), asked.Width, asked.Height);

        // <b>No overlap, and that is an answer rather than a failure.</b> A box that
        // misses the coverage draws an empty picture, which ADR-043's suite asserts.
        return (new CoverageWarp(asked.Width, asked.Height, steps, groundX, groundY), plan, null);
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

        // ADR-159: a multidimensional service's tiles are its first slice, as its exported pictures are by default.
        if (IsMultidimensional(coverage))
        {
            (readers, _) = await MosaicReadersAsync(context, _ => null, coverage, readers, asked.Extent, cancellation).ConfigureAwait(false);
        }

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
        IProjector projector,
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

        // <b>What export refuses, identify refuses too</b> — a slope asked for and the elevation answered is the same
        // accepted-and-not-applied shape (D-125), found by the reviewer's second pass on this operation.
        if (!ImageServerExportParameters.TryUnoffered(parameter, coverage.Info, out RasterFunction? asked, out string? unoffered))
        {
            await RefuseAsync(context, 400, unoffered!).ConfigureAwait(false);
            return;
        }

        if (MosaicParametersError(parameter) is { } unread)
        {
            await RefuseAsync(context, 400, unread).ConfigureAwait(false);
            return;
        }

        if (!ImageServerExportParameters.TryPoint(
                parameter, coverage.Info, out double x, out double y, out int pointSrid, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        CoverageInfo info = coverage.Info;

        // ADR-123: a point written in another reference — Web Mercator, from a map — is projected into the coverage's.
        if (pointSrid != info.Srid)
        {
            (IReadOnlyList<Geometry> projected, _) = await projector
                .ProjectAsync([new Point(x, y)], pointSrid, info.Srid, cancellation).ConfigureAwait(false);

            if (projected.Count != 1 || projected[0] is not Point moved || !double.IsFinite(moved.X) || !double.IsFinite(moved.Y))
            {
                await RefuseAsync(context, 400,
                    $"The point could not be projected from EPSG:{pointSrid.ToString(CultureInfo.InvariantCulture)} into "
                    + $"this image's EPSG:{info.Srid.ToString(CultureInfo.InvariantCulture)}.").ConfigureAwait(false);
                return;
            }

            x = moved.X;
            y = moved.Y;
        }

        // <b>`NoData`, as ArcGIS spells it, and the point beside it</b> — a null value with no location read as a fault
        // rather than as *nothing measured here* (reviewer, 2026-10-01).
        object location = new { x, y, spatialReference = new { wkid = info.Srid, latestWkid = info.Srid } };

        if (x < info.Extent.MinX || x > info.Extent.MaxX
            || y < info.Extent.MinY || y > info.Extent.MaxY)
        {
            await Results.Ok(new { objectId = 0, name = "Pixel", value = "NoData", location })
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

        // ADR-152, ADR-153: the pixel of the image a mosaic rule or a time puts on top here.
        (readers, string? chosen) = await MosaicReadersAsync(context, parameter, coverage, readers, new Envelope(x, y, x, y), cancellation)
            .ConfigureAwait(false);

        if (chosen is not null)
        {
            await RefuseAsync(context, 400, chosen).ConfigureAwait(false);
            return;
        }

        using ICoverageReader reader =
            await readers.OpenAsync(coverage.Path, cancellation).ConfigureAwait(false);

        // ADR-136: through a raster function — the one asked for, or the service's default — the pixel's answer is the
        // function's: its slope, its aspect, its shade. Which one is said beside it, so a client can label it.
        RasterFunction function = asked ?? RasterFunction.FromStyleText(coverage.Style);
        if (function.Kind != RasterFunctionKind.None)
        {
            (CoverageWindow derived, _) = await ReadThroughAsync(reader, coverage, function, 0, column, row, 1, 1, cancellation)
                .ConfigureAwait(false);
            await Results.Ok(new
            {
                objectId = 0,
                name = "Pixel",
                // ADR-151: bands chosen are several values, space-separated as ArcGIS writes a pixel of several bands.
                value = string.Join(" ", derived.Samples.Take(derived.Bands).Select(RasterFunction.Say)),
                rasterFunction = function.Name,
                location,
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        CoverageWindow window =
            await reader.ReadAsync(0, column, row, 1, 1, cancellation).ConfigureAwait(false);

        // <b>Space-separated, which is Esri's spelling for a multi-band pixel.</b> A
        // JSON array would be the better shape and would be a shape their clients do
        // not read; ADR-005's rule is that a compatibility surface speaks the other
        // product's dialect and the honesty lives in the documentation.
        string[] values = new string[window.Bands];
        bool measured = false;

        for (int band = 0; band < window.Bands; band++)
        {
            double sample = window.At(0, 0, band);
            values[band] = sample.ToString(CultureInfo.InvariantCulture);
            // NaN is no value whether or not the image declares a no-data: a float's hole, or ground no image covers.
            measured |= !(double.IsNaN(sample) || (band < info.Bands.Count && info.Bands[band].NoData is { } empty && sample.Equals(empty)));
        }

        // ADR-154: a classified pixel says its class, as ArcGIS's identify reads the table.
        AttributeClass? named = measured && window.Bands == 1 && AttributeTableOf(coverage) is { } classes
            ? classes.Find(window.At(0, 0, 0)) : null;

        await Results.Ok(new
        {
            objectId = 0,
            name = "Pixel",
            value = measured ? string.Join(' ', values) : "NoData",
            attributes = named is null ? null : new Dictionary<string, object?> { ["Value"] = named.Value, ["ClassName"] = named.Name },
            // <b>`latestWkid` beside `wkid`, because every other reference object on this
            // face carries both</b> and a client that reads one field on the service
            // document and a different one here has to special-case this response.
            location,
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
                coverage.Sharing, coverage.Owner, principal.Principal, principal.Authorization, coverage.SharedWith);

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

    /// <summary>The default style worked out for a coverage nobody has styled, by its file and when it changed.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CoverageStyle> Defaults = new();

    /// <summary>
    /// How a coverage is drawn — ADR-123: its stored style, or, when nobody has chosen one, a stretch worked out from
    /// its own values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The full range is right for bytes and wrong for everything else.</b> A 12-bit satellite image stretched over
    /// 0–65535 is nearly black, and a float elevation model stretched over 0–1 is white — the reviewer's finding.
    /// Eight-bit data keeps the full range, which is its own range; anything wider is stretched over what it holds:
    /// one band between its sampled minimum and maximum, colour between two standard deviations either side of the
    /// mean, as ArcGIS stretches imagery by default.
    /// </para>
    /// <para>
    /// <b>Fixed, not per window</b>, so adjacent tiles agree — the property ADR-043 chose the full range for. Kept by
    /// the file's path and the time it last changed, so a file replaced underneath gets a new one.
    /// </para>
    /// </remarks>
    internal static Task<CoverageStyle> StyleOfAsync(
        PublishedCoverage coverage, ICoverageReaderFactory readers, CancellationToken cancellation) =>
        StyleOfAsync(coverage, coverage.Style, readers, cancellation);

    /// <summary>How a coverage is drawn under a style's text — its stored one, or one being tried in Display.</summary>
    private static async Task<CoverageStyle> StyleOfAsync(
        PublishedCoverage coverage, string? text, ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(text) && !CoverageStyle.IsAuto(text))
        {
            return CoverageStyle.Parse(text);
        }

        // `stretch:auto;ramp:…` is the worked-out stretch with a ramp chosen over it.
        string? ramp = text is null ? null : CoverageStyle.Parse(text).RampName;

        if (coverage.Info.Bands.Count == 0 || coverage.Info.Bands[0].Kind == SampleKind.Unsigned8)
        {
            return CoverageStyle.Default.WithRamp(ramp);
        }

        string key = coverage.Path + "|" + (System.IO.File.Exists(coverage.Path)
            ? System.IO.File.GetLastWriteTimeUtc(coverage.Path).Ticks.ToString(CultureInfo.InvariantCulture)
            : "0");

        if (!Defaults.TryGetValue(key, out CoverageStyle? known))
        {
            BandStatistics[] measured = await MeasureAsync(coverage, readers, cancellation).ConfigureAwait(false);
            known = DefaultFrom(measured);
            Defaults[key] = known;
        }

        return known.WithRamp(ramp);
    }

    /// <summary>
    /// Draws a coverage's whole extent under a style not yet saved — ADR-123, the Display settings' picture, which
    /// redraws as its owner changes the controls rather than after they commit.
    /// </summary>
    internal static async Task PreviewAsync(
        HttpContext context,
        PublishedCoverage coverage,
        string style,
        int width,
        int height,
        ICoverageReaderFactory readers,
        IMapCanvasFactory canvases,
        IProjector projector,
        ConnectionBudget budget,
        HostSettings settings,
        CancellationToken cancellation)
    {
        Envelope e = coverage.Info.Extent;
        Dictionary<string, string> asked = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bbox"] = string.Join(',', new[] { e.MinX, e.MinY, e.MaxX, e.MaxY }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))),
            ["bboxSR"] = coverage.Info.Srid.ToString(CultureInfo.InvariantCulture),
            ["size"] = FormattableString.Invariant($"{width},{height}"),
            ["format"] = "png",
        };

        if (!ImageServerExportParameters.TryParse(
                key => asked.TryGetValue(key, out string? value) ? value : null,
                coverage.Info,
                new WidthHeight(settings.MaximumImageWidth, settings.MaximumImageHeight),
                out ImageServerExportParameters? parameters,
                out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        await ExportOnceAsync(context, coverage, parameters!, readers, canvases, projector, budget, cancellation, style)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The image service's legend — ADR-125: the colours it is drawn in, as ArcGIS writes a raster layer's legend, so
    /// the JS SDK's Legend widget and Pro's contents pane show the ramp its owner chose rather than nothing.
    /// </summary>
    /// <remarks>
    /// A single band is <c>Stretched</c>: its high and low values with the colours at the ends of its ramp. Three or
    /// more are an <c>RGB Composite</c>: the first three bands named for the channels they are drawn in.
    /// </remarks>
    private static async Task LegendAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        IMapCanvasFactory canvases,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        CoverageStyle style = await StyleOfAsync(coverage, readers, cancellation).ConfigureAwait(false);
        IReadOnlyList<BandInfo> bands = coverage.Info.Bands;

        object Entry(string label, Rgba colour)
        {
            using IMapCanvas swatch = canvases.Create(20, 20);
            swatch.Clear(colour);
            return new
            {
                label,
                url = string.Empty,
                imageData = Convert.ToBase64String(swatch.Encode(MapImageFormat.Png, 90)),
                contentType = "image/png",
                height = 20,
                width = 20,
            };
        }

        object[] entries;
        string kind;

        if (AttributeTableOf(coverage) is { Colours: true } classes && classes.Classes.Count <= 256)
        {
            // ADR-154: a classified image's legend is its classes.
            kind = "Unique Values";
            entries = [.. classes.Classes.Where(c => c.Colour is not null).Select(c => Entry(c.Name, c.Colour!.Value))];
        }
        else if (bands.Count >= 3)
        {
            kind = "RGB Composite";
            entries =
            [
                Entry("Red: Band_1", new Rgba(255, 0, 0, 255)),
                Entry("Green: Band_2", new Rgba(0, 255, 0, 255)),
                Entry("Blue: Band_3", new Rgba(0, 0, 255, 255)),
            ];
        }
        else
        {
            kind = "Stretched";
            (double low, double high) = style.Minimum is { } min && style.Maximum is { } max
                ? (min, max)
                : FullRange(bands.Count > 0 ? bands[0].Kind : SampleKind.Unsigned8);
            entries =
            [
                Entry("High : " + high.ToString("G6", CultureInfo.InvariantCulture), style.Along(1)),
                Entry("Low : " + low.ToString("G6", CultureInfo.InvariantCulture), style.Along(0)),
            ];
        }

        await Results.Ok(new
        {
            layers = new[]
            {
                new
                {
                    layerId = 0,
                    layerName = coverage.ServiceName,
                    layerType = "Raster Layer",
                    minScale = 0,
                    maxScale = 0,
                    legendType = kind,
                    legend = entries,
                },
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The raster functions a service names, None first as ArcGIS lists them — ADR-136.</summary>
    private static readonly string[] FunctionNames = ["None", .. RasterFunction.Names];

    private static readonly object[] FunctionInfos = [.. FunctionNames
        .Select(name => new { name, description = string.Empty, help = string.Empty })];

    /// <summary>The range a pixel type's full stretch runs over.</summary>
    /// <remarks>From zero, as <see cref="CoverageStyle"/> stretches it: a float band has no range and is given one.</remarks>
    private static (double Low, double High) FullRange(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => (0, 255),
        SampleKind.Signed16 => (0, short.MaxValue),
        SampleKind.Unsigned16 => (0, ushort.MaxValue),
        SampleKind.Signed32 => (0, int.MaxValue),
        _ => (0, 1),
    };

    /// <summary>
    /// The image's key properties — ADR-125. An image service over one file has none of the catalog's (sensor,
    /// acquisition date, cloud cover); an empty object is ArcGIS's own answer for that, where a refusal stopped clients
    /// that ask before drawing.
    /// </summary>
    private static async Task KeyPropertiesAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        await Results.Ok(new { }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The files an image service offers — ADR-148, ArcGIS's <c>download</c> — when its Download capability is on: one
    /// raster, its file or its mosaic's zip, fetched from <c>file</c>. Refused, as an operation it does not offer, when
    /// the capability is off.
    /// </summary>
    private static readonly int[] OneRaster = [1];

    private static async Task DownloadListAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (!coverage.Download)
        {
            await RefuseAsync(context, 400, "This image service does not offer its file: its Download capability is off.")
                .ConfigureAwait(false);
            return;
        }

        bool mosaic = Graticula.Raster.Tiff.VrtMosaicReader.IsMosaic(coverage.Path);
        IReadOnlyList<string> parts = mosaic ? Graticula.Raster.Tiff.VrtMosaicReader.FilesOf(coverage.Path) : [coverage.Path];
        string safe = string.Concat(coverage.ServiceName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

        await Results.Ok(new
        {
            rasterFiles = new[]
            {
                new
                {
                    id = mosaic ? $"{safe}.zip" : $"{safe}.tif",
                    size = parts.Where(File.Exists).Sum(f => new FileInfo(f).Length),
                    rasterIds = OneRaster,
                },
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The functions the service applies — ADR-136 — as the root names them. The JS SDK's <c>ImageryLayer</c> asks for
    /// them here whenever <c>allowRasterFunction</c> is true, and a refusal stopped it loading at all.
    /// </summary>
    private static async Task RasterFunctionInfosAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is null)
        {
            return;
        }

        await Results.Ok(new { rasterFunctionInfos = FunctionInfos }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Each band's minimum, maximum, mean and standard deviation — ADR-125 — which ArcGIS Pro's stretch reads. Sampled
    /// from the coarsest resolution the file holds, the same sample the default stretch is worked out from.
    /// </summary>
    private static async Task StatisticsOperationAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        BandStatistics[] measured = await MeasureAsync(coverage, readers, cancellation).ConfigureAwait(false);

        await Results.Ok(new
        {
            statistics = measured.Select(b => new
            {
                min = b.Minimum,
                max = b.Maximum,
                mean = b.Mean,
                standardDeviation = b.StandardDeviation,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Each band's histogram — ADR-128 — which ArcGIS Pro's stretch dialog draws and its percent-clip and standard
    /// deviation stretches read. 256 bins over the band's sampled range (an 8-bit band's bins are its 256 values), from
    /// the sample the statistics come from, no-data left out.
    /// </summary>
    private static async Task HistogramsAsync(
        HttpContext context,
        string serviceName,
        ICoverageCatalog coverages,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        BandStatistics[] measured = await MeasureAsync(coverage, readers, cancellation).ConfigureAwait(false);
        CoverageWindow? sample = await SampleAsync(coverage, readers, cancellation).ConfigureAwait(false);
        const int Size = 256;
        List<object> histograms = [];

        for (int band = 0; band < measured.Length; band++)
        {
            bool bytes = coverage.Info.Bands[band].Kind == SampleKind.Unsigned8;
            double low = bytes ? -0.5 : measured[band].Minimum;
            double high = bytes ? 255.5 : measured[band].Maximum;
            long[] counts = new long[Size];
            double? noData = coverage.Info.Bands[band].NoData;

            if (sample is not null && high > low)
            {
                for (int i = band; i < sample.Samples.Length; i += sample.Bands)
                {
                    double value = sample.Samples[i];

                    if (double.IsNaN(value) || (noData is { } absent && value == absent))
                    {
                        continue;
                    }

                    counts[Math.Clamp((int)((value - low) / (high - low) * Size), 0, Size - 1)]++;
                }
            }

            histograms.Add(new { size = Size, min = low, max = high, counts });
        }

        await Results.Ok(new { histograms }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The default stretch for what a coverage's bands hold.</summary>
    private static CoverageStyle DefaultFrom(BandStatistics[] bands)
    {
        if (bands.Length == 0 || bands.All(b => b.Maximum <= b.Minimum))
        {
            return CoverageStyle.Default;
        }

        if (bands.Length < 3)
        {
            return new CoverageStyle(StretchKind.Fixed, bands[0].Minimum, bands[0].Maximum);
        }

        BandStatistics[] colour = bands[..3];
        double low = Math.Max(colour.Min(b => b.Minimum), colour.Min(b => b.Mean - (2 * b.StandardDeviation)));
        double high = Math.Min(colour.Max(b => b.Maximum), colour.Max(b => b.Mean + (2 * b.StandardDeviation)));

        return high > low
            ? new CoverageStyle(StretchKind.Fixed, low, high)
            : new CoverageStyle(StretchKind.Fixed, colour.Min(b => b.Minimum), colour.Max(b => b.Maximum));
    }

    /// <summary>A coverage's sampled band statistics, for the Display settings — ADR-123.</summary>
    internal static async Task<IReadOnlyList<(double Minimum, double Maximum, double Mean, double StandardDeviation)>> StatisticsAsync(
        PublishedCoverage coverage, ICoverageReaderFactory readers, CancellationToken cancellation) =>
        [.. (await MeasureAsync(coverage, readers, cancellation).ConfigureAwait(false))
            .Select(b => (b.Minimum, b.Maximum, b.Mean, b.StandardDeviation))];

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
    /// <summary>
    /// The sample a coverage is described from: its coarsest resolution, at most 512 pixels a side — shared by the
    /// statistics and the histograms (ADR-128), so the two describe the same pixels.
    /// </summary>
    private static async Task<CoverageWindow> ReadSampleAsync(
        PublishedCoverage coverage, ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;
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

        return await reader.ReadAsync(level, 0, 0, width, height, cancellation).ConfigureAwait(false);
    }

    /// <summary>The sample, or null when the file cannot be read now.</summary>
    /// <summary>
    /// A whole-image sample through a function, for the statistics a stretch over it needs — ADR-151. A surface function
    /// takes the sample's own cell size, which is coarse, as the statistics are.
    /// </summary>
    internal static CoverageWindow ThroughSample(CoverageInfo info, RasterFunction function, CoverageWindow sample)
    {
        double perX = info.Extent.Width / Math.Max(1, sample.Width);
        double perY = info.Extent.Height / Math.Max(1, sample.Height);
        (double metresX, double metresY) = RasterFunction.Metres(perX, perY, AxisOrder.IsGeographic(info.Srid),
            (info.Extent.MinY + info.Extent.MaxY) / 2);
        return function.Apply(sample, metresX, metresY, info.Bands, (info.Extent.MinX, info.Extent.MaxY, perX, perY));
    }

    /// <summary>A coverage's coarsest sample, or null where it cannot be read — for ADR-154's list of values.</summary>
    internal static Task<CoverageWindow?> SampleOfAsync(PublishedCoverage coverage, ICoverageReaderFactory readers, CancellationToken cancellation) =>
        SampleAsync(coverage, readers, cancellation);

    private static async Task<CoverageWindow?> SampleAsync(
        PublishedCoverage coverage, ICoverageReaderFactory readers, CancellationToken cancellation)
    {
        try
        {
            return await ReadSampleAsync(coverage, readers, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<BandStatistics[]> MeasureAsync(
        PublishedCoverage coverage,
        ICoverageReaderFactory readers,
        CancellationToken cancellation)
    {
        CoverageInfo info = coverage.Info;

        BandStatistics[] statistics = new BandStatistics[info.Bands.Count];

        try
        {
            CoverageWindow window = await ReadSampleAsync(coverage, readers, cancellation).ConfigureAwait(false);

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

                    // A float image's absent pixels are often NaN, which no comparison with a declared value catches
                    // and which turns every statistic it touches into NaN (ADR-128).
                    if (double.IsNaN(value) || (noData is { } absent && value == absent))
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
