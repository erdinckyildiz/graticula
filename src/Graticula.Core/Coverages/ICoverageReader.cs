using System;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Coverages;

/// <summary>
/// A rectangle of samples, as read.
/// </summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Bands">How many bands are interleaved in <paramref name="Samples"/>.</param>
/// <param name="Samples">
/// The values, band-interleaved by pixel: <c>[x0b0, x0b1, x0b2, x1b0, …]</c>, row by
/// row from the top.
/// </param>
/// <remarks>
/// <para>
/// <b><c>double</c> for every sample kind, deliberately.</b> A reader that returned
/// bytes for byte imagery and floats for float imagery would push the storage type
/// across the tier boundary and make every caller switch on it — which is the
/// library's vocabulary leaking out under a different name. A double holds every
/// value the <see cref="SampleKind"/> list can produce without loss, and the cost is
/// memory in a buffer that is already bounded by the request's pixel ceiling.
/// </para>
/// <para>
/// <b>Band-interleaved by pixel rather than by plane</b>, because the consumer is a
/// renderer walking pixels in order, and the alternative would have it stride the
/// buffer three times for an RGB image.
/// </para>
/// </remarks>
public sealed record CoverageWindow(int Width, int Height, int Bands, double[] Samples)
{
    /// <summary>One sample, by position and band.</summary>
    /// <param name="x">Column, from the left.</param>
    /// <param name="y">Row, from the top.</param>
    /// <param name="band">Band index, from zero.</param>
    /// <returns>The value.</returns>
    public double At(int x, int y, int band) =>
        Samples[(((y * Width) + x) * Bands) + band];
}

/// <summary>
/// The port a raster format implements: tell me what this is, and give me those pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface is the tier boundary for raster</b>, and it is drawn where
/// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4 says:
/// decoding a TIFF, walking its tiles and overviews, decompressing and resampling are
/// Tier 2 and live behind here; which overview answers a request, what colour a value
/// becomes and how the result composites are Tier 1 and live above it. The
/// implementing library's vocabulary — directories, strips, photometric
/// interpretations, predictors — stops at this file.
/// </para>
/// <para>
/// <b>It is the same shape as <see cref="Graticula.Cartography.IMapCanvas"/> and for
/// the same reason.</b> That port takes resolved symbols and never style rules; this
/// one returns samples and never colours. Push a colour ramp across either boundary
/// and the boundary has stopped meaning anything.
/// </para>
/// <para>
/// <b>Two methods, and the second one takes an overview index rather than a
/// resolution.</b> Asking for *the best level for this scale* would be the reader
/// making a cartographic choice; asking for *level two* is the reader doing what it
/// is told. The choosing lives in Tier 1 beside the scale rules that already make the
/// equivalent decision for vector layers.
/// </para>
/// </remarks>
public interface ICoverageReader : IDisposable
{
    /// <summary>Everything knowable without reading pixels.</summary>
    CoverageInfo Info { get; }

    /// <summary>
    /// Reads a rectangle of samples from one resolution.
    /// </summary>
    /// <param name="overview">
    /// Zero for the full-resolution image; one and above index
    /// <see cref="CoverageInfo.Overviews"/>.
    /// </param>
    /// <param name="x">Left edge, in that resolution's pixels.</param>
    /// <param name="y">Top edge, in that resolution's pixels.</param>
    /// <param name="width">How many pixels across.</param>
    /// <param name="height">How many pixels down.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The samples.</returns>
    /// <remarks>
    /// <b>A window reaching past an edge is clamped rather than refused</b>, and the
    /// samples outside are the band's no-data value where it has one and zero where
    /// it does not. A caller asking for the pixels under a map request routinely
    /// straddles the edge, and making that an error would put the arithmetic in every
    /// caller instead of once here.
    /// </remarks>
    Task<CoverageWindow> ReadAsync(
        int overview,
        int x,
        int y,
        int width,
        int height,
        CancellationToken cancellationToken);
}

/// <summary>
/// Opens a coverage. Registered so that nothing above Tier 1 names a format.
/// </summary>
/// <remarks>
/// <b>A path, not a stream, and that is
/// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.3.</b>
/// Imagery is registered in place and never copied, so what the catalogue holds is a
/// reference to where the file lives. When that becomes an object-storage URL the
/// implementation behind here changes and this signature does not.
/// </remarks>
public interface ICoverageReaderFactory
{
    /// <summary>Opens a coverage for reading.</summary>
    /// <param name="path">Where it lives.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The reader, which the caller disposes.</returns>
    Task<ICoverageReader> OpenAsync(string path, CancellationToken cancellationToken);
}
