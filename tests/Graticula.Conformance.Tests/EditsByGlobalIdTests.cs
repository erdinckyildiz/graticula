using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A new hosted layer has GlobalIDs, and applyEdits with <c>useGlobalIds=true</c> adds, updates and deletes by them.
/// </summary>
/// <remarks>Written 2026-09-15: a layer had GlobalIDs only when an administrator added them, and
/// <c>useGlobalIds=true</c> was refused.</remarks>
[Collection("catalogue walk")]
public sealed class EditsByGlobalIdTests : ArcGisClient
{
    [Fact]
    public async Task A_defined_layer_is_edited_by_GlobalID()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_byglobal_" + Guid.NewGuid().ToString("N")[..8];

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

        try
        {
            Assert.Equal("globalid", (await GetJsonAsync(feature)).GetProperty("globalIdField").GetString());

            string minted = "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
            string point = "\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}}";

            (HttpStatusCode addStatus, JsonElement added) = await PostAsync(root, token!, $"{feature}/applyEdits",
                ("useGlobalIds", "true"),
                ("adds", $"[{{{point},\"attributes\":{{\"globalid\":\"{minted}\",\"label\":\"a\"}}}}]"));
            Assert.True(addStatus == HttpStatusCode.OK, added.ToString());
            Assert.Equal(minted, added.GetProperty("addResults")[0].GetProperty("globalId").GetString());

            (_, JsonElement updated) = await PostAsync(root, token!, $"{feature}/applyEdits",
                ("useGlobalIds", "true"),
                ("updates", $"[{{\"attributes\":{{\"globalid\":\"{minted}\",\"label\":\"b\"}}}}]"));
            Assert.True(updated.GetProperty("updateResults")[0].GetProperty("success").GetBoolean(), updated.ToString());

            JsonElement read = await GetJsonAsync($"{feature}/query?where=1%3D1&outFields=label,globalid");
            Assert.Equal("b", read.GetProperty("features")[0].GetProperty("attributes").GetProperty("label").GetString());
            Assert.Equal(minted, read.GetProperty("features")[0].GetProperty("attributes").GetProperty("globalid").GetString());

            string stranger = "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}";
            (HttpStatusCode unknown, JsonElement refused) = await PostAsync(root, token!, $"{feature}/applyEdits",
                ("useGlobalIds", "true"), ("deletes", $"[\"{stranger}\",\"{minted}\"]"));
            Assert.Equal(HttpStatusCode.BadRequest, unknown);
            Assert.Contains(stranger, refused.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, (await GetJsonAsync($"{feature}/query?where=1%3D1&returnCountOnly=true")).GetProperty("count").GetInt32());

            (HttpStatusCode single, _) = await PostAsync(root, token!, $"{feature}/deleteFeatures",
                ("useGlobalIds", "true"), ("objectIds", "1"));
            Assert.Equal(HttpStatusCode.BadRequest, single);

            (_, JsonElement deleted) = await PostAsync(root, token!, $"{feature}/applyEdits",
                ("useGlobalIds", "true"), ("deletes", $"[\"{minted}\"]"));
            Assert.True(deleted.GetProperty("deleteResults")[0].GetProperty("success").GetBoolean(), deleted.ToString());
            Assert.Equal(0, (await GetJsonAsync($"{feature}/query?where=1%3D1&returnCountOnly=true")).GetProperty("count").GetInt32());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string root, string token, string path, params (string Key, string Value)[] fields)
    {
        List<KeyValuePair<string, string>> pairs = [new("f", "json")];

        foreach ((string key, string value) in fields)
        {
            pairs.Add(new(key, value));
        }

        using FormUrlEncodedContent form = new(pairs);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{path}")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }
}
