using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// The ArcGIS services surface's answer to a caller who sent no token and asked for a service it cannot see —
/// <c>499 Token Required</c> in the body, as ArcGIS answers, and 401 on the status line.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-11-01 for [D-347](../../docs/architecture-debt.md).</b> The ArcGIS Maps SDK, given a portal item
/// whose service is shared with the organisation, asks for the service <i>without</i> a token first and attaches one
/// only when the answer challenges it — a 499 or 498. This server answered 404, its deliberate <i>not visible to
/// you</i>, which challenges nothing, so the SDK stopped there and the layer looked deleted. Measured with
/// <c>tools/sdk-probe/organisation.html</c> (SDK 4.30, headless Chrome): with the 404 rewritten to a 499 in the
/// browser, and nothing else changed, the SDK asked again with the token it already held, loaded the layer and
/// counted its eight features.
/// </para>
/// <para>
/// <b>Why this is still not a disclosure.</b> <see cref="InvalidToken"/> declined 499 here because answering it for a
/// private service and 404 for a missing one says which names exist. So an anonymous caller gets 499 for <i>both</i>:
/// every service it may not see — absent, private, shared with a group or the organisation — answers the same
/// challenge, and only a caller who has signed in is told 404. What an anonymous caller learns is that it is anonymous.
/// </para>
/// <para>
/// <b>401 on the status line, not 499.</b> 499 is the code this server's access log uses for a client that hung up
/// (<see cref="ErrorResponse"/>), and a refusal written as 499 would be counted as one. The ArcGIS clients read the
/// code from the body; <c>AnalysisEndpoints.RefuseAsync</c> already writes its 499 this way.
/// </para>
/// <para>
/// <b>Only on <c>/rest/services</c>, and only as JSON.</b> 499 is the ArcGIS services vocabulary; OGC API, WMS, WFS
/// and the native API keep their 404. A browser reading the directory keeps the page it had.
/// </para>
/// </remarks>
internal static class TokenRequired
{
    /// <summary>The code in the body.</summary>
    public const int Code = 499;

    /// <summary>Whether a refusal of a read on this request is answered with the challenge.</summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether it is.</returns>
    public static bool Applies(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Path.StartsWithSegments("/rest/services", StringComparison.OrdinalIgnoreCase)
            || RestDirectory.RefusesAsPage(context.Request))
        {
            return false;
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        // A caller the store could not identify keeps the 404: signing in again cannot help it, and a challenge would
        // send every client to a sign-in that is down too (D-192). A rejected token was already answered 498.
        return current is { Principal.IsAnonymous: true, StoreWasUnreachable: false, TokenWasRejected: false };
    }

    /// <summary>Writes the challenge.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public static Task WriteAsync(HttpContext context) => Result(context).ExecuteAsync(context);

    /// <summary>The challenge as a result to return, for a filter that must not write the response itself.</summary>
    /// <param name="context">The request, whose response is told not to be stored.</param>
    /// <returns>The result.</returns>
    public static IResult Result(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(
            new
            {
                error = new
                {
                    code = Code,
                    message = "Token Required",
                    details = new[]
                    {
                        "No service at this address is open to a caller without a token. Sign in and send the token: "
                        + "a service shared with you is then served, and one that does not exist answers 404. "
                        + "This answer is the same for a service that exists and one that does not.",
                    },
                },
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
