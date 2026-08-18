using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>What a role is, and what happened when somebody tried to change it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its one-line description.</param>
/// <param name="Privileges">What it grants.</param>
/// <param name="BuiltIn">Whether it is one of ADR-018 §3c's five.</param>
/// <param name="Members">How many members hold it.</param>
public sealed record RoleGrant(
    string Name,
    string Description,
    ImmutableHashSet<Privilege> Privileges,
    bool BuiltIn,
    int Members);

/// <summary>Why a role could not be created, edited or removed.</summary>
public enum RoleChange
{
    /// <summary>It happened.</summary>
    Done,

    /// <summary>There is no role by that name.</summary>
    Absent,

    /// <summary>A role by that name already exists.</summary>
    Exists,

    /// <summary>
    /// The administrator's grants are not data — ADR-035 §4b.
    /// </summary>
    /// <remarks>
    /// The owner: *"Admin yetkisi değiştirilemez. Ve sınırlandırılamaz."* Refused here as well as
    /// short-circuited in the check, because a screen that lets somebody try and then fails is
    /// clearer than one that appears to succeed and changes nothing.
    /// </remarks>
    Administrator,

    /// <summary>
    /// One of ADR-018 §3c's five, whose *existence* is not editable even though its privileges are.
    /// </summary>
    /// <remarks>
    /// <b>Not in ADR-035's text; it follows from the seed.</b> Migration 25 writes the five built-in
    /// roles into a fresh store, so one deleted here would come back on the next fresh install and
    /// the two stores would disagree about what exists. Separate from
    /// <see cref="Administrator"/> because reusing that outcome made the refusal for `publisher`
    /// open with a sentence about the administrator — right in substance, wrong in subject, and the
    /// operator reading it would go looking for a permissions problem they do not have.
    /// </remarks>
    BuiltIn,

    /// <summary>
    /// A privilege was granted without something it requires — ADR-035 §4e.
    /// </summary>
    MissingPrerequisite,

    /// <summary>A privilege name this build does not know.</summary>
    UnknownPrivilege,

    /// <summary>Members still hold it, so removing it would silently reduce what they may do.</summary>
    StillHeld,
}

/// <summary>
/// Reading and editing what each role grants.
/// </summary>
/// <remarks>
/// <para>
/// <b>A third port over the same store, and the reason is the one <c>IMemberDirectory</c> gives.</b>
/// Every request touches <see cref="IIdentityStore"/> to authenticate and <see cref="IRoleGrants"/>
/// to resolve; only <c>admin:manageRoles</c> touches this. Keeping them apart means the login path
/// has no route to editing a role.
/// </para>
/// <para>
/// <b>Both halves of every refusal are here rather than in the endpoint</b>, because ADR-035's rules
/// are about the store's contents and not about one API's manners. An endpoint is one caller; a
/// second one written later would have to remember all of them.
/// </para>
/// </remarks>
public interface IRoleDirectory
{
    /// <summary>Every role, what it grants, and how many members hold it.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The roles, in the order ADR-018 §3c lists the built-in ones, then the rest by name.</returns>
    Task<IReadOnlyList<RoleGrant>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates a role with a set of privileges.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="description">Its description.</param>
    /// <param name="privileges">What it grants.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and the missing prerequisite when that is why.</returns>
    Task<(RoleChange Outcome, string? Detail)> CreateAsync(
        string name,
        string description,
        IReadOnlyList<string> privileges,
        CancellationToken cancellationToken);

    /// <summary>Replaces what a role grants.</summary>
    /// <param name="name">The role.</param>
    /// <param name="privileges">Its new privileges, replacing all of them.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and the missing prerequisite when that is why.</returns>
    /// <remarks>
    /// <b>Replaces rather than adds, and the screen sends the whole set.</b> A patch API for this
    /// would make *"untick Delete"* and *"tick everything except Delete"* two different requests with
    /// the same intent, and the difference between them is where a race lives.
    /// </remarks>
    Task<(RoleChange Outcome, string? Detail)> SetPrivilegesAsync(
        string name,
        IReadOnlyList<string> privileges,
        CancellationToken cancellationToken);

    /// <summary>Removes a role no member holds.</summary>
    /// <param name="name">The role.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <b>Refused while anybody holds it.</b> Cascading would quietly reduce what those members may
    /// do, which is the same objection ADR-015 §6c makes to a member delete that reassigns silently:
    /// the operator should be told what is at stake and asked again.
    /// </remarks>
    Task<RoleChange> RemoveAsync(string name, CancellationToken cancellationToken);
}
