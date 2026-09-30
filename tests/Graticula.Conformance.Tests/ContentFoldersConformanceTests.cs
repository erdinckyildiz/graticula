using System;
using System.Collections.Generic;
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
/// A member's content folders and Move — ADR-114 conditions 1 and 2, through the native surface and the portal face.
/// </summary>
/// <remarks>
/// <b>It makes a folder and a layer of its own</b> and removes both. The layer's URL is read after every move, because
/// the one thing a content folder must never do is change it.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ContentFoldersConformanceTests : ArcGisClient
{
    private const string Points =
        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[32.85,39.93]},"properties":{"n":1}}]}""";

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    [Fact]
    public async Task Items_move_between_folders_without_their_address_changing_and_the_portal_says_so()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string stem = Guid.NewGuid().ToString("N")[..8];
        string service = $"zz_folders_{stem}";
        string title = $"zz Folder {stem}";
        string? folder = null;

        using MultipartFormDataContent upload = new();
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Points)), "file", "points.geojson");
        upload.Add(new StringContent(service), "name");
        (HttpStatusCode made, string madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/hosted/import", upload);
        Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"The import failed: {(int)made} {madeBody}");

        try
        {
            (HttpStatusCode created, string createdBody) = await SendAsync(root, token!, HttpMethod.Post, "/content/folders", Json(new { title }));
            Assert.True(created == HttpStatusCode.Created, $"Making a folder answered {(int)created}: {createdBody}");
            folder = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString();

            (HttpStatusCode again, _) = await SendAsync(root, token!, HttpMethod.Post, "/content/folders", Json(new { title = title.ToUpperInvariant() }));
            Assert.Equal(HttpStatusCode.Conflict, again);

            // ---------------------------------------------------------------- move in
            (_, string moved) = await SendAsync(root, token!, HttpMethod.Post, "/content/move",
                Json(new { items = new[] { new { service = $"hosted/{service}" } }, to = folder }));
            Assert.True(JsonDocument.Parse(moved).RootElement.GetProperty("results")[0].GetProperty("success").GetBoolean(), moved);

            (HttpStatusCode still, _) = await SendAsync(root, token!, HttpMethod.Get, $"/rest/services/hosted/{service}/FeatureServer?f=json");
            Assert.Equal(HttpStatusCode.OK, still);

            (_, string items) = await SendAsync(root, token!, HttpMethod.Get, "/content/items");
            JsonElement item = JsonDocument.Parse(items).RootElement.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("name").GetString() == $"hosted/{service}");
            Assert.Equal(folder, item.GetProperty("contentFolder").GetString());

            // ---------------------------------------------------------------- the portal face
            string user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "ci";
            string portalFolder = Guid.Parse(folder!).ToString("N");

            (_, string rootListing) = await SendAsync(root, token!, HttpMethod.Get, $"/sharing/rest/content/users/{user}?f=json");
            JsonElement listing = JsonDocument.Parse(rootListing).RootElement;
            Assert.Contains(listing.GetProperty("folders").EnumerateArray(), f => f.GetProperty("id").GetString() == portalFolder);
            Assert.DoesNotContain(listing.GetProperty("items").EnumerateArray(), i => i.GetProperty("title").GetString() == service);

            (_, string inFolder) = await SendAsync(root, token!, HttpMethod.Get, $"/sharing/rest/content/users/{user}/{portalFolder}?f=json");
            JsonElement[] there = [.. JsonDocument.Parse(inFolder).RootElement.GetProperty("items").EnumerateArray()];
            Assert.Contains(there, i => i.GetProperty("title").GetString() == service && i.GetProperty("ownerFolder").GetString() == portalFolder);

            (_, string searched) = await SendAsync(root, token!, HttpMethod.Get, $"/sharing/rest/search?q=ownerfolder:{portalFolder}&f=json");
            Assert.Contains(JsonDocument.Parse(searched).RootElement.GetProperty("results").EnumerateArray(),
                i => i.GetProperty("title").GetString() == service);

            // ---------------------------------------------------------------- a folder with items is not deleted
            (HttpStatusCode full, string fullBody) = await SendAsync(root, token!, HttpMethod.Delete, $"/content/folders/{folder}");
            Assert.True(full == HttpStatusCode.Conflict, $"Deleting a folder with an item answered {(int)full}: {fullBody}");

            // ---------------------------------------------------------------- moved out through the portal face
            string itemId = there.First(i => i.GetProperty("title").GetString() == service).GetProperty("id").GetString()!;
            (_, string portalMove) = await SendAsync(root, token!, HttpMethod.Post, $"/sharing/rest/content/users/{user}/moveItems",
                Form(("items", itemId), ("folder", "/"), ("f", "json")));
            Assert.True(JsonDocument.Parse(portalMove).RootElement.GetProperty("results")[0].GetProperty("success").GetBoolean(), portalMove);

            (HttpStatusCode emptied, string emptiedBody) = await SendAsync(root, token!, HttpMethod.Delete, $"/content/folders/{folder}");
            Assert.True(emptied == HttpStatusCode.OK, $"Deleting the emptied folder answered {(int)emptied}: {emptiedBody}");
            folder = null;

            (HttpStatusCode after, _) = await SendAsync(root, token!, HttpMethod.Get, $"/rest/services/hosted/{service}/FeatureServer?f=json");
            Assert.Equal(HttpStatusCode.OK, after);
        }
        finally
        {
            if (folder is not null)
            {
                await SendAsync(root, token!, HttpMethod.Post, "/content/move",
                    Json(new { items = new[] { new { service = $"hosted/{service}" } }, to = (string?)null }));
                await SendAsync(root, token!, HttpMethod.Delete, $"/content/folders/{folder}");
            }

            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/featureservices/{service}?folder=hosted&drop=true");
        }
    }
}
