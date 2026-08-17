using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Tiles;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// ADR-010's L3: tiles on disk, with a budget.
/// </summary>
/// <remarks>
/// <para>
/// <b>The budget is the part that was missing from the design.</b>
/// <c>failure-scenarios.md</c> N6 found that ADR-010 specified layers, keys,
/// invalidation and seeding and never said how large the cache may get. A tile
/// cache across a thousand services is unbounded by nature, and "the GIS server
/// filled the disk" is a memorable first incident.
/// </para>
/// <para>
/// <b>Reads do not consult the index</b> — failure-scenario N2. The path is
/// derivable from the key, so a lookup is one <c>File.Exists</c>. A platform
/// store outage costs cache <em>management</em> and not cache <em>reads</em>,
/// which matters because that outage is exactly when the cache may be the only
/// thing still able to answer. The in-memory index exists for eviction and
/// reporting, and nothing on the read path waits for it.
/// </para>
/// <para>
/// <b>Everything fails soft.</b> A full disk, a permission problem or a
/// half-written file degrades to no-cache. An optimisation that can fail a
/// request is a liability.
/// </para>
/// </remarks>
internal sealed class FileSystemTileCache : ITileCache, IDisposable
{
    /// <summary>A zero-length file means "this tile is empty", not "corrupt".</summary>
    /// <remarks>
    /// ADR-010 §2's negative caching. Most of a sparse layer's pyramid is
    /// emptiness, and rebuilding the ocean on every request is the waste this
    /// exists to stop. A zero-length file is the cheapest possible marker and
    /// costs one directory entry.
    /// </remarks>
    private const long EmptyMarker = 0;

    private readonly string _root;
    private readonly long _budget;
    private readonly long _perLayerBudget;
    /// <summary>
    /// The lifetime used when a layer names none of its own.
    /// </summary>
    /// <remarks>
    /// <b>Kept as a fallback rather than removed with D-25.</b> A layer that has
    /// never had its volatility set still needs an answer, and the honest one is
    /// the server's configured default — not "forever", which would serve stale
    /// tiles indefinitely to anyone who forgot to set it.
    /// </remarks>
    public TimeSpan DefaultLifetime { get; }

    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private readonly ConcurrentDictionary<string, Entry> _index = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _evicting = new(1, 1);
    private long _bytes;
    private bool _warned;
    private bool _disposed;

    /// <summary>Creates the cache and adopts whatever is already on disk.</summary>
    /// <param name="root">Where tiles live.</param>
    /// <param name="budget">Total bytes allowed.</param>
    /// <param name="perLayerBudget">Bytes allowed to any one layer.</param>
    /// <param name="lifetime">How long an entry is trusted.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="loggerFactory">For the fail-soft warnings.</param>
    public FileSystemTileCache(
        string root,
        long budget,
        long perLayerBudget,
        TimeSpan lifetime,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _root = root;
        _budget = budget;
        _perLayerBudget = perLayerBudget;
        DefaultLifetime = lifetime;
        _clock = clock;
        _log = loggerFactory.CreateLogger("tilecache");

        Adopt();
    }

    /// <inheritdoc/>
    public async Task<CachedTile> ReadAsync(
        TileCacheKey key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        string path = System.IO.Path.Combine(_root, key.Path());

        try
        {
            FileInfo file = new(path);

            if (!file.Exists)
            {
                return CachedTile.Miss;
            }

            // Expiry is read from the file rather than the index, so a cache
            // adopted from a previous run behaves the same as one this process
            // filled. Anything else makes a restart silently serve stale tiles.
            // <b>Zero means never, including within the same tick.</b> A plain
            // age comparison makes a freshly written entry younger than a
            // zero lifetime by exactly nothing, and nothing is not greater than
            // zero — so "never cache" served a cached tile for as long as the
            // clock did not move. The header already said no-store; this is the
            // half that decides what we ourselves hand back.
            if (lifetime <= TimeSpan.Zero
                || _clock.GetUtcNow() - file.LastWriteTimeUtc > lifetime)
            {
                return CachedTile.Miss;
            }

            Touch(key, file.Length);

            return file.Length == EmptyMarker
                ? CachedTile.Empty
                : new CachedTile(TileCacheOutcome.Hit,
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A miss, not an error. The tile gets rebuilt and the request is
            // answered; the only cost is the work the cache was meant to save.
            WarnOnce(e);
            return CachedTile.Miss;
        }
    }

    /// <inheritdoc/>
    public async Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tile);

        if (tile.Length > _perLayerBudget)
        {
            // One tile larger than a whole layer's quota would evict everything
            // else and then not fit. Refusing it is cheaper than discovering
            // that by emptying the cache.
            return;
        }

        string path = System.IO.Path.Combine(_root, key.Path());

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            // Written beside and moved into place. A reader that opens a
            // half-written tile gets a truncated protobuf, which decodes to
            // fewer features rather than to an error — a wrong map with nothing
            // to indicate it. The move is atomic on both filesystems we target.
            string temporary = string.Create(
                CultureInfo.InvariantCulture, $"{path}.{Environment.CurrentManagedThreadId}.tmp");

            await File.WriteAllBytesAsync(temporary, tile, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);

            // <b>The write time is stamped from our clock, not left to the
            // filesystem's.</b> Expiry compares the stamp against the same
            // clock, and the first version did not — it read the file's real
            // mtime and compared it with an injected one, so the two tests for
            // expiry both failed and would have kept failing for any deployment
            // whose container clock differed from the host's. Setting it makes
            // the cache's notion of time a single thing, and keeps expiry
            // correct across a restart, because the stamp survives in the file.
            File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime);

            Touch(key, tile.Length);
            await EvictIfOverBudgetAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
        }
    }

    /// <inheritdoc/>
    public int Purge(Guid layerId)
    {
        string prefix = layerId.ToString("N", CultureInfo.InvariantCulture);
        int removed = 0;

        // The index first, so a concurrent read cannot re-adopt an entry this is
        // about to delete. The directory removal is best-effort after that:
        // a file left behind is unreachable, because nothing looks for a key the
        // index no longer counts and the next write re-creates the tree.
        foreach (KeyValuePair<string, Entry> entry in _index)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                && _index.TryRemove(entry.Key, out Entry gone))
            {
                Interlocked.Add(ref _bytes, -gone.Size);
                removed++;
            }
        }

        try
        {
            string directory = System.IO.Path.Combine(_root, prefix);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
        }

        return removed;
    }

    /// <inheritdoc/>
    public (int Entries, long Bytes) Report(Guid? layerId)
    {
        if (layerId is null)
        {
            return (_index.Count, Interlocked.Read(ref _bytes));
        }

        string prefix = layerId.Value.ToString("N", CultureInfo.InvariantCulture);
        int count = 0;
        long bytes = 0;

        foreach (KeyValuePair<string, Entry> entry in _index)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
                bytes += entry.Value.Size;
            }
        }

        return (count, bytes);
    }

    /// <summary>Records that an entry exists and was just used.</summary>
    private void Touch(TileCacheKey key, long size)
    {
        string path = key.Path();
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        _index.AddOrUpdate(
            path,
            _ =>
            {
                Interlocked.Add(ref _bytes, size);
                return new Entry(size, now);
            },
            (_, existing) =>
            {
                Interlocked.Add(ref _bytes, size - existing.Size);
                return new Entry(size, now);
            });
    }

    /// <summary>
    /// Drops least-recently-used entries until the cache is inside its budget.
    /// </summary>
    /// <remarks>
    /// <b>Down to 90%, not to exactly the budget.</b> Evicting to the line means
    /// the next write is over it again and every subsequent write pays an
    /// eviction — a cache that spends most of its time deleting. The headroom
    /// makes eviction occasional and bulk instead of constant and single.
    /// </remarks>
    private async Task EvictIfOverBudgetAsync()
    {
        if (Interlocked.Read(ref _bytes) <= _budget)
        {
            return;
        }

        if (!await _evicting.WaitAsync(0).ConfigureAwait(false))
        {
            // Somebody else is already evicting. Two threads deleting by LRU at
            // once would double-count and empty far more than needed.
            return;
        }

        try
        {
            long target = (long)(_budget * 0.9);

            foreach (KeyValuePair<string, Entry> entry in
                     _index.OrderBy(e => e.Value.LastUsed))
            {
                if (Interlocked.Read(ref _bytes) <= target)
                {
                    break;
                }

                if (!_index.TryRemove(entry.Key, out Entry gone))
                {
                    continue;
                }

                Interlocked.Add(ref _bytes, -gone.Size);

                try
                {
                    File.Delete(System.IO.Path.Combine(_root, entry.Key));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    WarnOnce(e);
                }
            }
        }
        finally
        {
            _evicting.Release();
        }
    }

    /// <summary>
    /// Takes ownership of whatever a previous run left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, a restart forgets the cache exists.</b> The files stay,
    /// reads keep hitting them, and the budget counts from zero — so the cache
    /// grows without limit across restarts while appearing to be under control.
    /// That is the disk-filling incident N6 warned about, arriving by a route
    /// the budget alone does not close.
    /// </para>
    /// <para>
    /// <b>Not fatal if it fails.</b> A cache that cannot be scanned is a cache
    /// that starts empty, which is slow rather than broken.
    /// </para>
    /// </remarks>
    private void Adopt()
    {
        try
        {
            Directory.CreateDirectory(_root);

            long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

            foreach (string file in Directory.EnumerateFiles(_root, "*.mvt", SearchOption.AllDirectories))
            {
                FileInfo info = new(file);
                string relative = System.IO.Path.GetRelativePath(_root, file).Replace('\\', '/');

                _index[relative] = new Entry(info.Length, now);
                Interlocked.Add(ref _bytes, info.Length);
            }

            if (!_index.IsEmpty)
            {
                long adopted = Interlocked.Read(ref _bytes);
                Log.TileCacheAdopted(_log, _index.Count, adopted / 1048576.0);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
        }
    }

    /// <summary>
    /// Says once that the cache is not working, then stops.
    /// </summary>
    /// <remarks>
    /// A cache failing on every request would otherwise write a log line per
    /// request, which turns a degraded optimisation into a disk-filling incident
    /// of its own — and buries whatever else the log was trying to say.
    /// </remarks>
    private void WarnOnce(Exception e)
    {
        if (_warned)
        {
            return;
        }

        _warned = true;
        Log.TileCacheDegraded(_log, e.Message);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _evicting.Dispose();
        }
    }

    /// <summary>Size and last use, for eviction and reporting only.</summary>
    private readonly record struct Entry(long Size, long LastUsed);
}
