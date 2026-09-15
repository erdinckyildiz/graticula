using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A hosted layer given GlobalIDs names its GlobalID field, returns one per feature and reports it
/// from every edit.
/// </summary>
/// <remarks>
/// Written 2026-09-15: every document said <c>globalIdField: ""</c> and every edit result
/// <c>globalId: null</c>, though ADR-013 §2 said hosted layers had one.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ALayerWithGlobalIdsTests : ArcGisClient
{
    private static readonly Regex Braced = new("^\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\}$");

    [Fact]
    public async Task GlobalIDs_are_named_returned_and_reported()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_globalids_" + Guid.NewGuid().ToString("N")[..8];

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

        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            (HttpStatusCode enabled, string said) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/hosted/{layer}/global-ids", token!, "{}");

            Assert.True(enabled == HttpStatusCode.OK, $"Adding GlobalIDs answered {(int)enabled}: {said}");

            JsonElement document = await GetJsonAsync(feature);

            Assert.Equal("globalid", document.GetProperty("globalIdField").GetString());
            JsonElement field = document.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "globalid");
            Assert.Equal("esriFieldTypeGlobalID", field.GetProperty("type").GetString());
            Assert.False(field.GetProperty("editable").GetBoolean());

            using FormUrlEncodedContent form = new(
            [
                new KeyValuePair<string, string>(
                    "adds",
                    "[{\"geometry\":{\"x\":1000,\"y\":2000,\"spatialReference\":{\"wkid\":3857}},\"attributes\":{\"label\":\"x\"}}]"),
                new KeyValuePair<string, string>("f", "json"),
            ]);

            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{feature}/applyEdits")) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync(request);
            JsonElement add = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
                .GetProperty("addResults")[0];

            string global = add.GetProperty("globalId").GetString()!;
            Assert.Matches(Braced, global);

            JsonElement read = await GetJsonAsync($"{feature}/query?where=1%3D1&outFields=*");

            Assert.Equal("globalid", read.GetProperty("globalIdFieldName").GetString());
            Assert.Equal(
                global,
                read.GetProperty("features")[0].GetProperty("attributes").GetProperty("globalid").GetString());
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true",
                token!,
                json: null);
        }
    }
}
