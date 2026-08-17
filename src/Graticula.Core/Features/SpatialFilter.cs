using System;
using Graticula.Geometries;

namespace Graticula.Features;

/// <summary>
/// How a candidate feature must relate to a filter geometry.
/// </summary>
/// <remarks>
/// <b>ArcGIS's nine, named as ours.</b> The wire spellings
/// (<c>esriSpatialRelIntersects</c> and the rest) belong to the compatibility
/// layer, not here — [ADR-018](../../../docs/adr/ADR-018-authorization-and-roles.md)
/// §6 makes the same separation for privileges, and for the same reason: a third
/// party's vocabulary in the middle of the domain means every future surface
/// speaks it too.
/// </remarks>
public enum SpatialRelation
{
    /// <summary>They share any point at all. The default, and the cheapest.</summary>
    Intersects,

    /// <summary>The feature contains the filter geometry entirely.</summary>
    Contains,

    /// <summary>Their interiors meet in something of lower dimension than both.</summary>
    Crosses,

    /// <summary>
    /// Their bounding boxes overlap, whatever the shapes do.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately coarser than <see cref="Intersects"/>, and that is the
    /// point.</b> It is the index test on its own: cheap, and it returns
    /// features that do not actually touch the filter. A client asking for it is
    /// asking for speed over precision.
    /// </remarks>
    EnvelopeIntersects,

    /// <summary>
    /// The spatial index's own intersection test.
    /// </summary>
    /// <remarks>
    /// In PostGIS this is exactly <see cref="EnvelopeIntersects"/> — the
    /// <c>&amp;&amp;</c> operator is the index test — so the two are the same
    /// query here. Kept as a separate value because a client that asked for one
    /// should not be told it asked for the other, and because a provider whose
    /// index is not a bounding-box index would make them differ.
    /// </remarks>
    IndexIntersects,

    /// <summary>Same dimension, they overlap, and neither contains the other.</summary>
    Overlaps,

    /// <summary>They meet only at boundaries; their interiors do not.</summary>
    Touches,

    /// <summary>The feature lies entirely inside the filter geometry.</summary>
    Within,

    /// <summary>Whatever the DE-9IM pattern in the filter says.</summary>
    Relate,
}

/// <summary>
/// A spatial restriction: a geometry, how to compare against it, and how far.
/// </summary>
/// <param name="Geometry">The filter geometry, already in the layer's reference.</param>
/// <param name="Relation">How a feature must relate to it.</param>
/// <param name="RelatePattern">
/// A DE-9IM pattern, required by <see cref="SpatialRelation.Relate"/> and
/// meaningless otherwise.
/// </param>
/// <param name="Distance">
/// A buffer around the filter geometry, in the layer's units, or zero.
/// </param>
/// <remarks>
/// <para>
/// <b>The geometry arrives already projected into the layer's reference.</b>
/// Comparing a filter in one coordinate system against data in another is the
/// defect that made every 4326 tile silently empty (Q-96), and it is silent
/// again here: the boxes simply never meet and the answer is zero features with
/// no error. The conversion belongs at the edge, once, where the client's
/// <c>inSR</c> is known.
/// </para>
/// <para>
/// <b>Distance is in the layer's units, not the client's.</b> ArcGIS's
/// <c>units</c> parameter names feet, miles, kilometres and three more; turning
/// those into the layer's own unit is the compatibility layer's job, so that a
/// provider never has to know what <c>esriSRUnit_USNauticalMile</c> is.
/// </para>
/// </remarks>
public sealed record SpatialFilter(
    Geometry Geometry,
    SpatialRelation Relation = SpatialRelation.Intersects,
    string? RelatePattern = null,
    double Distance = 0)
{
    /// <summary>Throws if the combination is one no provider can execute.</summary>
    /// <returns>Itself, so it can be validated inline.</returns>
    public SpatialFilter Validated()
    {
        ArgumentNullException.ThrowIfNull(Geometry);

        if (Relation == SpatialRelation.Relate && string.IsNullOrWhiteSpace(RelatePattern))
        {
            throw new ArgumentException(
                "A Relate filter needs a DE-9IM pattern. Without one there is no relation to "
                + "test, and defaulting to Intersects would answer a different question than the "
                + "one asked.");
        }

        if (Distance < 0)
        {
            throw new ArgumentException("A buffer distance cannot be negative.");
        }

        return this;
    }
}

/// <summary>What to compute over a group of features.</summary>
public enum StatisticKind
{
    /// <summary>How many rows, ignoring nulls in the field.</summary>
    Count,

    /// <summary>Their total.</summary>
    Sum,

    /// <summary>The smallest.</summary>
    Min,

    /// <summary>The largest.</summary>
    Max,

    /// <summary>Their mean.</summary>
    Avg,

    /// <summary>Sample standard deviation.</summary>
    StdDev,

    /// <summary>Sample variance.</summary>
    Var,
}

/// <summary>One requested statistic.</summary>
/// <param name="Kind">What to compute.</param>
/// <param name="Field">Which column to compute it over.</param>
/// <param name="OutName">What to call the result.</param>
/// <remarks>
/// <b><paramref name="OutName"/> is the caller's, and it reaches SQL as an
/// identifier.</b> It is checked against the same rule a column name is before
/// it gets here — ADR-008 §4.6's two-step — because unlike a value it cannot be
/// bound as a parameter.
/// </remarks>
public readonly record struct StatisticRequest(StatisticKind Kind, string Field, string OutName);
