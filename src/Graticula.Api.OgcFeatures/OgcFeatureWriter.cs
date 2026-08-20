using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using Graticula.Features;
using Graticula.Formats;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// A feature collection and a single feature, as GeoJSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streamed, not built.</b> A collection is written to the response as features
/// arrive from the provider; holding a page of geometry in memory to serialise it
/// afterwards is an allocation proportional to the answer, and the answer is the
/// caller's choice.
/// </para>
/// <para>
/// <b><c>numberMatched</c> is optional and this server writes it.</b> §7.15.6 makes
/// it optional precisely because counting can cost as much as the page — it does
/// here, one extra query — and a client that cannot see the total cannot show a
/// pager or know when to stop following <c>next</c>. The cost is recorded in
/// [D-118](../../../docs/architecture-debt.md), which is the same complaint the WFS
/// face already carries.
/// </para>
/// </remarks>
public sealed class OgcFeatureWriter
{
    private readonly CollectionMetadata _collection;
    private readonly string _self;
    private readonly bool _latitudeFirst;

    /// <summary>Opens a writer for one collection.</summary>
    /// <param name="collection">The collection being written.</param>
    /// <param name="self">The absolute address of this collection's items.</param>
    /// <param name="latitudeFirst">
    /// Whether the negotiated reference system puts latitude before longitude.
    /// <b>Part 2 §6.4: once a CRS is negotiated the coordinates follow that CRS's own
    /// axis order</b>, and GeoJSON has nowhere to say which — the <c>Content-Crs</c>
    /// header is the only signal, so writing the wrong order under the right header
    /// is a wrong answer a client cannot detect.
    /// </param>
    public OgcFeatureWriter(CollectionMetadata collection, string self, bool latitudeFirst = false)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(self);

        _collection = collection;
        _self = self;
        _latitudeFirst = latitudeFirst;
    }

    /// <summary>
    /// Writes a page of features.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="features">The features, already in the response's CRS.</param>
    /// <param name="matched">How many the query matched in total, or null when unknown.</param>
    /// <param name="request">What was asked for, for the paging links.</param>
    /// <param name="query">The query string as sent, so links can preserve it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>How many features were written.</returns>
    public async System.Threading.Tasks.Task<int> WriteAsync(
        System.IO.Stream stream,
        IAsyncEnumerable<Feature> features,
        long? matched,
        OgcRequest request,
        IReadOnlyDictionary<string, string> query,
        System.Threading.CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        await using Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = false });

        json.WriteStartObject();
        json.WriteString("type", "FeatureCollection");
        json.WriteStartArray("features");

        int written = 0;

        await foreach (Feature feature in features.WithCancellation(cancellation).ConfigureAwait(false))
        {
            WriteFeature(json, feature, _latitudeFirst);
            written++;

            // Flushed as it goes, so a large page reaches the client while the rest
            // is still being read rather than after all of it has been buffered.
            if (json.BytesPending > 32 * 1024)
            {
                await json.FlushAsync(cancellation).ConfigureAwait(false);
            }
        }

        json.WriteEndArray();

        json.WriteNumber("numberReturned", written);

        if (matched is { } total)
        {
            json.WriteNumber("numberMatched", total);
        }

        json.WriteString(
            "timeStamp",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

        OgcDocuments.WriteLinks(json, Paging(request, query, written, matched));

        json.WriteEndObject();

        await json.FlushAsync(cancellation).ConfigureAwait(false);

        return written;
    }

    /// <summary>Writes one feature as a standalone GeoJSON document.</summary>
    /// <param name="feature">The feature, already in the response's CRS.</param>
    /// <param name="links">The links this document carries.</param>
    /// <returns>The JSON.</returns>
    /// <param name="latitudeFirst">Whether the negotiated CRS puts latitude first.</param>
    public static string WriteOne(
        Feature feature, IReadOnlyList<OgcDocuments.Link> links, bool latitudeFirst = false)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(links);

        using System.IO.MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            WriteFeatureBody(json, feature, latitudeFirst);
            OgcDocuments.WriteLinks(json, links);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFeature(Utf8JsonWriter json, Feature feature, bool latitudeFirst)
    {
        json.WriteStartObject();
        WriteFeatureBody(json, feature, latitudeFirst);
        json.WriteEndObject();
    }

    private static void WriteFeatureBody(
        Utf8JsonWriter json, Feature feature, bool latitudeFirst)
    {
        json.WriteString("type", "Feature");

        // <b>The bare identity, not the WFS `layer.id` form.</b> A feature's id here
        // is what `/collections/{id}/items/{featureId}` takes back, so prefixing it
        // with the collection would make the path carry the collection twice — and
        // the round trip is the only thing this member is for.
        json.WriteString("id", feature.Id);

        json.WritePropertyName("geometry");

        if (feature.Geometry is { } geometry)
        {
            GeoJsonWriter.WriteGeometry(json, geometry, latitudeFirst);
        }
        else
        {
            json.WriteNullValue();
        }

        json.WriteStartObject("properties");

        for (int i = 0; i < feature.Schema.Count; i++)
        {
            json.WritePropertyName(feature.Schema.Names[i]);
            GeoJsonWriter.WriteValue(json, feature[i]);
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// The paging links: self, and next where there is more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>next</c> is offered when the page came back full, even without a
    /// count.</b> A client following links stops when there is no <c>next</c>, so
    /// offering one that returns an empty page costs a request; never offering one
    /// after a full page loses data. The full page is the only evidence available
    /// when counting is off.
    /// </para>
    /// <para>
    /// <b><c>prev</c> is offered whenever the offset is not zero.</b> It is not
    /// required by Part 1 and it is what makes an HTML page navigable in both
    /// directions.
    /// </para>
    /// </remarks>
    private List<OgcDocuments.Link> Paging(
        OgcRequest request,
        IReadOnlyDictionary<string, string> query,
        int written,
        long? matched)
    {
        List<OgcDocuments.Link> links =
        [
            new OgcDocuments.Link(
                _self + Rebuild(query, request.Offset), "self", OgcNames.GeoJson,
                _collection.Title),
            new OgcDocuments.Link(
                _self + Rebuild(query, request.Offset, html: true), "alternate", OgcNames.Html,
                "This page as HTML"),
            new OgcDocuments.Link(
                _self[.._self.LastIndexOf("/items", StringComparison.Ordinal)],
                "collection", OgcNames.Json, _collection.Title),
        ];

        bool more = matched is { } total
            ? request.Offset + written < total
            : written == request.Limit;

        if (more)
        {
            links.Add(new OgcDocuments.Link(
                _self + Rebuild(query, request.Offset + request.Limit),
                "next", OgcNames.GeoJson, "Next page"));
        }

        if (request.Offset > 0)
        {
            links.Add(new OgcDocuments.Link(
                _self + Rebuild(query, Math.Max(0, request.Offset - request.Limit)),
                "prev", OgcNames.GeoJson, "Previous page"));
        }

        return links;
    }

    /// <summary>
    /// The query string again, with a new offset.
    /// </summary>
    /// <remarks>
    /// <b>Everything else is preserved.</b> A <c>next</c> link that dropped the
    /// caller's <c>bbox</c> would page through a different result set than the one
    /// they are reading, and the second page would look like data appearing from
    /// nowhere.
    /// </remarks>
    private static string Rebuild(
        IReadOnlyDictionary<string, string> query, int offset, bool html = false)
    {
        List<string> parts = [];

        foreach (KeyValuePair<string, string> pair in query)
        {
            if (string.Equals(pair.Key, "offset", StringComparison.Ordinal)
                || string.Equals(pair.Key, "f", StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        if (offset > 0)
        {
            parts.Add($"offset={offset.ToString(CultureInfo.InvariantCulture)}");
        }

        if (html)
        {
            parts.Add("f=html");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
