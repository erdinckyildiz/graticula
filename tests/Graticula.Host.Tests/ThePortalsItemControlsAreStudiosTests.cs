using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The four portal doors the ArcGIS veteran's review of 2026-11-02 asked for — <c>protect</c>, <c>reassign</c>,
/// <c>updateFolder</c> and an item's <c>groups</c> (ADR-214 §5a): the parts of them that decide something without a
/// server. The rest is Studio's own handlers, which the conformance suite drives through the doors.
/// </summary>
public sealed class ThePortalsItemControlsAreStudiosTests
{
    private static GroupSummary Group(string name, GroupStanding standing) =>
        new(Guid.NewGuid(), name, null, null, "owner", GroupItemUpdate.None, 1, 1, standing);

    [Fact]
    public void An_items_groups_are_sorted_into_the_three_lists_by_where_the_caller_stands()
    {
        GroupSummary owned = Group("owned", GroupStanding.Owner);
        GroupSummary managed = Group("managed", GroupStanding.Manager);
        GroupSummary joined = Group("joined", GroupStanding.Member);
        GroupSummary seen = Group("seen", GroupStanding.Outside);
        GroupSummary unshared = Group("unshared", GroupStanding.Owner);

        (List<GroupSummary> admin, List<GroupSummary> member, List<GroupSummary> other) = PortalEndpoints.SharedGroupsOf(
            [owned, managed, joined, seen, unshared], [owned.Id, managed.Id, joined.Id, seen.Id]);

        Assert.Equal(["owned", "managed"], admin.Select(g => g.Name));
        Assert.Equal(["joined"], member.Select(g => g.Name));
        Assert.Equal(["seen"], other.Select(g => g.Name));
    }

    [Fact]
    public void A_group_the_caller_cannot_see_is_not_named_although_the_item_is_shared_with_it()
    {
        GroupSummary seen = Group("seen", GroupStanding.Member);

        (List<GroupSummary> admin, List<GroupSummary> member, List<GroupSummary> other) = PortalEndpoints.SharedGroupsOf(
            [seen], [seen.Id, Guid.NewGuid()]);

        Assert.Empty(admin);
        Assert.Equal(["seen"], member.Select(g => g.Name));
        Assert.Empty(other);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("other\\ali")]
    public void A_reassignment_names_a_member_of_this_organisation_by_a_bare_name(string? target)
    {
        Assert.NotNull(PortalEndpoints.ReassignTargetRefusal(target));
    }

    [Fact]
    public void A_bare_name_may_be_asked_for()
    {
        Assert.Null(PortalEndpoints.ReassignTargetRefusal("ali@firma.com"));
    }
}
