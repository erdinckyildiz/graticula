using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis;

/// <summary>
/// Writes an ArcGIS FeatureServer <c>query</c> response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streams.</b> The header is written from the layer definition and the query
/// schema, both known before the first row arrives, and features are written as
/// they come. Nothing is materialised — A-037 measured allocation as the binding
/// constraint, and buffering a result to count it before writing would double the
/// peak for a number the client does not need.
/// </para>
/// <para>
/// That is why <see cref="IFeatureSource.SchemaFor"/> exists separately from
/// reading: a streaming response has to commit to its field list before it has
/// seen a row.
/// </para>
/// </remarks>
public sealed class FeatureServerQueryWriter
{
    private readonly LayerDefinition _layer;

    /// <summary>
    /// The most bytes a response body may reach before it is truncated, or 0 for
    /// no ceiling.
    /// </summary>
    /// <remarks>
    /// <b>A ceiling on the body, not on the row count</b> — Q-113. Zero means unset
    /// and is the behaviour of every build before this one, which is what makes the
    /// change additive.
    /// </remarks>
    private readonly long _maximumBytes;

    /// <summary>Creates a writer for one layer.</summary>
    /// <param name="layer">The layer being written.</param>
    /// <param name="maximumBytes">
    /// The most bytes the body may reach before truncation, or 0 for no ceiling.
    /// Truncation is reported as <c>exceededTransferLimit</c> — see the loop in
    /// <see cref="WriteAsync"/> for why that rather than an error.
    /// </param>
    public FeatureServerQueryWriter(LayerDefinition layer, long maximumBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        if (!layer.IsArcGisServable)
        {
            // ADR-013 §2a. OGC API Features accepts a string id; ArcGIS requires
            // a unique integer. Refusing here, by construction, means the
            // capability report and the endpoint cannot disagree.
            throw new ArgumentException(
                $"Layer '{layer.Name}' has no integer object-id column, so it cannot be served "
                + "through the ArcGIS surface (ADR-013 §2a). It remains servable natively. The "
                + "capability report states this rather than leaving a request to discover it.",
                nameof(layer));
        }

        _layer = layer;
        _maximumBytes = maximumBytes;
    }

    /// <summary>
    /// Writes the whole response, reading features as it goes.
    /// </summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="source">Where features come from.</param>
    /// <param name="query">What to read.</param>
    /// <param name="geometryType">
    /// The layer's declared geometry type. Declared rather than inferred from the
    /// first feature, because ArcGIS puts it in the header — before any row has
    /// been seen — and a layer with no matching rows still has a type.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many features were written.</returns>
    public async Task<int> WriteAsync(
        Utf8JsonWriter writer,
        IFeatureSource source,
        FeatureQuery query,
        GeometryKind geometryType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        FeatureSchema schema = source.SchemaFor(query);

        // The object id is fetched as a field and re-emitted as the OID rather
        // than taken from Feature.Id, because the two are not the same thing.
        // Q-57's declared identity may be a uuid or text; ADR-013 §2a's ArcGIS
        // object id must be a unique integer. Where a layer has both, they can
        // be different columns.
        int objectIdIndex = schema.IndexOf(_layer.ObjectIdColumn!);

        if (objectIdIndex < 0)
        {
            throw new ArgumentException(
                $"The query must request '{_layer.ObjectIdColumn}' so it can be written as the "
                + "object id. An ArcGIS response whose objectIdFieldName names a field the "
                + "response does not contain is one a client cannot page or select against.",
                nameof(query));
        }

        // The first row is pulled before a single byte is written, and this
        // ordering is load-bearing rather than tidy. Executing the query is what
        // raises the failures that matter — the statement timeout, a dropped
        // table, a credential that no longer works — and once the header has
        // been handed to the response's PipeWriter it cannot be taken back.
        // Response.Clear() resets the status and the headers; it does not
        // discard buffered body bytes. Writing the header first therefore
        // produced a 504 whose body was a truncated object with an error
        // appended to it: the right status carrying malformed JSON. Measured,
        // not reasoned about.
        //
        // A failure after this point still truncates. That case is narrower —
        // the database died mid-result — and is handled by aborting rather than
        // by pretending a clean response is still possible.
        IAsyncEnumerator<Feature> features =
            source.ReadAsync(query, cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            bool hasFirst = await features.MoveNextAsync().ConfigureAwait(false);

            return await WriteBodyAsync(writer, features, hasFirst, schema, objectIdIndex, query, geometryType, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await features.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> WriteBodyAsync(
        Utf8JsonWriter writer,
        IAsyncEnumerator<Feature> features,
        bool hasFirst,
        FeatureSchema schema,
        int objectIdIndex,
        FeatureQuery query,
        GeometryKind geometryType,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", _layer.ObjectIdColumn);
        writer.WriteString("globalIdFieldName", string.Empty);
        writer.WriteString("geometryType", ArcGisGeometryWriter.TypeName(geometryType));

        // <b>The reference the geometry is actually in, which is outSR when one
        // was asked for.</b> Reporting the layer's after transforming would
        // label metres as degrees, and a client would place the features
        // somewhere in the Gulf of Guinea without any error to explain it.
        int srid = query.OutSrid ?? _layer.Srid;

        writer.WriteStartObject("spatialReference");
        writer.WriteNumber("wkid", srid);
        writer.WriteNumber("latestWkid", srid);
        writer.WriteEndObject();

        // Field types are not known until a value has been seen, and the header
        // must be written first. Declaring them from the schema alone means
        // every field is a string until the catalogue records column types —
        // honest, and narrower than it will be. Recorded rather than hidden.
        writer.WriteStartArray("fields");
        foreach (string name in schema.Names)
        {
            WriteField(
                writer,
                name,
                string.Equals(name, _layer.ObjectIdColumn, StringComparison.Ordinal)
                    ? "esriFieldTypeOID"
                    : "esriFieldTypeString");
        }

        writer.WriteEndArray();

        writer.WriteStartArray("features");

        int written = 0;

        // <b>The response has a size ceiling, and it is in bytes because bytes are
        // what the ceiling is for.</b> Q-113: `query.Limit` bounds rows and nothing
        // bounded their width, so a caller asking for every field of every feature
        // with full-precision geometry stayed inside every limit we had and could
        // still return hundreds of megabytes. A row is not a unit of cost — one
        // polygon can outweigh ten thousand points.
        //
        // <b>And it is reported in the protocol's own vocabulary rather than as a
        // new one.</b> ArcGIS clients already page on `exceededTransferLimit`; a
        // response truncated by size sets the same flag a response truncated by
        // row count sets, so every client that handles paging today handles this
        // with no change. An error would have been a new contract, and a silent
        // truncation would be the worst of the three.
        bool truncatedBySize = false;

        for (bool more = hasFirst; more; more = await features.MoveNextAsync().ConfigureAwait(false))
        {
            WriteFeature(writer, features.Current, schema, objectIdIndex);
            written++;

            // Checked after writing rather than before, so at least one feature is
            // always returned: a ceiling small enough to refuse the first feature
            // would produce an empty page with `exceededTransferLimit` true, which
            // is a paging loop that never advances.
            if (_maximumBytes > 0 && written > 0
                && writer.BytesCommitted + writer.BytesPending >= _maximumBytes)
            {
                truncatedBySize = true;
                break;
            }
        }

        writer.WriteEndArray();

        // ArcGIS clients page on this flag. Reporting it from the limit rather
        // than from a count means we never have to buffer to find out — and a
        // full page is the only honest signal we have without asking the
        // database a second question.
        writer.WriteBoolean("exceededTransferLimit", truncatedBySize || written >= query.Limit);
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private void WriteFeature(
        Utf8JsonWriter writer, Feature feature, FeatureSchema schema, int objectIdIndex)
    {
        writer.WriteStartObject();

        writer.WriteStartObject("attributes");
        for (int i = 0; i < schema.Count; i++)
        {
            if (i == objectIdIndex)
            {
                WriteObjectId(writer, schema.Names[i], feature[i]);
                continue;
            }

            WriteAttribute(writer, schema.Names[i], feature[i]);
        }

        writer.WriteEndObject();

        if (feature.Geometry is null || feature.Geometry.IsEmpty)
        {
            // A row with no shape keeps its attributes. Dropping the feature
            // would change the answer to a count; omitting the property is what
            // ArcGIS clients expect for an attribute-only row.
            writer.WriteEndObject();
            return;
        }

        writer.WritePropertyName("geometry");
        ArcGisGeometryWriter.Write(writer, feature.Geometry, _layer.Srid);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the object id as a JSON number.
    /// </summary>
    /// <remarks>
    /// <b>A number, never a string.</b> The field is declared
    /// <c>esriFieldTypeOID</c>, and a client that pages or selects by object id
    /// against a quoted value gets no matches and no error. Caught by running
    /// the endpoint rather than by any test — the first response emitted
    /// <c>"objectid":"1"</c> and looked plausible.
    /// </remarks>
    private static void WriteObjectId(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case int number:
                writer.WriteNumber(name, number);
                return;
            case long number:
                writer.WriteNumber(name, number);
                return;
            case short number:
                writer.WriteNumber(name, number);
                return;
            default:
                throw new InvalidOperationException(
                    $"The object id column '{name}' produced "
                    + $"{value?.GetType().Name ?? "null"}, which is not an integer. ADR-013 §2a "
                    + "requires a unique integer for the ArcGIS surface, and the catalogue "
                    + "recorded this column as one — so the registration and the table disagree.");
        }
    }

    private static void WriteAttribute(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string text:
                writer.WriteString(name, text);
                break;
            case bool flag:
                writer.WriteNumber(name, flag ? 1 : 0);
                break;
            case int number:
                writer.WriteNumber(name, number);
                break;
            case short number:
                writer.WriteNumber(name, number);
                break;
            case double number:
                writer.WriteNumber(name, number);
                break;
            case float number:
                writer.WriteNumber(name, number);
                break;
            case decimal number:
                writer.WriteNumber(name, number);
                break;

            // A 64-bit integer has no ArcGIS field type and JavaScript loses
            // precision above 2^53, so it goes out as text. Visibly lossy beats
            // invisibly wrong — an OSM id silently rounded is a bug nobody finds.
            case long number:
                writer.WriteString(name, number.ToString(CultureInfo.InvariantCulture));
                break;

            case DateTime timestamp:
                writer.WriteNumber(
                    name, new DateTimeOffset(timestamp.ToUniversalTime()).ToUnixTimeMilliseconds());
                break;
            case DateTimeOffset timestamp:
                writer.WriteNumber(name, timestamp.ToUnixTimeMilliseconds());
                break;

            default:
                writer.WriteString(name, value.ToString());
                break;
        }
    }

    private static void WriteField(Utf8JsonWriter writer, string name, string type)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("alias", name);
        writer.WriteEndObject();
    }
}
