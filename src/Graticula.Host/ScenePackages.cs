using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// Scene layer packages on disk, read by entry — ADR-269 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The store holds the package; this node keeps a copy it can seek in.</b> A document's bytes come from the store as
/// one array, and an I3S client asks for hundreds of small resources inside it. Each node writes the package once to
/// <c>{StatePath}/scene-packages/{id}-{modified}.slpk</c> and answers from that file; a new version of the document has
/// a new name, so a replaced package is never read stale, and an old copy is deleted when its successor is written.
/// </para>
/// <para>
/// <b>Its own reader of the zip's directory, not <see cref="ZipArchive"/>.</b> A <see cref="ZipArchive"/> is not safe
/// for concurrent reads and reparses the directory on every open; the index here is read once — names, the local
/// header's offset, sizes and method, ZIP64 included — and every request is then a seek and a copy. The I3S rule is
/// that entries are stored, not deflated (the specification's *Scene Layer Packages*); a deflated entry is read too,
/// because packages from other producers drift from it.
/// </para>
/// </remarks>
internal sealed class ScenePackages
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Lazy<Task<Package>>> _open = new(StringComparer.Ordinal);

    /// <summary>Creates the cache under a state directory.</summary>
    /// <param name="statePath">The server's state path.</param>
    public ScenePackages(string statePath)
    {
        ArgumentNullException.ThrowIfNull(statePath);
        _directory = Path.Combine(statePath, "scene-packages");
    }

    /// <summary>The package a document holds, written to this node's disk on first use.</summary>
    /// <param name="document">The document, a scene layer package.</param>
    /// <param name="documents">The store, for its bytes.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The package, or null when the store no longer has it or it is not a readable zip.</returns>
    public async Task<Package?> OpenAsync(Document document, IDocumentStore documents, CancellationToken cancellation)
    {
        string key = $"{document.Id}-{document.Modified.UtcTicks}";
        Lazy<Task<Package>> opening = _open.GetOrAdd(key, _ => new Lazy<Task<Package>>(() => WriteAsync(document, documents, key)));

        try
        {
            return await opening.Value.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            _open.TryRemove(key, out _);
            return null;
        }
        catch (FileNotFoundException)
        {
            _open.TryRemove(key, out _);
            return null;
        }
    }

    private async Task<Package> WriteAsync(Document document, IDocumentStore documents, string key)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, key + ".slpk");

        if (!File.Exists(path))
        {
            byte[] content = await documents.ContentAsync(document.Id, CancellationToken.None).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The store no longer holds the package.");
            string partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
            await File.WriteAllBytesAsync(partial, content).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);

            // An earlier version of the same document is not read again.
            foreach (string old in Directory.EnumerateFiles(_directory, document.Id + "-*.slpk"))
            {
                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        return new Package(path, ReadDirectory(path));
    }

    /// <summary>Reads the central directory: every entry's name (lower case, forward slashes) and where it is.</summary>
    internal static Dictionary<string, Entry> ReadDirectory(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = file.Length;

        // The end-of-central-directory record is in the last 64 KiB + 22 bytes.
        int tail = (int)Math.Min(length, 65_557);
        byte[] end = new byte[tail];
        file.Seek(length - tail, SeekOrigin.Begin);
        file.ReadExactly(end);

        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(end, i) == 0x06054b50)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw new InvalidDataException("Not a zip: no end of central directory.");
        }

        long count = BitConverter.ToUInt16(end, eocd + 10);
        long directorySize = BitConverter.ToUInt32(end, eocd + 12);
        long directoryOffset = BitConverter.ToUInt32(end, eocd + 16);

        // ZIP64: the locator sits right before the record and points at the ZIP64 end record.
        if ((directoryOffset == 0xFFFFFFFF || count == 0xFFFF) && eocd >= 20
            && BitConverter.ToUInt32(end, eocd - 20) == 0x07064b50)
        {
            long zip64End = (long)BitConverter.ToUInt64(end, eocd - 20 + 8);
            byte[] record = new byte[56];
            file.Seek(zip64End, SeekOrigin.Begin);
            file.ReadExactly(record);
            if (BitConverter.ToUInt32(record, 0) != 0x06064b50)
            {
                throw new InvalidDataException("Not a zip: a broken ZIP64 end record.");
            }

            count = (long)BitConverter.ToUInt64(record, 32);
            directorySize = (long)BitConverter.ToUInt64(record, 40);
            directoryOffset = (long)BitConverter.ToUInt64(record, 48);
        }

        if (directoryOffset + directorySize > length || directorySize > int.MaxValue)
        {
            throw new InvalidDataException("Not a zip: the central directory lies outside the file.");
        }

        byte[] directory = new byte[directorySize];
        file.Seek(directoryOffset, SeekOrigin.Begin);
        file.ReadExactly(directory);

        Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        int at = 0;

        for (long n = 0; n < count && at + 46 <= directory.Length; n++)
        {
            if (BitConverter.ToUInt32(directory, at) != 0x02014b50)
            {
                throw new InvalidDataException("Not a zip: a broken central directory entry.");
            }

            ushort method = BitConverter.ToUInt16(directory, at + 10);
            long compressed = BitConverter.ToUInt32(directory, at + 20);
            long uncompressed = BitConverter.ToUInt32(directory, at + 24);
            int nameLength = BitConverter.ToUInt16(directory, at + 28);
            int extraLength = BitConverter.ToUInt16(directory, at + 30);
            int commentLength = BitConverter.ToUInt16(directory, at + 32);
            long local = BitConverter.ToUInt32(directory, at + 42);
            string name = Encoding.UTF8.GetString(directory, at + 46, nameLength);

            // ZIP64 extra field: the values that were 0xFFFFFFFF, in this order.
            int extra = at + 46 + nameLength;
            int extraEnd = extra + extraLength;
            while (extra + 4 <= extraEnd)
            {
                ushort id = BitConverter.ToUInt16(directory, extra);
                ushort size = BitConverter.ToUInt16(directory, extra + 2);
                if (id == 0x0001)
                {
                    int value = extra + 4;
                    if (uncompressed == 0xFFFFFFFF) { uncompressed = (long)BitConverter.ToUInt64(directory, value); value += 8; }
                    if (compressed == 0xFFFFFFFF) { compressed = (long)BitConverter.ToUInt64(directory, value); value += 8; }
                    if (local == 0xFFFFFFFF) { local = (long)BitConverter.ToUInt64(directory, value); }
                }

                extra += 4 + size;
            }

            entries[name.Replace('\\', '/').TrimStart('/').ToLowerInvariant()] = new Entry(local, compressed, uncompressed, method);
            at += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    /// <summary>Where one entry is.</summary>
    internal sealed record Entry(long LocalHeader, long CompressedSize, long Size, ushort Method);

    /// <summary>One package on this node's disk and its directory.</summary>
    internal sealed class Package(string path, IReadOnlyDictionary<string, Entry> entries)
    {
        /// <summary>The entries, by lower-case path.</summary>
        public IReadOnlyDictionary<string, Entry> Entries { get; } = entries;

        /// <summary>The first of the names given that the package holds, or null.</summary>
        public string? Find(params string[] names)
        {
            foreach (string name in names)
            {
                if (Entries.ContainsKey(name.ToLowerInvariant()))
                {
                    return name.ToLowerInvariant();
                }
            }

            return null;
        }

        private Dictionary<string, string>? _stems;

        /// <summary>
        /// The entry whose path, without its last segment's extension, is <paramref name="stem"/> — a resource a profile
        /// stores under an extension of its own (a point cloud's <c>.bin.pccxyz</c>, a voxel layer's), which the named
        /// candidates do not list (ADR-270 §5.6). The first such entry in the directory.
        /// </summary>
        public string? FindStem(string stem)
        {
            if (_stems is null)
            {
                Dictionary<string, string> stems = new(StringComparer.Ordinal);
                foreach (string name in Entries.Keys)
                {
                    int slash = name.LastIndexOf('/');
                    int dot = name.IndexOf('.', slash + 1);
                    if (dot > slash + 1)
                    {
                        stems.TryAdd(name[..dot], name);
                    }
                }

                _stems = stems;
            }

            return _stems.TryGetValue(stem.ToLowerInvariant(), out string? found) ? found : null;
        }

        /// <summary>An entry's stored bytes as they are in the zip after inflating a deflated one — a .gz is still gzip.</summary>
        public async Task<byte[]> ReadAsync(string name, CancellationToken cancellation)
        {
            Entry entry = Entries[name];
            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            byte[] header = new byte[30];
            file.Seek(entry.LocalHeader, SeekOrigin.Begin);
            await file.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
            if (BitConverter.ToUInt32(header, 0) != 0x04034b50)
            {
                throw new InvalidDataException("A broken local header.");
            }

            long data = entry.LocalHeader + 30 + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);
            file.Seek(data, SeekOrigin.Begin);

            if (entry.CompressedSize > int.MaxValue || entry.Size > int.MaxValue)
            {
                throw new InvalidDataException("An entry larger than one response can carry.");
            }

            byte[] stored = new byte[entry.CompressedSize];
            await file.ReadExactlyAsync(stored, cancellation).ConfigureAwait(false);

            if (entry.Method == 0)
            {
                return stored;
            }

            if (entry.Method != 8)
            {
                throw new InvalidDataException($"An entry compressed by method {entry.Method}, which this reader does not inflate.");
            }

            using MemoryStream inflated = new((int)entry.Size);
            await using (DeflateStream deflate = new(new MemoryStream(stored), CompressionMode.Decompress))
            {
                await deflate.CopyToAsync(inflated, cancellation).ConfigureAwait(false);
            }

            return inflated.ToArray();
        }
    }
}
