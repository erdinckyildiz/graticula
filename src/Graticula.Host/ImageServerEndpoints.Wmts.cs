using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
        app.MapGet($"{prefix}/{{serviceName}}/ImageServer/WMTS/1.0.0/WMTSCapabilities.xml", WmtsCapabilitiesAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapGet($"{prefix}/{{serviceName}}/ImageServer/WMTS/tile/1.0.0/{{layer}}/{{style}}/{{set}}/{{level:int}}/{{row:int}}/{{column:int}}",
                (HttpContext context, string serviceName, int level, int row, int column, ICoverageCatalog coverages,
                    ICoverageReaderFactory readers, IMapCanvasFactory canvases, IProjector projector, ConnectionBudget budget,
                    CancellationToken cancellation) =>
                    TileAsync(context, serviceName, level, row, column, coverages, readers, canvases, projector, budget, cancellation))
            .Governed(SharingGovernedExtensions.ByService);
    }

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

        string request = Q("request") ?? "GetCapabilities";

        if (request.Equals("GetCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            await WmtsCapabilitiesAsync(context, serviceName, coverages, projector, cancellation).ConfigureAwait(false);
            return;
        }

        if (!request.Equals("GetTile", StringComparison.OrdinalIgnoreCase))
        {
            await WmtsExceptionAsync(context, 400, "OperationNotSupported", "request",
                $"`{request}` is not a WMTS operation this service answers: it answers GetCapabilities and GetTile.").ConfigureAwait(false);
            return;
        }

        if (!int.TryParse(Q("tilematrix"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
            || !int.TryParse(Q("tilerow"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int row)
            || !int.TryParse(Q("tilecol"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int column))
        {
            await WmtsExceptionAsync(context, 400, "MissingParameterValue", "TileMatrix",
                "GetTile names a TileMatrix, a TileRow and a TileCol, each a whole number.").ConfigureAwait(false);
            return;
        }

        await TileAsync(context, serviceName, level, row, column, coverages, readers, canvases, projector, budget, cancellation)
            .ConfigureAwait(false);
    }

    private static async Task WmtsCapabilitiesAsync(
        HttpContext context, string serviceName, ICoverageCatalog coverages, IProjector projector, CancellationToken cancellation)
    {
        if (await FindAsync(context, serviceName, coverages, cancellation).ConfigureAwait(false) is not { } coverage)
        {
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
        string layer = coverage.Name;
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        using MemoryStream buffer = new();
        using (XmlWriter w = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            const string Wmts = "http://www.opengis.net/wmts/1.0", Ows = "http://www.opengis.net/ows/1.1", XLink = "http://www.w3.org/1999/xlink";
            w.WriteStartElement("Capabilities", Wmts);
            w.WriteAttributeString("xmlns", "ows", null, Ows);
            w.WriteAttributeString("xmlns", "xlink", null, XLink);
            w.WriteAttributeString("version", "1.0.0");

            w.WriteStartElement("ows", "ServiceIdentification", Ows);
            w.WriteElementString("ows", "Title", Ows, coverage.QualifiedName);
            w.WriteElementString("ows", "ServiceType", Ows, "OGC WMTS");
            w.WriteElementString("ows", "ServiceTypeVersion", Ows, "1.0.0");
            w.WriteEndElement();

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

            w.WriteStartElement("Contents", Wmts);
            w.WriteStartElement("Layer", Wmts);
            w.WriteElementString("ows", "Title", Ows, coverage.QualifiedName);

            if (wgs84[0] is { } box)
            {
                w.WriteStartElement("ows", "WGS84BoundingBox", Ows);
                w.WriteElementString("ows", "LowerCorner", Ows, $"{N(box.MinX)} {N(box.MinY)}");
                w.WriteElementString("ows", "UpperCorner", Ows, $"{N(box.MaxX)} {N(box.MaxY)}");
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

            w.WriteStartElement("ServiceMetadataURL", Wmts);
            w.WriteAttributeString("xlink", "href", XLink, own + "/1.0.0/WMTSCapabilities.xml");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellation).ConfigureAwait(false);
    }

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
