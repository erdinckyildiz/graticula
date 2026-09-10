using System;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Which surface a request reached, and which service it named.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two fields the request log would otherwise make an operator derive by eye.</b> Without
/// them, *how much of yesterday's traffic was WFS* and *which service is being hammered* are
/// questions answered by reading paths, which is what the log exists to stop.
/// </para>
/// <para>
/// <b>Derived from the path rather than set by each endpoint.</b> Asking every route to
/// declare its own face would be more accurate and would be wrong within a week: there are
/// dozens of them, a new one would forget, and the forgetting would be invisible. A path is
/// the one thing every request has.
/// </para>
/// </remarks>
internal static class RequestFacts
{
    /// <summary>The surface a path belongs to, or null when it is none of them.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>A short name — <c>ArcGIS</c>, <c>WMS</c>, <c>studio</c> and so on.</returns>
    /// <remarks>
    /// <b>The ArcGIS faces are not separated here and the service type is not the face.</b>
    /// A FeatureServer and an ImageServer are both the ArcGIS REST surface; which one a
    /// request hit is already in the path, and splitting them would make the useful
    /// comparison — ArcGIS against OGC against the console — impossible to see at a glance.
    /// </remarks>
    public static string? Face(PathString path)
    {
        string? value = path.Value;

        if (value is not { Length: > 1 })
        {
            return null;
        }

        return value switch
        {
            _ when Starts(value, "/rest") => "ArcGIS",
            _ when Starts(value, "/wms") => "WMS",
            _ when Starts(value, "/wfs") => "WFS",
            _ when Starts(value, "/ogc") => "OGC",
            _ when Starts(value, "/studio") => "studio",
            _ when Starts(value, "/server") || Starts(value, "/console") => "console",
            _ when Starts(value, "/admin") => "admin",
            _ when Starts(value, "/sharing") => "portal",
            _ when Starts(value, "/healthz") => "health",
            _ => null,
        };
    }

    /// <summary>The service a path names, as <c>folder/name</c>, or null.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>The qualified service name.</returns>
    /// <remarks>
    /// <para>
    /// <b>Read backwards from the service-type segment, which is what makes it work for both
    /// shapes.</b> A service lives at <c>/rest/services/{name}/{Type}</c> or
    /// <c>/rest/services/{folder}/{name}/{Type}</c>, and there is no way to tell a folder
    /// from a name going forwards — but the segment before the type is always the name, and
    /// the one before that is a folder if anything is left.
    /// </para>
    /// <para>
    /// <b>Nothing else is guessed.</b> A path with no service type in it returns null rather
    /// than the first segment that looks plausible: a log column that is sometimes a service
    /// and sometimes whatever was in that position cannot be filtered on.
    /// </para>
    /// </remarks>
    public static string? Service(PathString path) => Service(path, QueryString.Empty);

    /// <summary>
    /// What a request named, on any face that names one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three faces were writing a null into a column they had the answer for —
    /// [D-255](../../docs/architecture-debt.md).</b> This read the path and returned null unless
    /// it began <c>/rest</c>, so `request_log.service` was filled on the ArcGIS face and nowhere
    /// else. **Measured on the fixture, 2026-09-10**: ArcGIS 219,461 rows and **64.3%** named;
    /// OGC 30,678, WMS 9,848 and WFS 5,674 rows and **0%** named — the three faces that serve
    /// the same layers, saying nothing about which.
    /// </para>
    /// <para>
    /// <b>Found by walking [ADR-061](../../docs/adr/ADR-061-metrics-are-aggregate-and-per-entity-numbers-live-in-the-admin-api.md)
    /// condition 2</b> — the 2 AM scenario, *this service is slow*, using only what ADR-007 §5
    /// leaves available. The answer turned on this: the store has the column, and three of four
    /// faces were leaving it empty.
    /// </para>
    /// <para>
    /// <b>Each face names it where that face puts it, and nowhere else.</b> ArcGIS puts it in the
    /// path; OGC API Features puts the collection in the path under <c>collections/</c>; WMS and
    /// WFS put it in the query, as <c>layers</c> and <c>typeNames</c>. Reading a query parameter
    /// here is not a layering slip — it is where the protocol defines the answer to be.
    /// </para>
    /// <para>
    /// <b>The first of a list is taken, and the rest are dropped.</b> A WMS <c>GetMap</c> may
    /// name several layers; a log column holds one. The first is what the request is filed
    /// under, which is a choice rather than a truth — recorded here so nobody reads a
    /// single-service row as proof a request touched one.
    /// </para>
    /// </remarks>
    /// <param name="path">The request path.</param>
    /// <param name="query">Its query string, for the faces that name the service there.</param>
    /// <returns>The service or layer named, or null.</returns>
    public static string? Service(PathString path, QueryString query)
    {
        string? value = path.Value;

        if (value is null)
        {
            return null;
        }

        // <b>OGC API Features: the collection is a path segment.</b> `/ogc/features/v1/
        // collections/{id}` and everything under it — items, a single feature, the queryables.
        if (Starts(value, "/ogc"))
        {
            string[] ogc = value.Split('/', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < ogc.Length - 1; i++)
            {
                if (string.Equals(ogc[i], "collections", StringComparison.Ordinal))
                {
                    return ogc[i + 1];
                }
            }

            return null;
        }

        // <b>WMS and WFS: the protocol puts it in the query.</b> `layers` on GetMap and
        // GetFeatureInfo, `typeNames` on GetFeature and DescribeFeatureType. A GetCapabilities
        // names nothing, and null is the right answer for it.
        if (Starts(value, "/wms") || Starts(value, "/wfs"))
        {
            return Named(query, Starts(value, "/wms") ? "layers" : "typeNames");
        }

        if (!Starts(value, "/rest"))
        {
            return null;
        }

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            if (!parts[i].EndsWith("Server", StringComparison.Ordinal))
            {
                continue;
            }

            // The type is at i, so the name is at i-1 and a folder, if there is one, at
            // i-2 — but only when i-2 is past "services", which sits at index 1.
            if (i < 2)
            {
                return null;
            }

            return i >= 4 && parts[1] is "services"
                ? parts[i - 2] + "/" + parts[i - 1]
                : parts[i - 1];
        }

        return null;
    }

    /// <summary>The first value of a query parameter, matched however it is cased.</summary>
    /// <remarks>
    /// <b>Case-insensitive, because WMS and WFS are.</b> Both specifications say parameter
    /// *names* are case-insensitive, and a client writing `LAYERS=` is a client this log would
    /// otherwise file under nothing. The value is left exactly as it was sent.
    /// </remarks>
    /// <param name="query">The query string.</param>
    /// <param name="name">The parameter to read.</param>
    /// <returns>Its first comma-separated value, or null.</returns>
    private static string? Named(QueryString query, string name)
    {
        if (!query.HasValue)
        {
            return null;
        }

        foreach (string pair in query.Value!.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = pair.IndexOf('=', StringComparison.Ordinal);

            if (at <= 0
                || !pair[..at].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = Uri.UnescapeDataString(pair[(at + 1)..]);

            int comma = value.IndexOf(',', StringComparison.Ordinal);

            string first = (comma >= 0 ? value[..comma] : value).Trim();

            return first.Length is 0 ? null : first;
        }

        return null;
    }

    private static bool Starts(string value, string segment) =>
        value.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
        && (value.Length == segment.Length || value[segment.Length] == '/');
}
