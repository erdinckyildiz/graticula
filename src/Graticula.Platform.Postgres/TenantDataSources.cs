using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Tenancy;
using Npgsql;

namespace Graticula.Platform.Postgres;

/// <summary>
/// The catalogue connection a statement runs on, carrying <c>graticula.tenant</c> where the deployment holds more than
/// one tenant and the work names one — ADR-204 §5b.
/// </summary>
/// <remarks>
/// <para>
/// <b>One pool; the tenant is sent ahead of each statement — ADR-204 condition 14, measured 2026-10-31.</b> Until then
/// each tenant had a pool of its own, a share <c>max(2, 24/n)</c> of the request pool, whose every connection started
/// with <c>-c graticula.tenant=…</c>. Measured on a quiet machine with a Release build
/// (benchmarks/tenant-http/RESULTS.md, *Clean run*), varying one thing at a time at ten organisations: turning the
/// floor off changed nothing, turning the admission shares off changed nothing, and giving each organisation's pool
/// the full 24 took the busy organisation's query p95 from 31 ms to 9 at 4 callers. The pools were the cost — the busy
/// organisation queued behind its own two connections. The replacement is the one the condition named: every statement
/// of a tenant goes to the one pool with <c>SET graticula.tenant = '…'</c> in the same batch ahead of it.
/// </para>
/// <para>
/// <b>Why a statement can still not forget.</b> The stores reach the catalogue only through <see cref="TenantSource"/>,
/// whose two members — <see cref="TenantSource.CreateCommand"/> and <see cref="TenantSource.OpenConnectionAsync"/> —
/// are the only ways a statement is made, and each sets the tenant before anything the caller wrote runs. A connection
/// returned to the pool is reset by Npgsql (<c>RESET ALL</c> among it) before its next use, so a statement that names no
/// tenant never inherits one.
/// </para>
/// <para>
/// <b>A deployment of one tenant never routes</b>: its statements run as they always ran, naming no tenant, and the
/// floor's policy reads every row for such a connection. Nothing changes until a second tenant exists.
/// </para>
/// </remarks>
public static class TenantDataSources
{
    /// <summary>The roots that route by tenant — one per platform pool the host registers.</summary>
    private static readonly ConcurrentDictionary<NpgsqlDataSource, bool> Roots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Makes <paramref name="root"/> route by tenant; called where the platform pools are built.</summary>
    /// <param name="root">The pool every statement of the store runs on.</param>
    public static void Register(NpgsqlDataSource root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Roots[root] = true;
    }

    /// <summary>Registers <paramref name="root"/> to route by tenant (<see cref="Register"/>) and returns it.</summary>
    /// <param name="root">The platform pool.</param>
    /// <returns><paramref name="root"/>.</returns>
    public static NpgsqlDataSource RoutedByTenant(this NpgsqlDataSource root)
    {
        Register(root);
        return root;
    }

    /// <summary>
    /// Where a statement on <paramref name="root"/> is made now: carrying the tenant where the deployment holds more
    /// than one and the work names one, otherwise as it always was.
    /// </summary>
    /// <param name="root">The store's own data source.</param>
    /// <returns>The source to make the statement on.</returns>
    public static TenantSource Route(NpgsqlDataSource root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return TenantScope.MultiTenant && TenantScope.Current is { } tenant && Roots.ContainsKey(root)
            ? new TenantSource(root, tenant)
            : new TenantSource(root, null);
    }

    /// <summary>Where a tenant's statements are made, for a caller that holds the tenant itself.</summary>
    /// <param name="root">The store's own data source.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The source, carrying the tenant where the root routes.</returns>
    public static TenantSource For(NpgsqlDataSource root, Guid tenant)
    {
        using IDisposable scope = TenantScope.Enter(tenant);
        return Route(root);
    }
}

/// <summary>
/// A platform pool and the tenant its statements carry, or none — what <see cref="TenantDataSources.Route"/> gives a
/// store in place of the pool itself.
/// </summary>
public readonly struct TenantSource
{
    private readonly NpgsqlDataSource _root;
    private readonly string? _set;
    private readonly Guid? _tenant;

    /// <summary>Makes the source.</summary>
    /// <param name="root">The pool.</param>
    /// <param name="tenant">The tenant every statement carries, or null for none.</param>
    public TenantSource(NpgsqlDataSource root, Guid? tenant)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        // A Guid formatted by us, so a literal is safe; SET takes no parameter.
        _tenant = tenant;
        _set = tenant is { } t ? $"SET graticula.tenant = '{t:D}';\n" : null;
    }

    /// <summary>The pool itself, carrying no tenant — what a caller that is no tenant's (a migration, a test) passes.</summary>
    /// <param name="root">The pool.</param>
    public static implicit operator TenantSource(NpgsqlDataSource root) => new(root, null);

    /// <summary>The tenant's id, or null where this source carries none.</summary>
    public Guid? Tenant => _tenant;

    /// <summary>The pool statements go to.</summary>
    public NpgsqlDataSource Pool => _root;

    /// <summary>
    /// A connectionless command on the pool whose batch sets the tenant ahead of <paramref name="commandText"/>.
    /// </summary>
    /// <param name="commandText">The statement.</param>
    /// <returns>The command; a reader, scalar or row count is that of <paramref name="commandText"/>, because a
    /// <c>SET</c> returns no rows and counts none.</returns>
    public NpgsqlCommand CreateCommand(string commandText) =>
        _root.CreateCommand(_set is null ? commandText : _set + commandText);

    /// <summary>A connection from the pool, the tenant set on it before it is returned to the caller.</summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection; closing it returns it to the pool, which resets it.</returns>
    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await _root.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (_set is null)
        {
            return connection;
        }

        try
        {
            await using NpgsqlCommand set = new(_set, connection);
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
