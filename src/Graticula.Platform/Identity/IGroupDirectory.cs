using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Graticula.Platform.Identity;

/// <summary>
/// What a group may confer on the items shared with it — ADR-036 §4b, fixed at creation.
/// </summary>
public enum GroupItemUpdate
{
    /// <summary>
    /// Members read what is shared with the group and nothing more. The default.
    /// </summary>
    /// <remarks>
    /// <b>A narrowing the reference does not appear to offer, and ADR-036 §4c argues for it:</b> a
    /// group whose purpose is *these people may read this* should not have to declare an editing
    /// posture it will never use.
    /// </remarks>
    None,

    /// <summary>Members may edit items they themselves shared into the group.</summary>
    OwnItems,

    /// <summary>Members may edit every item shared with the group.</summary>
    AllItems,
}

/// <summary>Who may discover that a group exists — ADR-036 §4g.</summary>
/// <remarks>
/// <b>A different question from who may read its items.</b> A group's items are readable by its
/// members and by nobody else, whatever this says; this answers whether somebody outside it can find
/// it in a list and ask to join. The reference calls it *"Who can view this group?"*.
/// </remarks>
public enum GroupVisibility
{
    /// <summary>Only its members see it. The default, and what this server did before the setting.</summary>
    Members,

    /// <summary>Any signed-in member of the organisation can find it.</summary>
    Organization,

    /// <summary>Anybody, including an anonymous caller.</summary>
    Public,
}

/// <summary>How somebody comes to be in a group — ADR-036 §4g.</summary>
public enum GroupJoinPolicy
{
    /// <summary>An owner or manager adds them. The default.</summary>
    Invitation,

    /// <summary>
    /// They ask and somebody approves — <b>stored and refused on write until the queue exists</b>.
    /// </summary>
    /// <remarks>
    /// <b>A queue of pending requests is a table, a screen and a decision about who reviews them</b>,
    /// and none of that is built. The value is in the schema so the column does not have to be
    /// widened later; the write path refuses it, because a policy the server stores and does not
    /// honour is <see href="../../docs/architecture-debt.md">D-67</see> over again — that debt was a
    /// setting reported and unenforced for two days.
    /// </remarks>
    Request,

    /// <summary>Anybody who can see the group adds themselves.</summary>
    Self,
}

/// <summary>Who may add services to a group — ADR-036 §4g.</summary>
/// <remarks>
/// <b>Not <see cref="GroupItemUpdate"/>.</b> That governs editing what is already shared and is fixed
/// at creation; this governs who may share something in, and is editable. The reference's Settings
/// page offers this and not the other, which is the evidence they draw the same line.
/// </remarks>
public enum GroupContribute
{
    /// <summary>Every member may share their own services with the group.</summary>
    Members,

    /// <summary>Only the owner and its managers. The default, and what this server enforced.</summary>
    Managers,
}

/// <summary>Where a principal stands in relation to one group — ADR-036 §3's second axis.</summary>
public enum GroupStanding
{
    /// <summary>Not in it. Reads nothing shared with it.</summary>
    Outside,

    /// <summary>In it. Reads what is shared with it.</summary>
    Member,

    /// <summary>
    /// In it, and holds the group's operations inside it without owning it.
    /// </summary>
    /// <remarks>
    /// The owner: *"bir grubun sahibi olmayabilirsin ama yönetici olarak atanırsan, sen de grupta
    /// yetkili işlemler yapabilirsin."*
    /// </remarks>
    Manager,

    /// <summary>Owns it. May delete and transfer it, which a manager may not.</summary>
    /// <remarks>
    /// <b>Distinct from <see cref="Manager"/> on purpose — ADR-036 §3.</b> It is the difference
    /// between delegating work and delegating control, and a model that conflates them makes every
    /// helper a potential deleter.
    /// </remarks>
    Owner,
}

/// <summary>A group, and what the caller needs to know about it.</summary>
/// <param name="Id">Its identity.</param>
/// <param name="Name">Its name, unique case-insensitively.</param>
/// <param name="Title">A display title, or null.</param>
/// <param name="Description">What it is for, or null.</param>
/// <param name="Owner">Who owns it, by name.</param>
/// <param name="ItemUpdate">What it confers on its items. Fixed at creation.</param>
/// <param name="Members">How many principals belong.</param>
/// <param name="Items">How many services are shared with it.</param>
/// <param name="Standing">Where the asking principal stands, if one was named.</param>
/// <param name="Summary">A one-line summary, or null.</param>
/// <param name="Visibility">Who may discover it.</param>
/// <param name="JoinPolicy">How somebody comes to be in it.</param>
/// <param name="Contribute">Who may share services with it.</param>
/// <param name="DeleteLocked">Whether it is protected from deletion.</param>
/// <param name="CreatedAt">When it was made.</param>
public sealed record GroupSummary(
    Guid Id,
    string Name,
    string? Title,
    string? Description,
    string? Owner,
    GroupItemUpdate ItemUpdate,
    int Members,
    int Items,
    GroupStanding Standing,
    string? Summary = null,
    GroupVisibility Visibility = GroupVisibility.Members,
    GroupJoinPolicy JoinPolicy = GroupJoinPolicy.Invitation,
    GroupContribute Contribute = GroupContribute.Managers,
    bool DeleteLocked = false,
    DateTimeOffset CreatedAt = default);

/// <summary>A service shared with a group, and whether it actually reaches the members.</summary>
/// <param name="Name">Its qualified name.</param>
/// <param name="Sharing">
/// The service's <em>own</em> sharing scope. **This is the field that makes the two-step trap
/// visible.** Sharing a service into a group and setting its scope to `group` are separate acts, and
/// either alone is a state that reads as done and is not — so the screen shows which shares reach
/// anybody rather than warning in prose that some might not.
/// </param>
/// <param name="Kind">What sort of service it is — the slot a map arrives into.</param>
/// <param name="Shared">When it was shared with the group, or null for a row older than the column.</param>
/// <param name="SharedBy">
/// Who shared it. <b>More useful here than an owner would be:</b> with
/// <see cref="GroupContribute.Members"/> any member may share their own service in, so when one of
/// thirty is inert this names the person to talk to.
/// </param>
/// <param name="CoverLayer">
/// The lowest-numbered layer of the service, or null when it holds none.
/// <b>What a picture of it is drawn from</b>, because a service holds no geometry of its own — the
/// same cover <see cref="Graticula.Platform.Admin.AdminService"/> carries, reported here so a group's
/// Content tab can draw a thumbnail without joining two listings in the browser. Deriving it in the
/// client as the lowest layer id of the rows it happens to have is right only while every layer of
/// every service is visible to the caller, and goes silently wrong the first time one is not.
/// </param>
/// <param name="CoverIndex">That layer's index, which is what the address needs.</param>
public sealed record GroupItem(
    string Name,
    string Sharing,
    string? Kind = null,
    DateTimeOffset? Shared = null,
    string? SharedBy = null,
    string? CoverLayer = null,
    int CoverIndex = 0);

/// <summary>Somebody in a group, and how they came to be there.</summary>
/// <param name="Name">Their sign-in name.</param>
/// <param name="DisplayName">Their display name, or null if they have none.</param>
/// <param name="Standing">What they are in the group.</param>
/// <param name="Joined">When they were added, or null for a row older than the column.</param>
/// <param name="AddedBy">Who added them, or null for the owner and for pre-migration rows.</param>
/// <remarks>
/// <b><see cref="Joined"/> is an access-control fact, not a decoration.</b> A group's member list is
/// an access-control list, and *when did this person gain access to everything shared here* is an
/// audit question the console could not answer while the column sat unread.
/// </remarks>
public sealed record GroupMember(
    string Name,
    string? DisplayName,
    GroupStanding Standing,
    DateTimeOffset? Joined = null,
    string? AddedBy = null);

/// <summary>Why a group operation was refused.</summary>
public enum GroupChange
{
    /// <summary>It happened.</summary>
    Done,

    /// <summary>No group by that name or id.</summary>
    Absent,

    /// <summary>A group by that name already exists.</summary>
    Exists,

    /// <summary>
    /// The principal neither owns nor manages this group.
    /// </summary>
    /// <remarks>
    /// <b>ADR-036 §3 and condition 2.</b> `groups:manageMembers` is not *manage anybody's group*; a
    /// privilege that turned out to be global is the escalation this decision would otherwise have
    /// introduced.
    /// </remarks>
    NotYours,

    /// <summary>Only the owner or an administrator may do this — deleting or transferring.</summary>
    OwnerOnly,

    /// <summary>The named member or service does not exist.</summary>
    NoSuchTarget,

    /// <summary>
    /// The capability cannot be changed after creation — ADR-036 §4c.
    /// </summary>
    Immutable,

    /// <summary>
    /// The group is locked against deletion — ADR-036 §4g.
    /// </summary>
    /// <remarks>
    /// <b>A lock rather than a confirmation, and the difference is the point.</b> A confirmation is
    /// dismissed by habit; a lock has to be turned off deliberately, on the screen that shows what the
    /// group holds.
    /// </remarks>
    Locked,

    /// <summary>
    /// A join policy the schema admits and the application does not honour yet.
    /// </summary>
    NotBuilt,
}

/// <summary>
/// Groups, their members and what is shared with them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A fourth port over the platform store, for the reason the third one gave.</b> Every request
/// touches <see cref="IIdentityStore"/> and <see cref="IRoleGrants"/>; only a caller with a
/// <c>groups:*</c> privilege touches this.
/// </para>
/// <para>
/// <b>Every method takes the acting principal, and that is what makes the second axis real.</b> A
/// directory whose methods took only a group id would leave the membership check to each caller —
/// ADR-036 condition 2 is precisely that this must not be possible to forget.
/// </para>
/// </remarks>
public interface IGroupDirectory
{
    /// <summary>Groups this principal can see, with where they stand in each.</summary>
    /// <param name="principal">Who is asking.</param>
    /// <param name="all">
    /// True to list every group regardless of membership, which needs
    /// <c>admin:manageAllContent</c> and is the caller's to check.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The groups, by name.</returns>
    Task<IReadOnlyList<GroupSummary>> ListAsync(
        Guid principal, bool all, CancellationToken cancellationToken);

    /// <summary>Creates a group owned by this principal.</summary>
    /// <param name="owner">Who will own it.</param>
    /// <param name="name">Its name.</param>
    /// <param name="title">A display title, or null.</param>
    /// <param name="description">What it is for, or null.</param>
    /// <param name="itemUpdate">What it confers, fixed here and never again.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened, and the group's id when it was created.</returns>
    Task<(GroupChange Outcome, Guid Id)> CreateAsync(
        Guid owner,
        string name,
        string? title,
        string? description,
        GroupItemUpdate itemUpdate,
        CancellationToken cancellationToken);

    /// <summary>Removes a group. The owner's act, or an administrator's.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <b>Members and shares go with it, and nothing else does.</b> The services shared into it keep
    /// existing and revert to whatever their own scope says — deleting a group must not unpublish
    /// somebody's data, which is the difference between this and ADR-015 §6c's `delete` disposition.
    /// </remarks>
    Task<GroupChange> RemoveAsync(
        Guid acting, bool administrator, string name, CancellationToken cancellationToken);

    /// <summary>Adds or re-grades a member.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="asManager">Whether they are being made a manager.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<GroupChange> SetMemberAsync(
        Guid acting,
        bool administrator,
        string name,
        string member,
        bool asManager,
        CancellationToken cancellationToken);

    /// <summary>Removes a member.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    Task<GroupChange> RemoveMemberAsync(
        Guid acting,
        bool administrator,
        string name,
        string member,
        CancellationToken cancellationToken);

    /// <summary>Shares a service with a group, or stops.</summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="service">The service's name.</param>
    /// <param name="folder">Its folder, or null for the root.</param>
    /// <param name="wanted">True to share, false to unshare.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <b>Sharing into a group does not change the service's own scope.</b> A service reaches
    /// `group` scope because somebody set it there; this table says *which* groups. Setting the scope
    /// and choosing the groups are two acts because either without the other is a state somebody
    /// would misread — and the console says so on the screen.
    /// </remarks>
    Task<GroupChange> ShareAsync(
        Guid acting,
        bool administrator,
        string name,
        string service,
        string? folder,
        bool wanted,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes a group's editable policies.
    /// </summary>
    /// <param name="acting">Who is asking.</param>
    /// <param name="administrator">Whether they hold <c>admin:manageAllContent</c>.</param>
    /// <param name="name">The group.</param>
    /// <param name="title">A display title, or null to clear it.</param>
    /// <param name="summary">A one-line summary, or null to clear it.</param>
    /// <param name="description">What it is for, or null to clear it.</param>
    /// <param name="visibility">Who may discover it.</param>
    /// <param name="joinPolicy">How people join.</param>
    /// <param name="contribute">Who may share services with it.</param>
    /// <param name="deleteLocked">Whether it is protected from deletion.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <para>
    /// <b>The owner's or a manager's, and it replaces every field — including the three that are
    /// text.</b> Same shape as <see cref="IRoleDirectory.SetPrivilegesAsync"/>, and the same argument:
    /// a patch API would make *"set visibility"* and *"set everything, with visibility changed"* two
    /// requests with one intent, which is where a race lives.
    /// </para>
    /// <para>
    /// <b>Which makes a partial caller a data-loss bug, and the first version of this documentation
    /// said the opposite.</b> These three parameters were described as *"or null to leave it"* while
    /// the statement writes `set title = @title` — so a screen that posted only the policies would
    /// have erased the title, the summary and the description, and one that posted only a summary
    /// would have silently unlocked a delete-locked group. Caught by a design review before either
    /// screen existed. **A caller must send the whole object**, overlaid on what it last read; the
    /// console has one helper for that and nothing else may assemble the body.
    /// </para>
    /// <para>
    /// <b>`item_update` is not a parameter, and its absence is the decision.</b> §4c: there is no
    /// write path for it at all, which is a stronger form of immutable than a refusal in one.
    /// </para>
    /// </remarks>
    Task<GroupChange> SetSettingsAsync(
        Guid acting,
        bool administrator,
        string name,
        string? title,
        string? summary,
        string? description,
        GroupVisibility visibility,
        GroupJoinPolicy joinPolicy,
        GroupContribute contribute,
        bool deleteLocked,
        CancellationToken cancellationToken);

    /// <summary>Which members a group has, and what each is.</summary>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Member name to standing, empty when there is no such group.</returns>
    Task<IReadOnlyList<GroupMember>> MembersAsync(
        string name, CancellationToken cancellationToken);

    /// <summary>
    /// Members who could be added to this group, by name.
    /// </summary>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Enabled members not already in it, by name.</returns>
    /// <remarks>
    /// <para>
    /// <b>Names and nothing else, and that is the whole design.</b> Adding somebody to a group needs
    /// to know who exists, and reading the member directory needs <c>admin:manageMembers</c> — so a
    /// publisher who owns a group could not populate a picker and the console asked them to type a
    /// name from memory. The owner objected, and correctly.
    /// </para>
    /// <para>
    /// <b>The alternative was widening a privilege, which would have been the wrong repair.</b>
    /// <c>admin:manageMembers</c> carries creating accounts, changing roles, disabling and deleting;
    /// granting it so that somebody can fill a dropdown is
    /// <see href="../../docs/architecture-debt.md">D-20</see>'s complaint in reverse. This returns
    /// the one field a picker needs, to somebody who already manages the group — and a member's name
    /// is not a secret from other members: it is on every item they own and in every group they share.
    /// </para>
    /// <para>
    /// <b>Disabled accounts are excluded rather than shown greyed.</b> A disabled member cannot read
    /// anything, so adding them to a group is an act with no effect, and a picker that offers it is
    /// offering a mistake.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> CandidatesAsync(string name, CancellationToken cancellationToken);

    /// <summary>Which services are shared with a group.</summary>
    /// <param name="name">The group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Qualified service names.</returns>
    Task<IReadOnlyList<GroupItem>> ItemsAsync(string name, CancellationToken cancellationToken);
}
