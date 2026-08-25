namespace Graticula.Tiles;

/// <summary>
/// Which generation of the tiling code built a tile.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-155](../../../docs/architecture-debt.md): the cache key described the data and
/// never the code.</b> <see cref="TileCacheKey.FingerprintOf"/> takes five inputs and all
/// five are properties of the layer, so an upgrade that changed how a tile is drawn kept
/// every key it had and the deployment went on serving bytes built by the previous
/// release until each entry's lifetime ran out. Quiet by construction: the old tile is
/// valid MVT, it draws, and it disagrees with its neighbour rebuilt after that
/// neighbour's lifetime expired — so a map can show two generations of one pipeline at a
/// seam that moves.
/// </para>
/// <para>
/// <b>Not a build stamp, which is the fix that looks obvious and is wrong.</b> Stamping
/// the release into the path throws away every tile on every release, including the
/// releases that change nothing about tiling — which is
/// [ADR-010](../../../docs/adr/ADR-010-caching.md) §8's own objection arriving from the
/// other side: a full rebuild should be a deliberate, visible event rather than a side
/// effect of shipping.
/// </para>
/// <para>
/// <b>So it is a declared number, and the declaration cannot go stale.</b> A version
/// somebody has to remember to raise is the hand-maintained vocabulary
/// [Q-101](../../../docs/open-questions.md) is about — it lies in exactly the situation
/// it exists for. <c>TilePipelineVersionTests</c> hashes the source of the files that
/// decide a tile's bytes and fails the build when that hash changes without this number
/// changing. Raising it is then a decision somebody takes on purpose, and *not* raising
/// it is a decision they take on purpose too, in front of a failing test that names the
/// files that moved.
/// </para>
/// <para>
/// <b>Raising this throws away every cached tile in the deployment</b>, and that is the
/// intended effect. Entries under the old version become unreachable rather than wrong:
/// the same structural invalidation the schema fingerprint already gives, extended to
/// the half of the key that was missing.
/// </para>
/// </remarks>
public static class TilePipeline
{
    /// <summary>
    /// The generation of the tiling code. Raise it when a change alters a tile's bytes.
    /// </summary>
    /// <remarks>
    /// <b>Starts at 1 on 2026-08-25, not at 0.</b> Zero would be indistinguishable in a
    /// path from the absence this replaces, and a deployment upgraded onto this release
    /// should invalidate once — the caches it holds were built by code that never
    /// declared a generation at all, so nothing can say whether they agree with it.
    /// </remarks>
    public const int Version = 1;
}
