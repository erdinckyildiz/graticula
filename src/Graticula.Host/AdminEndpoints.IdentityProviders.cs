using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Oidc;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>A sign-in provider as the admin surface takes it.</summary>
/// <param name="Name">What the sign-in button says.</param>
/// <param name="Issuer">The provider's issuer URL.</param>
/// <param name="ClientId">This server's client id there.</param>
/// <param name="ClientSecret">This server's client secret there; absent to keep the stored one.</param>
/// <param name="ClearSecret">True to remove the stored secret, for a public client.</param>
/// <param name="Scopes">Scopes asked for; <c>openid profile email</c> when absent.</param>
/// <param name="UsernameClaim">The claim that names an account; <c>preferred_username</c> when absent.</param>
/// <param name="AutoCreate">Whether a first sign-in with no account makes one.</param>
/// <param name="DefaultRole">That account's role.</param>
/// <param name="DefaultUserType">That account's user type.</param>
/// <param name="Enabled">Whether it is offered; true when absent.</param>
internal sealed record IdentityProviderRequest(
    string? Name,
    string? Issuer,
    string? ClientId,
    string? ClientSecret,
    bool ClearSecret,
    string? Scopes,
    string? UsernameClaim,
    bool AutoCreate,
    string? DefaultRole,
    string? DefaultUserType,
    bool? Enabled);

/// <summary>
/// The OpenID Connect providers people sign in through — ADR-088, set from the console by owner decision.
/// </summary>
/// <remarks>
/// <b><c>admin:manageSecurity</c>, as registered apps are</b> (ADR-076): who may sign people in is the same question
/// as which apps may, and a provider added by the wrong person is a way in for everybody it names.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapIdentityProviders(WebApplication app)
    {
        app.MapGet("/admin/identity-providers", ListIdentityProvidersAsync);
        app.MapPost("/admin/identity-providers", CreateIdentityProviderAsync);
        app.MapPut("/admin/identity-providers/{id:guid}", UpdateIdentityProviderAsync);
        app.MapDelete("/admin/identity-providers/{id:guid}", DeleteIdentityProviderAsync);
        app.MapPost("/admin/identity-providers/{id:guid}/check", CheckIdentityProviderAsync);
    }

    private static async Task ListIdentityProvidersAsync(
        HttpContext context, IIdentityProviderStore store, IRoleDirectory roles, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<IdentityProvider> all = await store.ListAsync(cancellation).ConfigureAwait(false);
        IReadOnlyList<RoleGrant> defined = await roles.ListAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            // What the operator registers at the provider, said once for all of them.
            redirectUri = OidcEndpoints.RedirectUri(context),
            roles = defined.Select(r => r.Name).ToArray(),
            userTypes = UserTypes.All.ToArray(),
            providers = all.Select(DescribeProvider).ToArray(),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateIdentityProviderAsync(
        HttpContext context,
        IdentityProviderRequest request,
        IIdentityProviderStore store,
        IRoleDirectory roles,
        SecretProtector protector,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await ProviderSettingsAsync(context, request, roles, cancellation).ConfigureAwait(false) is not { } settings)
        {
            return;
        }

        (byte[], int)? secret = string.IsNullOrEmpty(request.ClientSecret)
            ? null
            : (protector.Protect(request.ClientSecret), protector.KeyVersion);

        if (await store.CreateAsync(settings, secret, cancellation).ConfigureAwait(false) is not { } created)
        {
            await Refuse(context, 409, $"There is already a sign-in provider named '{settings.Name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "identityProvider.create", settings.Name,
            Detail(new { settings.Issuer, settings.AutoCreate, settings.DefaultRole, secret = secret is not null }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(DescribeProvider(created), statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task UpdateIdentityProviderAsync(
        HttpContext context,
        Guid id,
        IdentityProviderRequest request,
        IIdentityProviderStore store,
        IRoleDirectory roles,
        SecretProtector protector,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { } before)
        {
            await Refuse(context, 404, $"No sign-in provider {id}.").ConfigureAwait(false);
            return;
        }

        if (await ProviderSettingsAsync(context, request, roles, cancellation).ConfigureAwait(false) is not { } settings)
        {
            return;
        }

        // <b>A secret is written only when one is sent</b>, so a form that never shows the stored one — which it
        // never does — cannot blank it by being saved. Clearing it is its own flag.
        (byte[], int)? secret = !string.IsNullOrEmpty(request.ClientSecret)
            ? (protector.Protect(request.ClientSecret), protector.KeyVersion)
            : null;

        bool stored = await store.UpdateAsync(id, settings, secret, cancellation, clearSecret: request.ClearSecret)
            .ConfigureAwait(false);

        if (!stored)
        {
            await Refuse(context, 409, $"There is already another sign-in provider named '{settings.Name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "identityProvider.update", settings.Name,
            Detail(new { from = before.Settings.Name, settings.Issuer, settings.AutoCreate, settings.DefaultRole, settings.Enabled,
                secret = request.ClearSecret ? "cleared" : secret is null ? "kept" : "replaced" }),
            succeeded: true, cancellation).ConfigureAwait(false);

        IdentityProvider now = await store.FindAsync(id, cancellation).ConfigureAwait(false) ?? before;

        await Results.Json(DescribeProvider(now)).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteIdentityProviderAsync(
        HttpContext context, Guid id, IIdentityProviderStore store, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { } provider)
        {
            await Refuse(context, 404, $"No sign-in provider {id}.").ConfigureAwait(false);
            return;
        }

        int accounts = await store.DeleteAsync(id, cancellation).ConfigureAwait(false);

        if (accounts > 0)
        {
            await Refuse(
                context, 409,
                $"{accounts} account{(accounts == 1 ? " signs" : "s sign")} in with {provider.Settings.Name}, so it is not "
                + "removed: they would have no way in. Turn it off instead, or remove those accounts first.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "identityProvider.delete", provider.Settings.Name, Detail(new { id }),
            succeeded: true, cancellation).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>Reads the provider's discovery document and keys, so an operator knows before anybody signs in.</summary>
    private static async Task CheckIdentityProviderAsync(
        HttpContext context, Guid id, IIdentityProviderStore store, OidcClient client, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { } provider)
        {
            await Refuse(context, 404, $"No sign-in provider {id}.").ConfigureAwait(false);
            return;
        }

        try
        {
            var configuration = await client.DiscoverAsync(provider.Settings.Issuer, cancellation, again: true)
                .ConfigureAwait(false);

            await Results.Json(new
            {
                reachable = true,
                issuer = configuration.Issuer,
                authorizationEndpoint = configuration.AuthorizationEndpoint,
                keys = configuration.SigningKeys.Count,
                pkce = configuration.CodeChallengeMethodsSupported.Contains("S256"),
            }).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (OidcException e)
        {
            await Refuse(context, 502, e.Message).ConfigureAwait(false);
        }
    }

    /// <summary>The settings a request describes, or null with the refusal written.</summary>
    private static async Task<IdentityProviderSettings?> ProviderSettingsAsync(
        HttpContext context, IdentityProviderRequest request, IRoleDirectory roles, CancellationToken cancellation)
    {
        string name = (request.Name ?? string.Empty).Trim();
        string issuer = (request.Issuer ?? string.Empty).Trim().TrimEnd('/');
        string clientId = (request.ClientId ?? string.Empty).Trim();
        string role = string.IsNullOrWhiteSpace(request.DefaultRole) ? Roles.Viewer : request.DefaultRole.Trim();
        string userType = string.IsNullOrWhiteSpace(request.DefaultUserType) ? UserTypes.Unrestricted : request.DefaultUserType.Trim();

        string? refusal = name.Length is 0 or > 100
            ? "A sign-in provider has a name of 1 to 100 characters: it is what the sign-in button says."
            : OidcClient.IssuerRefusal(issuer) is { } badIssuer
                ? badIssuer
                : clientId.Length == 0
                    ? "A sign-in provider needs the client id this server was given there."
                    : !UserTypes.All.Contains(userType, StringComparer.Ordinal)
                        ? $"'{userType}' is not a user type. They are {string.Join(", ", UserTypes.All)}."
                        : null;

        // Administrator is not a role an account gets by signing in: ADR-035 §4g makes it an administrator's act.
        if (refusal is null && string.Equals(role, Roles.Administrator, StringComparison.Ordinal))
        {
            refusal = "An account made at its first sign-in cannot be an administrator: making one is an "
                + "administrator's act. Choose another role, and raise the account's later.";
        }

        if (refusal is null)
        {
            IReadOnlyList<RoleGrant> defined = await roles.ListAsync(cancellation).ConfigureAwait(false);

            if (!defined.Any(r => string.Equals(r.Name, role, StringComparison.Ordinal)))
            {
                refusal = $"'{role}' is not a role on this server. It has {string.Join(", ", defined.Select(r => r.Name))}.";
            }
        }

        if (refusal is not null)
        {
            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return null;
        }

        return new IdentityProviderSettings(
            name,
            issuer,
            clientId,
            string.IsNullOrWhiteSpace(request.Scopes) ? "openid profile email" : request.Scopes.Trim(),
            string.IsNullOrWhiteSpace(request.UsernameClaim) ? "preferred_username" : request.UsernameClaim.Trim(),
            request.AutoCreate,
            role,
            userType,
            request.Enabled ?? true);
    }

    private static object DescribeProvider(IdentityProvider provider) => new
    {
        id = provider.Id,
        name = provider.Settings.Name,
        issuer = provider.Settings.Issuer,
        clientId = provider.Settings.ClientId,
        hasSecret = provider.HasSecret,
        scopes = provider.Settings.Scopes,
        usernameClaim = provider.Settings.UsernameClaim,
        autoCreate = provider.Settings.AutoCreate,
        defaultRole = provider.Settings.DefaultRole,
        defaultUserType = provider.Settings.DefaultUserType,
        enabled = provider.Settings.Enabled,
        accounts = provider.Accounts,
    };
}
