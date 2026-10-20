using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Tiles;

/// <summary>
/// Produces an encoded vector tile for one layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, <c>PostGisTileSource</c>, over the datastore — ADR-206.</b> [ADR-021] decided that
/// tiles are encoded by PostGIS. From 2026-09-13 to 2026-10-20 a second one read rows from DuckDB or Oracle and
/// handed them to a PostGIS encoder over a port (<c>IMvtEncoder</c>); ADR-206 took tiles off every registered source
/// and both went. This interface stays because the build-vs-adopt policy forbids a provider type in a Tier 1
/// signature: without it the tile endpoint would take an <c>NpgsqlDataSource</c>.
/// </para>
/// <para>
/// <b>It returns bytes, not geometry.</b> That is the whole of ADR-021 in one
/// signature: the encode step happens on the far side of this interface and
/// nothing above it knows what MVT looks like.
/// </para>
/// </remarks>
public interface ITileSource
{
    /// <summary>
    /// Builds one tile.
    /// </summary>
    /// <param name="address">Which tile.</param>
    /// <param name="layerName">The name to give the layer inside the tile.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The encoded tile, or an empty array where the tile has no features.
    /// </returns>
    /// <remarks>
    /// <b>Empty is a result, not an absence.</b> A tile with nothing in it is a
    /// correct answer that a client caches and stops asking for; a 404 is a
    /// failure it may retry. Most of a pyramid is empty, so getting this the
    /// wrong way round turns the ocean into a retry storm.
    /// </remarks>
    Task<byte[]> BuildAsync(
        TileAddress address, string layerName, CancellationToken cancellationToken);
}
