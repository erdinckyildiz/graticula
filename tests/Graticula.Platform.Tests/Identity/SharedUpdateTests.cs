using System;
using System.Collections.Generic;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// Shared update: when a group confers editing, and — mostly — when it does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-036](../../../docs/adr/ADR-036-groups.md) §4a as amended 2026-08-25 by owner
/// decision.</b> §4a shipped groups as *reading only*, deliberately, because the owner's
/// requirement had not asked for editing. It asks now, and §4b had already decided that the
/// capability lives on the group rather than on each share — so this is the addition that
/// decision was written to allow.
/// </para>
/// <para>
/// <b>What was actually wrong before.</b> `item_update` was stored, was editable through the
/// admin API and was shown in the group listing, and no code path read it. A setting the
/// server keeps and does not honour is [D-67](../../../docs/architecture-debt.md), and it is
/// the same shape the removed `public` visibility had.
/// </para>
/// <para>
/// <b>The invariant this must not break, and the reason half these tests are about
/// silence.</b> ADR-018 §3b says *sharing governs reading*. That is unchanged:
/// <see cref="LayerAccess.Evaluate"/> is untouched, and nothing here can make an item
/// readable that was not. What moved is the other half — editing was only a privilege, and
/// now it is a privilege *or* membership of a group that carries the setting.
/// </para>
/// </remarks>
public sealed class SharedUpdateTests
{
    private static readonly Guid Sharing = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Other = new("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void A_group_that_carries_the_setting_confers_editing()
    {
        Assert.True(LayerAccess.GroupConfersEditing(
            SharingScope.Group, In([Sharing], editable: [Sharing]), [Sharing]));
    }

    [Fact]
    public void A_group_without_the_setting_confers_nothing()
    {
        // <b>The ordinary group, and it is the common case.</b> `none` and `ownItems` both land
        // here: the first grants nothing by definition, and the second means *the items you
        // shared*, whose owner may already edit them by owning them.
        Assert.False(LayerAccess.GroupConfersEditing(
            SharingScope.Group, In([Sharing], editable: []), [Sharing]));
    }

    [Fact]
    public void A_group_the_caller_does_not_belong_to_confers_nothing()
    {
        // Belonging to *a* group with shared update does not open *another* group's items.
        Assert.False(LayerAccess.GroupConfersEditing(
            SharingScope.Group, In([Other], editable: [Other]), [Sharing]));
    }

    [Theory]
    [InlineData(SharingScope.Private)]
    [InlineData(SharingScope.Organization)]
    [InlineData(SharingScope.Public)]
    public void Only_a_group_share_can_be_widened_by_a_group(SharingScope scope)
    {
        // <b>The dangerous one, so it is stated for each scope rather than argued once.</b> A
        // caller who belongs to a sharing-update group must not thereby edit an item that is
        // not shared with it — including a *public* item, where the temptation to treat the
        // wide scope as the permissive one is greatest.
        Assert.False(LayerAccess.GroupConfersEditing(
            scope, In([Sharing], editable: [Sharing]), [Sharing]));
    }

    [Fact]
    public void An_item_shared_with_no_group_confers_nothing()
    {
        Assert.False(LayerAccess.GroupConfersEditing(
            SharingScope.Group, In([Sharing], editable: [Sharing]), []));

        Assert.False(LayerAccess.GroupConfersEditing(
            SharingScope.Group, In([Sharing], editable: [Sharing]), null));
    }

    [Fact]
    public void An_anonymous_caller_is_in_no_group_and_the_sets_are_empty()
    {
        // <b>Stated rather than assumed, as `Evaluate` states it.</b> *The empty set intersects
        // nothing* stops being true the day somebody adds a default group.
        Assert.False(LayerAccess.GroupConfersEditing(
            SharingScope.Group, Authorization.Nothing, [Sharing]));
    }

    [Fact]
    public void Editing_through_a_group_requires_being_able_to_read_through_it()
    {
        // <b>The subset property, asserted rather than trusted.</b> Every group that confers
        // editing must also pass the read check for the same item — otherwise somebody could
        // edit a layer they cannot see, and the 404 that hides unreadable layers would be
        // guarding a door with a hole beside it.
        Authorization caller = In([Sharing], editable: [Sharing]);

        Assert.True(LayerAccess.GroupConfersEditing(SharingScope.Group, caller, [Sharing]));

        Assert.Equal(
            LayerAccess.Reason.Group,
            LayerAccess.Evaluate(
                SharingScope.Group, owner: null, Member(), caller, [Sharing]));
    }

    [Fact]
    public void Reading_answers_exactly_what_it_did_before()
    {
        // <b>ADR-018 §3b's invariant, held still.</b> The same three cases with and without the
        // editing set: a reader who remembers *sharing governs reading* should find that their
        // memory is still correct.
        foreach (IReadOnlyList<Guid> editable in new[] { (IReadOnlyList<Guid>)[], [Sharing] })
        {
            Authorization caller = In([Sharing], editable);

            Assert.Equal(
                LayerAccess.Reason.Group,
                LayerAccess.Evaluate(
                    SharingScope.Group, owner: null, Member(), caller, [Sharing]));

            Assert.Equal(
                LayerAccess.Reason.Denied,
                LayerAccess.Evaluate(
                    SharingScope.Group, owner: null, Member(), caller, [Other]));

            Assert.Equal(
                LayerAccess.Reason.Denied,
                LayerAccess.Evaluate(
                    SharingScope.Private, owner: null, Member(), caller, [Sharing]));
        }
    }

    [Fact]
    public void The_editing_set_is_carried_through_resolve()
    {
        // The wiring, because a set that is computed and dropped on the floor is exactly the
        // failure this whole change is repairing.
        Authorization resolved = Authorization.Resolve(
            UserTypes.Viewer, [], CompiledRoleGrants.Instance, [Sharing, Other], [Sharing]);

        Assert.Equal([Name(Sharing), Name(Other)], Order(resolved.Groups));
        Assert.Equal([Name(Sharing)], Order(resolved.EditableGroups));
    }

    [Fact]
    public void A_caller_with_no_privileges_at_all_still_edits_through_the_group()
    {
        // <b>The point of the feature, and the reason it is a security decision.</b> Shared
        // update is not a shortcut for somebody who already holds `features:fullEdit`; it is
        // for a member who holds nothing and whose group says they may edit its items.
        Authorization viewer = Authorization.Resolve(
            UserTypes.Viewer, [], CompiledRoleGrants.Instance, [Sharing], [Sharing]);

        Assert.False(viewer.Allows(Privilege.FeaturesEdit));
        Assert.False(viewer.Allows(Privilege.FeaturesFullEdit));

        Assert.True(LayerAccess.GroupConfersEditing(SharingScope.Group, viewer, [Sharing]));
    }

    private static Authorization In(IReadOnlyList<Guid> groups, IReadOnlyList<Guid> editable) =>
        Authorization.Resolve(
            UserTypes.Unrestricted, [], CompiledRoleGrants.Instance, groups, editable);

    private static Principal Member() =>
        new(new Guid("cccccccc-0000-0000-0000-000000000003"), PrincipalKind.User, "member",
            "Member", isDisabled: false);

    // <b>Sorted as text, not as `Guid`.</b> `Guid.CompareTo` orders by the last fields before
    // the first, so a literal written to look ordered is not — which is how the first draft of
    // `The_editing_set_is_carried_through_resolve` failed on its own expectation rather than on
    // the code.
    private static List<string> Order(IEnumerable<Guid> ids)
    {
        List<string> sorted = [.. System.Linq.Enumerable.Select(ids, Name)];
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static string Name(Guid id) => id.ToString();
}
