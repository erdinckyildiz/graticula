using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Graticula.Platform.Identity;

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

    /// <summary>Create a group. Whoever creates it owns it.</summary>
    /// <remarks>
    /// <b>ADR-035 §4c, from the owner:</b> *"grup oluşturma da bir yetki."* A group is a set of
    /// members with items shared to it — Portal's fourth sharing scope, deferred by ADR-018 §3b and
    /// undeferred here by the decision that role privileges are editable, because there was
    /// otherwise nowhere to put the privilege this needs to be.
    /// </remarks>
    GroupsCreate,

    /// <summary>Delete a group you own.</summary>
    /// <remarks>
    /// <b>Owning is not the same as being allowed to delete</b> — *"kendi grubunu silme de bir
    /// yetki."* A role may let somebody create groups and not remove them, which is the shape an
    /// organisation wants when a group is a shared asset rather than a personal folder.
    /// </remarks>
    GroupsDeleteOwn,

    /// <summary>Add and remove a group's members.</summary>
    /// <remarks>
    /// <b>This is the privilege the group-manager axis narrows.</b> Holding it does not confer it
    /// over every group: a principal may act on a group they own or manage. ADR-035 §4c.
    /// </remarks>
    GroupsManageMembers,

    /// <summary>Share an item you own with a group you belong to.</summary>
    /// <remarks>
    /// <b>Distinct from `sharing:shareToOrganization` and narrower.</b> Sharing to a group makes an
    /// item readable by that group's members and by nobody else — Portal calls the result
    /// *semiprivate*. A role that may share to a group need not be allowed to share to the whole
    /// organisation, and that is the ordinary case rather than an exotic one.
    /// </remarks>
    GroupsShareTo,
}

/// <summary>
/// Who may read an item (ADR-018 §3b).
/// </summary>
/// <remarks>
/// Stored as a string so that Portal's fourth scope — shared with a group — can
/// be added without redesigning this — though <b>not without a migration, which this
/// comment claimed until 2026-08-18</b>: all three tables carrying the column also carry a
/// check constraint listing exactly three values, so the fourth needs the check widened.
/// Expand-only and cheap; the wrong part was *no schema change*. Groups are deliberately absent:
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

    /// <summary>
    /// Readable by the members of a group it is shared with, and by nobody else.
    /// </summary>
    /// <remarks>
    /// <b>Portal's fourth scope, added 2026-08-18 by [ADR-036] on the owner's requirement.</b>
    /// ADR-018 §3b deferred it — *"adding them here would be adopting a subsystem to complete a
    /// table"* — and the subsystem now exists because every group operation has a privilege to hang
    /// from. Esri's word for the result is *semiprivate*: private to everybody except the people you
    /// named.
    /// </remarks>
    Group,
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

    /// <summary>
    /// The wire name of a privilege, which is now also its stored name.
    /// </summary>
    /// <param name="privilege">The privilege.</param>
    /// <returns>Its name, e.g. <c>content:publishFeatures</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Moved out of the host on 2026-08-18, because these became rows.</b> The mapping lived in
    /// <c>Authorize.Name</c> and was a presentation detail — how a refusal spells a privilege in
    /// JSON. ADR-035 stores role grants by name, so a name is now a value in the
    /// database and a value in a deployment's configuration: it belongs to the platform's
    /// vocabulary, not to one API's rendering of it.
    /// </para>
    /// <para>
    /// <b>These names are a contract from the first stored grant.</b> Q-59 predicted exactly this
    /// and was overruled; ADR-035 §2 lists what follows. Renaming one silently changes what an
    /// existing role confers, so <c>RolePrivilegeCatalogueTests</c> carries the expected list and
    /// fails the build when it moves.
    /// </para>
    /// </remarks>
    public static string NameOf(Privilege privilege) => privilege switch
    {
        Privilege.ContentCreate => "content:create",
        Privilege.ContentPublishFeatures => "content:publishFeatures",
        Privilege.ContentPublishTiles => "content:publishTiles",
        Privilege.ContentRegisterDataStore => "content:registerDataStore",
        Privilege.FeaturesEdit => "features:edit",
        Privilege.FeaturesFullEdit => "features:fullEdit",
        Privilege.SharingShareToOrganization => "sharing:shareToOrganization",
        Privilege.SharingShareToPublic => "sharing:shareToPublic",
        Privilege.GroupsCreate => "groups:create",
        Privilege.GroupsDeleteOwn => "groups:deleteOwn",
        Privilege.GroupsManageMembers => "groups:manageMembers",
        Privilege.GroupsShareTo => "groups:shareTo",
        Privilege.AdminManageMembers => "admin:manageMembers",
        Privilege.AdminManageRoles => "admin:manageRoles",
        Privilege.AdminViewAllContent => "admin:viewAllContent",
        Privilege.AdminManageAllContent => "admin:manageAllContent",
        Privilege.AdminManageSecurity => "admin:manageSecurity",
        Privilege.AdminManageServer => "admin:manageServer",
        _ => throw new ArgumentOutOfRangeException(
            nameof(privilege), privilege, "Not a privilege in ADR-018 §3a or ADR-035 §4c."),
    };

    /// <summary>What each privilege lets somebody do, in one sentence.</summary>
    /// <param name="privilege">The privilege.</param>
    /// <returns>The sentence, addressed to whoever is deciding who gets it.</returns>
    /// <remarks>
    /// <para>
    /// <b>[D-100](../../../docs/architecture-debt.md): the screen an administrator reads to
    /// decide who can do what did not say what anything does.</b> A role carries a sentence and
    /// the eighteen privileges under it were bare identifiers. The row's own defence was that the
    /// identifiers are unusually self-describing and that this stops being true at the first one
    /// that is not — naming `admin:manageSecurity` as arguably already that one.
    /// </para>
    /// <para>
    /// <b>Written for the person granting it, not for the person who wrote it.</b> Each says what
    /// the holder can reach, and where that is wider than the name suggests it says so: the
    /// difference between editing what is shared with you and editing regardless of who made it,
    /// between sharing to a group and sharing to the internet, between reading private content
    /// and changing it.
    /// </para>
    /// <para>
    /// <b>A switch rather than a dictionary, for the reason <see cref="NameOf"/> is one:</b> the
    /// compiler names any privilege that has been added and not described, and
    /// `RolePrivilegeCatalogueTests` asserts every one of them answers. The row's trigger was
    /// *the next privilege added*; this is what makes that arrive as a build failure rather than
    /// as a support question.
    /// </para>
    /// </remarks>
    public static string DescriptionOf(Privilege privilege) => privilege switch
    {
        Privilege.ContentCreate =>
            "Create items and own them. Everything else about content starts here.",

        Privilege.ContentPublishFeatures =>
            "Publish a hosted feature layer — upload data and turn it into a service this "
            + "server stores and serves.",

        Privilege.ContentPublishTiles =>
            "Publish a hosted tile layer, which is the same upload served as pre-cut tiles "
            + "instead of features.",

        Privilege.ContentRegisterDataStore =>
            "Register a database this server does not own. The credential is stored here and "
            + "every layer published over it reaches whatever that credential reaches.",

        Privilege.FeaturesEdit =>
            "Edit features in layers shared with you, subject to whatever the layer itself "
            + "allows.",

        Privilege.FeaturesFullEdit =>
            "Edit and delete any feature in those layers, including ones somebody else created. "
            + "Wider than it sounds: it is the difference between correcting your own work and "
            + "correcting everybody's.",

        Privilege.SharingShareToOrganization =>
            "Share an item with everybody signed in to this server.",

        Privilege.SharingShareToPublic =>
            "Share an item with anybody at all. This is the one that puts data on the internet, "
            + "and it is deliberately separate from sharing to the organisation.",

        Privilege.GroupsCreate =>
            "Create a group. Whoever creates one owns it.",

        Privilege.GroupsDeleteOwn =>
            "Delete a group you own. Owning one is not the same as being allowed to remove it, "
            + "which is why this is its own privilege.",

        Privilege.GroupsManageMembers =>
            "Add and remove a group's members — for groups you own or manage, not for every "
            + "group on the server.",

        Privilege.GroupsShareTo =>
            "Share an item you own with a group you belong to, making it readable by that group "
            + "and by nobody else.",

        Privilege.AdminManageMembers =>
            "Create members, disable them, and reassign what they own. Not enough on its own to "
            + "change what anybody is allowed to do.",

        Privilege.AdminManageRoles =>
            "Grant and revoke roles and user types, and change what a role confers. Somebody with "
            + "this decides what everybody else may do.",

        Privilege.AdminViewAllContent =>
            "Read every item regardless of who it is shared with. Using it is recorded in the "
            + "audit log, because an administrator reading private content is legitimate and has "
            + "to leave a trace.",

        Privilege.AdminManageAllContent =>
            "Update, delete and reassign any item on the server, including ones shared with "
            + "nobody.",

        Privilege.AdminManageSecurity =>
            "Change how people sign in and stay signed in: certificates, session lifetime, "
            + "password rules and the authentication settings. It does not read content, and it "
            + "can decide who reaches it.",

        Privilege.AdminManageServer =>
            "Operate the server itself — migrations, connection pools, background workers, "
            + "cache pinning and the operational screens that show what it is doing.",

        _ => throw new ArgumentOutOfRangeException(
            nameof(privilege), privilege, "Not a privilege in ADR-018 §3a or ADR-035 §4c."),
    };

    /// <summary>Every privilege, in the order the enum declares them.</summary>
    public static ImmutableArray<Privilege> AllPrivileges { get; } =
        [.. Enum.GetValues<Privilege>()];

    /// <summary>Name to privilege, for reading a stored grant.</summary>
    private static readonly ImmutableDictionary<string, Privilege> ByName =
        AllPrivileges.ToImmutableDictionary(NameOf, p => p, StringComparer.Ordinal);

    /// <summary>
    /// Reads a stored or submitted privilege name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="privilege">The privilege, when the name is one we know.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    /// <b>False rather than throwing, and the two callers want opposite things.</b> A name arriving
    /// from an API is refused with the name in the message, because an operator who mistyped needs
    /// to see what they typed. A name arriving from the store is <em>ignored and logged</em>: a row
    /// written by a newer version must not stop an older one from starting, which is the same
    /// direction of caution the schema handshake takes.
    /// </remarks>
    public static bool TryParsePrivilege(string? name, out Privilege privilege)
    {
        privilege = default;
        return name is not null && ByName.TryGetValue(name, out privilege);
    }

    /// <summary>
    /// Which privileges a privilege requires, without containing them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These were the shape of `BuildGrants()` and are now data — ADR-035 §4e, condition 6.</b>
    /// <c>publisher</c> held <c>content:create</c> because its set was built on <c>user</c>'s, not
    /// because anybody wrote it down; flattening the five sets into rows deleted that fact. So a
    /// role that grants a publish privilege without <c>content:create</c> is refused on write, with
    /// the missing name in the refusal.
    /// </para>
    /// <para>
    /// <b>Refused rather than auto-added, unlike the reference.</b> Esri auto-grants for version
    /// management. Adding a privilege the operator did not tick is silently widening a grant, and
    /// this project refuses in that direction as a rule — the same call the per-service statement
    /// timeout made the same day. A refusal naming the prerequisite teaches the model; an auto-add
    /// hides it.
    /// </para>
    /// </remarks>
    public static ImmutableDictionary<Privilege, ImmutableArray<Privilege>> Prerequisites { get; } =
        new Dictionary<Privilege, ImmutableArray<Privilege>>
        {
            // Publishing puts content into the catalogue, and content is what `create` allows.
            // Esri states the same dependency for the same privilege.
            [Privilege.ContentPublishFeatures] = [Privilege.ContentCreate],
            [Privilege.ContentPublishTiles] = [Privilege.ContentCreate],

            // Registering a data source is only useful to somebody who can publish from it, and
            // ADR-034 §5c moved this to the administrator anyway.
            [Privilege.ContentRegisterDataStore] = [Privilege.ContentCreate],

            // Sharing to a group requires belonging to one, which requires groups to exist for
            // this principal at all.
            [Privilege.GroupsShareTo] = [Privilege.ContentCreate],
            [Privilege.GroupsDeleteOwn] = [Privilege.GroupsCreate],
        }.ToImmutableDictionary();

    /// <summary>
    /// Which privileges a privilege already contains.
    /// </summary>
    /// <remarks>
    /// <b>Resolved in the check, not in the stored grants — ADR-035 §4e.</b> A role holding only
    /// <c>features:fullEdit</c> passes a <c>features:edit</c> check. Storing both would show two
    /// ticks for one decision, and a state where the wider is on and the narrower off would mean
    /// nothing.
    /// </remarks>
    public static ImmutableDictionary<Privilege, ImmutableArray<Privilege>> Implies { get; } =
        new Dictionary<Privilege, ImmutableArray<Privilege>>
        {
            [Privilege.FeaturesFullEdit] = [Privilege.FeaturesEdit],
            [Privilege.AdminManageAllContent] = [Privilege.AdminViewAllContent],
        }.ToImmutableDictionary();

    /// <summary>
    /// Whether a privilege belongs to the administrative half of the catalogue.
    /// </summary>
    /// <param name="privilege">The privilege.</param>
    /// <remarks>
    /// <b>Two sections, and they are ADR-034's surfaces from the other direction — ADR-035 §4f.</b>
    /// The reference's role editor splits *General* from *Administrative*, and the split lands on
    /// the same line ADR-034 draws between Studio and Server. <c>content:registerDataStore</c> is
    /// the one privilege whose name and section disagree: the reference lists it under General and
    /// the owner moved the grant to the administrator on 2026-08-17, so it is administrative here
    /// and keeps a content name.
    /// </remarks>
    public static bool IsAdministrative(Privilege privilege) => privilege switch
    {
        Privilege.AdminManageMembers or Privilege.AdminManageRoles
            or Privilege.AdminViewAllContent or Privilege.AdminManageAllContent
            or Privilege.AdminManageSecurity or Privilege.AdminManageServer
            or Privilege.ContentRegisterDataStore => true,
        _ => false,
    };

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
            .Add(Privilege.FeaturesFullEdit)
            .Add(Privilege.SharingShareToPublic);

        // <b>Registering a data source is an administrator's act here, and Portal grants it to a
        // publisher.</b> Owner decision 2026-08-17: *"data sources studio'nun değil server'in bir
        // seçeneği. onu da sadece admin ayarlayabilir."* — data sources is Server's option, not
        // Studio's, and only an administrator configures it.
        //
        // <b>The reasoning is what the act touches.</b> Publishing puts a table on the map;
        // registering a source hands this server a **credential for somebody else's database** and
        // adds a machine the whole deployment then depends on. Its failures are operational — a
        // connection that is down, a schema that changed, a password that rotated — and the person
        // who answers for those is the administrator. It is also the one act on this surface whose
        // blast radius is outside our own store.
        //
        // <b>Narrower than Portal, and narrow is the safe direction</b> — the same shape as
        // D-20's note about `features:edit`. Moved by changing the grant rather than the endpoint,
        // so `content:registerDataStore` keeps its name and meaning and does not become a
        // privilege with nothing behind it.
        ImmutableHashSet<Privilege> administrator = publisher
            .Add(Privilege.ContentRegisterDataStore)
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
            .Add(Privilege.SharingShareToPublic)

            // <b>The group privileges, added 2026-08-18 with ADR-036 — and their absence was a
            // defect for the length of one measurement.</b> A ceiling that does not list a privilege
            // withholds it, so `groups:create` granted to a role was refused for every member whose
            // user type is `creator` — which is every member who is not unrestricted. The refusal
            // even said so correctly: *"your role grants it and your user type does not permit it"*.
            //
            // <b>Creator and above, because a group is content.</b> A viewer or an editor cannot
            // create content at all, and a group is a thing somebody owns. Being *in* a group is
            // unaffected: membership is the sharing axis, not a privilege, so a viewer reads what is
            // shared with a group they belong to.
            .Add(Privilege.GroupsCreate)
            .Add(Privilege.GroupsDeleteOwn)
            .Add(Privilege.GroupsManageMembers)
            .Add(Privilege.GroupsShareTo);

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

    /// <summary>What the roles granted before the user-type ceiling was applied.</summary>
    private readonly ImmutableHashSet<Privilege> _beforeCeiling;

    private Authorization(
        string userType,
        IEnumerable<string> roles,
        IEnumerable<Privilege> privileges,
        bool administrator = false,
        IEnumerable<Privilege>? beforeCeiling = null,
        IEnumerable<Guid>? groups = null,
        IEnumerable<Guid>? editableGroups = null)
    {
        UserType = userType;
        Roles = [.. roles];
        Privileges = [.. privileges];
        IsAdministrator = administrator;
        _beforeCeiling = beforeCeiling is null ? [.. privileges] : [.. beforeCeiling];
        Groups = groups is null ? [] : [.. groups];
        EditableGroups = editableGroups is null ? [] : [.. editableGroups];
    }

    /// <summary>Which groups this principal belongs to — ADR-036 §3's membership axis.</summary>
    public ImmutableHashSet<Guid> Groups { get; }

    /// <summary>
    /// The subset of <see cref="Groups"/> whose shared items every member may edit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared update — owner decision 2026-08-25, [ADR-036](../../../docs/adr/ADR-036-groups.md)
    /// §4a as amended.</b> A group's <c>item_update</c> has been stored since groups shipped and
    /// was honoured nowhere: the listing showed it and every edit went on asking for
    /// <c>features:fullEdit</c> alone. That is the shape [D-67](../../../docs/architecture-debt.md)
    /// records — a setting the server keeps and does not enforce — and it is the same shape the
    /// removed <c>public</c> visibility had.
    /// </para>
    /// <para>
    /// <b>A separate set rather than a flag on <see cref="Groups"/>, because they answer different
    /// questions.</b> <see cref="Groups"/> decides whether the caller may <em>read</em> an item
    /// shared with a group, which is ADR-018 §3b's invariant and is unchanged. This one decides
    /// whether they may <em>write</em> to it, and it is always a subset: a group that confers
    /// editing necessarily confers reading, and reversing that would let somebody edit a layer
    /// they cannot see.
    /// </para>
    /// <para>
    /// <b>Only <c>allItems</c> lands here.</b> <c>ownItems</c> means *the items you shared*, and
    /// their owner may already edit them by owning them, so it grants nothing this set could carry.
    /// That is worth stating rather than leaving as an omission somebody re-derives.
    /// </para>
    /// </remarks>
    public ImmutableHashSet<Guid> EditableGroups { get; }

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
    public static Authorization Resolve(string userType, IEnumerable<string> roles) =>
        Resolve(userType, roles, CompiledRoleGrants.Instance);

    /// <summary>
    /// What a principal may do, reading each role's grants from a given source.
    /// </summary>
    /// <param name="userType">The assigned user type.</param>
    /// <param name="roles">The assigned role names.</param>
    /// <param name="grants">Where each role's privileges come from.</param>
    /// <remarks>
    /// <para>
    /// <b>The source is a parameter since 2026-08-18, because the answer stopped being a
    /// constant.</b> ADR-035 makes a deployment able to edit what a role grants; the overload above
    /// keeps the compiled table for everything with no store, and it is the same answer every build
    /// before that gave.
    /// </para>
    /// <para>
    /// <b>Implications are applied here, not stored.</b> A role holding
    /// <c>features:fullEdit</c> passes a <c>features:edit</c> check without a second row saying so —
    /// ADR-035 §4e. Doing it here rather than at the check means every caller of
    /// <see cref="Authorization.Allows"/> gets it, including the ones written before the rule
    /// existed.
    /// </para>
    /// <para>
    /// <b>The administrator is recognised and not intersected.</b> The owner: *"Admin yetkisi
    /// değiştirilemez. Ve sınırlandırılamaz. Sistemde her işlemi yapabilir."* So the flag is set
    /// from the role list and <see cref="Authorization.Allows"/> answers true regardless of both the
    /// stored grants and the user-type ceiling. <b>The ceiling not applying is a deliberate
    /// consequence and worth naming:</b> ADR-018 §3a's ceiling exists so a migration cannot silently
    /// widen an imported deployment, and an administrator held below the privileges needed to
    /// administer is [D-14]'s unrecoverable server reached by a settings page. Import is where that
    /// concern belongs, and import is not built.
    /// </para>
    /// </remarks>
    /// <returns>The resolved authorization.</returns>
    public static Authorization Resolve(
        string userType, IEnumerable<string> roles, IRoleGrants grants) =>
        Resolve(userType, roles, grants, []);

    /// <summary>
    /// What a principal may do, including which groups they belong to.
    /// </summary>
    /// <param name="userType">The assigned user type.</param>
    /// <param name="roles">The assigned role names.</param>
    /// <param name="grants">Where each role's privileges come from.</param>
    /// <param name="groups">The groups this principal is in — ADR-036.</param>
    /// <returns>The resolved authorization.</returns>
    /// <remarks>
    /// <b>Group membership is not a privilege and is carried here anyway.</b> It is the second axis
    /// of ADR-036 §3, it is resolved from the same statement as the roles, and every reader that
    /// needs it already has an <c>Authorization</c> in hand — threading a fifth parameter through
    /// seven call sites to carry a set of ids would be the ceremony ADR-034 §5c complains about.
    /// </remarks>
    public static Authorization Resolve(
        string userType,
        IEnumerable<string> roles,
        IRoleGrants grants,
        IEnumerable<Guid> groups) =>
        Resolve(userType, roles, grants, groups, []);

    /// <inheritdoc cref="Resolve(string, IEnumerable{string}, IRoleGrants, IEnumerable{Guid})"/>
    /// <param name="userType">The caller's user type, which is the ceiling.</param>
    /// <param name="roles">The roles they hold.</param>
    /// <param name="grants">What each role grants in this deployment.</param>
    /// <param name="groups">Which groups they belong to.</param>
    /// <param name="editableGroups">
    /// Which of <paramref name="groups"/> confer editing what is shared with them — the groups
    /// whose <c>item_update</c> is <c>allItems</c>. See <see cref="EditableGroups"/>.
    /// </param>
    public static Authorization Resolve(
        string userType,
        IEnumerable<string> roles,
        IRoleGrants grants,
        IEnumerable<Guid> groups,
        IEnumerable<Guid> editableGroups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(editableGroups);

        ArgumentNullException.ThrowIfNull(userType);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(grants);

        string[] granted = [.. roles];
        HashSet<Privilege> fromRoles = [];

        foreach (string role in granted)
        {
            fromRoles.UnionWith(grants.PrivilegesOf(role));
        }

        // Everything the held privileges already contain.
        foreach (Privilege held in fromRoles.ToArray())
        {
            if (Identity.Roles.Implies.TryGetValue(held, out ImmutableArray<Privilege> narrower))
            {
                fromRoles.UnionWith(narrower);
            }
        }

        ImmutableHashSet<Privilege> ceiling = UserTypes.CeilingOf(userType);

        bool administrator = false;

        foreach (string role in granted)
        {
            if (string.Equals(role, Identity.Roles.Administrator, StringComparison.Ordinal))
            {
                administrator = true;
                break;
            }
        }

        return new Authorization(
            userType, granted, fromRoles.Intersect(ceiling), administrator, fromRoles, groups,
            editableGroups);
    }

    /// <summary>The assigned user type.</summary>
    public string UserType { get; }

    /// <summary>The assigned role names.</summary>
    public ImmutableArray<string> Roles { get; }

    /// <summary>What survived the intersection.</summary>
    public ImmutableHashSet<Privilege> Privileges { get; }

    /// <summary>
    /// Whether this principal holds the administrator role.
    /// </summary>
    /// <remarks>
    /// <b>ADR-035 §4b.</b> Not *"holds every administrative privilege"* — that is a set of rows and
    /// rows can be edited. This is the role, and the role's authority is a property of this code.
    /// </remarks>
    public bool IsAdministrator { get; }

    /// <summary>Whether this principal holds a privilege.</summary>
    /// <param name="privilege">The privilege.</param>
    /// <remarks>
    /// <b>An administrator holds every privilege, without consulting anything.</b> The owner:
    /// *"Admin yetkisi değiştirilemez. Ve sınırlandırılamaz."* Seeding the administrator's grants
    /// and refusing edits at the API would leave the claim standing on rows, and a store is written
    /// by more than one API over its life. So the check short-circuits and the rows are decoration
    /// for the screen.
    /// </remarks>
    public bool Allows(Privilege privilege) =>
        IsAdministrator || Privileges.Contains(privilege);

    /// <summary>
    /// Whether a role granted this privilege and the user type took it away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so a refusal can say <em>your role grants this and your user type
    /// does not permit it</em> rather than <em>you do not have this</em>. The
    /// second sends an administrator to grant a role they have already granted.
    /// </para>
    /// <para>
    /// <b>Answered from what the roles granted at resolution time — corrected 2026-08-18.</b> It
    /// asked <c>Roles.PrivilegesOf</c>, the compiled table, which was the answer until ADR-035 made
    /// role grants editable. After that it was the wrong answer in exactly the case this method
    /// exists for: an administrator who had just added an administrative privilege to a role would
    /// be told the member simply lacked it, rather than that their user type withheld it — and would
    /// grant the role again. Found by a test that edited a role and read the refusal.
    /// </para>
    /// <para>
    /// <b>The granted set is captured rather than looked up.</b> Storing what the roles conferred
    /// before the ceiling was applied costs one field and removes the need for this type to hold a
    /// reference to a grant source — which would make an authorization answer depend on a store that
    /// may have changed since the answer was computed.
    /// </para>
    /// </remarks>
    public bool WithheldByUserType(Privilege privilege) =>
        !Privileges.Contains(privilege) && _beforeCeiling.Contains(privilege);
}
