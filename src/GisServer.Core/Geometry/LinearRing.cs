using System;

namespace GisServer.Geometries;

/// <summary>A closed <see cref="LineString"/> bounding an area.</summary>
/// <remarks>
/// <para>
/// A separate type so that <see cref="Polygon"/>'s signature says what it needs
/// rather than accepting any line and hoping. Closure is checked once, here, and
/// never re-checked downstream.
/// </para>
/// <para>
/// <b>Validity is not checked.</b> Self-intersection, ring nesting and
/// orientation are topology, which <c>ADR-003</c> §6a places in tier 3 with the
/// adopted engine. This type enforces the two things that are cheap and
/// structural — closed, and enough positions to bound an area.
/// </para>
/// </remarks>
public sealed class LinearRing : LineString
{
    /// <summary>The minimum positions in a closed ring: three corners plus the repeat.</summary>
    public const int MinimumCoordinates = 4;

    /// <summary>An empty ring.</summary>
    public static new LinearRing Empty { get; } = new(XySequence.Empty);

    /// <summary>Creates a ring.</summary>
    /// <exception cref="ArgumentException">
    /// The sequence is neither empty nor a closed ring of at least
    /// <see cref="MinimumCoordinates"/> positions.
    /// </exception>
    public LinearRing(XySequence coordinates)
        : base(Validate(coordinates))
    {
    }

    private static XySequence Validate(XySequence coordinates)
    {
        if (coordinates.IsEmpty)
        {
            return coordinates;
        }

        if (coordinates.Count < MinimumCoordinates)
        {
            throw new ArgumentException(
                $"A LinearRing needs zero or at least {MinimumCoordinates} coordinates; got {coordinates.Count}.",
                nameof(coordinates));
        }

        int last = coordinates.Count - 1;
        if (!coordinates.X(0).Equals(coordinates.X(last)) || !coordinates.Y(0).Equals(coordinates.Y(last)))
        {
            throw new ArgumentException(
                "A LinearRing must be closed: the first and last coordinates must be identical.",
                nameof(coordinates));
        }

        return coordinates;
    }

    /// <summary>
    /// Twice the signed area, by the shoelace formula. Positive is
    /// counter-clockwise in a y-up coordinate system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Doubled because halving it would cost a division and every caller either
    /// compares it to zero or wants the sign. Orientation drives ring
    /// classification when converting to and from ArcGIS geometry, where winding
    /// order carries the shell-versus-hole distinction that Simple Features
    /// states explicitly.
    /// </para>
    /// <para>
    /// <b>This is the trapezoid form, and it is not the one to measure with.</b>
    /// <c>(x₂ − x₁)(y₂ + y₁)</c> subtracts the two large x values before
    /// multiplying, so it avoids most of the cancellation that made the plain
    /// shoelace wrong by 1.6 × 10⁻⁵ on real Web Mercator polygons (D-35) — but
    /// it still multiplies a small difference by a <em>sum</em> of two
    /// coordinates near 10⁷, so it is accurate to roughly 10⁻¹¹ rather than to
    /// machine precision. That is far more than a sign needs and less than an
    /// area deserves. <b>Area is
    /// <see cref="GeometryMeasures.Area(Geometry)"/></b>, which subtracts a
    /// local origin and agrees with PostGIS to better than 10⁻⁸.
    /// </para>
    /// </remarks>
    public double SignedArea2()
    {
        if (Coordinates.Count < MinimumCoordinates)
        {
            return 0;
        }

        ReadOnlySpan<double> xy = Coordinates.AsSpan();
        double sum = 0;

        for (int i = 0, j = xy.Length - 2; i < xy.Length; j = i, i += 2)
        {
            sum += (xy[j] - xy[i]) * (xy[j + 1] + xy[i + 1]);
        }

        return sum;
    }

    /// <summary>
    /// <see langword="true"/> when the ring winds counter-clockwise in a y-up
    /// coordinate system. An empty or degenerate ring is not.
    /// </summary>
    /// <remarks>
    /// Pinned by test, because winding is where this kind of code goes wrong
    /// quietly. The benchmark's MVT encoder already carried a note that tile
    /// space is y-down and inverts the sense — a sign error here would produce
    /// inside-out polygons that render as holes.
    /// </remarks>
    public bool IsCounterClockwise => SignedArea2() > 0;
}
