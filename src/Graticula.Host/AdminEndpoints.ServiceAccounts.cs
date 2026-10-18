using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Graticula.Platform.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Graticula.Host;

/// <summary>A service account asked for — ADR-200.</summary>
internal sealed record ServiceAccountRequest(string? Name, string? Description, string? UserType, string[]? Roles);

/// <summary>A key asked for: its name, and how many days it lives, or none for until revoked.</summary>
internal sealed record ApiKeyRequest(string? Name, int? ExpiresInDays);

/// <summary>A service account switched off or on.</summary>
internal sealed record ServiceAccountStateRequest(bool Disabled);

/// <summary>
/// Service accounts and their API keys — ADR-200, the row ADR-015 §5 decided and [D-10](../../docs/architecture-debt.md)
/// carried: *"API keys — service principals only — long-lived by nature, so scoped narrowly and revocable."*
/// </summary>
/// <remarks>
/// <para>
/// <b>Administrators' business, under <c>admin:manageSecurity</c></b>, as roles and identity providers are: a key is
/// a credential somebody else will hold, and who may make one is a security setting, not a content one.
/// </para>
/// <para>
/// <b>A key's text is answered once, when it is made, and never stored.</b> The store keeps its SHA-256, as a
/// session's; losing it means revoking it and making another.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex AccountName();

    private static void MapServiceAccounts(WebApplication app)
    {
        app.MapGet("/admin/service-accounts", ListServiceAccountsAsync);
        app.MapPost("/admin/service-accounts", CreateServiceAccountAsync);
        app.MapDelete("/admin/service-accounts/{name}", DeleteServiceAccountAsync);
        app.MapPut("/admin/service-accounts/{name}/state", SetServiceAccountStateAsync);
        app.MapGet("/admin/service-accounts/{name}/keys", ListApiKeysAsync);
        app.MapPost("/admin/service-accounts/{name}/keys", IssueApiKeyAsync);
        app.MapDelete("/admin/service-accounts/{name}/keys/{key:guid}", RevokeApiKeyAsync);

        // ADR-201: a member who lost both their phone and their recovery codes is let back in by an administrator.
        app.MapDelete("/admin/members/{name}/second-factor", RemoveMemberSecondFactorAsync);
    }

    private static async Task RemoveMemberSecondFactorAsync(
        HttpContext context, string name, IIdentityStore store, SecondFactors factors, IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers).ConfigureAwait(false))
        {
            return;
        }

        if (await store.FindForLoginAsync(name, cancellation).ConfigureAwait(false) is not { } found
            || !await factors.RemoveAsync(found.Principal.Id, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"'{name}' has no second factor.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "secondfactor.reset", name, Detail(new { }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = 204;
    }

    private static async Task ListServiceAccountsAsync(HttpContext context, IApiKeyStore keys, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<ServiceAccount> accounts = await keys.ListServiceAccountsAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            accounts = accounts.Select(a => new
            {
                name = a.Name,
                description = a.Description,
                userType = a.UserType,
                roles = a.Roles,
                created = a.CreatedAt,
                disabled = a.Disabled,
                liveKeys = a.Keys,
            }),
            note = "A service account is a machine: it signs in only with an API key, never owns an item, never "
                + "administers, and reaches the ArcGIS faces only — not this administration API.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateServiceAccountAsync(
        HttpContext context, ServiceAccountRequest request, IApiKeyStore keys, IAuditLog audit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        string name = request.Name?.Trim() ?? string.Empty;

        if (!AccountName().IsMatch(name))
        {
            await Refuse(context, 400, "'name' is letters, digits, '.', '_' and '-', up to 64 characters.").ConfigureAwait(false);
            return;
        }

        string userType = string.IsNullOrWhiteSpace(request.UserType) ? UserTypes.Creator : request.UserType.Trim();
        string[] roles = [.. (request.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal)];

        // ADR-015 §2: a machine never administers. Refused rather than dropped, so nobody believes they made one that does.
        if (roles.Contains(Roles.Administrator, StringComparer.Ordinal))
        {
            await Refuse(context, 400, "A service account cannot hold the administrator role: a machine never administers "
                + "(ADR-015 §2). Give it a role with the privileges its job needs.").ConfigureAwait(false);
            return;
        }

        ServiceAccount? made;

        try
        {
            made = await keys.CreateServiceAccountAsync(
                name, request.Description?.Trim(), userType, roles, context.Features.Get<RequestPrincipal>()!.Principal.Id,
                cancellation).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.ForeignKeyViolation)
        {
            await Refuse(context, 400, $"'{userType}' is not a user type, or one of the roles is not a role here. The roles "
                + "are listed at /admin/roles.").ConfigureAwait(false);
            return;
        }

        if (made is null)
        {
            await Refuse(context, 409, $"There is already an account named '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "serviceaccount.create", name, Detail(new { userType, roles }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        await Results.Json(new { name = made.Name, userType = made.UserType, roles = made.Roles }, statusCode: 201)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task DeleteServiceAccountAsync(
        HttpContext context, string name, IApiKeyStore keys, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (!await keys.DeleteServiceAccountAsync(name, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No service account '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "serviceaccount.delete", name, Detail(new { }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = 204;
    }

    private static async Task SetServiceAccountStateAsync(
        HttpContext context, string name, ServiceAccountStateRequest request, IApiKeyStore keys, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (!await keys.SetDisabledAsync(name, request.Disabled, cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"No service account '{name}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, request.Disabled ? "serviceaccount.disable" : "serviceaccount.enable", name,
            Detail(new { }), succeeded: true, cancellation).ConfigureAwait(false);
        await Results.Json(new { name, disabled = request.Disabled }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task ListApiKeysAsync(HttpContext context, string name, IApiKeyStore keys, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await keys.KeysAsync(name, cancellation).ConfigureAwait(false) is not { } listed)
        {
            await Refuse(context, 404, $"No service account '{name}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(new
        {
            account = name,
            keys = listed.Select(k => new
            {
                id = k.Id,
                name = k.Name,
                created = k.CreatedAt,
                expires = k.ExpiresAt,
                revoked = k.RevokedAt,
                lastUsed = k.LastUsedAt,
            }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task IssueApiKeyAsync(
        HttpContext context, string name, ApiKeyRequest request, IApiKeyStore keys, IAuditLog audit, TimeProvider time,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        string keyName = request.Name?.Trim() ?? string.Empty;

        if (keyName.Length is 0 or > 100)
        {
            await Refuse(context, 400, "'name' says what the key is for, up to 100 characters.").ConfigureAwait(false);
            return;
        }

        if (request.ExpiresInDays is { } days && (days < 1 || days > 3650))
        {
            await Refuse(context, 400, "'expiresInDays' is from 1 to 3650, or absent for a key that lives until revoked.")
                .ConfigureAwait(false);
            return;
        }

        string text = ApiKeyToken.Generate();
        DateTimeOffset? expires = request.ExpiresInDays is { } d ? time.GetUtcNow().AddDays(d) : null;

        if (await keys.IssueAsync(name, keyName, SessionToken.HashOf(text), expires, cancellation).ConfigureAwait(false)
            is not { } issued)
        {
            await Refuse(context, 409, $"No service account '{name}', or it already has a key named '{keyName}'.")
                .ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "apikey.issue", name, Detail(new { key = issued.Id, name = keyName, expires }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            id = issued.Id,
            name = keyName,
            expires,
            key = text,
            note = "This is the only time the key is shown; the server keeps its hash. Send it as an ArcGIS token — "
                + "token=, or X-Esri-Authorization: Bearer — or give it to the Maps SDK as apiKey and to the ArcGIS API "
                + "for Python as api_key.",
        }, statusCode: 201).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RevokeApiKeyAsync(
        HttpContext context, string name, Guid key, IApiKeyStore keys, IAuditLog audit, TimeProvider time,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (!await keys.RevokeAsync(name, key, time.GetUtcNow(), cancellation).ConfigureAwait(false))
        {
            await Refuse(context, 404, $"Service account '{name}' has no live key {key}.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "apikey.revoke", name, Detail(new { key }), succeeded: true, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = 204;
    }
}
