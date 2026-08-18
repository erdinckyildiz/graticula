using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Groups, and every rule ADR-036 says the store must keep.
/// </summary>
/// <remarks>
/// <b>The membership axis is the subject.</b> ADR-036 §3 gives an authorization answer two axes —
/// what a role grants, and where the principal stands in *this* group — and condition 2 asks that
/// the second one cannot turn out to be global. That is a property of these methods, so it is tested
/// here rather than through a screen.
/// </remarks>
public sealed class GroupDirectoryTests : PostgresFixture
{
    /// <summary>
    /// A group's members are managed by its owner and its managers, and by nobody else.
    /// </summary>
    /// <remarks>
    /// <b>ADR-036 condition 2.</b> The privilege to manage a group's members is not the privilege to
    /// manage every group's members. A directory that took a group id and trusted its caller would
    /// leave this to whoever wrote the endpoint, which is the escalation this decision would have
    /// introduced.
    /// </remarks>
    [Fact]
    public async Task Only_an_owner_a_manager_or_an_administrator_manages_a_groups_members()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, string ownerName) = await MemberAsync("zz_g_owner");
        (Guid helper, string helperName) = await MemberAsync("zz_g_helper");
        (Guid stranger, string strangerName) = await MemberAsync("zz_g_stranger");

        (GroupChange made, _) = await groups.CreateAsync(
            owner, "zz_g_one", "One", null, GroupItemUpdate.None, CancellationToken.None);

        Assert.Equal(GroupChange.Done, made);

        // A stranger with every privilege in the world is still outside this group.
        Assert.Equal(
            GroupChange.NotYours,
            await groups.SetMemberAsync(
                stranger, false, "zz_g_one", helperName, false, CancellationToken.None));

        // The owner may.
        Assert.Equal(
            GroupChange.Done,
            await groups.SetMemberAsync(
                owner, false, "zz_g_one", helperName, true, CancellationToken.None));

        // And now the helper is a manager, so they may too.
        Assert.Equal(
            GroupChange.Done,
            await groups.SetMemberAsync(
                helper, false, "zz_g_one", strangerName, false, CancellationToken.None));

        // A plain member may not: `stranger` was added as a member, not a manager.
        Assert.Equal(
            GroupChange.NotYours,
            await groups.SetMemberAsync(
                stranger, false, "zz_g_one", ownerName, false, CancellationToken.None));

        // An administrator may, whoever they are — a group whose owner has left must stay
        // administrable (ADR-036 §3).
        Assert.Equal(
            GroupChange.Done,
            await groups.SetMemberAsync(
                stranger, true, "zz_g_one", helperName, false, CancellationToken.None));
    }

    /// <summary>
    /// Deleting is the owner's, and a manager may not.
    /// </summary>
    /// <remarks>
    /// <b>The difference between delegating work and delegating control — ADR-036 §3.</b> A model
    /// that let a manager delete would make every helper a potential deleter, and the owner would
    /// have no way to grant the one without the other.
    /// </remarks>
    [Fact]
    public async Task A_manager_may_not_delete_the_group()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, _) = await MemberAsync("zz_g2_owner");
        (Guid manager, string managerName) = await MemberAsync("zz_g2_manager");

        await groups.CreateAsync(
            owner, "zz_g_two", null, null, GroupItemUpdate.None, CancellationToken.None);

        await groups.SetMemberAsync(
            owner, false, "zz_g_two", managerName, true, CancellationToken.None);

        Assert.Equal(
            GroupChange.OwnerOnly,
            await groups.RemoveAsync(manager, false, "zz_g_two", CancellationToken.None));

        Assert.Equal(
            GroupChange.Done,
            await groups.RemoveAsync(owner, false, "zz_g_two", CancellationToken.None));
    }

    /// <summary>
    /// The owner is a manager of their own group from the moment it exists.
    /// </summary>
    /// <remarks>
    /// <b>Otherwise a list filtered by membership excludes a group somebody owns</b>, and every screen
    /// has to special-case the owner. Asserted because it is a row written by the create path and
    /// nothing else would notice it missing.
    /// </remarks>
    [Fact]
    public async Task Creating_a_group_puts_its_owner_in_it()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, string ownerName) = await MemberAsync("zz_g3_owner");

        await groups.CreateAsync(
            owner, "zz_g_three", null, null, GroupItemUpdate.None, CancellationToken.None);

        try
        {
            IReadOnlyList<(string Member, GroupStanding Standing)> members =
                await groups.MembersAsync("zz_g_three", CancellationToken.None);

            (string member, GroupStanding standing) =
                members.Single(m => m.Member == ownerName);

            Assert.Equal(ownerName, member);

            // <b>Reported as owner, not manager.</b> The row says manager so that the membership
            // subquery finds them; the read reports the higher standing, because *owner* is what a
            // screen has to show to know whether Delete belongs on it.
            Assert.Equal(GroupStanding.Owner, standing);

            // And a list filtered by membership includes it.
            Assert.Contains(
                "zz_g_three",
                (await groups.ListAsync(owner, false, CancellationToken.None)).Select(g => g.Name));
        }
        finally
        {
            await groups.RemoveAsync(owner, false, "zz_g_three", CancellationToken.None);
        }
    }

    /// <summary>
    /// A group's capability is what it was created with, and nothing offers to change it.
    /// </summary>
    /// <remarks>
    /// <b>ADR-036 condition 3.</b> There is deliberately no method to change it — the immutability is
    /// the absence of a write path rather than a refusal in one, which is the stronger form. This
    /// test asserts the absence by reading the value back and by there being no setter to call: if
    /// somebody adds one, they have to delete this test's remarks to do it.
    /// </remarks>
    [Theory]
    [InlineData(GroupItemUpdate.None)]
    [InlineData(GroupItemUpdate.OwnItems)]
    [InlineData(GroupItemUpdate.AllItems)]
    public async Task A_groups_capability_is_fixed_at_creation(GroupItemUpdate wanted)
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, _) = await MemberAsync("zz_g4_owner");

        string name = "zz_g_four_" + wanted.ToString().ToLowerInvariant();

        await groups.CreateAsync(
            owner, name, null, null, wanted, CancellationToken.None);

        try
        {
            GroupSummary made = (await groups.ListAsync(owner, false, CancellationToken.None))
                .Single(g => g.Name == name);

            Assert.Equal(wanted, made.ItemUpdate);
        }
        finally
        {
            await groups.RemoveAsync(owner, false, name, CancellationToken.None);
        }
    }

    /// <summary>Two groups cannot share a name, whatever the case.</summary>
    [Fact]
    public async Task A_name_is_taken_regardless_of_case()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, _) = await MemberAsync("zz_g5_owner");

        (GroupChange first, _) = await groups.CreateAsync(
            owner, "zz_g_Five", null, null, GroupItemUpdate.None, CancellationToken.None);

        Assert.Equal(GroupChange.Done, first);

        try
        {
            (GroupChange again, _) = await groups.CreateAsync(
                owner, "zz_g_five", null, null, GroupItemUpdate.None, CancellationToken.None);

            Assert.Equal(GroupChange.Exists, again);
        }
        finally
        {
            await groups.RemoveAsync(owner, false, "zz_g_Five", CancellationToken.None);
        }
    }

    /// <summary>
    /// A group's owner cannot be removed from it.
    /// </summary>
    /// <remarks>
    /// <b>They would keep owning it and stop appearing in it</b>, which is a state nothing else in
    /// this schema can produce and no screen would explain.
    /// </remarks>
    [Fact]
    public async Task The_owner_cannot_be_removed_from_their_own_group()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, string ownerName) = await MemberAsync("zz_g6_owner");

        await groups.CreateAsync(
            owner, "zz_g_six", null, null, GroupItemUpdate.None, CancellationToken.None);

        try
        {
            Assert.Equal(
                GroupChange.NoSuchTarget,
                await groups.RemoveMemberAsync(
                    owner, false, "zz_g_six", ownerName, CancellationToken.None));

            Assert.Contains(
                ownerName,
                (await groups.MembersAsync("zz_g_six", CancellationToken.None))
                    .Select(m => m.Member));
        }
        finally
        {
            await groups.RemoveAsync(owner, false, "zz_g_six", CancellationToken.None);
        }
    }

    /// <summary>A member for a test to act as.</summary>
    private async Task<(Guid Id, string Name)> MemberAsync(string name)
    {
        await using Npgsql.NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) "
            + "values (@id, 'user', @name, 'creator') "
            + "on conflict (name) do update set name = excluded.name returning id");

        Guid id = Guid.NewGuid();
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);

        object? made = await command.ExecuteScalarAsync(CancellationToken.None);

        return (made is Guid existing ? existing : id, name);
    }
}
