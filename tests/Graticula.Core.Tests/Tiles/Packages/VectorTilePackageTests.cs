using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Tests.Shared;
using Graticula.Tiles;
using Graticula.Tiles.Packages;
using Xunit;

namespace Graticula.Core.Tests.Tiles.Packages;

/// <summary>A VTPK's zip layout: where the documents and the bundles go, and that nothing is deflated — ADR-098.</summary>
public sealed class VectorTilePackageTests
{
    private static byte[] Gzip(byte[] bytes)
    {
        using MemoryStream output = new();

        using (GZipStream zip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(bytes);
        }

        return output.ToArray();
    }

    [Fact]
    public async Task Documents_and_bundles_sit_where_the_package_layout_puts_them_and_a_tile_reads_back()
    {
        byte[] tile = Encoding.UTF8.GetBytes("a vector tile");
        byte[] stored = Gzip(tile);

        using MemoryStream output = new();

        using (VectorTilePackage package = new(output))
        {
            package.Add(VectorTilePackage.ServiceDocument, "{}"u8);
            package.Add(VectorTilePackage.StyleDocument, "{\"version\":8}"u8);
            package.Add(VectorTilePackage.ResourcePath("sprites/sprite.json"), "{}"u8);
            package.Add(VectorTilePackage.ItemInfo, VectorTilePackage.ItemInfoDocument("roads", "Roads", "d", 26, 36, 45, 42));
            package.Add(VectorTilePackage.PackageInfo, VectorTilePackage.PackageInfoDocument(Guid.NewGuid(), "roads", DateTimeOffset.UnixEpoch));

            // Level 9, row 190, column 300: the bundle at row 128, column 256.
            CompactCacheBundle.Layout layout = CompactCacheBundle.Plan(128, 256, [new BundleTile(190, 300, stored.Length, 0)]);

            await package.AddBundleAsync(
                9, layout, (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(stored), CancellationToken.None);
        }

        byte[] zip = output.ToArray();

        using ZipArchive read = new(new MemoryStream(zip), ZipArchiveMode.Read);

        Assert.Equal(
            [
                "esriinfo/item.pkinfo",
                "esriinfo/iteminfo.xml",
                "p12/resources/sprites/sprite.json",
                "p12/resources/styles/root.json",
                "p12/root.json",
                "p12/tile/L09/R0080C0100.bundle",
            ],
            read.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        Assert.Equal(tile, TilePackageReaders.VtpkTile(read, 9, 300, 190));
        Assert.Null(TilePackageReaders.VtpkTile(read, 9, 301, 190));
        Assert.Null(TilePackageReaders.VtpkTile(read, 8, 150, 95));

        // <b>Stored, not deflated</b>: every local file header's compression method is 0 (APPNOTE 4.4.5).
        int headers = 0;

        for (int at = 0; at + 30 <= zip.Length; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at)) == 0x04034b50)
            {
                Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 8)));
                headers++;
                at += 29;
            }
        }

        Assert.True(headers >= 6);
    }

    [Fact]
    public void A_resource_path_cannot_leave_the_resources_folder()
    {
        Assert.Equal("p12/resources/fonts/A B/0-255.pbf", VectorTilePackage.ResourcePath("fonts/A B/0-255.pbf"));
        Assert.Throws<ArgumentException>(() => VectorTilePackage.ResourcePath("../root.json"));
        Assert.Throws<ArgumentException>(() => VectorTilePackage.ResourcePath("/etc/passwd"));
        Assert.Throws<ArgumentException>(() => VectorTilePackage.ResourcePath("a\\b"));
    }

    [Fact]
    public void The_item_documents_escape_what_they_carry()
    {
        string item = Encoding.UTF8.GetString(VectorTilePackage.ItemInfoDocument("a<b", "t&t", "d", 1.5, 2, 3, 4));
        string package = Encoding.UTF8.GetString(VectorTilePackage.PackageInfoDocument(Guid.Empty, "a<b", DateTimeOffset.UnixEpoch));

        Assert.Contains("<name>a&lt;b</name>", item, StringComparison.Ordinal);
        Assert.Contains("<title>t&amp;t</title>", item, StringComparison.Ordinal);
        Assert.Contains("<xmin>1.5</xmin>", item, StringComparison.Ordinal);
        Assert.Contains("<type>Vector Tile Package</type>", package, StringComparison.Ordinal);
        Assert.Contains("<created>1970-01-01T00:00:00Z</created>", package, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_can_keep_only_some_levels_and_still_counts_exactly()
    {
        const double Half = TileAddress.WebMercatorHalfExtent;
        TileSeedPlan all = TileSeedPlan.For(new Envelope(-Half, -Half, Half, Half), 0, 5);

        TileSeedPlan some = all.Keeping(new System.Collections.Generic.HashSet<int> { 1, 3, 4, 9 });

        Assert.Equal([1, 3, 4], some.Levels.Select(level => level.Z));
        Assert.Equal(4 + 64 + 256, some.Total);
        Assert.Equal(all.Area, some.Area);
        Assert.Throws<ArgumentException>(() => all.Keeping(new System.Collections.Generic.HashSet<int> { 7 }));
    }
}
