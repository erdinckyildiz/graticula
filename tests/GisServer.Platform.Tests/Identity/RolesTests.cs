using System;
using System.Collections.Immutable;
using System.Linq;
using GisServer.Platform.Identity;
using GisServer.Platform.Schema;
using Xunit;

namespace GisServer.Platform.Tests.Identity;

/// <summary>
/// The role, user-type and sharing model of ADR-018.
/// </summary>
/// <remarks>
/// The model is ArcGIS Portal's, adopted by owner direction. These tests state
/// the decision rather than defend it: if the matrix changes, they should fail
/// loudly and be edited deliberately.
/// </remarks>
public sealed class RolesTests
{
    [Fact]
    public void The_role_set_is_the_five_portal_defaults()
    {
        // ToArray, because both sides would otherwise target-type to
        // ImmutableArray<string> — a struct whose Equals is identity on the
        // backing array, so the assertion compares the wrong thing and fails
        // while printing two identical collections.
        Assert.Equal(
            ["viewer", "data_editor", "user", "publisher", "administrator"],
            Roles.All.ToArray());
    }

    [Fact]
    public void Viewer_carries_no_privileges_at_all()
    {
        // The single most surprising line in the model, and the one most likely
        // to be "fixed" by someone holding the superseded design. Portal has no
        // read privilege: a viewer reads plenty, and reading is governed by
        // sharing. If this ever becomes non-empty, ADR-018 §2 has been undone.
        Assert.Empty(Roles.PrivilegesOf(Roles.Viewer));
    }

    [Fact]
    public void The_administrator_carries_every_privilege_that_exists()
    {
        // The guard on adding a privilege: a new enum member no role grants is a
        // capability nobody can use, and it presents as an endpoint that refuses
        // everyone including the administrator.
        Assert.Equal(
            Enum.GetValues<Privilege>().ToImmutableHashSet(),
            Roles.PrivilegesOf(Roles.Administrator));
    }

    [Fact]
    public void Publishing_hosted_content_does_not_carry_registering_a_data_source()
    {
        // Portal's placement, and ours from the superseded version, for the
        // reason recorded there: registering hands the server a credential to
        // somebody else's database. Both sit with the publisher — the point is
        // that a plain user has neither.
        Assert.DoesNotContain(Privilege.ContentRegisterDataStore, Roles.PrivilegesOf(Roles.User));
        Assert.DoesNotContain(Privilege.ContentPublishFeatures, Roles.PrivilegesOf(Roles.User));
        Assert.Contains(Privilege.ContentRegisterDataStore, Roles.PrivilegesOf(Roles.Publisher));
    }

    [Fact]
    public void Sharing_publicly_is_separated_from_sharing_with_the_organisation()
    {
        // The most consequential privilege in the set: the one that puts data on
        // the internet. A deployment wanting publishing without public exposure
        // withholds exactly this, which is only possible because they are two.
        Assert.Contains(Privilege.SharingShareToOrganization, Roles.PrivilegesOf(Roles.User));
        Assert.DoesNotContain(Privilege.SharingShareToPublic, Roles.PrivilegesOf(Roles.User));
        Assert.Contains(Privilege.SharingShareToPublic, Roles.PrivilegesOf(Roles.Publisher));
    }

    [Fact]
    public void A_publisher_cannot_administer()
    {
        ImmutableHashSet<Privilege> publisher = Roles.PrivilegesOf(Roles.Publisher);

        Assert.DoesNotContain(Privilege.AdminManageMembers, publisher);
        Assert.DoesNotContain(Privilege.AdminViewAllContent, publisher);
        Assert.DoesNotContain(Privilege.AdminManageServer, publisher);
    }

    [Fact]
    public void An_unknown_role_confers_nothing_rather_than_throwing()
    {
        Assert.Empty(Roles.PrivilegesOf("superuser"));
    }

    // ---------- the user-type ceiling ----------

    [Fact]
    public void The_default_user_type_withholds_nothing()
    {
        Assert.Equal(
            Enum.GetValues<Privilege>().ToImmutableHashSet(),
            UserTypes.CeilingOf(UserTypes.Unrestricted));
    }

    [Fact]
    public void A_narrow_user_type_clips_a_wide_role()
    {
        // ADR-018 condition 1, written from the scenario it exists for rather
        // than from the unit. Migration (Q-16) imports a member who holds the
        // Publisher role and a Viewer user type; the source system gave them
        // viewing only. Keeping the role and dropping the ceiling would grant
        // publishing rights the original withheld — silent privilege escalation
        // during an import nobody re-audits.
        Authorization imported = Authorization.Resolve(UserTypes.Viewer, [Roles.Publisher]);

        Assert.Empty(imported.Privileges);
        Assert.False(imported.Allows(Privilege.ContentPublishFeatures));
        Assert.Contains(Roles.Publisher, imported.Roles);
    }

    [Fact]
    public void A_clipped_privilege_reports_that_the_ceiling_took_it()
    {
        // Otherwise the refusal reads "you do not have this", and an
        // administrator grants the role again — which they had already granted.
        Authorization imported = Authorization.Resolve(UserTypes.Editor, [Roles.Publisher]);

        Assert.False(imported.Allows(Privilege.ContentPublishFeatures));
        Assert.True(imported.WithheldByUserType(Privilege.ContentPublishFeatures));

        // Not withheld by the ceiling — simply never granted by any held role.
        Authorization plain = Authorization.Resolve(UserTypes.Unrestricted, [Roles.Viewer]);
        Assert.False(plain.WithheldByUserType(Privilege.ContentPublishFeatures));
    }

    [Fact]
    public void A_creator_may_publish_but_never_administer()
    {
        Authorization creator = Authorization.Resolve(UserTypes.Creator, [Roles.Administrator]);

        Assert.True(creator.Allows(Privilege.ContentPublishFeatures));
        Assert.False(creator.Allows(Privilege.AdminManageMembers));
        Assert.True(creator.WithheldByUserType(Privilege.AdminManageMembers));
    }

    [Fact]
    public void An_unknown_user_type_clamps_to_nothing_rather_than_failing_open()
    {
        // The opposite of the choice made for an unknown role, and deliberately
        // so: a ceiling that fails open is not a ceiling.
        Authorization unknown = Authorization.Resolve("enterprise-plus", [Roles.Administrator]);

        Assert.Empty(unknown.Privileges);
    }

    // ---------- sharing decides reading ----------

    private static Principal User(Guid id) => new(id, PrincipalKind.User, $"u{id:N}", null, false);

    [Fact]
    public void A_public_layer_is_readable_by_anonymous()
    {
        Assert.Equal(
            LayerAccess.Reason.Public,
            LayerAccess.Evaluate(
                SharingScope.Public, Guid.NewGuid(), Principal.Anonymous, Authorization.Nothing));
    }

    [Fact]
    public void An_organisation_layer_is_not_readable_by_anonymous()
    {
        Assert.Equal(
            LayerAccess.Reason.Denied,
            LayerAccess.Evaluate(
                SharingScope.Organization,
                Guid.NewGuid(),
                Principal.Anonymous,
                Authorization.Nothing));
    }

    [Fact]
    public void An_organisation_layer_is_readable_by_any_authenticated_principal()
    {
        Assert.Equal(
            LayerAccess.Reason.Organization,
            LayerAccess.Evaluate(
                SharingScope.Organization,
                Guid.NewGuid(),
                User(Guid.NewGuid()),
                Authorization.Resolve(UserTypes.Unrestricted, [Roles.Viewer])));
    }

    [Fact]
    public void A_private_layer_is_readable_by_its_owner()
    {
        Guid owner = Guid.NewGuid();

        Assert.Equal(
            LayerAccess.Reason.Owner,
            LayerAccess.Evaluate(SharingScope.Private, owner, User(owner), Authorization.Nothing));
    }

    [Fact]
    public void A_private_layer_is_not_readable_by_another_member()
    {
        Assert.Equal(
            LayerAccess.Reason.Denied,
            LayerAccess.Evaluate(
                SharingScope.Private,
                Guid.NewGuid(),
                User(Guid.NewGuid()),
                Authorization.Resolve(UserTypes.Unrestricted, [Roles.Publisher])));
    }

    [Fact]
    public void An_administrator_reads_a_private_layer_by_override_and_it_is_labelled_as_one()
    {
        // ADR-018 condition 3 depends on this being a distinct reason rather
        // than a boolean: the audit record exists only because the caller can
        // tell that the override was what allowed it.
        Assert.Equal(
            LayerAccess.Reason.AdministrativeOverride,
            LayerAccess.Evaluate(
                SharingScope.Private,
                Guid.NewGuid(),
                User(Guid.NewGuid()),
                Authorization.Resolve(UserTypes.Unrestricted, [Roles.Administrator])));
    }

    [Fact]
    public void An_administrator_reading_a_public_layer_is_not_recorded_as_using_the_override()
    {
        // Otherwise the audit trail fills with overrides that overrode nothing,
        // and the entries that matter stop standing out.
        Assert.Equal(
            LayerAccess.Reason.Public,
            LayerAccess.Evaluate(
                SharingScope.Public,
                Guid.NewGuid(),
                User(Guid.NewGuid()),
                Authorization.Resolve(UserTypes.Unrestricted, [Roles.Administrator])));
    }

    [Fact]
    public void An_ownerless_layer_is_private_to_nobody_rather_than_public_to_everybody()
    {
        // Layers registered before ownership existed have no owner. Getting this
        // wrong leaves a fleet of pre-upgrade layers readable by anyone.
        Assert.Equal(
            LayerAccess.Reason.Denied,
            LayerAccess.Evaluate(
                SharingScope.Private, null, User(Guid.NewGuid()), Authorization.Nothing));
    }

    // ---------- the code and the schema agree ----------

    [Fact]
    public void Every_role_and_user_type_the_code_knows_is_seeded_by_the_migration()
    {
        // The drift guard. The names live in the code that resolves privileges
        // and in the rows a grant references; when they disagree, a grant names
        // something the server does not know and the principal silently loses
        // what it carried.
        string sql = string.Join(
            "\n",
            PlatformMigrations.All.All.Single(m => m.Version.Value == 3).Statements);

        foreach (string name in Roles.All.Concat(UserTypes.All))
        {
            Assert.Contains($"'{name}'", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_sharing_scopes_the_code_knows_are_the_ones_the_check_constraint_allows()
    {
        string sql = string.Join(
            "\n",
            PlatformMigrations.All.All.Single(m => m.Version.Value == 5).Statements);

        foreach (SharingScope scope in Enum.GetValues<SharingScope>())
        {
            Assert.Contains(
                $"'{scope.ToString().ToLowerInvariant()}'", sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Neither_new_migration_closes_the_rollback_window(int version)
    {
        Migration migration = PlatformMigrations.All.All.Single(m => m.Version.Value == version);

        Assert.Equal(MigrationPhase.Expand, migration.Phase);
        Assert.Equal(SchemaVersion.None, migration.RaisesMinimumReaderTo);
    }

    [Fact]
    public void The_shipped_schema_version_matches_the_highest_migration()
    {
        Assert.Equal(
            PlatformMigrations.All.All.Max(m => m.Version.Value),
            PlatformMigrations.ComponentSchemaVersion.Value);
    }
}
