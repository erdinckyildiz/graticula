using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Tests.Shared;
using Graticula.Tiles.Packages;
using Xunit;

namespace Graticula.Core.Tests.Tiles.Packages;

/// <summary>
/// A PMTiles v3 archive, written and read back by the specification rather than by the writer — ADR-098.
/// </summary>
/// <remarks>
/// <b>The reader is <c>TilePackageReaders</c></b>: the header at its offsets, the five varint runs of a directory with
/// the <c>0 = contiguous</c> offset rule, leaves marked by a run length of 0, and the Hilbert tile id computed there
/// independently — so a tile found by z/x/y through the reader is found by the specification's address, not the
/// writer's.
/// </remarks>
public sealed class PmTilesWriterTests
{
    private static readonly byte[] Metadata = Encoding.UTF8.GetBytes(
        """{"name":"t","vector_layers":[{"id":"roads","fields":{}}]}""");

    private static PmTilesDescription Description(int min, int max) =>
        new(min, max, -10, -5, 20, 15, min, 5, 5, Metadata);

    private static UInt128 ContentOf(byte[] bytes) =>
        new(BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(bytes), 0),
            BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(bytes), 8));

    private static async Task<byte[]> WriteAsync(
        List<(int Z, int X, int Y, byte[] Tile)> tiles, int min, int max, byte compression = PmTiles.CompressionNone)
    {
        PmTiles.Layout layout = PmTiles.Plan(
            [.. tiles.Select((t, i) => new PmTile(t.Z, t.X, t.Y, t.Tile.Length, ContentOf(t.Tile), i))],
            Description(min, max),
            compression);

        using MemoryStream output = new();
        long written = await PmTiles.WriteAsync(
            output, layout, (tile, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(tiles[(int)tile.Key].Tile),
            CancellationToken.None);

        Assert.Equal(output.Length, written);
        Assert.Equal(layout.FileSize, written);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0, 0, 0, 0ul)]
    [InlineData(1, 0, 0, 1ul)]
    [InlineData(1, 0, 1, 2ul)]
    [InlineData(1, 1, 1, 3ul)]
    [InlineData(1, 1, 0, 4ul)]
    [InlineData(2, 0, 0, 5ul)]
    public void The_tile_id_follows_the_specifications_examples(int z, int x, int y, ulong id)
    {
        // 0/0/0 → 0, 1/0/0 → 1 and 1/1/0 → 4 are the specification's own; the rest walk the level-1 curve and the
        // start of level 2.
        Assert.Equal(id, PmTiles.TileId(z, x, y));
        Assert.Equal(id, TilePackageReaders.HilbertId(z, x, y));
    }

    [Fact]
    public void The_writer_and_the_readers_tile_ids_agree_everywhere_on_several_levels()
    {
        for (int z = 0; z <= 6; z++)
        {
            HashSet<ulong> seen = [];
            int n = 1 << z;

            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                {
                    ulong id = PmTiles.TileId(z, x, y);
                    Assert.Equal(TilePackageReaders.HilbertId(z, x, y), id);
                    Assert.True(seen.Add(id));
                }
            }

            // A level's ids are a run: from the count of every lower level, one per tile.
            ulong first = (((ulong)1 << (2 * z)) - 1) / 3;
            Assert.Equal(first, seen.Min());
            Assert.Equal(first + (ulong)(n * n) - 1, seen.Max());
        }

        Assert.Equal(TilePackageReaders.HilbertId(12, 3423, 1763), PmTiles.TileId(12, 3423, 1763));
    }

    [Fact]
    public async Task The_header_says_where_every_section_is_and_what_the_tiles_are()
    {
        byte[] archive = await WriteAsync([(0, 0, 0, [1, 2, 3]), (1, 1, 0, [4, 5])], 0, 1, PmTiles.CompressionGzip);

        TilePackageReaders.PmHeader h = TilePackageReaders.PmTilesHeader(archive);

        Assert.Equal(127ul, h.RootOffset);
        Assert.True(h.RootOffset + h.RootLength <= 16384);
        Assert.Equal(h.RootOffset + h.RootLength, h.MetadataOffset);
        Assert.Equal(h.MetadataOffset + h.MetadataLength, h.LeavesOffset);
        Assert.Equal(0ul, h.LeavesLength);
        Assert.Equal(h.LeavesOffset + h.LeavesLength, h.DataOffset);
        Assert.Equal((ulong)archive.Length, h.DataOffset + h.DataLength);
        Assert.Equal(5ul, h.DataLength);
        Assert.Equal((2ul, 2ul, 2ul), (h.Addressed, h.Entries, h.Contents));
        Assert.Equal(1, h.Clustered);
        Assert.Equal(2, h.InternalCompression);
        Assert.Equal(2, h.TileCompression);
        Assert.Equal(1, h.TileType);
        Assert.Equal((0, 1), (h.MinZoom, h.MaxZoom));
        Assert.Equal((-100_000_000, -50_000_000, 200_000_000, 150_000_000), (h.MinLon, h.MinLat, h.MaxLon, h.MaxLat));
        Assert.Equal((0, 50_000_000, 50_000_000), (h.CenterZoom, h.CenterLon, h.CenterLat));

        // The metadata is gzip JSON with vector_layers, as a vector tileset's must be.
        byte[] metadata = TilePackageReaders.Gunzip(archive.AsSpan((int)h.MetadataOffset, (int)h.MetadataLength).ToArray());
        using JsonDocument parsed = JsonDocument.Parse(metadata);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.GetProperty("vector_layers").ValueKind);
    }

    [Fact]
    public async Task The_header_states_the_levels_that_have_tiles_not_the_levels_asked_for()
    {
        // 0-14 asked for, and only 10 and 11 had anything in them: small features vanish at the low levels and an
        // empty tile is left out. `pmtiles verify` refuses a MinZoom with no tile at it, and a centre outside the range
        // (2026-09-29, the fixture's parcels).
        byte[] archive = await WriteAsync([(10, 600, 390, [1]), (11, 1200, 780, [2]), (11, 1201, 780, [3])], 0, 14);

        TilePackageReaders.PmHeader h = TilePackageReaders.PmTilesHeader(archive);

        Assert.Equal((10, 11), (h.MinZoom, h.MaxZoom));
        Assert.Equal(10, h.CenterZoom);
    }

    [Fact]
    public async Task A_tile_is_found_by_z_x_y_through_the_readers_own_addressing()
    {
        List<(int, int, int, byte[])> tiles = [];

        for (int z = 0; z <= 4; z++)
        {
            for (int x = 0; x < 1 << z; x += 2)
            {
                for (int y = 0; y < 1 << z; y += 3)
                {
                    tiles.Add((z, x, y, Encoding.UTF8.GetBytes($"{z}/{x}/{y}")));
                }
            }
        }

        byte[] archive = await WriteAsync(tiles, 0, 4);

        foreach ((int z, int x, int y, byte[] tile) in tiles)
        {
            Assert.Equal(tile, TilePackageReaders.PmTilesTile(archive, z, x, y));
        }

        Assert.Null(TilePackageReaders.PmTilesTile(archive, 4, 1, 0));
    }

    [Fact]
    public async Task Equal_tiles_are_stored_once_and_a_run_of_them_is_one_entry()
    {
        byte[] sea = [9, 9, 9, 9];

        // Level 1 in tile id order is 0/0, 0/1, 1/1, 1/0: ids 1 to 4, one run of four.
        byte[] archive = await WriteAsync(
            [(1, 0, 0, sea), (1, 0, 1, sea), (1, 1, 1, sea), (1, 1, 0, sea), (0, 0, 0, [1])], 0, 1);

        TilePackageReaders.PmHeader h = TilePackageReaders.PmTilesHeader(archive);

        Assert.Equal(5ul, h.Addressed);
        Assert.Equal(2ul, h.Entries);
        Assert.Equal(2ul, h.Contents);
        Assert.Equal(5ul, h.DataLength);

        List<TilePackageReaders.PmEntry> root = TilePackageReaders.Directory(
            TilePackageReaders.Gunzip(archive.AsSpan((int)h.RootOffset, (int)h.RootLength).ToArray()));

        Assert.Equal(new TilePackageReaders.PmEntry(0, 0, 1, 1), root[0]);
        Assert.Equal(new TilePackageReaders.PmEntry(1, 1, 4, 4), root[1]);

        Assert.Equal(sea, TilePackageReaders.PmTilesTile(archive, 1, 1, 0));
    }

    [Fact]
    public async Task A_duplicate_that_is_not_next_to_its_twin_points_back_at_it()
    {
        byte[] a = [1, 1];
        byte[] b = [2, 2, 2];

        byte[] archive = await WriteAsync([(1, 0, 0, a), (1, 0, 1, b), (1, 1, 1, a)], 1, 1);

        TilePackageReaders.PmHeader h = TilePackageReaders.PmTilesHeader(archive);
        List<TilePackageReaders.PmEntry> root = TilePackageReaders.Directory(
            TilePackageReaders.Gunzip(archive.AsSpan((int)h.RootOffset, (int)h.RootLength).ToArray()));

        Assert.Equal(3, root.Count);
        Assert.Equal(0ul, root[2].Offset);
        Assert.Equal(2ul, h.Contents);
        Assert.Equal(a, TilePackageReaders.PmTilesTile(archive, 1, 1, 1));
    }

    [Fact]
    public async Task Many_tiles_go_into_leaf_directories_and_are_still_found()
    {
        // Distinct content per tile, so nothing collapses into a run, and enough entries that the root cannot hold
        // them in 16 KB.
        List<(int, int, int, byte[])> tiles = [];

        // Lengths that wander, and every third tile left out, so the directory does not compress into 16 KB.
        Random lengths = new(98);

        for (int x = 0; x < 512; x++)
        {
            for (int y = 0; y < 200; y++)
            {
                if ((x + y) % 3 == 0)
                {
                    continue;
                }

                byte[] tile = new byte[4 + lengths.Next(0, 300)];
                BitConverter.GetBytes((x * 1000) + y).CopyTo(tile, 0);
                tiles.Add((9, x, y, tile));
            }
        }

        byte[] archive = await WriteAsync(tiles, 9, 9);
        TilePackageReaders.PmHeader h = TilePackageReaders.PmTilesHeader(archive);

        Assert.True(h.LeavesLength > 0);
        Assert.True(h.RootOffset + h.RootLength <= 16384);

        List<TilePackageReaders.PmEntry> root = TilePackageReaders.Directory(
            TilePackageReaders.Gunzip(archive.AsSpan((int)h.RootOffset, (int)h.RootLength).ToArray()));

        Assert.All(root, entry => Assert.Equal(0ul, entry.RunLength));

        foreach ((int z, int x, int y, byte[] tile) in tiles.Where((_, i) => i % 97 == 0))
        {
            Assert.Equal(tile, TilePackageReaders.PmTilesTile(archive, z, x, y));
        }
    }

    [Fact]
    public void A_varint_is_little_endian_base_128()
    {
        using MemoryStream stream = new();
        PmTiles.WriteVarint(stream, 300);
        PmTiles.WriteVarint(stream, 0);
        PmTiles.WriteVarint(stream, ulong.MaxValue);

        byte[] bytes = stream.ToArray();
        Assert.Equal([0xAC, 0x02, 0x00], bytes[..3]);

        int at = 0;
        ulong first = TilePackageReaders.Varint(bytes, ref at);
        ulong second = TilePackageReaders.Varint(bytes, ref at);
        ulong third = TilePackageReaders.Varint(bytes, ref at);

        Assert.Equal((300ul, 0ul, ulong.MaxValue), (first, second, third));
        Assert.Equal(bytes.Length, at);
    }

    [Fact]
    public void A_tile_twice_or_empty_or_off_its_level_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PmTiles.Plan(
            [new PmTile(1, 0, 0, 1, 1, 0), new PmTile(1, 0, 0, 1, 2, 1)], Description(1, 1), PmTiles.CompressionNone));
        Assert.Throws<ArgumentException>(() => PmTiles.Plan(
            [new PmTile(1, 0, 0, 0, 1, 0)], Description(1, 1), PmTiles.CompressionNone));
        Assert.Throws<ArgumentOutOfRangeException>(() => PmTiles.TileId(1, 2, 0));
    }
}
