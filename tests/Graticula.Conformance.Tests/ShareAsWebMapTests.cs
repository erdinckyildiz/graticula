using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Pro's <i>Share As Web Map</i> after its layers: <c>addItem</c> with <c>type=Web Map</c> keeps the map, Pro reads it
/// back as the user's item and shares it with <c>shareItems</c> — and one group answers by its id. 2026-10-17, from a
/// trace of Pro failing at <i>"Failed to add web item"</i>.
/// </summary>
public sealed class ShareAsWebMapTests : ArcGisClient
{
    private const string Map =
        "{\"operationalLayers\":[],\"baseMap\":{\"baseMapLayers\":[],\"title\":\"None\"},\"spatialReference\":{\"wkid\":102100},\"version\":\"2.31\"}";

    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IDictionary<string, string>? form = null)
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");

        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task A_web_map_Pro_adds_is_kept_read_back_as_the_users_item_and_shared_by_Pros_form()
    {
        string title = $"zz_pro_webmap_{Guid.NewGuid():N}"[..28];

        JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem?f=json",
            new Dictionary<string, string>
            {
                ["type"] = "Web Map", ["title"] = title, ["tags"] = "pro,share", ["snippet"] = "From Pro", ["text"] = Map,
            });

        Assert.True(added.TryGetProperty("success", out JsonElement success) && success.GetBoolean(), added.GetRawText());
        string id = added.GetProperty("id").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", id);

        try
        {
            JsonElement read = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/users/{User}/items/{id}?f=json");
            Assert.Equal("Web Map", read.GetProperty("item").GetProperty("type").GetString());
            Assert.Equal(title, read.GetProperty("item").GetProperty("title").GetString());
            Assert.Equal("private", read.GetProperty("sharing").GetProperty("access").GetString());

            JsonElement shared = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/shareItems?f=json",
                new Dictionary<string, string> { ["items"] = id, ["everyone"] = "false", ["account"] = "true", ["groups"] = "false" });
            Assert.True(shared.GetProperty("results")[0].GetProperty("success").GetBoolean(), shared.GetRawText());

            JsonElement after = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/users/{User}/items/{id}?f=json");
            Assert.Equal("org", after.GetProperty("sharing").GetProperty("access").GetString());

            JsonElement data = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}/data?f=json");
            Assert.Equal(JsonValueKind.Array, data.GetProperty("operationalLayers").ValueKind);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>A 1 × 1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private async Task<(int Status, byte[] Body)> MultipartAsync(string path, IDictionary<string, string> fields)
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");

        using MultipartFormDataContent form = [];
        foreach ((string key, string value) in fields)
        {
            form.Add(new StringContent(value), key);
        }

        ByteArrayContent picture = new(Png);
        picture.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(picture, "thumbnail", "Thumbnail.png");

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private async Task<byte[]> ThumbnailAsync(string id)
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        using HttpRequestMessage request = new(HttpMethod.Get, $"{root}/content/webmaps/{id}/thumbnail");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_picture_Pro_sends_is_the_map_s_from_the_first_moment(bool withAddItem)
    {
        Dictionary<string, string> fields = new()
        {
            ["type"] = "Web Map", ["title"] = $"zz_pro_picture_{Guid.NewGuid():N}"[..28], ["text"] = Map, ["f"] = "json",
        };

        string id;
        if (withAddItem)
        {
            (int status, byte[] body) = await MultipartAsync($"/sharing/rest/content/users/{User}/addItem", fields);
            Assert.True(status == 200, System.Text.Encoding.UTF8.GetString(body));
            id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        }
        else
        {
            JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem?f=json", fields);
            id = added.GetProperty("id").GetString()!;
            Assert.Empty(await ThumbnailAsync(id));

            (int status, byte[] body) = await MultipartAsync(
                $"/sharing/rest/content/users/{User}/items/{id}/update", new Dictionary<string, string> { ["f"] = "json" });
            Assert.True(status == 200, System.Text.Encoding.UTF8.GetString(body));
        }

        try
        {
            Assert.Equal(Png, await ThumbnailAsync(id));
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    [Fact]
    public async Task Another_type_is_still_refused_and_says_what_is_added()
    {
        JsonElement refused = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem?f=json",
            new Dictionary<string, string> { ["type"] = "Code Sample", ["title"] = "zz_refused" });

        Assert.Contains("web map", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_web_map_that_is_not_JSON_is_refused_and_nothing_is_kept()
    {
        JsonElement refused = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem?f=json",
            new Dictionary<string, string> { ["type"] = "Web Map", ["title"] = "zz_broken", ["text"] = "{not json" });

        Assert.True(refused.TryGetProperty("error", out _), refused.GetRawText());
    }

    [Fact]
    public async Task One_group_answers_by_its_id_as_the_listing_has_it()
    {
        string name = $"zz_group_by_id_{Guid.NewGuid():N}"[..24];
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");

        using (HttpRequestMessage make = new(HttpMethod.Post, $"{root}/admin/groups"))
        {
            make.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            make.Content = new StringContent(JsonSerializer.Serialize(new { name, title = name }), System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage made = await Http.SendAsync(make);
            Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        }

        try
        {
            JsonElement listing = await SendAsync(HttpMethod.Get, $"/sharing/rest/community/groups?q=title:{name}&f=json");
            JsonElement results = listing.GetProperty("results");
            Assert.True(results.GetArrayLength() == 1, listing.GetRawText());

            string id = results[0].GetProperty("id").GetString()!;
            JsonElement one = await SendAsync(HttpMethod.Get, $"/sharing/rest/community/groups/{id}?f=json");

            Assert.Equal(results[0].GetRawText(), one.GetRawText());

            JsonElement none = await SendAsync(HttpMethod.Get, $"/sharing/rest/community/groups/{new string('0', 32)}?f=json");
            Assert.True(none.TryGetProperty("error", out _), none.GetRawText());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/groups/{name}");
        }
    }
}
