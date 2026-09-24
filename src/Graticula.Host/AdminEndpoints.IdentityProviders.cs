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
/// <param name="Kind"><c>oidc</c> when absent, or <c>ldap</c> for a directory — ADR-089.</param>
/// <param name="GroupsClaim">The claim an OpenID Connect provider lists groups in; <c>groups</c> when absent.</param>
/// <param name="UserBase">A directory's search base.</param>
/// <param name="UserFilter">A directory's filter for a person, <c>{0}</c> for the name typed.</param>
/// <param name="DisplayAttribute">A directory's attribute holding a name to show.</param>
/// <param name="GroupAttribute">A directory's attribute listing a person's groups.</param>
/// <param name="SubjectAttribute">A directory's attribute that never changes for a person, or empty for the DN.</param>
/// <param name="StartTls">Whether a directory's <c>ldap://</c> is upgraded with StartTLS.</param>
/// <param name="MetadataUrl">Where a SAML provider publishes its metadata — ADR-090.</param>
/// <param name="Metadata">A SAML provider's metadata document, uploaded where its URL cannot be reached.</param>
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
    bool? Enabled,
    string? Kind = null,
    string? GroupsClaim = null,
    string? UserBase = null,
    string? UserFilter = null,
    string? DisplayAttribute = null,
    string? GroupAttribute = null,
    string? SubjectAttribute = null,
    bool StartTls = false,
    string? MetadataUrl = null,
    string? Metadata = null);

/// <summary>A provider's group mappings as the admin surface takes them — ADR-089.</summary>
/// <param name="Mappings">Each: the directory's or provider's group, and the role and group here it gives.</param>
internal sealed record GroupMappingsRequest(IReadOnlyList<GroupMappingEntry>? Mappings);

/// <summary>One group mapping.</summary>
/// <param name="ExternalGroup">The group as the directory or provider names it.</param>
/// <param name="Role">The role it gives, or null.</param>
/// <param name="Group">The name of the group here its members join, or null.</param>
internal sealed record GroupMappingEntry(string? ExternalGroup, string? Role, string? Group);

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
        app.MapGet("/admin/identity-providers/{id:guid}/groups", GroupMappingsAsync);
        app.MapPut("/admin/identity-providers/{id:guid}/groups", SetGroupMappingsAsync);
    }

    private static async Task GroupMappingsAsync(
        HttpContext context, Guid id, IIdentityProviderStore store, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is null)
        {
            await Refuse(context, 404, $"No sign-in provider {id}.").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<GroupMapping> mappings = await store.MappingsAsync(id, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            mappings = mappings.Select(m => new { externalGroup = m.ExternalGroup, role = m.Role, group = m.GroupName }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces a provider's group mappings — ADR-089.</summary>
    /// <remarks>
    /// <b>A mapping to the administrator role is an administrator's to make</b>, as making an administrator by hand is
    /// (ADR-035 §4g): the mapping makes one at somebody's next sign-in, which is the same act at one remove.
    /// </remarks>
    private static async Task SetGroupMappingsAsync(
        HttpContext context,
        Guid id,
        GroupMappingsRequest request,
        IIdentityProviderStore store,
        IRoleDirectory roles,
        IGroupDirectory groups,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageSecurity).ConfigureAwait(false))
        {
            return;
        }

        if (await store.FindAsync(id, cancellation).ConfigureAwait(false) is not { } provider)
        {
            await Refuse(context, 404, $"No sign-in provider {id}.").ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        IReadOnlyList<RoleGrant> defined = await roles.ListAsync(cancellation).ConfigureAwait(false);
        IReadOnlyList<GroupSummary> known = await groups.ListAsync(current.Principal.Id, all: true, cancellation).ConfigureAwait(false);

        List<GroupMapping> mappings = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (GroupMappingEntry entry in request.Mappings ?? [])
        {
            string external = (entry.ExternalGroup ?? string.Empty).Trim();
            string? role = string.IsNullOrWhiteSpace(entry.Role) ? null : entry.Role.Trim();
            string? groupName = string.IsNullOrWhiteSpace(entry.Group) ? null : entry.Group.Trim();

            string? refusal = external.Length == 0
                ? "Each mapping names a group as the directory or provider calls it."
                : !seen.Add(external)
                    ? $"'{external}' is mapped twice; give it one role and one group."
                    : role is null && groupName is null
                        ? $"'{external}' gives neither a role nor a group, so it would do nothing."
                        : role is not null && !defined.Any(r => string.Equals(r.Name, role, StringComparison.Ordinal))
                            ? $"'{role}' is not a role on this server."
                            : groupName is not null && !known.Any(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase))
                                ? $"There is no group '{groupName}' here."
                                : null;

            if (refusal is not null)
            {
                await Refuse(context, 400, refusal).ConfigureAwait(false);
                return;
            }

            if (string.Equals(role, Roles.Administrator, StringComparison.Ordinal)
                && !await AdministratorOnlyAsync(context, "Mapping a group to the administrator role").ConfigureAwait(false))
            {
                return;
            }

            mappings.Add(new GroupMapping(
                external, role,
                groupName is null ? null : known.First(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)).Id));
        }

        await store.SetMappingsAsync(id, mappings, cancellation).ConfigureAwait(false);

        await AuditAsync(context, audit, "identityProvider.groups", provider.Settings.Name,
            Detail(new { mappings = mappings.Select(m => new { m.ExternalGroup, m.Role, m.GroupId }) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await GroupMappingsAsync(context, id, store, cancellation).ConfigureAwait(false);
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

            // ADR-090: what a SAML provider is given about this server.
            acsUrl = Graticula.Host.Saml.SamlEndpoints.AcsUrl(context),
            samlEntityId = Graticula.Host.Saml.SamlEndpoints.DefaultEntityId(context),
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

        if (await ProviderSettingsAsync(context, request, roles, null, cancellation).ConfigureAwait(false) is not { } settings)
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

        if (await ProviderSettingsAsync(context, request, roles, before, cancellation).ConfigureAwait(false) is not { } settings)
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
        HttpContext context,
        Guid id,
        IIdentityProviderStore store,
        OidcClient client,
        Graticula.Host.Ldap.LdapDirectory directory,
        CancellationToken cancellation)
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

        // ADR-090: a SAML provider is checked by reading its metadata again, from its URL when it has one.
        if (provider.Settings is { Kind: "saml", Saml: { } saml })
        {
            try
            {
                string metadata = saml.Metadata;

                if (saml.MetadataUrl.Length > 0)
                {
                    metadata = await Graticula.Host.Saml.SamlMetadata.FetchAsync(saml.MetadataUrl, cancellation).ConfigureAwait(false);
                    Graticula.Host.Saml.SamlMetadata.Describe(metadata);
                    await store.SetSamlMetadataAsync(provider.Id, metadata, DateTimeOffset.UtcNow, cancellation).ConfigureAwait(false);
                }

                Graticula.Host.Saml.SamlMetadata.Described idp = Graticula.Host.Saml.SamlMetadata.Describe(metadata);

                await Results.Json(new
                {
                    reachable = true,
                    saml = true,
                    entityId = idp.EntityId,
                    signOn = idp.SignOn.OriginalString,
                    certificates = idp.Certificates.Count,
                    expires = idp.Expires,
                }).ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (Graticula.Host.Saml.SamlException e)
            {
                await Refuse(context, 502, e.Message).ConfigureAwait(false);
            }

            return;
        }

        // ADR-089: a directory is checked by binding with its search account and reading its search base.
        if (provider.Settings.Kind == "ldap")
        {
            string? why;

            try
            {
                why = await directory.CheckAsync(provider, cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is System.DirectoryServices.Protocols.LdapException
                or System.DirectoryServices.Protocols.DirectoryException)
            {
                why = e.Message;
            }

            if (why is not null)
            {
                await Refuse(context, 502, why).ConfigureAwait(false);
                return;
            }

            await Results.Json(new { reachable = true, directory = true, userBase = provider.Settings.Ldap?.UserBase })
                .ExecuteAsync(context).ConfigureAwait(false);
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
        HttpContext context,
        IdentityProviderRequest request,
        IRoleDirectory roles,
        IdentityProvider? before,
        CancellationToken cancellation)
    {
        string name = (request.Name ?? string.Empty).Trim();
        string kind = string.IsNullOrWhiteSpace(request.Kind) ? "oidc" : request.Kind.Trim().ToLowerInvariant();
        bool directory = kind == "ldap";
        bool saml = kind == "saml";
        string issuer = (request.Issuer ?? string.Empty).Trim().TrimEnd('/');
        string clientId = (request.ClientId ?? string.Empty).Trim();

        // ADR-090: a SAML provider is its metadata, read from its URL now or taken as uploaded; its issuer is where
        // the metadata came from, and its client id is this server's entity id there.
        SamlSettings? samlSettings = null;
        string? samlRefusal = null;

        if (saml)
        {
            (samlSettings, samlRefusal) = await SamlSettingsAsync(request, before, cancellation).ConfigureAwait(false);

            if (samlSettings is not null)
            {
                issuer = samlSettings.MetadataUrl.Length > 0
                    ? samlSettings.MetadataUrl
                    : Graticula.Host.Saml.SamlMetadata.Describe(samlSettings.Metadata).EntityId;
                clientId = clientId.Length > 0 ? clientId : Graticula.Host.Saml.SamlEndpoints.DefaultEntityId(context);
            }
        }
        string userBase = (request.UserBase ?? string.Empty).Trim();
        string userFilter = string.IsNullOrWhiteSpace(request.UserFilter)
            ? "(&(objectClass=person)(|(sAMAccountName={0})(uid={0})(userPrincipalName={0})))"
            : request.UserFilter.Trim();
        string role = string.IsNullOrWhiteSpace(request.DefaultRole) ? Roles.Viewer : request.DefaultRole.Trim();
        string userType = string.IsNullOrWhiteSpace(request.DefaultUserType) ? UserTypes.Unrestricted : request.DefaultUserType.Trim();

        string? refusal = kind is not ("oidc" or "ldap" or "saml")
            ? "A sign-in provider is an OpenID Connect provider (oidc), an LDAP directory (ldap) or a SAML provider (saml)."
            : name.Length is 0 or > 100
            ? "A sign-in provider has a name of 1 to 100 characters: it is what the sign-in button says."
            : samlRefusal is not null
                ? samlRefusal
            : directory && Graticula.Host.Ldap.LdapDirectory.AddressRefusal(issuer, request.StartTls) is { } badAddress
                ? badAddress
            : kind == "oidc" && OidcClient.IssuerRefusal(issuer) is { } badIssuer
                ? badIssuer
                : kind == "oidc" && clientId.Length == 0
                    ? "A sign-in provider needs the client id this server was given there."
                    : directory && userBase.Length == 0
                        ? "A directory needs the base people are searched under, as in ou=people,dc=example,dc=org."
                        : directory && !userFilter.Contains("{0}", StringComparison.Ordinal)
                            ? "A directory's filter holds {0} where the name typed goes, as in (sAMAccountName={0})."
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
            string.IsNullOrWhiteSpace(request.UsernameClaim) ? (saml ? "NameID" : "preferred_username") : request.UsernameClaim.Trim(),
            request.AutoCreate,
            role,
            userType,
            request.Enabled ?? true,
            kind,
            string.IsNullOrWhiteSpace(request.GroupsClaim) ? (saml ? SamlGroupsAttribute : "groups") : request.GroupsClaim.Trim(),
            directory
                ? new LdapSettings(
                    userBase,
                    userFilter,
                    string.IsNullOrWhiteSpace(request.DisplayAttribute) ? "displayName" : request.DisplayAttribute.Trim(),
                    string.IsNullOrWhiteSpace(request.GroupAttribute) ? "memberOf" : request.GroupAttribute.Trim(),
                    (request.SubjectAttribute ?? string.Empty).Trim(),
                    request.StartTls)
                : null,
            samlSettings);
    }

    /// <summary>
    /// The attribute Entra ID lists a person's groups in, which is the default because most SAML providers asked about
    /// are Entra; AD FS sends <c>http://schemas.xmlsoap.org/claims/Group</c>, and Okta whatever the operator named.
    /// </summary>
    private const string SamlGroupsAttribute = "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups";

    /// <summary>
    /// A SAML provider's metadata as a request gives it — ADR-090: read from its URL now; else the document uploaded;
    /// else, for a provider already stored, the document it has. Null with the refusal when none can be used.
    /// </summary>
    private static async Task<(SamlSettings? Settings, string? Refusal)> SamlSettingsAsync(
        IdentityProviderRequest request, IdentityProvider? before, CancellationToken cancellation)
    {
        string url = (request.MetadataUrl ?? string.Empty).Trim();
        string display = (request.DisplayAttribute ?? string.Empty).Trim();

        try
        {
            if (url.Length > 0)
            {
                string fetched = await Graticula.Host.Saml.SamlMetadata.FetchAsync(url, cancellation).ConfigureAwait(false);
                Graticula.Host.Saml.SamlMetadata.Describe(fetched);
                return (new SamlSettings(url, fetched, DateTimeOffset.UtcNow, display), null);
            }

            if (!string.IsNullOrWhiteSpace(request.Metadata))
            {
                Graticula.Host.Saml.SamlMetadata.Describe(request.Metadata);
                return (new SamlSettings(string.Empty, request.Metadata, null, display), null);
            }

            if (before?.Settings.Saml is { } kept)
            {
                return (new SamlSettings(string.Empty, kept.Metadata, null, display), null);
            }

            return (null, "A SAML provider needs its metadata: the URL it publishes it at, or the file it gives.");
        }
        catch (Graticula.Host.Saml.SamlException e)
        {
            return (null, e.Message);
        }
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
        kind = provider.Settings.Kind,
        groupsClaim = provider.Settings.GroupsClaim,
        userBase = provider.Settings.Ldap?.UserBase,
        userFilter = provider.Settings.Ldap?.UserFilter,
        displayAttribute = provider.Settings.Ldap?.DisplayAttribute,
        groupAttribute = provider.Settings.Ldap?.GroupAttribute,
        subjectAttribute = provider.Settings.Ldap?.SubjectAttribute,
        startTls = provider.Settings.Ldap?.StartTls ?? false,
        saml = provider.Settings.Saml is { } saml ? DescribeSaml(provider, saml) : null,
    };

    /// <summary>What an operator reads about a SAML provider — ADR-090: whose metadata, until when its certificate holds.</summary>
    private static object DescribeSaml(IdentityProvider provider, SamlSettings saml)
    {
        Graticula.Host.Saml.SamlMetadata.Described? idp;

        try
        {
            idp = Graticula.Host.Saml.SamlMetadata.Describe(saml.Metadata);
        }
        catch (Graticula.Host.Saml.SamlException)
        {
            idp = null;
        }

        return new
        {
            metadataUrl = saml.MetadataUrl,
            fetchedAt = saml.FetchedAt,
            displayAttribute = saml.DisplayAttribute,
            entityId = idp?.EntityId,
            signOn = idp?.SignOn.OriginalString,
            certificateExpires = idp?.Expires,
            spMetadata = $"/rest/auth/saml/{provider.Id}/metadata",
        };
    }
}
