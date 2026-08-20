using System;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Map units to pixels, for one image.
/// </summary>
/// <remarks>
/// <para>
/// <b>The y axis flips and that is the whole reason this is a type.</b> Map
/// coordinates grow northward and image rows grow downward, so every single
/// coordinate in every geometry needs the same inversion. Written inline it is
/// correct in the first place somebody writes it and wrong in the fourth.
/// </para>
/// <para>
/// <b>The aspect ratio is not corrected.</b> WMS lets a client ask for an extent
/// and a pixel size that disagree, and the specification's answer is that the
/// server honours both — the map is stretched, and that is the client's choice.
/// Quietly adjusting the extent to fit would return a map of somewhere other than
/// what was asked for, which no client can detect.
/// </para>
/// </remarks>
public readonly struct PixelTransform : IEquatable<PixelTransform>
{
    private readonly double _scaleX;
    private readonly double _scaleY;

    /// <summary>Builds a transform.</summary>
    /// <param name="extent">The map extent, in the image's own CRS.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">The extent is empty or has no area.</exception>
    public PixelTransform(Envelope extent, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (extent.IsEmpty || extent.Width <= 0 || extent.Height <= 0)
        {
            throw new ArgumentException(
                "A map extent needs a positive width and height. An extent with no area is a "
                + "request for an image of nothing, and it produces a division by zero rather "
                + "than a blank map.",
                nameof(extent));
        }

        Extent = extent;
        Width = width;
        Height = height;

        _scaleX = width / extent.Width;
        _scaleY = height / extent.Height;
    }

    /// <summary>The map extent this image covers.</summary>
    public Envelope Extent { get; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Map units per pixel along x, which is the simplification tolerance.
    /// </summary>
    /// <remarks>
    /// <b>Along x rather than the smaller of the two.</b> A stretched image has two
    /// resolutions and simplifying to the coarser one would drop detail the taller
    /// axis can show. Using x is the convention every renderer uses and the error
    /// it admits is bounded by the stretch the client asked for.
    /// </remarks>
    public double UnitsPerPixel => Extent.Width / Width;

    /// <summary>A map x coordinate as a pixel x.</summary>
    /// <param name="mapX">The coordinate.</param>
    /// <returns>The pixel.</returns>
    public double X(double mapX) => (mapX - Extent.MinX) * _scaleX;

    /// <summary>A map y coordinate as a pixel y, inverted.</summary>
    /// <param name="mapY">The coordinate.</param>
    /// <returns>The pixel.</returns>
    public double Y(double mapY) => (Extent.MaxY - mapY) * _scaleY;

    /// <summary>A pixel x back to a map x, for identify.</summary>
    /// <param name="pixelX">The pixel.</param>
    /// <returns>The coordinate.</returns>
    public double MapX(double pixelX) => Extent.MinX + (pixelX / _scaleX);

    /// <summary>A pixel y back to a map y, for identify.</summary>
    /// <param name="pixelY">The pixel.</param>
    /// <returns>The coordinate.</returns>
    public double MapY(double pixelY) => Extent.MaxY - (pixelY / _scaleY);

    /// <summary>
    /// The extent grown by a margin in pixels, which is what a query must fetch.
    /// </summary>
    /// <remarks>
    /// <b>ADR-041 §5.3, and it is not a nicety.</b> A symbol whose centre is
    /// outside the extent still paints inside it, and a label anchored just off the
    /// edge still reaches in. Querying the bare extent draws a map with objects
    /// missing along every border — invisible in a single image and glaring the
    /// moment a client tiles its requests, which is how every WMS client works.
    /// </remarks>
    /// <param name="pixels">The margin, in pixels.</param>
    /// <returns>The extent to query.</returns>
    public Envelope Buffered(double pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixels);

        double x = pixels / _scaleX;
        double y = pixels / _scaleY;

        return new Envelope(
            Extent.MinX - x, Extent.MinY - y, Extent.MaxX + x, Extent.MaxY + y);
    }

    /// <inheritdoc/>
    public bool Equals(PixelTransform other) =>
        Extent.Equals(other.Extent) && Width == other.Width && Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PixelTransform other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Extent, Width, Height);

    /// <summary>Equality.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(PixelTransform left, PixelTransform right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(PixelTransform left, PixelTransform right) => !left.Equals(right);
}
