using System;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Refuses a request that lacks a privilege.
/// </summary>
/// <remarks>
/// <para>
/// ADR-018 §5 of the superseded text, retained: <b>401 and 403 mean different
/// things</b>, and the difference is what an operator acts on. 401 says
/// <em>authenticate</em>; 403 says <em>ask an administrator</em>.
/// </para>
/// <para>
/// <b>This governs doing, never reading.</b> Whether a caller may read a layer
/// is decided by <see cref="LayerAccess"/> from the layer's owner and scope,
/// because the model adopted in ADR-018 has no read privilege.
/// </para>
/// <para>
/// <b>Called by the endpoint, not by middleware.</b> A middleware table mapping
/// routes to privileges is a second place the routing lives, and the failure
/// mode of the two disagreeing is an endpoint reachable with no check at all.
/// Here, an endpoint with no check is visible as an endpoint with no check.
/// </para>
/// </remarks>
internal static class Authorize
{
    /// <summary>
    /// Requires a privilege, unless a group the caller belongs to confers editing this item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared update — owner decision 2026-08-25,
    /// [ADR-036](../../docs/adr/ADR-036-groups.md) §4a as amended.</b> A layer shared with a
    /// group whose <c>item_update</c> is <c>allItems</c> is editable by that group's members,
    /// whatever privileges they hold. Everything else is unchanged: no group, or a group
    /// without that setting, and this is <see cref="RequireAsync"/>.
    /// </para>
    /// <para>
    /// <b>One helper rather than the same three lines on each face.</b> ArcGIS
    /// <c>applyEdits</c> and OGC API Features Part 4 write through one
    /// <see cref="Graticula.Features.IFeatureWriter"/> (Q-44); they have to decide who may
    /// write the same way too, or the same layer is editable through one face and refused
    /// through the other — which is the kind of divergence a conformance run finds months
    /// later, on whichever face nobody tested.
    /// </para>
    /// <para>
    /// <b>The refusal is still the privilege's.</b> When the group does not carry it, the
    /// caller is told what privilege they lack and nothing about the item — D-03's rule is
    /// unchanged, and mentioning the group would tell somebody who cannot edit that a group
    /// exists which could.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="privilege">What is required when no group carries the edit.</param>
    /// <param name="layer">The item being written to.</param>
    /// <returns>Whether the caller may proceed; a refusal has been written when false.</returns>
    public static async Task<bool> RequireEditAsync(
        HttpContext context, Privilege privilege, Graticula.Platform.Catalog.PublishedLayer layer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()
            ?? throw new InvalidOperationException(
                "No principal was resolved for this request. The authentication middleware must "
                + "run before any endpoint, including for anonymous callers — 'no principal' is a "
                + "wiring bug, not an unauthenticated request.");

        if (LayerAccess.GroupConfersEditing(
                layer.Sharing, current.Authorization, layer.SharedWith))
        {
            return true;
        }

        return await RequireAsync(context, privilege).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the request may proceed; writes the refusal if not.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="privilege">What it needs.</param>
    /// <returns>True if allowed. If false, the response has been written.</returns>
    public static async Task<bool> RequireAsync(HttpContext context, Privilege privilege)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()
            ?? throw new InvalidOperationException(
                "No principal was resolved for this request. The authentication middleware must "
                + "run before any endpoint, including for anonymous callers — 'no principal' is a "
                + "wiring bug, not an unauthenticated request.");

        if (current.Authorization.Allows(privilege))
        {
            return true;
        }

        (int status, string message) = Refusal(current, privilege);

        // The refusal names the privilege and nothing about the resource.
        // D-03's rule: what the caller may not see, they may not learn the shape
        // of either.
        await Results.Json(new { error = new { code = status, message } }, statusCode: status)
            .ExecuteAsync(context)
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>
    /// Writes the refusal for a read that sharing did not permit.
    /// </summary>
    /// <remarks>
    /// <b>404, not 403</b> — the opposite of the choice made for privileges, and
    /// for a reason that only applies here. A 403 on a named layer confirms the
    /// layer exists, which turns the endpoint into a directory of everything
    /// published on the server. For a privilege there is nothing to enumerate;
    /// for an item name there is.
    /// </remarks>
    public static Task RefuseReadAsync(HttpContext context, string layerName)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Results.Json(
            new
            {
                error = new
                {
                    code = 404,
                    message =
                        $"No layer '{layerName}' is visible to you. It may not exist, or it may "
                        + "not be shared with you — this response is deliberately the same for "
                        + "both, so that layer names cannot be discovered by guessing.",
                },
            },
            statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
    }

    /// <summary>
    /// Writes the refusal for a service that is stopped.
    /// </summary>
    /// <remarks>
    /// <b>503, not 404.</b> ADR-020 §3: it exists and is unavailable, which is a
    /// different sentence from <em>no such layer</em>, and an operator whose
    /// client has started failing needs to see which one it is. Reached only by
    /// a caller already permitted to know the layer exists — a caller who is
    /// not gets the 404 and never learns there is anything to be unavailable.
    /// </remarks>
    public static Task RefuseStoppedAsync(HttpContext context, string layerName)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Results.Json(
            new
            {
                error = new
                {
                    code = 503,
                    message =
                        $"The service '{layerName}' is stopped. It is registered and deliberately "
                        + "unavailable — an operator took it out of rotation. This is not a "
                        + "transient failure and retrying will not help; ask an administrator to "
                        + "start it.",
                },
            },
            statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
    }

    /// <summary>
    /// The status and sentence for a refusal.
    /// </summary>
    /// <remarks>
    /// <b>The middle case is the one worth having.</b> ADR-018 §3a resolves
    /// capability as role ∩ user type, which creates a failure that reads as a
    /// bug: <em>I granted publisher and they still cannot publish.</em> Saying
    /// "you do not have this privilege" would send an administrator to grant a
    /// role they have already granted. Naming the ceiling sends them to the
    /// right place.
    /// </remarks>
    private static (int Status, string Message) Refusal(
        RequestPrincipal current, Privilege privilege)
    {
        string name = Name(privilege);

        if (current.Principal.IsAnonymous)
        {
            return (StatusCodes.Status401Unauthorized,
                $"This needs the '{name}' privilege and you are not signed in. "
                + "Sign in at /rest/auth/login.");
        }

        if (current.Authorization.WithheldByUserType(privilege))
        {
            return (StatusCodes.Status403Forbidden,
                $"Your role grants '{name}', and your user type "
                + $"'{current.Authorization.UserType}' does not permit it, so it is withheld. "
                + "Granting the role again will not help — the user type is what an administrator "
                + "must change.");
        }

        return (StatusCodes.Status403Forbidden,
            $"Your account does not have the '{name}' privilege. Ask an administrator to grant a "
            + "role that carries it.");
    }

    /// <summary>
    /// The wire name of a privilege.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written out rather than derived from the enum name, because these strings
    /// reach operators in refusal messages and logs — deriving them would make
    /// renaming a C# member silently change what people read.
    /// </para>
    /// <para>
    /// <b>These are ours, not Esri's.</b> They are shaped to be recognisable to
    /// someone who knows ArcGIS Portal. The mapping to Portal's own wire
    /// identifiers belongs in the ArcGIS compatibility layer (ADR-018 §6), so
    /// that a third party's vocabulary never sits in the middle of our domain.
    /// </para>
    /// </remarks>
    public static string Name(Privilege privilege) => Roles.NameOf(privilege);
}
