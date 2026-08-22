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
    public static string? Service(PathString path)
    {
        string? value = path.Value;

        if (value is null || !Starts(value, "/rest"))
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

    private static bool Starts(string value, string segment) =>
        value.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
        && (value.Length == segment.Length || value[segment.Length] == '/');
}
