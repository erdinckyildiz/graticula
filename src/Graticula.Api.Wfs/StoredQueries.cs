using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Graticula.Api.Wfs;

/// <summary>
/// The stored queries this server offers, which is one.
/// </summary>
/// <remarks>
/// <b><c>GetFeatureById</c> is not optional and that is why it is here.</b> WFS
/// 2.0's <em>Simple WFS</em> conformance class requires <c>ListStoredQueries</c>,
/// <c>DescribeStoredQueries</c> and this one query — a server without them is not
/// a conforming WFS at any level, whatever else it implements. Managing stored
/// queries (creating and dropping them) is a separate conformance class and is
/// declared FALSE in the capabilities.
/// </remarks>
public static class StoredQueries
{
    /// <summary>Writes the list of stored queries.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="types">The feature types this caller may see.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteListAsync(
        Stream stream, IReadOnlyList<WfsFeatureType> types, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(types);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("wfs", "ListStoredQueriesResponse", WfsNames.Wfs)
                .ConfigureAwait(false);

            await Prefixes(xml).ConfigureAwait(false);

            await xml.WriteStartElementAsync("wfs", "StoredQuery", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                null, "id", null, WfsRequest.GetFeatureByIdQuery).ConfigureAwait(false);

            await xml.WriteElementStringAsync(
                "wfs", "Title", WfsNames.Wfs, "Get feature by identifier").ConfigureAwait(false);

            foreach (WfsFeatureType type in types)
            {
                await xml.WriteElementStringAsync(
                    "wfs", "ReturnFeatureType", WfsNames.Wfs, type.QualifiedName)
                    .ConfigureAwait(false);
            }

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>Writes the description of the stored queries.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="types">The feature types this caller may see.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteDescriptionAsync(
        Stream stream, IReadOnlyList<WfsFeatureType> types, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(types);

        XmlWriter xml = XmlWriter.Create(stream, SafeXml.WriterSettings);

        await using (xml.ConfigureAwait(false))
        {
            await xml.WriteStartElementAsync("wfs", "DescribeStoredQueriesResponse", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync("xmlns", "xsd", null, WfsNames.Xsd)
                .ConfigureAwait(false);

            await Prefixes(xml).ConfigureAwait(false);

            await xml.WriteStartElementAsync("wfs", "StoredQueryDescription", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                null, "id", null, WfsRequest.GetFeatureByIdQuery).ConfigureAwait(false);

            await xml.WriteElementStringAsync(
                "wfs", "Title", WfsNames.Wfs, "Get feature by identifier").ConfigureAwait(false);

            await xml.WriteElementStringAsync(
                    "wfs",
                    "Abstract",
                    WfsNames.Wfs,
                    "Returns the single feature whose gml:id is given. The identifier is the "
                    + "feature type's local name, a dot, and the feature's own identity — the "
                    + "same string this server writes as gml:id.")
                .ConfigureAwait(false);

            await xml.WriteStartElementAsync("wfs", "Parameter", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "name", null, "id").ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "type", null, "xsd:string")
                .ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);

            await xml.WriteStartElementAsync("wfs", "QueryExpressionText", WfsNames.Wfs)
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(null, "isPrivate", null, "true")
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "language",
                    null,
                    "urn:ogc:def:queryLanguage:OGC-WFS::WFS_QueryExpression")
                .ConfigureAwait(false);

            await xml.WriteAttributeStringAsync(
                    null,
                    "returnFeatureTypes",
                    null,
                    string.Join(' ', Names(types)))
                .ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);

            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.WriteEndElementAsync().ConfigureAwait(false);
            await xml.FlushAsync().ConfigureAwait(false);
        }

        cancellation.ThrowIfCancellationRequested();
    }

    private static IEnumerable<string> Names(IReadOnlyList<WfsFeatureType> types)
    {
        foreach (WfsFeatureType type in types)
        {
            yield return type.QualifiedName;
        }
    }

    private static async Task Prefixes(XmlWriter xml)
    {
        await xml.WriteAttributeStringAsync(
            "xmlns", WfsNames.Prefix, null, WfsNames.Namespace).ConfigureAwait(false);
    }
}
