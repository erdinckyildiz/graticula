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
/// Writes the capabilities document — what this server is, and what it holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>It lists what the caller may see, and does not refuse.</b> ADR-039 §5: an
/// anonymous client gets the public feature types and learns nothing about the
/// rest, exactly as the services directory behaves. Refusing instead would tell
/// an unauthenticated caller that there is something here worth authenticating
/// for, and this server answers 404 for private content precisely so nobody
/// learns that.
/// </para>
/// <para>
/// <b>The filter capabilities are generated from what the reader accepts, in the
/// sense that both lists were written together and a test compares them.</b> A
/// capabilities document that advertises an operator the server refuses is worse
/// than one that advertises nothing: a client builds a request from it, and the
/// error arrives at the operator as *the server is broken* rather than *this is
/// not supported*.
/// </para>
/// </remarks>
public static class CapabilitiesDocument
{
    /// <summary>Comparison operators this server evaluates, as fes names them.</summary>
    public static IReadOnlyList<string> ComparisonOperators { get; } =
    [
        "PropertyIsEqualTo",
        "PropertyIsNotEqualTo",
        "PropertyIsLessThan",
        "PropertyIsGreaterThan",
        "PropertyIsLessThanOrEqualTo",
        "PropertyIsGreaterThanOrEqualTo",
        "PropertyIsLike",
        "PropertyIsNull",
        "PropertyIsBetween",
    ];

    /// <summary>Spatial operators this server evaluates.</summary>
    public static IReadOnlyList<string> SpatialOperators { get; } =
    [
        "BBOX",
        "Intersects",
        "Within",
        "Contains",
        "Crosses",
        "Overlaps",
        "Touches",
        "DWithin",
    ];

    /// <summary>Geometry shapes a filter may carry.</summary>
    public static IReadOnlyList<string> GeometryOperands { get; } =
    [
        "gml:Envelope",
        "gml:Point",
        "gml:LineString",
        "gml:Polygon",
        "gml:MultiPoint",
        "gml:MultiCurve",
        "gml:MultiSurface",
    ];

    /// <summary>Writes the document.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="endpoint">This server's own <c>/wfs</c> URL.</param>
    /// <param name="title">What to call this server.</param>
    /// <param name="types">The feature types this caller may see.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(
        Stream stream,
        string endpoint,
        string title,
        IReadOnlyList<WfsFeatureType> types,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("wfs", "WFS_Capabilities", WfsNames.Wfs)
                .ConfigureAwait(false);

            await Namespaces(xml).ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "version", null, WfsNames.Version)
                .ConfigureAwait(false);

            await IdentificationAsync(xml, title).ConfigureAwait(false);
            await ProviderAsync(xml).ConfigureAwait(false);
            await OperationsAsync(xml, endpoint).ConfigureAwait(false);
            await TypesAsync(xml, types).ConfigureAwait(false);
            await FilterCapabilitiesAsync(xml).ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Declares every namespace up front, including one per folder.
    /// </summary>
    /// <remarks>
    /// <b>At the root, so a type name means something where it is read.</b>
    /// <c>hosted:tr_yol</c> is a qualified name, and a client that copies it out of
    /// this document has to be able to resolve <c>hosted</c> — which it can only
    /// do if the prefix is bound here.
    /// </remarks>
    private static async Task Namespaces(XmlWriter xml)
    {
        await xml.WriteAttributeStringAsync("xmlns", "ows", null, WfsNames.Ows).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync("xmlns", "gml", null, WfsNames.Gml).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync("xmlns", "fes", null, WfsNames.Fes).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync("xmlns", "xlink", null, WfsNames.Xlink).ConfigureAwait(false);

        // <b>Bound at the root, so a type name means something where it is read.</b>
        // `graticula:tr_yol` is a qualified name, and a client that copies it out of
        // this document has to be able to resolve the prefix.
        await xml.WriteAttributeStringAsync(
            "xmlns", WfsNames.Prefix, null, WfsNames.Namespace).ConfigureAwait(false);
    }

    private static async Task IdentificationAsync(XmlWriter xml, string title)
    {
        await xml.WriteStartElementAsync("ows", "ServiceIdentification", WfsNames.Ows)
            .ConfigureAwait(false);

        await xml.WriteElementStringAsync("ows", "Title", WfsNames.Ows, title).ConfigureAwait(false);

        await xml.WriteElementStringAsync(
                "ows",
                "Abstract",
                WfsNames.Ows,
                // <b>[D-03](../../docs/architecture-debt.md): the abstract said *over
                // PostGIS*.</b> A capabilities document is reachable by anybody who can
                // reach a public service, and the engine behind a layer is the provider
                // type [security.md](../../docs/security.md) §5 keeps for an authenticated
                // administrator — it implies a version, and by implication an
                // organisation's internal topology. Nothing a WFS client does with this
                // document changes on the answer, which is what made it free to remove.
                "Read-only WFS 2.0. Query, paging and property values are "
                + "supported; Transaction and LockFeature are not implemented.")
            .ConfigureAwait(false);

        await xml.WriteStartElementAsync("ows", "ServiceType", WfsNames.Ows).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "codeSpace", null, "OGC").ConfigureAwait(false);
        await xml.WriteStringAsync(WfsNames.Service).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteElementStringAsync(
            "ows", "ServiceTypeVersion", WfsNames.Ows, WfsNames.Version).ConfigureAwait(false);

        await xml.WriteElementStringAsync("ows", "Fees", WfsNames.Ows, "NONE").ConfigureAwait(false);

        await xml.WriteElementStringAsync("ows", "AccessConstraints", WfsNames.Ows, "NONE")
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task ProviderAsync(XmlWriter xml)
    {
        await xml.WriteStartElementAsync("ows", "ServiceProvider", WfsNames.Ows)
            .ConfigureAwait(false);

        await xml.WriteElementStringAsync("ows", "ProviderName", WfsNames.Ows, "Graticula")
            .ConfigureAwait(false);

        // <b>Empty, and required.</b> OWS 1.1 makes ServiceContact mandatory inside
        // ServiceProvider and every one of its own children optional, so this is
        // the honest shape for a server that has no contact details to publish:
        // present, because the schema says so, and saying nothing it does not know.
        // Found by validating against the published schema rather than by reading
        // it — ADR-039 condition 5.
        await xml.WriteStartElementAsync("ows", "ServiceContact", WfsNames.Ows)
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task OperationsAsync(XmlWriter xml, string endpoint)
    {
        await xml.WriteStartElementAsync("ows", "OperationsMetadata", WfsNames.Ows)
            .ConfigureAwait(false);

        foreach (string operation in (string[])
        [
            "GetCapabilities",
            "DescribeFeatureType",
            "GetFeature",
            "GetPropertyValue",
            "ListStoredQueries",
            "DescribeStoredQueries",
        ])
        {
            await xml.WriteStartElementAsync("ows", "Operation", WfsNames.Ows).ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "name", null, operation).ConfigureAwait(false);

            await xml.WriteStartElementAsync("ows", "DCP", WfsNames.Ows).ConfigureAwait(false);
            await xml.WriteStartElementAsync("ows", "HTTP", WfsNames.Ows).ConfigureAwait(false);

            foreach (string method in (string[])["Get", "Post"])
            {
                await xml.WriteStartElementAsync("ows", method, WfsNames.Ows).ConfigureAwait(false);

                await xml.WriteAttributeStringAsync("xlink", "href", WfsNames.Xlink, endpoint)
                    .ConfigureAwait(false);

                await xml.WriteEndElementAsync().ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);

            if (string.Equals(operation, "GetFeature", StringComparison.Ordinal))
            {
                await AllowedAsync(
                        xml,
                        "outputFormat",
                        [WfsNames.GmlMediaType, WfsNames.GeoJsonMediaType])
                    .ConfigureAwait(false);
            }

            if (string.Equals(operation, "GetFeature", StringComparison.Ordinal)
                || string.Equals(operation, "GetPropertyValue", StringComparison.Ordinal))
            {
                // Both values mean the same here and both are honoured: no response
                // carries a reference, so there is nothing to resolve either way.
                await AllowedAsync(xml, "resolve", ["local", "none"]).ConfigureAwait(false);
            }

            if (string.Equals(operation, "GetCapabilities", StringComparison.Ordinal))
            {
                await AllowedAsync(xml, "AcceptVersions", [WfsNames.Version]).ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        // <b>The conformance classes, and the two false ones are the point.</b> A
        // client reads these to decide what it may send. Declaring
        // ImplementsTransactionalWFS FALSE is what stops it offering an edit
        // button that cannot work.
        foreach ((string name, string value) in ((string, string)[])
        [
            ("ImplementsBasicWFS", "TRUE"),
            ("ImplementsTransactionalWFS", "FALSE"),
            ("ImplementsLockingWFS", "FALSE"),
            ("KVPEncoding", "TRUE"),
            ("XMLEncoding", "TRUE"),
            ("SOAPEncoding", "FALSE"),
            ("ImplementsInheritance", "FALSE"),
            ("ImplementsRemoteResolve", "FALSE"),
            ("ImplementsResultPaging", "TRUE"),
            ("ImplementsStandardJoins", "FALSE"),
            ("ImplementsSpatialJoins", "FALSE"),
            ("ImplementsTemporalJoins", "FALSE"),
            ("ImplementsFeatureVersioning", "FALSE"),
            ("ManageStoredQueries", "FALSE"),
        ])
        {
            await ConstraintAsync(xml, WfsNames.Ows, "ows", name, value).ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task AllowedAsync(
        XmlWriter xml, string name, IReadOnlyList<string> values)
    {
        await xml.WriteStartElementAsync("ows", "Parameter", WfsNames.Ows).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "name", null, name).ConfigureAwait(false);
        await xml.WriteStartElementAsync("ows", "AllowedValues", WfsNames.Ows).ConfigureAwait(false);

        foreach (string value in values)
        {
            await xml.WriteElementStringAsync("ows", "Value", WfsNames.Ows, value)
                .ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task ConstraintAsync(
        XmlWriter xml, string ns, string prefix, string name, string value)
    {
        await xml.WriteStartElementAsync(prefix, "Constraint", ns).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "name", null, name).ConfigureAwait(false);
        await xml.WriteStartElementAsync("ows", "NoValues", WfsNames.Ows).ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteElementStringAsync("ows", "DefaultValue", WfsNames.Ows, value)
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task TypesAsync(XmlWriter xml, IReadOnlyList<WfsFeatureType> types)
    {
        // <b>Omitted entirely when it would be empty, because an empty one is not
        // valid.</b> wfs:FeatureTypeList requires at least one wfs:FeatureType, and
        // the list this caller may see can legitimately be empty — a server whose
        // layers are all private, answering an anonymous request. Writing the empty
        // element would make that caller's capabilities document fail to validate,
        // which is a worse answer than a document that lists nothing.
        if (types.Count == 0)
        {
            return;
        }

        await xml.WriteStartElementAsync("wfs", "FeatureTypeList", WfsNames.Wfs)
            .ConfigureAwait(false);

        foreach (WfsFeatureType type in types)
        {
            await xml.WriteStartElementAsync("wfs", "FeatureType", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteElementStringAsync("wfs", "Name", WfsNames.Wfs, type.QualifiedName)
                .ConfigureAwait(false);

            await xml.WriteElementStringAsync("wfs", "Title", WfsNames.Wfs, type.Title)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(type.Abstract))
            {
                await xml.WriteElementStringAsync(
                    "wfs", "Abstract", WfsNames.Wfs, type.Abstract).ConfigureAwait(false);
            }

            await xml.WriteElementStringAsync(
                "wfs", "DefaultCRS", WfsNames.Wfs, WfsNames.CrsUrn(type.Srid)).ConfigureAwait(false);

            // <b>Written only when the layer is already in WGS 84.</b> This element
            // is longitude/latitude in WGS 84 by definition, and converting an
            // extent in a national grid needs a projection — which is a round trip
            // per layer in a document meant to be cheap. Omitting it is allowed
            // (minOccurs="0"); writing the layer's own numbers under a WGS 84 label
            // would not be. A client loses the initial zoom for those layers and
            // gets no wrong answer, which is the right way round.
            // Q-125 carries the fix: project the extents once and remember them.
            if (type.Srid == 4326 && type.Extent is { IsEmpty: false } extent)
            {
                await BoundingBoxAsync(xml, extent).ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task BoundingBoxAsync(XmlWriter xml, Envelope extent)
    {
        await xml.WriteStartElementAsync("ows", "WGS84BoundingBox", WfsNames.Ows)
            .ConfigureAwait(false);

        await xml.WriteElementStringAsync(
            "ows", "LowerCorner", WfsNames.Ows, Corner(extent.MinX, extent.MinY))
            .ConfigureAwait(false);

        await xml.WriteElementStringAsync(
            "ows", "UpperCorner", WfsNames.Ows, Corner(extent.MaxX, extent.MaxY))
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        // Always longitude first: ows:WGS84BoundingBox is defined that way whatever
        // the CRS's own axis order says, and this is the one place in the document
        // where 4326 is not latitude-first.
        static string Corner(double x, double y) =>
            $"{x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task FilterCapabilitiesAsync(XmlWriter xml)
    {
        await xml.WriteStartElementAsync("fes", "Filter_Capabilities", WfsNames.Fes)
            .ConfigureAwait(false);

        await xml.WriteStartElementAsync("fes", "Conformance", WfsNames.Fes).ConfigureAwait(false);

        foreach ((string name, string value) in ((string, string)[])
        [
            ("ImplementsQuery", "TRUE"),
            ("ImplementsAdHocQuery", "TRUE"),
            ("ImplementsFunctions", "FALSE"),
            ("ImplementsResourceId", "TRUE"),
            ("ImplementsMinStandardFilter", "TRUE"),
            ("ImplementsStandardFilter", "TRUE"),
            ("ImplementsMinSpatialFilter", "TRUE"),
            ("ImplementsSpatialFilter", "FALSE"),
            ("ImplementsMinTemporalFilter", "FALSE"),
            ("ImplementsTemporalFilter", "FALSE"),
            ("ImplementsVersionNav", "FALSE"),
            ("ImplementsSorting", "TRUE"),
            ("ImplementsExtendedOperators", "FALSE"),
            ("ImplementsMinimumXPath", "TRUE"),
            ("ImplementsSchemaElementFunc", "FALSE"),
        ])
        {
            await ConstraintAsync(xml, WfsNames.Fes, "fes", name, value).ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteStartElementAsync("fes", "Id_Capabilities", WfsNames.Fes)
            .ConfigureAwait(false);

        await xml.WriteStartElementAsync("fes", "ResourceIdentifier", WfsNames.Fes)
            .ConfigureAwait(false);

        await xml.WriteAttributeStringAsync(null, "name", null, "fes:ResourceId")
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteStartElementAsync("fes", "Scalar_Capabilities", WfsNames.Fes)
            .ConfigureAwait(false);

        await xml.WriteStartElementAsync("fes", "LogicalOperators", WfsNames.Fes)
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await OperatorsAsync(xml, "ComparisonOperators", "ComparisonOperator", ComparisonOperators)
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteStartElementAsync("fes", "Spatial_Capabilities", WfsNames.Fes)
            .ConfigureAwait(false);

        await OperatorsAsync(xml, "GeometryOperands", "GeometryOperand", GeometryOperands)
            .ConfigureAwait(false);

        await OperatorsAsync(xml, "SpatialOperators", "SpatialOperator", SpatialOperators)
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task OperatorsAsync(
        XmlWriter xml, string group, string item, IReadOnlyList<string> names)
    {
        await xml.WriteStartElementAsync("fes", group, WfsNames.Fes).ConfigureAwait(false);

        foreach (string name in names)
        {
            await xml.WriteStartElementAsync("fes", item, WfsNames.Fes).ConfigureAwait(false);
            await xml.WriteAttributeStringAsync(null, "name", null, name).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }
}
