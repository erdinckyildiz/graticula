using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GisServer.Geometry;

/// <summary>
/// An area bounded by one exterior ring, with zero or more interior rings
/// punching holes in it.
/// </summary>
/// <remarks>
/// <para>
/// The shell-versus-hole distinction is <b>explicit</b> here. ArcGIS stores a
/// polygon as a flat list of rings and infers the distinction from winding
/// order; that inference happens at the ArcGIS edge, not in the domain, so the
/// rest of the server never has to re-derive it. See <see cref="GeometryKind"/>.
/// </para>
/// <para>
/// Rings are ordinarily slices of one shared buffer
/// (<see cref="XySequence.Slice"/>), so a polygon with many holes is still one
/// allocation of coordinate data.
/// </para>
/// </remarks>
public sealed class Polygon : Geometry
{
    private static readonly ReadOnlyCollection<LinearRing> NoHoles =
        new(Array.Empty<LinearRing>());

    /// <summary>An empty polygon.</summary>
    public static Polygon Empty { get; } = new(LinearRing.Empty);

    /// <summary>Creates a polygon.</summary>
    /// <param name="shell">The exterior ring.</param>
    /// <param name="holes">
    /// Interior rings. A hole is not permitted when the shell is empty, because
    /// a hole in nothing has no meaning and would silently misrepresent the
    /// caller's intent.
    /// </param>
    public Polygon(LinearRing shell, IReadOnlyList<LinearRing>? holes = null)
    {
        ArgumentNullException.ThrowIfNull(shell);

        Shell = shell;

        if (holes is null || holes.Count == 0)
        {
            Holes = NoHoles;
            return;
        }

        if (shell.IsEmpty)
        {
            throw new ArgumentException(
                "A polygon with an empty shell cannot have holes.", nameof(holes));
        }

        LinearRing[] copy = new LinearRing[holes.Count];
        for (int i = 0; i < holes.Count; i++)
        {
            copy[i] = holes[i] ?? throw new ArgumentException(
                $"Hole {i} is null.", nameof(holes));

            if (copy[i].IsEmpty)
            {
                throw new ArgumentException($"Hole {i} is empty.", nameof(holes));
            }
        }

        Holes = new ReadOnlyCollection<LinearRing>(copy);
    }

    /// <summary>The exterior ring.</summary>
    public LinearRing Shell { get; }

    /// <summary>The interior rings. Empty when the polygon is solid.</summary>
    public IReadOnlyList<LinearRing> Holes { get; }

    /// <inheritdoc/>
    public override GeometryKind Kind => GeometryKind.Polygon;

    /// <inheritdoc/>
    public override bool IsEmpty => Shell.IsEmpty;

    /// <inheritdoc/>
    public override int CoordinateCount
    {
        get
        {
            int count = Shell.CoordinateCount;
            for (int i = 0; i < Holes.Count; i++)
            {
                count += Holes[i].CoordinateCount;
            }

            return count;
        }
    }

    /// <summary>
    /// The shell's bounds. Holes lie inside the shell by definition, so they
    /// cannot extend it — and a polygon whose hole escapes its shell is invalid,
    /// which is topology's problem rather than the envelope's.
    /// </summary>
    protected override Envelope ComputeEnvelope() => Shell.Envelope;

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"POLYGON [shell {Shell.CoordinateCount}, holes {Holes.Count}]");
}
