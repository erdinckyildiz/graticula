using System;

namespace Graticula.Host;

/// <summary>
/// A filter this face cannot evaluate is refused, never dropped.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-125](../../docs/architecture-debt.md), found by the second security gate.</b>
/// <c>MapServer/export</c> and <c>MapServer/identify</c> accepted <c>layerDefs</c> and dropped it:
/// three exports were byte-identical with no <c>layerDefs</c>, with <c>il='Adana'</c>, and with
/// <c>1=1; DROP--</c>. Nothing reached the database — <b>which is why it was rated low and is also
/// exactly what makes it worth refusing</b>. A caller who uses it to restrict what is drawn is
/// silently shown everything.
/// </para>
/// <para>
/// <b>Every other surface on this server already refuses a filter it cannot evaluate.</b> WFS's
/// <c>FilterReader</c> does, the OGC face does, <c>PortalQuery</c> does. The rule is
/// [ADR-008](../../docs/adr/ADR-008-query-engine.md)'s: an unevaluated predicate is not a wider
/// answer, it is a wrong one, because the caller asked for less than they were given and has no
/// way to tell.
/// </para>
/// <para>
/// <b>It is a compatibility decision rather than a bug fix, and the row said so.</b> An ArcGIS
/// client that sends <c>layerDefs</c> today gets a map; after this it gets a 400. That is the
/// intended trade: a map that ignores the filter is the failure this cannot detect from outside,
/// and one that refuses is a failure the client can see and act on.
/// </para>
/// </remarks>
internal static class SilentlyDroppedFilter
{
    /// <summary>
    /// Refuses a parameter this face takes and cannot honour.
    /// </summary>
    /// <param name="value">Whatever arrived, or null.</param>
    /// <param name="name">The parameter's name, as the caller wrote it.</param>
    /// <param name="error">The sentence to answer with, when there is one.</param>
    /// <returns>True when the request may go on.</returns>
    /// <remarks>
    /// <b>Empty is not sent.</b> A client that builds a query string from a form writes
    /// <c>layerDefs=</c> for a filter nobody typed, and refusing that would refuse the request
    /// every ArcGIS client makes by default. What is refused is a filter somebody wrote.
    /// </remarks>
    public static bool Absent(string? value, string name, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        error = $"`{name}` is not supported by this server, and is refused rather than ignored. "
            + "Accepting it would draw every feature while you asked for some of them, and nothing "
            + "in the answer would say so. Filter the layer before publishing it, or ask the "
            + "FeatureServer face, which evaluates `where` against the database.";

        return false;
    }
}
