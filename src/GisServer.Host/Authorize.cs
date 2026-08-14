using System;
using System.Threading.Tasks;
using GisServer.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace GisServer.Host;

/// <summary>
/// Refuses a request that lacks a permission.
/// </summary>
/// <remarks>
/// <para>
/// ADR-018 §5. <b>401 and 403 mean different things</b> and the difference is
/// what an operator acts on: 401 says <em>authenticate</em>, 403 says <em>ask an
/// administrator</em>. Collapsing them loses the ability to tell a wrong
/// credential from a wrong grant, which is most of diagnosing an access problem.
/// </para>
/// <para>
/// <b>Called by the endpoint, not by middleware.</b> A middleware table mapping
/// routes to permissions is a second place the routing lives, and the failure
/// mode of the two disagreeing is an endpoint reachable with no check at all.
/// Here, the check is in the handler that needs it, and an endpoint with no
/// check is visible as an endpoint with no check.
/// </para>
/// </remarks>
internal static class Authorize
{
    /// <summary>
    /// Whether the request may proceed; writes the refusal if not.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="permission">What it needs.</param>
    /// <returns>True if allowed. If false, the response has been written.</returns>
    public static async Task<bool> RequireAsync(HttpContext context, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()
            ?? throw new InvalidOperationException(
                "No principal was resolved for this request. The authentication middleware must "
                + "run before any endpoint, including for anonymous callers — 'no principal' is a "
                + "wiring bug, not an unauthenticated request.");

        if (current.Authorization.Allows(permission))
        {
            return true;
        }

        (int status, string message) = current.Principal.IsAnonymous
            ? (StatusCodes.Status401Unauthorized,
                $"This needs the '{Name(permission)}' permission and you are not signed in. "
                + "Sign in at /rest/auth/login. If this server is meant to serve anonymous "
                + "callers, an administrator grants the 'viewer' role to 'anonymous'.")
            : (StatusCodes.Status403Forbidden,
                $"Your account does not have the '{Name(permission)}' permission. Ask an "
                + "administrator to grant a role that carries it.");

        // ADR-018 condition 3, and D-03's rule applied: the refusal names the
        // permission and nothing about the resource. What the caller may not see,
        // they may not learn the shape of either.
        await Results.Json(
            new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context)
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>
    /// The wire name of a permission.
    /// </summary>
    /// <remarks>
    /// These strings reach operators in refusal messages and logs, so they are
    /// written out rather than derived from the enum name — which would make
    /// renaming a C# member silently change what people read.
    /// </remarks>
    public static string Name(Permission permission) => permission switch
    {
        Permission.LayerRead => "layer.read",
        Permission.LayerPublishHosted => "layer.publish.hosted",
        Permission.DataSourceRegister => "datasource.register",
        Permission.LayerPublishRegistered => "layer.publish.registered",
        Permission.SharingOverride => "sharing.override",
        Permission.PrincipalManage => "principal.manage",
        Permission.RoleGrant => "role.grant",
        Permission.SessionManage => "session.manage",
        Permission.ServerOperate => "server.operate",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null),
    };
}
