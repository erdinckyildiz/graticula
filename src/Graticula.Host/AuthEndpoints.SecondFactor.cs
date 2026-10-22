using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>A code, to confirm or to remove a second factor.</summary>
/// <param name="Code">A code from the authenticator app, or a recovery code where removing.</param>
internal sealed record SecondFactorCodeRequest(string? Code);

/// <summary>The organisation's second-factor requirement: <c>off</c>, <c>administrators</c> or <c>everyone</c>.</summary>
internal sealed record SecondFactorRequirementRequest(string? Required);

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

        // ADR-201 condition 2: whom the organisation requires one of.
        app.MapGet("/admin/security/second-factor", SecondFactorRequirementAsync);
        app.MapPut("/admin/security/second-factor", SetSecondFactorRequirementAsync);
    }

    private static async Task SecondFactorStateAsync(
        HttpContext context, SecondFactors factors, SecondFactorPolicy policy, IIdentityStore store, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            await Refuse(context, 401, "Sign in to see your own second factor.").ConfigureAwait(false);
            return;
        }

        // `available`: a password here, so one can be set up here. `required`: the organisation requires one of this member
        // (ADR-201 condition 2) — so a page can say so before the next sign-in does.
        bool available = current.Principal.Kind == PrincipalKind.User
            && await store.FindForLoginAsync(current.QualifiedName, cancellation).ConfigureAwait(false) is { Credential: not null };
        SecondFactorRequirement requirement = await policy.ReadAsync(cancellation).ConfigureAwait(false);
        bool administrator = (await store.RolesOfAsync(current.Principal.Id, cancellation).ConfigureAwait(false))
            .Contains(Roles.Administrator, StringComparer.Ordinal);

        await Results.Json(new
        {
            enabled = await factors.RequiredAsync(current.Principal.Id, cancellation).ConfigureAwait(false),
            available,
            required = available && SecondFactorPolicy.Covers(requirement, administrator),
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

        if (await store.FindForLoginAsync(current.QualifiedName, cancellation).ConfigureAwait(false) is not { Credential: not null })
        {
            await Refuse(context, 409, "This account signs in through your organisation's identity provider or directory, "
                + "and its second step is set there.").ConfigureAwait(false);
            return;
        }

        if (await factors.BeginAsync(current.Principal, SecondFactorIssuer(context), cancellation)
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

    private static async Task SecondFactorRequirementAsync(
        HttpContext context, SecondFactorPolicy policy, PostgresSecondFactorStore store, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        SecondFactorRequirement requirement = await policy.ReadAsync(cancellation).ConfigureAwait(false);
        await RequirementJsonAsync(context, requirement, store, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets whom the organisation requires a second factor of — ADR-201 condition 2, off unless an administrator says so
    /// (owner decision 2026-10-19).
    /// </summary>
    /// <remarks>
    /// <b>The answer names who will be asked at their next sign-in</b>, because turning it on ends sign-in by password
    /// alone for them: <c>generateToken</c>, HTTP Basic and scripts signing in as them stop, and an API key cannot
    /// administer (ADR-200). Nobody is signed out by it; a session already open runs to its end.
    /// </remarks>
    private static async Task SetSecondFactorRequirementAsync(
        HttpContext context, SecondFactorRequirementRequest request, SecondFactorPolicy policy, PostgresSecondFactorStore store,
        IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (SecondFactorPolicy.Parse(request.Required) is not { } requirement)
        {
            await Refuse(context, 400, "'required' is one of off, administrators or everyone.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        await policy.WriteAsync(requirement, current.Principal.Id, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "secondfactor.requirement", SecondFactorPolicy.Name(requirement),
            AdminEndpoints.Detail(new { required = SecondFactorPolicy.Name(requirement) }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await RequirementJsonAsync(context, requirement, store, cancellation).ConfigureAwait(false);
    }

    private static readonly string[] RequirementChoices = ["off", "administrators", "everyone"];

    private static async Task RequirementJsonAsync(
        HttpContext context, SecondFactorRequirement requirement, PostgresSecondFactorStore store, CancellationToken cancellation)
    {
        IReadOnlyList<string> without = requirement == SecondFactorRequirement.Off
            ? []
            : await store.WithoutAsync(requirement == SecondFactorRequirement.Administrators, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            required = SecondFactorPolicy.Name(requirement),
            choices = RequirementChoices,
            askedAtNextSignIn = without,
            note = requirement == SecondFactorRequirement.Off
                ? "Nobody is required to have a second factor; a member may turn one on from their profile."
                : "A member it covers signs in with a code. One without a second factor sets one up at their next sign-in, "
                    + "in the console. Signing in by password alone — generateToken, HTTP Basic, scripts — stops for them, "
                    + "and an API key cannot administer. Accounts from an identity provider or a directory are not covered.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
