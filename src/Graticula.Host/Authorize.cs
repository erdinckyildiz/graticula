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
    /// Whether the caller may write to this layer — adding, changing or deleting features, or their
    /// attachments; writes the refusal when not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A layer is written to by its owner, by an administrator, and by a group its owner has
    /// shared it with for editing — owner decision 2026-09-16,
    /// [ADR-075](../../docs/adr/ADR-075-a-layer-is-edited-by-its-owner.md).</b> The rule is
    /// <see cref="LayerAccess.MayEdit"/>, and this is the one door every writing face goes through:
    /// ArcGIS <c>applyEdits</c> at both levels, the three OGC API Features writes, and attachments.
    /// </para>
    /// <para>
    /// <b>What it replaced was two privileges and a scope.</b> Adding asked for <c>features:edit</c>
    /// and changing asked for <c>features:fullEdit</c> — or, on a layer that records its creators,
    /// <c>features:edit</c> reaching the caller's own features (ADR-064) — and neither asked whose
    /// layer it was. So every member with an editor's role could write to every layer they could
    /// read, public ones included. The owner's delegation survives: shared update, ADR-036 §4a.
    /// </para>
    /// <para>
    /// <b>Adding and changing now have one answer</b>, because the question is no longer which
    /// privilege but whose layer, and a layer's owner may do both to it. The privilege the caller
    /// passes is still what a refusal names when a privilege is what is missing.
    /// </para>
    /// <para>
    /// <b>The refusal is the privilege's where a privilege would fix it, and the layer's
    /// otherwise.</b> Anonymous gets the privilege refusal, which asks for a token; an owner whose
    /// role has lost <c>features:edit</c> is told that. Anybody else can already read the layer —
    /// <c>ServiceLookup</c> answers 404 first for one they cannot — so naming the rule tells them
    /// nothing they could not see, and it does not name the owner.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="privilege">What a refusal names when the caller's role is what is missing.</param>
    /// <param name="layer">The layer being written to.</param>
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

        if (EditRightOf(current, layer) != LayerAccess.EditRight.None)
        {
            return true;
        }

        if (current.Principal.IsAnonymous
            || (layer.Owner == current.Principal.Id && !current.Authorization.Allows(privilege)))
        {
            return await RequireAsync(context, privilege).ConfigureAwait(false);
        }

        string message =
            $"Layer '{layer.Definition.Name}' is edited by its owner, by an administrator, and by a "
            + "group its owner has shared it with for editing. "
            + (layer.Owner is null
                ? "It has no owner, so until an administrator assigns one only an administrator edits it."
                : "You are none of these; ask its owner to share it with a group you belong to for editing.");

        await Results.Json(new { error = new { code = 403, message, details = Array.Empty<string>() } }, statusCode: 403)
            .ExecuteAsync(context)
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>The caller's right to write to a layer, from the request.</summary>
    /// <param name="current">The caller.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The ground it stands on, or none.</returns>
    internal static LayerAccess.EditRight EditRightOf(
        RequestPrincipal current, Graticula.Platform.Catalog.PublishedLayer layer) =>
        LayerAccess.MayEdit(
            layer.Owner, layer.Sharing, layer.SharedWith, current.Principal, current.Authorization);

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
        await Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status)
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
                    details = Array.Empty<string>(),
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
                    details = Array.Empty<string>(),
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
    internal static (int Status, string Message) Refusal(
        RequestPrincipal current, Privilege privilege)
    {
        string name = Name(privilege);

        if (current.Principal.IsAnonymous)
        {
            // <b>Anonymous for two different reasons, and they need different sentences --
            // D-192.</b> Sessions live in the platform store, so during an outage this server
            // cannot tell a valid token from a forged one and refuses both, correctly. Telling
            // the holder of a perfectly good token that they are *not signed in* sends them to
            // a login route that also cannot work, at the moment an administrator most needs
            // to look -- which is the failure ADR-017 §6 is written to prevent. Found by
            // `tools/outage-rehearsal.sh`, which stopped the datastore and read the answer.
            if (current.TokenOutsideScope)
            {
                return (StatusCodes.Status403Forbidden,
                    $"This needs the '{name}' privilege, and the token sent was issued by an ArcGIS token "
                    + "endpoint (generateToken), which opens the ArcGIS surfaces and not the administration "
                    + "API under /admin (ADR-015 §4). Sign in at /rest/auth/login for a token that does.");
            }

            if (current.StoreWasUnreachable)
            {
                return (StatusCodes.Status401Unauthorized,
                    $"This needs the '{name}' privilege, and this server cannot currently "
                    + "reach the platform store where sessions live -- so it cannot tell "
                    + "whether you are signed in, and refuses rather than assume. Your "
                    + "credentials are probably fine and signing in again will not work "
                    + "either. GET /admin/health answers without the store and says what is "
                    + "wrong with it.");
            }

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
