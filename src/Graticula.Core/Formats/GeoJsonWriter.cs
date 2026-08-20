using System;
using System.Globalization;
using System.Text.Json;
using Graticula.Geometries;

namespace Graticula.Formats;

/// <summary>
/// A geometry and an attribute value, as GeoJSON writes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extracted 2026-08-20, when a second caller asked for it.</b> This lived
/// privately inside the WFS collection writer and was correct there; OGC API
/// Features writes the same shapes, and a second copy of the ring-winding and
/// position rules is a second place for them to be right. That is the same seam
/// [ADR-008](../../../docs/adr/ADR-008-query-engine.md)'s predicate emitter got when
/// WFS became its second caller, arrived at from the same direction.
/// </para>
/// <para>
/// <b>The counterpart of <see cref="GeoJsonGeometry"/>, which only reads.</b> The
/// two directions had lived in different projects since the import path was built,
/// which meant nothing checked that this server writes what it will read back.
/// </para>
/// <para>
/// <b>RFC 7946: coordinates are longitude then latitude, always.</b> GeoJSON has no
/// axis-order question and no CRS member — a document is WGS 84 in longitude/latitude
/// order or it is not GeoJSON. OGC API Features Part 2 answers the *other* reference
/// systems with a <c>Content-Crs</c> header and a <c>crs</c> parameter rather than by
/// putting a CRS inside the document, which is the same decision the RFC made.
/// </para>
/// </remarks>
public static class GeoJsonWriter
{
    /// <summary>The only reference system a plain GeoJSON document is in.</summary>
    public const int Srid = 4326;

    /// <summary>
    /// Writes a geometry as a GeoJSON geometry object.
    /// </summary>
    /// <param name="json">Where to write.</param>
    /// <param name="geometry">The geometry, already in the document's CRS.</param>
    /// <exception cref="NotSupportedException">A geometry kind with no GeoJSON shape.</exception>
    public static void WriteGeometry(Utf8JsonWriter json, Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(geometry);

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
                    $"'{geometry.Kind}' is not a geometry this server can write as GeoJSON.");
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// Writes one attribute value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These rules were the WFS face's and are now both faces'.</b> Two surfaces
    /// of one server that serialised the same column differently would be a
    /// difference a client finds and nobody meant — and the choices below each carry
    /// a reason, so re-deciding them per face is how one of them ends up wrong.
    /// </para>
    /// <para>
    /// <b>A <c>long</c> is written as a number and the risk is recorded rather than
    /// avoided.</b> <c>FieldType.BigInteger</c>'s own remark notes that JavaScript
    /// loses integer precision above 2^53 — but a GeoJSON reader that is not
    /// JavaScript expects a number, and quoting every long would make ordinary
    /// identifiers strings for every client to work around. The surface that must
    /// choose the other way is a browser API, and neither of these is one.
    /// </para>
    /// <para>
    /// <b>Timestamps are normalised to UTC with a <c>Z</c>.</b> A timestamp written in
    /// the server's own offset is one a client in another zone reads as a different
    /// instant, with no error anywhere.
    /// </para>
    /// </remarks>
    /// <param name="json">Where to write.</param>
    /// <param name="value">The value, as the provider returned it.</param>
    public static void WriteValue(Utf8JsonWriter json, object? value)
    {
        ArgumentNullException.ThrowIfNull(json);

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
                json.WriteNumberValue(whole);
                break;

            case float or double or decimal:
                json.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;

            case DateTime moment:
                json.WriteStringValue(moment.ToUniversalTime()
                    .ToString(Timestamp, CultureInfo.InvariantCulture));
                break;

            case DateTimeOffset moment:
                json.WriteStringValue(moment.UtcDateTime
                    .ToString(Timestamp, CultureInfo.InvariantCulture));
                break;

            case byte[] bytes:
                json.WriteBase64StringValue(bytes);
                break;

            default:
                json.WriteStringValue(Text(value));
                break;
        }
    }

    /// <summary>A value as invariant text, for anything with no JSON shape of its own.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>How a timestamp is written, in UTC.</summary>
    private const string Timestamp = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

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
