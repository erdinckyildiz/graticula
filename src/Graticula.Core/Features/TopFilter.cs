using System;
using System.Collections.Generic;

namespace Graticula.Features;

/// <summary>
/// ArcGIS's <c>topFilter</c> for <c>queryTopFeatures</c>: the first <see cref="Count"/> features of each group, by an
/// order — answered 404 until the ArcGIS gap review of 2026-10-17.
/// </summary>
/// <param name="GroupBy">The fields whose values make a group; checked against the layer's before this is built.</param>
/// <param name="Count">How many features each group keeps, at least one.</param>
/// <param name="OrderBy">The order within a group; the object id when empty.</param>
public sealed record TopFilter(IReadOnlyList<string> GroupBy, int Count, IReadOnlyList<SortKey> OrderBy)
{
    /// <summary>The most features a group may keep.</summary>
    public const int MaximumCount = 1_000;

    /// <summary>Checks the shape; the names are checked by whoever read them from a request.</summary>
    public TopFilter Validated()
    {
        if (GroupBy.Count == 0)
        {
            throw new ArgumentException("A top filter groups by at least one field.", nameof(GroupBy));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Count, MaximumCount);
        return this;
    }
}
