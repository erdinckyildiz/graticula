using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The ArcGIS surface's answer to a token that is expired, revoked or was never issued — 498, as
/// ADR-015 §4a records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-15, after an authenticated review against the showcase.</b> A token was
/// revoked by signing out, and the same request with it answered 404 <i>No layer 'veteran_probe' is
/// visible to you</i>; the folder listing answered 200 without the private service in it. The ArcGIS
/// Maps SDK's identity manager and ArcGIS Pro ask the user to sign in again when a response says
/// 498, and not otherwise — so a dashboard whose twelve-hour token ran out did not prompt, it went
/// quietly empty, and the report that reached an administrator was that a service had been deleted.
/// </para>
/// <para>
/// <b>Why this is not a disclosure.</b> 498 says something about the token the caller holds and
/// nothing about any resource: the same answer comes back for a public layer, a private one and a
/// path that does not exist. ~~The deliberate 404 for a private resource asked for with no token at
/// all — which does not reveal that the resource exists — is unchanged, and that is why this server
/// still does not answer 499 there.~~ <b>Since 2026-11-01 it does, on <c>/rest/services</c></b>
/// (<see cref="TokenRequired"/>, D-347): the ArcGIS Maps SDK attaches a token it holds only when it is
/// challenged, and the disclosure is avoided the other way — an anonymous caller is challenged for a
/// service that does not exist as well, so the challenge says nothing about which names exist.
/// </para>
/// </remarks>
internal static class InvalidToken
{
    /// <summary>The code, in the body and on the status line alike.</summary>
    public const int Code = 498;

    private static readonly string[] Surfaces = ["/rest", "/sharing/rest"];

    // <b>Where a stale token must not stand in the way of getting a new one or of signing out.</b>
    // A client that still carries yesterday's token in its default parameters calls these to fix
    // exactly that, and refusing them for it would leave no way back.
    private static readonly string[] Exempt =
    [
        "/rest/generateToken",
        "/rest/auth",
        "/rest/info",
        "/services/rest/info",

        // Asked by the console and the directory banner to learn whether the token they hold is
        // still good; its answer for a dead one is *not authenticated*, which is what they act on.
        "/rest/whoami",
        "/sharing/rest/generateToken",
        "/sharing/rest/info",
    ];

    /// <summary>Whether a path answers a rejected token with 498.</summary>
    /// <param name="path">The request path.</param>
    /// <returns>Whether it does.</returns>
    public static bool Applies(PathString path) =>
        Surfaces.Any(surface => path.StartsWithSegments(surface, StringComparison.OrdinalIgnoreCase))
        && !Exempt.Any(exempt => path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a request answers a rejected token with 498: never a <c>HEAD</c>, which ArcGIS Enterprise answers 200 with a
    /// token it cannot read (measured 2026-11-13 against a 2025.2 portal: <c>HEAD /portal/sharing/rest?f=json</c> with a
    /// made-up token, 200, where a <c>GET</c> answers 200 with code 498 in the body). ArcGIS Field Maps opens with exactly
    /// that request, and a <c>HEAD</c> has no body to carry the error in. <b>What this is not:</b> the fix for Field Maps
    /// losing its portal when its 30-minute access token ran out. That was seen the same day — every request 498, syncs
    /// failing, <i>No portal connection</i> — and the device recovered by refreshing its token (a new session at 11:40
    /// UTC) before this change was running, so whether this change matters to it is not measured.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="path">The request path.</param>
    /// <returns>Whether it does.</returns>
    public static bool Applies(string method, PathString path) =>
        !HttpMethods.IsHead(method) && Applies(path);

    /// <summary>Writes the refusal.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public static Task WriteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(
            new
            {
                error = new
                {
                    code = Code,
                    message = "Invalid token.",
                    details = new[]
                    {
                        "The token sent with this request is expired, revoked or was never issued. "
                        + "Sign in again, or send the request without a token to be served as an "
                        + "anonymous caller.",
                    },
                },
            },
            statusCode: Code).ExecuteAsync(context);
    }
}
