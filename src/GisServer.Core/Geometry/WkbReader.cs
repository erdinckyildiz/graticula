using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace GisServer.Geometries;

/// <summary>
/// Reads OGC Well-Known Binary straight into the geometry model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ours rather than adopted, and not by preference.</b> The alternative
/// produces the adopted library's geometries, which are not the ones we use —
/// so adopting would mean parsing into 556,728 heap objects and then converting
/// out of them. <c>benchmarks/mvt-generation/RESULTS.md</c> finding 10 named that
/// directly: WKB parsing was building a geometry graph *"that exists only to be
/// discarded one stage later"*, and reading straight into our own representation
/// was the fix it pointed at.
/// </para>
/// <para>
/// <b>Z and M are dropped, and the caller is told.</b>
/// <c>docs/geometry-crs-policy.md</c> anticipates this: a representation that
/// discarded information is not writable, so
/// <see cref="Read(ReadOnlySpan{byte}, out bool)"/> reports whether anything was
/// lost and the layer that reads it becomes read-only for geometry. Silently
/// flattening would be exactly the *lossy on read means not writable* rule being
/// broken without anyone noticing.
/// </para>
/// <para>
/// Curves — <c>CircularString</c>, <c>CompoundCurve</c>, <c>CurvePolygon</c> —
/// are refused rather than approximated, per ADR-005 §3.3c. Linearising on the
/// way in would put an approximation where an exact arc was, and nothing
/// downstream could tell.
/// </para>
/// </remarks>
public static class WkbReader
{
    private const uint ZFlag = 0x8000_0000;
    private const uint MFlag = 0x4000_0000;
    private const uint SridFlag = 0x2000_0000;

    /// <summary>Reads a geometry, ignoring whether ordinates were dropped.</summary>
    public static Geometry Read(ReadOnlySpan<byte> wkb) => Read(wkb, out _);

    /// <summary>Reads a geometry.</summary>
    /// <param name="wkb">OGC WKB or PostGIS EWKB.</param>
    /// <param name="droppedOrdinates">
    /// <see langword="true"/> when Z or M values were present and discarded. The
    /// geometry is then a lossy read and must not be written back.
    /// </param>
    /// <exception cref="WkbFormatException">The bytes are not readable as WKB.</exception>
    public static Geometry Read(ReadOnlySpan<byte> wkb, out bool droppedOrdinates)
    {
        Cursor cursor = new(wkb);
        Geometry geometry = ReadGeometry(ref cursor);
        droppedOrdinates = cursor.DroppedOrdinates;

        if (!cursor.AtEnd)
        {
            throw new WkbFormatException(
                $"{cursor.Remaining} trailing byte(s) after a complete geometry. This is usually "
                + "a truncated or concatenated buffer rather than a different dialect.");
        }

        return geometry;
    }

    private static Geometry ReadGeometry(ref Cursor cursor)
    {
        bool littleEndian = cursor.ReadByteOrder();
        uint raw = cursor.ReadUInt32(littleEndian);

        // EWKB carries flags in the high bits; ISO WKB carries the same
        // information by adding 1000/2000/3000. Both appear in the wild and
        // PostGIS emits either depending on the function used, so both are read.
        bool hasZ = (raw & ZFlag) != 0;
        bool hasM = (raw & MFlag) != 0;
        bool hasSrid = (raw & SridFlag) != 0;

        uint code = raw & ~(ZFlag | MFlag | SridFlag);

        switch (code / 1000)
        {
            case 1: hasZ = true; break;
            case 2: hasM = true; break;
            case 3: hasZ = true; hasM = true; break;
        }

        code %= 1000;

        if (hasSrid)
        {
            // Discarded deliberately: CRS is a property of the layer, not of
            // every shape in it (Geometry's remarks explain why).
            cursor.ReadUInt32(littleEndian);
        }

        int ordinates = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        if (ordinates > 2)
        {
            cursor.DroppedOrdinates = true;
        }

        return code switch
        {
            1 => ReadPoint(ref cursor, littleEndian, ordinates),
            2 => new LineString(ReadSequence(ref cursor, littleEndian, ordinates)),
            3 => ReadPolygon(ref cursor, littleEndian, ordinates),
            4 => new MultiPoint(ReadParts<Point>(ref cursor, littleEndian, GeometryKind.MultiPoint)),
            5 => new MultiLineString(ReadParts<LineString>(ref cursor, littleEndian, GeometryKind.MultiLineString)),
            6 => new MultiPolygon(ReadParts<Polygon>(ref cursor, littleEndian, GeometryKind.MultiPolygon)),
            7 => throw new WkbFormatException(
                "GeometryCollection is not modelled. v1 serves homogeneous layers, and a "
                + "collection column would need a decision about what the layer's type is."),
            >= 8 and <= 12 => throw new WkbFormatException(
                $"WKB type {code} is a curve geometry. Curves are refused rather than "
                + "linearised on read (ADR-005 §3.3c): an approximation put where an exact arc "
                + "was is a loss nothing downstream can detect."),
            _ => throw new WkbFormatException($"Unknown WKB geometry type {code}."),
        };
    }

    private static Point ReadPoint(ref Cursor cursor, bool littleEndian, int ordinates)
    {
        double x = cursor.ReadDouble(littleEndian);
        double y = cursor.ReadDouble(littleEndian);

        for (int i = 2; i < ordinates; i++)
        {
            cursor.ReadDouble(littleEndian);
        }

        // WKB has no empty point; PostGIS emits NaN for one.
        return double.IsNaN(x) && double.IsNaN(y) ? Point.Empty : new Point(x, y);
    }

    private static XySequence ReadSequence(ref Cursor cursor, bool littleEndian, int ordinates)
    {
        int count = cursor.ReadCount(littleEndian, ordinates);

        if (count == 0)
        {
            return XySequence.Empty;
        }

        // One allocation for the whole sequence, filled in place. This is the
        // difference the benchmark measured: 404 MB to 204 MB on a z12 tile.
        double[] xy = new double[count * 2];

        for (int i = 0; i < count; i++)
        {
            xy[i * 2] = cursor.ReadDouble(littleEndian);
            xy[(i * 2) + 1] = cursor.ReadDouble(littleEndian);

            for (int skipped = 2; skipped < ordinates; skipped++)
            {
                cursor.ReadDouble(littleEndian);
            }
        }

        return XySequence.Wrap(xy);
    }

    private static Polygon ReadPolygon(ref Cursor cursor, bool littleEndian, int ordinates)
    {
        int ringCount = cursor.ReadUInt32AsCount(littleEndian);

        if (ringCount == 0)
        {
            return Polygon.Empty;
        }

        LinearRing shell = new(ReadSequence(ref cursor, littleEndian, ordinates));

        if (ringCount == 1)
        {
            return new Polygon(shell);
        }

        List<LinearRing> holes = new(ringCount - 1);
        for (int i = 1; i < ringCount; i++)
        {
            LinearRing hole = new(ReadSequence(ref cursor, littleEndian, ordinates));
            if (!hole.IsEmpty)
            {
                holes.Add(hole);
            }
        }

        return new Polygon(shell, holes);
    }

    private static List<TPart> ReadParts<TPart>(ref Cursor cursor, bool littleEndian, GeometryKind expected)
        where TPart : Geometry
    {
        int partCount = cursor.ReadUInt32AsCount(littleEndian);
        List<TPart> parts = new(partCount);

        for (int i = 0; i < partCount; i++)
        {
            // Each part carries its own byte order and type header. WKB permits
            // them to differ from the parent's, and some producers do.
            Geometry part = ReadGeometry(ref cursor);

            if (part is not TPart typed)
            {
                throw new WkbFormatException(
                    $"A {expected} part {i} is a {part.Kind}, which cannot belong to it.");
            }

            if (!typed.IsEmpty)
            {
                parts.Add(typed);
            }
        }

        return parts;
    }

    /// <summary>Walks the buffer, tracking position and byte order.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _position;

        public bool DroppedOrdinates { get; set; }

        public readonly bool AtEnd => _position == _buffer.Length;

        public readonly int Remaining => _buffer.Length - _position;

        public bool ReadByteOrder()
        {
            byte order = Take(1)[0];
            return order switch
            {
                0 => false,
                1 => true,
                _ => throw new WkbFormatException(
                    $"Byte order marker {order} is neither 0 (big endian) nor 1 (little endian). "
                    + "The buffer is probably not WKB."),
            };
        }

        public uint ReadUInt32(bool littleEndian)
        {
            ReadOnlySpan<byte> bytes = Take(4);
            return littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        public double ReadDouble(bool littleEndian)
        {
            ReadOnlySpan<byte> bytes = Take(8);
            return littleEndian
                ? BinaryPrimitives.ReadDoubleLittleEndian(bytes)
                : BinaryPrimitives.ReadDoubleBigEndian(bytes);
        }

        /// <summary>A count that must not exceed what the buffer can hold.</summary>
        public int ReadCount(bool littleEndian, int ordinates)
        {
            uint count = ReadUInt32(littleEndian);
            long needed = (long)count * ordinates * 8;

            // A hostile or corrupt buffer can claim four billion coordinates.
            // Checking against what is actually left turns a several-gigabyte
            // allocation into an exception — which matters because A-037
            // established allocation as this server's binding constraint.
            if (needed > Remaining)
            {
                throw new WkbFormatException(
                    $"The buffer declares {count} coordinates, needing {needed} bytes, but only "
                    + $"{Remaining} remain. Truncated or hostile input.");
            }

            return (int)count;
        }

        public int ReadUInt32AsCount(bool littleEndian)
        {
            uint count = ReadUInt32(littleEndian);

            // Each part or ring costs at least a header, so this bounds the
            // count without knowing its contents.
            if (count > (uint)Remaining)
            {
                throw new WkbFormatException(
                    $"The buffer declares {count} parts but holds only {Remaining} more bytes.");
            }

            return (int)count;
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (_position + length > _buffer.Length)
            {
                throw new WkbFormatException(
                    $"Ran out of bytes: needed {length} at offset {_position}, "
                    + $"buffer is {_buffer.Length}.");
            }

            ReadOnlySpan<byte> slice = _buffer.Slice(_position, length);
            _position += length;
            return slice;
        }
    }
}

/// <summary>Thrown when a buffer cannot be read as WKB.</summary>
public sealed class WkbFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WkbFormatException()
        : base("The buffer could not be read as WKB.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public WkbFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public WkbFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(WkbFormatException)}: {Message}");
}
