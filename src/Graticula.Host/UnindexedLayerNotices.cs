using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// Which layers this server has tiled from a table with no spatial index on its geometry column.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-095 §5.2: served, slowly, and said once.</b> A registered PostGIS table belongs to somebody
/// else and may have no GiST index; the tile statement's <c>&amp;&amp;</c> then reads the whole table for
/// every cold tile. Refusing the tile would take away a map that works; saying nothing leaves the operator
/// to learn it from a slow one and a busy database. The log and <c>/admin/health</c> are where
/// <see cref="DatumShiftNotices"/> already tells the operator what a client cannot act on, and this uses
/// the same two channels for the same reason.
/// </para>
/// <para>
/// <b>Read from the describe, not asked per tile.</b> <c>PostGisFeatureSource.DescribeAsync</c> reads the
/// catalogue's indexes in the statement it already runs (<c>LayerDescription.SpatiallyIndexed</c>), and the
/// describe is remembered for <c>ServiceContexts.Lifetime</c> — so the question costs one subquery per
/// thirty seconds per layer, inside the budget every describe takes, and nothing on a tile.
/// </para>
/// <para>
/// <b>Once per layer for the life of the process.</b> An index created later is not noticed here until the
/// next start, which is the cheap direction to be wrong in: the list says a table was unindexed when this
/// server first tiled it, and <c>GET /admin/services/{name}/cache</c> reads the current answer per layer.
/// </para>
/// </remarks>
internal sealed class UnindexedLayerNotices
{
    /// <summary>How many layers are remembered — the catalogue bounds it; this bounds a runaway catalogue.</summary>
    public const int Ceiling = 1024;

    private readonly ConcurrentDictionary<Guid, string> _seen = new();

    /// <summary>Whether the ceiling was reached and notices are being dropped.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Records a layer tiled with no spatial index, and logs the first time.</summary>
    /// <param name="layerId">The layer.</param>
    /// <param name="layerName">Its name, for the operator to read.</param>
    /// <param name="log">Where the first sighting goes.</param>
    public void Note(Guid layerId, string layerName, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (_seen.ContainsKey(layerId))
        {
            return;
        }

        if (_seen.Count >= Ceiling)
        {
            Truncated = true;
            return;
        }

        if (_seen.TryAdd(layerId, layerName))
        {
            Log.TiledWithoutSpatialIndex(log, layerName);
        }
    }

    /// <summary>What <c>/admin/health</c> reports, in name order so it reads the same twice.</summary>
    /// <returns>The layer names.</returns>
    public IReadOnlyList<string> Report() =>
        [.. _seen.Values.OrderBy(name => name, StringComparer.Ordinal)];
}
