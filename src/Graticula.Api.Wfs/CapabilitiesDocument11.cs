using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// The WFS 1.1.0 capabilities document — ADR-168: the same feature types and operations as 2.0.0's, in WFS 1.1.0's
/// shape — OWS 1.0, <c>DefaultSRS</c>, GML 3.1.1 as the output format, and OGC Filter 1.1's capabilities.
/// </summary>
public static class CapabilitiesDocument11
{
    private const string Ogc = "http://www.opengis.net/ogc";

    /// <summary>Writes the document.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="endpoint">The address the request came to.</param>
    /// <param name="title">What to call the service.</param>
    /// <param name="types">The feature types this caller may see.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="metadata">At a service's own address, what it says of itself (ADR-167), or null.</param>
    /// <param name="transactions">Whether this caller may edit any type listed (ADR-169).</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(
        Stream stream,
        string endpoint,
        string title,
        IReadOnlyList<WfsFeatureType> types,
        CancellationToken cancellation,
        Graticula.Catalog.OgcServiceMetadata? metadata = null,
        bool transactions = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(types);
        WfsDialect d = WfsDialect.V110;

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("wfs", "WFS_Capabilities", d.Wfs).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync("xmlns", "ows", null, d.Ows).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync("xmlns", "ogc", null, Ogc).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync("xmlns", "gml", null, d.Gml).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync("xmlns", "xlink", null, WfsNames.Xlink).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync("xmlns", WfsNames.Prefix, null, WfsNames.Namespace).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync(null, "version", null, d.Version).ConfigureAwait(false);

            // ows:ServiceIdentification.
            await xml.WriteStartElementAsync("ows", "ServiceIdentification", d.Ows).ConfigureAwait(false);
            await xml.WriteElementStringAsync("ows", "Title", d.Ows, title).ConfigureAwait(false);
            await xml.WriteElementStringAsync("ows", "Abstract", d.Ows, metadata?.Abstract is { Length: > 0 } described
                ? described
                : "WFS 1.1.0, beside the server's WFS 2.0.0, with Transaction; LockFeature is not implemented.")
                .ConfigureAwait(false);

            if (metadata?.Keywords is { Count: > 0 } keywords)
            {
                await xml.WriteStartElementAsync("ows", "Keywords", d.Ows).ConfigureAwait(false);

                foreach (string keyword in keywords)
                {
                    await xml.WriteElementStringAsync("ows", "Keyword", d.Ows, keyword).ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
            }

            await xml.WriteElementStringAsync("ows", "ServiceType", d.Ows, "WFS").ConfigureAwait(false);
            await xml.WriteElementStringAsync("ows", "ServiceTypeVersion", d.Ows, d.Version).ConfigureAwait(false);
            await xml.WriteElementStringAsync("ows", "Fees", d.Ows, metadata?.Fees is { Length: > 0 } fees ? fees : "NONE")
                .ConfigureAwait(false);
            await xml.WriteElementStringAsync("ows", "AccessConstraints", d.Ows,
                metadata?.AccessConstraints is { Length: > 0 } constraints ? constraints : "NONE").ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);

            await xml.WriteStartElementAsync("ows", "ServiceProvider", d.Ows).ConfigureAwait(false);
            await xml.WriteElementStringAsync("ows", "ProviderName", d.Ows, "Graticula").ConfigureAwait(false);
            await xml.WriteStartElementAsync("ows", "ServiceContact", d.Ows).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);

            // ows:OperationsMetadata: the read operations, GET and POST at this address.
            await xml.WriteStartElementAsync("ows", "OperationsMetadata", d.Ows).ConfigureAwait(false);

            foreach (string operation in transactions
                ? (string[])["GetCapabilities", "DescribeFeatureType", "GetFeature", "Transaction"]
                : ["GetCapabilities", "DescribeFeatureType", "GetFeature"])
            {
                await xml.WriteStartElementAsync("ows", "Operation", d.Ows).ConfigureAwait(false);
                await xml.WriteAttributeStringAsync(null, "name", null, operation).ConfigureAwait(false);
                await xml.WriteStartElementAsync("ows", "DCP", d.Ows).ConfigureAwait(false);
                await xml.WriteStartElementAsync("ows", "HTTP", d.Ows).ConfigureAwait(false);

                foreach (string method in (string[])["Get", "Post"])
                {
                    await xml.WriteStartElementAsync("ows", method, d.Ows).ConfigureAwait(false);
                    await xml.WriteAttributeStringAsync("xlink", "href", WfsNames.Xlink, endpoint).ConfigureAwait(false);
                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
                await xml.WriteEndElementAsync().ConfigureAwait(false);

                if (operation == "GetCapabilities")
                {
                    await ParameterAsync(xml, d, "AcceptVersions", WfsDialect.Versions).ConfigureAwait(false);
                }
                else if (operation == "GetFeature")
                {
                    await ParameterAsync(xml, d, "resultType", ["results", "hits"]).ConfigureAwait(false);
                    await ParameterAsync(xml, d, "outputFormat", [d.GmlMediaType, WfsNames.GeoJsonMediaType]).ConfigureAwait(false);
                }
                else
                {
                    await ParameterAsync(xml, d, "outputFormat", ["text/xml; subtype=gml/3.1.1"]).ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);

            // wfs:FeatureTypeList — omitted when empty, which the schema would refuse.
            if (types.Count > 0)
            {
                await xml.WriteStartElementAsync("wfs", "FeatureTypeList", d.Wfs).ConfigureAwait(false);
                await xml.WriteStartElementAsync("wfs", "Operations", d.Wfs).ConfigureAwait(false);
                foreach (string verb in transactions ? (string[])["Query", "Insert", "Update", "Delete"] : ["Query"])
                {
                    await xml.WriteElementStringAsync("wfs", "Operation", d.Wfs, verb).ConfigureAwait(false);
                }
                await xml.WriteEndElementAsync().ConfigureAwait(false);

                foreach (WfsFeatureType type in types)
                {
                    await xml.WriteStartElementAsync("wfs", "FeatureType", d.Wfs).ConfigureAwait(false);
                    await xml.WriteElementStringAsync("wfs", "Name", d.Wfs, type.QualifiedName).ConfigureAwait(false);
                    await xml.WriteElementStringAsync("wfs", "Title", d.Wfs, type.Title).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(type.Abstract))
                    {
                        await xml.WriteElementStringAsync("wfs", "Abstract", d.Wfs, type.Abstract).ConfigureAwait(false);
                    }

                    await xml.WriteElementStringAsync("wfs", "DefaultSRS", d.Wfs, WfsNames.CrsUrn(type.PublishedSrid))
                        .ConfigureAwait(false);
                    await xml.WriteStartElementAsync("wfs", "OutputFormats", d.Wfs).ConfigureAwait(false);
                    await xml.WriteElementStringAsync("wfs", "Format", d.Wfs, d.GmlMediaType).ConfigureAwait(false);
                    await xml.WriteEndElementAsync().ConfigureAwait(false);

                    if (type.Geographic is { IsEmpty: false } box)
                    {
                        await xml.WriteStartElementAsync("ows", "WGS84BoundingBox", d.Ows).ConfigureAwait(false);
                        await xml.WriteElementStringAsync("ows", "LowerCorner", d.Ows, Corner(box.MinX, box.MinY)).ConfigureAwait(false);
                        await xml.WriteElementStringAsync("ows", "UpperCorner", d.Ows, Corner(box.MaxX, box.MaxY)).ConfigureAwait(false);
                        await xml.WriteEndElementAsync().ConfigureAwait(false);
                    }

                    await xml.WriteEndElementAsync().ConfigureAwait(false);
                }

                await xml.WriteEndElementAsync().ConfigureAwait(false);
            }

            await FilterCapabilitiesAsync(xml).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    private static string Corner(double x, double y) =>
        $"{x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)}";

    private static async Task ParameterAsync(XmlWriter xml, WfsDialect d, string name, IReadOnlyList<string> values)
    {
        await xml.WriteStartElementAsync("ows", "Parameter", d.Ows).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "name", null, name).ConfigureAwait(false);

        foreach (string value in values)
        {
            await xml.WriteElementStringAsync("ows", "Value", d.Ows, value).ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>OGC Filter 1.1's capabilities: what the filter reader answers, in that version's vocabulary.</summary>
    private static async Task FilterCapabilitiesAsync(XmlWriter xml)
    {
        await xml.WriteStartElementAsync("ogc", "Filter_Capabilities", Ogc).ConfigureAwait(false);

        await xml.WriteStartElementAsync("ogc", "Spatial_Capabilities", Ogc).ConfigureAwait(false);
        await xml.WriteStartElementAsync("ogc", "GeometryOperands", Ogc).ConfigureAwait(false);

        foreach (string operand in (string[])["gml:Envelope", "gml:Point", "gml:LineString", "gml:Polygon"])
        {
            await xml.WriteElementStringAsync("ogc", "GeometryOperand", Ogc, operand).ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteStartElementAsync("ogc", "SpatialOperators", Ogc).ConfigureAwait(false);

        foreach (string op in (string[])["BBOX", "Intersects", "Within", "Contains", "Crosses", "Overlaps", "Touches", "DWithin"])
        {
            await xml.WriteStartElementAsync("ogc", "SpatialOperator", Ogc).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync(null, "name", null, op).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteStartElementAsync("ogc", "Scalar_Capabilities", Ogc).ConfigureAwait(false);
        await xml.WriteStartElementAsync("ogc", "LogicalOperators", Ogc).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteStartElementAsync("ogc", "ComparisonOperators", Ogc).ConfigureAwait(false);

        foreach (string op in (string[])["EqualTo", "NotEqualTo", "LessThan", "GreaterThan", "LessThanEqualTo", "GreaterThanEqualTo", "Like", "Between", "NullCheck"])
        {
            await xml.WriteElementStringAsync("ogc", "ComparisonOperator", Ogc, op).ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);

        // <b>EID as well as FID</b>: a GmlObjectId is read as a FeatureId is (WfsDialect), and WFS 1.1.0's basic
        // conformance requires the capability be said. OGC's WFS 1.1 suite failed Basic-GetCapabilities-tc15 on
        // its absence, the first time it ran — 2026-10-04.
        await xml.WriteStartElementAsync("ogc", "Id_Capabilities", Ogc).ConfigureAwait(false);
        await xml.WriteStartElementAsync("ogc", "EID", Ogc).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteStartElementAsync("ogc", "FID", Ogc).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }
}
