using System;
using Graticula.Coverages;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Which pixels of a coverage answer a request, and where they land.
/// </summary>
/// <param name="Overview">
/// Zero for the full-resolution image; higher indexes <see cref="CoverageInfo.Overviews"/>.
/// </param>
/// <param name="X">Left edge of the window, in that resolution's pixels.</param>
/// <param name="Y">Top edge of the window, in that resolution's pixels.</param>
/// <param name="Width">Window width, in that resolution's pixels.</param>
/// <param name="Height">Window height, in that resolution's pixels.</param>
/// <param name="Destination">Where that window belongs on the canvas.</param>
public readonly record struct CoveragePlan(
    int Overview,
    int X,
    int Y,
    int Width,
    int Height,
    PixelBox Destination);

/// <summary>
/// Works out what to read and where to put it. Tier 1, and the choice is the point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Choosing an overview is a cartographic decision, which is why
/// <see cref="ICoverageReader"/> takes an index rather than a resolution</b>
/// ([ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4).
/// Reading full resolution for a request drawn at a hundredth of it costs ten thousand
/// times the pixels for the same picture; reading too coarse a level draws a blurred
/// one. This is the same judgement <see cref="MapScale"/> already makes for vector
/// layers, and it lives beside it for that reason.
/// </para>
/// <para>
/// <b>The rule is: the coarsest level that is still at least as detailed as the
/// request.</b> Coarser than that would magnify, and magnified imagery reads as blur
/// the viewer cannot tell from the data being low-resolution. Finer than that is
/// correct and wasteful — the resampling on the way down averages real pixels, which
/// is what makes a reduced image look right, so the error is only in what it cost.
/// </para>
/// </remarks>
public static class CoveragePlanner
{
    /// <summary>
    /// Plans a read, or answers null when the request and the coverage do not overlap.
    /// </summary>
    /// <param name="info">The coverage.</param>
    /// <param name="extent">The requested ground extent, in the coverage's own reference.</param>
    /// <param name="width">The requested image width in pixels.</param>
    /// <param name="height">The requested image height in pixels.</param>
    /// <returns>What to read and where to draw it, or null when nothing overlaps.</returns>
    /// <remarks>
    /// <b>Null rather than an empty plan, because "no overlap" is an answer.</b> A
    /// request whose extent misses the coverage entirely gets a valid image with
    /// nothing in it, which is what ADR-041 condition 5 asks of the vector faces and is
    /// the same rule here. Making it an error would refuse a client panning off the
    /// edge of its own data.
    /// </remarks>
    public static CoveragePlan? Plan(CoverageInfo info, Envelope extent, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        double minX = Math.Max(extent.MinX, info.Extent.MinX);
        double minY = Math.Max(extent.MinY, info.Extent.MinY);
        double maxX = Math.Min(extent.MaxX, info.Extent.MaxX);
        double maxY = Math.Min(extent.MaxY, info.Extent.MaxY);

        if (maxX <= minX || maxY <= minY)
        {
            return null;
        }

        int overview = Choose(info, extent, width, height);

        (int levelWidth, int levelHeight) = Size(info, overview);

        // Ground per pixel at the chosen level. Derived from the level's own size
        // rather than from 2^overview, because a pyramid's levels round rather than
        // halve exactly and an odd image's third level is not its width over eight.
        (double perPixelX, double perPixelY) = PixelSize(info, overview);

        int left = (int)Math.Floor((minX - info.Extent.MinX) / perPixelX);
        int top = (int)Math.Floor((info.Extent.MaxY - maxY) / perPixelY);
        int right = (int)Math.Ceiling((maxX - info.Extent.MinX) / perPixelX);
        int bottom = (int)Math.Ceiling((info.Extent.MaxY - minY) / perPixelY);

        left = Math.Clamp(left, 0, levelWidth - 1);
        top = Math.Clamp(top, 0, levelHeight - 1);
        right = Math.Clamp(right, left + 1, levelWidth);
        bottom = Math.Clamp(bottom, top + 1, levelHeight);

        // <b>The destination comes from the window that was actually read, not from the
        // overlap.</b> The window was rounded outwards to whole pixels, so it covers a
        // little more ground than the overlap does; drawing it into the overlap's box
        // would squeeze it and shift everything by up to a pixel. Recomputing the
        // ground the window really spans and mapping that onto the canvas is what keeps
        // a coverage registered with the vector layers drawn over it.
        double readMinX = info.Extent.MinX + (left * perPixelX);
        double readMaxX = info.Extent.MinX + (right * perPixelX);
        double readMaxY = info.Extent.MaxY - (top * perPixelY);
        double readMinY = info.Extent.MaxY - (bottom * perPixelY);

        double scaleX = width / (extent.MaxX - extent.MinX);
        double scaleY = height / (extent.MaxY - extent.MinY);

        PixelBox destination = new(
            (readMinX - extent.MinX) * scaleX,
            (extent.MaxY - readMaxY) * scaleY,
            (readMaxX - extent.MinX) * scaleX,
            (extent.MaxY - readMinY) * scaleY);

        return new CoveragePlan(overview, left, top, right - left, bottom - top, destination);
    }

    /// <summary>The coarsest level still at least as detailed as the request.</summary>
    private static int Choose(CoverageInfo info, Envelope extent, int width, int height)
    {
        double wantedX = (extent.MaxX - extent.MinX) / width;
        double wantedY = (extent.MaxY - extent.MinY) / height;
        double wanted = Math.Min(wantedX, wantedY);

        int best = 0;

        for (int level = 0; level <= info.Overviews.Count; level++)
        {
            (int levelWidth, int levelHeight) = Size(info, level);

            double perPixelX = (info.Extent.MaxX - info.Extent.MinX) / levelWidth;
            double perPixelY = (info.Extent.MaxY - info.Extent.MinY) / levelHeight;
            double coarsest = Math.Max(perPixelX, perPixelY);

            if (coarsest <= wanted)
            {
                best = level;
                continue;
            }

            break;
        }

        return best;
    }

    /// <summary>How much ground one pixel of an overview level covers.</summary>
    /// <param name="info">The coverage.</param>
    /// <param name="overview">Zero for full resolution; higher indexes the overviews.</param>
    /// <returns>Ground units per pixel, east-west and north-south.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
    /// <remarks>
    /// <b>Public because the caller of <see cref="Plan"/> needs the same number, and was
    /// working it out again by hand.</b> The planner divides the extent by the level's size to
    /// choose a read window; the code that draws the result divides the extent by the level's
    /// size to place it. Two copies of one calculation, and the copy in the drawing path had
    /// the level lookup written out longhand — so a change to how a level's size is found
    /// would have moved one and not the other, and the symptom would be a picture slightly
    /// out of place at one overview level only.
    /// </remarks>
    public static (double X, double Y) PixelSize(CoverageInfo info, int overview)
    {
        ArgumentNullException.ThrowIfNull(info);

        (int width, int height) = Size(info, overview);

        return (
            (info.Extent.MaxX - info.Extent.MinX) / width,
            (info.Extent.MaxY - info.Extent.MinY) / height);
    }

    private static (int Width, int Height) Size(CoverageInfo info, int overview) =>
        overview == 0
            ? (info.Width, info.Height)
            : (info.Overviews[overview - 1].Width, info.Overviews[overview - 1].Height);
}
