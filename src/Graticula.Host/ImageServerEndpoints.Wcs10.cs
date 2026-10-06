using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// WCS 1.0.0 beside 2.0.1 — ADR-173: the same coverages, read by the same raw export, in the vocabulary QGIS's
/// <em>Add WCS layer</em> speaks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> QGIS 3.28's WCS provider asks for DescribeCoverage in 1.0.0 or 1.1 whatever it is told, and
/// could not open an image service here at any version setting — measured 2026-10-04, Q-162. The owner chose 1.0.0
/// on 2026-10-06. ArcGIS Server speaks it too.
/// </para>
/// <para>
/// <b>Only the request and the documents are 1.0's.</b> GetCoverage is the image service's raw export (ADR-127) with a
/// box, a size and a reference read from 1.0's parameters, so a 1.0 client and a 2.0 client get the same values for
/// the same area. 1.0 states every box in x, y order — longitude first in EPSG:4326 — whatever the reference's
/// authority says, which is how its clients write and read them.
/// </para>
/// </remarks>
internal static partial class ImageServerEndpoints
{
    private const string Wcs10 = "http://www.opengis.net/wcs";
    private const string Gml2 = "http://www.opengis.net/gml";
    private const string Ogc = "http://www.opengis.net/ogc";
    private const string Wcs10Version = "1.0.0";
    private const string Crs84Urn = "urn:ogc:def:crs:OGC:1.3:CRS84";

    /// <summary>The references a coverage is offered in: its own, and the two every client knows.</summary>
    private static IEnumerable<int> Wcs10References(PublishedCoverage coverage) =>
        new[] { coverage.Info.Srid, 4326, 3857 }.Distinct();

    /// <summary>Whether a request is for WCS 1.0.0, by the version it names — GetCapabilities negotiating below 2.0.</summary>
    private static bool IsWcs10(string request, string? version) =>
        version is { Length: > 0 } asked
        && (string.Equals(asked, Wcs10Version, StringComparison.Ordinal)
            || (request.Equals("GetCapabilities", StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(asked, out Version? number) && number < new Version(2, 0)));

    private static async Task Wcs10Async(
        HttpContext context, string request, List<PublishedCoverage> visible, ICoverageReaderFactory readers,
        IProjector projector, HostSettings settings, Func<string, string?> q, CancellationToken cancellation)
    {
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                await Wcs10CapabilitiesAsync(context, visible, projector, cancellation).ConfigureAwait(false);
                return;
            case "DESCRIBECOVERAGE":
                await Wcs10DescribeAsync(context, visible, q("coverage"), projector, cancellation).ConfigureAwait(false);
                return;
            case "GETCOVERAGE":
                await Wcs10GetCoverageAsync(context, visible, readers, projector, settings, q, cancellation).ConfigureAwait(false);
                return;
            default:
                await Wcs10RefuseAsync(context, request.Length == 0 ? "MissingParameterValue" : "OperationNotSupported",
                    $"'{request}' is not a WCS 1.0.0 operation this server answers: GetCapabilities, DescribeCoverage, GetCoverage.")
                    .ConfigureAwait(false);
                return;
        }
    }

    private static async Task Wcs10CapabilitiesAsync(
        HttpContext context, List<PublishedCoverage> visible, IProjector projector, CancellationToken cancellation)
    {
        IReadOnlyList<Envelope?> wgs84 = await GeographicExtents
            .InWgs84Async(projector, [.. visible.Select(c => (c.Info.Srid, (Envelope?)c.Info.Extent))], cancellation).ConfigureAwait(false);
        string endpoint = WcsEndpoint(context);

        await WcsXmlAsync(context, async w =>
        {
            await w.WriteStartDocumentAsync().ConfigureAwait(false);
            w.WriteStartElement("WCS_Capabilities", Wcs10);
            w.WriteAttributeString("xmlns", "gml", null, Gml2);
            w.WriteAttributeString("xmlns", "xlink", null, Xlink);
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                Wcs10 + " http://schemas.opengis.net/wcs/1.0.0/wcsCapabilities.xsd");
            w.WriteAttributeString("version", Wcs10Version);

            w.WriteStartElement("Service", Wcs10);
            w.WriteElementString("name", Wcs10, "WCS");
            w.WriteElementString("label", Wcs10, "Graticula");
            w.WriteElementString("fees", Wcs10, "NONE");
            w.WriteElementString("accessConstraints", Wcs10, "NONE");
            w.WriteEndElement();

            w.WriteStartElement("Capability", Wcs10);
            w.WriteStartElement("Request", Wcs10);

            foreach (string operation in (string[])["GetCapabilities", "DescribeCoverage", "GetCoverage"])
            {
                w.WriteStartElement(operation, Wcs10);
                w.WriteStartElement("DCPType", Wcs10);
                w.WriteStartElement("HTTP", Wcs10);
                w.WriteStartElement("Get", Wcs10);
                w.WriteStartElement("OnlineResource", Wcs10);
                w.WriteAttributeString("xlink", "type", Xlink, "simple");
                w.WriteAttributeString("xlink", "href", Xlink, endpoint + "?");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteStartElement("Exception", Wcs10);
            w.WriteElementString("Format", Wcs10, "application/vnd.ogc.se_xml");
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("ContentMetadata", Wcs10);

            for (int i = 0; i < visible.Count; i++)
            {
                w.WriteStartElement("CoverageOfferingBrief", Wcs10);
                WriteWcs10Identity(w, visible[i], wgs84[i]);
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
        }, cancellation).ConfigureAwait(false);
    }

    /// <summary>A coverage's description, name, label and longitude–latitude box — what the brief and the offering share.</summary>
    private static void WriteWcs10Identity(XmlWriter w, PublishedCoverage coverage, Envelope? wgs84)
    {
        if (coverage.Description is { Length: > 0 } description)
        {
            w.WriteElementString("description", Wcs10, description);
        }

        w.WriteElementString("name", Wcs10, CoverageIdOf(coverage));
        w.WriteElementString("label", Wcs10, coverage.QualifiedName);
        w.WriteStartElement("lonLatEnvelope", Wcs10);
        w.WriteAttributeString("srsName", Crs84Urn);
        Envelope box = wgs84 ?? new Envelope(-180, -90, 180, 90);
        w.WriteElementString("gml", "pos", Gml2, $"{N(box.MinX)} {N(box.MinY)}");
        w.WriteElementString("gml", "pos", Gml2, $"{N(box.MaxX)} {N(box.MaxY)}");
        w.WriteEndElement();
    }

    private static async Task Wcs10DescribeAsync(
        HttpContext context, List<PublishedCoverage> visible, string? names, IProjector projector, CancellationToken cancellation)
    {
        // No COVERAGE describes every coverage — 1.0.0 §8.3.2, unlike 2.0, where the id is required.
        List<PublishedCoverage> asked = [];

        foreach (string name in (names ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (visible.FirstOrDefault(c => CoverageIdOf(c) == name) is not { } coverage)
            {
                await Wcs10RefuseAsync(context, "CoverageNotDefined", $"'{name}' is not a coverage this server offers you.", "COVERAGE")
                    .ConfigureAwait(false);
                return;
            }

            asked.Add(coverage);
        }

        if (names is null)
        {
            asked = visible;
        }

        IReadOnlyList<Envelope?> wgs84 = await GeographicExtents
            .InWgs84Async(projector, [.. asked.Select(c => (c.Info.Srid, (Envelope?)c.Info.Extent))], cancellation).ConfigureAwait(false);

        await WcsXmlAsync(context, async w =>
        {
            await w.WriteStartDocumentAsync().ConfigureAwait(false);
            w.WriteStartElement("CoverageDescription", Wcs10);
            w.WriteAttributeString("xmlns", "gml", null, Gml2);
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                Wcs10 + " http://schemas.opengis.net/wcs/1.0.0/describeCoverage.xsd");
            w.WriteAttributeString("version", Wcs10Version);

            for (int i = 0; i < asked.Count; i++)
            {
                PublishedCoverage coverage = asked[i];
                CoverageInfo info = coverage.Info;
                string srs = $"EPSG:{info.Srid.ToString(CultureInfo.InvariantCulture)}";
                double cellX = (info.Extent.MaxX - info.Extent.MinX) / info.Width, cellY = (info.Extent.MaxY - info.Extent.MinY) / info.Height;

                w.WriteStartElement("CoverageOffering", Wcs10);
                WriteWcs10Identity(w, coverage, wgs84[i]);

                w.WriteStartElement("domainSet", Wcs10);
                w.WriteStartElement("spatialDomain", Wcs10);
                w.WriteStartElement("gml", "Envelope", Gml2);
                w.WriteAttributeString("srsName", srs);
                w.WriteElementString("gml", "pos", Gml2, $"{N(info.Extent.MinX)} {N(info.Extent.MinY)}");
                w.WriteElementString("gml", "pos", Gml2, $"{N(info.Extent.MaxX)} {N(info.Extent.MaxY)}");
                w.WriteEndElement();

                // The grid in pixel space, its origin the centre of the top-left cell — 1.0's RectifiedGrid.
                w.WriteStartElement("gml", "RectifiedGrid", Gml2);
                w.WriteAttributeString("dimension", "2");
                w.WriteAttributeString("srsName", srs);
                w.WriteStartElement("gml", "limits", Gml2);
                w.WriteStartElement("gml", "GridEnvelope", Gml2);
                w.WriteElementString("gml", "low", Gml2, "0 0");
                w.WriteElementString("gml", "high", Gml2,
                    $"{(info.Width - 1).ToString(CultureInfo.InvariantCulture)} {(info.Height - 1).ToString(CultureInfo.InvariantCulture)}");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteElementString("gml", "axisName", Gml2, "x");
                w.WriteElementString("gml", "axisName", Gml2, "y");
                w.WriteStartElement("gml", "origin", Gml2);
                w.WriteElementString("gml", "pos", Gml2, $"{N(info.Extent.MinX + (cellX / 2))} {N(info.Extent.MaxY - (cellY / 2))}");
                w.WriteEndElement();
                w.WriteElementString("gml", "offsetVector", Gml2, $"{N(cellX)} 0");
                w.WriteElementString("gml", "offsetVector", Gml2, $"0 {N(-cellY)}");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("rangeSet", Wcs10);
                w.WriteStartElement("RangeSet", Wcs10);
                w.WriteElementString("name", Wcs10, "bands");
                w.WriteElementString("label", Wcs10, $"{info.Bands.Count.ToString(CultureInfo.InvariantCulture)} band(s)");
                w.WriteStartElement("axisDescription", Wcs10);
                w.WriteStartElement("AxisDescription", Wcs10);
                w.WriteElementString("name", Wcs10, "Band");
                w.WriteElementString("label", Wcs10, "Band");
                w.WriteStartElement("values", Wcs10);

                for (int band = 1; band <= info.Bands.Count; band++)
                {
                    w.WriteElementString("singleValue", Wcs10, band.ToString(CultureInfo.InvariantCulture));
                }

                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("supportedCRSs", Wcs10);
                w.WriteElementString("requestResponseCRSs", Wcs10,
                    string.Join(' ', Wcs10References(coverage).Select(s => $"EPSG:{s.ToString(CultureInfo.InvariantCulture)}")));
                w.WriteEndElement();

                w.WriteStartElement("supportedFormats", Wcs10);
                w.WriteAttributeString("nativeFormat", "GeoTIFF");
                w.WriteElementString("formats", Wcs10, "GeoTIFF");
                w.WriteEndElement();

                w.WriteStartElement("supportedInterpolations", Wcs10);
                w.WriteAttributeString("default", "nearest neighbor");
                w.WriteElementString("interpolationMethod", Wcs10, "nearest neighbor");
                w.WriteEndElement();

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }, cancellation).ConfigureAwait(false);
    }

    private static async Task Wcs10GetCoverageAsync(
        HttpContext context, List<PublishedCoverage> visible, ICoverageReaderFactory readers, IProjector projector,
        HostSettings settings, Func<string, string?> q, CancellationToken cancellation)
    {
        foreach (string required in (string[])["COVERAGE", "CRS", "BBOX", "FORMAT"])
        {
            if (q(required) is null)
            {
                await Wcs10RefuseAsync(context, "MissingParameterValue", $"GetCoverage names a {required}.", required).ConfigureAwait(false);
                return;
            }
        }

        if (visible.FirstOrDefault(c => CoverageIdOf(c) == q("COVERAGE")) is not { } coverage)
        {
            await Wcs10RefuseAsync(context, "CoverageNotDefined", $"'{q("COVERAGE")}' is not a coverage this server offers you.", "COVERAGE")
                .ConfigureAwait(false);
            return;
        }

        if (q("FORMAT") is not ("GeoTIFF" or "GTiff" or "image/tiff" or "image/geotiff" or "TIFF"))
        {
            await Wcs10RefuseAsync(context, "InvalidFormat", $"'{q("FORMAT")}' is not a format this server writes: GeoTIFF.", "FORMAT")
                .ConfigureAwait(false);
            return;
        }

        if (q("INTERPOLATION") is { } interpolation && !interpolation.Equals("nearest neighbor", StringComparison.OrdinalIgnoreCase)
            && !interpolation.Equals("nearest", StringComparison.OrdinalIgnoreCase))
        {
            await Wcs10RefuseAsync(context, "InvalidParameterValue",
                $"'{interpolation}' is not an interpolation this server offers: nearest neighbor.", "INTERPOLATION").ConfigureAwait(false);
            return;
        }

        int? Reference(string name) =>
            q(name) is { } text && text.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[5..], NumberStyles.None, CultureInfo.InvariantCulture, out int srid) ? srid : null;

        if (Reference("CRS") is not { } crs || !await projector.KnowsAsync(crs, cancellation).ConfigureAwait(false))
        {
            await Wcs10RefuseAsync(context, "InvalidParameterValue", $"'{q("CRS")}' is not a reference this server knows. Name one as EPSG:<code>.", "CRS")
                .ConfigureAwait(false);
            return;
        }

        int responseCrs = crs;

        if (q("RESPONSE_CRS") is not null)
        {
            if (Reference("RESPONSE_CRS") is not { } named || !await projector.KnowsAsync(named, cancellation).ConfigureAwait(false))
            {
                await Wcs10RefuseAsync(context, "InvalidParameterValue",
                    $"'{q("RESPONSE_CRS")}' is not a reference this server knows. Name one as EPSG:<code>.", "RESPONSE_CRS").ConfigureAwait(false);
                return;
            }

            responseCrs = named;
        }

        double[] box = [.. (q("BBOX") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN)];

        if (box.Length != 4 || box.Any(double.IsNaN) || box[0] >= box[2] || box[1] >= box[3])
        {
            await Wcs10RefuseAsync(context, "InvalidParameterValue", $"'{q("BBOX")}' is not minx,miny,maxx,maxy with each minimum below its maximum.", "BBOX")
                .ConfigureAwait(false);
            return;
        }

        Envelope asked = new(box[0], box[1], box[2], box[3]);

        // The size: WIDTH and HEIGHT, or the resolution in the request's own reference.
        int width, height;

        if (int.TryParse(q("WIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            && int.TryParse(q("HEIGHT"), NumberStyles.None, CultureInfo.InvariantCulture, out int h) && w > 0 && h > 0)
        {
            (width, height) = (w, h);
        }
        else if (double.TryParse(q("RESX"), NumberStyles.Float, CultureInfo.InvariantCulture, out double resX)
            && double.TryParse(q("RESY"), NumberStyles.Float, CultureInfo.InvariantCulture, out double resY) && resX > 0 && resY > 0)
        {
            (width, height) = ((int)Math.Max(1, Math.Round(asked.Width / resX)), (int)Math.Max(1, Math.Round(asked.Height / resY)));
        }
        else
        {
            await Wcs10RefuseAsync(context, "MissingParameterValue", "GetCoverage names WIDTH and HEIGHT, or RESX and RESY.", "WIDTH")
                .ConfigureAwait(false);
            return;
        }

        if (width > settings.MaximumImageWidth || height > settings.MaximumImageHeight)
        {
            await Wcs10RefuseAsync(context, "InvalidParameterValue",
                $"That is {width} × {height} cells, and this server answers at most {settings.MaximumImageWidth} × {settings.MaximumImageHeight}.",
                "WIDTH").ConfigureAwait(false);
            return;
        }

        if (responseCrs != crs)
        {
            asked = await ReprojectAsync(projector, crs, asked, responseCrs, cancellation).ConfigureAwait(false) ?? asked;
        }

        (byte[]? file, string? refused) = await RawAsync(coverage,
                ImageServerExportParameters.ForValues(asked, width, height, responseCrs), RasterFunction.None, readers, projector,
                Resampling.Nearest, cancellation)
            .ConfigureAwait(false);

        if (file is null)
        {
            await Wcs10RefuseAsync(context, "InvalidParameterValue", refused ?? "The coverage could not be read.", "COVERAGE").ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "image/tiff";
        context.Response.Headers.ContentDisposition = $"inline; filename=\"{CoverageIdOf(coverage)}.tif\"";
        await context.Response.Body.WriteAsync(file, cancellation).ConfigureAwait(false);
    }

    /// <summary>A WCS 1.0.0 refusal: an OGC ServiceExceptionReport 1.2.0, as 1.0.0 §7.4 gives it.</summary>
    private static async Task Wcs10RefuseAsync(HttpContext context, string code, string text, string? locator = null)
    {
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/vnd.ogc.se_xml; charset=utf-8";
        string at = locator is { Length: > 0 } ? $" locator=\"{System.Security.SecurityElement.Escape(locator)}\"" : string.Empty;
        await context.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + $"<ServiceExceptionReport version=\"1.2.0\" xmlns=\"{Ogc}\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
            + $"xsi:schemaLocation=\"{Ogc} http://schemas.opengis.net/wcs/1.0.0/OGC-exception.xsd\">"
            + $"<ServiceException code=\"{System.Security.SecurityElement.Escape(code)}\"{at}>{System.Security.SecurityElement.Escape(text)}</ServiceException>"
            + "</ServiceExceptionReport>").ConfigureAwait(false);
    }
}
