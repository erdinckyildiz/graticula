using System.Threading;
using System.Threading.Tasks;

namespace GisServer.Tiles;

/// <summary>
/// Produces an encoded vector tile for one layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The port exists even though there is exactly one implementation, and
/// exactly one is expected.</b> [ADR-021] decided that tiles are encoded by
/// PostGIS, so <c>PostGisTileSource</c> is not the first of several. The port is
/// here because the build-vs-adopt policy forbids a provider type appearing in a
/// Tier 1 signature: without it the tile endpoint would take an
/// <c>NpgsqlDataSource</c>, and the argument that most of the tiling pipeline is
/// still ours would stop being true at the first line of the handler.
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
