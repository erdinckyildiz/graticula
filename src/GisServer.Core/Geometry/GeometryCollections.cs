using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GisServer.Geometries;

/// <summary>Shared behaviour for the homogeneous multi-part geometries.</summary>
/// <typeparam name="TPart">The part type.</typeparam>
/// <remarks>
/// Generic so that a <see cref="MultiPolygon"/>'s parts are typed as
/// <see cref="Polygon"/> rather than <see cref="Geometry"/>, and callers do not
/// cast. The base is closed to this assembly along with the rest of the
/// hierarchy.
/// </remarks>
public abstract class MultiGeometry<TPart> : Geometry
    where TPart : Geometry
{
    private protected MultiGeometry(IReadOnlyList<TPart>? parts, string kindName)
    {
        if (parts is null || parts.Count == 0)
        {
            Parts = new ReadOnlyCollection<TPart>(Array.Empty<TPart>());
            KindName = kindName;
            return;
        }

        TPart[] copy = new TPart[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            copy[i] = parts[i] ?? throw new ArgumentException($"Part {i} is null.", nameof(parts));

            if (copy[i].IsEmpty)
            {
                // An empty part is a silent way to end up with a non-empty
                // geometry that draws nothing, and it breaks the rule that
                // IsEmpty means "nothing here".
                throw new ArgumentException($"Part {i} is empty.", nameof(parts));
            }
        }

        Parts = new ReadOnlyCollection<TPart>(copy);
        KindName = kindName;
    }

    /// <summary>The parts, in order.</summary>
    public IReadOnlyList<TPart> Parts { get; }

    private string KindName { get; }

    /// <inheritdoc/>
    public override bool IsEmpty => Parts.Count == 0;

    /// <inheritdoc/>
    public override int CoordinateCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Parts.Count; i++)
            {
                count += Parts[i].CoordinateCount;
            }

            return count;
        }
    }

    /// <inheritdoc/>
    protected override Envelope ComputeEnvelope()
    {
        Envelope envelope = Envelope.Empty;
        for (int i = 0; i < Parts.Count; i++)
        {
            envelope = envelope.Union(Parts[i].Envelope);
        }

        return envelope;
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{KindName} [{Parts.Count} parts, {CoordinateCount} coordinates]");
}

/// <summary>An unordered set of positions.</summary>
public sealed class MultiPoint : MultiGeometry<Point>
{
    /// <summary>An empty multi-point.</summary>
    public static MultiPoint Empty { get; } = new(null);

    /// <summary>Creates a multi-point.</summary>
    public MultiPoint(IReadOnlyList<Point>? parts)
        : base(parts, "MULTIPOINT")
    {
    }

    /// <inheritdoc/>
    public override GeometryKind Kind => GeometryKind.MultiPoint;
}

/// <summary>One or more <see cref="LineString"/> parts.</summary>
/// <remarks>
/// ArcGIS folds this and <see cref="LineString"/> together as <c>Polyline</c>.
/// The fold happens where ArcGIS JSON is written.
/// </remarks>
public sealed class MultiLineString : MultiGeometry<LineString>
{
    /// <summary>An empty multi-line-string.</summary>
    public static MultiLineString Empty { get; } = new(null);

    /// <summary>Creates a multi-line-string.</summary>
    public MultiLineString(IReadOnlyList<LineString>? parts)
        : base(parts, "MULTILINESTRING")
    {
    }

    /// <inheritdoc/>
    public override GeometryKind Kind => GeometryKind.MultiLineString;
}

/// <summary>One or more <see cref="Polygon"/> parts.</summary>
/// <remarks>
/// ArcGIS folds this and <see cref="Polygon"/> together as <c>Polygon</c>, using
/// ring winding order to recover the part structure. That recovery happens at
/// the ArcGIS edge; here the parts are explicit.
/// </remarks>
public sealed class MultiPolygon : MultiGeometry<Polygon>
{
    /// <summary>An empty multi-polygon.</summary>
    public static MultiPolygon Empty { get; } = new(null);

    /// <summary>Creates a multi-polygon.</summary>
    public MultiPolygon(IReadOnlyList<Polygon>? parts)
        : base(parts, "MULTIPOLYGON")
    {
    }

    /// <inheritdoc/>
    public override GeometryKind Kind => GeometryKind.MultiPolygon;
}
