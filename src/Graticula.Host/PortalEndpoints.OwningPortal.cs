using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// Which portal the server says owns it, and where a client exchanges its token — <c>owningSystemUrl</c> and
/// <c>tokenServicesUrl</c> in <c>/rest/info</c> — ADR-299 condition 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>ArcGIS Pro is told <c>/studio</c>, and every other client the root, as before.</b> Pro publishes only to a portal
/// that has a web adaptor name: the root portal keeps <i>Share As Web Layer</i> grey (Esri article 000031246), so the
/// portal a publishing Pro is signed in to is <c>…/studio</c>. Pro writes the server's <c>owningSystemUrl</c> into the
/// draft as the target portal and compares it with its active portal; the root origin did not match, and the desktop
/// application stopped at 002901 <i>"the target portal for publishing does not match the currently active portal"</i>
/// before the analyzer ran (measured 2026-11-12).
/// </para>
/// <para>
/// <b>Why not everyone.</b> ArcGIS Field Maps signs in at the root (Q-228) and the ArcGIS SDKs use this field to decide
/// which portal's token a server takes; telling them <c>/studio</c> would make them treat a server they already reach as
/// another portal's. Pro names itself in every request (<c>ArcGIS Pro 3.7.0 (…) - ArcGISPro</c>, the wire log on dev),
/// so the answer is chosen by that and nothing else changes for anybody. <b>INFERRED</b> from the owner's
/// <i>"kalan işleri de tamamla"</i> (2026-11-13), given in answer to the question whether to make this change; the
/// Pro-only scope is this ADR's, listed for the owner.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The portal under which Pro publishes: the product's portal address.</summary>
    internal const string ProPortalBase = "/studio";

    /// <summary>Whether the caller is ArcGIS Pro, by the name it gives in every request.</summary>
    internal static bool IsArcGisPro(string? userAgent) =>
        userAgent is { Length: > 0 }
        && (userAgent.StartsWith("ArcGIS Pro", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("- ArcGISPro", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The portal that owns this server for this caller: <c>/studio</c> for a publishing Pro, otherwise the address the
    /// caller used (its origin and path base), which is what every client was told before ADR-299.
    /// </summary>
    internal static string OwningSystemUrl(HttpContext context)
    {
        string origin = $"{context.Request.Scheme}://{context.Request.Host}";
        return ProTarget(context) ? origin + ProPortalBase : origin + context.Request.PathBase;
    }

    /// <summary>Where the caller exchanges a token: the owning portal's <c>generateToken</c> for Pro, this server's otherwise.</summary>
    internal static string TokenServicesUrl(HttpContext context)
    {
        string origin = $"{context.Request.Scheme}://{context.Request.Host}";
        return ProTarget(context) ? $"{origin}{ProPortalBase}/sharing/rest/generateToken" : $"{origin}/rest/generateToken";
    }

    private static bool ProTarget(HttpContext context) =>
        IsArcGisPro(context.Request.Headers.UserAgent.ToString())
        && PublishingFromPro(context.RequestServices.GetRequiredService<IConfiguration>());
}
