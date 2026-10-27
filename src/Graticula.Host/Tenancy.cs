using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Graticula.Platform.Tenancy;
using Graticula.Providers.PostGis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Graticula.Host;

/// <summary>The tenant a request reached, by its host name — ADR-204 §5a.</summary>
/// <param name="Tenant">The tenant.</param>
internal sealed record TenantFeature(Tenant Tenant);

/// <summary>A group of another tenant a member is in: whose it is, its name and title, and where the member stands.</summary>
internal sealed record GroupElsewhere(Tenant Tenant, string Name, string? Title, string Standing);

/// <summary>
/// The deployment's tenants as last read, which host names reach which, and whose home a member is — ADR-204.
/// </summary>
/// <remarks>
/// <b>Read at start and after every change made here</b>; a second node learns of a change at its next start or its next
/// change. Nothing here has run on two nodes, and ADR-204's state line names the refresh on <c>graticula_grants</c> as
/// the condition that closes that.
/// </remarks>
internal sealed class TenantDirectory(ITenantStore store, NpgsqlDataSource root) : IHostedService
{
    private IReadOnlyList<Tenant> _all =
        [new Tenant(TenantScope.DefaultTenant, "default", "Default", [], PortalIdentity.Current ?? "0000000000000000", false)];

    /// <summary>Every tenant, the default first.</summary>
    public IReadOnlyList<Tenant> All => Volatile.Read(ref _all);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Reads the tenants again, and tells the catalogue's connections how many there are.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Tenant> read;

        try
        {
            read = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            // A store not migrated to 100 yet: one tenant, as before.
            return;
        }

        if (read.Count == 0)
        {
            return;
        }

        Volatile.Write(ref _all, read);
        TenantScope.SetCount(read.Count);
        TenantDataSources.Forget();
    }

    /// <summary>
    /// The tenant a host name reaches: with one tenant, always it; otherwise the tenant that lists the host, or the
    /// default tenant where it lists none and no other claims the host — and null, for a 421, where nobody does.
    /// </summary>
    public Tenant? Resolve(string host)
    {
        IReadOnlyList<Tenant> all = All;

        if (all.Count == 1)
        {
            return all[0];
        }

        string name = host.Trim().ToLowerInvariant();

        if (all.FirstOrDefault(t => t.Hosts.Contains(name, StringComparer.Ordinal)) is { } claimed)
        {
            return claimed;
        }

        return all.FirstOrDefault(t => t.IsDefault) is { Hosts.Count: 0 } open ? open : null;
    }

    /// <summary>Whether a tenant lists this host name by name — <see cref="Claims(IReadOnlyList{Tenant}, string)"/>.</summary>
    public bool Claims(string host) => Claims(All, host);

    /// <summary>
    /// Whether a tenant lists this host name by name: the question a proxy issuing certificates on demand asks before
    /// it asks a certificate authority for one (<c>/healthz/certificate</c>).
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="Resolve"/>.</b> A default tenant that lists no host names answers every host, so asking it would
    /// say yes to any name a wildcard DNS record sends here — and every yes is a certificate requested, against a
    /// certificate authority's weekly limit for the whole domain. Only a name somebody wrote down is one.
    /// </remarks>
    internal static bool Claims(IReadOnlyList<Tenant> all, string host)
    {
        string[] name = PostgresTenantStore.Normalised([host]);
        return name.Length == 1 && all.Any(t => t.Hosts.Contains(name[0], StringComparer.Ordinal));
    }

    /// <summary>
    /// Each member's home and system flag as last read, for thirty seconds — so a deployment of several tenants does not
    /// spend a connection of the root pool on every authenticated request (condition 14).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, (Guid Tenant, bool SystemAdministrator)? Home)> _homes = new();

    /// <summary>A member's home tenant and whether they administer the system, read across every tenant.</summary>
    public async Task<(Guid Tenant, bool SystemAdministrator)?> HomeOfAsync(Guid principal, CancellationToken cancellationToken)
    {
        if (_homes.TryGetValue(principal, out var kept) && DateTimeOffset.UtcNow - kept.At < TimeSpan.FromSeconds(30))
        {
            return kept.Home;
        }

        (Guid Tenant, bool SystemAdministrator)? home = await ReadHomeAsync(principal, cancellationToken).ConfigureAwait(false);
        _homes[principal] = (DateTimeOffset.UtcNow, home);
        return home;
    }

    /// <summary>
    /// The groups of other tenants a member is in, that still admit other tenants' members — ADR-204 §5e item 17.4,
    /// ArcGIS Online's external groups on the member's own Groups page. Read on the root pool, because the floor hides
    /// another tenant's group and its membership rows from this one's; narrowed here to the member's own rows.
    /// </summary>
    public async Task<IReadOnlyList<GroupElsewhere>> GroupsElsewhereAsync(
        Guid principal, Guid here, CancellationToken cancellationToken)
    {
        if (All.Count < 2)
        {
            return [];
        }

        await using NpgsqlCommand command = root.CreateCommand(
            """
            select g.tenant_id, g.name, g.title,
                   case when g.owner_principal_id = @principal then 'owner' else m.membership end
              from sharing_group_member m
              join sharing_group g on g.id = m.group_id
             where m.principal_id = @principal
               and g.admits_other_tenants
               and g.tenant_id <> @here
             order by g.tenant_id, lower(coalesce(g.title, g.name))
            """);
        command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("here", here);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<GroupElsewhere> found = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (All.FirstOrDefault(t => t.Id == reader.GetGuid(0)) is { Suspended: false } tenant)
            {
                found.Add(new GroupElsewhere(
                    tenant, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            }
        }

        return found;
    }

    private async Task<(Guid Tenant, bool SystemAdministrator)?> ReadHomeAsync(Guid principal, CancellationToken cancellationToken)
    {
        // The root pool names no tenant, so the floor lets it read every member's row.
        await using NpgsqlCommand command = root.CreateCommand("select tenant_id, system_admin from principal where id = @id");
        command.Parameters.AddWithValue("id", principal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetBoolean(1))
            : null;
    }
}

/// <summary>
/// Where a request meets its tenant: the host's tenant chosen before anything reads the catalogue, a member of another
/// tenant made an outsider once they are known, and the system's acts kept to the system's administrator — ADR-204.
/// </summary>
internal static class TenantRouting
{
    /// <summary>
    /// The administration that is the machine's, not an organisation's — ADR-204 §5d: answered only to the system
    /// administrator where the deployment holds several tenants. Each prefix covers every route below it.
    /// </summary>
    internal static readonly string[] SystemOnly =
    [
        "/admin/tenants", "/admin/backups", "/admin/logs/server", "/admin/health", "/admin/usage", "/admin/routes",
        "/admin/settings/style-origins", "/admin/settings/geometry-service", "/admin/settings/ground",
    ];

    /// <summary>
    /// Exactly these, and not what is below them: the general settings. ~~And every change to a role~~ — an
    /// organisation makes and changes its own roles since ADR-204 §5e item 18, and the built-in ones are refused to
    /// every organisation while there are several (<c>AdminEndpoints.BuiltInRoleRefusal</c>).
    /// </summary>
    private static bool SystemOnlyExactly(HttpRequest request) =>
        request.Path.Equals("/admin/settings", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The administration that is an organisation's own — ADR-204 §5e item 13: every prefix here reaches rows the
    /// tenant floor keeps (`pg_policies`), or reads nothing that is anyone's. Each prefix covers every route below it;
    /// what <see cref="SystemOnly"/> names under one of these is still the system's.
    /// </summary>
    /// <remarks>
    /// <b>An allowlist beside the denylist, from the ArcGIS veteran's review.</b> With only <see cref="SystemOnly"/>,
    /// every administrative route added later was an organisation's by default, whether or not it reached anything the
    /// floor keeps. Now a route on neither list is refused to everyone but the system administrator while there are
    /// several organisations, and <c>EveryAdministrativeRouteIsClassifiedTests</c> fails the build until it is put on
    /// one — so the decision is made when the route is written, not found later.
    /// </remarks>
    internal static readonly string[] OrganisationOwn =
    [
        "/admin/services", "/admin/layers", "/admin/coverages", "/admin/featureservices", "/admin/hosted",
        "/admin/publish", "/admin/datasources", "/admin/groups", "/admin/members", "/admin/member-categories",
        "/admin/item-categories", "/admin/service-accounts", "/admin/identity-providers", "/admin/oauth",
        "/admin/security/second-factor", "/admin/domains", "/admin/relationships", "/admin/folders",
        "/admin/recycle-bin", "/admin/roles", "/admin/logs", "/admin/jobs", "/admin/settings/basemaps",
        "/admin/references", "/admin/tiling-schemes", "/admin/thumbnail", "/admin/generateToken", "/admin/partnerships", "/admin/organization",
        "/admin/search",
    ];

    /// <summary>Whether a request's administrative route is on either list.</summary>
    internal static bool IsClassified(HttpRequest request) =>
        !request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
        || IsSystemOnly(request)
        || OrganisationOwn.Any(prefix => request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a request is the system's to answer where the deployment holds several tenants.</summary>
    internal static bool IsSystemOnly(HttpRequest request) =>
        SystemOnly.Any(prefix => request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
        || SystemOnlyExactly(request);

    /// <summary>
    /// Why a change is refused because of what it is aimed at rather than its path — ADR-204, the console audit of
    /// 2026-10-22: a member of another organisation (their own organisation administers their account; a password set
    /// here signed them in at home), a system service (one for every organisation), or the managed datastore (shared by
    /// every organisation). Null when none applies. Reads are not refused here.
    /// </summary>
    private static async Task<string?> SharedOrForeignTargetAsync(HttpContext context, Tenant here)
    {
        HttpRequest request = context.Request;

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return null;
        }

        string[] parts = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        NpgsqlDataSource root = context.RequestServices.GetRequiredService<NpgsqlDataSource>();

        // /admin/members/{name}[/...]: a member whose home is another organisation.
        if (parts is ["admin", "members", var member, ..])
        {
            // The name as this organisation reads it (§5e item 16): bare, its own member; `organisation\name`, another's.
            await using NpgsqlCommand home = TenantDataSources.For(root, here.Id).CreateCommand(
                "select tenant_id from principal where id = graticula_principal_named(@name)");
            home.Parameters.AddWithValue("name", Uri.UnescapeDataString(member));

            if (await home.ExecuteScalarAsync(context.RequestAborted).ConfigureAwait(false) is Guid tenant && tenant != here.Id)
            {
                return $"'{Uri.UnescapeDataString(member)}' is a member of another organization, which administers their "
                    + "account; here they can only be added to or removed from a group that admits them.";
            }
        }

        // /admin/services/{name}/(start|stop|sharing|limits): a system service, one for every organisation.
        if (parts is ["admin", "services", var service, "start" or "stop" or "sharing" or "limits"])
        {
            await using NpgsqlCommand system = root.CreateCommand(
                "select exists (select 1 from system_service where lower(name) = lower(@name) and coalesce(lower(folder), '') = lower(@folder))");
            system.Parameters.AddWithValue("name", Uri.UnescapeDataString(service));
            system.Parameters.AddWithValue("folder", request.Query["folder"].ToString().Trim());

            if (await system.ExecuteScalarAsync(context.RequestAborted).ConfigureAwait(false) is true)
            {
                return "This is one of the server's own services, used by every organization on it; its system "
                    + "administrator starts, stops and shares it.";
            }
        }

        // /admin/datasources/{id}/quiesce: the managed datastore, shared by every organisation.
        if (parts is ["admin", "datasources", var id, "quiesce"] && Guid.TryParse(id, out Guid source))
        {
            await using NpgsqlCommand shared = root.CreateCommand("select is_datastore from data_source where id = @id");
            shared.Parameters.AddWithValue("id", source);

            if (await shared.ExecuteScalarAsync(context.RequestAborted).ConfigureAwait(false) is true)
            {
                return "The datastore is shared by every organization on this server; its system administrator pauses it.";
            }
        }

        return null;
    }

    /// <summary>Chooses the request's tenant by its host, or answers 421 or 503 when it reaches none it may.</summary>
    public static async Task ResolveAsync(HttpContext context, Func<Task> next)
    {
        TenantDirectory directory = context.RequestServices.GetRequiredService<TenantDirectory>();

        // A liveness or readiness probe is the machine's and comes by address: it reaches the system, never a tenant.
        if (context.Request.Path.StartsWithSegments("/healthz", StringComparison.Ordinal))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (directory.Resolve(context.Request.Host.Host) is not { } tenant)
        {
            await Results.Json(
                new { error = new { code = 421, message = $"No organisation on this server answers at '{context.Request.Host.Host}'.", details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status421MisdirectedRequest).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (tenant.Suspended)
        {
            await Results.Json(
                new { error = new { code = 503, message = $"'{tenant.Name}' is suspended by the system's administrator.", details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.Features.Set(new TenantFeature(tenant));

        using (TenantScope.Enter(tenant.Id))
        {
            await next().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After authentication: a member of another tenant becomes an outsider here, the system's administrator is said
    /// to be one, and a system-only act is refused to anybody else — where the deployment holds several tenants.
    /// </summary>
    /// <returns>The principal to go on with, or null when the request was answered.</returns>
    public static async Task<RequestPrincipal?> AfterAuthenticationAsync(HttpContext context, RequestPrincipal current)
    {
        if (!TenantScope.MultiTenant || context.Features.Get<TenantFeature>() is not { } here)
        {
            return current;
        }

        RequestPrincipal judged = current;

        if (!current.Principal.IsAnonymous
            && await context.RequestServices.GetRequiredService<TenantDirectory>()
                .HomeOfAsync(current.Principal.Id, context.RequestAborted).ConfigureAwait(false) is { } home)
        {
            bool system = home.SystemAdministrator && home.Tenant == here.Tenant.Id;

            judged = home.Tenant == here.Tenant.Id
                ? new RequestPrincipal(current.Principal, current.SessionId, current.Authorization, current.MustChangePassword)
                {
                    StoreWasUnreachable = current.StoreWasUnreachable,
                    TokenOutsideScope = current.TokenOutsideScope,
                    IsSystemAdministrator = system,
                }
                : new RequestPrincipal(current.Principal, current.SessionId, current.Authorization.AsOutsider(), mustChangePassword: false)
                {
                    StoreWasUnreachable = current.StoreWasUnreachable,
                    TokenOutsideScope = current.TokenOutsideScope,
                    HomeOrganisation = context.RequestServices.GetRequiredService<TenantDirectory>().All
                        .FirstOrDefault(t => t.Id == home.Tenant)?.Key,
                };
        }

        if (!judged.IsSystemAdministrator
            && await SharedOrForeignTargetAsync(context, here.Tenant).ConfigureAwait(false) is { } refusal)
        {
            await Results.Json(
                new { error = new { code = 403, message = refusal, details = Array.Empty<string>() } },
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        if (!judged.IsSystemAdministrator && !IsClassified(context.Request))
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 403,
                        message = "This administration has not been opened to an organization's administrator, so only the "
                            + "system administrator reaches it while the server holds several organizations (ADR-204).",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        if (IsSystemOnly(context.Request) && !judged.IsSystemAdministrator)
        {
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = 403,
                        message = "This is the system's administration, which only the system administrator reaches while the "
                            + "server holds several organisations (ADR-204).",
                        details = Array.Empty<string>(),
                    },
                },
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }

        return judged;
    }
}

/// <summary>The system administrator's tenant administration — ADR-204 §5d: <c>/admin/tenants</c>.</summary>
internal static class TenantEndpoints
{
    /// <summary>A tenant to make, and the first administrator it is given.</summary>
    internal sealed record CreateTenantRequest(string? Key, string? Name, string[]? Hosts, string? Administrator);

    /// <summary>A change to a tenant.</summary>
    internal sealed record UpdateTenantRequest(string? Name, string[]? Hosts, bool? Suspended);

    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/tenants", ListAsync);
        app.MapPost("/admin/tenants", CreateAsync);
        app.MapPut("/admin/tenants/{key}", UpdateAsync);
        app.MapDelete("/admin/tenants/{key}", DeleteAsync);
        app.MapPut("/admin/groups/{name}/tenants", AdmitAsync);
        app.MapPost("/admin/tenants/{key}/password", ReissueAsync);

        // A proxy's on-demand TLS asks here before it requests a certificate (Caddy's `ask`, with ?domain=): 200 when an
        // organisation lists the name, 404 when none does — so a new organisation's host needs no proxy change, and a
        // name nobody listed spends nothing of the certificate authority's limit. Under /healthz because the proxy asks
        // by address, which reaches no tenant, and because the answer is the machine's, not an organisation's.
        app.MapGet("/healthz/certificate", (string? domain, TenantDirectory directory) =>
            string.IsNullOrWhiteSpace(domain) ? Refused(400, "'domain' names the host a certificate would be issued for.")
            : directory.Claims(domain) ? Results.Ok(new { domain, issue = true })
            : Refused(404, $"No organisation on this server lists '{domain}'."));
    }

    /// <summary>A tenant's administrator to issue a new password for.</summary>
    internal sealed record ReissueRequest(string? Administrator);

    /// <summary>
    /// Issues a new password, shown once, for an administrator of a tenant — the way back when the first one was lost.
    /// The system administrator's act; the member must change it at their next sign-in.
    /// </summary>
    private static async Task<IResult> ReissueAsync(
        HttpContext context, string key, ReissueRequest request, TenantDirectory directory, IMemberDirectory members,
        IPasswordHasher hasher, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await SystemAsync(context, directory).ConfigureAwait(false))
        {
            return Refused(403, "Only the system administrator issues a password for an organisation's administrator.");
        }

        if (directory.All.FirstOrDefault(t => t.Key == key) is not { } tenant)
        {
            return Refused(404, $"No tenant '{key}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Administrator))
        {
            return Refused(400, "'administrator' names the member whose password is issued again.");
        }

        string password = IssuedPassword.Issue();
        bool set;

        // Inside the tenant, so a bare name is one of its own members and nobody else's (§5e item 16).
        using (TenantScope.Enter(tenant.Id))
        {
            set = await members.SetPasswordAsync(request.Administrator.Trim(), hasher.Hash(password), cancellation).ConfigureAwait(false);
        }

        if (!set)
        {
            return Refused(404, $"'{tenant.Name}' has no member called '{request.Administrator.Trim()}'.");
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "tenant.password", key, AdminEndpoints.Detail(new { administrator = request.Administrator.Trim() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { administrator = new { name = request.Administrator.Trim(), password, mustChange = true } });
    }

    /// <summary>Whether a group admits members of other tenants.</summary>
    /// <param name="AdmitsOtherTenants">Whether the group admits other organisations' members.</param>
    /// <param name="PartnersOnly">Whether only its organisation's partners' — ADR-219 §5.3; unchanged when absent.</param>
    internal sealed record AdmitRequest(bool AdmitsOtherTenants, bool? PartnersOnly = null);

    /// <summary>
    /// Marks a group as admitting other tenants' members — ADR-204 §5c: the tenant's administrator's act, and only where
    /// the deployment holds several tenants.
    /// </summary>
    private static async Task<IResult> AdmitAsync(
        HttpContext context, string name, AdmitRequest request, IGroupDirectory groups, IAuditLog audit, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (!current.Authorization.IsAdministrator)
        {
            return Refused(403, "Only this organisation's administrator lets a group admit members of other organisations.");
        }

        if (!TenantScope.MultiTenant)
        {
            return Refused(409, "This server holds one organisation, so there are no others for a group to admit.");
        }

        // ADR-219 §5.3: limited to partners, which a group holding members of other organisations refuses until they go —
        // asked <b>before anything is written</b>, so a refusal leaves the group as it was. It answered 409 once while
        // the switch beside it had already opened the group to every organisation (ux-designer's pass, 2026-10-26).
        if (request.AdmitsOtherTenants && request.PartnersOnly is true)
        {
            int strangers = await groups.StrangersAsync(name, cancellation).ConfigureAwait(false);

            if (strangers > 0)
            {
                return Refused(409, strangers == 1
                    ? $"'{name}' has a member of an organization that is not a partner of this one. Remove them before "
                        + "limiting the group to partners."
                    : $"'{name}' has {strangers} members of organizations that are not partners of this one. Remove them "
                        + "before limiting the group to partners.");
            }
        }

        if (await groups.SetAdmitsOtherTenantsAsync(name, request.AdmitsOtherTenants, cancellation).ConfigureAwait(false) != GroupChange.Done)
        {
            return Refused(404, $"No group '{name}'.");
        }

        if (request.AdmitsOtherTenants && request.PartnersOnly is bool only
            && (await groups.SetPartnersOnlyAsync(name, only, cancellation).ConfigureAwait(false)).Change == GroupChange.Exists)
        {
            // Somebody added a stranger between the two statements; the group admits others as it was asked to.
            return Refused(409, $"A member of an organization that is not a partner was added to '{name}' meanwhile. "
                + "Remove them before limiting the group to partners.");
        }

        bool partnersOnly = await groups.AdmitsPartnersOnlyAsync(name, cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(
            context, audit, "group.tenants", name,
            AdminEndpoints.Detail(new { admitsOtherTenants = request.AdmitsOtherTenants, partnersOnly }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Ok(new { group = name, admitsOtherTenants = request.AdmitsOtherTenants, partnersOnly });
    }

    private static object Describe(Tenant tenant) => new
    {
        id = tenant.Id,
        key = tenant.Key,
        name = tenant.Name,
        hosts = tenant.Hosts,
        portalId = tenant.PortalId,
        suspended = tenant.Suspended,
        isDefault = tenant.IsDefault,
    };

    /// <summary>Whether the caller administers the system: the flag on their own row, at their own tenant's host.</summary>
    private static async Task<bool> SystemAsync(HttpContext context, TenantDirectory directory)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.IsSystemAdministrator)
        {
            return true;
        }

        if (current.Principal.IsAnonymous)
        {
            return false;
        }

        // One tenant: no outsider and no routing yet, so the row says it directly.
        return !TenantScope.MultiTenant
            && await directory.HomeOfAsync(current.Principal.Id, context.RequestAborted).ConfigureAwait(false) is { SystemAdministrator: true };
    }

    private static IResult Refused(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);

    private static async Task<IResult> ListAsync(HttpContext context, TenantDirectory directory, CancellationToken cancellation)
    {
        if (!await SystemAsync(context, directory).ConfigureAwait(false))
        {
            return Refused(403, "The tenants are the system administrator's to read.");
        }

        await directory.RefreshAsync(cancellation).ConfigureAwait(false);
        return Results.Ok(new { tenants = directory.All.Select(Describe), multiTenant = TenantScope.MultiTenant });
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context, CreateTenantRequest request, TenantDirectory directory, ITenantStore tenants,
        IMemberDirectory members, IPasswordHasher hasher, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await SystemAsync(context, directory).ConfigureAwait(false))
        {
            return Refused(403, "Only the system administrator makes a tenant.");
        }

        string key = (request.Key ?? string.Empty).Trim().ToLowerInvariant();
        string name = (request.Name ?? string.Empty).Trim();
        string? administrator = request.Administrator?.Trim();
        string[] hosts = PostgresTenantStore.Normalised(request.Hosts ?? []);

        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,30}$") || key == "default")
        {
            return Refused(400, "'key' is 1 to 31 lower-case letters, digits and underscores, starting with a letter, and not 'default'.");
        }

        if (name.Length == 0 || hosts.Length == 0 || string.IsNullOrEmpty(administrator))
        {
            return Refused(400, "A tenant needs a name, at least one host name it is reached at, and the name of its first administrator.");
        }

        // <b>Condition 9: never two tenants over a connection the floor does not bind.</b> A superuser or a BYPASSRLS
        // role reads every tenant's rows whatever the policies say, so the floor would protect nothing.
        if (!await tenants.FloorBindsAsync(cancellation).ConfigureAwait(false))
        {
            return Refused(409,
                "This server connects to its platform store as a superuser or a role with BYPASSRLS, which row-level "
                + "security does not bind, so a second organisation would not be kept apart from the first. Connect as a "
                + "serving role first — tools/serving-role.sql (ADR-204 condition 9).");
        }

        if (await tenants.CreateAsync(key, name, hosts, cancellation).ConfigureAwait(false) is not { } made)
        {
            return Refused(409, $"The key '{key}' or one of the host names is already another tenant's.");
        }

        await directory.RefreshAsync(cancellation).ConfigureAwait(false);

        // The first administrator is made inside the new tenant, so their row and their role are the tenant's.
        // <b>All or nothing</b>: a failure after the tenant was written removes it again, so a create that does not
        // answer 201 has made nothing and its password is never lost (the console's report, 2026-10-22).
        string password = IssuedPassword.Issue();
        Principal? first;

        try
        {
            using (TenantScope.Enter(made.Id))
            {
                first = await members.CreateMemberAsync(
                    administrator, null, hasher.Hash(password), Roles.Administrator, UserTypes.Unrestricted, cancellation)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            await tenants.PurgeAsync(key, CancellationToken.None).ConfigureAwait(false);
            await directory.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (first is null)
        {
            await tenants.PurgeAsync(key, cancellation).ConfigureAwait(false);
            await directory.RefreshAsync(cancellation).ConfigureAwait(false);
            // A new organisation holds nobody, and a name is unique only within one (§5e item 16), so this is not a
            // clash with another organisation's member; it is said as what it is.
            return Refused(409, $"The first administrator '{administrator}' could not be made in the new organization; nothing was made.");
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "tenant.create", key, AdminEndpoints.Detail(new { name, hosts, administrator }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.Json(
            new { tenant = Describe(made), administrator = new { name = administrator, password, mustChange = true } },
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context, string key, UpdateTenantRequest request, TenantDirectory directory, ITenantStore tenants,
        IAuditLog audit, CancellationToken cancellation)
    {
        if (!await SystemAsync(context, directory).ConfigureAwait(false))
        {
            return Refused(403, "Only the system administrator changes a tenant.");
        }

        if (key == "default" && request.Suspended == true)
        {
            return Refused(400, "The default organisation cannot be suspended: the system's administration is reached through it.");
        }

        if (await tenants.UpdateAsync(key, request.Name, request.Hosts, request.Suspended, cancellation).ConfigureAwait(false) is not { } changed)
        {
            return Refused(404, $"No tenant '{key}', or one of the host names is already another tenant's.");
        }

        await directory.RefreshAsync(cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "tenant.update", key, AdminEndpoints.Detail(request), succeeded: true, cancellation)
            .ConfigureAwait(false);

        return Results.Ok(Describe(changed));
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context, string key, TenantDirectory directory, ITenantStore tenants, IAuditLog audit,
        NpgsqlDataSource root, PostgresLayerCatalog layers, LayerConnections connections, PostGisImporter importer,
        RecycleBinService recycle, SourceQuiesce quiesce, CancellationToken cancellation)
    {
        if (!await SystemAsync(context, directory).ConfigureAwait(false))
        {
            return Refused(403, "Only the system administrator removes a tenant.");
        }

        if (key == "default")
        {
            return Refused(400, "The default organisation is the deployment's own and is never removed.");
        }

        // <b>Purge</b>: the tenant and every row it owns, at the system administrator's explicit word.
        if (string.Equals(context.Request.Query["purge"], "true", StringComparison.OrdinalIgnoreCase))
        {
            if (directory.All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal)) is not { IsDefault: false } doomed)
            {
                return Refused(404, $"No tenant '{key}'.");
            }

            Holdings held = await HoldingsOfAsync(root, layers, recycle, doomed.Id, cancellation).ConfigureAwait(false);

            // ADR-207, as every other removal of a registered layer asks it (D-316): a history trigger left on a
            // customer's table would record to nobody once the organisation that switched it on is gone.
            List<string> uncheckedLayers = [];

            if (await AdminEndpoints.RegisteredKeepsRefusalAsync(
                    context, connections, held.Layers, l => $"Layer '{l.Definition.Name}' of '{key}'",
                    $"removed and '{key}' was kept", uncheckedLayers, cancellation)
                .ConfigureAwait(false))
            {
                return Results.Empty;
            }

            foreach (PublishedLayer hosted in held.Layers.Where(l => l.Definition.IsHosted))
            {
                if (quiesce.Holding(hosted.ConnectionString) is { } outOfService)
                {
                    throw new SourceQuiescedException(SourceQuiesce.Says(outOfService), outOfService.Until);
                }
            }

            if (!await tenants.PurgeAsync(key, cancellation).ConfigureAwait(false))
            {
                return Refused(404, $"No tenant '{key}'.");
            }

            (List<string> dropped, List<string> failed) = await DropHostedAsync(importer, recycle, held, cancellation)
                .ConfigureAwait(false);

            await directory.RefreshAsync(cancellation).ConfigureAwait(false);
            await AdminEndpoints.AuditAsync(context, audit, "tenant.purge", key,
                AdminEndpoints.Detail(new { key, dropped, failed, @unchecked = uncheckedLayers }), succeeded: true, cancellation)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                deleted = key,
                purged = true,
                dropped,
                failed,
                @unchecked = uncheckedLayers,
                note = failed.Count == 0
                    ? $"Its hosted data went with it: {dropped.Count} table(s) and view(s) dropped, the recycle bin's included."
                    : $"Its rows are gone, and {failed.Count} hosted table(s) could not be dropped: {string.Join("; ", failed)}",
            });
        }

        IReadOnlyList<string> owned = await tenants.DeleteAsync(key, cancellation).ConfigureAwait(false);

        if (owned.Count > 0)
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = 409,
                        message = $"'{key}' still holds {string.Join(", ", owned)}; nothing was removed.",
                        // What it holds, one entry each, for a screen to list; the API's way past it is ?purge=true.
                        details = owned.ToArray(),
                    },
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        await directory.RefreshAsync(cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "tenant.delete", key, AdminEndpoints.Detail(new { key }), succeeded: true, cancellation).ConfigureAwait(false);
        return Results.Ok(new { deleted = key });
    }

    /// <summary>What a purge reaches outside the organisation's catalogue rows — D-316.</summary>
    /// <param name="Layers">Its layers.</param>
    /// <param name="Services">Its services, to tell a view's from a table's.</param>
    /// <param name="Everyone">Every organisation's layers, so a table another one also serves is kept.</param>
    /// <param name="Bin">Its recycle bin, whose tables wait in the recycle schema.</param>
    private sealed record Holdings(
        IReadOnlyList<PublishedLayer> Layers, IReadOnlyList<PublishedService> Services, IReadOnlyList<PublishedLayer> Everyone,
        IReadOnlyList<RecycleEntry> Bin);

    /// <summary>
    /// An organisation's layers, services and recycle bin, chosen by the tenant its rows carry — never by entering its
    /// scope and listing.
    /// </summary>
    /// <remarks>
    /// <b>The floor is not trusted to choose.</b> A store connected as a superuser, or a role with <c>BYPASSRLS</c>, is not
    /// narrowed by it, and a list taken "as the tenant" would then be every tenant's — and a purge drops the hosted tables
    /// of what it lists. So the ids come from the root pool by <c>tenant_id</c>, and the catalogue's listings, read as the
    /// system's work, are filtered by them.
    /// </remarks>
    private static async Task<Holdings> HoldingsOfAsync(
        NpgsqlDataSource root, PostgresLayerCatalog layers, RecycleBinService recycle, Guid tenant, CancellationToken cancellation)
    {
        HashSet<Guid> layerIds = await IdsAsync(root, "layer", tenant, cancellation).ConfigureAwait(false);
        HashSet<Guid> serviceIds = await IdsAsync(root, "service", tenant, cancellation).ConfigureAwait(false);
        HashSet<Guid> entryIds = await IdsAsync(root, "recycle_entry", tenant, cancellation).ConfigureAwait(false);

        using (TenantScope.Enter(null))
        {
            IReadOnlyList<PublishedLayer> everyone = await layers.ListAsync(cancellation).ConfigureAwait(false);
            IReadOnlyList<PublishedService> services = await layers.ListServicesAsync(cancellation).ConfigureAwait(false);
            IReadOnlyList<RecycleEntry> bin = await recycle.Bin.ListAsync(cancellation).ConfigureAwait(false);

            return new Holdings(
                [.. everyone.Where(l => layerIds.Contains(l.Id))],
                [.. services.Where(s => serviceIds.Contains(s.Id))],
                everyone,
                [.. bin.Where(e => entryIds.Contains(e.Id))]);
        }
    }

    private static async Task<HashSet<Guid>> IdsAsync(NpgsqlDataSource root, string table, Guid tenant, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = root.CreateCommand($"select id from {table} where tenant_id = @tenant");
        command.Parameters.AddWithValue("tenant", tenant);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        HashSet<Guid> ids = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>
    /// Drops a purged organisation's hosted data — owner decision 2026-10-27 (D-316), as a service's delete drops its own
    /// (ADR-034 §5k): its recycle bin's waiting tables, its views, then its tables. A table a layer of another
    /// organisation also serves is kept. Each failure is named and the rest go on, because the rows are already gone and
    /// stopping would leave more behind, not less.
    /// </summary>
    private static async Task<(List<string> Dropped, List<string> Failed)> DropHostedAsync(
        PostGisImporter importer, RecycleBinService recycle, Holdings held, CancellationToken cancellation)
    {
        List<string> dropped = [];
        List<string> failed = [];

        foreach (RecycleEntry entry in held.Bin)
        {
            try
            {
                await recycle.PurgeAsync(entry, cancellation).ConfigureAwait(false);
                dropped.Add($"recycle bin: {entry.Title}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add($"recycle bin: {entry.Title}: {e.Message}");
            }
        }

        HashSet<Guid> views = [.. held.Services.Where(s => s.ViewOf is not null).Select(s => s.Id)];
        HashSet<Guid> theirs = [.. held.Layers.Select(l => l.Id)];

        foreach (PublishedLayer layer in held.Layers.Where(l => l.Definition.IsHosted).OrderBy(l => views.Contains(l.ServiceId) ? 0 : 1))
        {
            bool view = views.Contains(layer.ServiceId);
            string name = $"{layer.Definition.SchemaName}.{layer.Definition.TableName}";

            if (dropped.Contains(name)
                || held.Everyone.Any(other => !theirs.Contains(other.Id)
                    && string.Equals(other.Definition.SchemaName, layer.Definition.SchemaName, StringComparison.Ordinal)
                    && string.Equals(other.Definition.TableName, layer.Definition.TableName, StringComparison.Ordinal)
                    && string.Equals(other.ConnectionString, layer.ConnectionString, StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                if (view)
                {
                    await importer.DropViewAsync(layer.Definition.TableName, cancellation).ConfigureAwait(false);
                }
                else
                {
                    await importer.DropAsync(layer.Definition.SchemaName, layer.Definition.TableName, cancellation).ConfigureAwait(false);
                }

                dropped.Add(name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add($"{name}: {e.Message}");
            }
        }

        return (dropped, failed);
    }
}
