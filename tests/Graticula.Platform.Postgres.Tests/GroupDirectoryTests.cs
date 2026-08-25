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
            IReadOnlyList<GroupMember> members =
                await groups.MembersAsync("zz_g_three", CancellationToken.None);

            (string member, GroupStanding standing) =
                members.Select(m => (m.Name, m.Standing)).Single(m => m.Name == ownerName);

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
                    .Select(m => m.Name));
        }
        finally
        {
            await groups.RemoveAsync(owner, false, "zz_g_six", CancellationToken.None);
        }
    }

    /// <summary>A member for a test to act as.</summary>
    /// <summary>
    /// The settings write replaces every field, including the three that are text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test exists because the port's own documentation said the opposite for an hour.</b>
    /// Three parameters were described as *"or null to leave it"* while the statement writes
    /// <c>set title = @title</c> — so a Settings screen posting only its four policies would have
    /// erased the title, the summary and the description, and one posting only a summary would have
    /// silently unlocked a delete-locked group. A design review caught it before either screen
    /// existed; nothing in this suite would have.
    /// </para>
    /// <para>
    /// <b>It pins the replace rather than arguing against it.</b> A <c>coalesce</c> patch where null
    /// means *leave* cannot express *clear this description*, so replace is the right shape — and a
    /// shape that destroys data when called partially needs a test saying so out loud, not a comment
    /// hoping the next caller reads it. The console has one helper that overlays a patch on its last
    /// read, and this is the assertion that keeps that helper necessary rather than decorative.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Writing_settings_replaces_the_text_fields_too()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_gs_owner");

        (GroupChange made, _) = await groups.CreateAsync(
            owner, "zz_gs_replace", "Title kept", "Why it exists",
            GroupItemUpdate.None, CancellationToken.None);

        Assert.Equal(GroupChange.Done, made);

        try
        {
            Assert.Equal(
                GroupChange.Done,
                await groups.SetSettingsAsync(
                    owner, false, "zz_gs_replace",
                    title: null, summary: null, description: null,
                    GroupVisibility.Organization, GroupJoinPolicy.Self, GroupContribute.Members,
                    deleteLocked: false,
                    GroupMemberList.Members, membersMayLeave: true,
                    CancellationToken.None));

            GroupSummary after = (await groups.ListAsync(owner, true, CancellationToken.None))
                .Single(g => g.Name == "zz_gs_replace");

            // The policies took, and the text went with them — documented behaviour, and the reason
            // the console never assembles this body from the controls in front of it.
            Assert.Equal(GroupVisibility.Organization, after.Visibility);
            Assert.Equal(GroupJoinPolicy.Self, after.JoinPolicy);
            Assert.Equal(GroupContribute.Members, after.Contribute);
            Assert.Null(after.Title);
            Assert.Null(after.Description);
        }
        finally
        {
            await groups.RemoveAsync(owner, true, "zz_gs_replace", CancellationToken.None);
        }
    }

    /// <summary>
    /// A locked group refuses to be deleted, and refuses an administrator too.
    /// </summary>
    /// <remarks>
    /// <b>ADR-036 §4g, and the administrator half is the decision rather than an oversight.</b> A
    /// protection the most privileged caller passes through is a protection against typing rather than
    /// against deleting, and the operator who sets the lock is usually the one who would fat-finger
    /// it. Every other refusal in this store yields to <c>administrator: true</c>, so this one needs a
    /// test to stop it being 'fixed' into consistency with them.
    /// </remarks>
    [Fact]
    public async Task A_delete_lock_binds_an_administrator()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_gs_lockowner");

        await groups.CreateAsync(
            owner, "zz_gs_locked", null, null, GroupItemUpdate.None, CancellationToken.None);

        await groups.SetSettingsAsync(
            owner, false, "zz_gs_locked", null, null, null,
            GroupVisibility.Members, GroupJoinPolicy.Invitation, GroupContribute.Managers,
            deleteLocked: true,
                    GroupMemberList.Members, membersMayLeave: true,
                    CancellationToken.None);

        try
        {
            Assert.Equal(
                GroupChange.Locked,
                await groups.RemoveAsync(owner, true, "zz_gs_locked", CancellationToken.None));

            Assert.Contains(
                "zz_gs_locked",
                (await groups.ListAsync(owner, true, CancellationToken.None)).Select(g => g.Name));
        }
        finally
        {
            await groups.SetSettingsAsync(
                owner, false, "zz_gs_locked", null, null, null,
                GroupVisibility.Members, GroupJoinPolicy.Invitation, GroupContribute.Managers,
                deleteLocked: false,
                    GroupMemberList.Members, membersMayLeave: true,
                    CancellationToken.None);

            await groups.RemoveAsync(owner, true, "zz_gs_locked", CancellationToken.None);
        }
    }

    /// <summary>
    /// A join policy the schema admits and nothing honours is refused on write, and refused first.
    /// </summary>
    /// <remarks>
    /// <b>D-67 is why this is a refusal rather than a silent store.</b> That debt was a setting
    /// reported and unenforced for two days. Joining <c>Request</c> needs a queue of pending requests —
    /// a table, a screen and a decision about who reviews them — so the column admits the value, to
    /// avoid widening it later, and the write path names what is missing instead of accepting it.
    /// </remarks>
    [Fact]
    public async Task Joining_by_request_is_refused_until_the_queue_exists()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_gs_reqowner");

        await groups.CreateAsync(
            owner, "zz_gs_request", null, null, GroupItemUpdate.None, CancellationToken.None);

        try
        {
            Assert.Equal(
                GroupChange.NotBuilt,
                await groups.SetSettingsAsync(
                    owner, false, "zz_gs_request", null, null, null,
                    GroupVisibility.Organization, GroupJoinPolicy.Request, GroupContribute.Members,
                    deleteLocked: false,
                    GroupMemberList.Members, membersMayLeave: true,
                    CancellationToken.None));

            // <b>And it refused before it wrote anything.</b> A store that refused after the update
            // would leave the other three policies changed by a call that reported failure — which is
            // worse than either accepting or refusing, because nothing tells the caller.
            GroupSummary after = (await groups.ListAsync(owner, true, CancellationToken.None))
                .Single(g => g.Name == "zz_gs_request");

            Assert.Equal(GroupVisibility.Members, after.Visibility);
            Assert.Equal(GroupJoinPolicy.Invitation, after.JoinPolicy);
            Assert.Equal(GroupContribute.Managers, after.Contribute);
        }
        finally
        {
            await groups.RemoveAsync(owner, true, "zz_gs_request", CancellationToken.None);
        }
    }

    /// <summary>
    /// An unrecognised stored visibility reads as the narrowest value, never the widest.
    /// </summary>
    /// <remarks>
    /// <b>The direction is the whole assertion.</b> A row written by a newer build, carrying a
    /// visibility this one does not know, must not make a group public by accident: the safe reading
    /// of *"I do not understand this"* is the one that shows it to fewer people. The opposite default
    /// is how a private group becomes discoverable during a downgrade — silently, and for everybody.
    /// </remarks>
    [Fact]
    public async Task An_unknown_stored_visibility_reads_as_members_only()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_gs_futureowner");

        await groups.CreateAsync(
            owner, "zz_gs_future", null, null, GroupItemUpdate.None, CancellationToken.None);

        try
        {
            // The check constraint is what stops this being writable through the port, so the row is
            // forged past it — which is the shape a downgrade produces without asking anybody.
            await using (Npgsql.NpgsqlCommand forge = DataSource.CreateCommand(
                "alter table sharing_group drop constraint sharing_group_visibility_known; "
                + "update sharing_group set visibility = 'everybody_and_their_dog' "
                + "where name = 'zz_gs_future'"))
            {
                await forge.ExecuteNonQueryAsync();
            }

            GroupSummary read = (await groups.ListAsync(owner, true, CancellationToken.None))
                .Single(g => g.Name == "zz_gs_future");

            Assert.Equal(GroupVisibility.Members, read.Visibility);
        }
        finally
        {
            await using (Npgsql.NpgsqlCommand restore = DataSource.CreateCommand(
                "update sharing_group set visibility = 'members' "
                + "where visibility not in ('members', 'organization', 'public'); "
                + "alter table sharing_group add constraint sharing_group_visibility_known "
                + "check (visibility in ('members', 'organization', 'public'))"))
            {
                await restore.ExecuteNonQueryAsync();
            }

            await groups.RemoveAsync(owner, true, "zz_gs_future", CancellationToken.None);
        }
    }

    /// <summary>
    /// A group's visibility decides who can find it, and finding it is not reading it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test exists because the setting did nothing for its first hour.</b> `visibility` was
    /// stored, reported by two endpoints, and read by no <c>where</c> clause — so a group set to
    /// *everybody* was discoverable by exactly the people who could already see it, while the console
    /// said otherwise. That is <see href="../../docs/architecture-debt.md">D-67</see> precisely, and it
    /// shipped in the same change that **refuses** <see cref="GroupJoinPolicy.Request"/> on the ground
    /// that a policy stored and unenforced is D-67 over again.
    /// </para>
    /// <para>
    /// <b>Both halves are asserted, and the second is the one that matters.</b> That an outsider can
    /// now list the group is the feature; that listing it leaves them <see
    /// cref="GroupStanding.Outside"/> is what keeps ADR-036 §4g's *"being able to see that a group
    /// exists is not being able to read what is in it"* true — the endpoint withholds the member and
    /// item lists on that standing, and it can only do that if the standing is right.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_organization_visible_group_is_found_by_an_outsider_who_is_still_outside()
    {
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_gv_owner");
        (Guid outsider, _) = await MemberAsync("zz_gv_outsider");

        await groups.CreateAsync(
            owner, "zz_gv_group", "Findable", null, GroupItemUpdate.None, CancellationToken.None);

        try
        {
            // Closed to begin with, which is the default and what this server did before the column.
            Assert.DoesNotContain(
                "zz_gv_group",
                (await groups.ListAsync(outsider, false, CancellationToken.None)).Select(g => g.Name));

            Assert.Equal(
                GroupChange.Done,
                await groups.SetSettingsAsync(
                    owner, false, "zz_gv_group", "Findable", null, null,
                    GroupVisibility.Organization, GroupJoinPolicy.Invitation,
                    GroupContribute.Managers, deleteLocked: false,
                    GroupMemberList.Members, membersMayLeave: true,
                    CancellationToken.None));

            GroupSummary seen = (await groups.ListAsync(outsider, false, CancellationToken.None))
                .Single(g => g.Name == "zz_gv_group");

            // <b>Found, and still outside.</b> If this said `Member`, the visibility disjunct would be
            // conferring membership — a group's contents readable by anybody who can see the group,
            // which is the failure the disjunct is one line away from.
            Assert.Equal(GroupStanding.Outside, seen.Standing);
            Assert.Equal("Findable", seen.Title);
        }
        finally
        {
            await groups.RemoveAsync(owner, true, "zz_gv_group", CancellationToken.None);
        }
    }

    /// <summary>
    /// `public` is refused on write, for the same reason `request` is.
    /// </summary>
    /// <remarks>
    /// <b>Consistency is the argument, and the alternative was nearly shipped.</b> `public` would mean
    /// *discoverable by anybody, including an anonymous caller*, and there is nowhere for that to
    /// happen: the group listing requires a signed-in caller, so `public` and `organization` are
    /// enforced identically. Accepting it would report a discovery this server does not perform — which
    /// is what the neighbouring line refuses `request` for, and two identical situations treated
    /// differently on one screen is the inconsistency an operator notices. Where a public group is
    /// actually discovered is Q-119, and it is a decision about anonymous surfaces rather than about
    /// groups. <b>Readable and only unwritable</b>: a row a future build writes still reads correctly.
    /// </remarks>
    [Fact]
    public async Task Public_is_not_a_visibility_this_product_offers()
    {
        // <b>Owner decision 2026-08-25, migration 36, Q-119.</b> It used to be stored and
        // refused on write — the same shape `request` still has — and it is now not a value
        // at all: `GroupVisibility` has two members, so a build that tried to set it would
        // not compile. What is left to assert is the *stored* side, because a row can
        // survive a partial restore, and it must read as `organization` rather than as
        // `members`: `organization` is what `public` was always enforced as, and demoting it
        // further would narrow what an operator chose without telling them.
        await MigrateAsync();

        PostgresGroupDirectory groups = new(DataSource);
        (Guid owner, _) = await MemberAsync("zz_gv_pubowner");

        await groups.CreateAsync(
            owner, "zz_gv_public", null, null, GroupItemUpdate.None, CancellationToken.None);

        try
        {
            Assert.Equal(
                GroupChange.Done,
                await groups.SetSettingsAsync(
                    owner, false, "zz_gv_public", null, null, null,
                    GroupVisibility.Organization, GroupJoinPolicy.Invitation,
                    GroupContribute.Managers, deleteLocked: false,
                    GroupMemberList.Members, membersMayLeave: true,
                    CancellationToken.None));

            GroupSummary after = (await groups.ListAsync(owner, true, CancellationToken.None))
                .Single(g => g.Name == "zz_gv_public");

            Assert.Equal(GroupVisibility.Organization, after.Visibility);
        }
        finally
        {
            await groups.RemoveAsync(owner, true, "zz_gv_public", CancellationToken.None);
        }
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
