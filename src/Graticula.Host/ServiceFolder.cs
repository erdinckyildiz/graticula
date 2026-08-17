using System;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Platform.Catalog;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Which URL space a service belongs in, and what happens at the wrong one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosted and registered services are separated by URL, not only by a flag.</b>
/// This is ArcGIS Enterprise's shape — hosted feature services live in a
/// <c>Hosted</c> folder and referenced ones do not — and it carries a real
/// distinction rather than a cosmetic one. A hosted service owns its table and
/// unpublishing it may drop that table; a registered service points at somebody
/// else's and must never. One namespace means every operation re-derives which
/// kind it is holding, and one day gets it wrong.
/// </para>
/// <para>
/// <b>The wrong path redirects rather than 404s.</b> A 404 tells a client the
/// service does not exist, which is false and unhelpful — the service exists and
/// has moved. A 301 tells them where, and every HTTP client follows it, so URLs
/// built before the folder existed keep working while teaching their owners the
/// new shape.
/// </para>
/// </remarks>
internal static class ServiceFolder
{
    /// <summary>
    /// Which folder a request path names, or null for the root.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>The folder, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>A lookup now, and the comment that used to be here said so.</b> It read: *hosted is
    /// the only folder that holds services … when a second service folder exists this stops
    /// being a boolean and becomes a lookup.* That happened on 2026-08-17, when the owner
    /// asked to publish registered layers into named folders — *"örneğin turkiye folderi"* —
    /// while hosted data stays in <c>hosted</c>.
    /// </para>
    /// <para>
    /// <b>Read from the path's shape rather than from a list of known folders.</b> The service
    /// type is the fixed point: every one of these URLs ends
    /// <c>…/{service}/FeatureServer/…</c>, so the segment before the type is the service and
    /// anything between <c>/rest/services</c> and that is the folder. Comparing against known
    /// names instead would need a catalogue read on the request path to route it — and would
    /// answer 404 for a folder created a moment ago.
    /// </para>
    /// </remarks>
    public static string? FolderOf(PathString path)
    {
        string value = path.Value ?? string.Empty;

        // /rest/services/{service}/FeatureServer      -> no folder
        // /rest/services/{folder}/{service}/FeatureServer -> the folder
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // rest, services, then either one or two names before the service type.
        int type = Array.FindIndex(segments, IsServiceType);

        return type >= 4 ? Uri.UnescapeDataString(segments[type - 2]) : null;
    }

    /// <summary>The segment that ends a service's address, whichever face it is.</summary>
    private static bool IsServiceType(string segment) =>
        segment.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("VectorTileServer", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("GeometryServer", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("MapServer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends the caller to the same service at its real URL.</summary>
    /// <param name="context">The request.</param>
    /// <param name="service">The service, which knows its folder.</param>
    /// <returns>The write.</returns>
    public static Task RedirectAsync(HttpContext context, PublishedService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        return RedirectAsync(context, service.Folder is not null);
    }

    /// <summary>Whether the request came in at the right URL space for this layer.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer it resolved to.</param>
    /// <returns>Whether the folder matches.</returns>
    public static bool Matches(HttpContext context, PublishedLayer layer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);

        return InHostedFolder(context.Request.Path) == (layer.Folder is not null);
    }

    /// <summary>Sends the caller to the same service at its real URL.</summary>
    /// <param name="context">The request.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The write.</returns>
    public static Task RedirectAsync(HttpContext context, PublishedLayer layer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);

        return RedirectAsync(context, layer.Folder is not null);
    }

    private static Task RedirectAsync(HttpContext context, bool hosted)
    {
        string path = context.Request.Path.Value ?? "";
        string prefix = $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}/";

        string moved = hosted
            ? path.Replace("/rest/services/", prefix, StringComparison.OrdinalIgnoreCase)
            : path.Replace(prefix, "/rest/services/", StringComparison.OrdinalIgnoreCase);

        // 301, not 302: this is where the service lives, permanently. A client
        // that caches it is doing the right thing.
        context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        context.Response.Headers.Location = moved + context.Request.QueryString;

        return Task.CompletedTask;
    }

    /// <summary>Whether a path sits inside the hosted folder.</summary>
    /// <remarks>
    /// Case-insensitive, so a client sending ArcGIS's own capitalised
    /// <c>Hosted</c> is treated the same as one sending ours.
    /// </remarks>
    private static bool InHostedFolder(PathString path) =>
        path.StartsWithSegments(
            $"/rest/services/{FeatureServerMetadataWriter.HostedFolder}",
            StringComparison.OrdinalIgnoreCase);
}
