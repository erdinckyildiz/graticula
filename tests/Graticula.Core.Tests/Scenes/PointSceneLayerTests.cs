using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Graticula.Features;
using Graticula.Scenes;
using Xunit;

namespace Graticula.Core.Tests.Scenes;

/// <summary>
/// D-386: point scene layers, whose geometry I3S allows only as Draco. The Draco is read back here by a reader of the
/// public bitstream specification's sequential point cloud, which is all the writer writes; that Google's own decoder
/// and the Maps SDK read it too was measured (2026-11-05) and is recorded in ADR-269.
/// </summary>
public sealed class PointSceneLayerTests
{
    [Fact]
    public void A_draco_point_cloud_is_the_sequential_layout_with_its_i3s_metadata()
    {
        byte[] draco = DracoPointCloud.Encode([(1.5f, -2f, 3f), (4f, 5f, -6.25f)], [42, 7], 1e-5, 9e-6);
        Cloud cloud = Cloud.Read(draco);

        Assert.Equal((2, 3), (cloud.Major, cloud.Minor));
        Assert.Equal(0, cloud.Geometry); // a point cloud
        Assert.Equal(0, cloud.Method); // sequential
        Assert.Equal(2, cloud.Points);
        Assert.Equal(1e-5, BinaryPrimitives.ReadDoubleLittleEndian(cloud.Metadata[0]["i3s-scale_x"]));
        Assert.Equal(9e-6, BinaryPrimitives.ReadDoubleLittleEndian(cloud.Metadata[0]["i3s-scale_y"]));
        Assert.Equal("feature-index", Encoding.ASCII.GetString(cloud.Metadata[1]["i3s-attribute-type"]));
        Assert.Equal([42, 7], Ints(cloud.Metadata[1]["i3s-feature-ids"]));

        // POSITION, three floats; GENERIC, one unsigned int — each with the generic sequential encoder: raw values.
        Assert.Equal([(0, 9, 3, 0), (4, 6, 1, 1)], cloud.Attributes);
        Assert.Equal([1.5f, -2f, 3f, 4f, 5f, -6.25f], cloud.Positions);
        Assert.Equal([0u, 1u], cloud.Indices);
    }

    [Fact]
    public void Unscaled_positions_carry_no_scale_metadata()
    {
        Cloud cloud = Cloud.Read(DracoPointCloud.Encode([(0f, 0f, 0f)], [1]));

        Assert.False(cloud.Metadata.ContainsKey(0));
        Assert.True(cloud.Metadata.ContainsKey(1));
    }

    [Fact]
    public void A_few_points_are_one_node_whose_ids_attributes_and_positions_agree()
    {
        ScenePoint[] points = [.. Enumerable.Range(1, 20).Select(i => new ScenePoint(i, 29 + (i * 0.001), 41 + (i * 0.0005), i * 10, [$"p{i}", (double)i]))];
        using ZipArchive zip = Package(points);

        JsonElement layer = Json(zip, "3dSceneLayer.json.gz");
        Assert.Equal("Point", layer.GetProperty("layerType").GetString());
        Assert.Equal("points", layer.GetProperty("store").GetProperty("profile").GetString());
        Assert.Equal("maxScreenThreshold", layer.GetProperty("pointNodePages").GetProperty("lodSelectionMetricType").GetString());
        Assert.Equal("draco", layer.GetProperty("geometryDefinitions")[0].GetProperty("geometryBuffers")[0].GetProperty("compressedAttributes").GetProperty("encoding").GetString());
        Assert.Equal("absoluteHeight", layer.GetProperty("elevationInfo").GetProperty("mode").GetString());

        JsonElement node = Json(zip, "nodepages/0.json.gz").GetProperty("nodes")[0];
        Assert.False(node.TryGetProperty("children", out _));
        Assert.Equal(20, node.GetProperty("mesh").GetProperty("geometry").GetProperty("featureCount").GetInt32());

        Cloud cloud = Cloud.Read(Plain(zip, "nodes/0/geometries/0.bin.gz"));
        int[] ids = Ints(cloud.Metadata[1]["i3s-feature-ids"]);
        Assert.Equal(Enumerable.Range(1, 20), ids);
        Assert.Equal(ids, Oids(Plain(zip, "nodes/0/attributes/f_0/0.bin.gz")));

        // A point's place comes back from its offset, the scale factors and the node's centre.
        double[] centre = [.. node.GetProperty("obb").GetProperty("center").EnumerateArray().Select(c => c.GetDouble())];
        double sx = BinaryPrimitives.ReadDoubleLittleEndian(cloud.Metadata[0]["i3s-scale_x"]);
        double sy = BinaryPrimitives.ReadDoubleLittleEndian(cloud.Metadata[0]["i3s-scale_y"]);
        int k = Array.IndexOf(ids, 7);
        Assert.Equal(29.007, centre[0] + (cloud.Positions[3 * k] * sx), 6);
        Assert.Equal(41.0035, centre[1] + (cloud.Positions[(3 * k) + 1] * sy), 6);
        Assert.Equal(70, centre[2] + cloud.Positions[(3 * k) + 2], 3);
    }

    [Fact]
    public void Many_points_are_thinned_above_the_leaves_and_every_point_is_in_a_leaf()
    {
        Random random = new(11);
        ScenePoint[] points = [.. Enumerable.Range(1, 9000).Select(i => new ScenePoint(i, 29 + random.NextDouble(), 41 + random.NextDouble(), 0, []))];
        using ZipArchive zip = Package(points, SceneElevation.RelativeToGround);

        Assert.Equal("relativeToGround", Json(zip, "3dSceneLayer.json.gz").GetProperty("elevationInfo").GetProperty("mode").GetString());
        List<JsonElement> nodes = [.. zip.Entries.Where(e => e.FullName.StartsWith("nodepages/", StringComparison.Ordinal))
            .SelectMany(e => Json(zip, e.FullName).GetProperty("nodes").EnumerateArray())];

        HashSet<int> inLeaves = [];
        foreach (JsonElement node in nodes)
        {
            int index = node.GetProperty("index").GetInt32();
            int[] ids = Ints(Cloud.Read(Plain(zip, $"nodes/{index}/geometries/0.bin.gz")).Metadata[1]["i3s-feature-ids"]);
            if (node.TryGetProperty("children", out _))
            {
                // A thinned sample: one point a cell of a 32 × 32 grid at most, and each of them in a leaf below.
                Assert.InRange(ids.Length, 1, 1024);
                Assert.True(node.GetProperty("lodThreshold").GetDouble() > 0);
            }
            else
            {
                Assert.InRange(ids.Length, 1, SceneLayerPackageWriter.MaximumPointsPerNode);
                inLeaves.UnionWith(ids);
            }
        }

        Assert.True(nodes[0].TryGetProperty("children", out _), "Nine thousand points were not split.");
        Assert.Equal(9000, inLeaves.Count);
    }

    [Fact]
    public void An_id_wider_than_32_bits_is_refused()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => SceneLayerPackageWriter.WritePoints(
            "wide", [], [new ScenePoint(5_000_000_000, 29, 41, 0, [])], [0, 0, 0], 8, SceneElevation.RelativeToGround, DateTimeOffset.UnixEpoch));
        Assert.Contains("32-bit", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static ZipArchive Package(IReadOnlyList<ScenePoint> points, SceneElevation elevation = SceneElevation.AbsoluteHeight) =>
        new(new MemoryStream(SceneLayerPackageWriter.WritePoints(
            "points", [new SceneField("name", FieldType.Text), new SceneField("value", FieldType.Double)], points, [0, 112, 192], 8,
            elevation, DateTimeOffset.UnixEpoch)), ZipArchiveMode.Read);

    private static byte[] Plain(ZipArchive zip, string name)
    {
        using GZipStream gzip = new(zip.GetEntry(name)!.Open(), CompressionMode.Decompress);
        using MemoryStream plain = new();
        gzip.CopyTo(plain);
        return plain.ToArray();
    }

    private static JsonElement Json(ZipArchive zip, string name) => JsonDocument.Parse(Plain(zip, name)).RootElement.Clone();

    private static int[] Ints(byte[] bytes) =>
        [.. Enumerable.Range(0, bytes.Length / 4).Select(i => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4 * i)))];

    private static int[] Oids(byte[] buffer) =>
        [.. Enumerable.Range(0, (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer)).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4 + (4 * i))))];

    /// <summary>A reader of the sequential point cloud with generic attributes, as the Draco specification lays it out.</summary>
    private sealed record Cloud(
        int Major, int Minor, int Geometry, int Method, Dictionary<int, Dictionary<string, byte[]>> Metadata, int Points,
        List<(int Type, int DataType, int Components, int Id)> Attributes, float[] Positions, uint[] Indices)
    {
        public static Cloud Read(byte[] b)
        {
            Assert.Equal("DRACO", Encoding.ASCII.GetString(b, 0, 5));
            int p = 5;
            int major = b[p++], minor = b[p++], geometry = b[p++], method = b[p++];
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
            p += 2;
            Assert.Equal(0x8000, flags);

            uint Varint()
            {
                uint value = 0;
                for (int shift = 0; ; shift += 7)
                {
                    byte c = b[p++];
                    value |= (uint)(c & 0x7f) << shift;
                    if ((c & 0x80) == 0)
                    {
                        return value;
                    }
                }
            }

            Dictionary<string, byte[]> Element()
            {
                Dictionary<string, byte[]> entries = [];
                uint count = Varint();
                for (int i = 0; i < count; i++)
                {
                    int length = b[p++];
                    string name = Encoding.ASCII.GetString(b, p, length);
                    p += length;
                    int size = (int)Varint();
                    entries[name] = b[p..(p + size)];
                    p += size;
                }

                Assert.Equal(0u, Varint()); // no sub-metadata
                return entries;
            }

            Dictionary<int, Dictionary<string, byte[]>> metadata = [];
            uint attributeMetadata = Varint();
            for (int i = 0; i < attributeMetadata; i++)
            {
                int id = (int)Varint();
                metadata[id] = Element();
            }

            Assert.Empty(Element()); // the geometry's own

            int points = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4;
            Assert.Equal(1, b[p++]); // one attributes decoder
            uint count = Varint();
            List<(int, int, int, int)> attributes = [];
            for (int i = 0; i < count; i++)
            {
                int type = b[p++], dataType = b[p++], components = b[p++];
                Assert.Equal(0, b[p++]); // not normalised
                attributes.Add((type, dataType, components, (int)Varint()));
            }

            for (int i = 0; i < count; i++)
            {
                Assert.Equal(0, b[p++]); // generic sequential encoder
            }

            float[] positions = new float[3 * points];
            for (int i = 0; i < positions.Length; i++, p += 4)
            {
                positions[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p));
            }

            uint[] indices = new uint[points];
            for (int i = 0; i < points; i++, p += 4)
            {
                indices[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
            }

            Assert.Equal(b.Length, p); // nothing left over
            return new Cloud(major, minor, geometry, method, metadata, points, attributes, positions, indices);
        }
    }
}
