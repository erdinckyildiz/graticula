using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Tiles.Packages;

/// <summary>One tile going into a bundle: where it is on the level's grid and how long its stored bytes are.</summary>
/// <param name="Row">The tile's row on its level, counted down from the grid's top.</param>
/// <param name="Column">The tile's column on its level, counted east from the grid's left.</param>
/// <param name="Length">How many bytes are stored for it — the bytes the bundle carries, already compressed.</param>
/// <param name="Key">The caller's handle for reading the bytes back; the writer only passes it on.</param>
public readonly record struct BundleTile(int Row, int Column, int Length, long Key);

/// <summary>
/// Writes one bundle file of Esri's <em>compact cache V2</em> storage — the tile store inside a vector tile
/// package — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the published specification only</b> (CLAUDE.md §5): Esri's <em>Compact Cache V2</em>
/// technical description, published by Esri on GitHub as <c>Esri/raster-tiles-compactcache</c>,
/// <c>CompactCacheV2.md</c>. Every number below is that document's: the 64-byte header and its fields,
/// the 128 × 128 index of 8-byte records immediately after it in row-major order, a record's offset in bits
/// 0–39 and size in bits 40–63, a size of zero meaning *no tile*, the legacy 4-byte size written before each
/// tile with the index pointing past it, and the file name <c>R&lt;rrrr&gt;C&lt;cccc&gt;.bundle</c> in lowercase
/// hexadecimal of the bundle's top-left row and column. The document is written for raster caches; that a
/// vector tile package stores its tiles in the same bundles is Esri's statement about vector tile services
/// (<c>storageFormat: compactV2</c>, <c>packetSize: 128</c> in the documented service resource) and is what
/// the ADR relies on.
/// </para>
/// <para>
/// <b>Planned before it is written, so the file is written front to back once.</b> Every tile's size is known
/// before the first byte goes out — the export stages them — so the header's file size and largest tile and
/// every index record are computed first, and the bundle can be streamed straight into a zip entry, which is
/// not seekable. A writer that seeked back to patch the header would need a temporary file per bundle.
/// </para>
/// <para>
/// <b>Tiles are laid out in index order</b>, row by row from the bundle's top left, so the same tiles make the
/// same bytes whatever order they were built in — a concurrent walk finishes a batch in any order.
/// </para>
/// </remarks>
public static class CompactCacheBundle
{
    /// <summary>Tiles along one side of a bundle — the specification's packet size.</summary>
    public const int PacketSize = 128;

    /// <summary>The header's length in bytes.</summary>
    public const int HeaderSize = 64;

    /// <summary>How many index records a bundle has: one per tile position, 128 × 128.</summary>
    public const int RecordCount = PacketSize * PacketSize;

    /// <summary>The index's length in bytes: 8 per record.</summary>
    public const int IndexSize = RecordCount * 8;

    /// <summary>Where the first tile's size prefix goes: after the header and the index.</summary>
    public const long DataStart = HeaderSize + IndexSize;

    /// <summary>The largest tile a record can describe: its size field is 24 bits.</summary>
    public const int LargestTile = (1 << 24) - 1;

    /// <summary>The largest offset a record can hold: its offset field is 40 bits.</summary>
    public const long LargestOffset = (1L << 40) - 1;

    /// <summary>The folder a level's bundles are in — <c>L</c> and the level in two decimal digits.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The folder's name, e.g. <c>L08</c>.</returns>
    public static string LevelFolder(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 99);

        return "L" + level.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>The bundle a tile is in: the top-left row and column of its 128 × 128 block.</summary>
    /// <param name="row">The tile's row.</param>
    /// <param name="column">The tile's column.</param>
    /// <returns>The block's origin — always a multiple of 128.</returns>
    public static (int Row, int Column) OriginOf(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfNegative(column);

        return (row - (row % PacketSize), column - (column % PacketSize));
    }

    /// <summary>
    /// A bundle's file name: <c>R</c>, the origin row, <c>C</c>, the origin column, each in lowercase hexadecimal
    /// of at least four digits.
    /// </summary>
    /// <param name="originRow">The bundle's top-left row, a multiple of 128.</param>
    /// <param name="originColumn">The bundle's top-left column, a multiple of 128.</param>
    /// <returns>The name, e.g. <c>R0080C0100.bundle</c>.</returns>
    public static string FileName(int originRow, int originColumn)
    {
        if (originRow < 0 || originColumn < 0 || originRow % PacketSize != 0 || originColumn % PacketSize != 0)
        {
            throw new ArgumentException(
                $"A bundle starts at a row and a column that are multiples of {PacketSize}; "
                + $"{originRow} and {originColumn} are not both.");
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"R{originRow:x4}C{originColumn:x4}.bundle");
    }

    /// <summary>Where one tile's index record sits in a bundle file.</summary>
    /// <param name="row">The tile's row, absolute or relative — only its position in the block counts.</param>
    /// <param name="column">The tile's column.</param>
    /// <returns><c>64 + 8 × (128 × (row mod 128) + (column mod 128))</c>, the specification's formula.</returns>
    public static long IndexOffsetOf(int row, int column) =>
        HeaderSize + (8L * ((PacketSize * (long)(row % PacketSize)) + (column % PacketSize)));

    /// <summary>A bundle's shape, worked out before it is written.</summary>
    /// <param name="OriginRow">The bundle's top-left row.</param>
    /// <param name="OriginColumn">The bundle's top-left column.</param>
    /// <param name="Tiles">The tiles, in the order their bytes are written — index order.</param>
    /// <param name="Offsets">Each tile's offset, the first byte of its data, in the same order.</param>
    /// <param name="Index">Every record, 16,384 of them, row-major; zero where there is no tile.</param>
    /// <param name="FileSize">The file's length in bytes.</param>
    /// <param name="MaximumTileSize">The largest tile, as the header states it.</param>
    public sealed record Layout(
        int OriginRow,
        int OriginColumn,
        IReadOnlyList<BundleTile> Tiles,
        IReadOnlyList<long> Offsets,
        ulong[] Index,
        long FileSize,
        int MaximumTileSize);

    /// <summary>Works out a bundle's layout from its tiles' sizes.</summary>
    /// <param name="originRow">The bundle's top-left row, a multiple of 128.</param>
    /// <param name="originColumn">The bundle's top-left column, a multiple of 128.</param>
    /// <param name="tiles">The tiles, in any order. An empty tile is not stored and is not passed.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentException">A tile is outside the bundle, appears twice, is empty or is larger
    /// than a record can describe, or the bundle would pass the 40-bit offset.</exception>
    public static Layout Plan(int originRow, int originColumn, IReadOnlyList<BundleTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        _ = FileName(originRow, originColumn); // the origin's rule, said once

        BundleTile?[] byPosition = new BundleTile?[RecordCount];

        foreach (BundleTile tile in tiles)
        {
            int dr = tile.Row - originRow;
            int dc = tile.Column - originColumn;

            if (dr < 0 || dr >= PacketSize || dc < 0 || dc >= PacketSize)
            {
                throw new ArgumentException(
                    $"Tile row {tile.Row}, column {tile.Column} is not in the bundle that starts at row "
                    + $"{originRow}, column {originColumn}.");
            }

            if (tile.Length <= 0)
            {
                throw new ArgumentException(
                    $"Tile row {tile.Row}, column {tile.Column} is empty. An empty tile is not stored: its record "
                    + "says size zero, which the specification reads as no tile.");
            }

            if (tile.Length > LargestTile)
            {
                throw new ArgumentException(
                    $"Tile row {tile.Row}, column {tile.Column} is {tile.Length:N0} bytes, and a compact cache record "
                    + $"describes at most {LargestTile:N0} (24 bits).");
            }

            int at = (dr * PacketSize) + dc;

            if (byPosition[at] is not null)
            {
                throw new ArgumentException($"Tile row {tile.Row}, column {tile.Column} is given twice.");
            }

            byPosition[at] = tile;
        }

        List<BundleTile> order = [];
        List<long> offsets = [];
        ulong[] index = new ulong[RecordCount];
        long position = DataStart;
        int largest = 0;

        for (int at = 0; at < RecordCount; at++)
        {
            if (byPosition[at] is not { } tile)
            {
                continue;
            }

            // The legacy size prefix first; the record points at the byte after it.
            long offset = position + 4;

            if (offset > LargestOffset)
            {
                throw new ArgumentException(
                    "The bundle would pass the 40-bit offset a compact cache record can hold (one terabyte).");
            }

            index[at] = ((ulong)tile.Length << 40) | (ulong)offset;
            order.Add(tile);
            offsets.Add(offset);
            position = offset + tile.Length;
            largest = Math.Max(largest, tile.Length);
        }

        return new Layout(originRow, originColumn, order, offsets, index, position, largest);
    }

    /// <summary>The 64-byte header of a planned bundle.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>The header's bytes, little-endian throughout.</returns>
    /// <remarks>
    /// <b>The field values are the specification's table</b>: version 3, 16,384 records, the largest tile, an
    /// offset byte count of 5, no slack, the file size, a user header at offset 40 of 20 + 131,072 bytes, the
    /// four legacy values 3, 16, 16,384 and 5, and an index size of 131,072.
    /// </remarks>
    public static byte[] Header(Layout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        byte[] header = new byte[HeaderSize];
        Span<byte> h = header;

        BinaryPrimitives.WriteUInt32LittleEndian(h[0..], 3);                                // version
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], RecordCount);                      // record count
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)layout.MaximumTileSize);      // maximum tile size
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 5);                               // offset byte count
        BinaryPrimitives.WriteUInt64LittleEndian(h[16..], 0);                               // slack space
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], (ulong)layout.FileSize);          // file size
        BinaryPrimitives.WriteUInt64LittleEndian(h[32..], 40);                              // user header offset
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 20 + IndexSize);                  // user header size
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 3);                               // legacy
        BinaryPrimitives.WriteUInt32LittleEndian(h[48..], 16);                              // legacy
        BinaryPrimitives.WriteUInt32LittleEndian(h[52..], RecordCount);                     // legacy
        BinaryPrimitives.WriteUInt32LittleEndian(h[56..], 5);                               // legacy
        BinaryPrimitives.WriteUInt32LittleEndian(h[60..], IndexSize);                       // index size

        return header;
    }

    /// <summary>Writes a planned bundle, front to back.</summary>
    /// <param name="output">Where to — need not be seekable.</param>
    /// <param name="layout">The plan from <see cref="Plan"/>.</param>
    /// <param name="read">Returns a tile's stored bytes; they must be exactly <see cref="BundleTile.Length"/> long.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many bytes were written — the layout's file size.</returns>
    /// <exception cref="InvalidDataException">A tile's bytes are not the length it was planned at.</exception>
    public static async Task<long> WriteAsync(
        Stream output,
        Layout layout,
        Func<BundleTile, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(read);

        await output.WriteAsync(Header(layout), cancellationToken).ConfigureAwait(false);

        byte[] index = new byte[IndexSize];

        for (int i = 0; i < RecordCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(index.AsSpan(i * 8), layout.Index[i]);
        }

        await output.WriteAsync(index, cancellationToken).ConfigureAwait(false);

        long written = DataStart;
        byte[] prefix = new byte[4];

        foreach (BundleTile tile in layout.Tiles)
        {
            ReadOnlyMemory<byte> bytes = await read(tile, cancellationToken).ConfigureAwait(false);

            if (bytes.Length != tile.Length)
            {
                throw new InvalidDataException(
                    $"Tile row {tile.Row}, column {tile.Column} was planned at {tile.Length} bytes and read back as "
                    + $"{bytes.Length}. The bundle's index would point into the wrong bytes.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)tile.Length);
            await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            written += 4 + bytes.Length;
        }

        return written;
    }
}
