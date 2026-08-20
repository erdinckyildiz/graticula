using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Api.Wfs;

/// <summary>
/// Writes a feature collection as GeoJSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always WGS 84, because that is what GeoJSON is.</b> RFC 7946 removed the
/// <c>crs</c> member and pins coordinates to WGS 84 longitude/latitude. This
/// repository has already taken that position once, on the other side of the
/// wire: the import path refuses a GeoJSON file whose features are not in 4326,
/// and ADR-038 §4B records that the guard was right when it fired. Writing a
/// national grid's easting and northing into a GeoJSON document would be the same
/// error with the arrow reversed — nothing would report it, and every reader
/// would place the features in the Gulf of Guinea.
/// </para>
/// <para>
/// So the host reprojects to 4326 for this format. A request that asks for
/// GeoJSON <em>and</em> another reference is refused rather than served in one of
/// the two, which is the only honest answer when the two cannot both be true.
/// </para>
/// <para>
/// <b>Longitude first, always.</b> GeoJSON has no axis-order question — the
/// specification fixes the order — so none of <see cref="WfsNames.IsLatitudeFirst"/>
/// applies here. That asymmetry with the GML writer is the specification's, not
/// ours.
/// </para>
/// </remarks>
public sealed class GeoJsonFeatureCollectionWriter
{
    /// <summary>The only reference GeoJSON is written in.</summary>
    public const int Srid = 4326;

    private readonly WfsFeatureType _type;

    /// <summary>Creates a writer for one feature type.</summary>
    /// <param name="type">The type being written.</param>
    public GeoJsonFeatureCollectionWriter(WfsFeatureType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        _type = type;
    }

    /// <summary>Writes the collection.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="features">The page of features, read as they are written.</param>
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

        Utf8JsonWriter json = new(stream);

        await using (json.ConfigureAwait(false))
        {
            json.WriteStartObject();
            json.WriteString("type", "FeatureCollection");

            json.WriteString(
                "timeStamp",
                timestamp.UtcDateTime.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            if (numberMatched is { } matched)
            {
                json.WriteNumber("numberMatched", matched);
            }
            else
            {
                json.WriteString("numberMatched", "unknown");
            }

            json.WriteNumber("numberReturned", numberReturned);

            json.WriteStartArray("features");

            await foreach (Feature feature in features.WithCancellation(cancellation)
                .ConfigureAwait(false))
            {
                Write(json, feature);

                // Flushed per feature so a large page leaves the process in
                // pieces rather than as one buffer. The same reasoning as the
                // ArcGIS writer: A-037 makes the peak the thing to bound.
                if (json.BytesPending > 16 * 1024)
                {
                    await json.FlushAsync(cancellation).ConfigureAwait(false);
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();

            await json.FlushAsync(cancellation).ConfigureAwait(false);
        }
    }

    private void Write(Utf8JsonWriter json, Feature feature)
    {
        json.WriteStartObject();
        json.WriteString("type", "Feature");
        json.WriteString("id", _type.GmlIdOf(feature.Id));

        json.WritePropertyName("geometry");

        if (feature.Geometry is { } geometry)
        {
            WriteGeometry(json, geometry);
        }
        else
        {
            json.WriteNullValue();
        }

        json.WriteStartObject("properties");

        for (int i = 0; i < feature.Schema.Count; i++)
        {
            json.WritePropertyName(feature.Schema.Names[i]);
            WriteValue(json, feature[i]);
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;

            case bool flag:
                json.WriteBooleanValue(flag);
                break;

            case byte or sbyte or short or ushort or int or uint:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;

            case long whole:
                // <b>Written as a number and not as text, deliberately, and the
                // risk is recorded rather than avoided.</b> FieldType.BigInteger's
                // own remark notes that JavaScript loses integer precision above
                // 2^53 — but a GeoJSON reader that is not JavaScript expects a
                // number, and quoting every long would make ordinary identifiers
                // strings for every client to work around. The surface that must
                // choose the other way is a browser API, and this is not one.
                json.WriteNumberValue(whole);
                break;

            case float or double or decimal:
                json.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;

            case DateTime moment:
                json.WriteStringValue(moment.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                break;

            case DateTimeOffset moment:
                json.WriteStringValue(moment.UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                break;

            case byte[] bytes:
                json.WriteBase64StringValue(bytes);
                break;

            default:
                json.WriteStringValue(GmlFeatureCollectionWriter.Text(value));
                break;
        }
    }

    private static void WriteGeometry(Utf8JsonWriter json, Geometry geometry)
    {
        json.WriteStartObject();

        switch (geometry)
        {
            case Point point:
                json.WriteString("type", "Point");
                json.WriteStartArray("coordinates");

                if (!point.IsEmpty)
                {
                    json.WriteNumberValue(point.X);
                    json.WriteNumberValue(point.Y);
                }

                json.WriteEndArray();
                break;

            case LineString line:
                json.WriteString("type", "LineString");
                json.WriteStartArray("coordinates");
                WritePositions(json, line.Coordinates);
                json.WriteEndArray();
                break;

            case Polygon polygon:
                json.WriteString("type", "Polygon");
                json.WriteStartArray("coordinates");
                WriteRings(json, polygon);
                json.WriteEndArray();
                break;

            case MultiPoint points:
                json.WriteString("type", "MultiPoint");
                json.WriteStartArray("coordinates");

                foreach (Point part in points.Parts)
                {
                    json.WriteStartArray();

                    if (!part.IsEmpty)
                    {
                        json.WriteNumberValue(part.X);
                        json.WriteNumberValue(part.Y);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndArray();
                break;

            case MultiLineString lines:
                json.WriteString("type", "MultiLineString");
                json.WriteStartArray("coordinates");

                foreach (LineString part in lines.Parts)
                {
                    json.WriteStartArray();
                    WritePositions(json, part.Coordinates);
                    json.WriteEndArray();
                }

                json.WriteEndArray();
                break;

            case MultiPolygon polygons:
                json.WriteString("type", "MultiPolygon");
                json.WriteStartArray("coordinates");

                foreach (Polygon part in polygons.Parts)
                {
                    json.WriteStartArray();
                    WriteRings(json, part);
                    json.WriteEndArray();
                }

                json.WriteEndArray();
                break;

            default:
                throw new NotSupportedException(
                    $"'{geometry.Kind}' is not a geometry this surface can write as GeoJSON.");
        }

        json.WriteEndObject();
    }

    private static void WriteRings(Utf8JsonWriter json, Polygon polygon)
    {
        json.WriteStartArray();
        WritePositions(json, polygon.Shell.Coordinates);
        json.WriteEndArray();

        foreach (LinearRing hole in polygon.Holes)
        {
            json.WriteStartArray();
            WritePositions(json, hole.Coordinates);
            json.WriteEndArray();
        }
    }

    private static void WritePositions(Utf8JsonWriter json, XySequence coordinates)
    {
        for (int i = 0; i < coordinates.Count; i++)
        {
            json.WriteStartArray();
            json.WriteNumberValue(coordinates.X(i));
            json.WriteNumberValue(coordinates.Y(i));
            json.WriteEndArray();
        }
    }
}
