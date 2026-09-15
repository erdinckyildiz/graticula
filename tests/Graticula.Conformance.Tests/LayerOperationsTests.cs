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
/// <c>calculate</c>, <c>validateSQL</c> and <c>queryDomains</c> answer, and <c>calculate</c> goes
/// through the edit path.
/// </summary>
/// <remarks>Written 2026-09-15: all three were 404.</remarks>
[Collection("catalogue walk")]
public sealed class LayerOperationsTests : ArcGisClient
{
    [Fact]
    public async Task Calculate_sets_the_selected_features_and_the_other_two_answer()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_ops_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new[]
                {
                    new { name = "label", type = "text", nullable = true },
                    new { name = "status", type = "text", nullable = true },
                },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string service = $"/rest/services/{parts[2]}/{parts[3]}/FeatureServer";

        try
        {
            string Point(string label) =>
                $"{{\"geometry\":{{\"x\":1000,\"y\":2000,\"spatialReference\":{{\"wkid\":3857}}}},\"attributes\":{{\"label\":\"{label}\"}}}}";

            await PostAsync(root, token!, $"{feature}/addFeatures", ("features", $"[{Point("a")},{Point("a")},{Point("b")}]"));

            JsonElement calculated = await PostAsync(root, token!, $"{feature}/calculate",
                ("where", "label = 'a'"),
                ("calcExpression", "[{\"field\":\"status\",\"value\":\"done\"}]"));

            Assert.True(calculated.GetProperty("success").GetBoolean(), calculated.ToString());
            Assert.Equal(2, calculated.GetProperty("updatedFeatureCount").GetInt32());

            long done = (await GetJsonAsync($"{feature}/query?where=status%3D%27done%27&returnCountOnly=true"))
                .GetProperty("count").GetInt64();
            Assert.Equal(2, done);

            JsonElement expression = await PostAsync(root, token!, $"{feature}/calculate",
                ("where", "1=1"),
                ("calcExpression", "[{\"field\":\"status\",\"sqlExpression\":\"upper(label)\"}]"),
                expectFailure: true);
            Assert.Contains("sqlExpression", expression.ToString(), StringComparison.Ordinal);

            JsonElement valid = await GetJsonAsync($"{feature}/validateSQL?sql=label%3D%27a%27&sqlType=where");
            Assert.True(valid.GetProperty("isValidSQL").GetBoolean());

            JsonElement invalid = await GetJsonAsync($"{feature}/validateSQL?sql=nosuch%3D1&sqlType=where");
            Assert.False(invalid.GetProperty("isValidSQL").GetBoolean());
            Assert.NotEmpty(invalid.GetProperty("validationErrors").EnumerateArray());

            JsonElement domains = await GetJsonAsync($"{service}/queryDomains?layers=[0]");
            Assert.Equal(JsonValueKind.Array, domains.GetProperty("domains").ValueKind);
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);
        }
    }

    private async Task<JsonElement> PostAsync(
        string root, string token, string path, (string Key, string Value) first, (string Key, string Value)? second = null, bool expectFailure = false)
    {
        List<KeyValuePair<string, string>> fields = [new(first.Key, first.Value), new("f", "json")];

        if (second is { } more)
        {
            fields.Add(new(more.Key, more.Value));
        }

        using FormUrlEncodedContent form = new(fields);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(expectFailure || response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
