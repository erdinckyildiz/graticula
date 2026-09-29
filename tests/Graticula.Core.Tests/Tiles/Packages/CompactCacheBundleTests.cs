using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Tests.Shared;
using Graticula.Tiles.Packages;
using Xunit;

namespace Graticula.Core.Tests.Tiles.Packages;

/// <summary>
/// A compact cache V2 bundle, written and read back by the specification rather than by the writer — ADR-098.
/// </summary>
/// <remarks>
/// <b>The reader is <c>TilePackageReaders</c>, written from Esri's <em>CompactCacheV2.md</em></b>: the record at
/// <c>64 + 8 × (128 × (row mod 128) + (column mod 128))</c>, its offset in bits 0–39 and its size in 40–63, and the
/// four-byte size just before the tile. Every expected number below is worked from that description.
/// </remarks>
public sealed class CompactCacheBundleTests
{
    private static byte[] Bytes(int length, byte fill) => [.. Enumerable.Repeat(fill, length)];

    private static async Task<byte[]> WriteAsync(int originRow, int originColumn, List<(int Row, int Column, byte[] Tile)> tiles)
    {
        CompactCacheBundle.Layout layout = CompactCacheBundle.Plan(
            originRow, originColumn, [.. tiles.Select((t, i) => new BundleTile(t.Row, t.Column, t.Tile.Length, i))]);

        using MemoryStream output = new();
        long written = await CompactCacheBundle.WriteAsync(
            output, layout, (tile, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(tiles[(int)tile.Key].Tile), CancellationToken.None);

        Assert.Equal(output.Length, written);
        Assert.Equal(layout.FileSize, written);
        return output.ToArray();
    }

    [Fact]
    public async Task The_header_carries_the_specifications_values()
    {
        byte[] bundle = await WriteAsync(0, 0, [(0, 0, Bytes(10, 1)), (5, 7, Bytes(300, 2))]);

        var header = TilePackageReaders.BundleHeader(bundle);

        Assert.Equal(3u, header.Version);
        Assert.Equal(16384u, header.RecordCount);
        Assert.Equal(300u, header.MaxTileSize);
        Assert.Equal(5u, header.OffsetBytes);
        Assert.Equal(0ul, header.Slack);
        Assert.Equal((ulong)bundle.Length, header.FileSize);
        Assert.Equal(40ul, header.UserHeaderOffset);
        Assert.Equal(20u + 131072u, header.UserHeaderSize);
        Assert.Equal((3u, 16u, 16384u, 5u), (header.Legacy1, header.Legacy2, header.Legacy3, header.Legacy4));
        Assert.Equal(131072u, header.IndexSize);
    }

    [Fact]
    public async Task Offsets_and_sizes_are_where_the_specification_puts_them()
    {
        // Two tiles, laid out in index order: (0,0) first, then row 5 column 7.
        byte[] first = Bytes(10, 1);
        byte[] second = Bytes(300, 2);
        byte[] bundle = await WriteAsync(0, 0, [(5, 7, second), (0, 0, first)]);

        // The data starts after the 64-byte header and the 131,072-byte index; each tile after its 4-byte size.
        const long DataStart = 64 + 131072;
        ulong recordFirst = BitConverter.ToUInt64(bundle, 64);
        ulong recordSecond = BitConverter.ToUInt64(bundle, (int)(64 + (8 * ((128 * 5) + 7))));

        Assert.Equal((ulong)(DataStart + 4), recordFirst & ((1UL << 40) - 1));
        Assert.Equal(10ul, recordFirst >> 40);
        Assert.Equal((ulong)(DataStart + 4 + 10 + 4), recordSecond & ((1UL << 40) - 1));
        Assert.Equal(300ul, recordSecond >> 40);
        Assert.Equal(DataStart + 4 + 10 + 4 + 300, bundle.Length);

        Assert.Equal(first, TilePackageReaders.BundleTile(bundle, 0, 0));
        Assert.Equal(second, TilePackageReaders.BundleTile(bundle, 5, 7));
    }

    [Fact]
    public async Task A_position_with_no_tile_says_size_zero()
    {
        byte[] bundle = await WriteAsync(128, 256, [(130, 300, Bytes(5, 9))]);

        Assert.Null(TilePackageReaders.BundleTile(bundle, 128, 256));
        Assert.Null(TilePackageReaders.BundleTile(bundle, 255, 383));
        Assert.Equal(Bytes(5, 9), TilePackageReaders.BundleTile(bundle, 130, 300));

        // Every record but one is zero.
        int nonZero = 0;

        for (int i = 0; i < 16384; i++)
        {
            nonZero += BitConverter.ToUInt64(bundle, 64 + (8 * i)) == 0 ? 0 : 1;
        }

        Assert.Equal(1, nonZero);
    }

    [Fact]
    public async Task A_full_bundle_round_trips_every_tile()
    {
        List<(int, int, byte[])> tiles = [];

        for (int r = 0; r < 128; r += 3)
        {
            for (int c = 0; c < 128; c += 5)
            {
                tiles.Add((256 + r, 128 + c, Bytes(1 + ((r * 7) + c) % 50, (byte)(r ^ c))));
            }
        }

        byte[] bundle = await WriteAsync(256, 128, tiles);

        foreach ((int r, int c, byte[] tile) in tiles)
        {
            Assert.Equal(tile, TilePackageReaders.BundleTile(bundle, r, c));
        }
    }

    [Fact]
    public void The_file_name_is_the_origin_in_lowercase_hexadecimal_of_four_digits_or_more()
    {
        Assert.Equal("R0000C0000.bundle", CompactCacheBundle.FileName(0, 0));
        Assert.Equal("R0080C0100.bundle", CompactCacheBundle.FileName(128, 256));
        Assert.Equal("R3f80C1ff80.bundle", CompactCacheBundle.FileName(16256, 130944));
        Assert.Equal("L08", CompactCacheBundle.LevelFolder(8));
        Assert.Equal("L22", CompactCacheBundle.LevelFolder(22));
        Assert.Equal((128, 256), CompactCacheBundle.OriginOf(200, 300));
        Assert.Throws<ArgumentException>(() => CompactCacheBundle.FileName(1, 0));
    }

    [Fact]
    public void A_tile_outside_the_bundle_twice_or_empty_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CompactCacheBundle.Plan(0, 0, [new BundleTile(128, 0, 1, 0)]));
        Assert.Throws<ArgumentException>(() => CompactCacheBundle.Plan(0, 0, [new BundleTile(1, 1, 1, 0), new BundleTile(1, 1, 2, 1)]));
        Assert.Throws<ArgumentException>(() => CompactCacheBundle.Plan(0, 0, [new BundleTile(1, 1, 0, 0)]));
        Assert.Throws<ArgumentException>(() => CompactCacheBundle.Plan(0, 0, [new BundleTile(1, 1, 1 << 24, 0)]));
    }

    [Fact]
    public async Task Bytes_that_are_not_the_planned_length_are_refused()
    {
        CompactCacheBundle.Layout layout = CompactCacheBundle.Plan(0, 0, [new BundleTile(0, 0, 4, 0)]);
        using MemoryStream output = new();

        await Assert.ThrowsAsync<InvalidDataException>(() => CompactCacheBundle.WriteAsync(
            output, layout, (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[3]), CancellationToken.None));
    }

    [Fact]
    public void The_index_offset_is_the_specifications_formula()
    {
        Assert.Equal(64, CompactCacheBundle.IndexOffsetOf(0, 0));
        Assert.Equal(64 + (8 * ((128 * 5) + 7)), CompactCacheBundle.IndexOffsetOf(133, 263));
        Assert.Equal(64 + (8 * 16383), CompactCacheBundle.IndexOffsetOf(127, 127));
    }
}
