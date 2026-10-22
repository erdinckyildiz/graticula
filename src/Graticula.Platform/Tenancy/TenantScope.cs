using System;
using System.Threading;

namespace Graticula.Platform.Tenancy;

/// <summary>
/// The tenant the work in progress belongs to — ADR-204. A request's is the tenant its host name names; a job's is the
/// tenant that started it; anything else names none and is the system's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Carried by the execution context</b>, so it flows into every await and every task a request starts, and never
/// into one it did not. The catalogue's connections read it (<c>TenantDataSources</c>), and that is how a statement
/// reaches the database with its tenant without any statement naming it.
/// </para>
/// <para>
/// <b>No tenant means the system, not the default tenant.</b> A request always names one — the middleware sets it
/// before anything reads the catalogue — so the only work without one is the server's own, which reads every tenant's
/// rows as it did before tenants existed.
/// </para>
/// </remarks>
public static class TenantScope
{
    /// <summary>The tenant every deployment has, which every row written before tenants existed belongs to.</summary>
    public static readonly Guid DefaultTenant = new("10000000-0000-0000-0000-000000000001");

    private static readonly AsyncLocal<Guid?> Scope = new();

    private static int _tenants = 1;

    /// <summary>The tenant of the work in progress, or null for the system's.</summary>
    public static Guid? Current => Scope.Value;

    /// <summary>The tenant of the work in progress, or the default one where none is named.</summary>
    public static Guid Effective => Scope.Value ?? DefaultTenant;

    /// <summary>
    /// Whether the deployment holds more than one tenant — the only state in which a connection carries its tenant and
    /// the floor narrows what it reads.
    /// </summary>
    public static bool MultiTenant => Volatile.Read(ref _tenants) > 1;

    /// <summary>How many tenants the deployment holds, as last read from the catalogue.</summary>
    public static int Count => Volatile.Read(ref _tenants);

    /// <summary>Records how many tenants there are; the tenant directory calls it whenever it reads them.</summary>
    /// <param name="count">The number, at least one.</param>
    public static void SetCount(int count) => Volatile.Write(ref _tenants, Math.Max(1, count));

    /// <summary>Runs the rest of the current flow as <paramref name="tenant"/>'s, until the result is disposed.</summary>
    /// <param name="tenant">The tenant, or null for the system's work.</param>
    /// <returns>What restores the tenant there was before.</returns>
    public static IDisposable Enter(Guid? tenant)
    {
        Guid? before = Scope.Value;
        Scope.Value = tenant;
        return new Restore(before);
    }

    /// <summary>
    /// Runs a job as the tenant it was started in, where the deployment holds several — and as the system's work where it
    /// holds one, which is what every job was before tenants existed.
    /// </summary>
    /// <param name="tenant">The job's tenant, as its row records it.</param>
    /// <returns>What restores the tenant there was before.</returns>
    public static IDisposable EnterJob(Guid? tenant) => Enter(MultiTenant ? tenant : null);

    private sealed class Restore(Guid? before) : IDisposable
    {
        public void Dispose() => Scope.Value = before;
    }
}
