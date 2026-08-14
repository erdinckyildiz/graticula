using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace GisServer.Platform.Identity;

/// <summary>
/// What a principal may do (ADR-018 §2a).
/// </summary>
/// <remarks>
/// <para>
/// <b>The names are load-bearing.</b> They appear in refusal messages, so an
/// operator reads them in a log and a support conversation. Renaming one is a
/// change to what people see, not a refactor.
/// </para>
/// <para>
/// An enum rather than strings at the call site: a typo in a permission check is
/// otherwise a check that silently never matches, which fails open.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification =
        "CA1711 reserves the 'Permission' suffix for Code Access Security types deriving from "
        + "CodeAccessPermission. CAS was removed in .NET Core, so the collision the rule protects "
        + "against cannot occur. Renaming to Capability would make the type disagree with "
        + "ADR-018 §2a, which is the document a reader arrives from.")]
public enum Permission
{
    /// <summary>Read a published layer.</summary>
    LayerRead,

    /// <summary>Create and own hosted content.</summary>
    LayerPublishHosted,

    /// <summary>
    /// Register a data source.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LayerPublishHosted"/> because they are different
    /// risks wearing the same word. Publishing hosted data puts a file in our
    /// datastore; registering a source hands the server a credential to somebody
    /// else's database, and every layer over it inherits that reach.
    /// </remarks>
    DataSourceRegister,

    /// <summary>Publish a layer over a registered data source.</summary>
    LayerPublishRegistered,

    /// <summary>Override an owner's sharing decision.</summary>
    SharingOverride,

    /// <summary>Create, disable and edit principals.</summary>
    PrincipalManage,

    /// <summary>Grant and revoke roles.</summary>
    RoleGrant,

    /// <summary>List and terminate other principals' sessions.</summary>
    SessionManage,

    /// <summary>
    /// Migrations, certificates, and other server operations.
    /// </summary>
    /// <remarks>
    /// Platform administrator only, because a migration can close the rollback
    /// window (ADR-016 §4a) and that is not a GIS decision.
    /// </remarks>
    ServerOperate,
}

/// <summary>
/// The fixed role set (ADR-018 §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fixed in v1, custom roles deferred rather than refused.</b> A custom role
/// is an expression over a permission catalogue, and the moment customers write
/// roles against that catalogue it becomes a public contract that cannot be
/// renamed or split without breaking their grants. Nine permissions derived from
/// three endpoints is not a vocabulary worth freezing.
/// </para>
/// <para>
/// <b>The names here and the rows in the <c>role</c> table are two copies of one
/// fact.</b> They are kept in step by <c>RoleSeedTests</c>, which reads the
/// migration; if they drift, a grant names a role the server does not know and
/// the principal silently loses every permission it carried.
/// </para>
/// </remarks>
public static class Roles
{
    /// <summary>Read published layers.</summary>
    public const string Viewer = "viewer";

    /// <summary>Viewer, plus create and own hosted content.</summary>
    public const string Publisher = "publisher";

    /// <summary>Publisher, plus register sources and override sharing.</summary>
    public const string GisAdministrator = "gis-administrator";

    /// <summary>Everything.</summary>
    public const string PlatformAdministrator = "platform-administrator";

    /// <summary>
    /// What each role carries, already flattened.
    /// </summary>
    /// <remarks>
    /// <b>The nesting in ADR-018 §2 is expanded here rather than resolved at
    /// check time.</b> A check that walks a hierarchy is a check that can be
    /// asked about a role not in the hierarchy, and the answer to that question
    /// should not be computed — it should be absent.
    /// </remarks>
    public static ImmutableDictionary<string, ImmutableHashSet<Permission>> Grants { get; } =
        BuildGrants();

    /// <summary>Every role name, in increasing order of authority.</summary>
    public static ImmutableArray<string> All { get; } =
        [Viewer, Publisher, GisAdministrator, PlatformAdministrator];

    /// <summary>What one role carries, or empty if it is not a role we know.</summary>
    /// <remarks>
    /// Empty rather than throwing. A grant naming an unknown role is a store
    /// written by a different version, and the safe reading of an unknown grant
    /// is that it confers nothing.
    /// </remarks>
    public static ImmutableHashSet<Permission> PermissionsOf(string role) =>
        Grants.TryGetValue(role, out ImmutableHashSet<Permission>? permissions)
            ? permissions
            : [];

    /// <summary>A one-line description, for the <c>role</c> table and the admin API.</summary>
    public static string DescriptionOf(string role) => role switch
    {
        Viewer => "Read published layers.",
        Publisher => "Create and own hosted content, and read published layers.",
        GisAdministrator =>
            "Register data sources, publish over them, override sharing, and everything a "
            + "publisher may do.",
        PlatformAdministrator =>
            "Administer principals, roles, sessions and the server itself, and everything a GIS "
            + "administrator may do.",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a role in ADR-018 §2."),
    };

    private static ImmutableDictionary<string, ImmutableHashSet<Permission>> BuildGrants()
    {
        ImmutableHashSet<Permission> viewer = [Permission.LayerRead];

        ImmutableHashSet<Permission> publisher = viewer.Add(Permission.LayerPublishHosted);

        ImmutableHashSet<Permission> gisAdministrator = publisher
            .Add(Permission.DataSourceRegister)
            .Add(Permission.LayerPublishRegistered)
            .Add(Permission.SharingOverride);

        ImmutableHashSet<Permission> platformAdministrator = gisAdministrator
            .Add(Permission.PrincipalManage)
            .Add(Permission.RoleGrant)
            .Add(Permission.SessionManage)
            .Add(Permission.ServerOperate);

        return new Dictionary<string, ImmutableHashSet<Permission>>(StringComparer.Ordinal)
        {
            [Viewer] = viewer,
            [Publisher] = publisher,
            [GisAdministrator] = gisAdministrator,
            [PlatformAdministrator] = platformAdministrator,
        }.ToImmutableDictionary(StringComparer.Ordinal);
    }
}

/// <summary>
/// What one principal may do, resolved from their grants.
/// </summary>
/// <remarks>
/// Computed once per request rather than per check. The alternative — asking the
/// store on each check — makes the cost of an authorization check depend on how
/// many the handler happens to make, which is the sort of thing that quietly
/// discourages checking.
/// </remarks>
public sealed class Authorization
{
    /// <summary>A principal with no grants at all.</summary>
    public static readonly Authorization Nothing = new([], []);

    /// <summary>Creates a resolved set.</summary>
    /// <param name="roles">The role names granted.</param>
    /// <param name="permissions">What they add up to.</param>
    public Authorization(IEnumerable<string> roles, IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(permissions);

        Roles = [.. roles];
        Permissions = [.. permissions];
    }

    /// <summary>Resolves the permissions a set of role names carries.</summary>
    public static Authorization FromRoles(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        string[] granted = [.. roles];
        HashSet<Permission> permissions = [];

        foreach (string role in granted)
        {
            permissions.UnionWith(Identity.Roles.PermissionsOf(role));
        }

        return new Authorization(granted, permissions);
    }

    /// <summary>The role names granted.</summary>
    public ImmutableArray<string> Roles { get; }

    /// <summary>What they add up to.</summary>
    public ImmutableHashSet<Permission> Permissions { get; }

    /// <summary>Whether this principal holds a permission.</summary>
    public bool Allows(Permission permission) => Permissions.Contains(permission);
}
