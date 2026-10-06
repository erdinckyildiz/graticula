using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Api.Tiles;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// WMTS 1.0.0 for an image service, at ArcGIS's address — <c>…/ImageServer/WMTS</c> — over the tiles its
/// <c>tile</c> operation already serves: ADR-163.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grid is the service's own</b> (<see cref="TilingScheme.For"/>), in its own reference, so a WMTS tile is the
/// same picture as the ArcGIS tile with the same level, row and column, drawn by the same code. Key-value
/// (<c>request=GetTile</c>) and RESTful (<c>…/WMTS/tile/1.0.0/{layer}/{style}/{set}/{level}/{row}/{column}</c>, as
/// ArcGIS writes it) both reach it.
/// </para>
/// <para>
/// <b>Scale denominators are WMTS's</b>: the resolution in metres over 0.28 mm, a degree counted as
/// 111,319.49 m on a geographic grid, which is how OGC's own well-known scale sets are stated.
/// </para>
/// </remarks>
internal static partial class ImageServerEndpoints
{
    private const string WmtsSet = "default028mm";
    private const double MetresPerDegree = 111319.49079327358;

    /// <summary>Maps WMTS under each image service.</summary>
    private static void MapWmts(WebApplication app, string prefix)
    {
        app.MapGet($"{prefix}/{{serviceName}}/ImageServer/WMTS", WmtsKvpAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapGet($"{prefix}/{{serviceName}}/ImageServer/WMTS/1.0.0/WMTSCapabilities.xml",
                (HttpContext context, string serviceName, ICoverageCatalog coverages, IProjector projector, CancellationToken cancellation) =>
                    WmtsCapabilitiesAsync(context, serviceName, coverages, projector, cancellation))
            .Governed(SharingGovernedExtensions.ByService);
        app.MapGet($"{prefix}/{{serviceName}}/ImageServer/WMTS/tile/1.0.0/{{layer}}/{{style}}/{{set}}/{{level:int}}/{{row:int}}/{{column:int}}",
                async (HttpContext context, string serviceName, int level, int row, int column, ICoverageCatalog coverages,
                    ICoverageReaderFactory readers, IMapCanvasFactory canvases, IProjector projector, ConnectionBudget budget,
                    CancellationToken cancellation) =>
                {
                    if (!await WmtsOffAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false))
                    {
                        await TileAsync(context, serviceName, level, row, column, coverages, readers, canvases, projector, budget, cancellation)
                            .ConfigureAwait(false);
                    }
                })
            .Governed(SharingGovernedExtensions.ByService);
    }

    /// <summary>
    /// The key-value binding: read by the same parser as the server's own <c>/wmts</c>, then held to this
    /// service's one layer, style, format and grid.
    /// </summary>
    /// <remarks>
    /// <b>Its own reading until 2026-10-06, and a lenient one.</b> It looked only at REQUEST and the three numbers, so
    /// a missing or wrong SERVICE was answered with a document, and a GetTile for another layer, style, format or tile
    /// matrix set was answered with this service's tile. OGC's WMTS 1.0 suite, run against it for the first time,
    /// failed fifteen assertions, most of them that. A request with no parameters at all still gets the
    /// capabilities, which is what a person pasting the address into a browser means.
    /// </remarks>
    private static async Task WmtsKvpAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, ICoverageReaderFactory readers,
        IMapCanvasFactory canvases, IProjector projector, ConnectionBudget budget, CancellationToken cancellation)
    {
        string? Q(string name)
        {
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in context.Request.Query)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value.ToString();
                }
            }

            return null;
        }

        if (context.Request.Query.Count == 0)
        {
            await WmtsCapabilitiesAsync(context, serviceName, coverages, projector, cancellation).ConfigureAwait(false);
            return;
        }

        if (!WmtsRequest.TryParse(Q, out WmtsRequest? request, out WmtsFault? fault))
        {
            await WmtsRefuseAsync(context, fault!).ConfigureAwait(false);
            return;
        }

        if (request!.Operation == WmtsOperation.GetCapabilities)
        {
            await WmtsCapabilitiesAsync(context, serviceName, coverages, projector, cancellation, request.Sections).ConfigureAwait(false);
            return;
        }

        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage
            || await WmtsOffAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false))
        {
            return;
        }

        TilingScheme scheme = TilingScheme.For(coverage.Info);

        if (WmtsTileFault(request, coverage.Name, scheme) is { } wrong)
        {
            await WmtsRefuseAsync(context, wrong).ConfigureAwait(false);
            return;
        }

        await TileAsync(context, serviceName, int.Parse(request.TileMatrix, NumberStyles.None, CultureInfo.InvariantCulture),
                (int)request.TileRow, (int)request.TileCol, coverages, readers, canvases, projector, budget, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What is wrong with a GetTile for this service, or null: its one layer, the default style, PNG, its one tile
    /// matrix set, a matrix it has, and a row and column inside that matrix — WMTS 1.0.0 Table 28's codes.
    /// </summary>
    /// <param name="request">The GetTile.</param>
    /// <param name="layer">The service's one layer.</param>
    /// <param name="scheme">The service's grid.</param>
    /// <returns>The fault, or null.</returns>
    internal static WmtsFault? WmtsTileFault(WmtsRequest request, string layer, TilingScheme scheme)
    {
        if (!string.Equals(request.Layer, layer, StringComparison.Ordinal))
        {
            return WmtsFault.Invalid("LAYER", $"'{request.Layer}' is not a layer of this service; its one layer is '{layer}'.");
        }

        if (request.StyleOrFormatFault("image/png") is { } styleOrFormat)
        {
            return styleOrFormat;
        }

        if (!string.Equals(request.TileMatrixSet, WmtsSet, StringComparison.Ordinal))
        {
            return WmtsFault.Invalid("TILEMATRIXSET", $"'{request.TileMatrixSet}' is not a tile matrix set of this layer; its one set is '{WmtsSet}'.");
        }

        if (!int.TryParse(request.TileMatrix, NumberStyles.None, CultureInfo.InvariantCulture, out int level)
            || !scheme.Levels.Any(l => l.Level == level))
        {
            return WmtsFault.Invalid("TILEMATRIX", $"'{request.TileMatrix}' is not a tile matrix of '{WmtsSet}'.");
        }

        if (request.TileRow >= scheme.TilesDown(level))
        {
            return new WmtsFault(WmtsFault.TileOutOfRange, "TILEROW",
                $"Row {request.TileRow} is outside tile matrix {level}, which has {scheme.TilesDown(level)} rows.");
        }

        return request.TileCol >= scheme.TilesAcross(level)
            ? new WmtsFault(WmtsFault.TileOutOfRange, "TILECOL",
                $"Column {request.TileCol} is outside tile matrix {level}, which has {scheme.TilesAcross(level)} columns.")
            : null;
    }

    /// <summary>Whether the service's owner turned WMTS off — ADR-166 — said as a WMTS exception when they did.</summary>
    private static async Task<bool> WmtsOffAsync(HttpContext context, string serviceName, ICoverageCatalog coverages, CancellationToken cancellation)
    {
        (string? folder, string name) = Split(context, serviceName);

        if (await coverages.FindAsync(folder, name, cancellation).ConfigureAwait(false) is { } coverage && !coverage.OffersOgc("WMTS"))
        {
            await WmtsExceptionAsync(context, 404, "OperationNotSupported", "service",
                "This image service's owner has turned WMTS off. Its ArcGIS face is unaffected.").ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static async Task WmtsCapabilitiesAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, IProjector projector, CancellationToken cancellation,
        WmtsSections? sections = null)
    {
        sections ??= WmtsSections.All;

        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
            return;
        }

        if (!coverage.OffersOgc("WMTS"))
        {
            await WmtsExceptionAsync(context, 404, "OperationNotSupported", "service",
                "This image service's owner has turned WMTS off. Its ArcGIS face is unaffected.").ConfigureAwait(false);
            return;
        }

        TilingScheme scheme = TilingScheme.For(coverage.Info);
        int srid = scheme.Srid;
        bool degrees = Graticula.Geometries.AxisOrder.IsGeographic(srid);
        bool northFirst = Graticula.Geometries.AxisOrder.IsLatitudeFirst(srid);
        IReadOnlyList<Envelope?> wgs84 = await GeographicExtents
            .InWgs84Async(projector, [(coverage.Info.Srid, (Envelope?)coverage.Info.Extent)], cancellation).ConfigureAwait(false);

        string own = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"
            + context.Request.Path.Value![..(context.Request.Path.Value!.IndexOf("/WMTS", StringComparison.Ordinal) + 5)];

        using MemoryStream buffer = new();
        using (XmlWriter w = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            w.WriteStartElement("Capabilities", Wmts);
            w.WriteAttributeString("xmlns", "ows", null, Ows);
            w.WriteAttributeString("xmlns", "xlink", null, XLink);
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                Wmts + " http://schemas.opengis.net/wmts/1.0/wmtsGetCapabilities_response.xsd");
            w.WriteAttributeString("version", "1.0.0");

            if (sections.Includes("ServiceIdentification"))
            {
                w.WriteStartElement("ows", "ServiceIdentification", Ows);
                w.WriteElementString("ows", "Title", Ows, coverage.QualifiedName);
                w.WriteElementString("ows", "ServiceType", Ows, "OGC WMTS");
                w.WriteElementString("ows", "ServiceTypeVersion", Ows, "1.0.0");
                w.WriteEndElement();
            }

            if (sections.Includes("ServiceProvider"))
            {
                WmtsSections.WriteServiceProvider(w, "Graticula", null);
            }

            if (sections.Includes("OperationsMetadata"))
            {
                WmtsOperations(w, own);
            }

            if (sections.Includes("Contents"))
            {
                WmtsContents(w, coverage, scheme, wgs84[0], own, srid, degrees, northFirst);
            }

            w.WriteStartElement("ServiceMetadataURL", Wmts);
            w.WriteAttributeString("xlink", "href", XLink, own + "/1.0.0/WMTSCapabilities.xml");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellation).ConfigureAwait(false);
    }

    private const string Wmts = "http://www.opengis.net/wmts/1.0", Ows = "http://www.opengis.net/ows/1.1", XLink = "http://www.w3.org/1999/xlink";

    /// <summary>The two operations, at the key-value address.</summary>
    private static void WmtsOperations(XmlWriter w, string own)
    {
        w.WriteStartElement("ows", "OperationsMetadata", Ows);
        foreach (string operation in (string[])["GetCapabilities", "GetTile"])
        {
            w.WriteStartElement("ows", "Operation", Ows);
            w.WriteAttributeString("name", operation);
            w.WriteStartElement("ows", "DCP", Ows);
            w.WriteStartElement("ows", "HTTP", Ows);
            w.WriteStartElement("ows", "Get", Ows);
            w.WriteAttributeString("xlink", "href", XLink, own + "?");
            w.WriteStartElement("ows", "Constraint", Ows);
            w.WriteAttributeString("name", "GetEncoding");
            w.WriteStartElement("ows", "AllowedValues", Ows);
            w.WriteElementString("ows", "Value", Ows, "KVP");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    /// <summary>The service's one layer and its one tile matrix set.</summary>
    private static void WmtsContents(
        XmlWriter w, PublishedCoverage coverage, TilingScheme scheme, Envelope? box, string own, int srid, bool degrees, bool northFirst)
    {
        string layer = coverage.Name;
        w.WriteStartElement("Contents", Wmts);
        w.WriteStartElement("Layer", Wmts);
        w.WriteElementString("ows", "Title", Ows, coverage.QualifiedName);

        if (box is { } wgs84)
        {
            w.WriteStartElement("ows", "WGS84BoundingBox", Ows);
            w.WriteElementString("ows", "LowerCorner", Ows, $"{N(wgs84.MinX)} {N(wgs84.MinY)}");
            w.WriteElementString("ows", "UpperCorner", Ows, $"{N(wgs84.MaxX)} {N(wgs84.MaxY)}");
            w.WriteEndElement();
        }

        w.WriteElementString("ows", "Identifier", Ows, layer);
        w.WriteStartElement("Style", Wmts);
        w.WriteAttributeString("isDefault", "true");
        w.WriteElementString("ows", "Identifier", Ows, "default");
        w.WriteEndElement();
        w.WriteElementString("Format", Wmts, "image/png");
        w.WriteStartElement("TileMatrixSetLink", Wmts);
        w.WriteElementString("TileMatrixSet", Wmts, WmtsSet);
        w.WriteEndElement();
        w.WriteStartElement("ResourceURL", Wmts);
        w.WriteAttributeString("format", "image/png");
        w.WriteAttributeString("resourceType", "tile");
        w.WriteAttributeString("template", own + "/tile/1.0.0/" + layer + "/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("TileMatrixSet", Wmts);
        w.WriteElementString("ows", "Identifier", Ows, WmtsSet);
        w.WriteElementString("ows", "SupportedCRS", Ows, $"urn:ogc:def:crs:EPSG::{srid.ToString(CultureInfo.InvariantCulture)}");

        foreach (TileLevel level in scheme.Levels)
        {
            w.WriteStartElement("TileMatrix", Wmts);
            w.WriteElementString("ows", "Identifier", Ows, level.Level.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("ScaleDenominator", Wmts, N(level.Resolution * (degrees ? MetresPerDegree : 1) / 0.00028));
            w.WriteElementString("TopLeftCorner", Wmts, northFirst
                ? $"{N(scheme.OriginY)} {N(scheme.OriginX)}"
                : $"{N(scheme.OriginX)} {N(scheme.OriginY)}");
            w.WriteElementString("TileWidth", Wmts, scheme.TileSize.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("TileHeight", Wmts, scheme.TileSize.ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("MatrixWidth", Wmts, scheme.TilesAcross(level.Level).ToString(CultureInfo.InvariantCulture));
            w.WriteElementString("MatrixHeight", Wmts, scheme.TilesDown(level.Level).ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>A refusal in WMTS's own terms, with the status Table 28 gives its code.</summary>
    private static Task WmtsRefuseAsync(HttpContext context, WmtsFault fault) =>
        WmtsExceptionAsync(context, fault.Status, fault.Code, fault.Locator ?? string.Empty, fault.Message);

    private static async Task WmtsExceptionAsync(HttpContext context, int status, string code, string locator, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ows:ExceptionReport xmlns:ows=\"http://www.opengis.net/ows/1.1\" version=\"1.1.0\">"
            + $"<ows:Exception exceptionCode=\"{code}\" locator=\"{SecurityElementEscape(locator)}\"><ows:ExceptionText>{SecurityElementEscape(text)}</ows:ExceptionText></ows:Exception>"
            + "</ows:ExceptionReport>").ConfigureAwait(false);
    }

    private static string SecurityElementEscape(string text) => System.Security.SecurityElement.Escape(text) ?? string.Empty;
}
