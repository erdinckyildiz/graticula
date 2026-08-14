using System;
using System.Collections.Immutable;
using System.Linq;
using GisServer.Platform.Identity;
using GisServer.Platform.Schema;
using Xunit;

namespace GisServer.Platform.Tests.Identity;

/// <summary>
/// The role set and what each role carries (ADR-018 §2).
/// </summary>
/// <remarks>
/// The role set is <c>INFERRED</c> and may change on one sentence from the
/// project owner. These tests are therefore written to state the decision rather
/// than to defend it: if the set changes, they should fail loudly and be edited
/// deliberately, not quietly still pass.
/// </remarks>
public sealed class RolesTests
{
    [Fact]
    public void The_role_set_is_the_four_ADR018_names()
    {
        // ToArray, because both sides would otherwise target-type to
        // ImmutableArray<string> — a struct whose Equals is identity on the
        // backing array, so the assertion compares the wrong thing and fails
        // while printing two identical collections.
        Assert.Equal(
            ["viewer", "publisher", "gis-administrator", "platform-administrator"],
            Roles.All.ToArray());
    }

    [Theory]
    [InlineData(Roles.Viewer, 1)]
    [InlineData(Roles.Publisher, 2)]
    [InlineData(Roles.GisAdministrator, 5)]
    [InlineData(Roles.PlatformAdministrator, 9)]
    public void Each_role_carries_the_count_the_ADR_table_shows(string role, int expected) =>
        Assert.Equal(expected, Roles.PermissionsOf(role).Count);

    [Fact]
    public void The_roles_nest_so_each_carries_everything_the_one_below_does()
    {
        // ADR-018 §2 claims nesting. Nesting is a claim that can be dropped later
        // without breaking a grant; adding it later would silently widen every
        // existing one. Either way it should be true when it is claimed.
        for (int i = 1; i < Roles.All.Length; i++)
        {
            ImmutableHashSet<Permission> lower = Roles.PermissionsOf(Roles.All[i - 1]);
            ImmutableHashSet<Permission> higher = Roles.PermissionsOf(Roles.All[i]);

            Assert.True(
                lower.IsSubsetOf(higher),
                $"'{Roles.All[i]}' does not carry everything '{Roles.All[i - 1]}' does: missing "
                + string.Join(", ", lower.Except(higher)));
        }
    }

    [Fact]
    public void The_platform_administrator_carries_every_permission_that_exists()
    {
        // The guard on adding a permission: a new enum member that no role grants
        // is a capability nobody can ever use, and the failure presents as an
        // endpoint that refuses everyone including the administrator.
        Assert.Equal(
            Enum.GetValues<Permission>().ToImmutableHashSet(),
            Roles.PermissionsOf(Roles.PlatformAdministrator));
    }

    [Fact]
    public void Publishing_hosted_data_does_not_carry_registering_a_data_source()
    {
        // ADR-018 §2a: different risks wearing the same word. Publishing puts a
        // file in our datastore; registering hands the server a credential to
        // somebody else's database, and every layer over it inherits that reach.
        ImmutableHashSet<Permission> publisher = Roles.PermissionsOf(Roles.Publisher);

        Assert.Contains(Permission.LayerPublishHosted, publisher);
        Assert.DoesNotContain(Permission.DataSourceRegister, publisher);
        Assert.DoesNotContain(Permission.LayerPublishRegistered, publisher);
    }

    [Fact]
    public void A_gis_administrator_cannot_manage_principals_or_operate_the_server()
    {
        ImmutableHashSet<Permission> gis = Roles.PermissionsOf(Roles.GisAdministrator);

        Assert.DoesNotContain(Permission.PrincipalManage, gis);
        Assert.DoesNotContain(Permission.RoleGrant, gis);
        Assert.DoesNotContain(Permission.ServerOperate, gis);
    }

    [Fact]
    public void An_unknown_role_confers_nothing_rather_than_throwing()
    {
        // A grant naming a role we do not know is a store written by a different
        // version. The safe reading of an unknown grant is that it confers
        // nothing; throwing would turn one stale row into a 500 on every request
        // by that principal.
        Assert.Empty(Roles.PermissionsOf("superuser"));
    }

    [Fact]
    public void An_authorization_built_from_no_roles_allows_nothing()
    {
        Authorization none = Authorization.FromRoles([]);

        foreach (Permission permission in Enum.GetValues<Permission>())
        {
            Assert.False(none.Allows(permission));
        }
    }

    [Fact]
    public void Several_roles_union_rather_than_the_highest_winning()
    {
        Authorization both = Authorization.FromRoles([Roles.Viewer, Roles.GisAdministrator]);

        Assert.True(both.Allows(Permission.LayerRead));
        Assert.True(both.Allows(Permission.DataSourceRegister));
        Assert.False(both.Allows(Permission.ServerOperate));
    }

    [Fact]
    public void An_unknown_role_alongside_a_known_one_does_not_discard_the_known_one()
    {
        Authorization mixed = Authorization.FromRoles(["superuser", Roles.Viewer]);

        Assert.True(mixed.Allows(Permission.LayerRead));
    }

    [Fact]
    public void Every_role_the_code_knows_is_seeded_by_the_migration()
    {
        // The drift guard. The role names live in two places — the code that
        // resolves permissions, and the rows a grant references — and when they
        // disagree a grant names a role the server does not know, so the
        // principal silently loses every permission it carried rather than
        // getting an error. Reading the migration is what makes this catch it.
        string sql = string.Join(
            "\n",
            PlatformMigrations.All.All
                .Single(m => m.Version.Value == 3)
                .Statements);

        foreach (string role in Roles.All)
        {
            Assert.Contains($"'{role}'", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_role_migration_is_expand_and_does_not_close_the_rollback_window()
    {
        // It only inserts rows, so a server built against schema 2 reads this
        // store perfectly well — it ignores a table it never queries. Raising
        // minimum_reader_version here would strand an older binary for nothing
        // (ADR-016 §4a).
        Migration roles = PlatformMigrations.All.All.Single(m => m.Version.Value == 3);

        Assert.Equal(MigrationPhase.Expand, roles.Phase);
        Assert.Equal(SchemaVersion.None, roles.RaisesMinimumReaderTo);
    }

    [Fact]
    public void The_shipped_schema_version_matches_the_highest_migration()
    {
        Assert.Equal(
            PlatformMigrations.All.All.Max(m => m.Version.Value),
            PlatformMigrations.ComponentSchemaVersion.Value);
    }
}
