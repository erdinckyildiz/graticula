using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The portal and its hosting server name one ArcGIS Enterprise release, 12.1, with the portal API level a real 12.1
/// portal names beside it — owner decision 2026-10-19 (V-58). Measured that day on two public Enterprise 12.1 portals:
/// <c>currentVersion "2026.1"</c>, <c>enterpriseVersion "12.1.0"</c>, <c>enterpriseBuild "786"</c>.
/// </summary>
public sealed class OneEnterpriseReleaseTests : ArcGisClient
{
    [Theory]
    [InlineData("/sharing/rest")]
    [InlineData("/sharing/rest/portals/self")]
    public async Task The_portal_names_Enterprise_12_1_as_a_real_12_1_portal_does(string path)
    {
        JsonElement portal = await GetJsonAsync(path);

        Assert.Equal("2026.1", portal.GetProperty("currentVersion").GetString());
        Assert.Equal("12.1.0", portal.GetProperty("enterpriseVersion").GetString());
        Assert.Equal("786", portal.GetProperty("enterpriseBuild").GetString());
    }

    [Fact]
    public async Task The_portal_and_its_hosting_server_name_the_same_release()
    {
        JsonElement portal = await GetJsonAsync("/sharing/rest");
        JsonElement server = await GetJsonAsync("/rest/info");

        Assert.Equal(server.GetProperty("fullVersion").GetString(), portal.GetProperty("enterpriseVersion").GetString());
        Assert.StartsWith(
            server.GetProperty("currentVersion").GetRawText(),
            portal.GetProperty("enterpriseVersion").GetString(),
            StringComparison.Ordinal);
    }
}
