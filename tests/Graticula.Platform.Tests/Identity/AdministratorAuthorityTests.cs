using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// The administrator's authority is a property of this code, not a set of rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-035 §4b and condition 2, from the owner:</b> *"Admin yetkisi değiştirilemez. Ve
/// sınırlandırılamaz. Sistemde her işlemi yapabilir."* Seeding the administrator's grants and
/// refusing edits at the API would leave that standing on rows, and a store is written by more than
/// one API over its life. So the check short-circuits, and these tests hold it to that from both
/// directions.
/// </para>
/// <para>
/// <b>The empty-store case is the one with teeth.</b> A test that gives the administrator its
/// privileges and then checks it has them proves nothing about §4b. This one takes them all away.
/// </para>
/// </remarks>
public sealed class AdministratorAuthorityTests
{
    /// <summary>
    /// An administrator holds every privilege with the grant store completely empty.
    /// </summary>
    [Fact]
    public void An_administrator_passes_every_check_with_no_stored_grants_at_all()
    {
        Authorization resolved = Authorization.Resolve(
            UserTypes.Creator, [Roles.Administrator], Grants.Nothing);

        Assert.True(resolved.IsAdministrator);

        foreach (Privilege privilege in Roles.AllPrivileges)
        {
            Assert.True(
                resolved.Allows(privilege),
                $"An administrator was refused {Roles.NameOf(privilege)} because no row granted "
                + "it. ADR-035 §4b: the rows are decoration for the screen, and deleting them must "
                + "not be a way to disarm an administrator.");
        }
    }

    /// <summary>
    /// The user-type ceiling does not narrow an administrator either.
    /// </summary>
    /// <remarks>
    /// <b>A deliberate consequence, and it is the least obvious part of §4b.</b> ADR-018 §3a's
    /// ceiling exists so importing a deployment cannot silently widen what the source granted. An
    /// administrator held below the privileges needed to administer is D-14's unrecoverable server,
    /// reached through a settings page — so *"sınırlandırılamaz"* is taken at its word and import is
    /// where the escalation concern belongs.
    /// </remarks>
    [Fact]
    public void A_viewer_user_type_does_not_disarm_an_administrator()
    {
        Authorization resolved = Authorization.Resolve(
            UserTypes.Viewer, [Roles.Administrator], CompiledRoleGrants.Instance);

        Assert.True(resolved.Allows(Privilege.AdminManageServer));
        Assert.True(resolved.Allows(Privilege.ContentPublishFeatures));
    }

    /// <summary>
    /// No other role becomes an administrator, however many privileges it is given.
    /// </summary>
    /// <remarks>
    /// <b>ADR-035 §4g and condition 7.</b> Without this, §4a's editability contains its own defeat:
    /// a role holding <c>admin:manageRoles</c> could grant itself the rest, and one holding
    /// <c>admin:manageMembers</c> could hand its holder the administrator role. Holding every
    /// privilege must not be the same thing as *being* the administrator, because the operations
    /// reserved to that role are refused by name rather than by privilege.
    /// </remarks>
    [Fact]
    public void A_custom_role_holding_every_privilege_is_still_not_an_administrator()
    {
        Grants everything = new(new Dictionary<string, ImmutableHashSet<Privilege>>
        {
            ["almost_admin"] = [.. Roles.AllPrivileges],
        });

        Authorization resolved = Authorization.Resolve(
            UserTypes.Unrestricted, ["almost_admin"], everything);

        Assert.False(
            resolved.IsAdministrator,
            "A custom role granted every privilege in the catalogue reports itself as the "
            + "administrator. The operations ADR-035 §4g reserves — changing a role to or from "
            + "administrator, deleting an administrator, resetting an administrator's password — are "
            + "refused by asking this flag, so a role that can set it can escalate to it.");

        // It does hold each privilege, which is the point of granting them: the distinction is
        // between *what may be done* and *who the server is unable to restrain*.
        Assert.True(resolved.Allows(Privilege.AdminManageRoles));
    }

    /// <summary>
    /// A role holding the wider privilege passes a check for the narrower one.
    /// </summary>
    /// <remarks>
    /// <b>ADR-035 §4e's implication half, condition 6.</b> Resolved here rather than stored, so the
    /// screen shows one tick per decision and a state where the wider is on and the narrower off
    /// cannot exist.
    /// </remarks>
    [Theory]
    [InlineData(Privilege.FeaturesFullEdit, Privilege.FeaturesEdit)]
    [InlineData(Privilege.AdminManageAllContent, Privilege.AdminViewAllContent)]
    public void The_wider_privilege_satisfies_a_check_for_the_narrower(
        Privilege wider, Privilege narrower)
    {
        Grants only = new(new Dictionary<string, ImmutableHashSet<Privilege>>
        {
            ["one"] = [wider],
        });

        Authorization resolved = Authorization.Resolve(UserTypes.Unrestricted, ["one"], only);

        Assert.True(resolved.Allows(wider));

        Assert.True(
            resolved.Allows(narrower),
            $"A role holding {Roles.NameOf(wider)} was refused {Roles.NameOf(narrower)}, which it "
            + "contains. ADR-035 §4e resolves that here so the stored grants stay minimal.");
    }

    /// <summary>
    /// A privilege the ceiling withholds is withheld, for everybody but an administrator.
    /// </summary>
    /// <remarks>
    /// <b>The direction that must keep working.</b> Editable roles make the ceiling the only thing
    /// between an edited role and a privilege nobody reviewed, so it has to still bite.
    /// </remarks>
    [Fact]
    public void The_ceiling_still_narrows_an_ordinary_role()
    {
        Grants generous = new(new Dictionary<string, ImmutableHashSet<Privilege>>
        {
            ["eager"] = [Privilege.ContentPublishFeatures, Privilege.AdminManageServer],
        });

        Authorization resolved = Authorization.Resolve(UserTypes.Creator, ["eager"], generous);

        Assert.True(resolved.Allows(Privilege.ContentPublishFeatures));

        Assert.False(
            resolved.Allows(Privilege.AdminManageServer),
            "A creator user type let an edited role confer an administrative privilege. ADR-018 §3a's "
            + "ceiling is what stops a role edit from becoming an escalation nobody reviewed.");

        Assert.True(
            resolved.WithheldByUserType(Privilege.AdminManageServer),
            "The refusal cannot say *your role grants this and your user type does not permit it*, "
            + "so the operator sees *you do not have this* and edits the role again.");
    }

    /// <summary>An unknown role confers nothing rather than throwing.</summary>
    [Fact]
    public void A_role_nobody_defined_confers_nothing()
    {
        Authorization resolved = Authorization.Resolve(
            UserTypes.Unrestricted, ["deleted_last_week"], Grants.Nothing);

        Assert.False(resolved.IsAdministrator);
        Assert.Empty(resolved.Privileges);
    }

    /// <summary>A grant source built from a dictionary, for tests that need an exact one.</summary>
    private sealed class Grants : IRoleGrants
    {
        private readonly ImmutableDictionary<string, ImmutableHashSet<Privilege>> _held;

        public Grants(IReadOnlyDictionary<string, ImmutableHashSet<Privilege>> held) =>
            _held = held.ToImmutableDictionary(StringComparer.Ordinal);

        /// <summary>A store with no grants at all, which is §4b's decisive case.</summary>
        public static Grants Nothing { get; } =
            new(new Dictionary<string, ImmutableHashSet<Privilege>>(StringComparer.Ordinal));

        public ImmutableHashSet<Privilege> PrivilegesOf(string role) =>
            _held.TryGetValue(role, out ImmutableHashSet<Privilege>? p) ? p : [];

        public ImmutableDictionary<string, ImmutableHashSet<Privilege>> All() => _held;

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
