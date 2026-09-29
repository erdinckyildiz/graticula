using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Graticula.Geometries;

namespace Graticula.Api.Tiles;

/// <summary>One WMTS layer: one vector tile service.</summary>
/// <param name="Identifier">Its identifier — the same id the OGC API Tiles face uses (<see cref="TileDocuments.CollectionId"/>).</param>
/// <param name="Title">What a person reads.</param>
/// <param name="Abstract">Its description, or null.</param>
/// <param name="Wgs84">Its extent in longitude and latitude, or null when unknown.</param>
/// <param name="Set">The tile matrix set its grid is.</param>
public sealed record WmtsLayer(string Identifier, string Title, string? Abstract, Envelope? Wgs84, TileMatrixSet Set);

/// <summary>
/// The WMTS 1.0.0 service metadata document — 07-057r7 §7.1.1.2 and its schema,
/// <c>wmtsGetCapabilities_response.xsd</c> — ADR-097 §5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>A layer is a vector tile service, its one format is a Mapbox Vector Tile, its one style is
/// <c>default</c>, and its one tile matrix set is its service's grid.</b> The sets are the ones OGC API
/// Tiles describes (<see cref="TileMatrixSet"/>): the registered <c>WebMercatorQuad</c> with its
/// well-known scale set — WMTS 1.0.0 Annex E's <c>GoogleMapsCompatible</c>, 256 cells at 0.28 mm, for
/// the reason <see cref="TileMatrixSet"/> gives — and a TUREF or custom set at 512 cells. One definition
/// for two faces, so a client moving between them sees the same identifiers and numbers.
/// </para>
/// <para>
/// <b><c>TopLeftCorner</c> is in the reference's own axis order</b> (07-057r7 §6.1, <c>ows:SupportedCRS</c>
/// as a URN): easting first for Web Mercator, northing first for every TUREF grid, whose authority writes
/// it that way (ADR-060). A corner written easting-first for TM30 would be read by a conforming client
/// as a point in the Black Sea.
/// </para>
/// <para>
/// <b>Both bindings are described</b>: KVP at <c>/wmts?</c>, and the RESTful <c>ResourceURL</c> template
/// under <c>/wmts/1.0.0/</c>, which is what most WMTS clients prefer when it is offered.
/// </para>
/// </remarks>
public static class WmtsCapabilities
{
    /// <summary>WMTS 1.0's namespace.</summary>
    public const string Wmts = "http://www.opengis.net/wmts/1.0";

    /// <summary>XLink's namespace.</summary>
    public const string XLink = "http://www.w3.org/1999/xlink";

    /// <summary>The WMTS 1.0 URN of the GoogleMapsCompatible well-known scale set — Annex E.4.</summary>
    public const string GoogleMapsCompatibleUrn = "urn:ogc:def:wkss:OGC:1.0:GoogleMapsCompatible";

    /// <summary>The file extension of a RESTful tile.</summary>
    public const string Extension = ".pbf";

    /// <summary>The RESTful path of the capabilities document, under <c>/wmts</c>.</summary>
    public const string RestCapabilities = "/1.0.0/WMTSCapabilities.xml";

    /// <summary>Writes the document.</summary>
    /// <param name="endpoint">The KVP endpoint's absolute URL, <c>…/wmts</c>.</param>
    /// <param name="layers">The layers the caller may see.</param>
    /// <returns>UTF-8 XML.</returns>
    public static byte[] Write(string endpoint, IReadOnlyList<WmtsLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(layers);

        using MemoryStream stream = new();

        using (XmlWriter xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("Capabilities", Wmts);
            xml.WriteAttributeString("xmlns", "ows", null, WmtsFault.Ows);
            xml.WriteAttributeString("xmlns", "xlink", null, XLink);
            xml.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            xml.WriteAttributeString(
                "xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                Wmts + " http://schemas.opengis.net/wmts/1.0/wmtsGetCapabilities_response.xsd");
            xml.WriteAttributeString("version", WmtsRequest.Version);

            xml.WriteStartElement("ows", "ServiceIdentification", WmtsFault.Ows);
            xml.WriteElementString("ows", "Title", WmtsFault.Ows, "Graticula — vector tiles");
            xml.WriteElementString(
                "ows", "Abstract", WmtsFault.Ows,
                "The vector tiles of this server's VectorTileServer services as WMTS layers. The tiles are "
                + "the same bytes the ArcGIS and OGC API Tiles faces serve.");
            xml.WriteElementString("ows", "ServiceType", WmtsFault.Ows, "OGC WMTS");
            xml.WriteElementString("ows", "ServiceTypeVersion", WmtsFault.Ows, WmtsRequest.Version);
            xml.WriteEndElement();

            xml.WriteStartElement("ows", "OperationsMetadata", WmtsFault.Ows);
            Operation(xml, "GetCapabilities", endpoint + "?", endpoint + RestCapabilities);
            Operation(xml, "GetTile", endpoint + "?", endpoint + "/1.0.0/");
            xml.WriteEndElement();

            xml.WriteStartElement("Contents", Wmts);

            foreach (WmtsLayer layer in layers)
            {
                Layer(xml, endpoint, layer);
            }

            // Each set once, however many layers use it, in the order the layers first name them.
            foreach (TileMatrixSet set in layers.Select(l => l.Set).DistinctBy(s => s.Id, StringComparer.Ordinal))
            {
                Set(xml, set);
            }

            xml.WriteEndElement();

            xml.WriteStartElement("ServiceMetadataURL", Wmts);
            xml.WriteAttributeString("xlink", "href", XLink, endpoint + RestCapabilities);
            xml.WriteEndElement();

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return stream.ToArray();
    }

    /// <summary>The RESTful tile template of a layer — the <c>ResourceURL</c>.</summary>
    /// <param name="endpoint">The KVP endpoint's absolute URL.</param>
    /// <param name="layer">The layer's identifier.</param>
    /// <returns>The template, with WMTS's own variable names.</returns>
    public static string TileTemplate(string endpoint, string layer) =>
        endpoint + "/1.0.0/" + Uri.EscapeDataString(layer) + "/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}" + Extension;

    private static void Operation(XmlWriter xml, string name, string kvp, string rest)
    {
        xml.WriteStartElement("ows", "Operation", WmtsFault.Ows);
        xml.WriteAttributeString("name", name);
        xml.WriteStartElement("ows", "DCP", WmtsFault.Ows);
        xml.WriteStartElement("ows", "HTTP", WmtsFault.Ows);

        foreach ((string href, string encoding) in (ReadOnlySpan<(string, string)>)[(kvp, "KVP"), (rest, "RESTful")])
        {
            xml.WriteStartElement("ows", "Get", WmtsFault.Ows);
            xml.WriteAttributeString("xlink", "href", XLink, href);
            xml.WriteStartElement("ows", "Constraint", WmtsFault.Ows);
            xml.WriteAttributeString("name", "GetEncoding");
            xml.WriteStartElement("ows", "AllowedValues", WmtsFault.Ows);
            xml.WriteElementString("ows", "Value", WmtsFault.Ows, encoding);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void Layer(XmlWriter xml, string endpoint, WmtsLayer layer)
    {
        xml.WriteStartElement("Layer", Wmts);
        xml.WriteElementString("ows", "Title", WmtsFault.Ows, layer.Title);

        if (layer.Abstract is { Length: > 0 } text)
        {
            xml.WriteElementString("ows", "Abstract", WmtsFault.Ows, text);
        }

        if (layer.Wgs84 is { IsEmpty: false } box)
        {
            // Longitude first, always: ows:WGS84BoundingBox is CRS84 by definition (OWS Common §10.2.2).
            xml.WriteStartElement("ows", "WGS84BoundingBox", WmtsFault.Ows);
            xml.WriteElementString("ows", "LowerCorner", WmtsFault.Ows, Pair(box.MinX, box.MinY));
            xml.WriteElementString("ows", "UpperCorner", WmtsFault.Ows, Pair(box.MaxX, box.MaxY));
            xml.WriteEndElement();
        }

        xml.WriteElementString("ows", "Identifier", WmtsFault.Ows, layer.Identifier);

        xml.WriteStartElement("Style", Wmts);
        xml.WriteAttributeString("isDefault", "true");
        xml.WriteElementString("ows", "Identifier", WmtsFault.Ows, WmtsRequest.DefaultStyle);
        xml.WriteEndElement();

        xml.WriteElementString("Format", Wmts, TileNames.Mvt);

        xml.WriteStartElement("TileMatrixSetLink", Wmts);
        xml.WriteElementString("TileMatrixSet", Wmts, layer.Set.Id);
        xml.WriteEndElement();

        xml.WriteStartElement("ResourceURL", Wmts);
        xml.WriteAttributeString("format", TileNames.Mvt);
        xml.WriteAttributeString("resourceType", "tile");
        xml.WriteAttributeString("template", TileTemplate(endpoint, layer.Identifier));
        xml.WriteEndElement();

        xml.WriteEndElement();
    }

    private static void Set(XmlWriter xml, TileMatrixSet set)
    {
        xml.WriteStartElement("TileMatrixSet", Wmts);
        xml.WriteElementString("ows", "Title", WmtsFault.Ows, set.Title);
        xml.WriteElementString("ows", "Identifier", WmtsFault.Ows, set.Id);
        xml.WriteElementString("ows", "SupportedCRS", WmtsFault.Ows, set.CrsUrn);

        if (set.WellKnownScaleSet is not null)
        {
            xml.WriteElementString("WellKnownScaleSet", Wmts, GoogleMapsCompatibleUrn);
        }

        (double first, double second) = set.Origin;

        foreach (TileMatrix matrix in set.Matrices)
        {
            xml.WriteStartElement("TileMatrix", Wmts);
            xml.WriteElementString("ows", "Identifier", WmtsFault.Ows, matrix.Id);
            xml.WriteElementString("ScaleDenominator", Wmts, Number(matrix.ScaleDenominator));
            xml.WriteElementString("TopLeftCorner", Wmts, Pair(first, second));
            xml.WriteElementString("TileWidth", Wmts, matrix.TileWidth.ToString(CultureInfo.InvariantCulture));
            xml.WriteElementString("TileHeight", Wmts, matrix.TileHeight.ToString(CultureInfo.InvariantCulture));
            xml.WriteElementString("MatrixWidth", Wmts, matrix.MatrixWidth.ToString(CultureInfo.InvariantCulture));
            xml.WriteElementString("MatrixHeight", Wmts, matrix.MatrixHeight.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Pair(double first, double second) => Number(first) + " " + Number(second);
}
