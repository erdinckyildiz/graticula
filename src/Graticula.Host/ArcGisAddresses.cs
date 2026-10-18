using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The addresses an ArcGIS shop already has written down, answered by this server's own routes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither name is this product's choice.</b> The server's own address is the root — <c>/rest/services</c> — and
/// stays so. <c>/arcgis</c> is where a stand-alone ArcGIS Server has answered since 9.x, and so what every Pro
/// connection, ArcPy script, web map and bookmark of one says; <c>/server</c> is ArcGIS Enterprise's default Web Adaptor
/// name for its hosting server. Answering both turns a migration into changing a host name (owner, 2026-10-17:
/// <i>"arcgis yerine server yazsak olmuyor mu?"</i> — the answer was both, since the names are the clients').
/// </para>
/// <para>
/// <b>Always on.</b> Until 2026-10-17 the <c>/arcgis</c> rewrite sat inside the switch for publishing from Pro, which
/// needed it first, so turning publishing off would have broken every old address silently. Only
/// <c>/arcgis/admin</c> — Pro's view of a hosting server's admin API, which is not this server's <c>/admin</c> —
/// stays with publishing; without it, that address is a 404 rather than this server's own admin API under a name
/// that promises another.
/// </para>
/// <para>
/// <b>Under <c>/server</c>, only <c>/server/rest</c>.</b> <c>/server/</c> and its pages are this product's Server
/// screens, and they stay.
/// </para>
/// </remarks>
internal static class ArcGisAddresses
{
    internal enum Outcome
    {
        /// <summary>Not one of these addresses; the path goes on as it came.</summary>
        Unchanged,

        /// <summary>Answered by this server's route at <see cref="Mapping.Path"/>.</summary>
        Rewritten,

        /// <summary>The server's own base — <c>/arcgis</c> — which an ArcGIS Server answers with a page.</summary>
        ServerPage,

        /// <summary>An address that exists only while publishing from Pro is on.</summary>
        NotFound,
    }

    internal readonly record struct Mapping(Outcome Outcome, PathString Path);

    internal static Mapping Map(PathString path, bool publishing)
    {
        // Its admin URL is the hosting server's admin API Pro reads before it publishes, which is not this server's own
        // /admin: the token exchange goes to /admin/generateToken, the rest to the recorder.
        if (path.StartsWithSegments("/arcgis/admin", out PathString admin))
        {
            return !publishing
                ? new Mapping(Outcome.NotFound, path)
                : new Mapping(Outcome.Rewritten, admin.StartsWithSegments("/generateToken")
                    ? "/admin/generateToken"
                    : new PathString("/pro-admin").Add(admin.HasValue ? admin : "/"));
        }

        if (path.StartsWithSegments("/arcgis", out PathString rest))
        {
            // <b>The server's own address answers 200</b>: Pro asked it dozens of times, unauthenticated, between the
            // token exchange and the reads it never made, and met a 302 each time (traced 2026-10-17); an ArcGIS
            // Server's base answers a page. This one is a page of its own, naming the directory.
            return !rest.HasValue || rest == "/"
                ? new Mapping(Outcome.ServerPage, path)
                : new Mapping(Outcome.Rewritten, rest);
        }

        if (path.StartsWithSegments("/server/rest", out PathString serverRest))
        {
            return new Mapping(Outcome.Rewritten, new PathString("/rest").Add(serverRest));
        }

        return new Mapping(Outcome.Unchanged, path);
    }
}
