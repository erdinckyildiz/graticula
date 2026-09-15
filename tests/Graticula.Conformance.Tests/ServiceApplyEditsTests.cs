using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The service's own <c>applyEdits</c> writes several layers, and keeps all of them or none.
/// </summary>
/// <remarks>
/// Written 2026-09-15: <c>FeatureServer/applyEdits</c> answered 405. ArcGIS Runtime and Field Maps
/// write a feature and its related records there in one call.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ServiceApplyEditsTests : ArcGisClient
{
    private const string Point =
        "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"x\"}}]";

    [Fact]
    public async Task Two_layers_are_written_together_and_rolled_back_together()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string first = "zz_svc_edits_" + Guid.NewGuid().ToString("N")[..8];

        string feature0 = await DefineAsync(root, token!, first, serviceName: null);
        string[] parts = feature0.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string folder = parts[2];
        string service = parts[3];

        try
        {
            string feature1 = await DefineAsync(root, token!, first + "_b", serviceName: service);
            int layer1 = int.Parse(feature1.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
            string serviceUrl = $"/rest/services/{folder}/{service}/FeatureServer";

            // ---- one good add and one that names a column the layer does not have: nothing is kept ----
            JsonElement refused = await PostAsync(root, token!, $"{serviceUrl}/applyEdits",
                ("edits", $"[{{\"id\":0,\"adds\":{Point}}},{{\"id\":{layer1},\"adds\":[{{\"geometry\":{{\"x\":1,\"y\":2}},\"attributes\":{{\"nosuch\":1}}}}]}}]"),
                ("rollbackOnFailure", "true"));

            Assert.Equal(JsonValueKind.Array, refused.ValueKind);
            Assert.All(refused.EnumerateArray(), entry => Assert.True(entry.GetProperty("rolledBack").GetBoolean(), entry.ToString()));
            Assert.Equal(0, await CountAsync($"/rest/services/{folder}/{service}/FeatureServer/0"));

            // ---- both good: both kept, each answered under its own id ----
            JsonElement kept = await PostAsync(root, token!, $"{serviceUrl}/applyEdits",
                ("edits", $"[{{\"id\":0,\"adds\":{Point}}},{{\"id\":{layer1},\"adds\":{Point}}}]"));

            Dictionary<int, JsonElement> byId = kept.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetInt32());

            Assert.True(byId[0].GetProperty("addResults")[0].GetProperty("success").GetBoolean(), kept.ToString());
            Assert.True(byId[layer1].GetProperty("addResults")[0].GetProperty("success").GetBoolean(), kept.ToString());
            Assert.Equal(1, await CountAsync($"/rest/services/{folder}/{service}/FeatureServer/0"));
            Assert.Equal(1, await CountAsync($"/rest/services/{folder}/{service}/FeatureServer/{layer1}"));
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder={folder}&drop=true", token!, json: null);
        }
    }

    private async Task<long> CountAsync(string layer) =>
        (await GetJsonAsync($"{layer}/query?where=1%3D1&returnCountOnly=true")).GetProperty("count").GetInt64();

    private async Task<string> DefineAsync(string root, string token, string name, string? serviceName)
    {
        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token,
            JsonSerializer.Serialize(new
            {
                name,
                geometryType = "Point",
                serviceName,
                fields = new[] { new { name = "label", type = "text", nullable = true } },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining '{name}' answered {(int)defined}: {design}");

        return JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
    }

    private async Task<JsonElement> PostAsync(string root, string token, string path, params (string Key, string Value)[] fields)
    {
        using FormUrlEncodedContent form = new(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("f", "json")));
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
