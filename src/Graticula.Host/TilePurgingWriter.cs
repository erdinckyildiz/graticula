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
internal sealed class TilePurgingWriter(IFeatureWriter inner, ITileCache tiles, Guid layerId) : IFeatureWriter
{
    /// <inheritdoc/>
    public async Task<EditOutcome> ApplyAsync(EditBatch batch, CancellationToken cancellationToken)
    {
        EditOutcome outcome = await inner.ApplyAsync(batch, cancellationToken).ConfigureAwait(false);

        if (!outcome.RolledBack
            && outcome.Adds.Concat(outcome.Updates).Concat(outcome.Deletes).Any(result => result.Succeeded))
        {
            tiles.Purge(layerId);
        }

        return outcome;
    }
}
