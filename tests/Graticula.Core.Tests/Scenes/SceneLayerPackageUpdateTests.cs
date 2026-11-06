using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Graticula.Features;
using Graticula.Scenes;
using Xunit;

namespace Graticula.Core.Tests.Scenes;

/// <summary>
/// ADR-277 §5.2: a partial update writes again only the nodes whose buildings changed, and copies every other node from
/// the package as it was.
/// </summary>
public sealed class SceneLayerPackageUpdateTests
{
    private static readonly SceneField[] Fields = [new("floors", FieldType.Integer, "Floors")];
    private static readonly int[] Colour = [200, 200, 200];
    private static readonly DateTimeOffset When = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A city of 40 × 40 square buildings, so the tree has several leaves.</summary>
    private static List<SceneBuilding> City(Func<int, double>? height = null)
    {
        List<SceneBuilding> city = [];
        for (int i = 0; i < 1600; i++)
        {
            double lon = 29 + ((i % 40) * 0.0005), lat = 41 + ((i / 40) * 0.0005);
            IReadOnlyList<(double, double)> square = [(lon, lat), (lon + 0.0002, lat), (lon + 0.0002, lat + 0.0002), (lon, lat + 0.0002), (lon, lat)];
            city.Add(new SceneBuilding(i + 1, [[square]], 0, height?.Invoke(i) ?? 10 + (i % 7), [i % 9]));
        }

        return city;
    }

    private static Dictionary<string, byte[]> Entries(byte[] package)
    {
        using ZipArchive zip = new(new MemoryStream(package));
        return zip.Entries.ToDictionary(e => e.FullName, e => { using Stream s = e.Open(); using MemoryStream m = new(); s.CopyTo(m); return m.ToArray(); });
    }

    private static int Features(byte[] package)
    {
        int total = 0;
        foreach ((string name, byte[] bytes) in Entries(package).Where(e => e.Key.StartsWith("nodepages/", StringComparison.Ordinal)))
        {
            using GZipStream gz = new(new MemoryStream(bytes), CompressionMode.Decompress);
            foreach (JsonElement node in JsonDocument.Parse(gz).RootElement.GetProperty("nodes").EnumerateArray())
            {
                if (!node.TryGetProperty("children", out _) && node.TryGetProperty("mesh", out JsonElement mesh))
                {
                    total += mesh.GetProperty("geometry").GetProperty("featureCount").GetInt32();
                }
            }
        }

        return total;
    }

    [Fact]
    public void Nothing_changed_leaves_the_package_as_it_was()
    {
        byte[] before = SceneLayerPackageWriter.Write("zz", Fields, City(), Colour, When);
        SceneLayerPackageWriter.Updated? updated = SceneLayerPackageWriter.Update(before, "zz", Fields, City(), Colour, When);

        Assert.NotNull(updated);
        Assert.Equal(0, updated!.Changed);
        Assert.Same(before, updated.Package);
    }

    [Fact]
    public void One_building_raised_rewrites_its_leaf_and_the_nodes_above_it_and_copies_the_rest()
    {
        byte[] before = SceneLayerPackageWriter.Write("zz", Fields, City(), Colour, When);
        SceneLayerPackageWriter.Updated? updated = SceneLayerPackageWriter.Update(
            before, "zz", Fields, City(i => i == 777 ? 99 : 10 + (i % 7)), Colour, When);

        Assert.NotNull(updated);
        Assert.Equal(1, updated!.Changed);
        Dictionary<string, byte[]> old = Entries(before), now = Entries(updated.Package);
        string[] geometries = [.. old.Keys.Where(k => k.EndsWith("/geometries/0.bin.gz", StringComparison.Ordinal))];
        int same = geometries.Count(k => now.TryGetValue(k, out byte[]? b) && b.AsSpan().SequenceEqual(old[k]));

        Assert.True(updated.Rewritten >= 2 && updated.Rewritten < geometries.Length, $"{updated.Rewritten} of {geometries.Length} nodes were written again.");
        Assert.Equal(geometries.Length - updated.Rewritten, same);
        Assert.Equal(1600, Features(updated.Package));
    }

    [Fact]
    public void A_new_building_joins_the_nearest_leaf_and_a_deleted_one_leaves_its_own()
    {
        List<SceneBuilding> city = City();
        byte[] before = SceneLayerPackageWriter.Write("zz", Fields, city, Colour, When);
        IReadOnlyList<(double, double)> square = [(29.001, 41.001), (29.0012, 41.001), (29.0012, 41.0012), (29.001, 41.0012), (29.001, 41.001)];
        List<SceneBuilding> edited = [.. city.Where(b => b.Id != 5), new SceneBuilding(5000, [[square]], 0, 30, [1])];

        SceneLayerPackageWriter.Updated? updated = SceneLayerPackageWriter.Update(before, "zz", Fields, edited, Colour, When);

        Assert.NotNull(updated);
        Assert.Equal(2, updated!.Changed);
        Assert.Equal(1600, Features(updated.Package));
    }

    [Fact]
    public void Much_changed_or_no_plan_is_made_whole_again()
    {
        byte[] before = SceneLayerPackageWriter.Write("zz", Fields, City(), Colour, When);

        Assert.Null(SceneLayerPackageWriter.Update(before, "zz", Fields, City(i => 50 + i), Colour, When));
        Assert.Null(SceneLayerPackageWriter.Update([.. EmptyZip()], "zz", Fields, City(), Colour, When));
    }

    private static byte[] EmptyZip()
    {
        using MemoryStream m = new();
        using (ZipArchive zip = new(m, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("3dSceneLayer.json.gz");
        }

        return m.ToArray();
    }
}
