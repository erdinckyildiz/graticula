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
/// ArcGIS's <c>addToDefinition</c>, <c>deleteFromDefinition</c> and <c>truncate</c> answer at both
/// addresses ArcGIS clients compute, and change the layer.
/// </summary>
/// <remarks>Written 2026-09-15: all three were 404, so the ArcGIS API for Python's
/// <c>manager.add_to_definition</c> and <c>manager.truncate</c> could not be used against this server.</remarks>
[Collection("catalogue walk")]
public sealed class ArcGisLayerAdministrationTests : ArcGisClient
{
    [Fact]
    public async Task Fields_are_added_and_dropped_and_the_layer_is_emptied()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_admin_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new[] { new { name = "label", type = "text", nullable = true } },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string folder = parts[2];
        string service = parts[3];

        // ArcGIS Online's spelling and ArcGIS Enterprise's.
        string online = $"/rest/admin/services/{folder}/{service}/FeatureServer/0";
        string enterprise = $"/rest/admin/services/{folder}/{service}.FeatureServer/0";

        try
        {
            await PostAsync(root, token!, $"{feature}/addFeatures",
                ("features", "[{\"geometry\":{\"x\":1,\"y\":2,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"a\"}},{\"geometry\":{\"x\":3,\"y\":4,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"b\"}}]"));

            JsonElement added = await PostAsync(root, token!, $"{online}/addToDefinition",
                ("addToDefinition", "{\"fields\":[{\"name\":\"status\",\"type\":\"esriFieldTypeString\",\"alias\":\"Status\",\"length\":20,\"nullable\":true}]}"));
            Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());

            JsonElement[] fields = [.. (await GetJsonAsync(feature)).GetProperty("fields").EnumerateArray()];
            Assert.Contains(fields, f => f.GetProperty("name").GetString() == "status" && f.GetProperty("type").GetString() == "esriFieldTypeString");

            JsonElement refused = await PostAsync(root, token!, $"{enterprise}/addToDefinition",
                ("addToDefinition", "{\"fields\":[{\"name\":\"x\",\"type\":\"esriFieldTypeString\"}],\"indexes\":[{\"name\":\"i\",\"fields\":\"x\"}]}"),
                expectFailure: true);
            Assert.Contains("indexes", refused.ToString(), StringComparison.Ordinal);

            JsonElement dropped = await PostAsync(root, token!, $"{enterprise}/deleteFromDefinition",
                ("deleteFromDefinition", "{\"fields\":[{\"name\":\"status\"}]}"));
            Assert.True(dropped.GetProperty("success").GetBoolean(), dropped.ToString());
            Assert.DoesNotContain(
                (await GetJsonAsync(feature)).GetProperty("fields").EnumerateArray(),
                f => f.GetProperty("name").GetString() == "status");

            JsonElement notAsync = await PostAsync(root, token!, $"{online}/truncate", ("async", "true"), expectFailure: true);
            Assert.Contains("async", notAsync.ToString(), StringComparison.Ordinal);

            Assert.Equal(2, await CountAsync(feature));

            JsonElement attachments = await PostAsync(root, token!, $"{online}/truncate", ("attachmentOnly", "true"));
            Assert.True(attachments.GetProperty("success").GetBoolean(), attachments.ToString());
            Assert.Equal(2, await CountAsync(feature));

            JsonElement emptied = await PostAsync(root, token!, $"{enterprise}/truncate", ("async", "false"));
            Assert.True(emptied.GetProperty("success").GetBoolean(), emptied.ToString());
            Assert.Equal(0, await CountAsync(feature));

            // Object ids keep counting: the next feature is not given an id somebody has already seen.
            JsonElement next = await PostAsync(root, token!, $"{feature}/addFeatures",
                ("features", "[{\"geometry\":{\"x\":5,\"y\":6,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"c\"}}]"));
            Assert.True(next.GetProperty("addResults")[0].GetProperty("objectId").GetInt64() > 2, next.ToString());

            // Posted with no credential, so the refusal is the privilege and not a method mismatch.
            using FormUrlEncodedContent nobody = new([new("f", "json")]);
            using HttpResponseMessage anonymous = await Http.PostAsync(new Uri($"{root}{online}/truncate"), nobody);
            Assert.True(
                anonymous.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"An anonymous truncate answered {(int)anonymous.StatusCode}.");
            Assert.Equal(1, await CountAsync(feature));
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{service}?folder={folder}&drop=true", token!, json: null);
        }
    }

    private async Task<long> CountAsync(string feature) =>
        (await GetJsonAsync($"{feature}/query?where=1%3D1&returnCountOnly=true")).GetProperty("count").GetInt64();

    private async Task<JsonElement> PostAsync(
        string root, string token, string path, (string Key, string Value) field, bool expectFailure = false)
    {
        using FormUrlEncodedContent form = new([new(field.Key, field.Value), new("f", "json")]);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(
            expectFailure ? !response.IsSuccessStatusCode : response.IsSuccessStatusCode,
            $"{path} answered {(int)response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
