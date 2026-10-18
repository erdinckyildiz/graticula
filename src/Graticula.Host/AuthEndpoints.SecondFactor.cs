using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>A code, to confirm or to remove a second factor.</summary>
/// <param name="Code">A code from the authenticator app, or a recovery code where removing.</param>
internal sealed record SecondFactorCodeRequest(string? Code);

/// <summary>
/// A member's own second factor — ADR-201: turned on with a code from an authenticator app, which proves the secret
/// reached it, and off with a code, so a session left open is not enough to remove it.
/// </summary>
/// <remarks>
/// <b>For accounts with a password here.</b> One that signs in through an identity provider or a directory has its
/// second step there, where its organisation set it; a code here would be a second policy nobody chose.
/// </remarks>
internal static partial class AuthEndpoints
{
    private static void MapSecondFactor(WebApplication app)
    {
        app.MapGet("/rest/auth/second-factor", SecondFactorStateAsync);
        app.MapPost("/rest/auth/second-factor", BeginSecondFactorAsync);
        app.MapPost("/rest/auth/second-factor/confirm", ConfirmSecondFactorAsync);
        app.MapPost("/rest/auth/second-factor/remove", RemoveSecondFactorAsync);
    }

    private static async Task SecondFactorStateAsync(HttpContext context, SecondFactors factors, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to see your own second factor.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            enabled = await factors.RequiredAsync(current.Principal.Id, cancellation).ConfigureAwait(false),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task BeginSecondFactorAsync(
        HttpContext context, SecondFactors factors, IIdentityStore store, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous || current.Principal.Kind != PrincipalKind.User)
        {
            await Refuse(context, 401, "Sign in as a member to turn on a second factor.").ConfigureAwait(false);
            return;
        }

        if (await store.FindForLoginAsync(current.Principal.Name, cancellation).ConfigureAwait(false) is not { Credential: not null })
        {
            await Refuse(context, 409, "This account signs in through your organisation's identity provider or directory, "
                + "and its second step is set there.").ConfigureAwait(false);
            return;
        }

        if (await factors.BeginAsync(current.Principal, $"Graticula ({context.Request.Host.Host})", cancellation)
                .ConfigureAwait(false) is not { } begun)
        {
            await Refuse(context, 409, "A second factor is already on. Remove it first, with a code, to set up another.")
                .ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            secret = begun.Secret,
            address = begun.Address,
            note = "Add this to an authenticator app — scan the address as a QR code, or type the secret — then send the "
                + "code it shows to /rest/auth/second-factor/confirm. Nothing is asked at sign-in until then.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ConfirmSecondFactorAsync(
        HttpContext context, SecondFactorCodeRequest request, SecondFactors factors, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to turn on a second factor.").ConfigureAwait(false);
            return;
        }

        if (await factors.ConfirmAsync(current.Principal.Id, request.Code, cancellation).ConfigureAwait(false)
            is not { } recovery)
        {
            await Refuse(context, 400, "The code does not match, or no second factor is being set up. Use the code the "
                + "app shows now; if it still fails, check the device's clock.").ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(context, audit, "secondfactor.enable", current.Principal.Name,
            AdminEndpoints.Detail(new { }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            enabled = true,
            recoveryCodes = recovery,
            note = "Keep these somewhere other than the phone. Each signs you in once in place of a code; they are not "
                + "shown again. Scripts that signed in with your password need an API key now (ADR-200).",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RemoveSecondFactorAsync(
        HttpContext context, SecondFactorCodeRequest request, SecondFactors factors, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to remove your second factor.").ConfigureAwait(false);
            return;
        }

        if (!await factors.VerifyAsync(current.Principal.Id, request.Code, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 400, "Removing a second factor takes a code from it, or a recovery code, so a session "
                + "left open is not enough. Lost both? An administrator can remove it.").ConfigureAwait(false);
            return;
        }

        await factors.RemoveAsync(current.Principal.Id, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "secondfactor.remove", current.Principal.Name,
            AdminEndpoints.Detail(new { }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { enabled = false }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
