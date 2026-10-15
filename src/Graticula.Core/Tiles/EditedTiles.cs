using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;

namespace Graticula.Tiles;

/// <summary>
/// Where an edit's features were and are, in a tiling scheme's own reference — read from the store before and after the
/// edit, so a cache drops only the tiles they touch (ADR-101 condition 4, owner decision 2026-10-15).
/// </summary>
public interface IEditedExtent
{
    /// <summary>The extent of these features in a reference, or null when it cannot be had.</summary>
    /// <param name="identities">The features' object ids.</param>
    /// <param name="srid">The reference to give it in — a tiling scheme's.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The extent; <see cref="Envelope.Empty"/> when none of them has a geometry; null when it is unknown.</returns>
    Task<Envelope?> ExtentAsync(IReadOnlyCollection<long> identities, int srid, CancellationToken cancellationToken);
}

/// <summary>Which tiles of a scheme an extent reaches — ADR-101 condition 4.</summary>
public static class EditedTiles
{
    /// <summary>
    /// Whether a tile is one an edit within <paramref name="extent"/> may have changed.
    /// </summary>
    /// <remarks>
    /// <b>A tile's own box, grown by one tile on every side.</b> A tile carries the features within its buffer of its
    /// edge, and a generalised level draws a feature over neighbours it does not touch; one tile's span is more than
    /// either, so a tile this says no to is one the edit could not have changed. The box is the scheme's own
    /// (<see cref="VectorTileScheme.Envelope"/>), so where a tile is is decided in one place.
    /// </remarks>
    /// <param name="scheme">The tiling scheme.</param>
    /// <param name="extent">The edit's extent, in <see cref="VectorTileScheme.Srid"/>.</param>
    /// <returns>The test.</returns>
    public static Func<TileAddress, bool> Reached(VectorTileScheme scheme, Envelope extent)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        return address =>
        {
            // An address this scheme does not have is not one it can say no about.
            if (scheme.Rejection(address) is not null)
            {
                return true;
            }

            Envelope tile = scheme.Envelope(address);
            double pad = tile.Width;

            return tile.MinX - pad <= extent.MaxX && tile.MaxX + pad >= extent.MinX
                && tile.MinY - pad <= extent.MaxY && tile.MaxY + pad >= extent.MinY;
        };
    }
}
