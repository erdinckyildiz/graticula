using Graticula.Platform.Identity;
using Xunit;

namespace Graticula.Platform.Tests.Identity;

/// <summary>
/// A service account never administers and never comes to own anything, whatever its roles say — ADR-015 §2, ADR-200.
/// </summary>
public sealed class AMachineNeverOwnsOrAdministersTests
{
    [Fact]
    public void A_publisher_machine_reads_and_edits_but_creates_nothing()
    {
        Authorization machine = Authorization.Resolve(UserTypes.Creator, [Roles.Publisher]).ForAMachine();

        Assert.True(machine.Allows(Privilege.FeaturesEdit));
        Assert.False(machine.Allows(Privilege.ContentCreate));
        Assert.False(machine.Allows(Privilege.ContentPublishFeatures));
        Assert.False(machine.Allows(Privilege.ContentPublishTiles));
        Assert.False(machine.Allows(Privilege.GroupsCreate));
    }

    [Fact]
    public void The_administrator_role_confers_nothing_on_a_machine()
    {
        Authorization machine = Authorization.Resolve(UserTypes.Creator, [Roles.Administrator]).ForAMachine();

        Assert.False(machine.IsAdministrator);
        Assert.False(machine.Allows(Privilege.AdminManageSecurity));
        Assert.DoesNotContain(Roles.Administrator, machine.Roles);
    }

    [Fact]
    public void A_key_is_recognisable_and_random()
    {
        string one = ApiKeyToken.Generate();
        string two = ApiKeyToken.Generate();

        Assert.True(ApiKeyToken.IsOne(one));
        Assert.NotEqual(one, two);
        Assert.False(ApiKeyToken.IsOne(SessionToken.Generate()));
    }
}
