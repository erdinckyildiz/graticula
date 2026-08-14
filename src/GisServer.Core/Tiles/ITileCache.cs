using System;
using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Tiles;

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
public readonly record struct CachedTile(TileCacheOutcome Outcome, byte[] Bytes)
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
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was found.</returns>
    Task<CachedTile> ReadAsync(TileCacheKey key, CancellationToken cancellationToken);

    /// <summary>Stores a tile, or the fact that it is empty.</summary>
    /// <param name="key">Which tile.</param>
    /// <param name="tile">The bytes, or empty to remember an absence.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task WriteAsync(TileCacheKey key, byte[] tile, CancellationToken cancellationToken);

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
