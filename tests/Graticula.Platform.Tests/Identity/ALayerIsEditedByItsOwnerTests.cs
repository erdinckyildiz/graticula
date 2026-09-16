using System;
using System.Collections.Generic;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// Who may write to a layer, and who may change what it is — owner decision 2026-09-16, ADR-075.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's words: <i>"herkese açık bile olsa katman, bir sahibi olmalı. Düzenleme hakkı ona
/// ve admin olan kullanıcılara ait. Adminler her şeyi düzenleyebilir."</i></b> Asked whether that
/// also closes the owner's own delegation, the owner kept it: a shared-update group still edits.
/// Asked about layers from before ownership existed, the owner chose administrators alone.
/// </para>
/// <para>
/// <b>Half of these are about who is now refused</b>, because that is where the change is: until this
/// a member whose role held <c>features:edit</c> wrote to every layer they could read, public ones
/// included, and the person answerable for the layer had no say.
/// </para>
/// </remarks>
public sealed class ALayerIsEditedByItsOwnerTests
{
    private static readonly Guid OwnerId = new("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid Group = new("bbbbbbbb-0000-0000-0000-00000000000b");

    private static readonly Principal Owner =
        new(OwnerId, PrincipalKind.User, "owner", "Owner", isDisabled: false);

    private static readonly Principal Stranger =
        new(new Guid("cccccccc-0000-0000-0000-00000000000c"), PrincipalKind.User, "stranger", "Stranger", isDisabled: false);

    private static Authorization Role(string role, IReadOnlyList<Guid>? groups = null, IReadOnlyList<Guid>? editable = null) =>
        Authorization.Resolve(
            UserTypes.Unrestricted, [role], CompiledRoleGrants.Instance, groups ?? [], editable ?? []);

    [Theory]
    [InlineData(SharingScope.Private)]
    [InlineData(SharingScope.Organization)]
    [InlineData(SharingScope.Public)]
    public void The_owner_with_an_editor_s_role_writes_to_their_own_layer(SharingScope scope) =>
        Assert.Equal(
            LayerAccess.EditRight.Owner,
            LayerAccess.MayEdit(OwnerId, scope, [], Owner, Role(Roles.Publisher)));

    [Theory]
    [InlineData(Roles.DataEditor)]
    [InlineData(Roles.Publisher)]
    public void A_role_that_edits_does_not_reach_somebody_else_s_public_layer(string role)
    {
        // <b>The case that changed.</b> Until 2026-09-16 both of these wrote to it: `features:edit`
        // added features, and `features:fullEdit` changed anybody's.
        Assert.Equal(
            LayerAccess.EditRight.None,
            LayerAccess.MayEdit(OwnerId, SharingScope.Public, [], Stranger, Role(role)));
    }

    [Fact]
    public void An_administrator_writes_to_every_layer_including_one_with_no_owner()
    {
        Authorization administrator = Role(Roles.Administrator);

        Assert.Equal(
            LayerAccess.EditRight.Administrator,
            LayerAccess.MayEdit(OwnerId, SharingScope.Private, [], Stranger, administrator));

        Assert.Equal(
            LayerAccess.EditRight.Administrator,
            LayerAccess.MayEdit(null, SharingScope.Public, [], Stranger, administrator));
    }

    [Fact]
    public void A_layer_with_no_owner_is_nobody_else_s_to_edit()
    {
        // Owner decision the same day: until an administrator assigns one, administrators alone.
        Assert.Equal(
            LayerAccess.EditRight.None,
            LayerAccess.MayEdit(null, SharingScope.Public, [], Stranger, Role(Roles.Publisher)));
    }

    [Fact]
    public void The_owner_s_delegation_survives_through_a_shared_update_group()
    {
        // A viewer — no editing privilege at all — in a group the owner shared the layer with.
        Authorization member = Authorization.Resolve(
            UserTypes.Viewer, [], CompiledRoleGrants.Instance, [Group], [Group]);

        Assert.Equal(
            LayerAccess.EditRight.Group,
            LayerAccess.MayEdit(OwnerId, SharingScope.Group, [Group], Stranger, member));
    }

    [Fact]
    public void An_owner_whose_role_no_longer_edits_stops_writing()
    {
        // Ownership decides which layers; the role still decides whether this account edits at all.
        Assert.Equal(
            LayerAccess.EditRight.None,
            LayerAccess.MayEdit(OwnerId, SharingScope.Private, [], Owner, Role(Roles.Viewer)));
    }

    [Fact]
    public void Anonymous_never_writes_even_to_a_public_layer() =>
        Assert.Equal(
            LayerAccess.EditRight.None,
            LayerAccess.MayEdit(null, SharingScope.Public, [], Principal.Anonymous, Authorization.Nothing));

    [Fact]
    public void Changing_what_a_layer_is_belongs_to_its_owner_and_administrators_and_not_to_a_group()
    {
        Authorization groupMember = Authorization.Resolve(
            UserTypes.Viewer, [], CompiledRoleGrants.Instance, [Group], [Group]);

        Assert.True(LayerAccess.MayManage(OwnerId, Owner, Role(Roles.User)));
        Assert.True(LayerAccess.MayManage(OwnerId, Stranger, Role(Roles.Administrator)));
        Assert.True(LayerAccess.MayManage(null, Stranger, Role(Roles.Administrator)));

        // The ones that answered yes before this, through a privilege alone.
        Assert.False(LayerAccess.MayManage(OwnerId, Stranger, Role(Roles.Publisher)));
        Assert.False(LayerAccess.MayManage(null, Stranger, Role(Roles.Publisher)));

        // And the delegation that writes features does not reach the definition.
        Assert.False(LayerAccess.MayManage(OwnerId, Stranger, groupMember));

        Assert.False(LayerAccess.MayManage(null, Principal.Anonymous, Authorization.Nothing));
    }
}
