using System.Collections.Generic;
using Graticula.Features;

namespace Graticula.Api.Wfs;

/// <summary>
/// What a <c>fes:Filter</c> becomes: the three things a query can carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three slots rather than one tree, because that is what the engine has.</b>
/// <c>FeatureQuery</c> holds an attribute predicate, one spatial restriction and a
/// list of identities, and combines them with <c>and</c>. Filter Encoding is
/// richer than that — it can say <c>Or(PropertyIsEqualTo, Intersects)</c> — and
/// <see cref="FilterReader"/> refuses what it cannot carry rather than dropping a
/// clause. A filter half-applied returns features the caller excluded, silently,
/// which is the failure ADR-008 §2 names.
/// </para>
/// </remarks>
/// <param name="Predicate">The attribute half, or null.</param>
/// <param name="Spatial">The spatial half, or null.</param>
/// <param name="FilterSrid">
/// The reference the spatial filter's geometry is in, when the request named one
/// that is not the layer's. Null means it is already the layer's.
/// </param>
/// <param name="ResourceIds">Identities named by <c>fes:ResourceId</c>.</param>
public sealed record ParsedFilter(
    AttributePredicate? Predicate,
    SpatialFilter? Spatial,
    int? FilterSrid,
    IReadOnlyList<string> ResourceIds)
{
    /// <summary>A filter that restricts nothing.</summary>
    public static ParsedFilter None { get; } = new(null, null, null, []);

    /// <summary>Whether this filter says anything at all.</summary>
    public bool IsEmpty => Predicate is null && Spatial is null && ResourceIds.Count == 0;
}
