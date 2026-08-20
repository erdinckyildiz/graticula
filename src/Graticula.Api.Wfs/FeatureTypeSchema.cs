using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Writes the XML Schema a client reads before it can parse a feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>DescribeFeatureType is the schema, not a description of one.</b> A WFS
/// client uses it to know which properties exist and what type each is, and GDAL
/// in particular reads it before it will show a layer's fields at all. It is
/// generated from the same <c>LayerDescription</c> the query path uses, so the
/// schema and the data cannot disagree.
/// </para>
/// <para>
/// <b>One schema, always, because there is one namespace.</b> This wrote a schema
/// of <c>xsd:import</c> elements when the requested types spanned namespaces —
/// which the specification calls for, and which the OGC conformance suite cannot
/// consume: it builds its model from a DescribeFeatureType naming no types, and a
/// document of imports declares no feature types to find. That cost 264 of its
/// tests. Folders stopped being namespaces (<see cref="WfsNames.Prefix"/>), so the
/// case is gone rather than handled.
/// </para>
/// <para>
/// <b>Every property is optional and nillable.</b> A row is allowed a null in any
/// column — including the geometry, which <c>Feature</c> records as its own
/// case — and a schema that said otherwise would make a valid row an invalid
/// document. Nullability is carried in the <c>LayerDescription</c> and could be
/// reflected here; it is not, because a column that is non-null in the table can
/// still be absent from a response when <c>PropertyName</c> narrowed the request.
/// </para>
/// </remarks>
public static class FeatureTypeSchema
{
    /// <summary>Writes a schema covering the given types.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="types">The feature types to describe.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(
        Stream stream,
        IReadOnlyList<WfsFeatureType> types,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(types);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("xsd", "schema", WfsNames.Xsd).ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "gml", null, WfsNames.Gml)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                "xmlns", WfsNames.Prefix, null, WfsNames.Namespace).ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                null, "targetNamespace", null, WfsNames.Namespace).ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "elementFormDefault", null, "qualified")
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "version", null, "1.0").ConfigureAwait(false);

            await xml.WriteStartElementAsync("xsd", "import", WfsNames.Xsd).ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "namespace", null, WfsNames.Gml)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "schemaLocation",
                    null,
                    "http://schemas.opengis.net/gml/3.2.1/gml.xsd")
                .ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);

            foreach (WfsFeatureType type in types)
            {
                await TypeAsync(xml, type).ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    private static async Task TypeAsync(XmlWriter xml, WfsFeatureType type)
    {
        string complex = $"{type.Name}Type";

        await xml.WriteStartElementAsync("xsd", "element", WfsNames.Xsd).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "name", null, type.Name).ConfigureAwait(false);

        await xml.WriteAttributeStringAsync(
            null, "type", null, $"{WfsNames.Prefix}:{complex}").ConfigureAwait(false);

        await xml.WriteAttributeStringAsync(
            null, "substitutionGroup", null, "gml:AbstractFeature").ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);

        await xml.WriteStartElementAsync("xsd", "complexType", WfsNames.Xsd).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "name", null, complex).ConfigureAwait(false);

        await xml.WriteStartElementAsync("xsd", "complexContent", WfsNames.Xsd)
            .ConfigureAwait(false);

        await xml.WriteStartElementAsync("xsd", "extension", WfsNames.Xsd).ConfigureAwait(false);

        await xml.WriteAttributeStringAsync(null, "base", null, "gml:AbstractFeatureType")
            .ConfigureAwait(false);

        await xml.WriteStartElementAsync("xsd", "sequence", WfsNames.Xsd).ConfigureAwait(false);

        foreach (FieldDescription field in type.Fields)
        {
            await PropertyAsync(xml, field.Name, XsdTypeOf(field.Type)).ConfigureAwait(false);
        }

        await PropertyAsync(xml, type.GeometryProperty, GmlTypeOf(type.GeometryType))
            .ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task PropertyAsync(XmlWriter xml, string name, string type)
    {
        await xml.WriteStartElementAsync("xsd", "element", WfsNames.Xsd).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "name", null, name).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "type", null, type).ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "minOccurs", null, "0").ConfigureAwait(false);
        await xml.WriteAttributeStringAsync(null, "nillable", null, "true").ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>Our field types, as XML Schema names them.</summary>
    /// <param name="type">The field type.</param>
    /// <returns>The schema type name.</returns>
    public static string XsdTypeOf(FieldType type) => type switch
    {
        FieldType.SmallInteger => "xsd:short",
        FieldType.Integer => "xsd:int",
        FieldType.BigInteger => "xsd:long",
        FieldType.Single => "xsd:float",
        FieldType.Double => "xsd:double",
        FieldType.Boolean => "xsd:boolean",
        FieldType.Date => "xsd:dateTime",
        FieldType.Binary => "xsd:base64Binary",

        // Guid and Unknown are both text on the wire. A uuid has no XML Schema
        // type of its own, and Unknown is by definition something we could not
        // map — rendering it as anything narrower would be a claim.
        _ => "xsd:string",
    };

    /// <summary>
    /// Our geometry kinds, as GML 3.2 names their property types.
    /// </summary>
    /// <remarks>
    /// <b>Curve and Surface, not LineString and Polygon.</b> GML 3.2 has no
    /// <c>gml:LineStringPropertyType</c> or <c>gml:MultiPolygonPropertyType</c> —
    /// those are 3.1 — and a schema naming them fails to resolve, which a client
    /// reports as a broken layer rather than as a bad schema.
    /// </remarks>
    /// <param name="kind">The geometry kind.</param>
    /// <returns>The GML property type name.</returns>
    public static string GmlTypeOf(GeometryKind kind) => kind switch
    {
        GeometryKind.Point => "gml:PointPropertyType",
        GeometryKind.MultiPoint => "gml:MultiPointPropertyType",
        GeometryKind.LineString => "gml:CurvePropertyType",
        GeometryKind.MultiLineString => "gml:MultiCurvePropertyType",
        GeometryKind.Polygon => "gml:SurfacePropertyType",
        GeometryKind.MultiPolygon => "gml:MultiSurfacePropertyType",
        _ => "gml:GeometryPropertyType",
    };
}
