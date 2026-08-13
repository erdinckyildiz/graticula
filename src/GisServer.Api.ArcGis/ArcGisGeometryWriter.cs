using System;
using System.Collections.Generic;
using System.Text.Json;
using GisServer.Geometries;

namespace GisServer.Api.ArcGis;

/// <summary>
/// Writes our Simple Features geometry as ArcGIS REST JSON.
/// </summary>
/// <remarks>
/// <para>
/// The conversion ADR-005 §3.3c specifies. The domain speaks Simple Features
/// because Esri's model is lossier — a <c>Polyline</c> conflates one path with
/// many, and a <c>Polygon</c> is a flat bag of rings where winding order carries
/// the part structure. Going this direction is mechanical; going back needs ring
/// classification, which is why the domain does not store the flat form.
/// </para>
/// <para>
/// <b>Winding is normalised here, and it is the part that goes wrong quietly.</b>
/// ArcGIS requires exterior rings clockwise and interior rings
/// counter-clockwise. PostGIS guarantees neither — a table can hold either
/// orientation, and OSM data does. A polygon written with the wrong sense
/// renders as a hole, and no error is raised anywhere.
/// </para>
/// </remarks>
public static class ArcGisGeometryWriter
{
    /// <summary>The ArcGIS type name for a geometry, for <c>geometryType</c>.</summary>
    /// <remarks>
    /// Note that two of ours collapse onto one of theirs in each of two cases.
    /// That is the loss described above, and it is why the domain keeps them
    /// apart.
    /// </remarks>
    public static string TypeName(GeometryKind kind) => kind switch
    {
        GeometryKind.Point => "esriGeometryPoint",
        GeometryKind.MultiPoint => "esriGeometryMultipoint",
        GeometryKind.LineString or GeometryKind.MultiLineString => "esriGeometryPolyline",
        GeometryKind.Polygon or GeometryKind.MultiPolygon => "esriGeometryPolygon",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No ArcGIS equivalent."),
    };

    /// <summary>
    /// Writes <paramref name="geometry"/> as an ArcGIS geometry object.
    /// </summary>
    /// <param name="writer">The writer, positioned where a value is expected.</param>
    /// <param name="geometry">The geometry.</param>
    /// <param name="wkid">
    /// The layer's spatial reference. Carried on the geometry in ArcGIS JSON even
    /// though our model keeps it on the layer, because their clients expect it
    /// per shape.
    /// </param>
    public static void Write(Utf8JsonWriter writer, Geometry geometry, int wkid)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);

        writer.WriteStartObject();

        switch (geometry)
        {
            case Point point:
                WritePointBody(writer, point);
                break;

            case MultiPoint multiPoint:
                writer.WriteStartArray("points");
                foreach (Point part in multiPoint.Parts)
                {
                    WriteCoordinate(writer, part.X, part.Y);
                }

                writer.WriteEndArray();
                break;

            case MultiLineString multiLine:
                writer.WriteStartArray("paths");
                foreach (LineString part in multiLine.Parts)
                {
                    WriteSequence(writer, part.Coordinates, reversed: false);
                }

                writer.WriteEndArray();
                break;

            case LineString line:
                writer.WriteStartArray("paths");
                WriteSequence(writer, line.Coordinates, reversed: false);
                writer.WriteEndArray();
                break;

            case MultiPolygon multiPolygon:
                writer.WriteStartArray("rings");
                foreach (Polygon part in multiPolygon.Parts)
                {
                    WritePolygonRings(writer, part);
                }

                writer.WriteEndArray();
                break;

            case Polygon polygon:
                writer.WriteStartArray("rings");
                WritePolygonRings(writer, polygon);
                writer.WriteEndArray();
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(geometry), geometry.Kind, "No ArcGIS equivalent.");
        }

        writer.WriteStartObject("spatialReference");
        writer.WriteNumber("wkid", wkid);
        writer.WriteNumber("latestWkid", wkid);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WritePointBody(Utf8JsonWriter writer, Point point)
    {
        if (point.IsEmpty)
        {
            // ArcGIS represents an empty point as nulls rather than omitting the
            // fields, and a client that sees neither x nor y treats the geometry
            // as malformed rather than absent.
            writer.WriteNull("x");
            writer.WriteNull("y");
            return;
        }

        writer.WriteNumber("x", point.X);
        writer.WriteNumber("y", point.Y);
    }

    /// <summary>
    /// Writes a polygon's rings with ArcGIS winding: shell clockwise, holes
    /// counter-clockwise.
    /// </summary>
    private static void WritePolygonRings(Utf8JsonWriter writer, Polygon polygon)
    {
        // Our IsCounterClockwise is pinned against PostGIS's ST_IsPolygonCCW,
        // so "reverse when it disagrees with what ArcGIS wants" is a check
        // against a verified reference rather than against an assumption.
        WriteSequence(writer, polygon.Shell.Coordinates, reversed: polygon.Shell.IsCounterClockwise);

        foreach (LinearRing hole in polygon.Holes)
        {
            WriteSequence(writer, hole.Coordinates, reversed: !hole.IsCounterClockwise);
        }
    }

    private static void WriteSequence(Utf8JsonWriter writer, XySequence coordinates, bool reversed)
    {
        writer.WriteStartArray();

        if (reversed)
        {
            // Reversed in place rather than by building a reversed ring. A
            // 215,488-vertex polygon exists in the corpus, and copying it to
            // change the order of writing would be the allocation A-037 warns
            // about, incurred for nothing.
            for (int i = coordinates.Count - 1; i >= 0; i--)
            {
                WriteCoordinate(writer, coordinates.X(i), coordinates.Y(i));
            }
        }
        else
        {
            for (int i = 0; i < coordinates.Count; i++)
            {
                WriteCoordinate(writer, coordinates.X(i), coordinates.Y(i));
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteCoordinate(Utf8JsonWriter writer, double x, double y)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(x);
        writer.WriteNumberValue(y);
        writer.WriteEndArray();
    }
}
