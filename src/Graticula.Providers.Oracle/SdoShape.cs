using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Graticula.Providers.Oracle;

/// <summary>
/// An <c>SDO_GEOMETRY</c> taken apart and put together by this server — for the shapes Oracle's own WKB gets wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>SDO_UTIL.TO_WKBGEOMETRY</c> for every shape</b> — measured on Oracle Database 23ai Free, 2026-10-20.
/// For a shape with three ordinates it writes a 2D type code followed by three doubles per vertex, which no WKB reader
/// takes as written; under a geographic reference (4326, 4979) it writes the elevation as <c>0</c>; and for four
/// ordinates it refuses (ORA-13199), as its WKT and GeoJSON do. <c>FROM_WKBGEOMETRY</c> refuses ISO's Z and M codes the
/// same way. So a layer that stores Z or M is read from the shape's parts — its <c>SDO_GTYPE</c>, its point, its element
/// info and its ordinates, handed over as JSON by the database — and written by building the shape from parts, and the
/// WKB in between is ISO's, which Core reads and writes.
/// </para>
/// <para>
/// <b>Straight lines only.</b> An arc, a circle, a compound element or an oriented point has no WKB form without
/// densifying it; <see cref="ToWkb"/> answers null for one, and the reader falls back to Oracle's flat WKB of the same
/// shape, without its Z and M, as it would for a layer that stores none.
/// </para>
/// <para>
/// <b>The vertex layout is the same on both sides.</b> Oracle stores a measure after an elevation (<c>SDO_GTYPE</c>
/// 4402, measure in the fourth place) or alone in the third (3302), and ISO WKB orders x, y, z, m the same way.
/// </para>
/// </remarks>
internal static class SdoShape
{
    /// <summary>The parts of a shape, as the database hands them over and as a written one is bound.</summary>
    /// <param name="GType">The <c>SDO_GTYPE</c>: dimensions, measure position, type — <c>DLTT</c>.</param>
    /// <param name="Point">The <c>SDO_POINT</c>'s x, y and z, or null.</param>
    /// <param name="ElemInfo">The element info triplets, or null.</param>
    /// <param name="Ordinates">The ordinates, or null.</param>
    internal sealed record Parts(int GType, double?[]? Point, int[]? ElemInfo, double[]? Ordinates);

    /// <summary>
    /// The SQL expression that hands a shape's parts over as one JSON document, for the column <paramref name="shape"/>
    /// of the table aliased <paramref name="alias"/>; null where the shape is.
    /// </summary>
    internal static string PartsSql(string alias, string shape)
    {
        string g = alias + "." + shape;

        return $"case when {g} is null then null else json_object("
            + $"'t' value {g}.sdo_gtype, "
            + $"'p' value case when {g}.sdo_point is not null then json_array({g}.sdo_point.x, {g}.sdo_point.y, {g}.sdo_point.z null on null) end, "
            + $"'e' value (select json_arrayagg(e.column_value returning clob) from table({g}.sdo_elem_info) e), "
            + $"'o' value (select json_arrayagg(o.column_value returning clob) from table({g}.sdo_ordinates) o) "
            + "returning clob) end";
    }

    /// <summary>Reads the JSON <see cref="PartsSql"/> produced.</summary>
    internal static Parts ParseParts(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        double?[]? point = root.TryGetProperty("p", out JsonElement p) && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null)]
            : null;
        int[]? elements = root.TryGetProperty("e", out JsonElement e) && e.ValueKind == JsonValueKind.Array
            ? [.. e.EnumerateArray().Select(v => checked((int)v.GetDecimal()))]
            : null;
        double[]? ordinates = root.TryGetProperty("o", out JsonElement o) && o.ValueKind == JsonValueKind.Array
            ? [.. o.EnumerateArray().Select(v => v.GetDouble())]
            : null;

        return new Parts(root.GetProperty("t").GetInt32(), point, elements, ordinates);
    }

    /// <summary>
    /// The shape as ISO WKB, little-endian, with its Z and M; null for one with an arc, a circle, a compound element, an
    /// oriented point or a collection, which the caller reads from Oracle's flat WKB instead.
    /// </summary>
    internal static byte[]? ToWkb(Parts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        int dimensions = parts.GType / 1000;
        int measureAt = parts.GType / 100 % 10;
        int type = parts.GType % 100;

        if (dimensions is < 2 or > 4)
        {
            return null;
        }

        // Where z and m are in a vertex, 0-based; -1 for absent.
        (int z, int m) = dimensions switch
        {
            2 => (-1, -1),
            3 => measureAt == 3 ? (-1, 2) : (2, -1),
            _ => measureAt == 3 ? (3, 2) : (2, 3),
        };

        Layout layout = new(dimensions, z, m);

        // A point in SDO_POINT, with no elements.
        if (parts.ElemInfo is null or { Length: 0 })
        {
            if (type != 1 || parts.Point is not { Length: 3 } at || at[0] is not { } x || at[1] is not { } y)
            {
                return null;
            }

            if (dimensions == 4)
            {
                return null;
            }

            using MemoryStream single = new();
            WritePoint(single, layout, dimensions == 2 ? [x, y] : [x, y, at[2] ?? double.NaN], 0);
            return single.ToArray();
        }

        if (parts.ElemInfo.Length % 3 != 0 || parts.Ordinates is null)
        {
            return null;
        }

        List<Element> elements = [];

        for (int i = 0; i < parts.ElemInfo.Length; i += 3)
        {
            int start = parts.ElemInfo[i] - 1;
            int end = i + 3 < parts.ElemInfo.Length ? parts.ElemInfo[i + 3] - 1 : parts.Ordinates.Length;
            int etype = parts.ElemInfo[i + 1];
            int interpretation = parts.ElemInfo[i + 2];

            if (etype == 0)
            {
                continue;
            }

            bool straight = etype switch
            {
                1 => interpretation >= 1,
                2 or 1003 or 2003 => interpretation == 1,
                _ => false,
            };

            if (!straight || start < 0 || end > parts.Ordinates.Length || start >= end || (end - start) % dimensions != 0)
            {
                return null;
            }

            elements.Add(new Element(etype, start, end));
        }

        using MemoryStream stream = new();

        switch (type)
        {
            case 1 when elements is [{ Kind: 1 } only] && Count(only, dimensions) == 1:
                WritePoint(stream, layout, parts.Ordinates, only.Start);
                break;

            case 5 when elements.All(x => x.Kind == 1):
            {
                int count = elements.Sum(x => Count(x, dimensions));
                Header(stream, 4, layout);
                WriteUInt(stream, (uint)count);

                foreach (Element element in elements)
                {
                    for (int at = element.Start; at < element.End; at += dimensions)
                    {
                        WritePoint(stream, layout, parts.Ordinates, at);
                    }
                }

                break;
            }

            case 2 when elements is [{ Kind: 2 } line]:
                WriteLine(stream, layout, parts.Ordinates, line);
                break;

            case 6 when elements.All(x => x.Kind == 2):
                Header(stream, 5, layout);
                WriteUInt(stream, (uint)elements.Count);

                foreach (Element element in elements)
                {
                    WriteLine(stream, layout, parts.Ordinates, element);
                }

                break;

            case 3 or 7:
            {
                List<List<Element>> polygons = [];

                foreach (Element element in elements)
                {
                    if (element.Kind == 1003)
                    {
                        polygons.Add([element]);
                    }
                    else if (element.Kind == 2003 && polygons.Count > 0)
                    {
                        polygons[^1].Add(element);
                    }
                    else
                    {
                        return null;
                    }
                }

                if (polygons.Count == 0 || (type == 3 && polygons.Count != 1))
                {
                    return null;
                }

                if (type == 7)
                {
                    Header(stream, 6, layout);
                    WriteUInt(stream, (uint)polygons.Count);
                }

                foreach (List<Element> rings in polygons)
                {
                    Header(stream, 3, layout);
                    WriteUInt(stream, (uint)rings.Count);

                    foreach (Element ring in rings)
                    {
                        WriteVertices(stream, layout, parts.Ordinates, ring);
                    }
                }

                break;
            }

            default:
                return null;
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The parts of a shape to write, from ISO or extended WKB with its Z and M: a point as a one-point element, rings
    /// turned as Oracle requires — the outer counterclockwise, the holes clockwise.
    /// </summary>
    /// <exception cref="InvalidDataException">A collection, or WKB this reads no further.</exception>
    internal static Parts FromWkb(ReadOnlySpan<byte> wkb)
    {
        WkbCursor cursor = new(wkb.ToArray());
        List<int> elements = [];
        List<double> ordinates = [];

        (int type, int dimensions, bool hasM) = ReadInto(ref cursor, elements, ordinates);

        if (cursor.Position != wkb.Length)
        {
            throw new InvalidDataException("Trailing bytes after a complete geometry.");
        }

        int oracleType = type switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 5,
            5 => 6,
            6 => 7,
            _ => throw new InvalidDataException($"A WKB geometry of type {type} is not written to an Oracle layer."),
        };

        int gtype = (dimensions * 1000) + (hasM ? dimensions * 100 : 0) + oracleType;
        return new Parts(gtype, null, [.. elements], [.. ordinates]);
    }

    /// <summary>A JSON array of numbers, each written so it reads back to the same double.</summary>
    internal static string JsonArray(IEnumerable<double> values) =>
        "[" + string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    /// <summary>A JSON array of whole numbers.</summary>
    internal static string JsonArray(IEnumerable<int> values) =>
        "[" + string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    // ------------------------------------------------------------------ reading parts into WKB

    private readonly record struct Layout(int Dimensions, int Z, int M);

    private readonly record struct Element(int Kind, int Start, int End);

    private static int Count(Element element, int dimensions) => (element.End - element.Start) / dimensions;

    private static void Header(Stream stream, uint code, Layout layout)
    {
        stream.WriteByte(1);
        WriteUInt(stream, code + (layout.Z >= 0 ? 1000u : 0u) + (layout.M >= 0 ? 2000u : 0u));
    }

    private static void WritePoint(Stream stream, Layout layout, double[] ordinates, int at)
    {
        Header(stream, 1, layout);
        WriteVertex(stream, layout, ordinates, at);
    }

    private static void WriteLine(Stream stream, Layout layout, double[] ordinates, Element line)
    {
        Header(stream, 2, layout);
        WriteVertices(stream, layout, ordinates, line);
    }

    private static void WriteVertices(Stream stream, Layout layout, double[] ordinates, Element element)
    {
        WriteUInt(stream, (uint)Count(element, layout.Dimensions));

        for (int at = element.Start; at < element.End; at += layout.Dimensions)
        {
            WriteVertex(stream, layout, ordinates, at);
        }
    }

    private static void WriteVertex(Stream stream, Layout layout, double[] ordinates, int at)
    {
        WriteDouble(stream, ordinates[at]);
        WriteDouble(stream, ordinates[at + 1]);

        if (layout.Z >= 0)
        {
            WriteDouble(stream, ordinates[at + layout.Z]);
        }

        if (layout.M >= 0)
        {
            WriteDouble(stream, ordinates[at + layout.M]);
        }
    }

    private static void WriteUInt(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    // ------------------------------------------------------------------ reading WKB into parts

    private const uint ZFlag = 0x80000000;
    private const uint MFlag = 0x40000000;
    private const uint SridFlag = 0x20000000;

    private struct WkbCursor(byte[] bytes)
    {
        public int Position;

        public readonly int Length => bytes.Length;

        public byte Byte()
        {
            if (Position >= bytes.Length)
            {
                throw new InvalidDataException("The WKB ends early.");
            }

            return bytes[Position++];
        }

        public uint UInt(bool little)
        {
            Need(4);
            uint value = little
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Position))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(Position));
            Position += 4;
            return value;
        }

        public double Double(bool little)
        {
            Need(8);
            double value = little
                ? BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(Position))
                : BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(Position));
            Position += 8;
            return value;
        }

        private readonly void Need(int count)
        {
            if (Position + count > bytes.Length)
            {
                throw new InvalidDataException("The WKB ends early.");
            }
        }
    }

    private static (int Type, int Dimensions, bool HasM) ReadInto(ref WkbCursor cursor, List<int> elements, List<double> ordinates)
    {
        bool little = cursor.Byte() == 1;
        uint raw = cursor.UInt(little);
        bool hasZ = (raw & ZFlag) != 0;
        bool hasM = (raw & MFlag) != 0;
        uint code = raw & ~(ZFlag | MFlag | SridFlag);

        if ((raw & SridFlag) != 0)
        {
            cursor.UInt(little);
        }

        switch (code / 1000)
        {
            case 1: hasZ = true; break;
            case 2: hasM = true; break;
            case 3: hasZ = true; hasM = true; break;
        }

        int type = (int)(code % 1000);
        int dimensions = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);

        switch (type)
        {
            case 1:
                elements.AddRange([ordinates.Count + 1, 1, 1]);
                ReadVertices(ref cursor, little, dimensions, 1, ordinates);
                break;

            case 2:
                elements.AddRange([ordinates.Count + 1, 2, 1]);
                ReadVertices(ref cursor, little, dimensions, (int)cursor.UInt(little), ordinates);
                break;

            case 3:
            {
                uint rings = cursor.UInt(little);

                for (int r = 0; r < rings; r++)
                {
                    int start = ordinates.Count;
                    elements.AddRange([start + 1, r == 0 ? 1003 : 2003, 1]);
                    ReadVertices(ref cursor, little, dimensions, (int)cursor.UInt(little), ordinates);
                    Orient(ordinates, start, dimensions, counterClockwise: r == 0);
                }

                break;
            }

            case 4:
            {
                uint count = cursor.UInt(little);
                elements.AddRange([ordinates.Count + 1, 1, (int)count]);

                for (int i = 0; i < count; i++)
                {
                    ReadInto(ref cursor, [], ordinates);
                }

                break;
            }

            case 5 or 6:
            {
                uint count = cursor.UInt(little);

                for (int i = 0; i < count; i++)
                {
                    ReadInto(ref cursor, elements, ordinates);
                }

                break;
            }

            default:
                throw new InvalidDataException($"A WKB geometry of type {type} is not written to an Oracle layer.");
        }

        return (type, dimensions, hasM);
    }

    private static void ReadVertices(ref WkbCursor cursor, bool little, int dimensions, int count, List<double> ordinates)
    {
        for (int i = 0; i < count * dimensions; i++)
        {
            ordinates.Add(cursor.Double(little));
        }
    }

    /// <summary>Reverses a ring's vertices in place, whole vertices at a time, when it does not turn the way asked.</summary>
    private static void Orient(List<double> ordinates, int start, int dimensions, bool counterClockwise)
    {
        int count = (ordinates.Count - start) / dimensions;
        double twiceArea = 0;

        for (int i = 0; i < count - 1; i++)
        {
            int a = start + (i * dimensions);
            int b = a + dimensions;
            twiceArea += (ordinates[a] * ordinates[b + 1]) - (ordinates[b] * ordinates[a + 1]);
        }

        if (twiceArea == 0 || (twiceArea > 0) == counterClockwise)
        {
            return;
        }

        for (int i = 0, j = count - 1; i < j; i++, j--)
        {
            for (int d = 0; d < dimensions; d++)
            {
                int a = start + (i * dimensions) + d;
                int b = start + (j * dimensions) + d;
                (ordinates[a], ordinates[b]) = (ordinates[b], ordinates[a]);
            }
        }
    }
}
