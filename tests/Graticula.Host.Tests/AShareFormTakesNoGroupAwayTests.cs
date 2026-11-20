using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-214 condition 2, the group half (2026-11-20): a portal share form adds groups and takes none away, so a form that
/// names neither everyone, the organisation nor a group leaves an item that is in a group shared with it.
/// </summary>
public sealed class AShareFormTakesNoGroupAwayTests
{
    [Fact]
    public void Private_with_a_group_held_stays_group_shared()
    {
        Assert.Equal(SharingScope.Group, PortalEndpoints.KeptInItsGroups(SharingScope.Private, 1));
    }

    [Fact]
    public void Private_with_no_group_is_private()
    {
        Assert.Equal(SharingScope.Private, PortalEndpoints.KeptInItsGroups(SharingScope.Private, 0));
    }

    [Theory]
    [InlineData(SharingScope.Organization)]
    [InlineData(SharingScope.Public)]
    [InlineData(SharingScope.Group)]
    [InlineData(SharingScope.AllOrganizations)]
    public void A_wider_scope_is_left_as_asked(SharingScope asked)
    {
        Assert.Equal(asked, PortalEndpoints.KeptInItsGroups(asked, 2));
    }
}
