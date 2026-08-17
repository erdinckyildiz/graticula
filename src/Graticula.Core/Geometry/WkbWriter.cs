using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Graticula.Geometries;

/// <summary>
/// Writes geometry as ISO Well-Known Binary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Binary rather than text, and the reason is precision.</b> The obvious
/// alternative is to hand PostGIS a WKT string and let it parse — no writer
/// needed. But WKT round-trips a double through decimal text, and this is a
/// project that refuses silent loss on principle: ADR-008 §4.5a will not let a
/// client write back geometry that lost fidelity on read, and it would be
/// strange to enforce that while quietly shedding bits on the way out. WKB
/// carries the IEEE-754 bytes.
/// </para>
/// <para>
/// <b>Two dimensions only, matching the reader.</b> Our geometry model is
/// <see cref="XySequence"/>, which has no Z or M to write. That is not a
/// limitation being papered over — it is exactly why ADR-008 §4.5a exists, and
/// the write path refuses rather than flattening.
/// </para>
/// <para>
/// <b>Little-endian always.</b> WKB permits either per geometry and readers must
/// accept both — ours does — but a writer that chose by host architecture would
/// produce different bytes on different machines for the same geometry, which
/// makes every byte-comparison test a lie on one of them.
/// </para>
/// </remarks>
public static class WkbWriter
{
    private const byte LittleEndian = 1;

    /// <summary>How many bytes <see cref="Write"/> will produce.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <remarks>
    /// Computed rather than guessed so the caller can rent one buffer of the
    /// right size. A-037 measured allocation as the binding constraint, and a
    /// growing buffer on the write path would copy every geometry twice.
    /// </remarks>
    public static int SizeOf(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        // 1 byte order + 4 type, then the body.
        return 5 + BodySize(geometry);
    }

    /// <summary>Writes a geometry into a buffer.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="destination">At least <see cref="SizeOf"/> bytes.</param>
    /// <returns>How many bytes were written.</returns>
    public static int Write(Geometry geometry, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        int needed = SizeOf(geometry);

        if (destination.Length < needed)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} bytes and this geometry needs "
                + $"{needed}. Ask SizeOf first.",
                nameof(destination));
        }

        int written = WriteGeometry(geometry, destination);

        // A mismatch means SizeOf and Write disagree about the same geometry,
        // which would corrupt anything written after it in a shared buffer.
        // Cheap to check, and the alternative is a bug that presents as a
        // malformed geometry three layers away.
        if (written != needed)
        {
            throw new InvalidOperationException(
                $"WkbWriter wrote {written} bytes for a {geometry.Kind} and SizeOf said {needed}. "
                + "The two are out of step, which is a bug in this class.");
        }

        return written;
    }

    /// <summary>Writes a geometry into a new array.</summary>
    public static byte[] ToArray(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        byte[] buffer = new byte[SizeOf(geometry)];
        Write(geometry, buffer);
        return buffer;
    }

    private static int BodySize(Geometry geometry) => geometry switch
    {
        Point => 16,
        LineString line => 4 + (line.Coordinates.Count * 16),
        Polygon polygon => 4 + RingsSize(polygon),
        MultiPoint multi => 4 + Sum(multi.Parts),
        MultiLineString multi => 4 + Sum(multi.Parts),
        MultiPolygon multi => 4 + Sum(multi.Parts),
        _ => throw new NotSupportedException(
            $"{geometry.Kind} cannot be written as WKB by this writer."),
    };

    private static int RingsSize(Polygon polygon)
    {
        int size = 4 + (polygon.Shell.Coordinates.Count * 16);

        foreach (LinearRing hole in polygon.Holes)
        {
            size += 4 + (hole.Coordinates.Count * 16);
        }

        return size;
    }

    private static int Sum<T>(IReadOnlyList<T> parts)
        where T : Geometry
    {
        int size = 0;

        foreach (T part in parts)
        {
            size += 5 + BodySize(part);
        }

        return size;
    }

    private static int WriteGeometry(Geometry geometry, Span<byte> destination)
    {
        destination[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], TypeCodeOf(geometry));

        int at = 5;

        switch (geometry)
        {
            case Point point:
                at += WriteXy((point.X, point.Y), destination[at..]);
                break;

            case LineString line:
                at += WriteSequence(line.Coordinates, destination[at..]);
                break;

            case Polygon polygon:
                BinaryPrimitives.WriteUInt32LittleEndian(
                    destination[at..], (uint)(1 + polygon.Holes.Count));
                at += 4;
                at += WriteSequence(polygon.Shell.Coordinates, destination[at..]);

                foreach (LinearRing hole in polygon.Holes)
                {
                    at += WriteSequence(hole.Coordinates, destination[at..]);
                }

                break;

            case MultiPoint multi:
                at += WriteParts(multi.Parts, destination[at..]);
                break;

            case MultiLineString multi:
                at += WriteParts(multi.Parts, destination[at..]);
                break;

            case MultiPolygon multi:
                at += WriteParts(multi.Parts, destination[at..]);
                break;

            default:
                throw new NotSupportedException(
                    $"{geometry.Kind} cannot be written as WKB by this writer.");
        }

        return at;
    }

    private static int WriteParts<T>(IReadOnlyList<T> parts, Span<byte> destination)
        where T : Geometry
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)parts.Count);
        int at = 4;

        foreach (T part in parts)
        {
            // Each part carries its own byte order and type, which is what makes
            // a WKB collection self-describing rather than a bag of coordinates.
            at += WriteGeometry(part, destination[at..]);
        }

        return at;
    }

    private static int WriteSequence(XySequence points, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)points.Count);
        int at = 4;

        ReadOnlySpan<double> ordinates = points.AsSpan();

        for (int i = 0; i < ordinates.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(destination[at..], ordinates[i]);
            at += 8;
        }

        return at;
    }

    private static int WriteXy((double X, double Y) position, Span<byte> destination)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(destination, position.X);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[8..], position.Y);
        return 16;
    }

    /// <summary>The ISO WKB type code, 2D.</summary>
    /// <remarks>
    /// The plain codes, not the +1000 Z or +2000 M variants, because this writer
    /// has no Z or M to describe. A reader seeing these knows exactly what it is
    /// getting.
    /// </remarks>
    private static uint TypeCodeOf(Geometry geometry) => geometry.Kind switch
    {
        GeometryKind.Point => 1,
        GeometryKind.LineString => 2,
        GeometryKind.Polygon => 3,
        GeometryKind.MultiPoint => 4,
        GeometryKind.MultiLineString => 5,
        GeometryKind.MultiPolygon => 6,
        _ => throw new NotSupportedException(
            $"{geometry.Kind} has no ISO WKB code in this writer."),
    };
}
