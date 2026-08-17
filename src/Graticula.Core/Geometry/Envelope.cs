using System;
using System.Globalization;

namespace Graticula.Geometries;

/// <summary>
/// An axis-aligned bounding rectangle.
/// </summary>
/// <remarks>
/// <para>
/// Not a <see cref="Geometry"/>. Simple Features treats a bounding box as a
/// property rather than a type, and keeping it out of the hierarchy means
/// nothing has to ask <em>is this geometry actually a rectangle</em>. ArcGIS does
/// expose <c>esriGeometryEnvelope</c> as a geometry; that is handled where ArcGIS
/// JSON is read and written, not here.
/// </para>
/// <para>
/// It earns its place on the hot path. <c>benchmarks/mvt-generation</c> finding 2
/// showed that in a dense tile most features are entirely inside the tile, where
/// the correct answer is an envelope comparison and no clipping at all — that
/// trivial-accept is what made our clipper 63× faster than general overlay.
/// </para>
/// </remarks>
public readonly struct Envelope : IEquatable<Envelope>
{
    private readonly bool _hasValue;

    /// <summary>An envelope containing nothing. Union with it is the identity.</summary>
    public static Envelope Empty => default;

    /// <summary>Creates an envelope, ordering the bounds if they are reversed.</summary>
    public Envelope(double minX, double minY, double maxX, double maxY)
    {
        MinX = Math.Min(minX, maxX);
        MinY = Math.Min(minY, maxY);
        MaxX = Math.Max(minX, maxX);
        MaxY = Math.Max(minY, maxY);
        _hasValue = true;
    }

    /// <summary>Lower x bound. Zero when <see cref="IsEmpty"/>.</summary>
    public double MinX { get; }

    /// <summary>Lower y bound. Zero when <see cref="IsEmpty"/>.</summary>
    public double MinY { get; }

    /// <summary>Upper x bound. Zero when <see cref="IsEmpty"/>.</summary>
    public double MaxX { get; }

    /// <summary>Upper y bound. Zero when <see cref="IsEmpty"/>.</summary>
    public double MaxY { get; }

    /// <summary><see langword="true"/> when this envelope bounds nothing.</summary>
    public bool IsEmpty => !_hasValue;

    /// <summary>Extent along x. Zero for a vertical line or a point.</summary>
    public double Width => _hasValue ? MaxX - MinX : 0;

    /// <summary>Extent along y. Zero for a horizontal line or a point.</summary>
    public double Height => _hasValue ? MaxY - MinY : 0;

    /// <summary>The bounds of a coordinate sequence. Empty for an empty sequence.</summary>
    public static Envelope Of(XySequence coordinates)
    {
        if (coordinates.IsEmpty)
        {
            return Empty;
        }

        ReadOnlySpan<double> xy = coordinates.AsSpan();
        double minX = xy[0], maxX = xy[0], minY = xy[1], maxY = xy[1];

        for (int i = 2; i < xy.Length; i += 2)
        {
            double x = xy[i];
            double y = xy[i + 1];
            if (x < minX) { minX = x; }
            if (x > maxX) { maxX = x; }
            if (y < minY) { minY = y; }
            if (y > maxY) { maxY = y; }
        }

        return new Envelope(minX, minY, maxX, maxY);
    }

    /// <summary>The smallest envelope containing both. Empty operands are ignored.</summary>
    public Envelope Union(Envelope other)
    {
        if (IsEmpty) { return other; }
        if (other.IsEmpty) { return this; }

        return new Envelope(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY));
    }

    /// <summary>
    /// <see langword="true"/> when the two envelopes share any point, edges
    /// included. An empty envelope intersects nothing.
    /// </summary>
    public bool Intersects(Envelope other) =>
        _hasValue && other._hasValue &&
        other.MinX <= MaxX && other.MaxX >= MinX &&
        other.MinY <= MaxY && other.MaxY >= MinY;

    /// <summary>
    /// <see langword="true"/> when <paramref name="other"/> lies wholly within
    /// this envelope, edges included. **This is the trivial-accept test** that
    /// lets the tile path skip clipping entirely for features already inside.
    /// </summary>
    public bool Contains(Envelope other) =>
        _hasValue && other._hasValue &&
        other.MinX >= MinX && other.MaxX <= MaxX &&
        other.MinY >= MinY && other.MaxY <= MaxY;

    /// <summary>Expands by <paramref name="distance"/> on all four sides.</summary>
    public Envelope Expand(double distance) =>
        IsEmpty ? Empty : new Envelope(MinX - distance, MinY - distance, MaxX + distance, MaxY + distance);

    /// <inheritdoc/>
    public bool Equals(Envelope other) =>
        _hasValue == other._hasValue &&
        MinX.Equals(other.MinX) && MinY.Equals(other.MinY) &&
        MaxX.Equals(other.MaxX) && MaxY.Equals(other.MaxY);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Envelope other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_hasValue, MinX, MinY, MaxX, MaxY);

    /// <summary>Value equality.</summary>
    public static bool operator ==(Envelope left, Envelope right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(Envelope left, Envelope right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => IsEmpty
        ? "Envelope.Empty"
        : string.Create(CultureInfo.InvariantCulture, $"Envelope[{MinX} {MinY}, {MaxX} {MaxY}]");
}
