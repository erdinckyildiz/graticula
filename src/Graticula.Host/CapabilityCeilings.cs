using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// Whether a service's configured ceiling refuses a capability, and the one sentence that
/// says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>One predicate and one sentence, four faces — [D-180](../../docs/architecture-debt.md),
/// and the reason it is here is [D-46](../../docs/architecture-debt.md).</b> ArcGIS, OGC API
/// Features, WFS and WMS each refuse in their own vocabulary, and that part cannot be shared:
/// a `ServiceExceptionReport` is not an RFC 7807 problem. What can be shared is the decision
/// itself and the words it is explained in, and those had already been written twice and were
/// about to be written twice more.
/// </para>
/// <para>
/// <b>The sentence names the setting rather than the caller.</b> Whoever reads it is usually an
/// operator during an incident, and the useful fact is *this service is configured to offer
/// these things* — not *you are not allowed*, which sends them to look at permissions.
/// </para>
/// </remarks>
public static class CapabilityCeilings
{
    /// <summary>Whether the ceiling refuses this capability.</summary>
    /// <remarks>
    /// <b>A null ceiling is no ceiling, and an empty one refuses everything.</b> The two are
    /// different states in the catalogue and mean different things: nothing configured, against
    /// configured to offer nothing.
    /// </remarks>
    /// <param name="layer">The layer, which carries its service's ceiling.</param>
    /// <param name="capability">What this request needs — <c>Query</c>, <c>Create</c>,
    /// <c>Update</c> or <c>Delete</c>.</param>
    /// <returns><see langword="true"/> when the request must be refused.</returns>
    public static bool Refuses(PublishedLayer layer, string capability) =>
        layer.CapabilityCeiling is { } ceiling
        && !ceiling.Contains(capability, StringComparer.Ordinal);

    /// <summary>The refusal, in words, for any face to carry.</summary>
    /// <param name="layer">The layer whose service is configured.</param>
    /// <param name="refused">What was asked for and is not offered.</param>
    /// <returns>The sentence.</returns>
    public static string Explain(PublishedLayer layer, params string[] refused)
    {
        IReadOnlyList<string> ceiling = layer.CapabilityCeiling ?? [];

        string offered = ceiling.Count == 0 ? "nothing" : string.Join(", ", ceiling);

        return $"Service '{layer.ServiceName}' is configured to offer {offered}, so "
            + $"{string.Join(" and ", refused)} is refused here. The service is running and "
            + "answering what it does offer; an administrator can change this on its "
            + "capabilities.";
    }
}
