using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// WCS 2.0.1 — ADR-170: image services as coverages, their values as GeoTIFF, at <c>/wcs</c> and at each service's own
/// <c>…/ImageServer/WCSServer</c>, as ArcGIS gives them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The values, not a picture.</b> <c>GetCoverage</c> is the image service's raw export (ADR-127): the same read, the
/// same warp into another reference, the same GeoTIFF a client gets from <c>exportImage</c> with <c>format=tiff</c>.
/// </para>
/// <para>
/// <b>Core, KVP GET, GeoTIFF, scaling and CRS</b> — trimming and slicing by <c>subset</c> on the two axes,
/// <c>scalesize</c> and <c>scalefactor</c>, <c>subsettingcrs</c> and <c>outputcrs</c>. A coverage larger than the
/// server's image ceiling is answered only scaled, and the refusal says by how much.
/// </para>
/// </remarks>
internal static partial class ImageServerEndpoints
{
    private const string Wcs = "http://www.opengis.net/wcs/2.0";
    private const string Ows2 = "http://www.opengis.net/ows/2.0";
    private const string Gml32 = "http://www.opengis.net/gml/3.2";
    private const string GmlCov = "http://www.opengis.net/gmlcov/1.0";
    private const string Swe = "http://www.opengis.net/swe/2.0";
    private const string Xlink = "http://www.w3.org/1999/xlink";
    private const string WcsVersion = "2.0.1";
    private const string WcsScopeKey = "wcs.scope";

    private static readonly string[] WcsProfiles =
    [
        "http://www.opengis.net/spec/WCS/2.0/conf/core",
        "http://www.opengis.net/spec/WCS_protocol-binding_get-kvp/1.0/conf/get-kvp",
        "http://www.opengis.net/spec/GMLCOV_geotiff-coverages/1.0/conf/geotiff-coverage",
        "http://www.opengis.net/spec/WCS_service-extension_scaling/1.0/conf/scaling",
        "http://www.opengis.net/spec/WCS_service-extension_crs/1.0/conf/crs",
    ];

    /// <summary>Maps <c>/wcs</c> and each image service's <c>WCSServer</c>.</summary>
    internal static void MapWcs(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/wcs", WcsAsync).Governed(SharingGovernedExtensions.ByFiltering);

        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            app.MapGet($"{prefix}/{{serviceName}}/ImageServer/WCSServer", (HttpContext context, string serviceName,
                    ICoverageCatalog coverages, ICoverageReaderFactory readers, IProjector projector, HostSettings settings,
                    CancellationToken cancellation) =>
                {
                    (string? folder, string name) = Split(context, serviceName);
                    context.Items[WcsScopeKey] = (folder, name);
                    return WcsAsync(context, coverages, readers, projector, settings, cancellation);
                })
                .Governed(SharingGovernedExtensions.ByFiltering);
        }
    }

    /// <summary>A coverage's id: its service's qualified name, an XML name — <c>hosted__elevation</c>.</summary>
    internal static string CoverageIdOf(PublishedCoverage coverage) => coverage.QualifiedName.Replace("/", "__", StringComparison.Ordinal);

    private static async Task WcsAsync(
        HttpContext context, ICoverageCatalog coverages, ICoverageReaderFactory readers, IProjector projector,
        HostSettings settings, CancellationToken cancellation)
    {
        string? Q(string name) => context.Request.Query.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) is
            { Key: not null } pair && pair.Value.ToString() is { Length: > 0 } v ? v : null;

        if (!string.Equals(Q("service"), "WCS", StringComparison.OrdinalIgnoreCase))
        {
            await WcsRefuseAsync(context, 400, Q("service") is null ? "MissingParameterValue" : "InvalidParameterValue", "service",
                "This endpoint serves WCS; say service=WCS.").ConfigureAwait(false);
            return;
        }

        string request = Q("request") ?? string.Empty;

        if (!request.Equals("GetCapabilities", StringComparison.OrdinalIgnoreCase) && Q("version") is { } version
            && version is not ("2.0.1" or "2.0.0"))
        {
            await WcsRefuseAsync(context, 400, "VersionNegotiationFailed", "version",
                $"This server speaks WCS 2.0.1, and the request asks for '{version}'.").ConfigureAwait(false);
            return;
        }

        List<PublishedCoverage> visible = await WcsVisibleAsync(context, coverages, cancellation).ConfigureAwait(false);

        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                await WcsCapabilitiesAsync(context, visible, projector, cancellation).ConfigureAwait(false);
                return;
            case "DESCRIBECOVERAGE":
                await DescribeCoverageAsync(context, visible, Q("coverageId"), cancellation).ConfigureAwait(false);
                return;
            case "GETCOVERAGE":
                await GetCoverageAsync(context, visible, readers, projector, settings, Q, cancellation).ConfigureAwait(false);
                return;
            default:
                await WcsRefuseAsync(context, 400, request.Length == 0 ? "MissingParameterValue" : "OperationNotSupported", "request",
                    $"'{request}' is not a WCS operation this server answers: GetCapabilities, DescribeCoverage, GetCoverage.")
                    .ConfigureAwait(false);
                return;
        }
    }

    /// <summary>The image services this caller may see, started, offering WCS (ADR-166), within the request's service.</summary>
    private static async Task<List<PublishedCoverage>> WcsVisibleAsync(HttpContext context, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);
        (string? Folder, string Name)? scope = context.Items.TryGetValue(WcsScopeKey, out object? held) && held is ValueTuple<string?, string> s
            ? s : null;

        return [.. (await coverages.ListAsync(cancellation).ConfigureAwait(false))
            .Where(c => seesStopped || c.Status == ServiceStatus.Started)
            .Where(c => c.OffersOgc("WCS"))
            .Where(c => scope is not { } only || (string.Equals(c.Name, only.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Folder ?? string.Empty, only.Folder ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
            .Where(c => LayerAccess.Evaluate(c.Sharing, c.Owner, current.Principal, current.Authorization, c.SharedWith).IsAllowed())
            .OrderBy(c => c.QualifiedName, StringComparer.OrdinalIgnoreCase)];
    }

    private static string WcsEndpoint(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}";

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The axis labels of a reference, in its own order: <c>Lat Long</c> for a latitude-first one, <c>E N</c> otherwise.</summary>
    private static (string First, string Second, bool LatitudeFirst) AxesOf(int srid) =>
        Graticula.Geometries.AxisOrder.IsLatitudeFirst(srid)
            ? (Graticula.Geometries.AxisOrder.IsGeographic(srid) ? "Lat" : "N", Graticula.Geometries.AxisOrder.IsGeographic(srid) ? "Long" : "E", true)
            : (Graticula.Geometries.AxisOrder.IsGeographic(srid) ? "Long" : "E", Graticula.Geometries.AxisOrder.IsGeographic(srid) ? "Lat" : "N", false);

    private static string CrsUri(int srid) => $"http://www.opengis.net/def/crs/EPSG/0/{srid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Two numbers in the reference's own axis order.</summary>
    private static string Pair(double x, double y, bool latitudeFirst) => latitudeFirst ? $"{N(y)} {N(x)}" : $"{N(x)} {N(y)}";

    private static async Task WcsCapabilitiesAsync(
        HttpContext context, List<PublishedCoverage> visible, IProjector projector, CancellationToken cancellation)
    {
        IReadOnlyList<Envelope?> wgs84 = await GeographicExtents
            .InWgs84Async(projector, [.. visible.Select(c => (c.Info.Srid, (Envelope?)c.Info.Extent))], cancellation).ConfigureAwait(false);
        (string? folder, string name)? scope = context.Items.TryGetValue(WcsScopeKey, out object? held) && held is ValueTuple<string?, string> s ? s : null;
        PublishedCoverage? own = scope is not null ? visible.FirstOrDefault() : null;
        string endpoint = WcsEndpoint(context);

        await WcsXmlAsync(context, async w =>
        {
            await w.WriteStartElementAsync("wcs", "Capabilities", Wcs).ConfigureAwait(false);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, Ows2).ConfigureAwait(false);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, Xlink).ConfigureAwait(false);
            await w.WriteAttributeStringAsync(null, "version", null, WcsVersion).ConfigureAwait(false);

            await w.WriteStartElementAsync("ows", "ServiceIdentification", Ows2).ConfigureAwait(false);
            await w.WriteElementStringAsync("ows", "Title", Ows2, own?.QualifiedName ?? "Graticula").ConfigureAwait(false);
            await w.WriteElementStringAsync("ows", "Abstract", Ows2, own?.Description is { Length: > 0 } d ? d
                : "The image services this server publishes, as coverages: their values as GeoTIFF.").ConfigureAwait(false);

            if (own?.Tags is { Count: > 0 } tags)
            {
                await w.WriteStartElementAsync("ows", "Keywords", Ows2).ConfigureAwait(false);

                foreach (string tag in tags)
                {
                    await w.WriteElementStringAsync("ows", "Keyword", Ows2, tag).ConfigureAwait(false);
                }

                await w.WriteEndElementAsync().ConfigureAwait(false);
            }

            await w.WriteElementStringAsync("ows", "ServiceType", Ows2, "OGC WCS").ConfigureAwait(false);
            await w.WriteElementStringAsync("ows", "ServiceTypeVersion", Ows2, WcsVersion).ConfigureAwait(false);

            foreach (string profile in WcsProfiles)
            {
                await w.WriteElementStringAsync("ows", "Profile", Ows2, profile).ConfigureAwait(false);
            }

            await w.WriteElementStringAsync("ows", "Fees", Ows2, own?.OgcFees is { Length: > 0 } fees ? fees : "NONE").ConfigureAwait(false);
            await w.WriteElementStringAsync("ows", "AccessConstraints", Ows2,
                own?.OgcAccessConstraints is { Length: > 0 } constraints ? constraints : "NONE").ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);

            await w.WriteStartElementAsync("ows", "ServiceProvider", Ows2).ConfigureAwait(false);
            await w.WriteElementStringAsync("ows", "ProviderName", Ows2, "Graticula").ConfigureAwait(false);
            await w.WriteStartElementAsync("ows", "ServiceContact", Ows2).ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);

            await w.WriteStartElementAsync("ows", "OperationsMetadata", Ows2).ConfigureAwait(false);

            foreach (string operation in (string[])["GetCapabilities", "DescribeCoverage", "GetCoverage"])
            {
                await w.WriteStartElementAsync("ows", "Operation", Ows2).ConfigureAwait(false);
                await w.WriteAttributeStringAsync(null, "name", null, operation).ConfigureAwait(false);
                await w.WriteStartElementAsync("ows", "DCP", Ows2).ConfigureAwait(false);
                await w.WriteStartElementAsync("ows", "HTTP", Ows2).ConfigureAwait(false);
                await w.WriteStartElementAsync("ows", "Get", Ows2).ConfigureAwait(false);
                await w.WriteAttributeStringAsync("xlink", "href", Xlink, endpoint + "?").ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
            }

            await w.WriteEndElementAsync().ConfigureAwait(false);

            await w.WriteStartElementAsync("wcs", "ServiceMetadata", Wcs).ConfigureAwait(false);
            await w.WriteElementStringAsync("wcs", "formatSupported", Wcs, "image/tiff").ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);

            await w.WriteStartElementAsync("wcs", "Contents", Wcs).ConfigureAwait(false);

            for (int i = 0; i < visible.Count; i++)
            {
                await w.WriteStartElementAsync("wcs", "CoverageSummary", Wcs).ConfigureAwait(false);
                await w.WriteElementStringAsync("ows", "Title", Ows2, visible[i].QualifiedName).ConfigureAwait(false);

                if (wgs84[i] is { IsEmpty: false } box)
                {
                    await w.WriteStartElementAsync("ows", "WGS84BoundingBox", Ows2).ConfigureAwait(false);
                    await w.WriteElementStringAsync("ows", "LowerCorner", Ows2, $"{N(box.MinX)} {N(box.MinY)}").ConfigureAwait(false);
                    await w.WriteElementStringAsync("ows", "UpperCorner", Ows2, $"{N(box.MaxX)} {N(box.MaxY)}").ConfigureAwait(false);
                    await w.WriteEndElementAsync().ConfigureAwait(false);
                }

                await w.WriteElementStringAsync("wcs", "CoverageId", Wcs, CoverageIdOf(visible[i])).ConfigureAwait(false);
                await w.WriteElementStringAsync("wcs", "CoverageSubtype", Wcs, "RectifiedGridCoverage").ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
            }

            await w.WriteEndElementAsync().ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);
    }

    private static async Task DescribeCoverageAsync(
        HttpContext context, List<PublishedCoverage> visible, string? ids, CancellationToken cancellation)
    {
        if (ids is null)
        {
            await WcsRefuseAsync(context, 400, "MissingParameterValue", "coverageId", "DescribeCoverage names one or more coverageId.")
                .ConfigureAwait(false);
            return;
        }

        List<PublishedCoverage> described = [];

        foreach (string id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (visible.FirstOrDefault(c => CoverageIdOf(c) == id) is not { } found)
            {
                await WcsRefuseAsync(context, 404, "NoSuchCoverage", "coverageId", $"'{id}' is not a coverage this server offers you.")
                    .ConfigureAwait(false);
                return;
            }

            described.Add(found);
        }

        await WcsXmlAsync(context, async w =>
        {
            await w.WriteStartElementAsync("wcs", "CoverageDescriptions", Wcs).ConfigureAwait(false);
            await w.WriteAttributeStringAsync("xmlns", "gml", null, Gml32).ConfigureAwait(false);
            await w.WriteAttributeStringAsync("xmlns", "gmlcov", null, GmlCov).ConfigureAwait(false);
            await w.WriteAttributeStringAsync("xmlns", "swe", null, Swe).ConfigureAwait(false);

            foreach (PublishedCoverage coverage in described)
            {
                await DescriptionAsync(w, coverage).ConfigureAwait(false);
            }

            await w.WriteEndElementAsync().ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);
    }

    /// <summary>One <c>wcs:CoverageDescription</c>: its envelope, its grid, its bands.</summary>
    private static async Task DescriptionAsync(XmlWriter w, PublishedCoverage coverage)
    {
        CoverageInfo info = coverage.Info;
        string id = CoverageIdOf(coverage);
        (string first, string second, bool latitudeFirst) = AxesOf(info.Srid);
        string crs = CrsUri(info.Srid);

        await w.WriteStartElementAsync("wcs", "CoverageDescription", Wcs).ConfigureAwait(false);
        await w.WriteAttributeStringAsync("gml", "id", Gml32, id).ConfigureAwait(false);

        await w.WriteStartElementAsync("gml", "boundedBy", Gml32).ConfigureAwait(false);
        await w.WriteStartElementAsync("gml", "Envelope", Gml32).ConfigureAwait(false);
        await w.WriteAttributeStringAsync(null, "srsName", null, crs).ConfigureAwait(false);
        await w.WriteAttributeStringAsync(null, "axisLabels", null, $"{first} {second}").ConfigureAwait(false);
        await w.WriteAttributeStringAsync(null, "uomLabels", null, Graticula.Geometries.AxisOrder.IsGeographic(info.Srid) ? "deg deg" : "m m").ConfigureAwait(false);
        await w.WriteAttributeStringAsync(null, "srsDimension", null, "2").ConfigureAwait(false);
        await w.WriteElementStringAsync("gml", "lowerCorner", Gml32, Pair(info.Extent.MinX, info.Extent.MinY, latitudeFirst)).ConfigureAwait(false);
        await w.WriteElementStringAsync("gml", "upperCorner", Gml32, Pair(info.Extent.MaxX, info.Extent.MaxY, latitudeFirst)).ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);

        await w.WriteElementStringAsync("wcs", "CoverageId", Wcs, id).ConfigureAwait(false);

        // The grid: column i east, row j south, from the centre of the top-left cell.
        await w.WriteStartElementAsync("gml", "domainSet", Gml32).ConfigureAwait(false);
        await w.WriteStartElementAsync("gml", "RectifiedGrid", Gml32).ConfigureAwait(false);
        await w.WriteAttributeStringAsync("gml", "id", Gml32, id + "-grid").ConfigureAwait(false);
        await w.WriteAttributeStringAsync(null, "dimension", null, "2").ConfigureAwait(false);
        await w.WriteStartElementAsync("gml", "limits", Gml32).ConfigureAwait(false);
        await w.WriteStartElementAsync("gml", "GridEnvelope", Gml32).ConfigureAwait(false);
        await w.WriteElementStringAsync("gml", "low", Gml32, "0 0").ConfigureAwait(false);
        await w.WriteElementStringAsync("gml", "high", Gml32,
            $"{(info.Width - 1).ToString(CultureInfo.InvariantCulture)} {(info.Height - 1).ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);
        await w.WriteElementStringAsync("gml", "axisLabels", Gml32, "i j").ConfigureAwait(false);
        await w.WriteStartElementAsync("gml", "origin", Gml32).ConfigureAwait(false);
        await w.WriteStartElementAsync("gml", "Point", Gml32).ConfigureAwait(false);
        await w.WriteAttributeStringAsync("gml", "id", Gml32, id + "-origin").ConfigureAwait(false);
        await w.WriteAttributeStringAsync(null, "srsName", null, crs).ConfigureAwait(false);
        await w.WriteElementStringAsync("gml", "pos", Gml32,
            Pair(info.Extent.MinX + (info.PixelWidth / 2), info.Extent.MaxY - (info.PixelHeight / 2), latitudeFirst)).ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);

        foreach ((double dx, double dy) in ((double, double)[])[(info.PixelWidth, 0), (0, -info.PixelHeight)])
        {
            await w.WriteStartElementAsync("gml", "offsetVector", Gml32).ConfigureAwait(false);
            await w.WriteAttributeStringAsync(null, "srsName", null, crs).ConfigureAwait(false);
            await w.WriteStringAsync(Pair(dx, dy, latitudeFirst)).ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);
        }

        await w.WriteEndElementAsync().ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);

        // The bands, each a quantity, with the value that means none.
        await w.WriteStartElementAsync("gmlcov", "rangeType", GmlCov).ConfigureAwait(false);
        await w.WriteStartElementAsync("swe", "DataRecord", Swe).ConfigureAwait(false);

        foreach (BandInfo band in info.Bands)
        {
            await w.WriteStartElementAsync("swe", "field", Swe).ConfigureAwait(false);
            await w.WriteAttributeStringAsync(null, "name", null, $"band_{(band.Index + 1).ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            await w.WriteStartElementAsync("swe", "Quantity", Swe).ConfigureAwait(false);
            await w.WriteElementStringAsync("swe", "description", Swe, band.Kind.ToString()).ConfigureAwait(false);

            if (band.NoData is { } none)
            {
                await w.WriteStartElementAsync("swe", "nilValues", Swe).ConfigureAwait(false);
                await w.WriteStartElementAsync("swe", "NilValues", Swe).ConfigureAwait(false);
                await w.WriteStartElementAsync("swe", "nilValue", Swe).ConfigureAwait(false);
                await w.WriteAttributeStringAsync(null, "reason", null, "http://www.opengis.net/def/nil/OGC/0/unknown").ConfigureAwait(false);
                await w.WriteStringAsync(N(none)).ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
                await w.WriteEndElementAsync().ConfigureAwait(false);
            }

            await w.WriteStartElementAsync("swe", "uom", Swe).ConfigureAwait(false);
            await w.WriteAttributeStringAsync(null, "code", null, "1").ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);
            await w.WriteEndElementAsync().ConfigureAwait(false);
        }

        await w.WriteEndElementAsync().ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);

        await w.WriteStartElementAsync("wcs", "ServiceParameters", Wcs).ConfigureAwait(false);
        await w.WriteElementStringAsync("wcs", "CoverageSubtype", Wcs, "RectifiedGridCoverage").ConfigureAwait(false);
        await w.WriteElementStringAsync("wcs", "nativeFormat", Wcs, "image/tiff").ConfigureAwait(false);
        await w.WriteEndElementAsync().ConfigureAwait(false);

        await w.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// GetCoverage: the values of the coverage, or of the part its <c>subset</c>s trim, at its own resolution or the one
    /// a scaling asks for, in its reference or <c>outputcrs</c>, as GeoTIFF.
    /// </summary>
    /// <summary>
    /// The status a refused subset is answered with: 404, as WCS 2.0.1 Core's exception table gives for both
    /// <c>InvalidAxisLabel</c> and <c>InvalidSubsetting</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not 400, which is what a reader would guess and what this answered until 2026-10-04</b>, when OGC's WCS 2.0
    /// suite was first run here and its four exception tests each failed on the status alone, with the right code in
    /// the body.
    /// </remarks>
    private const int SubsetRefused = 404;

    private static async Task GetCoverageAsync(
        HttpContext context, List<PublishedCoverage> visible, ICoverageReaderFactory readers, IProjector projector,
        HostSettings settings, Func<string, string?> q, CancellationToken cancellation)
    {
        if (q("coverageId") is not { } id)
        {
            await WcsRefuseAsync(context, 400, "MissingParameterValue", "coverageId", "GetCoverage names a coverageId.").ConfigureAwait(false);
            return;
        }

        if (visible.FirstOrDefault(c => CoverageIdOf(c) == id) is not { } coverage)
        {
            await WcsRefuseAsync(context, 404, "NoSuchCoverage", "coverageId", $"'{id}' is not a coverage this server offers you.").ConfigureAwait(false);
            return;
        }

        if (q("format") is { } format && format is not ("image/tiff" or "image/geotiff" or "image/tiff;application=geotiff"))
        {
            await WcsRefuseAsync(context, 400, "InvalidParameterValue", "format", $"'{format}' is not a format this server writes: image/tiff.")
                .ConfigureAwait(false);
            return;
        }

        // <b>`mediaType` has one legal value</b> (WCS 2.0.1 Core requirement 29): `multipart/related`. Anything else was
        // ignored and the coverage answered anyway until CITE asked with `mediatype_bogus` on 2026-10-04.
        if (q("mediaType") is { } mediaType && !string.Equals(mediaType, "multipart/related", StringComparison.OrdinalIgnoreCase))
        {
            await WcsRefuseAsync(context, 400, "InvalidParameterValue", "mediaType",
                $"'{mediaType}' is not a mediaType; the only one WCS 2.0.1 defines is multipart/related.").ConfigureAwait(false);
            return;
        }

        CoverageInfo info = coverage.Info;

        // The references the subsets are in and the answer is written in: the coverage's own unless named.
        int subsetSrid = info.Srid, outputSrid = info.Srid;

        foreach ((string name, Action<int> set) in new (string, Action<int>)[] { ("subsettingcrs", v => subsetSrid = v), ("outputcrs", v => outputSrid = v) })
        {
            if (q(name) is { } named)
            {
                if (!int.TryParse(named[(named.LastIndexOfAny(['/', ':']) + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                    || !await projector.KnowsAsync(code, cancellation).ConfigureAwait(false))
                {
                    await WcsRefuseAsync(context, 400, name == "outputcrs" ? "OutputCrs-NotSupported" : "SubsettingCrs-NotSupported", name,
                        $"'{named}' is not a reference this server knows. Name one as http://www.opengis.net/def/crs/EPSG/0/<code>.").ConfigureAwait(false);
                    return;
                }

                set(code);
            }
        }

        // The extent in the subsetting reference: the coverage's own, trimmed by each subset on the axis it names.
        Envelope extent = subsetSrid == info.Srid
            ? info.Extent
            : (await ReprojectAsync(projector, info.Srid, info.Extent, subsetSrid, cancellation).ConfigureAwait(false)) ?? info.Extent;
        double minX = extent.MinX, maxX = extent.MaxX, minY = extent.MinY, maxY = extent.MaxY;

        // Requirement 31: at most one subset per axis. A second one was quietly intersected with the first.
        bool subsetVertical = false, subsetHorizontal = false;

        foreach (string subset in context.Request.Query.Where(p => string.Equals(p.Key, "subset", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(p => p.Value.ToArray()).OfType<string>())
        {
            int open = subset.IndexOf('(', StringComparison.Ordinal);
            int close = subset.LastIndexOf(')');

            if (open <= 0 || close < open)
            {
                await WcsRefuseAsync(context, SubsetRefused, "InvalidSubsetting", "subset", $"'{subset}' is not axis(low,high) or axis(point).").ConfigureAwait(false);
                return;
            }

            string axis = subset[..open].Trim().ToUpperInvariant();
            string[] bounds = subset[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            bool vertical = axis is "LAT" or "LATITUDE" or "N" or "Y" or "NORTHING";
            bool horizontal = axis is "LONG" or "LON" or "LONGITUDE" or "E" or "X" or "EASTING";

            if (!vertical && !horizontal)
            {
                await WcsRefuseAsync(context, SubsetRefused, "InvalidAxisLabel", "subset",
                    $"'{subset[..open].Trim()}' is not an axis of this coverage; they are {AxesOf(subsetSrid).First} and {AxesOf(subsetSrid).Second}.").ConfigureAwait(false);
                return;
            }

            if (vertical ? subsetVertical : subsetHorizontal)
            {
                await WcsRefuseAsync(context, SubsetRefused, "InvalidAxisLabel", "subset",
                    $"'{subset[..open].Trim()}' is subset twice; a GetCoverage takes at most one subset per axis.").ConfigureAwait(false);
                return;
            }

            (subsetVertical, subsetHorizontal) = (subsetVertical || vertical, subsetHorizontal || horizontal);

            double[] values = new double[bounds.Length];

            for (int b = 0; b < bounds.Length; b++)
            {
                if (bounds[b] == "*")
                {
                    values[b] = double.NaN;
                }
                else if (!double.TryParse(bounds[b], NumberStyles.Float, CultureInfo.InvariantCulture, out values[b]))
                {
                    await WcsRefuseAsync(context, SubsetRefused, "InvalidSubsetting", "subset", $"'{bounds[b]}' is not a number.").ConfigureAwait(false);
                    return;
                }
            }

            // A slice — one value — is the cell it falls in.
            (double low, double high) = values.Length == 1
                ? (values[0] - ((vertical ? info.PixelHeight : info.PixelWidth) / 2), values[0] + ((vertical ? info.PixelHeight : info.PixelWidth) / 2))
                : (double.IsNaN(values[0]) ? double.MinValue : values[0], values.Length > 1 && !double.IsNaN(values[1]) ? values[1] : double.MaxValue);

            if (low > high)
            {
                await WcsRefuseAsync(context, SubsetRefused, "InvalidSubsetting", "subset", $"'{subset}' has its low above its high.").ConfigureAwait(false);
                return;
            }

            if (vertical)
            {
                (minY, maxY) = (Math.Max(minY, low), Math.Min(maxY, high));
            }
            else
            {
                (minX, maxX) = (Math.Max(minX, low), Math.Min(maxX, high));
            }
        }

        if (minX >= maxX || minY >= maxY)
        {
            await WcsRefuseAsync(context, SubsetRefused, "InvalidSubsetting", "subset", "The subsets leave nothing of the coverage.").ConfigureAwait(false);
            return;
        }

        // The size: the coverage's own cells over the extent, unless a scaling names one.
        double cellX = (extent.MaxX - extent.MinX) / info.Width, cellY = (extent.MaxY - extent.MinY) / info.Height;
        int width = Math.Max(1, (int)Math.Round((maxX - minX) / cellX)), height = Math.Max(1, (int)Math.Round((maxY - minY) / cellY));

        if (q("scalefactor") is { } factorText)
        {
            if (!double.TryParse(factorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) || factor <= 0)
            {
                await WcsRefuseAsync(context, 400, "InvalidScaleFactor", "scalefactor", "scalefactor is a number above zero.").ConfigureAwait(false);
                return;
            }

            (width, height) = (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)));
        }

        if (q("scalesize") is { } sizes)
        {
            foreach (string part in sizes.Split(',', StringSplitOptions.TrimEntries))
            {
                int open = part.IndexOf('(', StringComparison.Ordinal);
                string axis = open > 0 ? part[..open].Trim().ToUpperInvariant() : string.Empty;

                if (open <= 0 || !int.TryParse(part[(open + 1)..].TrimEnd(')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 1)
                {
                    await WcsRefuseAsync(context, 400, "InvalidExtent", "scalesize", $"'{part}' is not axis(cells).").ConfigureAwait(false);
                    return;
                }

                if (axis is "I" or "LONG" or "LON" or "E" or "X")
                {
                    width = size;
                }
                else
                {
                    height = size;
                }
            }
        }

        if (width > settings.MaximumImageWidth || height > settings.MaximumImageHeight)
        {
            await WcsRefuseAsync(context, 400, "InvalidExtent", "scalesize",
                $"That is {width} × {height} cells, and this server answers at most {settings.MaximumImageWidth} × "
                + $"{settings.MaximumImageHeight}. Trim it with subset, or ask for fewer cells with scalesize or scalefactor.")
                .ConfigureAwait(false);
            return;
        }

        Envelope asked = new(minX, minY, maxX, maxY);

        // The answer's extent in its own reference, when it is not the one the subsets are in.
        if (outputSrid != subsetSrid)
        {
            asked = await ReprojectAsync(projector, subsetSrid, asked, outputSrid, cancellation).ConfigureAwait(false) ?? asked;
        }

        (byte[]? file, string? refused) = await RawAsync(coverage,
                ImageServerExportParameters.ForValues(asked, width, height, outputSrid), RasterFunction.None, readers, projector,
                Resampling.Nearest, cancellation)
            .ConfigureAwait(false);

        if (file is null)
        {
            await WcsRefuseAsync(context, 400, "InvalidParameterValue", "coverageId", refused ?? "The coverage could not be read.").ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "image/tiff";
        context.Response.Headers.ContentDisposition = $"inline; filename=\"{id}.tif\"";
        await context.Response.Body.WriteAsync(file, cancellation).ConfigureAwait(false);
    }

    /// <summary>An envelope moved into another reference by its four corners, or null when it cannot be.</summary>
    private static async Task<Envelope?> ReprojectAsync(IProjector projector, int from, Envelope box, int to, CancellationToken cancellation)
    {
        try
        {
            (IReadOnlyList<Geometry> projected, _) = await projector.ProjectAsync(
                [new Point(box.MinX, box.MinY), new Point(box.MaxX, box.MinY), new Point(box.MaxX, box.MaxY), new Point(box.MinX, box.MaxY)],
                from, to, cancellation).ConfigureAwait(false);
            Envelope whole = Envelope.Empty;

            foreach (Geometry corner in projected)
            {
                whole = whole.IsEmpty ? corner.Envelope : whole.Union(corner.Envelope);
            }

            return whole.IsEmpty ? null : whole;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task WcsXmlAsync(HttpContext context, Func<XmlWriter, Task> write, CancellationToken cancellation)
    {
        using MemoryStream buffer = new();

        using (XmlWriter w = XmlWriter.Create(buffer, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false) }))
        {
            await write(w).ConfigureAwait(false);
            await w.FlushAsync().ConfigureAwait(false);
        }

        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellation).ConfigureAwait(false);
    }

    /// <summary>An OWS 2.0 exception report — WCS 2.0's refusal.</summary>
    private static async Task WcsRefuseAsync(HttpContext context, int status, string code, string locator, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + $"<ows:ExceptionReport xmlns:ows=\"{Ows2}\" version=\"2.0.0\">"
            + $"<ows:Exception exceptionCode=\"{System.Security.SecurityElement.Escape(code)}\" locator=\"{System.Security.SecurityElement.Escape(locator)}\">"
            + $"<ows:ExceptionText>{System.Security.SecurityElement.Escape(text)}</ows:ExceptionText></ows:Exception></ows:ExceptionReport>")
            .ConfigureAwait(false);
    }
}
