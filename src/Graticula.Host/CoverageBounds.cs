using System.Globalization;
using Graticula.Coverages;

namespace Graticula.Host;

/// <summary>
/// The largest image a file may declare when it comes into the catalogue — owner decision 2026-10-13, ADR-009
/// condition 1, the bomb half.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the tags the reader already parsed, before anything is catalogued.</b> Opening a TIFF reads its tags
/// and allocates nothing from them, so until this a file declaring impossible dimensions was catalogued happily and
/// refused later, once per request, by the read path's window bound. That ordering was an accident rather than a
/// control; this is the control.
/// </para>
/// <para>
/// <b>Files, not mosaics.</b> It is applied where a file is registered in place or uploaded — each image of a
/// mosaic as it arrives — and not to the virtual join of them, which may legitimately be wider than any one file:
/// a country at ten centimetres is fifteen million pixels across. The isolation half of the condition, a parser in
/// a process of its own, is not this.
/// </para>
/// </remarks>
internal static class CoverageBounds
{
    /// <summary>A side of 2^20 pixels.</summary>
    public const int DefaultMaximumSide = 1 << 20;

    /// <summary>2^36 pixels in all, about 68.7 billion.</summary>
    public const long DefaultMaximumPixels = 1L << 36;

    /// <summary>Why a file declaring this image is refused, or null when it fits.</summary>
    /// <param name="name">The file, as the person who sent it calls it.</param>
    /// <param name="info">What its tags declare.</param>
    /// <param name="settings">The bounds.</param>
    /// <returns>The sentence, or null.</returns>
    public static string? Refusal(string name, CoverageInfo info, HostSettings settings) =>
        Refusal(name, info.Width, info.Height, settings.RasterMaximumSide, settings.RasterMaximumPixels);

    /// <summary>Why a file declaring <paramref name="width"/> by <paramref name="height"/> is refused, or null.</summary>
    /// <param name="name">The file.</param>
    /// <param name="width">Pixels across, as declared.</param>
    /// <param name="height">Pixels down, as declared.</param>
    /// <param name="maximumSide">The bound on either side.</param>
    /// <param name="maximumPixels">The bound on both together.</param>
    /// <returns>The sentence, or null.</returns>
    public static string? Refusal(string name, int width, int height, int maximumSide, long maximumPixels)
    {
        long pixels = (long)width * height;

        if (width <= maximumSide && height <= maximumSide && pixels <= maximumPixels)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name} declares an image of {width:N0} × {height:N0} pixels, and this server registers "
            + $"one of at most {maximumSide:N0} pixels a side and {maximumPixels:N0} in all. A file that declares "
            + $"far more than it can hold is the shape of a decompression bomb; an image really this large is let in "
            + $"by raising Graticula:RasterMaximumSide and Graticula:RasterMaximumPixels.");
    }
}
