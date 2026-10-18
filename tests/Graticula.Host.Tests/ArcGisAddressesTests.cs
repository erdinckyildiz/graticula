using Microsoft.AspNetCore.Http;
using Xunit;
using static Graticula.Host.ArcGisAddresses;

namespace Graticula.Host.Tests;

/// <summary>
/// The addresses an ArcGIS shop already has — <c>/arcgis</c> and <c>/server/rest</c> — answer whether or not Pro may
/// publish, and only Pro's view of a hosting server's admin API goes with publishing.
/// </summary>
public sealed class ArcGisAddressesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_names_reach_the_services_directory_with_publishing_on_or_off(bool publishing)
    {
        Assert.Equal(new Mapping(Outcome.Rewritten, "/rest/services"), Map("/arcgis/rest/services", publishing));
        Assert.Equal(new Mapping(Outcome.Rewritten, "/rest/services"), Map("/server/rest/services", publishing));
        Assert.Equal(
            new Mapping(Outcome.Rewritten, "/rest/services/turkiye/tr_ref/FeatureServer/0"),
            Map("/server/rest/services/turkiye/tr_ref/FeatureServer/0", publishing));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_arcgis_base_is_a_page_as_an_ArcGIS_Server_base_is(bool publishing)
    {
        Assert.Equal(Outcome.ServerPage, Map("/arcgis", publishing).Outcome);
        Assert.Equal(Outcome.ServerPage, Map("/arcgis/", publishing).Outcome);
    }

    [Fact]
    public void Pros_admin_address_exists_only_while_Pro_may_publish()
    {
        Assert.Equal(new Mapping(Outcome.Rewritten, "/admin/generateToken"), Map("/arcgis/admin/generateToken", true));
        Assert.Equal(new Mapping(Outcome.Rewritten, "/pro-admin/services"), Map("/arcgis/admin/services", true));

        // Not this server's own /admin under a name that promises another.
        Assert.Equal(Outcome.NotFound, Map("/arcgis/admin/services", false).Outcome);
        Assert.Equal(Outcome.NotFound, Map("/arcgis/admin/generateToken", false).Outcome);
    }

    [Theory]
    [InlineData("/server")]
    [InlineData("/server/")]
    [InlineData("/server/services")]
    [InlineData("/server/roles")]
    [InlineData("/server/restart")]
    [InlineData("/arcgisuris.xml")]
    [InlineData("/rest/services")]
    [InlineData("/serverless/rest/services")]
    public void The_Server_screens_and_everything_else_are_left_alone(string path)
    {
        Assert.Equal(new Mapping(Outcome.Unchanged, path), Map(path, publishing: true));
    }
}
