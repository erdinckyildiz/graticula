using System.Globalization;

namespace GisServer.Geometry;

/// <summary>A single position.</summary>
/// <remarks>
/// Holds two doubles directly rather than an <see cref="XySequence"/>. A point
/// backed by a one-element buffer would allocate an array to store sixteen bytes,
/// and points arrive in bulk — a city's worth of address points is millions of
/// them.
/// </remarks>
public sealed class Point : Geometry
{
    /// <summary>The empty point. Has no position.</summary>
    public static Point Empty { get; } = new();

    private Point()
    {
        IsEmpty = true;
    }

    /// <summary>Creates a point at the given position.</summary>
    public Point(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>The x ordinate. Zero when <see cref="IsEmpty"/>.</summary>
    public double X { get; }

    /// <summary>The y ordinate. Zero when <see cref="IsEmpty"/>.</summary>
    public double Y { get; }

    /// <inheritdoc/>
    public override GeometryKind Kind => GeometryKind.Point;

    /// <inheritdoc/>
    public override bool IsEmpty { get; }

    /// <inheritdoc/>
    public override int CoordinateCount => IsEmpty ? 0 : 1;

    /// <inheritdoc/>
    protected override Envelope ComputeEnvelope() =>
        IsEmpty ? Envelope.Empty : new Envelope(X, Y, X, Y);

    /// <inheritdoc/>
    public override string ToString() => IsEmpty
        ? "POINT EMPTY"
        : string.Create(CultureInfo.InvariantCulture, $"POINT ({X} {Y})");
}
