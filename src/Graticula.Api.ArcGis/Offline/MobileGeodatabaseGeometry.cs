using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis.Offline;

/// <summary>
/// The integer grid a mobile geodatabase stores coordinates on — ADR-288.
/// </summary>
/// <remarks>
/// A coordinate is kept as <c>round((x − XOrigin) × XYScale)</c>, and the grid is declared per feature class in its
/// item definition, so a writer chooses it and must say what it chose (research note §2,
/// <c>docs/research/mobile-geodatabase-format.md</c>).
/// </remarks>
/// <param name="XOrigin">The false easting the grid starts at.</param>
/// <param name="YOrigin">The false northing the grid starts at.</param>
/// <param name="XYScale">Grid steps per unit of the reference.</param>
/// <param name="ZOrigin">The false origin of elevations.</param>
/// <param name="ZScale">Grid steps per unit of elevation.</param>
/// <param name="MOrigin">The false origin of measures.</param>
/// <param name="MScale">Grid steps per unit of measure.</param>
public readonly record struct CoordinateGrid(
    double XOrigin, double YOrigin, double XYScale,
    double ZOrigin = -100000, double ZScale = 10000,
    double MOrigin = -100000, double MScale = 10000)
{
    /// <summary>The grid ArcGIS gives a geographic reference: a nanodegree from −400°.</summary>
    public static CoordinateGrid Geographic { get; } = new(-400, -400, 1e9);

    /// <summary>The grid ArcGIS gives Web Mercator: a tenth of a millimetre.</summary>
    public static CoordinateGrid WebMercator { get; } = new(-20037700, -30241100, 10000);

    /// <summary>
    /// A grid for any other projected reference: a tenth of a millimetre from −10⁸ metres, wide enough for any
    /// coordinate on Earth in any projection this server serves.
    /// </summary>
    public static CoordinateGrid Projected { get; } = new(-1e8, -1e8, 10000);

    /// <summary>The grid this server writes for a reference.</summary>
    /// <param name="wkid">The reference's well-known id.</param>
    /// <param name="geographic">Whether the reference is geographic.</param>
    public static CoordinateGrid For(int wkid, bool geographic) =>
        geographic ? Geographic : wkid is 3857 or 102100 or 102113 or 900913 ? WebMercator : Projected;

    internal long X(double x) => Grid(x, XOrigin, XYScale);

    internal long Y(double y) => Grid(y, YOrigin, XYScale);

    internal long Z(double z) => Grid(z, ZOrigin, ZScale);

    internal long M(double m) => Grid(m, MOrigin, MScale);

    private static long Grid(double value, double origin, double scale)
    {
        double steps = Math.Round((value - origin) * scale, MidpointRounding.AwayFromZero);
        if (double.IsNaN(steps) || steps < 0 || steps > 4.5e15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "The coordinate falls outside the grid the offline copy is written on.");
        }

        return (long)steps;
    }
}

/// <summary>
/// The geometry blob of a mobile geodatabase, encoded and decoded — ADR-288.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observed, not published.</b> The layout below was read from blobs ArcGIS Pro wrote for geometries this project
/// chose, and from files ArcGIS Server wrote for a service this project made; the derivation is disclosed in
/// <c>docs/research/mobile-geodatabase-format.md</c> §0 and §2. Fifty captured blobs re-encode byte for byte.
/// </para>
/// <code>
/// int32  (srid &lt;&lt; 8) | 0x64
/// int32  point count, part separators included
/// byte   type: point 0x04, multipoint 0x08, polyline 0x14, polygon 0x18; +2 with Z, +1 with M
/// byte   flags: 0x1 point/multipoint, 0x4 polyline, 0x8 polygon; 0x40 several parts (or several points)
/// [count &gt; 10]     xmin ymin xmax ymax
/// [0x40, not multi] extra part count, then each extra part's first index, delta-coded
/// 4 bytes payload length, a varint padded with zeros
/// 4 bytes 01, dimension byte (Z 1, M 2, polygons | 0x10), 00, 04 for polygons else 00
/// payload x0 y0 dx dy …, then z0 dz …, then m0 dm …
/// </code>
/// <para>
/// Every number after the fixed header is a signed variable-length integer: first byte bit 7 <i>more follows</i>,
/// bit 6 <i>negative</i>, bits 0–5 the lowest six bits; each later byte seven more. Polyline paths and polygon
/// outer rings are separated by a point at grid (−1, 0); holes follow their outer ring without one.
/// </para>
/// </remarks>
public static class MobileGeodatabaseGeometry
{
    private const byte Magic = 0x64;

    /// <summary>The value of <c>st_geometry_columns.geometry_type</c> for a kind and its ordinates.</summary>
    /// <param name="kind">The geometry kind, single or multi.</param>
    /// <param name="ordinates">The ordinates beyond x and y.</param>
    public static int ColumnType(GeometryKind kind, GeometryOrdinates ordinates)
    {
        int basis = kind switch
        {
            GeometryKind.Point => 1,
            GeometryKind.MultiPoint => 4,
            GeometryKind.LineString or GeometryKind.MultiLineString => 5,
            GeometryKind.Polygon or GeometryKind.MultiPolygon => 6,
            _ => throw new NotSupportedException($"A {kind} cannot be written to an offline copy."),
        };
        return basis + (ordinates.HasFlag(GeometryOrdinates.Z) ? 1000 : 0) + (ordinates.HasFlag(GeometryOrdinates.M) ? 2000 : 0);
    }

    /// <summary>Encodes a geometry, or returns null for an empty one.</summary>
    /// <param name="geometry">The geometry, with its Z and M if the layer has them.</param>
    /// <param name="srid">The reference's well-known id, as the column declares it.</param>
    /// <param name="grid">The grid the layer is declared on.</param>
    /// <param name="ordinates">
    /// The ordinates the <em>column</em> carries. A coordinate without one is written as zero, as the column demands a
    /// value; a coordinate with one the column lacks has it dropped.
    /// </param>
    public static byte[]? Encode(Geometry geometry, int srid, CoordinateGrid grid, GeometryOrdinates ordinates)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
        {
            return null;
        }

        bool hasZ = ordinates.HasFlag(GeometryOrdinates.Z);
        bool hasM = ordinates.HasFlag(GeometryOrdinates.M);
        var points = new List<(long X, long Y, long Z, long M)>();
        var starts = new List<int>();
        byte type, nibble;
        bool multi;

        void Add(double x, double y, double? z, double? m) =>
            points.Add((grid.X(x), grid.Y(y), hasZ ? grid.Z(z ?? 0) : 0, hasM ? grid.M(m ?? 0) : 0));

        void AddSequence(XySequence s, bool reversed)
        {
            for (int k = 0; k < s.Count; k++)
            {
                int i = reversed ? s.Count - 1 - k : k;
                Add(s.X(i), s.Y(i), s.HasZ ? s.Z(i) : null, s.HasM ? s.M(i) : null);
            }
        }

        void Separate()
        {
            if (points.Count > 0)
            {
                points.Add((-1, 0, 0, 0));
            }

            starts.Add(points.Count);
        }

        // OGC's winding, not ArcGIS JSON's: Pro wrote a clockwise shell counter-clockwise and a counter-clockwise hole
        // clockwise (research note §2), the reverse of what ArcGisGeometryWriter sends to the same clients.
        void AddPolygon(Polygon polygon)
        {
            Separate();
            AddSequence(polygon.Shell.Coordinates, reversed: !polygon.Shell.IsCounterClockwise);
            foreach (LinearRing hole in polygon.Holes)
            {
                AddSequence(hole.Coordinates, reversed: hole.IsCounterClockwise);
            }
        }

        switch (geometry)
        {
            case Point p:
                Add(p.X, p.Y, p.Z, p.M);
                (type, nibble, multi) = (0x04, 0x01, false);
                break;
            case MultiPoint mp:
                foreach (Point p in mp.Parts)
                {
                    Add(p.X, p.Y, p.Z, p.M);
                }

                (type, nibble, multi) = (0x08, 0x01, points.Count > 1);
                break;
            case LineString line:
                Separate();
                AddSequence(line.Coordinates, reversed: false);
                (type, nibble, multi) = (0x14, 0x04, false);
                break;
            case MultiLineString lines:
                foreach (LineString line in lines.Parts)
                {
                    Separate();
                    AddSequence(line.Coordinates, reversed: false);
                }

                (type, nibble, multi) = (0x14, 0x04, lines.Parts.Count > 1);
                break;
            case Polygon polygon:
                AddPolygon(polygon);
                (type, nibble, multi) = (0x18, 0x08, false);
                break;
            case MultiPolygon polygons:
                foreach (Polygon polygon in polygons.Parts)
                {
                    AddPolygon(polygon);
                }

                (type, nibble, multi) = (0x18, 0x08, polygons.Parts.Count > 1);
                break;
            default:
                throw new NotSupportedException($"A {geometry.Kind} cannot be written to an offline copy.");
        }

        if (hasZ)
        {
            type += 2;
        }

        if (hasM)
        {
            type += 1;
        }

        using var output = new MemoryStream(32 + (points.Count * 12));
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(head, (srid << 8) | Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], points.Count);
        output.Write(head);
        output.WriteByte(type);
        output.WriteByte((byte)(nibble | (multi ? 0x40 : 0)));

        if (points.Count > 10)
        {
            long xmin = long.MaxValue, ymin = long.MaxValue, xmax = long.MinValue, ymax = long.MinValue;
            foreach ((long x, long y, _, _) in points)
            {
                if (x == -1 && y == 0)
                {
                    continue;
                }

                xmin = Math.Min(xmin, x);
                ymin = Math.Min(ymin, y);
                xmax = Math.Max(xmax, x);
                ymax = Math.Max(ymax, y);
            }

            WriteVarint(output, xmin);
            WriteVarint(output, ymin);
            WriteVarint(output, xmax);
            WriteVarint(output, ymax);
        }

        if (multi && type is not (0x08 or 0x09 or 0x0A or 0x0B))
        {
            WriteVarint(output, starts.Count - 1);
            int previous = 0;
            for (int i = 1; i < starts.Count; i++)
            {
                WriteVarint(output, starts[i] - previous);
                previous = starts[i];
            }
        }

        using var payload = new MemoryStream(points.Count * 12);
        long px = 0, py = 0;
        foreach ((long x, long y, _, _) in points)
        {
            WriteVarint(payload, x - px);
            WriteVarint(payload, y - py);
            (px, py) = (x, y);
        }

        if (hasZ)
        {
            long pz = 0;
            foreach ((_, _, long z, _) in points)
            {
                WriteVarint(payload, z - pz);
                pz = z;
            }
        }

        if (hasM)
        {
            long pm = 0;
            foreach ((_, _, _, long m) in points)
            {
                WriteVarint(payload, m - pm);
                pm = m;
            }
        }

        long start = output.Position;
        WriteVarint(output, payload.Length);
        int written = (int)(output.Position - start);
        if (written > 4)
        {
            throw new NotSupportedException("A geometry this large cannot be written to an offline copy.");
        }

        for (int i = written; i < 4; i++)
        {
            output.WriteByte(0);
        }

        bool polygonal = type >= 0x18;
        byte dims = (byte)((hasZ ? 1 : 0) | (hasM ? 2 : 0) | (polygonal ? 0x10 : 0));
        output.WriteByte(1);
        output.WriteByte(dims);
        output.WriteByte(0);
        output.WriteByte(polygonal ? (byte)4 : (byte)0);
        payload.Position = 0;
        payload.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Decodes a blob into a geometry carrying its Z and M.</summary>
    /// <param name="blob">The blob.</param>
    /// <param name="grid">The grid the column is declared on.</param>
    /// <param name="srid">The reference the blob names.</param>
    /// <exception cref="InvalidDataException">The blob is not one this decoder understands.</exception>
    public static Geometry Decode(ReadOnlySpan<byte> blob, CoordinateGrid grid, out int srid)
    {
        if (blob.Length < 18 || blob[0] != Magic)
        {
            throw new InvalidDataException("The value is not a mobile geodatabase geometry.");
        }

        srid = BinaryPrimitives.ReadInt32LittleEndian(blob) >> 8;
        int count = BinaryPrimitives.ReadInt32LittleEndian(blob[4..]);
        byte type = blob[8];
        byte flags = blob[9];
        bool hasZ = (type & 2) != 0, hasM = (type & 1) != 0;
        int basis = type & ~0x03;
        if (basis is not (0x04 or 0x08 or 0x14 or 0x18) || count < 1)
        {
            throw new InvalidDataException($"Geometry type 0x{type:X2} is not one an offline copy holds.");
        }

        int at = 10;
        if (count > 10)
        {
            for (int i = 0; i < 4; i++)
            {
                ReadVarint(blob, ref at);
            }
        }

        if ((flags & 0x40) != 0 && basis != 0x08)
        {
            long extra = ReadVarint(blob, ref at);
            for (long i = 0; i < extra; i++)
            {
                ReadVarint(blob, ref at);
            }
        }

        at += 8;
        var xs = new long[count];
        var ys = new long[count];
        long x = 0, y = 0;
        for (int i = 0; i < count; i++)
        {
            x += ReadVarint(blob, ref at);
            y += ReadVarint(blob, ref at);
            xs[i] = x;
            ys[i] = y;
        }

        double[]? zs = hasZ ? ReadOrdinate(blob, ref at, count, grid.ZOrigin, grid.ZScale) : null;
        double[]? ms = hasM ? ReadOrdinate(blob, ref at, count, grid.MOrigin, grid.MScale) : null;
        if (at != blob.Length)
        {
            throw new InvalidDataException("The geometry has bytes after its last coordinate.");
        }

        // Split at separators into runs of coordinates.
        var runs = new List<XySequence>();
        int from = 0;
        for (int i = 0; i <= count; i++)
        {
            if (i == count || (xs[i] == -1 && ys[i] == 0))
            {
                runs.Add(Sequence(from, i - from));
                from = i + 1;
            }
        }

        XySequence Sequence(int start, int length)
        {
            var xy = new double[length * 2];
            double[]? z = hasZ ? new double[length] : null;
            double[]? m = hasM ? new double[length] : null;
            for (int k = 0; k < length; k++)
            {
                xy[2 * k] = (xs[start + k] / grid.XYScale) + grid.XOrigin;
                xy[(2 * k) + 1] = (ys[start + k] / grid.XYScale) + grid.YOrigin;
                if (z is not null)
                {
                    z[k] = zs![start + k];
                }

                if (m is not null)
                {
                    m[k] = ms![start + k];
                }
            }

            return XySequence.Wrap(xy, z, m);
        }

        switch (basis)
        {
            case 0x04:
            {
                XySequence s = runs[0];
                return Point.Create(s.X(0), s.Y(0), hasZ ? s.Z(0) : null, hasM ? s.M(0) : null);
            }

            case 0x08:
            {
                XySequence s = runs[0];
                var parts = new Point[s.Count];
                for (int i = 0; i < s.Count; i++)
                {
                    parts[i] = Point.Create(s.X(i), s.Y(i), hasZ ? s.Z(i) : null, hasM ? s.M(i) : null);
                }

                return new MultiPoint(parts);
            }

            case 0x14:
                return runs.Count == 1
                    ? new LineString(runs[0])
                    : new MultiLineString(runs.ConvertAll(r => new LineString(r)));

            default:
            {
                var polygons = new List<Polygon>(runs.Count);
                foreach (XySequence run in runs)
                {
                    polygons.Add(Rings(run));
                }

                return polygons.Count == 1 ? polygons[0] : new MultiPolygon(polygons);
            }
        }
    }

    /// <summary>One outer ring followed by its holes, each ending where it meets its own first point.</summary>
    private static Polygon Rings(XySequence run)
    {
        var rings = new List<LinearRing>();
        int start = 0;
        for (int i = 1; i < run.Count; i++)
        {
            if (i - start >= 3 && run.X(i) == run.X(start) && run.Y(i) == run.Y(start))
            {
                rings.Add(new LinearRing(run.Slice(start, i - start + 1)));
                start = i + 1;
                i = start;
            }
        }

        if (start < run.Count)
        {
            throw new InvalidDataException("A polygon ring does not close.");
        }

        return new Polygon(rings[0], rings.Count > 1 ? rings.GetRange(1, rings.Count - 1) : null);
    }

    private static double[] ReadOrdinate(ReadOnlySpan<byte> blob, ref int at, int count, double origin, double scale)
    {
        var values = new double[count];
        long v = 0;
        for (int i = 0; i < count; i++)
        {
            v += ReadVarint(blob, ref at);
            values[i] = (v / scale) + origin;
        }

        return values;
    }

    internal static void WriteVarint(Stream output, long value)
    {
        bool negative = value < 0;
        ulong magnitude = negative ? (ulong)(-value) : (ulong)value;
        byte first = (byte)((magnitude & 0x3F) | (negative ? 0x40u : 0u));
        magnitude >>= 6;
        if (magnitude != 0)
        {
            first |= 0x80;
        }

        output.WriteByte(first);
        while (magnitude != 0)
        {
            byte next = (byte)(magnitude & 0x7F);
            magnitude >>= 7;
            if (magnitude != 0)
            {
                next |= 0x80;
            }

            output.WriteByte(next);
        }
    }

    internal static long ReadVarint(ReadOnlySpan<byte> blob, ref int at)
    {
        if (at >= blob.Length)
        {
            throw new InvalidDataException("The geometry ends inside a number.");
        }

        byte b = blob[at++];
        bool negative = (b & 0x40) != 0;
        ulong value = (ulong)(b & 0x3F);
        int shift = 6;
        while ((b & 0x80) != 0)
        {
            if (at >= blob.Length || shift > 62)
            {
                throw new InvalidDataException("The geometry ends inside a number.");
            }

            b = blob[at++];
            value |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        }

        return negative ? -(long)value : (long)value;
    }
}
