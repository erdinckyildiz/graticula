using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Graticula.Scenes;

/// <summary>
/// A Draco point cloud as an I3S point scene layer carries it — ADR-269 §5.3, written from the public Draco bitstream
/// specification (google.github.io/draco/spec) and the I3S point profile.
/// </summary>
/// <remarks>
/// <para>
/// <b>The plainest encoding the format has, because every decoder must read it.</b> Draco offers prediction, quantisation
/// and entropy coding; this writes none of them. The point cloud is <i>sequential</i> (encoder method 0) and both
/// attributes use the <i>generic</i> sequential attribute encoder, whose values are the attribute's raw bytes one point
/// after another. It is larger than what ArcGIS Pro writes (a kd-tree with quantised positions) and the package's gzip
/// takes back much of the difference; what it buys is an encoder short enough to check by eye against the
/// specification and a decoder anywhere — Google's reference decoder and the Maps SDK's both read it (measured
/// 2026-11-05).
/// </para>
/// <para>
/// <b>What I3S asks of the cloud.</b> Two attributes: POSITION, three 32-bit floats a point, and a GENERIC attribute of
/// one unsigned 32-bit integer whose metadata says <c>i3s-attribute-type = feature-index</c> and carries the features'
/// ids as <c>i3s-feature-ids</c>, a 32-bit integer array the index points into. Positions are offsets from the node's
/// centre, and longitude and latitude may be scaled to metres with the factors <c>i3s-scale_x</c> and
/// <c>i3s-scale_y</c> in the position's metadata, which a client multiplies back — the layout ArcGIS Pro 3.7 writes
/// (read from its own package with Google's decoder).
/// </para>
/// </remarks>
public static class DracoPointCloud
{
    // Bitstream 2.3, the point cloud version of Draco 1.3 and later and the one ArcGIS Pro writes.
    private const byte MajorVersion = 2;
    private const byte MinorVersion = 3;
    private const byte PointCloudGeometry = 0;
    private const byte SequentialEncoding = 0;
    private const ushort MetadataFlag = 0x8000;

    private const byte PositionAttribute = 0;
    private const byte GenericAttribute = 4;
    private const byte Uint32Type = 6;
    private const byte Float32Type = 9;
    private const byte GenericSequentialEncoder = 0;

    /// <summary>Encodes points and their feature ids.</summary>
    /// <param name="positions">Each point's offset from the node's centre, in the units the scale factors undo.</param>
    /// <param name="featureIds">Each point's feature id, in the order of the points.</param>
    /// <param name="scaleX">What a client multiplies x by; written only when it is not 1.</param>
    /// <param name="scaleY">What a client multiplies y by; written only when it is not 1.</param>
    /// <returns>The Draco bytes.</returns>
    public static byte[] Encode(
        IReadOnlyList<(float X, float Y, float Z)> positions, IReadOnlyList<int> featureIds, double scaleX = 1, double scaleY = 1)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(featureIds);
        if (positions.Count != featureIds.Count)
        {
            throw new ArgumentException("Every point needs one feature id.", nameof(featureIds));
        }

        if (positions.Count == 0)
        {
            throw new ArgumentException("A point cloud has at least one point.", nameof(positions));
        }

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        // The header.
        writer.Write("DRACO"u8);
        writer.Write(MajorVersion);
        writer.Write(MinorVersion);
        writer.Write(PointCloudGeometry);
        writer.Write(SequentialEncoding);
        writer.Write(MetadataFlag);

        // The metadata: one element for each attribute, keyed by its unique id, then the geometry's, which is empty.
        // Entries are written sorted by name, as Draco keeps them.
        bool scaled = scaleX != 1 || scaleY != 1;
        Varint(writer, scaled ? 2u : 1u);

        if (scaled)
        {
            Varint(writer, 0); // the position attribute's unique id
            Varint(writer, 2);
            Entry(writer, "i3s-scale_x", Double(scaleX));
            Entry(writer, "i3s-scale_y", Double(scaleY));
            Varint(writer, 0); // no sub-metadata
        }

        Varint(writer, 1); // the feature index's unique id
        Varint(writer, 2);
        Entry(writer, "i3s-attribute-type", Encoding.ASCII.GetBytes("feature-index"));
        byte[] ids = new byte[4 * featureIds.Count];
        for (int i = 0; i < featureIds.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(ids.AsSpan(4 * i), featureIds[i]);
        }

        Entry(writer, "i3s-feature-ids", ids);
        Varint(writer, 0);

        Varint(writer, 0); // the geometry's own metadata: no entries
        Varint(writer, 0); // and no sub-metadata

        // The geometry: a sequential point cloud is its point count.
        writer.Write(positions.Count);

        // One attributes decoder, holding both attributes.
        writer.Write((byte)1);
        Varint(writer, 2);
        writer.Write(PositionAttribute);
        writer.Write(Float32Type);
        writer.Write((byte)3); // components
        writer.Write((byte)0); // not normalised
        Varint(writer, 0); // unique id
        writer.Write(GenericAttribute);
        writer.Write(Uint32Type);
        writer.Write((byte)1);
        writer.Write((byte)0);
        Varint(writer, 1);

        // Each attribute's sequential encoder, then the values: raw, point after point.
        writer.Write(GenericSequentialEncoder);
        writer.Write(GenericSequentialEncoder);

        foreach ((float x, float y, float z) in positions)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
        }

        for (uint i = 0; i < positions.Count; i++)
        {
            writer.Write(i);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void Entry(BinaryWriter writer, string name, byte[] value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
        Varint(writer, (uint)value.Length);
        writer.Write(value);
    }

    private static byte[] Double(double value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    private static void Varint(BinaryWriter writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }

        writer.Write((byte)value);
    }
}
