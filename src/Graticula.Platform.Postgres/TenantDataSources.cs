using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Graticula.Platform.Tenancy;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The catalogue connection a statement runs on: the tenant's, carrying <c>graticula.tenant</c>, where the deployment
/// holds more than one tenant and the work names one — ADR-204 §5b.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per-tenant pools and not a <c>set_config</c> before every statement.</b> ADR-204's prototype carried the
/// tenant in a batch ahead of each statement, over one pool. The stores here run three hundred statements as
/// connectionless commands (<c>NpgsqlDataSource.CreateCommand</c>), which no hook reaches, and rewriting every one of
/// them is the change most likely to forget one. A data source whose connections start with <c>-c graticula.tenant=…</c>
/// cannot forget: every statement on it is floored. The cost is a pool per tenant, which ADR-204 measured as costing the
/// busy tenant queue time; each tenant's pool is kept to a share of the request pool's ceiling — INFERRED, condition 15.
/// </para>
/// <para>
/// <b>A deployment of one tenant never routes</b>: its statements run on the pool they always ran on, which names no
/// tenant, and the floor's policy reads every row for such a connection. Nothing changes until a second tenant exists.
/// </para>
/// </remarks>
public static class TenantDataSources
{
    /// <summary>The connection strings of the roots that route, by the root — one per platform pool.</summary>
    private static readonly ConcurrentDictionary<NpgsqlDataSource, (string ConnectionString, int Ceiling)> Roots =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>A pool per root per tenant: bounded by the platform pools (two) times the tenants the catalogue holds.</summary>
    private static readonly ConcurrentDictionary<(NpgsqlDataSource Root, Guid Tenant), NpgsqlDataSource> Pools = new();

    /// <summary>Makes <paramref name="root"/> route to its tenants' pools; called where the platform pools are built.</summary>
    /// <param name="root">The pool every statement of the store runs on today.</param>
    /// <param name="connectionString">Its connection string, password included — the pool's own hides it.</param>
    /// <param name="ceiling">The root pool's ceiling, which the tenants' pools share.</param>
    public static void Register(NpgsqlDataSource root, string connectionString, int ceiling)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Roots[root] = (connectionString, Math.Max(1, ceiling));
    }

    /// <summary>
    /// The data source a statement on <paramref name="root"/> runs on now: the tenant's where the deployment holds more
    /// than one and the work names one, otherwise the root itself.
    /// </summary>
    /// <param name="root">The store's own data source.</param>
    /// <returns>The data source to use.</returns>
    public static NpgsqlDataSource Route(NpgsqlDataSource root)
    {
        if (!TenantScope.MultiTenant || TenantScope.Current is not { } tenant || !Roots.TryGetValue(root, out var made))
        {
            return root;
        }

        return Pools.GetOrAdd((root, tenant), key => Build(made.ConnectionString, made.Ceiling, key.Tenant));
    }

    /// <summary>Registers <paramref name="root"/> to route by tenant (<see cref="Register"/>) and returns it.</summary>
    /// <param name="root">The platform pool.</param>
    /// <param name="connectionString">Its connection string, password included.</param>
    /// <param name="ceiling">Its ceiling.</param>
    /// <returns><paramref name="root"/>.</returns>
    public static NpgsqlDataSource RoutedByTenant(this NpgsqlDataSource root, string connectionString, int ceiling)
    {
        Register(root, connectionString, ceiling);
        return root;
    }

    /// <summary>The data source a tenant's statements run on, for a caller that holds the tenant itself.</summary>
    /// <param name="root">The store's own data source.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The tenant's data source, or the root where it does not route.</returns>
    public static NpgsqlDataSource For(NpgsqlDataSource root, Guid tenant)
    {
        using IDisposable scope = TenantScope.Enter(tenant);
        return Route(root);
    }

    /// <summary>Forgets every tenant pool, after a tenant was removed; the next statement builds what it needs.</summary>
    public static void Forget()
    {
        foreach (KeyValuePair<(NpgsqlDataSource Root, Guid Tenant), NpgsqlDataSource> pool in Pools)
        {
            if (Pools.TryRemove(pool.Key, out NpgsqlDataSource? gone))
            {
                gone.Dispose();
            }
        }
    }

    private static NpgsqlDataSource Build(string connectionString, int ceiling, Guid tenant)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);

        // The tenant is a startup option of every physical connection, so no statement can run on this pool without it,
        // and the pool's reset on return never clears it.
        builder.Options = $"{builder.Options} -c graticula.tenant={tenant:D}".Trim();
        // <b>The tenants share the request pool's ceiling; they do not each get one — condition 14, measured
        // 2026-10-22.</b> At max(4, …) ten tenants held 83 connections under 64 callers and the platform store's
        // breaker opened over and over: the budget the startup check counts (24) was exceeded by the pools it did not
        // count. A share of at least two, and idle connections let go in seconds, so a tenant nobody is using holds none.
        builder.MaxPoolSize = Math.Max(2, ceiling / Math.Max(1, TenantScope.Count));
        builder.MinPoolSize = 0;
        builder.ConnectionIdleLifetime = 10;
        builder.ConnectionPruningInterval = 5;
        string named = $"{builder.ApplicationName}:{tenant:N}";
        builder.ApplicationName = named.Length <= 63 ? named : named[..63];

        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }
}
