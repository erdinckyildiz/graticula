using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Graticula.Tests.Shared;

/// <summary>
/// Readers for the two export formats, written in the tests from the published specifications and not from the
/// writers — ADR-098 §4.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second reading of each specification, so a writer and its test cannot share a misunderstanding.</b> Nothing
/// here calls into <c>Graticula.Tiles.Packages</c>: the bundle is read by Esri's <em>Compact Cache V2</em> description
/// (the index at 64, the 40/24-bit record, the size prefix before the tile), the archive by PMTiles v3's specification
/// (the header offsets, the five varint runs, the run length of 0 for a leaf, offsets relative to their sections), and
/// the Hilbert tile id by the specification's own definition, worked here with Wikipedia's <c>xy2d</c>.
/// </para>
/// <para>
/// <b>Every integer read from a buffer is read into a local before the position moves</b> — in C#, <c>i += F(ref i)</c>
/// reads <c>i</c> before the call, which is the bug these readers must not have.
/// </para>
/// </remarks>
internal static class TilePackageReaders
{
    /// <summary>A compact cache V2 bundle's tile, or null when its record says size zero.</summary>
    public static byte[]? BundleTile(byte[] bundle, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        long at = 64 + (8L * ((128L * (row % 128)) + (column % 128)));
        ulong record = BinaryPrimitives.ReadUInt64LittleEndian(bundle.AsSpan((int)at, 8));

        const ulong M = 1UL << 40;
        ulong offset = record % M;
        ulong size = record / M;

        if (size == 0)
        {
            return null;
        }

        uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan((int)offset - 4, 4));

        if (prefix != size)
        {
            throw new InvalidDataException($"The size before the tile says {prefix} and its record {size}.");
        }

        return bundle.AsSpan((int)offset, (int)size).ToArray();
    }

    /// <summary>The header of a bundle, as the specification's table names its fields.</summary>
    public static (uint Version, uint RecordCount, uint MaxTileSize, uint OffsetBytes, ulong Slack, ulong FileSize,
        ulong UserHeaderOffset, uint UserHeaderSize, uint Legacy1, uint Legacy2, uint Legacy3, uint Legacy4, uint IndexSize)
        BundleHeader(byte[] bundle)
    {
        ReadOnlySpan<byte> h = bundle.AsSpan(0, 64);

        return (
            BinaryPrimitives.ReadUInt32LittleEndian(h[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(h[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(h[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(h[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[44..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[52..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[56..]),
            BinaryPrimitives.ReadUInt32LittleEndian(h[60..]));
    }

    /// <summary>A tile out of a VTPK: the bundle it is in, by the package layout, and its gzip undone.</summary>
    /// <returns>The tile as a renderer reads it, or null when the package has none there.</returns>
    public static byte[]? VtpkTile(ZipArchive package, int z, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(package);

        int r = y - (y % 128);
        int c = x - (x % 128);
        string path = $"p12/tile/L{z:00}/R{r:x4}C{c:x4}.bundle";

        ZipArchiveEntry? entry = package.GetEntry(path);

        if (entry is null)
        {
            return null;
        }

        using MemoryStream bundle = new();

        using (Stream read = entry.Open())
        {
            read.CopyTo(bundle);
        }

        byte[]? stored = BundleTile(bundle.ToArray(), y, x);

        return stored is null ? null : Gunzip(stored);
    }

    /// <summary>Undoes gzip.</summary>
    public static byte[] Gunzip(byte[] bytes)
    {
        using MemoryStream output = new();

        using (GZipStream zip = new(new MemoryStream(bytes), CompressionMode.Decompress))
        {
            zip.CopyTo(output);
        }

        return output.ToArray();
    }

    /// <summary>A PMTiles v3 header, field by field.</summary>
    public sealed record PmHeader(
        ulong RootOffset, ulong RootLength, ulong MetadataOffset, ulong MetadataLength,
        ulong LeavesOffset, ulong LeavesLength, ulong DataOffset, ulong DataLength,
        ulong Addressed, ulong Entries, ulong Contents, byte Clustered, byte InternalCompression,
        byte TileCompression, byte TileType, byte MinZoom, byte MaxZoom,
        int MinLon, int MinLat, int MaxLon, int MaxLat, byte CenterZoom, int CenterLon, int CenterLat);

    /// <summary>Reads a PMTiles v3 header.</summary>
    public static PmHeader PmTilesHeader(byte[] archive)
    {
        ReadOnlySpan<byte> h = archive.AsSpan(0, 127);

        if (!h[..7].SequenceEqual("PMTiles"u8) || h[7] != 3)
        {
            throw new InvalidDataException("Not a PMTiles version 3 archive.");
        }

        ulong U(int at) => BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(at, 8));
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(at, 4));

        return new PmHeader(
            U(8), U(16), U(24), U(32), U(40), U(48), U(56), U(64), U(72), U(80), U(88),
            h[96], h[97], h[98], h[99], h[100], h[101],
            I(102), I(106), I(110), I(114), h[118], I(119), I(123));
    }

    /// <summary>One decoded directory entry.</summary>
    public readonly record struct PmEntry(ulong TileId, ulong Offset, ulong Length, ulong RunLength);

    /// <summary>Reads an unsigned LEB128 integer at a position, and moves the position past it.</summary>
    public static ulong Varint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            byte b = bytes[position];
            position++;
            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;

            if (shift > 63)
            {
                throw new InvalidDataException("A varint longer than 64 bits.");
            }
        }
    }

    /// <summary>Decodes an (already decompressed) directory.</summary>
    public static List<PmEntry> Directory(ReadOnlySpan<byte> bytes)
    {
        int at = 0;
        ulong count = Varint(bytes, ref at);

        ulong[] ids = new ulong[count];
        ulong[] runs = new ulong[count];
        ulong[] lengths = new ulong[count];
        ulong[] offsets = new ulong[count];

        ulong last = 0;

        for (ulong i = 0; i < count; i++)
        {
            ulong delta = Varint(bytes, ref at);
            last += delta;
            ids[i] = last;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong run = Varint(bytes, ref at);
            runs[i] = run;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong length = Varint(bytes, ref at);
            lengths[i] = length;
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong raw = Varint(bytes, ref at);

            offsets[i] = raw == 0 && i > 0
                ? offsets[i - 1] + lengths[i - 1]
                : raw - 1;
        }

        if (at != bytes.Length)
        {
            throw new InvalidDataException($"The directory is {bytes.Length} bytes and its entries end at {at}.");
        }

        List<PmEntry> entries = [];

        for (ulong i = 0; i < count; i++)
        {
            entries.Add(new PmEntry(ids[i], offsets[i], lengths[i], runs[i]));
        }

        return entries;
    }

    /// <summary>
    /// The Hilbert tile id, from the specification's definition: the tiles of every lower level, plus the position on
    /// this level's curve — Wikipedia's <c>xy2d</c> with the full side's rotation.
    /// </summary>
    public static ulong HilbertId(int z, int x, int y)
    {
        ulong acc = 0;

        for (int t = 0; t < z; t++)
        {
            acc += 1UL << (2 * t);
        }

        long n = 1L << z;
        long rx, ry, d = 0;
        long px = x, py = y;

        for (long s = n / 2; s > 0; s /= 2)
        {
            rx = (px & s) > 0 ? 1 : 0;
            ry = (py & s) > 0 ? 1 : 0;
            d += s * s * ((3 * rx) ^ ry);

            if (ry == 0)
            {
                if (rx == 1)
                {
                    px = n - 1 - px;
                    py = n - 1 - py;
                }

                (px, py) = (py, px);
            }
        }

        return acc + (ulong)d;
    }

    /// <summary>Finds a tile in a PMTiles archive by z/x/y, through the root and at most one leaf, and ungzips it
    /// when the header says so.</summary>
    /// <returns>The tile, or null when the archive has none there.</returns>
    public static byte[]? PmTilesTile(byte[] archive, int z, int x, int y)
    {
        PmHeader header = PmTilesHeader(archive);
        ulong id = HilbertId(z, x, y);

        byte[] Internal(ulong offset, ulong length)
        {
            byte[] raw = archive.AsSpan((int)offset, (int)length).ToArray();
            return header.InternalCompression == 2 ? Gunzip(raw) : raw;
        }

        List<PmEntry> directory = Directory(Internal(header.RootOffset, header.RootLength));

        for (int depth = 0; depth < 3; depth++)
        {
            PmEntry? found = null;

            foreach (PmEntry entry in directory)
            {
                if (entry.TileId > id)
                {
                    break;
                }

                found = entry;
            }

            if (found is not { } hit)
            {
                return null;
            }

            if (hit.RunLength == 0)
            {
                directory = Directory(Internal(header.LeavesOffset + hit.Offset, hit.Length));
                continue;
            }

            if (id >= hit.TileId + hit.RunLength)
            {
                return null;
            }

            byte[] stored = archive.AsSpan((int)(header.DataOffset + hit.Offset), (int)hit.Length).ToArray();
            return header.TileCompression == 2 ? Gunzip(stored) : stored;
        }

        throw new InvalidDataException("More than two levels of leaf directories.");
    }
}
