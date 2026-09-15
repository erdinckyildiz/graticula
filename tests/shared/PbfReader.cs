using System;
using System.Collections.Generic;
using System.Text;

namespace Graticula.Testing;

/// <summary>
/// Reads a FeatureCollection PBF response from its bytes, without the writer that produced it.
/// </summary>
/// <remarks>
/// <b>A second implementation, on purpose.</b> A test that decoded with the server's own encoder
/// types would agree with any mistake they share. This reads the wire format from the protobuf
/// encoding rules and the field numbers of Esri's published <c>FeatureCollection.proto</c>, and
/// nothing in <c>/src</c>. During development it was checked in turn against the independent npm
/// decoder <c>arcgis-pbf-parser</c> (Apache 2.0), which is not shipped or referenced.
/// </remarks>
public static class PbfReader
{
    /// <summary>One field on the wire.</summary>
    /// <param name="Number">The field number.</param>
    /// <param name="WireType">0 varint, 1 fixed64, 2 length-delimited, 5 fixed32.</param>
    /// <param name="Varint">The value of a varint field.</param>
    /// <param name="Bytes">The payload of a length-delimited or fixed field.</param>
    public sealed record Field(int Number, int WireType, ulong Varint, byte[] Bytes)
    {
        /// <summary>The payload as a string.</summary>
        public string Text => Encoding.UTF8.GetString(Bytes);

        /// <summary>The payload as a double.</summary>
        public double AsDouble => BitConverter.ToDouble(Bytes, 0);

        /// <summary>A zigzag varint.</summary>
        public long SInt => (long)(Varint >> 1) ^ -(long)(Varint & 1);

        /// <summary>The payload read as a message.</summary>
        public IReadOnlyList<Field> Message => Read(Bytes);
    }

    /// <summary>Every field of a message, in order.</summary>
    public static IReadOnlyList<Field> Read(byte[] bytes)
    {
        List<Field> fields = [];
        int at = 0;

        while (at < bytes.Length)
        {
            ulong tag = VarintAt(bytes, ref at);
            int number = (int)(tag >> 3);
            int wire = (int)(tag & 7);

            switch (wire)
            {
                case 0:
                    fields.Add(new Field(number, wire, VarintAt(bytes, ref at), []));
                    break;
                case 1:
                    fields.Add(new Field(number, wire, 0, bytes[at..(at + 8)]));
                    at += 8;
                    break;
                case 2:
                    int length = (int)VarintAt(bytes, ref at);
                    fields.Add(new Field(number, wire, 0, bytes[at..(at + length)]));
                    at += length;
                    break;
                case 5:
                    fields.Add(new Field(number, wire, 0, bytes[at..(at + 4)]));
                    at += 4;
                    break;
                default:
                    throw new FormatException($"Wire type {wire} at byte {at} is not one a FeatureCollection uses.");
            }
        }

        return fields;
    }

    /// <summary>A packed run of varints.</summary>
    public static List<ulong> Packed(byte[] bytes)
    {
        List<ulong> values = [];
        int at = 0;

        while (at < bytes.Length)
        {
            values.Add(VarintAt(bytes, ref at));
        }

        return values;
    }

    /// <summary>A packed run of zigzag varints.</summary>
    public static List<long> PackedSInt(byte[] bytes) =>
        Packed(bytes).ConvertAll(v => (long)(v >> 1) ^ -(long)(v & 1));

    /// <summary>The root's <c>queryResult</c>, after checking the version.</summary>
    public static IReadOnlyList<Field> QueryResult(byte[] response)
    {
        IReadOnlyList<Field> root = Read(response);
        Field version = root.One(1);

        if (version.Text != "1")
        {
            throw new FormatException($"Version '{version.Text}', where the specification is at 1.");
        }

        return root.One(2).Message;
    }

    /// <summary>
    /// A geometry's parts in world coordinates, undoing the transform and the per-part delta encoding.
    /// </summary>
    public static List<List<(double X, double Y)>> Parts(IReadOnlyList<Field> geometry, IReadOnlyList<Field> transform)
    {
        bool upperLeft = transform.OptionalVarint(1) == 0;
        IReadOnlyList<Field> scale = transform.One(2).Message;
        IReadOnlyList<Field> translate = transform.One(3).Message;
        double sx = scale.One(1).AsDouble;
        double sy = scale.One(2).AsDouble;
        double tx = translate.One(1).AsDouble;
        double ty = translate.One(2).AsDouble;

        List<long> coords = geometry.Find(f => f.Number == 3) is { } c ? PackedSInt(c.Bytes) : [];
        List<ulong> lengths = geometry.Find(f => f.Number == 2) is { } l ? Packed(l.Bytes) : [(ulong)(coords.Count / 2)];

        List<List<(double, double)>> parts = [];
        int at = 0;

        foreach (ulong length in lengths)
        {
            List<(double, double)> part = [];
            long x = 0;
            long y = 0;

            for (ulong n = 0; n < length; n++)
            {
                x = n == 0 ? coords[at] : x + coords[at];
                y = n == 0 ? coords[at + 1] : y + coords[at + 1];
                at += 2;

                part.Add((tx + (x * sx), upperLeft ? ty - (y * sy) : ty + (y * sy)));
            }

            parts.Add(part);
        }

        return parts;
    }

    /// <summary>The one field with this number.</summary>
    public static Field One(this IReadOnlyList<Field> fields, int number)
    {
        Field? found = null;

        foreach (Field field in fields)
        {
            if (field.Number == number)
            {
                found = found is null ? field : throw new FormatException($"Field {number} appears more than once.");
            }
        }

        return found ?? throw new FormatException($"Field {number} is missing.");
    }

    /// <summary>Every field with this number.</summary>
    public static List<Field> All(this IReadOnlyList<Field> fields, int number)
    {
        List<Field> found = [];

        foreach (Field field in fields)
        {
            if (field.Number == number)
            {
                found.Add(field);
            }
        }

        return found;
    }

    /// <summary>A varint field's value, or zero — proto3's default — when it is absent.</summary>
    public static ulong OptionalVarint(this IReadOnlyList<Field> fields, int number)
    {
        foreach (Field field in fields)
        {
            if (field.Number == number)
            {
                return field.Varint;
            }
        }

        return 0;
    }

    /// <summary>The first field matching, or null.</summary>
    public static Field? Find(this IReadOnlyList<Field> fields, Predicate<Field> match)
    {
        foreach (Field field in fields)
        {
            if (match(field))
            {
                return field;
            }
        }

        return null;
    }

    private static ulong VarintAt(byte[] bytes, ref int at)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            byte b = bytes[at++];
            value |= (ulong)(b & 0x7F) << shift;

            if (b < 0x80)
            {
                return value;
            }

            shift += 7;
        }
    }
}
