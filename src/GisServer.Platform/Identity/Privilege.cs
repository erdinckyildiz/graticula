using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace GisServer.Platform.Identity;

/// <summary>
/// What a principal may <em>do</em> (ADR-018 §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no read privilege, and its absence is the model.</b> ArcGIS
/// Portal grants <em>doing</em> through roles and governs <em>seeing</em>
/// through sharing, and ADR-018 §2 adopts that split. Whether a caller may read
/// a layer is computed from the layer's owner and scope — see
/// <see cref="SharingScope"/> — not from anything here.
/// </para>
/// <para>
/// <b>These names are ours, shaped to be recognisable.</b> Portal's wire
/// identifiers (<c>portal:user:createItem</c> and the rest) are a compatibility
/// vocabulary and live in the ArcGIS layer, because CLAUDE.md §51 keeps
/// compatibility adapters outside the core domain. Putting a third party's
/// vocabulary in the middle of the domain would make every future divergence a
/// breaking change to our core.
/// </para>
/// </remarks>
public enum Privilege
{
    /// <summary>Create and own items.</summary>
    ContentCreate,

    /// <summary>Publish a hosted feature layer.</summary>
    ContentPublishFeatures,

    /// <summary>Publish a hosted tile layer.</summary>
    ContentPublishTiles,

    /// <summary>
    /// Register a data source.
    /// </summary>
    /// <remarks>
    /// Under content rather than administration, which is Portal's placement.
    /// The risk note stands: registering hands the server a credential to
    /// somebody else's database, and every layer over it inherits that reach.
    /// </remarks>
    ContentRegisterDataStore,

    /// <summary>Edit features in layers shared with you.</summary>
    FeaturesEdit,

    /// <summary>Edit and delete regardless of editor tracking.</summary>
    FeaturesFullEdit,

    /// <summary>Share an item with the organisation.</summary>
    SharingShareToOrganization,

    /// <summary>
    /// Share an item publicly.
    /// </summary>
    /// <remarks>
    /// Separated from organisation sharing, as Portal separates them, and the
    /// most consequential entry in the set: it is the one that puts data on the
    /// internet. A deployment wanting publishing without public exposure
    /// withholds exactly this.
    /// </remarks>
    SharingShareToPublic,

    /// <summary>Create, disable and reassign principals.</summary>
    AdminManageMembers,

    /// <summary>Grant and revoke roles and user types.</summary>
    AdminManageRoles,

    /// <summary>See items regardless of their sharing scope.</summary>
    /// <remarks>
    /// ADR-018 condition 3: using this is auditable. An administrator reading a
    /// private layer is legitimate and must leave a record, or the sharing model
    /// is decorative.
    /// </remarks>
    AdminViewAllContent,

    /// <summary>Update, delete and reassign any item.</summary>
    AdminManageAllContent,

    /// <summary>Certificates, sessions, authentication settings.</summary>
    AdminManageSecurity,

    /// <summary>Migrations, pools, workers, pinning.</summary>
    AdminManageServer,
}

/// <summary>
/// Who may read an item (ADR-018 §3b).
/// </summary>
/// <remarks>
/// Stored as a string so that Portal's fourth scope — shared with a group — can
/// be added as a value rather than a migration. Groups are deliberately absent:
/// they are a real object with membership and ownership of their own, and are
/// not needed to make reading work.
/// </remarks>
public enum SharingScope
{
    /// <summary>The owner, and administrators holding view-all.</summary>
    Private,

    /// <summary>Any authenticated principal.</summary>
    Organization,

    /// <summary>Anyone, including anonymous.</summary>
    Public,
}

/// <summary>
/// The default roles, which are Portal's (ADR-018 §3c).
/// </summary>
public static class Roles
{
    /// <summary>Read what is shared with them. Carries no privileges at all.</summary>
    /// <remarks>
    /// <b>Empty is correct.</b> A viewer can read plenty; reading is simply not
    /// a privilege. Anyone who finds this surprising is holding the superseded
    /// model, where <c>layer.read</c> existed.
    /// </remarks>
    public const string Viewer = "viewer";

    /// <summary>Viewer, plus editing features in layers shared with them.</summary>
    public const string DataEditor = "data_editor";

    /// <summary>Viewer, plus creating and owning content.</summary>
    public const string User = "user";

    /// <summary>User, plus publishing and registering data sources.</summary>
    public const string Publisher = "publisher";

    /// <summary>Everything.</summary>
    public const string Administrator = "administrator";

    /// <summary>Every default role, in increasing order of authority.</summary>
    public static ImmutableArray<string> All { get; } =
        [Viewer, DataEditor, User, Publisher, Administrator];

    /// <summary>What each role grants, already flattened.</summary>
    public static ImmutableDictionary<string, ImmutableHashSet<Privilege>> Grants { get; } =
        BuildGrants();

    /// <summary>What one role grants, or empty if it is not a role we know.</summary>
    /// <remarks>
    /// Empty rather than throwing. A grant naming an unknown role is a store
    /// written by a different version, and the safe reading of an unknown grant
    /// is that it confers nothing.
    /// </remarks>
    public static ImmutableHashSet<Privilege> PrivilegesOf(string role) =>
        Grants.TryGetValue(role, out ImmutableHashSet<Privilege>? privileges) ? privileges : [];

    /// <summary>A one-line description, for the <c>role</c> table and the admin API.</summary>
    public static string DescriptionOf(string role) => role switch
    {
        Viewer => "Read items shared with them. Reading is governed by sharing, not by privilege.",
        DataEditor => "Edit features in layers shared with them.",
        User => "Create and own content, and share it with the organisation.",
        Publisher => "Publish hosted layers, register data sources, and share publicly.",
        Administrator => "Administer members, roles, all content, and the server.",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a role in ADR-018 3c."),
    };

    private static ImmutableDictionary<string, ImmutableHashSet<Privilege>> BuildGrants()
    {
        // Viewer grants nothing. See the remarks on Viewer.
        ImmutableHashSet<Privilege> viewer = [];

        ImmutableHashSet<Privilege> dataEditor = viewer.Add(Privilege.FeaturesEdit);

        ImmutableHashSet<Privilege> user = viewer
            .Add(Privilege.ContentCreate)
            .Add(Privilege.SharingShareToOrganization);

        ImmutableHashSet<Privilege> publisher = user
            .Union(dataEditor)
            .Add(Privilege.ContentPublishFeatures)
            .Add(Privilege.ContentPublishTiles)
            .Add(Privilege.ContentRegisterDataStore)
            .Add(Privilege.FeaturesFullEdit)
            .Add(Privilege.SharingShareToPublic);

        ImmutableHashSet<Privilege> administrator = publisher
            .Add(Privilege.AdminManageMembers)
            .Add(Privilege.AdminManageRoles)
            .Add(Privilege.AdminViewAllContent)
            .Add(Privilege.AdminManageAllContent)
            .Add(Privilege.AdminManageSecurity)
            .Add(Privilege.AdminManageServer);

        return new Dictionary<string, ImmutableHashSet<Privilege>>(StringComparer.Ordinal)
        {
            [Viewer] = viewer,
            [DataEditor] = dataEditor,
            [User] = user,
            [Publisher] = publisher,
            [Administrator] = administrator,
        }.ToImmutableDictionary(StringComparer.Ordinal);
    }
}

/// <summary>
/// A ceiling on what any role may confer on a principal (ADR-018 §3a).
/// </summary>
/// <remarks>
/// <para>
/// Portal's user type is a licensing tier. <b>We have no licences to meter and
/// enforce the ceiling anyway</b>, which needs a reason under CLAUDE.md §6, and
/// the reason is Q-16.
/// </para>
/// <para>
/// When migration imports a deployment where a member holds the Publisher role
/// and a Viewer user type, the source system gave them viewing only. An import
/// that keeps the role and drops the ceiling grants rights the original
/// withheld — <b>silent privilege escalation during migration</b>, which is the
/// worst kind of import bug, because nobody re-audits a system they believe they
/// copied.
/// </para>
/// <para>
/// It costs nothing in a fresh install: <see cref="Unrestricted"/> contains
/// every privilege and is the default.
/// </para>
/// </remarks>
public static class UserTypes
{
    /// <summary>The default: no ceiling at all.</summary>
    public const string Unrestricted = "unrestricted";

    /// <summary>Read only, whatever role is held.</summary>
    public const string Viewer = "viewer";

    /// <summary>Read and edit, but never publish or administer.</summary>
    public const string Editor = "editor";

    /// <summary>Everything a publisher does, but no administration.</summary>
    public const string Creator = "creator";

    /// <summary>Every user type, in increasing order of ceiling.</summary>
    public static ImmutableArray<string> All { get; } =
        [Viewer, Editor, Creator, Unrestricted];

    /// <summary>What each type permits.</summary>
    public static ImmutableDictionary<string, ImmutableHashSet<Privilege>> Ceilings { get; } =
        BuildCeilings();

    /// <summary>
    /// What one type permits.
    /// </summary>
    /// <remarks>
    /// <b>An unknown user type permits nothing</b>, which is the opposite of the
    /// choice made for an unknown role — and deliberately so. A ceiling that
    /// fails open is not a ceiling; an unrecognised one must clamp rather than
    /// vanish.
    /// </remarks>
    public static ImmutableHashSet<Privilege> CeilingOf(string userType) =>
        Ceilings.TryGetValue(userType, out ImmutableHashSet<Privilege>? ceiling) ? ceiling : [];

    /// <summary>A one-line description.</summary>
    public static string DescriptionOf(string userType) => userType switch
    {
        Unrestricted => "No ceiling. Whatever the assigned roles grant.",
        Viewer => "May only read, whatever role is assigned.",
        Editor => "May read and edit features, but never publish or administer.",
        Creator => "May do everything a publisher does, but never administer.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(userType), userType, "Not a user type in ADR-018 3a."),
    };

    private static ImmutableDictionary<string, ImmutableHashSet<Privilege>> BuildCeilings()
    {
        ImmutableHashSet<Privilege> everything = [.. Enum.GetValues<Privilege>()];

        ImmutableHashSet<Privilege> viewer = [];
        ImmutableHashSet<Privilege> editor = [Privilege.FeaturesEdit];

        ImmutableHashSet<Privilege> creator = editor
            .Add(Privilege.ContentCreate)
            .Add(Privilege.ContentPublishFeatures)
            .Add(Privilege.ContentPublishTiles)
            .Add(Privilege.ContentRegisterDataStore)
            .Add(Privilege.FeaturesFullEdit)
            .Add(Privilege.SharingShareToOrganization)
            .Add(Privilege.SharingShareToPublic);

        return new Dictionary<string, ImmutableHashSet<Privilege>>(StringComparer.Ordinal)
        {
            [Viewer] = viewer,
            [Editor] = editor,
            [Creator] = creator,
            [Unrestricted] = everything,
        }.ToImmutableDictionary(StringComparer.Ordinal);
    }
}

/// <summary>
/// What one principal may do, after the ceiling has been applied.
/// </summary>
public sealed class Authorization
{
    /// <summary>A principal with nothing at all.</summary>
    public static readonly Authorization Nothing =
        new(UserTypes.Viewer, [], []);

    private Authorization(
        string userType, IEnumerable<string> roles, IEnumerable<Privilege> privileges)
    {
        UserType = userType;
        Roles = [.. roles];
        Privileges = [.. privileges];
    }

    /// <summary>
    /// Resolves the intersection of the user type's ceiling and the roles' grants.
    /// </summary>
    /// <param name="userType">The assigned user type.</param>
    /// <param name="roles">The assigned role names.</param>
    /// <remarks>
    /// ADR-018 §3a. The intersection, not the union — and the confusing failure
    /// this creates is <em>"I granted publisher and they still cannot publish"</em>,
    /// which is why <see cref="WithheldByUserType"/> exists and why refusals name
    /// it.
    /// </remarks>
    public static Authorization Resolve(string userType, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(userType);
        ArgumentNullException.ThrowIfNull(roles);

        string[] granted = [.. roles];
        HashSet<Privilege> fromRoles = [];

        foreach (string role in granted)
        {
            fromRoles.UnionWith(Identity.Roles.PrivilegesOf(role));
        }

        ImmutableHashSet<Privilege> ceiling = UserTypes.CeilingOf(userType);

        return new Authorization(userType, granted, fromRoles.Intersect(ceiling));
    }

    /// <summary>The assigned user type.</summary>
    public string UserType { get; }

    /// <summary>The assigned role names.</summary>
    public ImmutableArray<string> Roles { get; }

    /// <summary>What survived the intersection.</summary>
    public ImmutableHashSet<Privilege> Privileges { get; }

    /// <summary>Whether this principal holds a privilege.</summary>
    public bool Allows(Privilege privilege) => Privileges.Contains(privilege);

    /// <summary>
    /// Whether a role granted this privilege and the user type took it away.
    /// </summary>
    /// <remarks>
    /// Exists so a refusal can say <em>your role grants this and your user type
    /// does not permit it</em> rather than <em>you do not have this</em>. The
    /// second sends an administrator to grant a role they have already granted.
    /// </remarks>
    public bool WithheldByUserType(Privilege privilege)
    {
        if (Privileges.Contains(privilege))
        {
            return false;
        }

        foreach (string role in Roles)
        {
            if (Identity.Roles.PrivilegesOf(role).Contains(privilege))
            {
                return true;
            }
        }

        return false;
    }
}
