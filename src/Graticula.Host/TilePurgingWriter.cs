using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Features;
using Graticula.Tiles;

namespace Graticula.Host;

/// <summary>
/// Empties a layer's cached tiles once an edit to it has been kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole layer, not the tiles the edit touched.</b> Which tiles an edit reaches depends on
/// the geometry before and after it, at every zoom, with the tile buffer around each — the old
/// shape of an update is not in the batch, and a delete carries no shape at all. Working it out
/// would mean reading every edited row before the write. Emptying the layer is always right and
/// costs a cold pyramid; a layer edited often enough for that to matter is one whose lifetime an
/// operator should already have set short (D-25).
/// </para>
/// <para>
/// <b>Only when something was kept.</b> A batch that was rolled back, or in which every edit
/// failed, changed nothing in the datastore, and emptying a warm cache for it would be a cost with
/// nothing bought.
/// </para>
/// <para>
/// <b>What it does not close:</b> a tile build that read the layer before the edit committed and
/// writes its bytes after this purge leaves one stale entry, for at most the layer's lifetime.
/// That window is the length of one tile build, and closing it would need a version on the key
/// the datastore does not have for a hosted table.
/// </para>
/// </remarks>
internal sealed class TilePurgingWriter(
    IFeatureWriter inner,
    ITileCache tiles,
    Guid[] layerIds,
    Func<CancellationToken, Task<Graticula.Tiles.VectorTileScheme?>>? schemeOf = null) : IFeatureWriter
{
    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        VectorTileScheme? scheme = schemeOf is null ? null : await schemeOf(cancellationToken).ConfigureAwait(false);
        IEditedExtent? store = inner as IEditedExtent;
        Graticula.Geometries.Envelope? before = await BeforeAsync(store, scheme, batch, cancellationToken).ConfigureAwait(false);

        EditOutcome outcome = await inner.ApplyAsync(batch, cancellationToken).ConfigureAwait(false);

        await PurgeAfterAsync(tiles, store, scheme, layerIds, before, outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Where the features an edit will touch were, before it moves or removes them — ADR-101 condition 4; null when
    /// the layer's scheme or the store cannot say, and the whole layer is then emptied as every edit did before.
    /// </summary>
    internal static async Task<Graticula.Geometries.Envelope?> BeforeAsync(
        IEditedExtent? store, VectorTileScheme? scheme, EditBatch batch, CancellationToken cancellationToken) =>
        scheme is null || store is null
            ? null
            : await store.ExtentAsync([.. batch.Updates.Select(u => u.Identity), .. batch.Deletes], scheme.Srid, cancellationToken)
                .ConfigureAwait(false);

    /// <summary>
    /// Empties what an edit that kept something reached: the layer's own tiles by address when the extent is known on
    /// both sides of the edit, and otherwise — and for every layer showing its rows under another service (ADR-113),
    /// which may be cut on another scheme — the whole layer.
    /// </summary>
    internal static async Task PurgeAfterAsync(
        ITileCache tiles,
        IEditedExtent? store,
        VectorTileScheme? scheme,
        Guid[] layerIds,
        Graticula.Geometries.Envelope? before,
        EditOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.RolledBack
            || !outcome.Adds.Concat(outcome.Updates).Concat(outcome.Deletes).Any(result => result.Succeeded))
        {
            return;
        }

        Graticula.Geometries.Envelope? after = before is null || store is null || scheme is null
            ? null
            : await store.ExtentAsync(
                    [.. outcome.Adds.Concat(outcome.Updates).Where(r => r.Succeeded).Select(r => r.Identity)],
                    scheme.Srid, cancellationToken)
                .ConfigureAwait(false);

        for (int i = 0; i < layerIds.Length; i++)
        {
            if (i == 0 && before is { } was && after is { } now)
            {
                Graticula.Geometries.Envelope reached = was.IsEmpty ? now : now.IsEmpty ? was : was.Union(now);

                if (!reached.IsEmpty)
                {
                    tiles.Purge(layerIds[i], EditedTiles.Reached(scheme!, reached));
                }

                continue;
            }

            tiles.Purge(layerIds[i]);
        }
    }
}
