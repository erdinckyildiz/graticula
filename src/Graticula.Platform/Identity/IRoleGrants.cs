using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// What each role grants, read from wherever the deployment keeps it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A seam, because the answer stopped being a constant on 2026-08-18.</b>
/// <see cref="Roles.Grants"/> was the answer: a dictionary compiled into this assembly. ADR-035
/// makes a deployment able to change what a role grants, so the answer moved into the store and this
/// interface is what stands where the constant did.
/// </para>
/// <para>
/// <b>It is deliberately not <c>IIdentityStore</c>'s job.</b> That interface answers questions about
/// a <em>principal</em> — who they are, which roles they hold, what their user type is. Role grants
/// are a property of the deployment rather than of anybody in it, they change on a different
/// schedule, and they are read on every request while a principal's roles are read once per session.
/// Folding them together would make one cache serve two lifetimes.
/// </para>
/// <para>
/// <b>The administrator is not answered from here.</b> ADR-035 §4b: an administrator passes every
/// privilege check without consulting stored grants, so no implementation of this interface can
/// widen or narrow what an administrator may do. Implementations still <em>report</em> the
/// administrator's grants, because the roles screen has to show them.
/// </para>
/// </remarks>
public interface IRoleGrants
{
    /// <summary>
    /// What one role grants right now.
    /// </summary>
    /// <param name="role">The role name.</param>
    /// <returns>Its privileges, empty when the role is unknown.</returns>
    /// <remarks>
    /// <b>Empty rather than throwing, for the reason <see cref="Roles.PrivilegesOf"/> gives.</b> A
    /// grant naming a role we do not know is a store written by a different version, and the safe
    /// reading of an unknown grant is that it confers nothing.
    /// </remarks>
    ImmutableHashSet<Privilege> PrivilegesOf(string role);

    /// <summary>
    /// Every role and what it grants, for the screen that edits them.
    /// </summary>
    /// <returns>Role name to privileges.</returns>
    ImmutableDictionary<string, ImmutableHashSet<Privilege>> All();

    /// <summary>
    /// Reads the grants again, discarding anything held.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <b>Called by the endpoint that writes, rather than waited out.</b> An administrator who has
    /// just revoked a privilege must not be told to wait for a cache — ADR-031 §2b makes the same
    /// argument for sharing, and a revocation is the direction where staleness is unsafe.
    /// </remarks>
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The grants this build was compiled with, for anything that has no store.
/// </summary>
/// <remarks>
/// <b>Not a fallback for a store that failed.</b> It is what the tests, the migration seed and any
/// non-Postgres path use, and it answers exactly what every build before ADR-035 answered. A
/// deployment whose store is unreachable does not silently fall back to this — that would mean a
/// revoked privilege coming back during an outage, which is the one direction ADR-018 refuses.
/// </remarks>
public sealed class CompiledRoleGrants : IRoleGrants
{
    /// <summary>The single instance; it holds nothing that can change.</summary>
    public static CompiledRoleGrants Instance { get; } = new();

    /// <inheritdoc/>
    public ImmutableHashSet<Privilege> PrivilegesOf(string role) => Roles.PrivilegesOf(role);

    /// <inheritdoc/>
    public ImmutableDictionary<string, ImmutableHashSet<Privilege>> All() => Roles.Grants;

    /// <inheritdoc/>
    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
