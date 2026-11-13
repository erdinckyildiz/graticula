using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>admin/services/canCreateService</c> and <c>admin/services/properties</c> are the server's resources, not folders —
/// ADR-299. Read as folders, they answered "Folder '…' does not exist.", and Pro turned that into analyzer 00147.
/// </summary>
public sealed class AdminServicesResourceTests
{
    [Theory]
    [InlineData("services/canCreateService")]
    [InlineData("services/properties")]
    [InlineData("services/types")]
    [InlineData("services/CanCreateService")]
    public void TheServersOwnResourcesAreNotFolders(string what) =>
        Assert.False(PortalEndpoints.IsAdminServicesFolderPath(what));

    [Theory]
    [InlineData("services/hosted")]
    [InlineData("services/Utilities")]
    public void AFolderNameIsAFolder(string what) =>
        Assert.True(PortalEndpoints.IsAdminServicesFolderPath(what));

    [Theory]
    [InlineData("services/hosted/roads.MapServer")]
    [InlineData("services/roads.MapServer")]
    [InlineData("services")]
    public void AServiceOrTheRootIsNotAFolderPath(string what) =>
        Assert.False(PortalEndpoints.IsAdminServicesFolderPath(what));
}
