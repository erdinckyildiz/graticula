using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// D-266: every face a map reads a layer's data through is sent with the lifetime FeatureServer's <c>query</c> is — a
/// count, the ids, OGC API Features <c>items</c> and WFS <c>GetFeature</c> as well as the features themselves.
/// </summary>
/// <remarks>
/// <b>Compared with the features query rather than with a number</b>, so the test holds whatever the layer's lifetime
/// is: <c>no-store</c> for a layer somebody can edit, <c>max-age</c> for one nobody can. Before 2026-10-29 the four
/// other faces carried no <c>Cache-Control</c> at all.
/// </remarks>
[Collection("catalogue walk")]
public sealed class EveryReadOfALayerKeepsAsLongTests : ArcGisClient
{
    [Fact]
    public async Task A_count_ids_OGC_items_and_WFS_GetFeature_are_kept_as_long_as_the_features_query()
    {
        string root = await RequireServerAsync();
        string service = (Environment.GetEnvironmentVariable(QueryCapabilityConformanceTests.LayerVariable)
            ?? "hosted/ci_buildings").Trim('/');
        string layer = service[(service.LastIndexOf('/') + 1)..];

        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        async Task<string> ControlAsync(string path)
        {
            using HttpResponseMessage answer = await http.GetAsync(new Uri(root + path));
            Assert.True(answer.IsSuccessStatusCode, $"{path} answered {(int)answer.StatusCode}.");
            return answer.Headers.CacheControl?.ToString() ?? "(none)";
        }

        string features = await ControlAsync($"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&f=json");
        Assert.NotEqual("(none)", features);

        Assert.Equal(features, await ControlAsync($"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json"));
        Assert.Equal(features, await ControlAsync($"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnIdsOnly=true&f=json"));
        Assert.Equal(features, await ControlAsync($"/ogc/features/v1/collections/{Uri.EscapeDataString(layer)}/items?limit=1&f=json"));
        Assert.Equal(features, await ControlAsync($"/wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames=graticula:{layer}&count=1"));
    }
}
