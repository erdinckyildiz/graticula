using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Writes a <c>wfs:FeatureCollection</c> in GML 3.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streamed, and the counts are still exact.</b> The collection's
/// <c>numberReturned</c> is a required attribute on the root element, so it has to
/// be known before the first feature is written — and buffering the page to find
/// out is the allocation A-037 measured as this server's binding constraint. The
/// caller supplies both numbers instead, having asked the provider how many rows
/// match; that is one extra round trip and it buys exact paging metadata rather
/// than the <c>unknown</c> the specification also permits. The cost is
/// [D-118](../../../docs/architecture-debt.md).
/// </para>
/// <para>
/// <b>A null value is <c>xsi:nil</c>, not an absent element.</b> The two mean
/// different things to a client reading a schema where every property is optional:
/// absent is *not asked for*, nil is *asked for and empty*. Collapsing them makes
/// a narrowed request indistinguishable from a null column.
/// </para>
/// </remarks>
public sealed class GmlFeatureCollectionWriter
{
    private readonly WfsFeatureType _type;
    private readonly GmlGeometryWriter _geometry;
    private readonly string _endpoint;

    /// <summary>Creates a writer for one feature type.</summary>
    /// <param name="type">The type being written.</param>
    /// <param name="outputSrid">The reference the geometries will be in.</param>
    /// <param name="endpoint">This server's own <c>/wfs</c> URL, for the schema hint.</param>
    public GmlFeatureCollectionWriter(WfsFeatureType type, int outputSrid, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        _type = type;
        _endpoint = endpoint;
        _geometry = new GmlGeometryWriter(outputSrid);
    }

    /// <summary>Writes the collection.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="features">The page of features, read as they are written.</param>
    /// <param name="numberMatched">How many match, or null for unknown.</param>
    /// <param name="numberReturned">How many this page holds.</param>
    /// <param name="timestamp">When the response was produced.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="next">Where the following page is, or null if this is the last.</param>
    /// <param name="previous">Where the preceding page is, or null if this is the first.</param>
    /// <remarks>
    /// <b><c>next</c> and <c>previous</c> are how a WFS client pages, and they were
    /// absent.</b> §7.7.4.4.1 requires them on a response that is part of a larger
    /// result set — CITE's <c>traverseResultSetInBothDirections</c> and
    /// <c>getFeatureWithHitsOnly</c> both fail without them. <c>numberMatched</c> alone
    /// is not a substitute: it tells a client how much there is and not how to ask for
    /// the rest, and a client that has to construct <c>startIndex</c> itself is
    /// guessing at a page size the server chose.
    /// </remarks>
    public async Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<Feature> features,
        long? numberMatched,
        long numberReturned,
        DateTimeOffset timestamp,
        CancellationToken cancellation,
        string? next = null,
        string? previous = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(features);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("wfs", "FeatureCollection", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "gml", null, WfsNames.Gml)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "xsi", null, WfsNames.Xsi)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", WfsNames.Prefix, null, WfsNames.Namespace)
                .ConfigureAwait(false);

            // <b>The hint that makes this document resolvable.</b> Two pairs: the
            // WFS namespace against the published schema, and this server's own
            // application namespace against its DescribeFeatureType. Without the
            // second, a validator — and a strict client — has the feature elements
            // and nowhere to learn what they are. Added after validating against
            // schemas.opengis.net, which is exactly what ADR-039 condition 5 is
            // for.
            await xml.WriteAttributeStringAsync("xsi", "schemaLocation", WfsNames.Xsi, Hint())
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "timeStamp",
                    null,
                    timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "numberMatched",
                    null,
                    numberMatched?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "numberReturned",
                    null,
                    numberReturned.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);

            if (next is { Length: > 0 })
            {
                await xml.WriteAttributeStringAsync(null, "next", null, next)
                    .ConfigureAwait(false);
            }

            if (previous is { Length: > 0 })
            {
                await xml.WriteAttributeStringAsync(null, "previous", null, previous)
                    .ConfigureAwait(false);
            }

            await foreach (Feature feature in features.WithCancellation(cancellation)
                .ConfigureAwait(false))
            {
                await MemberAsync(xml, feature).ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Writes one feature as the whole document.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="feature">The feature.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>The <c>GetFeatureById</c> stored query answers with the feature, not with a
    /// collection containing it</b> — WFS 2.0 §7.9.3.6. This surface wrapped it, so a
    /// client looking for the requested <c>gml:id</c> on the root element found nothing:
    /// CITE's <c>invokeGetFeatureById</c> reports *expected [look_buildings.1] but found
    /// []*, which is a document that is well formed, contains the right feature, and
    /// cannot be read by the operation that asked for it.
    /// </para>
    /// <para>
    /// <b>The namespaces and the schema hint move to the feature element</b>, because
    /// the element that was carrying them is gone and a feature document nobody can
    /// resolve is no better than a collection nobody expected.
    /// </para>
    /// </remarks>
    public async Task WriteFeatureAsync(
        Stream stream, Feature feature, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(feature);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync(WfsNames.Prefix, _type.Name, WfsNames.Namespace)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "gml", null, WfsNames.Gml)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "xsi", null, WfsNames.Xsi)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xsi", "schemaLocation", WfsNames.Xsi, Hint())
                .ConfigureAwait(false);

            await BodyAsync(xml, feature).ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    private string Hint() =>
        $"{WfsNames.Wfs} http://schemas.opengis.net/wfs/2.0/wfs.xsd "
        + $"{WfsNames.Namespace} {_endpoint}?service=WFS&version={WfsNames.Version}"
        + $"&request=DescribeFeatureType&typeNames={_type.QualifiedName}";

    private async Task MemberAsync(XmlWriter xml, Feature feature)
    {
        await xml.WriteStartElementAsync("wfs", "member", WfsNames.Wfs).ConfigureAwait(false);

        await xml.WriteStartElementAsync(WfsNames.Prefix, _type.Name, WfsNames.Namespace)
            .ConfigureAwait(false);

        await BodyAsync(xml, feature).ConfigureAwait(false);

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One feature's identifier, properties and geometry, inside an element already open.
    /// </summary>
    /// <remarks>
    /// <b>Shared between the collection and the single-feature document</b>, because two
    /// writers for one feature is two places for `xsi:nil` and the geometry identifier to
    /// drift, and the second would only be exercised by the one operation nobody tests
    /// by hand.
    /// </remarks>
    private async Task BodyAsync(XmlWriter xml, Feature feature)
    {
        string gmlId = _type.GmlIdOf(feature.Id);

        await xml.WriteAttributeStringAsync("gml", "id", WfsNames.Gml, gmlId).ConfigureAwait(false);

        for (int i = 0; i < feature.Schema.Count; i++)
        {
            string name = feature.Schema.Names[i];

            await xml.WriteStartElementAsync(WfsNames.Prefix, name, WfsNames.Namespace)
                .ConfigureAwait(false);

            if (feature[i] is { } value)
            {
                await xml.WriteStringAsync(Text(value)).ConfigureAwait(false);
            }
            else
            {
                await xml.WriteAttributeStringAsync("xsi", "nil", WfsNames.Xsi, "true")
                    .ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
        }

        await xml.WriteStartElementAsync(
            WfsNames.Prefix, _type.GeometryProperty, WfsNames.Namespace).ConfigureAwait(false);

        if (feature.Geometry is { } geometry)
        {
            await _geometry.WriteAsync(xml, geometry, $"{gmlId}.geom").ConfigureAwait(false);
        }
        else
        {
            await xml.WriteAttributeStringAsync("xsi", "nil", WfsNames.Xsi, "true")
                .ConfigureAwait(false);
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A value as XML text.
    /// </summary>
    /// <remarks>
    /// <b>Invariant throughout, and dates in the one format XML Schema
    /// defines.</b> A decimal comma or a local date format produces a document
    /// that parses everywhere and means something different in half of those
    /// places, which is the worst kind of interoperability failure because
    /// nothing reports it.
    /// </remarks>
    /// <param name="value">The attribute value.</param>
    /// <returns>Its text.</returns>
    public static string Text(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        DateTime moment => moment.ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
