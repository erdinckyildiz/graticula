using System.Globalization;

namespace Graticula.Geometries;

/// <summary>A single position.</summary>
/// <remarks>
/// Holds two doubles directly rather than an <see cref="XySequence"/>. A point
/// backed by a one-element buffer would allocate an array to store sixteen bytes,
/// and points arrive in bulk — a city's worth of address points is millions of
/// them.
/// </remarks>
public class Point : Geometry
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

    /// <summary>
    /// A position with an elevation, a measure, or both — ADR-077.
    /// </summary>
    /// <remarks>
    /// <b>A factory rather than a constructor, so a flat point costs nothing for the ability.</b> The first
    /// version kept one array reference on every point; measured over a million points decoded from WKB
    /// and written as ArcGIS JSON it was eight bytes each, 83.9 MB to 91.6 MB — nine per cent more
    /// allocation on a point layer for a feature no surface serves yet, where allocation rather than CPU
    /// is this server's binding constraint (A-037). A point that carries Z or M is an instance of a
    /// private subclass instead, and a flat one is the same object it always was.
    /// </remarks>
    /// <param name="x">The x ordinate.</param>
    /// <param name="y">The y ordinate.</param>
    /// <param name="z">The elevation, or null.</param>
    /// <param name="m">The measure, or null.</param>
    /// <returns>The point.</returns>
    public static Point Create(double x, double y, double? z, double? m) =>
        z is null && m is null ? new Point(x, y) : new WithOrdinates(x, y, z, m);

    /// <summary>Which ordinates beyond x and y this point carries.</summary>
    public virtual GeometryOrdinates Ordinates => GeometryOrdinates.None;

    /// <summary>The elevation, or null when this point has none.</summary>
    public virtual double? Z => null;

    /// <summary>The measure, or null when this point has none.</summary>
    public virtual double? M => null;

    private sealed class WithOrdinates(double x, double y, double? z, double? m) : Point(x, y)
    {
        public override GeometryOrdinates Ordinates { get; } =
            (z is null ? GeometryOrdinates.None : GeometryOrdinates.Z)
            | (m is null ? GeometryOrdinates.None : GeometryOrdinates.M);

        public override double? Z { get; } = z;

        public override double? M { get; } = m;
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
