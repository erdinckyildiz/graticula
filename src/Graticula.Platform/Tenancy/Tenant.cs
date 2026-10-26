using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Tenancy;

/// <summary>An organisation the deployment holds — ADR-204.</summary>
/// <param name="Id">Its id, which every row it owns carries as <c>tenant_id</c>.</param>
/// <param name="Key">A short name for it, lower-case letters, digits and underscores.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Hosts">The host names it is reached at; for the default tenant, empty means every host no other claims.</param>
/// <param name="PortalId">Its organisation id as ArcGIS clients read it (<c>portals/self.id</c>).</param>
/// <param name="Suspended">Whether it is suspended, which answers every request at its hosts with 503.</param>
/// <param name="Description">What it says about itself, its own administrator's to write (ADR-221).</param>
public sealed record Tenant(
    Guid Id, string Key, string Name, IReadOnlyList<string> Hosts, string PortalId, bool Suspended, string? Description = null)
{
    /// <summary>Whether this is the tenant every deployment has.</summary>
    public bool IsDefault => Id == TenantScope.DefaultTenant;
}

/// <summary>Where the tenants are kept — the platform store's <c>tenant</c> table.</summary>
public interface ITenantStore
{
    /// <summary>Every tenant, the default first.</summary>
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Makes a tenant; null when its key or one of its hosts is taken.</summary>
    Task<Tenant?> CreateAsync(string key, string name, IReadOnlyList<string> hosts, CancellationToken cancellationToken);

    /// <summary>
    /// Changes what an organisation calls itself — its name and description, its own administrator's to change (ADR-221
    /// condition 3); null when there is no such tenant. Hosts and suspension are not touched: they are the system's.
    /// </summary>
    Task<Tenant?> DescribeAsync(string key, string name, string? description, CancellationToken cancellationToken);

    /// <summary>Changes a tenant's name, hosts or suspension; null when there is no such tenant.</summary>
    Task<Tenant?> UpdateAsync(string key, string? name, IReadOnlyList<string>? hosts, bool? suspended, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a tenant that owns nothing — no member, service or map; what it still owns, by kind, when it was refused.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a tenant and every row it owns in the catalogue — its members, groups, content and settings — in one
    /// transaction; false when there is no such tenant. Hosted tables in the datastore are not dropped.
    /// </summary>
    Task<bool> PurgeAsync(string key, CancellationToken cancellationToken);

    /// <summary>Whether the floor binds the role the store connects as — neither a superuser nor <c>BYPASSRLS</c>.</summary>
    Task<bool> FloorBindsAsync(CancellationToken cancellationToken);
}
