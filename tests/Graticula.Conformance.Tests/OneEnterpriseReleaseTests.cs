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

    /// <summary>
    /// The hosting server's admin root and its <c>info</c> said 11.2 while every other document said 12.1 — the ArcGIS
    /// veteran's review of 2026-11-03. Written on 2026-10-17, two days before the owner made the pair one release.
    /// </summary>
    [Theory]
    [InlineData("/arcgis/admin", "currentVersion")]
    [InlineData("/arcgis/admin/info", "currentversion")]
    public async Task The_hosting_servers_admin_names_the_release_its_REST_documents_name(string path, string key)
    {
        JsonElement server = await GetJsonAsync("/rest/info");
        JsonElement admin = await GetJsonAsync(path);

        Assert.Equal(server.GetProperty("currentVersion").GetRawText(), admin.GetProperty(key).GetRawText());
        Assert.Equal(server.GetProperty("fullVersion").GetString(), admin.GetProperty("fullVersion").GetString());
    }
}
