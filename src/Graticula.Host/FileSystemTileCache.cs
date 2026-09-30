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

    /// <summary>How many bytes the cache may hold — <c>Graticula:TileCacheBudgetMB</c>.</summary>
    /// <remarks>
    /// <b>Readable so that a seed can be measured against it before it starts</b> — ADR-093 §3. A seed
    /// that does not fit is a seed that evicts, and the operator is owed that sentence before the job
    /// runs rather than a cache read-back that has quietly stopped matching the seed's own counts.
    /// </remarks>
    public long Budget => _budget;

    /// <summary>
    /// How long past its lifetime a tile may still be served while its source cannot build it, when its
    /// layer names no limit of its own — <c>Graticula:TileStaleIfErrorHours</c>, 24 hours by default.
    /// </summary>
    /// <remarks>
    /// <b>Owner decision 2026-09-29, ADR-010 §5.1a.</b> Readable here beside <see cref="DefaultLifetime"/> for the
    /// same reason that one is: the tile route asks the cache for its defaults rather than taking a second copy of
    /// the settings.
    /// </remarks>
    public TimeSpan DefaultStaleLimit { get; }

    /// <summary>The stale limit when nothing configures one: 24 hours past a tile's lifetime.</summary>
    internal const int DefaultStaleIfErrorHours = 24;

    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private readonly ConcurrentDictionary<string, Entry> _index = new(StringComparer.Ordinal);

    /// <summary>What each layer's entries hold, kept beside <see cref="_index"/> as <see cref="_bytes"/> is.</summary>
    /// <remarks>
    /// <b>So that a service's quota is a sum of a few counters rather than a pass over the index</b> on every
    /// write — ADR-010 §3. The index is a whole cache's worth of entries; a service has a handful of layers.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, long> _layerBytes = new();

    /// <summary>What each service's quota has evicted since this process started — the read-back's number.</summary>
    private readonly ConcurrentDictionary<Guid, QuotaEvictions> _quotaEvicted = new();
    /// <summary>When each layer was last purged, for a layer whose directory could not be taken away at once.</summary>
    /// <remarks>
    /// <b>The read path trusts the disk, so a purge that leaves a file behind leaves a tile a read will serve</b> —
    /// the index says nothing to <see cref="ReadAsync"/> (N2). <see cref="Purge"/> therefore moves the layer's
    /// directory aside, which is one rename; only when even that fails does it fall back to this stamp, and every read
    /// then refuses a file written at or before it. Removed once a later purge of the layer succeeds.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, DateTime> _purgedAt = new();

    /// <summary>Where a purged layer's directory goes to be deleted — outside every path a key can name.</summary>
    internal const string PurgedDirectory = ".purged";

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
    /// <param name="staleLimit">
    /// How long past its lifetime a tile may be served while its source cannot build it, or null for
    /// <see cref="DefaultStaleIfErrorHours"/>. Last and optional, so every caller written before it is unchanged.
    /// </param>
    public FileSystemTileCache(
        string root,
        long budget,
        long perLayerBudget,
        TimeSpan lifetime,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        TimeSpan? staleLimit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _root = root;
        _budget = budget;
        _perLayerBudget = perLayerBudget;
        DefaultLifetime = lifetime;
        DefaultStaleLimit = staleLimit ?? TimeSpan.FromHours(DefaultStaleIfErrorHours);
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

            if (!file.Exists || PurgedAfter(key.LayerId, file.LastWriteTimeUtc))
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

            // <b>The stamp the line above already read, carried out instead of
            // dropped.</b> It is this cache's own clock, set on write and never
            // touched again — `Touch` is in-memory bookkeeping for the LRU and does
            // not move it — so it is the moment the tile was generated, which is
            // exactly what `Age` means. D-248.
            DateTimeOffset written = new(file.LastWriteTimeUtc, TimeSpan.Zero);

            return file.Length == EmptyMarker
                ? CachedTile.Empty with { Written = written }
                : new CachedTile(
                    TileCacheOutcome.Hit,
                    await File.ReadAllBytesAsync(path, cancellationToken)
                        .ConfigureAwait(false),
                    written);
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
    /// <remarks>
    /// <para>
    /// <b>An expired entry is still on the disk because nothing here deletes one for being old.</b>
    /// <see cref="ReadAsync"/> calls it a miss and leaves it; it goes when it is rebuilt over, when eviction
    /// reaches it — in the budget's order, where its age earns it nothing — or when its layer is purged. So
    /// <em>expired</em> is a state an entry passes through on the way to being replaced, and <em>purged</em>
    /// is its absence: <see cref="Purge"/> deletes the layer's directory, and a key with no file behind it
    /// answers <see cref="CachedTile.Miss"/> here as it does everywhere.
    /// </para>
    /// <para>
    /// <b>Touched like a hit</b>, because it is being used: an entry keeping a map drawn through an outage is the
    /// last one the least-recently-used half of eviction should pick within its level.
    /// </para>
    /// </remarks>
    public async Task<CachedTile> ReadExpiredAsync(
        TileCacheKey key, TimeSpan lifetime, TimeSpan staleLimit, CancellationToken cancellationToken)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return CachedTile.Miss;
        }

        string path = System.IO.Path.Combine(_root, key.Path());

        try
        {
            FileInfo file = new(path);

            if (!file.Exists || PurgedAfter(key.LayerId, file.LastWriteTimeUtc))
            {
                return CachedTile.Miss;
            }

            TimeSpan age = _clock.GetUtcNow() - file.LastWriteTimeUtc;

            if (age > lifetime + (staleLimit > TimeSpan.Zero ? staleLimit : TimeSpan.Zero))
            {
                return CachedTile.Miss;
            }

            Touch(key, file.Length);

            DateTimeOffset written = new(file.LastWriteTimeUtc, TimeSpan.Zero);
            bool expired = age > lifetime;

            return file.Length == EmptyMarker
                ? CachedTile.Empty with { Written = written, Expired = expired }
                : new CachedTile(
                    TileCacheOutcome.Hit,
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                    written,
                    expired);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
            return CachedTile.Miss;
        }
    }

    /// <inheritdoc/>
    public Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken) =>
        WriteAsync(key, tile, null, cancellationToken);

    /// <inheritdoc/>
    public async Task WriteAsync(
        TileCacheKey key, byte[] tile, TileCacheQuota? quota, CancellationToken cancellationToken)
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

            if (quota is not null)
            {
                await EvictIfOverQuotaAsync(quota, key.Path()).ConfigureAwait(false);
            }
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

        // <b>Stamped before anything is removed</b>, so a read that lands between here and the directory going away
        // already refuses what it finds. The stamp is dropped below once the directory is gone.
        _purgedAt[layerId] = _clock.GetUtcNow().UtcDateTime;

        // The index next, so eviction and the quota stop counting what is about to go.
        //
        // <b>This comment used to say a file left behind was unreachable, "because nothing looks for a key the index
        // no longer counts". The read path looks for exactly that</b> — it is one File.Exists, by design (N2) — so a
        // delete that failed part-way left tiles a read served as fresh until their lifetime ran out. What makes the
        // purge hold is now the move below, and the stamp when even the move fails.
        foreach (KeyValuePair<string, Entry> entry in _index)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                && _index.TryRemove(entry.Key, out Entry gone))
            {
                Count(gone.Layer, -gone.Size);
                removed++;
            }
        }

        // Subtracted entry by entry above rather than dropped wholesale, so a write racing the purge keeps its own
        // count; the counter itself goes only when it is back to nothing (D-279).
        _layerBytes.TryRemove(new KeyValuePair<Guid, long>(layerId, 0));

        // <b>Moved aside in one rename, then deleted.</b> The rename is what a read can observe: before it the
        // stamp refuses the old files, after it there are none at the layer's path. The delete that follows may
        // fail or take long on a large pyramid and changes nothing a read sees; what it leaves is removed at the
        // next start (<see cref="Adopt"/>), which never adopts anything under <see cref="PurgedDirectory"/>.
        string directory = System.IO.Path.Combine(_root, prefix);
        bool movedAside;

        try
        {
            if (Directory.Exists(directory))
            {
                string aside = System.IO.Path.Combine(
                    _root,
                    PurgedDirectory,
                    string.Create(CultureInfo.InvariantCulture, $"{prefix}.{Guid.NewGuid():N}"));

                Directory.CreateDirectory(System.IO.Path.Combine(_root, PurgedDirectory));
                Directory.Move(directory, aside);
                DeleteQuietly(aside);
            }

            movedAside = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The rename failed — on Windows, a file under the directory is open. The old best-effort delete is
            // still worth trying for the space; the stamp is what keeps the answer right.
            WarnOnce(e);
            DeleteQuietly(directory);
            movedAside = !Directory.Exists(directory);
        }

        if (movedAside)
        {
            _purgedAt.TryRemove(layerId, out _);
        }

        return removed;
    }

    /// <summary>Whether a file of this layer was written at or before the layer's last purge that did not complete.</summary>
    private bool PurgedAfter(Guid layer, DateTime written) =>
        !_purgedAt.IsEmpty && _purgedAt.TryGetValue(layer, out DateTime purged) && written <= purged;

    /// <summary>Deletes a directory tree, and says nothing if it cannot.</summary>
    private void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
        }
    }

    /// <summary>
    /// The tiles of one layer, at one level and inside a rectangle, that are cached and fresh —
    /// ADR-010 §6b's read-back, for a seeded area.
    /// </summary>
    /// <param name="layer">Any key of the layer at the level: the layer, its fingerprint and the
    /// level are read from it, and its column and row are not.</param>
    /// <param name="range">The rectangle.</param>
    /// <param name="lifetime">How long the layer's tiles stay fresh.</param>
    /// <returns>Each fresh tile's position as <c>x × 2^32 + y</c> — not <c>x × 2^z + y</c>, since a custom tiling scheme's level need not be 2^z tiles wide (ADR-096).</returns>
    /// <remarks>
    /// <para>
    /// <b>Read from the directory rather than from the index</b>, for the reason §3 (N2) gives the
    /// lookup: the path derives from the key, so the directory is the truth and the index is this
    /// process's memory of it. The level's directory is found from <see cref="TileCacheKey.Path"/>
    /// itself, so this cannot come to disagree with where a tile is written.
    /// </para>
    /// <para>
    /// <b>Only the columns that exist are listed</b>, so the cost follows what is cached rather than
    /// how wide the rectangle is. Fresh is <see cref="ReadAsync"/>'s test — the write time against
    /// the lifetime — so a tile counted here is one a request would be served.
    /// </para>
    /// </remarks>
    internal HashSet<long> FreshIn(TileCacheKey layer, TileRange range, TimeSpan lifetime)
    {
        HashSet<long> fresh = [];

        if (lifetime <= TimeSpan.Zero)
        {
            return fresh;
        }

        string level = System.IO.Path.Combine(
            _root,
            System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(
                (layer with { Address = new TileAddress(range.Z, 0, 0) }).Path()))!);

        DateTime oldest = _clock.GetUtcNow().UtcDateTime - lifetime;

        try
        {
            if (!Directory.Exists(level))
            {
                return fresh;
            }

            foreach (string column in Directory.EnumerateDirectories(level))
            {
                if (!int.TryParse(System.IO.Path.GetFileName(column), NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                    || x < range.MinX || x > range.MaxX)
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(column, "*.mvt"))
                {
                    if (!int.TryParse(
                            System.IO.Path.GetFileNameWithoutExtension(file), NumberStyles.None,
                            CultureInfo.InvariantCulture, out int y)
                        || y < range.MinY || y > range.MaxY)
                    {
                        continue;
                    }

                    DateTime written = File.GetLastWriteTimeUtc(file);

                    if (written >= oldest && !PurgedAfter(layer.LayerId, written))
                    {
                        // Column and row packed into one number without assuming 2^z a side — a custom
                        // tiling scheme's level may be wider (ADR-096).
                        fresh.Add(((long)x << 32) | (uint)y);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
        }

        return fresh;
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

        // A key this process made is of the pipeline running now by construction: Path() wrote it.
        Entry touched = new(size, now, key.Address.Z, Current: true, key.LayerId);

        // <b>The bytes are counted after the index has taken the change, and only by the call whose change it
        // took — [D-283](../../docs/architecture-debt.md).</b> This was `AddOrUpdate` with `Interlocked.Add` inside
        // its two factories, and `ConcurrentDictionary` runs a factory again whenever another thread changed the
        // key first, keeping only the last result: every lost race counted its delta anyway. The totals drifted
        // from what the index held under concurrent writes and reads of one tile, and eviction then acted on bytes
        // that were not there. Here the delta is taken from the entry the swap actually replaced, so each insert or
        // replace is counted once, whoever wins.
        while (true)
        {
            if (_index.TryGetValue(path, out Entry existing))
            {
                if (_index.TryUpdate(path, touched, existing))
                {
                    Count(existing.Layer, size - existing.Size);
                    return;
                }
            }
            else if (_index.TryAdd(path, touched))
            {
                Count(key.LayerId, size);
                return;
            }
        }
    }

    /// <summary>Moves the whole cache's byte count and one layer's by the same delta — the only way either moves.</summary>
    private void Count(Guid layer, long delta)
    {
        Interlocked.Add(ref _bytes, delta);
        CountLayer(layer, delta);
    }

    /// <summary>Moves a layer's byte count by a delta.</summary>
    private void CountLayer(Guid layer, long delta)
    {
        if (delta != 0)
        {
            _layerBytes.AddOrUpdate(layer, delta, (_, held) => held + delta);
        }
    }

    /// <summary>
    /// Drops entries until the cache is inside its budget: another pipeline's first, then the highest
    /// level first, and the least recently used first within a level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Down to 90%, not to exactly the budget.</b> Evicting to the line means
    /// the next write is over it again and every subsequent write pays an
    /// eviction — a cache that spends most of its time deleting. The headroom
    /// makes eviction occasional and bulk instead of constant and single.
    /// </para>
    /// <para>
    /// <b>The level comes before the recency, by owner decision on 2026-09-29</b> — ADR-093 §3. Plain
    /// least-recently-used evicted the wrong end of a seed: a seed walks lowest level first, so the
    /// tiles it built first are the oldest in the cache, and a seed larger than the budget threw away
    /// its own low levels to make room for its high ones. ADR-010 §6a measured the low levels as the
    /// most expensive to build and the most valuable to hold, which is exactly backwards.
    /// </para>
    /// <para>
    /// <b>A strict order rather than recency weighted by level, because weighting does not give the
    /// property.</b> Any finite bonus per level is overtaken by a seed that runs long enough: a level-5
    /// tile written two hours before a level-14 tile loses to it under every weight that still lets
    /// recency matter at all. A strict order holds however long the seed runs, and the grid bounds it —
    /// over any area, every level below <c>z</c> together holds about a third as many tiles as level
    /// <c>z</c>, so what it protects is always the small end of the pyramid.
    /// </para>
    /// <para>
    /// <b>What it costs, said rather than hidden.</b> A map browsing deep levels works in whatever the
    /// lower levels leave free, and a deep tile read a second ago goes before a shallow one nobody has
    /// read since the seed. That is the trade the decision made: a shallow tile is the one every map
    /// passes through, and the one that takes longest to build again.
    /// </para>
    /// <para>
    /// <b>Another pipeline's tiles go before any of that</b>, because nothing can read them again:
    /// <see cref="TilePipeline.Version"/> is in every key (D-155), so after an upgrade the old
    /// generation's pyramid is unreachable, and a strict level order alone would keep its low levels in
    /// preference to the new generation's live high ones. A tile orphaned by a changed fingerprint is
    /// not recognised — nothing in its path says so — and is bounded by the same third; the refresh
    /// that notices the change purges the layer anyway.
    /// </para>
    /// <para>
    /// <b>No dearer than before</b>: the same one sort over the index, by three keys instead of one.
    /// </para>
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
            long target = TargetOf(_budget);

            foreach (KeyValuePair<string, Entry> entry in
                     _index.OrderBy(e => e.Value.Current)
                           .ThenByDescending(e => e.Value.Zoom)
                           .ThenBy(e => e.Value.LastUsed))
            {
                if (Interlocked.Read(ref _bytes) <= target)
                {
                    break;
                }

                Evict(entry.Key);
            }
        }
        finally
        {
            _evicting.Release();
        }
    }

    /// <summary>
    /// Drops one service's entries until the service is inside its quota — in the order the whole cache evicts
    /// in, and touching no other service's tiles.
    /// </summary>
    /// <param name="quota">The service, its layers and its quota.</param>
    /// <param name="written">The entry just written, which is never the one evicted to make room for itself.</param>
    /// <remarks>
    /// <para>
    /// <b>Owner decision 2026-09-29, ADR-010 §3: evict the service's own, never refuse the write.</b> The same
    /// three keys as <see cref="EvictIfOverBudgetAsync"/> — another pipeline's tiles first, then the highest
    /// level, then the least recently used within a level — so a quota keeps a service's low levels for the
    /// reason the budget keeps everybody's, and a seed estimated against a quota (ADR-093 §5.9) is estimated
    /// against the order that will actually run.
    /// </para>
    /// <para>
    /// <b>Down to 90% of the quota, by <see cref="TargetOf"/></b>, for the budget's reason: evicting to the line
    /// makes every following write of the service pay an eviction.
    /// </para>
    /// <para>
    /// <b>The same lock as the budget's, taken the same way.</b> A write that finds either eviction running
    /// leaves the work to it or to the next write, rather than two passes deleting at once; a service left over
    /// its quota by that is brought back by its next tile, which is the only thing that can take it further
    /// over.
    /// </para>
    /// <para>
    /// <b>The tile just written is kept</b>, which is what <em>never refuse the write</em> means once the write has
    /// happened: evicting it would be a refusal by another name, and a quota smaller than one tile would leave the
    /// service with nothing cached at all. Such a service holds that one tile, over its quota, until the next.
    /// </para>
    /// <para>
    /// <b>A pass over the index only when the service is over</b>, which the per-layer counters say without one;
    /// and evicting to 90% makes that occasional, as it does for the budget.
    /// </para>
    /// </remarks>
    private async Task EvictIfOverQuotaAsync(TileCacheQuota quota, string written)
    {
        if (HeldBy(quota.Layers) <= quota.Bytes)
        {
            return;
        }

        if (!await _evicting.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            HashSet<Guid> layers = [.. quota.Layers];
            long target = TargetOf(quota.Bytes);
            long held = HeldBy(quota.Layers);

            foreach (KeyValuePair<string, Entry> entry in
                     _index.Where(e => layers.Contains(e.Value.Layer)
                                       && !string.Equals(e.Key, written, StringComparison.Ordinal))
                           .OrderBy(e => e.Value.Current)
                           .ThenByDescending(e => e.Value.Zoom)
                           .ThenBy(e => e.Value.LastUsed)
                           .ToList())
            {
                if (held <= target)
                {
                    break;
                }

                if (Evict(entry.Key) is { } gone)
                {
                    held -= gone.Size;

                    _quotaEvicted.AddOrUpdate(
                        quota.Service,
                        new QuotaEvictions(1, gone.Size),
                        (_, sofar) => new QuotaEvictions(sofar.Entries + 1, sofar.Bytes + gone.Size));
                }
            }
        }
        finally
        {
            _evicting.Release();
        }
    }

    /// <summary>Removes one entry from the index and its file from the disk.</summary>
    /// <returns>The entry, or null when somebody else removed it first.</returns>
    private Entry? Evict(string relative)
    {
        if (!_index.TryRemove(relative, out Entry gone))
        {
            return null;
        }

        Count(gone.Layer, -gone.Size);

        try
        {
            File.Delete(System.IO.Path.Combine(_root, relative));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WarnOnce(e);
        }

        return gone;
    }

    /// <summary>What a set of layers holds in the cache — a service's use against its quota.</summary>
    /// <param name="layers">The layers.</param>
    /// <returns>Their bytes, from the per-layer counters, with no pass over the index.</returns>
    internal long HeldBy(IEnumerable<Guid> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        long bytes = 0;

        foreach (Guid layer in layers)
        {
            bytes += Math.Max(0, _layerBytes.GetValueOrDefault(layer));
        }

        return bytes;
    }

    /// <summary>What a service's quota has evicted since this process started.</summary>
    /// <param name="service">The service.</param>
    /// <returns>Entries and bytes.</returns>
    internal QuotaEvictions EvictedByQuota(Guid service) => _quotaEvicted.GetValueOrDefault(service);

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

            // What a purge moved aside and could not finish deleting. Never adopted: it is a purged layer's tiles.
            DeleteQuietly(System.IO.Path.Combine(_root, PurgedDirectory));

            long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

            foreach (string file in Directory.EnumerateFiles(_root, "*.mvt", SearchOption.AllDirectories))
            {
                if (System.IO.Path.GetRelativePath(_root, file).StartsWith(PurgedDirectory, StringComparison.Ordinal))
                {
                    continue;
                }

                FileInfo info = new(file);
                string relative = System.IO.Path.GetRelativePath(_root, file).Replace('\\', '/');

                (int zoom, bool current) = LevelOf(relative);
                Guid layer = LayerOf(relative);
                if (_index.TryAdd(relative, new Entry(info.Length, now, zoom, current, layer)))
                {
                    Count(layer, info.Length);
                }
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

    /// <summary>
    /// The level of an adopted file, and whether the pipeline running now wrote it — read from the
    /// path, which is <see cref="TileCacheKey.Path"/>'s <c>{layer}/v{version}/{fingerprint}/{z}/{x}/{y}.mvt</c>.
    /// </summary>
    /// <param name="relative">The file's path under the root, with forward slashes.</param>
    /// <returns>The level, and whether it is of the current pipeline.</returns>
    /// <remarks>
    /// <b>A path that does not parse is evicted first</b>: it is not a key anything asks for, so it is
    /// the same garbage another pipeline's tile is.
    /// </remarks>
    internal static (int Zoom, bool Current) LevelOf(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        string[] parts = relative.Split('/');

        if (parts.Length != 6
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int zoom))
        {
            return (int.MaxValue, false);
        }

        return (zoom, string.Equals(
            parts[1],
            string.Create(CultureInfo.InvariantCulture, $"v{TilePipeline.Version}"),
            StringComparison.Ordinal));
    }

    /// <summary>The layer an adopted file belongs to — the first segment of its path — or empty when it has none.</summary>
    /// <param name="relative">The file's path under the root, with forward slashes.</param>
    /// <returns>The layer.</returns>
    internal static Guid LayerOf(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        int cut = relative.IndexOf('/', StringComparison.Ordinal);

        return cut > 0 && Guid.TryParseExact(relative.AsSpan(0, cut), "N", out Guid layer) ? layer : Guid.Empty;
    }

    /// <summary>
    /// What the cache holds of one layer, level by level — how many of its tiles and bytes, and how
    /// many of a seed's rectangle are already there — for a seed's size estimate.
    /// </summary>
    /// <param name="layer">Any key of the layer as serving asks for it now: its layer, fingerprint and
    /// pipeline generation are read from it, and its address is not.</param>
    /// <param name="ranges">The seed's rectangle at each level.</param>
    /// <returns>The holding at each level where the cache has anything of the layer.</returns>
    /// <remarks>
    /// <para>
    /// <b>From the index, in one pass, and it opens no file.</b> The samples are every entry of the
    /// layer at the level, whatever fingerprint or generation wrote it — an older shape's tiles are as
    /// good a guide to size as the current one's. What is <em>present</em> is narrower: the entries a
    /// seed would find under the key it writes, inside its rectangle, because only those it will not
    /// add again. An expired one counts as present too, since the seed rewrites it in place.
    /// </para>
    /// <para>
    /// <b>The prefix is cut from <see cref="TileCacheKey.Path"/> itself</b>, as <see cref="FreshIn"/>
    /// finds its directory, so this cannot come to disagree with where a tile is written.
    /// </para>
    /// </remarks>
    internal IReadOnlyDictionary<int, TileSeedEstimate.Holding> HoldingOf(
        TileCacheKey layer, IReadOnlyList<TileRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        string id = layer.LayerId.ToString("N", CultureInfo.InvariantCulture) + "/";
        string current = PrefixOf(layer);

        Dictionary<int, TileRange> wanted = ranges.ToDictionary(range => range.Z);
        Dictionary<int, TileSeedEstimate.Holding> found = [];

        foreach (KeyValuePair<string, Entry> entry in _index)
        {
            if (!entry.Key.StartsWith(id, StringComparison.Ordinal)
                || !wanted.TryGetValue(entry.Value.Zoom, out TileRange range))
            {
                continue;
            }

            found.TryGetValue(range.Z, out TileSeedEstimate.Holding held);

            bool present = entry.Key.StartsWith(current, StringComparison.Ordinal)
                && Within(entry.Key, current.Length, range);

            found[range.Z] = new TileSeedEstimate.Holding(
                held.Samples + 1, held.SampleBytes + entry.Value.Size, held.Present + (present ? 1 : 0));
        }

        return found;
    }

    /// <summary>What eviction brings the cache down to once it is over its budget: 90% of it.</summary>
    /// <param name="budget">The budget.</param>
    /// <returns>The target, in bytes.</returns>
    /// <remarks>
    /// A function rather than a literal inside the eviction because a seed's estimate asks the same
    /// question — how much is left once the cache has evicted — and two copies of 0.9 would drift.
    /// </remarks>
    internal static long TargetOf(long budget) => (long)(budget * 0.9);

    /// <summary>
    /// The bytes the cache holds at each level, counting only tiles the pipeline running now wrote —
    /// the ones eviction ranks by level (<see cref="EvictIfOverBudgetAsync"/>).
    /// </summary>
    /// <returns>Bytes by level; a level holding nothing is absent.</returns>
    /// <remarks>
    /// <b>For a seed's estimate</b> (ADR-093 §5.9): whatever sits at a level below a seed tile's
    /// outranks it and is kept in its place, so these are the bytes a seed cannot make room from.
    /// Another pipeline's tiles are left out, because they go before anything.
    /// </remarks>
    /// <param name="layers">Only these layers' tiles — a service's, for an estimate against its quota — or null
    /// for the whole cache.</param>
    internal IReadOnlyDictionary<int, long> CurrentBytesByLevel(IReadOnlySet<Guid>? layers = null)
    {
        Dictionary<int, long> held = [];

        foreach (KeyValuePair<string, Entry> entry in _index)
        {
            if (entry.Value.Current && (layers is null || layers.Contains(entry.Value.Layer)))
            {
                held[entry.Value.Zoom] = held.GetValueOrDefault(entry.Value.Zoom) + entry.Value.Size;
            }
        }

        return held;
    }

    /// <summary><c>{layer}/v{version}/{fingerprint}/</c>, cut from the key's own path.</summary>
    private static string PrefixOf(TileCacheKey layer)
    {
        string path = (layer with { Address = new TileAddress(0, 0, 0) }).Path();

        // Drop "0/0/0.mvt", the three segments the address wrote.
        int cut = path.Length;

        for (int i = 0; i < 3; i++)
        {
            cut = path.LastIndexOf('/', cut - 1);
        }

        return path[..(cut + 1)];
    }

    /// <summary>Whether the <c>{z}/{x}/{y}.mvt</c> after the prefix lies inside the rectangle.</summary>
    private static bool Within(string key, int start, TileRange range)
    {
        string[] parts = key[start..].Split('/');

        return parts.Length == 3
            && parts[2].EndsWith(".mvt", StringComparison.Ordinal)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[2].AsSpan(0, parts[2].Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int y)
            && x >= range.MinX && x <= range.MaxX && y >= range.MinY && y <= range.MaxY;
    }

    /// <summary>Size, last use, level and generation, for eviction and reporting only.</summary>
    /// <param name="Size">The file's length.</param>
    /// <param name="LastUsed">When it was last read or written, in Unix milliseconds.</param>
    /// <param name="Zoom">Its level, which eviction orders by before recency.</param>
    /// <param name="Current">Whether the pipeline running now wrote it; a tile it did not write is unreachable.</param>
    /// <param name="Layer">The layer it belongs to, for a service's quota; empty for a path that does not parse.</param>
    private readonly record struct Entry(long Size, long LastUsed, int Zoom, bool Current, Guid Layer);

    /// <summary>What a service's quota has evicted.</summary>
    /// <param name="Entries">How many entries.</param>
    /// <param name="Bytes">How many bytes.</param>
    internal readonly record struct QuotaEvictions(long Entries, long Bytes);
}
