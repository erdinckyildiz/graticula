using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An item's tags — ADR-111: set on a service and a web map, kept tidy, listed, and said by the portal face.
/// </summary>
/// <remarks>
/// <b>The fixture's EarlyAlert service is tagged and then untagged</b>, so the test leaves it as it found it; the map
/// is its own and deleted.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ItemTagsConformanceTests : ArcGisClient
{
    private static readonly string[] Messy = [" roads ", "Roads", "", "ankara"];
    private static readonly string[] Crews = ["field crews"];

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string root, string? token, HttpMethod method, string path, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}")
        {
            Content = json is null ? null : new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_service_and_a_map_carry_tags_that_are_listed_and_said_by_the_portal()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string named = Environment.GetEnvironmentVariable("GRATICULA_TEST_MULTILAYER") ?? "hosted/ci_EarlyAlert";
        string folder = named.Contains('/', StringComparison.Ordinal) ? named.Split('/')[0] : "";
        string service = named.Split('/')[^1];
        string? map = null;

        try
        {
            // Kept tidy: trimmed, the same word once whatever its case, empties dropped.
            (HttpStatusCode set, string setBody) = await SendAsync(root, token, HttpMethod.Put,
                $"/admin/services/{service}/tags?folder={folder}", new { tags = Messy });
            Assert.True(set == HttpStatusCode.OK, $"Tagging answered {(int)set}: {setBody}");
            Assert.Equal(["roads", "ankara"], JsonDocument.Parse(setBody).RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

            // Listed with the item.
            (_, string items) = await SendAsync(root, token, HttpMethod.Get, "/content/items");
            JsonElement item = JsonDocument.Parse(items).RootElement.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("name").GetString() == named);
            Assert.Contains(item.GetProperty("tags").EnumerateArray(), t => t.GetString() == "ankara");

            // Too many is refused, with nothing changed.
            (HttpStatusCode many, _) = await SendAsync(root, token, HttpMethod.Put,
                $"/admin/services/{service}/tags?folder={folder}", new { tags = Enumerable.Range(0, 40).Select(i => $"t{i}").ToArray() });
            Assert.Equal(HttpStatusCode.BadRequest, many);

            // A map, the same way, and the portal face says its tags.
            (HttpStatusCode created, string body) = await SendAsync(root, token, HttpMethod.Post, "/content/webmaps", new
            {
                title = "zz tagged map", sharing = "private",
                document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
            });
            Assert.Equal(HttpStatusCode.Created, created);
            map = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();

            (HttpStatusCode mapTagged, _) = await SendAsync(root, token, HttpMethod.Put, $"/content/webmaps/{map}/tags", new { tags = Crews });
            Assert.Equal(HttpStatusCode.OK, mapTagged);

            (_, string portal) = await SendAsync(root, token, HttpMethod.Get, $"/sharing/rest/content/items/{map}?f=json");
            Assert.Contains("field crews", portal, StringComparison.Ordinal);
        }
        finally
        {
            await SendAsync(root, token, HttpMethod.Put, $"/admin/services/{service}/tags?folder={folder}", new { tags = Array.Empty<string>() });
            if (map is not null) await SendAsync(root, token, HttpMethod.Delete, $"/content/webmaps/{map}");
        }
    }
}
