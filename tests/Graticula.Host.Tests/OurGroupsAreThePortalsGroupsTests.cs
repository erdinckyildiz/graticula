using System;
using System.Reflection;
using Graticula.Host;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-036's groups, published as portal groups, and the four places the mapping is
/// imperfect held still.
/// </summary>
/// <remarks>
/// <para>
/// <b>The endpoint answered with an empty list until 2026-09-09, and the emptiness
/// was the fault.</b> Our groups are real and a service can be shared with one, so
/// <em>this portal has no groups</em> was a false claim rather than a missing
/// feature — [Q-127](../../docs/open-questions.md), closed by owner decision:
/// <em>gruplarımızı göster</em>.
/// </para>
/// <para>
/// <b>What is worth asserting is the mapping, not the plumbing.</b> Most of it is
/// exact; four fields are not, and a documented imperfection that nothing holds still
/// is a comment somebody will contradict. So the four are pinned here beside the
/// exact ones, and the last test asserts the property that keeps a group from being
/// mistaken for an item by the search both share.
/// </para>
/// </remarks>
public sealed class OurGroupsAreThePortalsGroupsTests
{
    private static readonly Guid Id = new("3f2b1c4d5e6f78901a2b3c4d5e6f7081");

    private static GroupSummary Group(
        string name = "planning",
        string? title = "Planning",
        string? owner = "root",
        GroupItemUpdate itemUpdate = GroupItemUpdate.None,
        GroupStanding standing = GroupStanding.Member,
        GroupVisibility visibility = GroupVisibility.Members,
        GroupJoinPolicy joinPolicy = GroupJoinPolicy.Invitation,
        GroupContribute contribute = GroupContribute.Managers,
        GroupMemberList memberList = GroupMemberList.Members,
        bool deleteLocked = false,
        bool membersMayLeave = true,
        DateTimeOffset createdAt = default) => new(
            Id,
            name,
            title,
            "What it is for",
            owner,
            itemUpdate,
            Members: 4,
            Items: 2,
            standing,
            Summary: "A short line",
            visibility,
            joinPolicy,
            contribute,
            deleteLocked,
            createdAt,
            memberList,
            membersMayLeave);

    private static object? Field(object group, string name) =>
        group.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?.GetValue(group);

    private static object Portal(GroupSummary group) =>
        PortalGroup.Of(group, "A1B2C3D4E5F60718", "erdinc");

    [Fact]
    public void The_id_is_the_groups_own_id_spelled_the_way_a_portal_spells_one()
    {
        // 32 hexadecimal characters, which is what an Esri id is — so nothing new is
        // minted and there is nothing to keep in step with the group.
        Assert.Equal("3f2b1c4d5e6f78901a2b3c4d5e6f7081", Field(Portal(Group()), "id"));
    }

    [Fact]
    public void The_title_is_the_label_when_there_is_one_and_the_name_when_there_is_not()
    {
        Assert.Equal("Planning", Field(Portal(Group()), "title"));
        Assert.Equal("planning", Field(Portal(Group(title: null)), "title"));
    }

    [Fact]
    public void An_ownerless_group_is_the_products_and_not_somebody_elses()
    {
        // The same rule an item follows when nobody owns it. A group whose owning
        // principal has been deleted must not borrow the caller's name.
        Assert.Equal("root", Field(Portal(Group()), "owner"));
        Assert.Equal("graticula", Field(Portal(Group(owner: null)), "owner"));
    }

    [Theory]
    [InlineData(GroupVisibility.Members, "private")]
    [InlineData(GroupVisibility.Organization, "org")]
    public void Visibility_is_the_portals_word_for_it(GroupVisibility visibility, string access)
    {
        Assert.Equal(access, Field(Portal(Group(visibility: visibility)), "access"));
    }

    [Fact]
    public void No_group_is_ever_public_because_this_server_has_no_such_visibility()
    {
        // Q-119 removed the *anybody, including anonymous* visibility on 2026-08-25
        // because nothing honoured it. The enumeration having two values rather than
        // three is the guarantee; this asserts the guarantee is what is published.
        foreach (GroupVisibility visibility in Enum.GetValues<GroupVisibility>())
        {
            Assert.NotEqual("public", Field(Portal(Group(visibility: visibility)), "access"));
        }
    }

    [Theory]
    [InlineData(GroupStanding.Owner, "owner")]
    [InlineData(GroupStanding.Manager, "admin")]
    [InlineData(GroupStanding.Member, "member")]
    [InlineData(GroupStanding.Outside, "none")]
    public void Every_standing_has_a_member_type_and_none_is_a_default(
        GroupStanding standing, string memberType)
    {
        object membership = Field(Portal(Group(standing: standing)), "userMembership")!;

        Assert.Equal(memberType, Field(membership, "memberType"));
        Assert.Equal("erdinc", Field(membership, "username"));
    }

    [Theory]
    [InlineData(GroupJoinPolicy.Invitation, true, false)]
    [InlineData(GroupJoinPolicy.Request, false, false)]
    [InlineData(GroupJoinPolicy.Self, false, true)]
    public void The_three_join_policies_are_the_portals_two_flags(
        GroupJoinPolicy policy, bool invitationOnly, bool autoJoin)
    {
        // Neither flag set is the reference's own *ask and be approved*, so the
        // three-way map is exact rather than lossy.
        object portal = Portal(Group(joinPolicy: policy));

        Assert.Equal(invitationOnly, Field(portal, "isInvitationOnly"));
        Assert.Equal(autoJoin, Field(portal, "autoJoin"));
    }

    [Theory]
    [InlineData(GroupContribute.Managers, true)]
    [InlineData(GroupContribute.Members, false)]
    public void Who_may_contribute_is_the_view_only_flag(
        GroupContribute contribute, bool viewOnly)
    {
        Assert.Equal(viewOnly, Field(Portal(Group(contribute: contribute)), "isViewOnly"));
    }

    [Fact]
    public void The_settings_a_portal_group_also_has_are_carried_rather_than_dropped()
    {
        object portal = Portal(Group(
            memberList: GroupMemberList.Managers,
            deleteLocked: true,
            membersMayLeave: false));

        Assert.Equal(true, Field(portal, "hiddenMembers"));
        Assert.Equal(true, Field(portal, "protected"));
        Assert.Equal(true, Field(portal, "leavingDisallowed"));
    }

    [Fact]
    public void Seeing_a_group_is_not_reading_it_so_no_membership_leaves_with_it()
    {
        // ADR-036 §4g. An organisation-visible group reaches a non-member with
        // standing Outside, and what they learn is that it exists. A member count
        // would be a disclosure about the people in it, and an item list would be
        // the thing membership is for.
        object portal = Portal(Group(standing: GroupStanding.Outside));

        Assert.Null(Field(portal, "members"));
        Assert.Null(Field(portal, "items"));
        Assert.Null(Field(portal, "memberList"));
    }

    // ---- The four imperfections, pinned so that a comment cannot drift from them ----

    [Fact]
    public void Modified_is_the_creation_time_because_nothing_records_a_change()
    {
        // Imperfection 1. A portal group has both dates and this server keeps one,
        // so `modified` says *created* — which means a client sorting by it sorts by
        // age. Recorded in ADR-040 §4a rather than papered over with `now`, which
        // would make every group look freshly edited on every request.
        DateTimeOffset at = new(2026, 8, 18, 9, 30, 0, TimeSpan.Zero);
        object portal = Portal(Group(createdAt: at));

        Assert.Equal(at.ToUnixTimeMilliseconds(), Field(portal, "created"));
        Assert.Equal(Field(portal, "created"), Field(portal, "modified"));
    }

    [Fact]
    public void A_group_older_than_the_column_has_no_date_rather_than_a_wrong_one()
    {
        object portal = Portal(Group());

        Assert.Null(Field(portal, "created"));
        Assert.Null(Field(portal, "modified"));
    }

    [Theory]
    [InlineData(GroupItemUpdate.AllItems, 1)]
    [InlineData(GroupItemUpdate.OwnItems, 0)]
    [InlineData(GroupItemUpdate.None, 0)]
    public void Shared_update_is_all_or_nothing_there_and_is_not_here(
        GroupItemUpdate update, int capabilities)
    {
        // Imperfection 2, and the one with teeth. `updateitemcontrol` is the
        // reference's name for ADR-036 §4a-i's *allItems*; `ownItems` has no
        // equivalent and therefore publishes as no capability at all. That is an
        // understatement rather than a false claim — the server goes on honouring
        // it — and this pins which direction the loss runs in.
        string[] published = (string[])Field(Portal(Group(itemUpdate: update)), "capabilities")!;

        Assert.Equal(capabilities, published.Length);

        if (capabilities == 1)
        {
            Assert.Equal("updateitemcontrol", published[0]);
        }
    }

    [Fact]
    public void Tags_are_empty_because_our_groups_carry_none()
    {
        // Imperfection 3. Empty rather than absent: a portal group always has the
        // field, and a client that reads tags reads a list.
        Assert.Empty((string[])Field(Portal(Group()), "tags")!);
    }

    [Fact]
    public void A_field_this_server_does_not_hold_is_left_out_rather_than_defaulted()
    {
        // Imperfection 4, stated as the rule that produced the other three. A
        // defaulted field is a claim: `phone: ""` says the group has no phone, and
        // `sortField: "title"` says somebody chose one.
        object portal = Portal(Group());

        foreach (string absent in new[]
        {
            "phone", "sortField", "sortOrder", "provider", "providerGroupName",
            "isFav", "isReadOnly", "notificationsEnabled", "membershipAccess",
        })
        {
            Assert.Null(Field(portal, absent));
        }
    }

    // ---- The property that keeps a group from being mistaken for an item ----

    [Fact]
    public void A_group_is_filtered_by_the_same_search_that_filters_items()
    {
        object portal = Portal(Group());

        Assert.True(PortalQuery.Matches(portal, string.Empty));
        Assert.True(PortalQuery.Matches(portal, "*"));
        Assert.True(PortalQuery.Matches(portal, "owner:root"));
        Assert.True(PortalQuery.Matches(portal, "access:private"));
        Assert.True(PortalQuery.Matches(portal, "Planning"));
        Assert.False(PortalQuery.Matches(portal, "owner:someone-else"));
    }

    [Fact]
    public void A_search_for_a_service_does_not_come_back_holding_a_group()
    {
        // <b>The wrong answer this whole surface is arranged to prevent.</b> A group
        // has no `type` and no `url`, so the clauses Pro uses to find a geocoder or a
        // feature service must not match one — and they cannot, because a clause
        // PortalQuery cannot evaluate against the object returns nothing rather than
        // everything.
        object portal = Portal(Group());

        Assert.False(PortalQuery.Matches(portal, "type:\"Feature Service\""));
        Assert.False(PortalQuery.Matches(
            portal, "url:https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer"));
    }
}
