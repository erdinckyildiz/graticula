using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Who may see a group's members, and whether a member may leave it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner decision 2026-08-25, from ArcGIS's group settings — migration 37,
/// [ADR-036](../../docs/adr/ADR-036-groups.md) §4a-ii.</b> Two settings, and the second one
/// needed a capability built before it could mean anything: until this change there was no way
/// to leave a group at all, so *members cannot leave* would have been a checkbox governing
/// nothing — the same defect `item_update` had for as long as it existed.
/// </para>
/// <para>
/// <b>Leaving is tested here rather than only through the endpoint</b>, because the refusals
/// are decided in one SQL statement and the interesting ones are the two that look alike from
/// outside: *not a member* and *not allowed to leave* must not be distinguishable to somebody
/// outside the group, and must be distinguishable to somebody inside it.
/// </para>
/// </remarks>
public sealed class GroupMemberListAndLeavingTests : PostgresFixture
{
    [Fact]
    public async Task The_member_list_setting_defaults_to_members_and_round_trips()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_ml_owner");

        (GroupChange made, _) = await groups.CreateAsync(
            owner, "zz_ml_one", "One", null, GroupItemUpdate.None, CancellationToken.None);

        Assert.Equal(GroupChange.Done, made);

        // <b>Every group that existed before migration 37 reads as this.</b> The default is the
        // wider value because it is what the group already did; the narrower one would have
        // silently changed every existing group on upgrade.
        GroupSummary before = await OneAsync(groups, owner, "zz_ml_one");

        Assert.Equal(GroupMemberList.Members, before.MemberList);
        Assert.True(before.MembersMayLeave);

        Assert.Equal(
            GroupChange.Done,
            await groups.SetSettingsAsync(
                owner, false, "zz_ml_one", null, null, null,
                GroupVisibility.Members, GroupJoinPolicy.Invitation, GroupContribute.Managers,
                deleteLocked: false,
                GroupMemberList.Managers, membersMayLeave: false,
                CancellationToken.None));

        GroupSummary after = await OneAsync(groups, owner, "zz_ml_one");

        Assert.Equal(GroupMemberList.Managers, after.MemberList);
        Assert.False(after.MembersMayLeave);
    }

    [Fact]
    public async Task A_member_leaves_and_stops_being_one()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        PostgresIdentityStore identity = new(DataSource);

        (Guid owner, _) = await MemberAsync("zz_lv_owner");
        (Guid member, string memberName) = await MemberAsync("zz_lv_member");

        (GroupChange made, Guid id) = await groups.CreateAsync(
            owner, "zz_lv_one", "One", null, GroupItemUpdate.None, CancellationToken.None);

        Assert.Equal(GroupChange.Done, made);

        Assert.Equal(
            GroupChange.Done,
            await groups.SetMemberAsync(
                owner, false, "zz_lv_one", memberName, false, CancellationToken.None));

        (_, _, IReadOnlyList<Guid> inside, _) =
            await identity.GrantsOfAsync(member, CancellationToken.None);

        Assert.Contains(id, inside);

        Assert.Equal(
            GroupChange.Done,
            await groups.LeaveAsync(member, "zz_lv_one", CancellationToken.None));

        // <b>The point, and it is the reading that matters.</b> Leaving is not cosmetic: what was
        // shared with the group stops being readable, which is what the group conferred.
        (_, _, IReadOnlyList<Guid> after, _) =
            await identity.GrantsOfAsync(member, CancellationToken.None);

        Assert.DoesNotContain(id, after);
    }

    [Fact]
    public async Task An_administrative_group_refuses_and_says_which_refusal_it_is()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);

        (Guid owner, _) = await MemberAsync("zz_ag_owner");
        (Guid member, string memberName) = await MemberAsync("zz_ag_member");
        (Guid stranger, _) = await MemberAsync("zz_ag_stranger");

        Assert.Equal(
            GroupChange.Done,
            (await groups.CreateAsync(
                owner, "zz_ag_one", "One", null, GroupItemUpdate.None,
                CancellationToken.None)).Outcome);

        Assert.Equal(
            GroupChange.Done,
            await groups.SetMemberAsync(
                owner, false, "zz_ag_one", memberName, false, CancellationToken.None));

        Assert.Equal(
            GroupChange.Done,
            await groups.SetSettingsAsync(
                owner, false, "zz_ag_one", null, null, null,
                GroupVisibility.Members, GroupJoinPolicy.Invitation, GroupContribute.Managers,
                deleteLocked: false,
                GroupMemberList.Members, membersMayLeave: false,
                CancellationToken.None));

        // A member is told the group decides.
        Assert.Equal(
            GroupChange.Locked,
            await groups.LeaveAsync(member, "zz_ag_one", CancellationToken.None));

        // <b>And a stranger is told nothing.</b> `Absent` for somebody outside, whatever the
        // setting says — otherwise the refusal is an oracle for which groups exist.
        Assert.Equal(
            GroupChange.Absent,
            await groups.LeaveAsync(stranger, "zz_ag_one", CancellationToken.None));

        // The owner's refusal is its own, because the way out of it is different.
        Assert.Equal(
            GroupChange.OwnerOnly,
            await groups.LeaveAsync(owner, "zz_ag_one", CancellationToken.None));
    }

    [Fact]
    public async Task The_owner_cannot_leave_even_when_the_group_allows_it()
    {
        // <b>D-14's shape one level down.</b> A group whose owner has walked out has nobody who
        // can administer it; transfer or delete instead, and both already exist.
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_ol_owner");

        Assert.Equal(
            GroupChange.Done,
            (await groups.CreateAsync(
                owner, "zz_ol_one", "One", null, GroupItemUpdate.None,
                CancellationToken.None)).Outcome);

        Assert.Equal(
            GroupChange.OwnerOnly,
            await groups.LeaveAsync(owner, "zz_ol_one", CancellationToken.None));
    }

    [Fact]
    public async Task Leaving_a_group_that_does_not_exist_reads_as_absent()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid somebody, _) = await MemberAsync("zz_nx_somebody");

        Assert.Equal(
            GroupChange.Absent,
            await groups.LeaveAsync(somebody, "zz_nx_no_such_group", CancellationToken.None));
    }

    private static async Task<GroupSummary> OneAsync(
        PostgresGroupDirectory groups, Guid who, string name)
    {
        foreach (GroupSummary each in
            await groups.ListAsync(who, false, CancellationToken.None))
        {
            if (string.Equals(each.Name, name, StringComparison.Ordinal))
            {
                return each;
            }
        }

        throw new InvalidOperationException($"'{name}' was not listed for this caller.");
    }

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
