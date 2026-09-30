using System;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Platform.Catalog;

/// <summary>An item's tags — ADR-111: words to find it by, as Portal's items carry.</summary>
public static class ItemTags
{
    /// <summary>The most tags one item keeps.</summary>
    public const int MaximumCount = 32;

    /// <summary>The longest one tag may be.</summary>
    public const int MaximumLength = 64;

    /// <summary>
    /// The tags as they are stored: trimmed, empties dropped, the same word once whatever its case, in the order given;
    /// or null with the reason when there are too many or one is too long.
    /// </summary>
    /// <param name="given">What the caller sent.</param>
    /// <param name="why">The refusal, when there is one.</param>
    /// <returns>The tags to store, or null.</returns>
    public static IReadOnlyList<string>? Normalise(IEnumerable<string?>? given, out string? why)
    {
        why = null;
        List<string> kept = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? raw in given ?? [])
        {
            string tag = (raw ?? string.Empty).Trim();
            if (tag.Length == 0 || !seen.Add(tag)) continue;

            if (tag.Length > MaximumLength)
            {
                why = $"A tag is at most {MaximumLength} characters; '{tag[..20]}…' is {tag.Length}.";
                return null;
            }

            kept.Add(tag);
        }

        if (kept.Count > MaximumCount)
        {
            why = $"An item keeps at most {MaximumCount} tags; {kept.Count} were sent.";
            return null;
        }

        return kept;
    }
}
