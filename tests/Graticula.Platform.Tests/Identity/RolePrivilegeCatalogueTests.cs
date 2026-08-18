using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// The privilege catalogue, which became a public contract on 2026-08-18.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-035 condition 3.</b> Q-59 predicted this and was overruled: from the first deployment that
/// writes a role against these names, a rename or a removal silently changes what an existing role
/// confers. Nothing in a compiler notices that. This class is what notices.
/// </para>
/// <para>
/// <b>The expected list is written out rather than derived.</b> A test that reads the names from the
/// enum asserts that the enum equals itself. The point is to make removing a name a deliberate act
/// with a failing build in front of it, so the list has to be a second, independent statement.
/// </para>
/// </remarks>
public sealed class RolePrivilegeCatalogueTests
{
    /// <summary>Every privilege name, as of ADR-035. Adding to this list is fine; removing is not.</summary>
    private static readonly string[] Expected =
    [
        "content:create",
        "content:publishFeatures",
        "content:publishTiles",
        "content:registerDataStore",
        "features:edit",
        "features:fullEdit",
        "sharing:shareToOrganization",
        "sharing:shareToPublic",
        "groups:create",
        "groups:deleteOwn",
        "groups:manageMembers",
        "groups:shareTo",
        "admin:manageMembers",
        "admin:manageRoles",
        "admin:viewAllContent",
        "admin:manageAllContent",
        "admin:manageSecurity",
        "admin:manageServer",
    ];

    /// <summary>
    /// No privilege name has disappeared or been renamed.
    /// </summary>
    /// <remarks>
    /// <b>The failure message is the point.</b> A test that says *"expected 18, got 17"* sends
    /// somebody to count; one that names the missing privilege and says what it means for existing
    /// grants tells them what they broke.
    /// </remarks>
    [Fact]
    public void Every_privilege_name_this_catalogue_has_published_still_exists()
    {
        HashSet<string> present = [.. Roles.AllPrivileges.Select(Roles.NameOf)];

        string[] gone = [.. Expected.Where(name => !present.Contains(name))];

        Assert.True(
            gone.Length == 0,
            "These privilege names no longer exist: " + string.Join(", ", gone)
            + ". From the first deployment that wrote a role against them (ADR-035), each is a "
            + "grant somebody holds — removing or renaming one silently changes what their role "
            + "confers, and nothing else would notice. If the removal is deliberate it needs a "
            + "migration that rewrites the affected rows, and this list needs editing in the same "
            + "commit.");
    }

    /// <summary>Two privileges cannot share a name.</summary>
    [Fact]
    public void No_two_privileges_share_a_name()
    {
        Dictionary<string, int> counted = [];

        foreach (Privilege privilege in Roles.AllPrivileges)
        {
            string name = Roles.NameOf(privilege);
            counted[name] = counted.GetValueOrDefault(name) + 1;
        }

        string[] shared = [.. counted.Where(e => e.Value > 1).Select(e => e.Key)];

        Assert.True(
            shared.Length == 0,
            "Two privileges answer to the same name: " + string.Join(", ", shared)
            + ". A stored grant would then confer whichever one the parser happened to return.");
    }

    /// <summary>Every name round-trips through the parser.</summary>
    /// <remarks>
    /// <b>Both directions, because the store writes one and reads the other.</b> A privilege whose
    /// name cannot be parsed back is a grant that is silently dropped on read — the row exists, the
    /// screen shows the tick, and the check refuses.
    /// </remarks>
    [Fact]
    public void Every_name_parses_back_to_the_privilege_it_came_from()
    {
        foreach (Privilege privilege in Roles.AllPrivileges)
        {
            string name = Roles.NameOf(privilege);

            Assert.True(
                Roles.TryParsePrivilege(name, out Privilege back),
                $"'{name}' is written by NameOf and not read by TryParsePrivilege, so a role "
                + "granted it would appear to have it and not have it.");

            Assert.Equal(privilege, back);
        }
    }

    /// <summary>An unknown name is refused rather than guessed at.</summary>
    [Theory]
    [InlineData("content:invent")]
    [InlineData("")]
    [InlineData("CONTENT:CREATE")]
    [InlineData("content:Create")]
    public void An_unknown_name_is_not_parsed(string name)
    {
        // <b>Case-sensitive, deliberately.</b> These are stored values compared by a database with
        // an ordinal collation; accepting `CONTENT:CREATE` here would create two spellings of one
        // grant and the primary key would let both exist.
        Assert.False(Roles.TryParsePrivilege(name, out _));
    }

    /// <summary>
    /// Every prerequisite and implication names privileges that exist, and neither is circular.
    /// </summary>
    /// <remarks>
    /// <b>ADR-035 condition 6, the half a reader can check by reading.</b> The other half — that the
    /// rules are enforced — is <c>RoleDirectoryTests</c>'s.
    /// </remarks>
    [Fact]
    public void The_dependency_rules_refer_only_to_privileges_that_exist()
    {
        HashSet<Privilege> all = [.. Roles.AllPrivileges];

        foreach ((Privilege privilege, ImmutableArray<Privilege> needs) in Roles.Prerequisites)
        {
            Assert.Contains(privilege, all);

            foreach (Privilege need in needs)
            {
                Assert.Contains(need, all);

                Assert.NotEqual(privilege, need);

                // A requires B and B requires A is a pair nobody can ever grant.
                Assert.False(
                    Roles.Prerequisites.TryGetValue(need, out ImmutableArray<Privilege> back)
                    && back.Contains(privilege),
                    $"{Roles.NameOf(privilege)} and {Roles.NameOf(need)} require each other, so "
                    + "neither can be granted without the other and the refusal fires whichever "
                    + "order they arrive in.");
            }
        }

        foreach ((Privilege wider, ImmutableArray<Privilege> narrower) in Roles.Implies)
        {
            Assert.Contains(wider, all);

            foreach (Privilege inner in narrower)
            {
                Assert.Contains(inner, all);
                Assert.NotEqual(wider, inner);

                // <b>An implication must not also be a prerequisite.</b> One says *you already have
                // it*, the other says *you must also tick it* — together they would refuse a role
                // for lacking something it holds by implication.
                Assert.False(
                    Roles.Prerequisites.TryGetValue(wider, out ImmutableArray<Privilege> needs)
                    && needs.Contains(inner),
                    $"{Roles.NameOf(wider)} both implies and requires {Roles.NameOf(inner)}.");
            }
        }
    }

    /// <summary>
    /// The two sections cover every privilege and overlap nowhere.
    /// </summary>
    /// <remarks>
    /// <b>ADR-035 §4f: the role editor has two sections and a privilege has to be in one.</b> A
    /// privilege in neither is one no screen offers, which is how a grant becomes unreachable.
    /// </remarks>
    [Fact]
    public void Every_privilege_belongs_to_exactly_one_section()
    {
        int administrative = Roles.AllPrivileges.Count(Roles.IsAdministrative);
        int general = Roles.AllPrivileges.Length - administrative;

        Assert.True(administrative > 0, "No privilege is administrative, so Server has no section.");
        Assert.True(general > 0, "No privilege is general, so Studio has no section.");

        // The one crossing the line, and it is deliberate: named for content, granted to the
        // administrator by owner decision 2026-08-17. Asserted so that moving it back is a decision.
        Assert.True(
            Roles.IsAdministrative(Privilege.ContentRegisterDataStore),
            "content:registerDataStore is not administrative here. The owner moved it to the "
            + "administrator on 2026-08-17 — *\"data sources studio'nun değil server'in bir "
            + "seçeneği\"* — and the reference lists it under General. If it moves back, ADR-034 "
            + "§5c and ADR-035 §4f both need amending.");
    }

    /// <summary>
    /// The compiled grants — which seed the store — are what they were before ADR-035.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-035 condition 1, the half that can be checked without a database.</b> Migration 25
    /// writes <see cref="Roles.Grants"/> into <c>role_privilege</c>, so an upgrading deployment gets
    /// exactly these. A change here silently changes what every existing role confers on upgrade.
    /// </para>
    /// <para>
    /// <b>Written out, not derived, for the reason the name list is.</b> And the four group
    /// privileges are deliberately absent from every role: ADR-035 §4c defines them and migration 25
    /// grants them to nobody, so the upgrade cannot widen anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_seed_grants_are_exactly_what_the_five_roles_conferred_before()
    {
        Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
        {
            ["viewer"] = [],
            ["data_editor"] = ["features:edit"],
            ["user"] = ["content:create", "sharing:shareToOrganization"],
            ["publisher"] =
            [
                "content:create", "content:publishFeatures", "content:publishTiles",
                "features:edit", "features:fullEdit", "sharing:shareToOrganization",
                "sharing:shareToPublic",
            ],
            ["administrator"] =
            [
                "admin:manageAllContent", "admin:manageMembers", "admin:manageRoles",
                "admin:manageSecurity", "admin:manageServer", "admin:viewAllContent",
                "content:create", "content:publishFeatures", "content:publishTiles",
                "content:registerDataStore", "features:edit", "features:fullEdit",
                "sharing:shareToOrganization", "sharing:shareToPublic",
            ],
        };

        Assert.Equal(expected.Count, Roles.All.Length);

        foreach ((string role, string[] names) in expected)
        {
            string[] actual =
            [
                .. Roles.PrivilegesOf(role).Select(Roles.NameOf)
                    .OrderBy(n => n, StringComparer.Ordinal),
            ];

            Assert.Equal(
                names.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                actual);
        }

        // No built-in role receives a group privilege at seed time.
        foreach (string role in Roles.All)
        {
            foreach (Privilege held in Roles.PrivilegesOf(role))
            {
                Assert.DoesNotContain("groups:", Roles.NameOf(held), StringComparison.Ordinal);
            }
        }
    }
}
