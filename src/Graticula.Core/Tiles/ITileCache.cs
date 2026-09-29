using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Tiles;

/// <summary>
/// What a cached tile is: bytes, or a remembered absence, or nothing.
/// </summary>
/// <remarks>
/// <b>An empty tile is a result, not a miss.</b> Most of a pyramid is empty and
/// rebuilding the ocean on every request is pure waste — ADR-010 §2 calls this
/// negative caching and it matters more here than anywhere else, because a
/// sparse layer's cache is mostly emptiness. Collapsing <c>Empty</c> into
/// <c>Miss</c> would make the cache useless for exactly the tiles it holds most
/// of.
/// </remarks>
public enum TileCacheOutcome
{
    /// <summary>Nothing is held for this key.</summary>
    Miss,

    /// <summary>Bytes are held.</summary>
    Hit,

    /// <summary>This tile is known to have nothing in it.</summary>
    Empty,
}

/// <summary>A cache lookup.</summary>
/// <param name="Outcome">What was found.</param>
/// <param name="Bytes">The tile, when <paramref name="Outcome"/> is a hit.</param>
/// <param name="Written">
/// When the entry was stored, for a hit, or <see langword="null"/> when the cache does
/// not know.
/// </param>
/// <remarks>
/// <b><paramref name="Written"/> was added 2026-09-09 because the number already
/// existed and was being discarded — [D-248](../../../docs/architecture-debt.md).</b>
/// A cache read compares the entry's stamp against the lifetime to decide whether it
/// is still fresh, and then returned only the bytes; so a tile served from this
/// server's own store carried <c>Cache-Control: max-age</c> and no <c>Age</c>, and a
/// proxy in front of it restarted the whole lifetime from its own receipt. Worst-case
/// staleness was ours plus theirs, per layer of cache, and nothing in the response
/// said so.
/// </remarks>
/// <param name="Expired">
/// True when the entry is past its lifetime and was handed out anyway, by
/// <see cref="ITileCache.ReadExpiredAsync"/> — ADR-010 §5.1a. Never true from
/// <see cref="ITileCache.ReadAsync"/>, which treats an expired entry as a miss.
/// </param>
public readonly record struct CachedTile(
    TileCacheOutcome Outcome, byte[] Bytes, DateTimeOffset? Written = null, bool Expired = false)
{
    /// <summary>Nothing held.</summary>
    public static CachedTile Miss => new(TileCacheOutcome.Miss, []);

    /// <summary>Known to be empty.</summary>
    public static CachedTile Empty => new(TileCacheOutcome.Empty, []);

    /// <summary>Whether the caller can answer without building the tile.</summary>
    public bool Answered => Outcome != TileCacheOutcome.Miss;
}

/// <summary>
/// Holds built tiles so the datastore does not build them twice.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-021 made this more important, not less.</b> With encoding moved into
/// PostGIS, every cache miss is datastore load — and ADR-019 makes the datastore
/// mandatory and shared by every service, so the thing a miss costs is the one
/// resource the whole deployment contends for.
/// </para>
/// <para>
/// <b>Every method fails soft.</b> A cache is an optimisation and an
/// optimisation that can fail a request is a liability (ADR-010 §3). A full
/// disk, a permission problem or a corrupt file degrades to no-cache, never to
/// an error.
/// </para>
/// </remarks>
public interface ITileCache
{
    /// <summary>Looks a tile up.</summary>
    /// <param name="key">Which tile, and of what shape.</param>
    /// <param name="lifetime">
    /// How long an entry for this layer stays fresh.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found.</returns>
    /// <remarks>
    /// <para>
    /// <b>The lifetime is a parameter, not a property of the cache</b> — D-25,
    /// and [ADR-010](../../../docs/adr/ADR-010-caching.md) §5.3 said so from the
    /// start. Volatility belongs to the data: a cadastral layer changes twice a
    /// year and an incident layer changes every minute, and A-028 records that
    /// the administrator is the only person who knows which is which. One global
    /// number is wrong in both directions at once, and the two failures look
    /// nothing alike — too long shows an hour-old picture with nothing to say
    /// so; too short rebuilds a pyramid that has not changed since spring, which
    /// is the datastore load ADR-021 relies on this cache to absorb.
    /// </para>
    /// <para>
    /// <b>Passed on read rather than stored in the key.</b> The key is what
    /// identifies a tile, and a lifetime is not part of that identity —
    /// including it would make every entry a miss the moment an administrator
    /// changed the number, throwing away a whole seeded pyramid to apply a
    /// setting that does not affect a single byte of content.
    /// </para>
    /// </remarks>
    Task<CachedTile> ReadAsync(
        TileCacheKey key, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>
    /// Looks a tile up while its source cannot build it: the entry when it is fresh, or past its
    /// lifetime by no more than <paramref name="staleLimit"/> — ADR-010 §5.1a.
    /// </summary>
    /// <param name="key">Which tile, and of what shape.</param>
    /// <param name="lifetime">How long an entry for this layer stays fresh.</param>
    /// <param name="staleLimit">How long past that an entry may still be served; zero for never.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found, with <see cref="CachedTile.Expired"/> saying whether it is past its lifetime.</returns>
    /// <remarks>
    /// <para>
    /// <b>A second method rather than a flag on <see cref="ReadAsync"/>, because the two are asked at
    /// different moments.</b> <see cref="ReadAsync"/> is asked before a build and must call an expired entry
    /// a miss, or nothing would ever be rebuilt. This is asked only after the build was refused — the
    /// source's breaker is open, its budget is full, an operator quiesced it — when the alternative is a
    /// 503 rather than a rebuild. Owner decision 2026-09-29, D-278.
    /// </para>
    /// <para>
    /// <b>Only the exact key, so only what <see cref="Purge"/> left.</b> An entry an edit, a refresh or an
    /// unpublish purged is gone from the disk and cannot be found here; an entry a changed shape, grid,
    /// source or pipeline made unreachable is under another key and cannot be asked for. So §5.1's
    /// <em>wrong</em> class never comes back through this door, and only its <em>stale</em> class does.
    /// </para>
    /// <para>
    /// <b>A zero lifetime answers nothing, stale included.</b> It is an administrator saying <em>never serve
    /// this layer from a cache</em>, and an outage does not change what they asked for.
    /// </para>
    /// </remarks>
    Task<CachedTile> ReadExpiredAsync(
        TileCacheKey key, TimeSpan lifetime, TimeSpan staleLimit, CancellationToken cancellationToken);

    /// <summary>Stores a tile, or the fact that it is empty.</summary>
    /// <param name="key">Which tile.</param>
    /// <param name="tile">The bytes, or empty to remember an absence.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken);

    /// <summary>Stores a tile, then holds its service inside the service's own quota.</summary>
    /// <param name="key">Which tile.</param>
    /// <param name="tile">The bytes, or empty to remember an absence.</param>
    /// <param name="quota">The service's quota, or null for none — only the cache's own budget applies.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>The write is never refused for the quota</b> — owner decision 2026-09-29, ADR-010 §3. A service over
    /// its quota evicts its own tiles, in the order the whole cache evicts, and the tile just built is kept.
    /// </remarks>
    Task WriteAsync(TileCacheKey key, byte[] tile, TileCacheQuota? quota, CancellationToken cancellationToken);

    /// <summary>
    /// Removes everything held for a layer.
    /// </summary>
    /// <param name="layerId">The layer.</param>
    /// <returns>How many entries went.</returns>
    /// <remarks>
    /// <b>This is the <em>wrong</em> class of ADR-010 §5.1, not the stale one.</b>
    /// Unpublishing, a permission change or a schema change must purge rather
    /// than expire, because serving those is a correctness failure and in the
    /// permissions case a disclosure. Purged stays purged even during a source
    /// outage.
    /// </remarks>
    int Purge(Guid layerId);

    /// <summary>What the cache is holding, for one layer or all of them.</summary>
    /// <param name="layerId">The layer, or null for the whole cache.</param>
    /// <returns>Entry count and total bytes.</returns>
    /// <remarks>
    /// ADR-010 §6b: cache state must be readable per layer. An operator asking
    /// *is this layer seeded* or *why is the disk full* has no other way to find
    /// out, and a cache nobody can see is one nobody suspects.
    /// </remarks>
    (int Entries, long Bytes) Report(Guid? layerId);
}

/// <summary>
/// How many bytes of the cache one service's tiles may hold — ADR-010 §3's per-service quota.
/// </summary>
/// <param name="Service">The service, by catalogue id — what the quota's evictions are counted under.</param>
/// <param name="Layers">Its layers, whose tiles are the service's: a tile is cached per layer.</param>
/// <param name="Bytes">The quota.</param>
/// <remarks>
/// <b>Carried with the write rather than kept by the cache</b>, because the cache is keyed by layer and knows
/// nothing of services, and the catalogue that does is read on every request anyway. A quota changed by an
/// administrator is therefore in force on the next tile built, with nothing to invalidate.
/// </remarks>
public sealed record TileCacheQuota(Guid Service, System.Collections.Generic.IReadOnlyList<Guid> Layers, long Bytes);
