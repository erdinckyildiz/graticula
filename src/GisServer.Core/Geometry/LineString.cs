using System;
using System.Globalization;

namespace GisServer.Geometry;

/// <summary>A connected sequence of straight segments.</summary>
/// <remarks>
/// ArcGIS calls this a <c>Polyline</c> and does not distinguish one path from
/// many; see <see cref="GeometryKind"/> for why we keep the distinction and
/// collapse it at the API edge instead.
/// </remarks>
public class LineString : Geometry
{
    /// <summary>An empty line string.</summary>
    public static LineString Empty { get; } = new(XySequence.Empty);

    /// <summary>Creates a line string over the given coordinates.</summary>
    /// <exception cref="ArgumentException">
    /// Exactly one coordinate was supplied. A line needs two positions or none —
    /// a single-coordinate line is a modelling error rather than a degenerate
    /// case worth carrying.
    /// </exception>
    public LineString(XySequence coordinates)
    {
        if (coordinates.Count == 1)
        {
            throw new ArgumentException(
                "A LineString needs zero or two or more coordinates; got 1.",
                nameof(coordinates));
        }

        Coordinates = coordinates;
    }

    /// <summary>The positions along the line, in order.</summary>
    public XySequence Coordinates { get; }

    /// <inheritdoc/>
    public override GeometryKind Kind => GeometryKind.LineString;

    /// <inheritdoc/>
    public override bool IsEmpty => Coordinates.IsEmpty;

    /// <inheritdoc/>
    public override int CoordinateCount => Coordinates.Count;

    /// <summary>
    /// <see langword="true"/> when the first and last positions are identical.
    /// </summary>
    public bool IsClosed =>
        Coordinates.Count >= 2 &&
        Coordinates.X(0).Equals(Coordinates.X(Coordinates.Count - 1)) &&
        Coordinates.Y(0).Equals(Coordinates.Y(Coordinates.Count - 1));

    /// <inheritdoc/>
    protected override Envelope ComputeEnvelope() => Envelope.Of(Coordinates);

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{Kind.ToString().ToUpperInvariant()} [{CoordinateCount}]");
}
