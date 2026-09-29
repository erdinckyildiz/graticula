using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Tiles;
using Microsoft.Win32.SafeHandles;

namespace Graticula.Host;

/// <summary>One tile an export has kept: where it is on the grid and where its bytes are in the staging file.</summary>
/// <param name="Address">The tile.</param>
/// <param name="Offset">Where its stored bytes start in the staging file.</param>
/// <param name="Length">How many stored bytes — gzip-compressed.</param>
/// <param name="Content">The first 128 bits of the stored bytes' SHA-256, so equal tiles are stored once.</param>
internal readonly record struct StagedTile(TileAddress Address, long Offset, int Length, UInt128 Content);

/// <summary>
/// The tiles of one export, kept on disk as they are walked, before they are written into the package — ADR-098 §5.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Staged, because both formats want every tile's size before their first byte.</b> A compact cache bundle begins
/// with an index of every tile in it and a PMTiles archive with a header naming every section's offset, and the walk
/// finishes tiles in batches in no fixed order. So each tile is compressed once, appended here, and remembered by
/// offset; the package is then planned from the list and streamed from this file in the order it wants.
/// </para>
/// <para>
/// <b>Gzip-compressed on the way in</b>, because both packages store their tiles that way: a VTPK's bundles hold
/// gzip-compressed tiles (its service document says <c>tileCompression: gzip</c>), and the PMTiles archive says gzip
/// in its header. Compressing once here means the package writers only copy.
/// </para>
/// <para>
/// <b>An empty tile is not kept.</b> Neither format stores one: a bundle's record says size zero and a PMTiles
/// directory has no entry, and both mean <em>nothing here</em> to a reader.
/// </para>
/// </remarks>
internal sealed class TileExportStaging : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly Dictionary<TileAddress, StagedTile> _tiles = [];
    private readonly Lock _gate = new();
    private long _length;

    /// <summary>Starts a staging file, replacing any a lost run left at the same path.</summary>
    /// <param name="path">Where.</param>
    public TileExportStaging(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = path;
        _file = File.OpenHandle(
            path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    /// <summary>Where the file is.</summary>
    public string Path { get; }

    /// <summary>The tiles kept, one per address.</summary>
    /// <remarks>
    /// <b>One per address, the last one kept.</b> The walk retries a batch after a source outage, and a tile after the
    /// one that met the outage may already have been built and kept; it arrives again, and the second copy replaces the
    /// first (whose bytes stay in the file, unread). Both formats refuse a tile given twice, and rightly.
    /// </remarks>
    public IReadOnlyList<StagedTile> Tiles
    {
        get
        {
            lock (_gate)
            {
                return [.. _tiles.Values];
            }
        }
    }

    /// <summary>How many tiles are kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tiles.Count;
            }
        }
    }

    /// <summary>The staging file's length — every kept tile's stored bytes.</summary>
    public long Length => Interlocked.Read(ref _length);

    /// <summary>A tile's bytes as a package stores them: gzip.</summary>
    /// <param name="tile">The tile as the route serves it.</param>
    /// <returns>The compressed bytes.</returns>
    public static byte[] Compress(byte[] tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        using MemoryStream output = new();

        using (GZipStream zip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(tile);
        }

        return output.ToArray();
    }

    /// <summary>Keeps one tile.</summary>
    /// <param name="address">Where it is.</param>
    /// <param name="tile">Its bytes as the route serves them; an empty tile is not kept.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored length, or 0 for an empty tile.</returns>
    public async Task<int> AddAsync(TileAddress address, byte[] tile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tile);

        if (tile.Length == 0)
        {
            lock (_gate)
            {
                _tiles.Remove(address);
            }

            return 0;
        }

        byte[] stored = Compress(tile);
        UInt128 content = ContentOf(stored);
        long offset;

        // The offset is reserved under the lock and the write happens outside it: positional writes to different
        // ranges of one handle do not need each other.
        lock (_gate)
        {
            offset = _length;
            _length += stored.Length;
        }

        await RandomAccess.WriteAsync(_file, stored, offset, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _tiles[address] = new StagedTile(address, offset, stored.Length, content);
        }

        return stored.Length;
    }

    /// <summary>Reads a kept tile's stored bytes back.</summary>
    /// <param name="offset">Where they start.</param>
    /// <param name="length">How many.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bytes.</returns>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int length, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[length];
        int read = 0;

        while (read < length)
        {
            int got = await RandomAccess.ReadAsync(_file, bytes.AsMemory(read), offset + read, cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
            {
                throw new EndOfStreamException(
                    $"The export's staging file ends before the tile at {offset} does; it was cut short.");
            }

            read += got;
        }

        return bytes;
    }

    /// <summary>An identity of stored bytes: the first 128 bits of their SHA-256.</summary>
    internal static UInt128 ContentOf(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);

        return new UInt128(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]));
    }

    /// <summary>Closes the file, which deletes it.</summary>
    public void Dispose() => _file.Dispose();
}
