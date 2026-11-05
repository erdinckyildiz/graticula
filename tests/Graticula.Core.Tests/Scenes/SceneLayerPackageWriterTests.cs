using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Graticula.Features;
using Graticula.Scenes;
using Xunit;

namespace Graticula.Core.Tests.Scenes;

/// <summary>ADR-269 §5.3: the scene layer package the server writes from footprints.</summary>
public sealed class SceneLayerPackageWriterTests
{
    private static IReadOnlyList<(double Lon, double Lat)> Square(double lon, double lat, double size) =>
        [(lon, lat), (lon + size, lat), (lon + size, lat + size), (lon, lat + size), (lon, lat)];

    [Fact]
    public void A_footprint_with_a_hole_is_covered_once_counter_clockwise()
    {
        (double, double)[] outer = [(0, 0), (10, 0), (10, 10), (0, 10)];
        (double, double)[] hole = [(3, 3), (3, 7), (7, 7), (7, 3)];

        (List<(double X, double Y)> vertices, List<int> triangles) = FootprintTriangulator.Triangulate(outer, [hole]);

        double area = 0;
        for (int t = 0; t < triangles.Count; t += 3)
        {
            (double X, double Y) a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            double twice = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
            Assert.True(twice >= 0, "A triangle is clockwise.");
            area += twice / 2;
        }

        Assert.Equal(100 - 16, area, 6);
    }

    [Fact]
    public void A_clockwise_footprint_is_covered_as_well()
    {
        (List<(double X, double Y)> vertices, List<int> triangles) =
            FootprintTriangulator.Triangulate([(0, 0), (0, 4), (4, 4), (4, 0)], []);

        Assert.Equal(6, triangles.Count);
        Assert.Equal(4, vertices.Count);
    }

    [Fact]
    public void A_node_box_turns_as_arcgis_pro_turns_its_root()
    {
        // Measured in a package ArcGIS Pro 3.7 wrote for footprints centred here (2026-11-05). Pro fits its box to the
        // data, so it turns a little further than a box aligned to east, north and up; what is asserted is the convention
        // — the axes' order, the direction of the turn and the sign of w — to two places, not Pro's fit.
        (double x, double y, double z, double w) = SceneLayerPackageWriter.Quaternion(28.977, 41.015);

        Assert.Equal(0.211252, x, 2);
        Assert.Equal(0.358470, y, 2);
        Assert.Equal(0.783408, z, 2);
        Assert.Equal(0.461675, w, 2);
    }

    [Fact]
    public void A_package_is_a_zip_of_stored_entries_with_a_layer_nodes_geometry_attributes_and_a_hash_table()
    {
        SceneBuilding[] buildings =
        [
            new(7, [[Square(29.0, 41.0, 0.001)]], 0, 20, ["Kule", 20.0]),
            new(9, [[Square(29.002, 41.0, 0.001)]], 5, 0, ["Düz", null]),
        ];

        byte[] package = SceneLayerPackageWriter.Write("Buildings", [new SceneField("name", FieldType.Text), new SceneField("height", FieldType.Double)],
            buildings, [200, 190, 180], DateTimeOffset.UnixEpoch);

        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
        Assert.Equal("@specialIndexFileHASH128@", zip.Entries[^1].FullName);

        JsonElement layer = Json(zip, "3dSceneLayer.json.gz");
        Assert.Equal("3DObject", layer.GetProperty("layerType").GetString());
        Assert.Equal("1.7", layer.GetProperty("store").GetProperty("version").GetString());
        Assert.Equal(3, layer.GetProperty("fields").GetArrayLength());

        JsonElement nodes = Json(zip, "nodepages/0.json.gz").GetProperty("nodes");
        Assert.False(nodes[0].TryGetProperty("mesh", out _), "The root of a 3D object layer carries no geometry.");
        JsonElement leaf = nodes[1];
        JsonElement geometry = leaf.GetProperty("mesh").GetProperty("geometry");
        int vertices = geometry.GetProperty("vertexCount").GetInt32();

        // A square with a height: two roof, two floor and eight wall triangles. A flat one: its two roof triangles.
        Assert.Equal((12 + 2) * 3, vertices);
        Assert.Equal(2, geometry.GetProperty("featureCount").GetInt32());

        byte[] buffer = Plain(zip, $"nodes/{leaf.GetProperty("index").GetInt32()}/geometries/0.bin.gz");
        Assert.Equal((uint)vertices, BinaryPrimitives.ReadUInt32LittleEndian(buffer));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)));
        Assert.Equal(8 + (16 * vertices) + (16 * 2), buffer.Length);

        int ids = 8 + (16 * vertices), ranges = ids + 16;
        Assert.Equal(7ul, BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(ids)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ranges)));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ranges + 4)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ranges + 8)));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(ranges + 12)));

        // Strings: count, total bytes, each count with its terminator, then the bytes; a missing double is NaN.
        byte[] names = Plain(zip, $"nodes/{leaf.GetProperty("index").GetInt32()}/attributes/f_1/0.bin.gz");
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(names));
        Assert.Equal("Kule\0Düz\0", Encoding.UTF8.GetString(names, 16, names.Length - 16));
        byte[] heights = Plain(zip, $"nodes/{leaf.GetProperty("index").GetInt32()}/attributes/f_2/0.bin.gz");
        Assert.Equal(8 + 16, heights.Length);
        Assert.Equal(20.0, BinaryPrimitives.ReadDoubleLittleEndian(heights.AsSpan(8)));
        Assert.True(double.IsNaN(BinaryPrimitives.ReadDoubleLittleEndian(heights.AsSpan(16))));

        // Every hash record names an entry and the offset of its local header.
        byte[] table = Read(zip, "@specialIndexFileHASH128@");
        Assert.Equal(24 * (zip.Entries.Count - 1), table.Length);
        // MD5 is the format's index key (I3S slpk_hashtable), not a security measure.
#pragma warning disable CA5351
        Dictionary<string, string> byDigest = zip.Entries.Take(zip.Entries.Count - 1)
            .ToDictionary(e => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(e.FullName.ToLowerInvariant()))), e => e.FullName);
#pragma warning restore CA5351
        for (int i = 0; i < table.Length; i += 24)
        {
            string name = byDigest[Convert.ToHexString(table, i, 16)];
            int offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(i + 16));
            Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(offset)));
            Assert.Equal(name, Encoding.UTF8.GetString(package, offset + 30, BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(offset + 26))));
        }
    }

    [Fact]
    public void More_buildings_than_a_node_holds_are_split_into_quarters()
    {
        List<SceneBuilding> buildings = [];
        for (int i = 0; i < SceneLayerPackageWriter.MaximumPerNode * 3; i++)
        {
            buildings.Add(new(i + 1, [[Square(29.0 + (i % 40 * 0.0003), 41.0 + (i / 40 * 0.0003), 0.0001)]], 0, 10, []));
        }

        byte[] package = SceneLayerPackageWriter.Write("Many", [], buildings, [255, 255, 255], DateTimeOffset.UnixEpoch);
        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        JsonElement nodes = Json(zip, "nodepages/0.json.gz").GetProperty("nodes");

        int carried = nodes.EnumerateArray().Where(n => n.TryGetProperty("mesh", out _))
            .Sum(n => n.GetProperty("mesh").GetProperty("geometry").GetProperty("featureCount").GetInt32());
        Assert.Equal(buildings.Count, carried);
        Assert.All(nodes.EnumerateArray().Where(n => n.TryGetProperty("mesh", out _)),
            n => Assert.True(n.GetProperty("mesh").GetProperty("geometry").GetProperty("featureCount").GetInt32() <= SceneLayerPackageWriter.MaximumPerNode));
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        using Stream stream = zip.GetEntry(name)!.Open();
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] Plain(ZipArchive zip, string name)
    {
        using GZipStream gzip = new(new MemoryStream(Read(zip, name)), CompressionMode.Decompress);
        using MemoryStream copy = new();
        gzip.CopyTo(copy);
        return copy.ToArray();
    }

    private static JsonElement Json(ZipArchive zip, string name) => JsonDocument.Parse(Plain(zip, name)).RootElement.Clone();
}
