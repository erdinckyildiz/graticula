using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;

namespace GisServer.Api.ArcGis;

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

    /// <summary>Creates a writer for one layer.</summary>
    public FeatureServerQueryWriter(LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

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

        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", _layer.ObjectIdColumn);
        writer.WriteString("globalIdFieldName", string.Empty);
        writer.WriteString("geometryType", ArcGisGeometryWriter.TypeName(geometryType));

        writer.WriteStartObject("spatialReference");
        writer.WriteNumber("wkid", _layer.Srid);
        writer.WriteNumber("latestWkid", _layer.Srid);
        writer.WriteEndObject();

        // Field types are not known until a value has been seen, and the header
        // must be written first. Declaring them from the schema alone means
        // every field is a string until the catalogue records column types —
        // honest, and narrower than it will be. Recorded rather than hidden.
        writer.WriteStartArray("fields");
        WriteField(writer, _layer.ObjectIdColumn!, "esriFieldTypeOID");
        foreach (string name in schema.Names)
        {
            WriteField(writer, name, "esriFieldTypeString");
        }

        writer.WriteEndArray();

        writer.WriteStartArray("features");

        int written = 0;
        await foreach (Feature feature in source.ReadAsync(query, cancellationToken)
            .ConfigureAwait(false))
        {
            WriteFeature(writer, feature, schema);
            written++;
        }

        writer.WriteEndArray();

        // ArcGIS clients page on this flag. Reporting it from the limit rather
        // than from a count means we never have to buffer to find out — and a
        // full page is the only honest signal we have without asking the
        // database a second question.
        writer.WriteBoolean("exceededTransferLimit", written >= query.Limit);
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private void WriteFeature(Utf8JsonWriter writer, Feature feature, FeatureSchema schema)
    {
        writer.WriteStartObject();

        writer.WriteStartObject("attributes");
        WriteAttribute(writer, _layer.ObjectIdColumn!, feature.Id);
        for (int i = 0; i < schema.Count; i++)
        {
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
