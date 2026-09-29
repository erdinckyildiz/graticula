using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Tiles.Packages;

/// <summary>One tile going into a PMTiles archive.</summary>
/// <param name="Z">The level.</param>
/// <param name="X">The column.</param>
/// <param name="Y">The row, counted down from the top — XYZ, which is what PMTiles addresses.</param>
/// <param name="Length">How many bytes are stored for it, already compressed as the header will say.</param>
/// <param name="Content">
/// An identity of the bytes — a hash — so two tiles with the same bytes are stored once. Two tiles with equal
/// identities must have equal bytes; the writer does not look.
/// </param>
/// <param name="Key">The caller's handle for reading the bytes back.</param>
public readonly record struct PmTile(int Z, int X, int Y, int Length, UInt128 Content, long Key);

/// <summary>What a PMTiles archive says about itself beyond its tiles.</summary>
/// <param name="MinZoom">The lowest level asked for. The header states the lowest level that has a tile, and this only when none has.</param>
/// <param name="MaxZoom">The highest, likewise.</param>
/// <param name="MinLongitude">The west edge, in degrees.</param>
/// <param name="MinLatitude">The south edge.</param>
/// <param name="MaxLongitude">The east edge.</param>
/// <param name="MaxLatitude">The north edge.</param>
/// <param name="CenterZoom">The level a reader may open at, drawn into the levels the header states.</param>
/// <param name="CenterLongitude">The centre's longitude.</param>
/// <param name="CenterLatitude">The centre's latitude.</param>
/// <param name="MetadataJson">The metadata object, UTF-8 JSON; for vector tiles it must hold <c>vector_layers</c>.</param>
public sealed record PmTilesDescription(
    int MinZoom,
    int MaxZoom,
    double MinLongitude,
    double MinLatitude,
    double MaxLongitude,
    double MaxLatitude,
    int CenterZoom,
    double CenterLongitude,
    double CenterLatitude,
    byte[] MetadataJson);

/// <summary>
/// Writes a PMTiles version 3 archive of vector tiles — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the published specification only</b>: <c>protomaps/PMTiles</c>, <c>spec/v3/spec.md</c>. The
/// 127-byte header and its fields; the Hilbert tile id, cumulative over the levels from zero; a directory as five
/// runs of little-endian variable-width integers — the count, the tile ids delta-encoded, the run lengths, the
/// lengths, and the offsets as <c>offset + 1</c> or <c>0</c> when the entry follows the one before it; a run
/// length of 0 marking a leaf directory; offsets relative to the tile data section for tiles and to the leaf
/// section for leaves; the root directory within the first 16,384 bytes; and <c>vector_layers</c> in the metadata
/// of a vector tileset.
/// </para>
/// <para>
/// <b>Everything is planned before anything is written</b>, for <see cref="CompactCacheBundle"/>'s reason: the
/// header comes first and holds every section's offset, and the tiles are read once, in order, from wherever the
/// caller staged them.
/// </para>
/// <para>
/// <b>Deduplicated and run-length encoded, and so clustered.</b> Tiles are laid out in tile id order; a tile whose
/// bytes were already stored points back at them, and a run of consecutive ids with one content is one entry — the
/// open sea at a low level, or a uniform fill. That keeps the specification's definition of <em>clustered</em>:
/// every offset either follows the one before it or refers to a lesser one.
/// </para>
/// <para>
/// <b>Directories are gzip-compressed</b> (internal compression 2). The tiles' own compression is the caller's and is
/// stated in the header as it is given.
/// </para>
/// </remarks>
public static class PmTiles
{
    /// <summary>The header's length.</summary>
    public const int HeaderSize = 127;

    /// <summary>The most the compressed root directory may take: 16,384 less the header.</summary>
    public const int LargestRootDirectory = 16384 - HeaderSize;

    /// <summary>The <c>Compression</c> enumeration's value for none.</summary>
    public const byte CompressionNone = 0x01;

    /// <summary>The <c>Compression</c> enumeration's value for gzip.</summary>
    public const byte CompressionGzip = 0x02;

    /// <summary>The <c>TileType</c> enumeration's value for a Mapbox Vector Tile.</summary>
    public const byte TileTypeMvt = 0x01;

    /// <summary>The recommended media type.</summary>
    public const string MediaType = "application/vnd.pmtiles";

    /// <summary>The highest level a tile id is computed for here — far beyond any tile this server cuts.</summary>
    public const int DeepestLevel = 26;

    /// <summary>
    /// A tile's id: how many tiles the levels below it hold, plus its position along the level's Hilbert curve.
    /// </summary>
    /// <param name="z">The level.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row, from the top.</param>
    /// <returns>The id.</returns>
    /// <remarks>
    /// The specification's own examples hold: <c>0/0/0</c> is 0, <c>1/0/0</c> is 1 and <c>1/1/0</c> is 4
    /// (<c>PmTilesWriterTests</c>).
    /// </remarks>
    public static ulong TileId(int z, int x, int y)
    {
        if (z < 0 || z > DeepestLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(z), z, $"A level is 0 to {DeepestLevel} here.");
        }

        long n = 1L << z;

        if (x < 0 || y < 0 || x >= n || y >= n)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), $"Level {z} has columns and rows 0 to {n - 1}; {x}/{y} is outside it.");
        }

        // The tiles of every level below: 1 + 4 + … + 4^(z-1) = (4^z − 1) / 3.
        ulong below = ((1UL << (2 * z)) - 1) / 3;

        ulong d = 0;
        long cx = x;
        long cy = y;

        for (long s = n / 2; s > 0; s /= 2)
        {
            long rx = (cx & s) > 0 ? 1 : 0;
            long ry = (cy & s) > 0 ? 1 : 0;

            d += (ulong)(s * s * ((3 * rx) ^ ry));

            // The quadrant's rotation, so the curve inside it joins the next one.
            if (ry == 0)
            {
                if (rx == 1)
                {
                    cx = s - 1 - (cx & (s - 1));
                    cy = s - 1 - (cy & (s - 1));
                }
                else
                {
                    cx &= s - 1;
                    cy &= s - 1;
                }

                (cx, cy) = (cy, cx);
            }
            else
            {
                cx &= s - 1;
                cy &= s - 1;
            }
        }

        return below + d;
    }

    /// <summary>Appends an unsigned little-endian base-128 integer.</summary>
    /// <param name="into">Where to.</param>
    /// <param name="value">The value.</param>
    public static void WriteVarint(Stream into, ulong value)
    {
        ArgumentNullException.ThrowIfNull(into);

        while (value >= 0x80)
        {
            into.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        into.WriteByte((byte)value);
    }

    /// <summary>One directory entry.</summary>
    /// <param name="TileId">The tile's id, or the first id of a leaf.</param>
    /// <param name="Offset">The first byte, relative to the section it points into.</param>
    /// <param name="Length">How many bytes.</param>
    /// <param name="RunLength">How many consecutive ids share these bytes; 0 for a leaf.</param>
    public readonly record struct Entry(ulong TileId, ulong Offset, uint Length, uint RunLength);

    /// <summary>A directory in the specification's encoding, before compression.</summary>
    /// <param name="entries">The entries, ascending by tile id.</param>
    /// <returns>The bytes.</returns>
    public static byte[] EncodeDirectory(IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using MemoryStream stream = new();

        WriteVarint(stream, (ulong)entries.Count);

        ulong last = 0;

        foreach (Entry entry in entries)
        {
            if (entry.TileId < last)
            {
                throw new ArgumentException("A directory's entries are in ascending tile id order.", nameof(entries));
            }

            WriteVarint(stream, entry.TileId - last);
            last = entry.TileId;
        }

        foreach (Entry entry in entries)
        {
            WriteVarint(stream, entry.RunLength);
        }

        foreach (Entry entry in entries)
        {
            WriteVarint(stream, entry.Length);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            bool follows = i > 0 && entries[i].Offset == entries[i - 1].Offset + entries[i - 1].Length;

            WriteVarint(stream, follows ? 0 : entries[i].Offset + 1);
        }

        return stream.ToArray();
    }

    /// <summary>An archive's shape, worked out before it is written.</summary>
    /// <param name="Header">The 127 header bytes.</param>
    /// <param name="Root">The compressed root directory.</param>
    /// <param name="Metadata">The compressed metadata.</param>
    /// <param name="Leaves">The compressed leaf directories, one after another.</param>
    /// <param name="Contents">Each distinct content, in the order its bytes are written into the tile data section.</param>
    /// <param name="FileSize">The archive's length.</param>
    public sealed record Layout(
        byte[] Header,
        byte[] Root,
        byte[] Metadata,
        byte[] Leaves,
        IReadOnlyList<PmTile> Contents,
        long FileSize);

    /// <summary>Works out an archive from its tiles' sizes and contents.</summary>
    /// <param name="tiles">The tiles, in any order; an empty tile is not passed.</param>
    /// <param name="description">The rest of what the header and the metadata say.</param>
    /// <param name="tileCompression">How the tiles' bytes are compressed — <see cref="CompressionGzip"/> or <see cref="CompressionNone"/>.</param>
    /// <returns>The layout.</returns>
    public static Layout Plan(IReadOnlyList<PmTile> tiles, PmTilesDescription description, byte tileCompression)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(description);

        List<(ulong Id, PmTile Tile)> sorted = new(tiles.Count);

        foreach (PmTile tile in tiles)
        {
            if (tile.Length <= 0)
            {
                throw new ArgumentException($"Tile {tile.Z}/{tile.X}/{tile.Y} is empty; an empty tile is left out.", nameof(tiles));
            }

            sorted.Add((TileId(tile.Z, tile.X, tile.Y), tile));
        }

        sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

        Dictionary<UInt128, ulong> stored = [];
        List<PmTile> contents = [];
        List<Entry> entries = [];
        ulong dataLength = 0;
        ulong addressed = 0;

        for (int i = 0; i < sorted.Count; i++)
        {
            (ulong id, PmTile tile) = sorted[i];

            if (i > 0 && sorted[i - 1].Id == id)
            {
                throw new ArgumentException($"Tile {tile.Z}/{tile.X}/{tile.Y} is given twice.", nameof(tiles));
            }

            if (!stored.TryGetValue(tile.Content, out ulong offset))
            {
                offset = dataLength;
                stored[tile.Content] = offset;
                contents.Add(tile);
                dataLength += (ulong)tile.Length;
            }

            addressed++;

            // <b>A run: the next id, with the same bytes.</b> The previous entry grows instead of a new one.
            if (entries.Count > 0
                && entries[^1].TileId + entries[^1].RunLength == id
                && entries[^1].Offset == offset
                && entries[^1].Length == (uint)tile.Length)
            {
                Entry previous = entries[^1];
                entries[^1] = previous with { RunLength = previous.RunLength + 1 };
                continue;
            }

            entries.Add(new Entry(id, offset, (uint)tile.Length, 1));
        }

        (byte[] root, byte[] leaves) = Directories(entries);
        byte[] metadata = Gzip(description.MetadataJson);

        long rootOffset = HeaderSize;
        long metadataOffset = rootOffset + root.Length;
        long leavesOffset = metadataOffset + metadata.Length;
        long dataOffset = leavesOffset + leaves.Length;

        byte[] header = new byte[HeaderSize];
        Span<byte> h = header;

        "PMTiles"u8.CopyTo(h);
        h[7] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(h[8..], (ulong)rootOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[16..], (ulong)root.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], (ulong)metadataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[32..], (ulong)metadata.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[40..], (ulong)leavesOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[48..], (ulong)leaves.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[56..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[64..], dataLength);
        BinaryPrimitives.WriteUInt64LittleEndian(h[72..], addressed);
        BinaryPrimitives.WriteUInt64LittleEndian(h[80..], (ulong)entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(h[88..], (ulong)contents.Count);
        h[96] = 1;                    // clustered: offsets follow tile id order, or point back to a duplicate
        h[97] = CompressionGzip;      // internal compression: the directories and the metadata
        h[98] = tileCompression;
        h[99] = TileTypeMvt;
        // <b>The levels are the ones that have tiles, not the ones asked for.</b> An empty tile is left out, so an
        // export of small features over 0-14 can hold nothing below 10; the specification's MinZoom and MaxZoom are
        // the archive's own, and `pmtiles verify` refuses a header that claims a level with no tile in it
        // (2026-09-29, found by exporting the fixture's parcels). The centre level is drawn into that range.
        (int minZoom, int maxZoom) = sorted.Count == 0
            ? (description.MinZoom, description.MaxZoom)
            : (sorted[0].Tile.Z, sorted[^1].Tile.Z);
        int centerZoom = Math.Clamp(description.CenterZoom, minZoom, maxZoom);

        h[100] = (byte)minZoom;
        h[101] = (byte)maxZoom;
        Position(h[102..], description.MinLongitude, description.MinLatitude);
        Position(h[110..], description.MaxLongitude, description.MaxLatitude);
        h[118] = (byte)centerZoom;
        Position(h[119..], description.CenterLongitude, description.CenterLatitude);

        return new Layout(header, root, metadata, leaves, contents, dataOffset + (long)dataLength);
    }

    /// <summary>Writes a planned archive, front to back.</summary>
    /// <param name="output">Where to — need not be seekable.</param>
    /// <param name="layout">The plan from <see cref="Plan"/>.</param>
    /// <param name="read">Returns a tile's stored bytes; exactly <see cref="PmTile.Length"/> of them.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bytes written — the layout's file size.</returns>
    public static async Task<long> WriteAsync(
        Stream output,
        Layout layout,
        Func<PmTile, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(read);

        await output.WriteAsync(layout.Header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(layout.Root, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(layout.Metadata, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(layout.Leaves, cancellationToken).ConfigureAwait(false);

        long written = HeaderSize + layout.Root.Length + layout.Metadata.Length + layout.Leaves.Length;

        foreach (PmTile tile in layout.Contents)
        {
            ReadOnlyMemory<byte> bytes = await read(tile, cancellationToken).ConfigureAwait(false);

            if (bytes.Length != tile.Length)
            {
                throw new InvalidDataException(
                    $"Tile {tile.Z}/{tile.X}/{tile.Y} was planned at {tile.Length} bytes and read back as {bytes.Length}.");
            }

            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            written += bytes.Length;
        }

        return written;
    }

    /// <summary>
    /// The root directory, and leaf directories when the root alone would not fit in the first 16 KB.
    /// </summary>
    /// <remarks>
    /// <b>One level of leaves, as the specification encourages</b>: the entries are cut into leaves of a fixed
    /// number, and the number grows by a fifth until the root that points at them fits. With 4,096 entries a leaf
    /// the root fits under a quarter of a million leaves' worth of tiles long before it matters here.
    /// </remarks>
    private static (byte[] Root, byte[] Leaves) Directories(List<Entry> entries)
    {
        byte[] root = Gzip(EncodeDirectory(entries));

        if (root.Length <= LargestRootDirectory)
        {
            return (root, []);
        }

        int size = 4096;

        while (true)
        {
            using MemoryStream leaves = new();
            List<Entry> pointers = [];

            for (int start = 0; start < entries.Count; start += size)
            {
                List<Entry> leaf = entries.GetRange(start, Math.Min(size, entries.Count - start));
                byte[] encoded = Gzip(EncodeDirectory(leaf));

                pointers.Add(new Entry(leaf[0].TileId, (ulong)leaves.Length, (uint)encoded.Length, 0));
                leaves.Write(encoded);
            }

            root = Gzip(EncodeDirectory(pointers));

            if (root.Length <= LargestRootDirectory)
            {
                return (root, leaves.ToArray());
            }

            size += Math.Max(1, size / 5);
        }
    }

    private static void Position(Span<byte> into, double longitude, double latitude)
    {
        BinaryPrimitives.WriteInt32LittleEndian(into, (int)Math.Round(Math.Clamp(longitude, -180, 180) * 10_000_000));
        BinaryPrimitives.WriteInt32LittleEndian(into[4..], (int)Math.Round(Math.Clamp(latitude, -90, 90) * 10_000_000));
    }

    /// <summary>Gzip, as the internal compression the header declares.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The compressed bytes.</returns>
    public static byte[] Gzip(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using MemoryStream output = new();

        using (GZipStream zip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(bytes);
        }

        return output.ToArray();
    }
}
