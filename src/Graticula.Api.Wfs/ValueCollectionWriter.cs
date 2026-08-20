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
/// Writes a <c>wfs:ValueCollection</c>, which is what GetPropertyValue returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this operation exists here at all.</b> ADR-039 §5 left GetPropertyValue
/// out, and the capabilities declared <c>ImplementsBasicWFS</c> TRUE — and Basic
/// WFS requires it. The OGC conformance suite said so in as many words. The choice
/// was to shrink the declaration or to make it true; the declaration is what tells
/// a client it may send a filter at all, and this server does answer filters, so
/// shrinking it would hide a capability rather than correct a claim.
/// </para>
/// <para>
/// <b>It is a projection of the same query, not a second query path.</b> The
/// caller names one property; the engine reads that property and the identity, and
/// each value is written as its own member. Nothing about matching, paging or
/// filtering differs from GetFeature, which is why none of it is repeated.
/// </para>
/// </remarks>
public sealed class ValueCollectionWriter
{
    private readonly WfsFeatureType _type;
    private readonly GmlGeometryWriter _geometry;
    private readonly string _property;
    private readonly bool _isGeometry;
    private readonly bool _isIdentifier;

    /// <summary>Creates a writer for one property of one feature type.</summary>
    /// <param name="type">The type being read.</param>
    /// <param name="property">The property whose values are wanted.</param>
    /// <param name="isGeometry">Whether that property is the geometry.</param>
    /// <param name="isIdentifier">Whether the caller asked for <c>@gml:id</c>.</param>
    /// <param name="outputSrid">The reference geometry will be written in.</param>
    public ValueCollectionWriter(
        WfsFeatureType type, string property, bool isGeometry, bool isIdentifier, int outputSrid)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);

        _type = type;
        _property = property;
        _isGeometry = isGeometry;
        _isIdentifier = isIdentifier;
        _geometry = new GmlGeometryWriter(outputSrid);
    }

    /// <summary>Writes the collection.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="features">The page, read as it is written.</param>
    /// <param name="numberMatched">How many match, or null for unknown.</param>
    /// <param name="numberReturned">How many this page holds.</param>
    /// <param name="timestamp">When the response was produced.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<Feature> features,
        long? numberMatched,
        long numberReturned,
        DateTimeOffset timestamp,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(features);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("wfs", "ValueCollection", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "gml", null, WfsNames.Gml)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "xsi", null, WfsNames.Xsi)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", WfsNames.Prefix, null, WfsNames.Namespace)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "timeStamp",
                    null,
                    timestamp.UtcDateTime.ToString(
                        "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
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

            await foreach (Feature feature in features.WithCancellation(cancellation)
                .ConfigureAwait(false))
            {
                await MemberAsync(xml, feature).ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task MemberAsync(XmlWriter xml, Feature feature)
    {
        await xml.WriteStartElementAsync("wfs", "member", WfsNames.Wfs).ConfigureAwait(false);

        // <b>An attribute has no element of its own.</b> A client asking for
        // @gml:id wants the identifiers, so the member carries the value itself
        // rather than wrapping it in a property element that does not exist in the
        // schema.
        if (_isIdentifier)
        {
            await xml.WriteStringAsync(_type.GmlIdOf(feature.Id)).ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
            return;
        }

        await xml.WriteStartElementAsync(WfsNames.Prefix, _property, WfsNames.Namespace)
            .ConfigureAwait(false);

        if (_isGeometry)
        {
            if (feature.Geometry is { } shape)
            {
                await _geometry
                    .WriteAsync(xml, shape, $"{_type.GmlIdOf(feature.Id)}.geom")
                    .ConfigureAwait(false);
            }
            else
            {
                await xml.WriteAttributeStringAsync("xsi", "nil", WfsNames.Xsi, "true")
                    .ConfigureAwait(false);
            }
        }
        else
        {
            int index = feature.Schema.IndexOf(_property);

            if (index >= 0 && feature[index] is { } value)
            {
                await xml.WriteStringAsync(GmlFeatureCollectionWriter.Text(value))
                    .ConfigureAwait(false);
            }
            else
            {
                await xml.WriteAttributeStringAsync("xsi", "nil", WfsNames.Xsi, "true")
                    .ConfigureAwait(false);
            }
        }

        await xml.WriteEndElementAsync().ConfigureAwait(false);
        await xml.WriteEndElementAsync().ConfigureAwait(false);
    }
}
