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
/// ADR-280 condition 2: a 3D object's glTF model, placed by the transform its layer keeps, written into the scene layer
/// as a building's triangles are, in its own colours.
/// </summary>
public sealed class SceneModelTests
{
    private const double Lon = 29.0, Lat = 41.0;
    private static readonly SceneField[] Fields = [new("name", FieldType.Text), new("globalid", FieldType.Guid, GlobalId: true)];
    private static readonly int[] Grey = [200, 200, 200];
    private static readonly int[] RootNodes = [0];

    /// <summary>A glb of one box, <paramref name="w"/> wide (x), <paramref name="h"/> tall (y) and <paramref name="d"/> deep
    /// (z), standing on its origin, in one linear colour, its node moved by <paramref name="lift"/> up.</summary>
    private static byte[] Box(float w, float h, float d, float[] linear, float lift = 0, string? extension = null)
    {
        float x = w / 2, z = d / 2;
        float[][] c = [[-x, 0, -z], [x, 0, -z], [x, h, -z], [-x, h, -z], [-x, 0, z], [x, 0, z], [x, h, z], [-x, h, z]];
        float[] positions = [.. c.SelectMany(p => p)];
        ushort[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 3, 7, 6, 3, 6, 2, 0, 1, 5, 0, 5, 4];
        byte[] bin = new byte[(positions.Length * 4) + (indices.Length * 2)];
        Buffer.BlockCopy(positions, 0, bin, 0, positions.Length * 4);
        Buffer.BlockCopy(indices, 0, bin, positions.Length * 4, indices.Length * 2);
        string json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = RootNodes } },
            nodes = new[] { new { mesh = 0, translation = new[] { 0f, lift, 0f } } },
            meshes = new[] { new { primitives = new[] { new Dictionary<string, object>
            {
                ["attributes"] = new { POSITION = 0 }, ["indices"] = 1, ["material"] = 0,
                ["extensions"] = extension is null ? new Dictionary<string, object>() : new Dictionary<string, object> { [extension] = new { } },
            } } } },
            materials = new[] { new { pbrMetallicRoughness = new { baseColorFactor = new[] { linear[0], linear[1], linear[2], 1f } } } },
            accessors = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = 8, type = "VEC3", min = new[] { -x, 0, -z }, max = new[] { x, h, z } },
                new { bufferView = 1, componentType = 5123, count = indices.Length, type = "SCALAR" },
            },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = positions.Length * 4 },
                new { buffer = 0, byteOffset = positions.Length * 4, byteLength = indices.Length * 2 },
            },
            buffers = new[] { new { byteLength = bin.Length } },
        });
        byte[] j = Encoding.UTF8.GetBytes(json);
        int jl = (j.Length + 3) / 4 * 4, bl = (bin.Length + 3) / 4 * 4;
        using MemoryStream m = new();
        using BinaryWriter wr = new(m);
        wr.Write(0x46546C67u);
        wr.Write(2u);
        wr.Write((uint)(12 + 8 + jl + 8 + bl));
        wr.Write((uint)jl);
        wr.Write(0x4E4F534Au);
        wr.Write(j);
        for (int i = j.Length; i < jl; i++) wr.Write((byte)' ');
        wr.Write((uint)bl);
        wr.Write(0x004E4942u);
        wr.Write(bin);
        for (int i = bin.Length; i < bl; i++) wr.Write((byte)0);
        return m.ToArray();
    }

    private static SceneBuilding Placed(long id, SceneModel model) => new(id, [], 0, 0, ["zz", "{00000000-0000-4000-8000-00000000000" + id + "}"], model);

    /// <summary>Each vertex of the leaf, absolute (longitude, latitude, metres), with its colour.</summary>
    private static List<(double Lon, double Lat, double Z, byte R, byte G, byte B)> Leaf(byte[] package)
    {
        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        JsonElement page = JsonDocument.Parse(Plain(zip, "nodepages/0.json.gz")).RootElement;
        JsonElement node = page.GetProperty("nodes")[1];
        double[] centre = [.. node.GetProperty("obb").GetProperty("center").EnumerateArray().Select(v => v.GetDouble())];
        byte[] g = Plain(zip, "nodes/1/geometries/0.bin.gz");
        int v = (int)BinaryPrimitives.ReadUInt32LittleEndian(g);
        List<(double, double, double, byte, byte, byte)> points = [];
        for (int i = 0; i < v; i++)
        {
            int p = 8 + (i * 12), c = 8 + (v * 12) + (i * 4);
            points.Add((centre[0] + BinaryPrimitives.ReadSingleLittleEndian(g.AsSpan(p)), centre[1] + BinaryPrimitives.ReadSingleLittleEndian(g.AsSpan(p + 4)),
                centre[2] + BinaryPrimitives.ReadSingleLittleEndian(g.AsSpan(p + 8)), g[c], g[c + 1], g[c + 2]));
        }

        return points;
    }

    private static byte[] Plain(ZipArchive zip, string name)
    {
        using GZipStream gzip = new(zip.GetEntry(name)!.Open(), CompressionMode.Decompress);
        using MemoryStream copy = new();
        gzip.CopyTo(copy);
        return copy.ToArray();
    }

    private static double EastMetres(double lon) => (lon - Lon) * Math.PI / 180 * 6_378_137.0 * Math.Cos(Lat * Math.PI / 180);

    private static double NorthMetres(double lat) => (lat - Lat) * Math.PI / 180 * 6_378_137.0;

    [Fact]
    public void A_glb_is_read_as_its_triangles_moved_by_its_node_in_its_materials_colour()
    {
        GltfTriangles read = GltfTriangles.Read(Box(2, 3, 4, [1f, 0f, 0f], lift: 5));
        Assert.Equal(36, read.Points.Count);
        Assert.Equal(0, read.Skipped);
        Assert.Equal(5, read.Points.Min(p => p.Y), 5);
        Assert.Equal(8, read.Points.Max(p => p.Y), 5);
        Assert.All(read.Colours, c => Assert.Equal((255, 0, 0, 255), ((int)c.R, (int)c.G, (int)c.B, (int)c.A)));
        Assert.Throws<FormatException>(() => GltfTriangles.Read(Encoding.ASCII.GetBytes("not a glb at all, at all")));

        // An extension that only describes the triangles is read past (Assimp writes FB_ngon_encoding on every
        // primitive); one that stores them another way is not read, and is counted.
        Assert.Equal(36, GltfTriangles.Read(Box(2, 3, 4, [1f, 0f, 0f], extension: "FB_ngon_encoding")).Points.Count);
        GltfTriangles draco = GltfTriangles.Read(Box(2, 3, 4, [1f, 0f, 0f], extension: "KHR_draco_mesh_compression"));
        Assert.Empty(draco.Points);
        Assert.Equal(1, draco.Skipped);
    }

    [Fact]
    public void A_model_is_written_where_its_origin_is_in_its_own_colour_and_its_globalid_is_typed_so()
    {
        // 10 m east-west (glTF x), 20 m tall (y), 4 m north-south (z), at 12 m above the origin's datum.
        byte[] package = SceneLayerPackageWriter.Write("zz", Fields,
            [Placed(1, SceneModel.At(Box(10, 20, 4, [0f, 0.2158605f, 1f]), "h1", Lon, Lat, 12))], Grey, DateTimeOffset.UtcNow);
        List<(double Lon, double Lat, double Z, byte R, byte G, byte B)> leaf = Leaf(package);

        Assert.Equal(36, leaf.Count);
        Assert.Equal(-5, EastMetres(leaf.Min(p => p.Lon)), 1);
        Assert.Equal(5, EastMetres(leaf.Max(p => p.Lon)), 1);
        Assert.Equal(-2, NorthMetres(leaf.Min(p => p.Lat)), 1);
        Assert.Equal(2, NorthMetres(leaf.Max(p => p.Lat)), 1);
        Assert.Equal(12, leaf.Min(p => p.Z), 2);
        Assert.Equal(32, leaf.Max(p => p.Z), 2);
        // Linear (0, 0.2159, 1) is sRGB (0, 128, 255): the model's colour, not the layer's grey.
        Assert.All(leaf, p => Assert.Equal((0, 128, 255), ((int)p.R, (int)p.G, (int)p.B)));

        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        JsonElement layer = JsonDocument.Parse(Plain(zip, "3dSceneLayer.json.gz")).RootElement;
        Assert.Equal("esriFieldTypeGlobalID", layer.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("name").GetString() == "globalid").GetProperty("type").GetString());
    }

    [Fact]
    public void A_model_is_scaled_turned_and_moved_as_its_transform_says()
    {
        // Scaled twice up only, turned 90° about up (east becomes north), moved 100 m east.
        SceneModel model = new(Box(10, 20, 4, [1f, 1f, 1f]), "h2", Lon, Lat, 0, (100, 0, 0), (1, 1, 2), (0, 0, 1), 90);
        List<(double Lon, double Lat, double Z, byte R, byte G, byte B)> leaf =
            Leaf(SceneLayerPackageWriter.Write("zz", Fields, [Placed(1, model)], Grey, DateTimeOffset.UtcNow));

        Assert.Equal(40, leaf.Max(p => p.Z), 2);
        // The 10 m side now runs north-south and the 4 m side east-west, about 100 m east.
        Assert.Equal(4, EastMetres(leaf.Max(p => p.Lon)) - EastMetres(leaf.Min(p => p.Lon)), 1);
        Assert.Equal(10, NorthMetres(leaf.Max(p => p.Lat)) - NorthMetres(leaf.Min(p => p.Lat)), 1);
        Assert.Equal(100, (EastMetres(leaf.Max(p => p.Lon)) + EastMetres(leaf.Min(p => p.Lon))) / 2, 1);
    }

    [Fact]
    public void A_moved_model_is_a_changed_feature_to_the_partial_update()
    {
        byte[] glb = Box(10, 20, 4, [1f, 1f, 1f]);
        SceneBuilding first = Placed(1, SceneModel.At(glb, "h3", Lon, Lat, 0));
        SceneBuilding other = Placed(2, SceneModel.At(glb, "h3", Lon + 0.01, Lat, 0));
        byte[] before = SceneLayerPackageWriter.Write("zz", Fields, [first, other], Grey, DateTimeOffset.UtcNow);

        SceneLayerPackageWriter.Updated? same = SceneLayerPackageWriter.Update(before, "zz", Fields, [first, other], Grey, DateTimeOffset.UtcNow);
        Assert.NotNull(same);
        Assert.Equal(0, same.Changed);

        SceneBuilding moved = first with { Model = first.Model! with { Translation = (5, 0, 0) } };
        SceneLayerPackageWriter.Updated? after = SceneLayerPackageWriter.Update(before, "zz", Fields, [moved, other], Grey, DateTimeOffset.UtcNow, wholeAbove: 1);
        Assert.NotNull(after);
        Assert.Equal(1, after.Changed);
    }
}
