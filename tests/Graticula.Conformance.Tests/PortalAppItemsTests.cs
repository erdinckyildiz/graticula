using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-264, from the ArcGIS veteran's review of v1.0.499 (#1): an app or a link is an item — added with its text or its
/// address, read back, updated — and any item keeps resources, as Experience Builder, Dashboards and Survey123 store
/// theirs. Each was refused for want of a file, or a 404.
/// </summary>
public sealed class PortalAppItemsTests : ArcGisClient
{
    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<(JsonElement Json, string Text)> SendAsync(HttpMethod method, string path, IDictionary<string, string>? form = null)
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
        string text = await response.Content.ReadAsStringAsync();
        JsonElement json = text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (json, text);
    }

    private async Task<string> AddAsync(Dictionary<string, string> form)
    {
        form["f"] = "json";
        (JsonElement added, string raw) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", form);
        Assert.True(added.TryGetProperty("id", out JsonElement id), raw);
        return id.GetString()!;
    }

    private async Task DeleteAsync(string id) => await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/delete",
        new Dictionary<string, string> { ["permanentDelete"] = "true", ["f"] = "json" });

    [Fact]
    public async Task An_app_is_added_with_its_text_read_back_and_updated_and_a_link_with_its_address()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        const string Config = """{"version":"4.1","widgets":[{"type":"indicator"}]}""";
        string? app = null, link = null;

        try
        {
            app = await AddAsync(new()
            {
                ["type"] = "Dashboard", ["title"] = $"zz dashboard {tag}", ["text"] = Config,
                ["typeKeywords"] = "Dashboard,Operations Dashboard", ["tags"] = "zz,apps",
            });

            (JsonElement item, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}?f=json");
            Assert.Equal("Dashboard", item.GetProperty("type").GetString());
            Assert.Contains("Operations Dashboard", item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));

            (JsonElement data, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/data?f=json");
            Assert.Equal("4.1", data.GetProperty("version").GetString());

            (JsonElement updated, string updatedRaw) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{app}/update",
                new Dictionary<string, string> { ["text"] = """{"version":"4.2"}""", ["f"] = "json" });
            Assert.True(updated.GetProperty("success").GetBoolean(), updatedRaw);
            Assert.Equal("4.2", (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/data?f=json")).Json
                .GetProperty("version").GetString());

            (JsonElement notJson, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{app}/update",
                new Dictionary<string, string> { ["text"] = "{not json", ["f"] = "json" });
            Assert.True(notJson.TryGetProperty("error", out _));

            const string Address = "https://services.example.com/arcgis/rest/services/Roads/FeatureServer";
            link = await AddAsync(new() { ["type"] = "Feature Service", ["title"] = $"zz roads {tag}", ["url"] = Address });
            (JsonElement linked, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{link}?f=json");
            Assert.Equal("Feature Service", linked.GetProperty("type").GetString());
            Assert.Equal(Address, linked.GetProperty("url").GetString());

            (JsonElement badUrl, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem",
                new Dictionary<string, string> { ["type"] = "Feature Service", ["title"] = "zz bad", ["url"] = "ftp://x/y", ["f"] = "json" });
            Assert.True(badUrl.TryGetProperty("error", out _));
        }
        finally
        {
            foreach (string? id in new[] { app, link })
            {
                if (id is not null)
                {
                    await DeleteAsync(id);
                }
            }
        }
    }

    [Fact]
    public async Task An_item_keeps_resources_added_updated_listed_read_and_removed()
    {
        string tag = Guid.NewGuid().ToString("N")[..8];
        string app = await AddAsync(new() { ["type"] = "Web Experience", ["title"] = $"zz experience {tag}", ["text"] = "{}" });
        string owner = $"/sharing/rest/content/users/{User}/items/{app}";

        try
        {
            (JsonElement added, string addedRaw) = await SendAsync(HttpMethod.Post, $"{owner}/addResources", new Dictionary<string, string>
            {
                ["resourcesPrefix"] = "config", ["fileName"] = "config.json", ["text"] = """{"pages":1}""", ["f"] = "json",
            });
            Assert.True(added.GetProperty("success").GetBoolean(), addedRaw);

            // An add does not replace, and an update does not add.
            (JsonElement again, _) = await SendAsync(HttpMethod.Post, $"{owner}/addResources", new Dictionary<string, string>
            {
                ["resourcesPrefix"] = "config", ["fileName"] = "config.json", ["text"] = "{}", ["f"] = "json",
            });
            Assert.True(again.TryGetProperty("error", out _));
            (JsonElement missing, _) = await SendAsync(HttpMethod.Post, $"{owner}/updateResources", new Dictionary<string, string>
            {
                ["fileName"] = "nowhere.json", ["text"] = "{}", ["f"] = "json",
            });
            Assert.True(missing.TryGetProperty("error", out _));

            (JsonElement replaced, _) = await SendAsync(HttpMethod.Post, $"{owner}/updateResources", new Dictionary<string, string>
            {
                ["resourcesPrefix"] = "config", ["fileName"] = "config.json", ["text"] = """{"pages":2}""", ["f"] = "json",
            });
            Assert.True(replaced.GetProperty("success").GetBoolean());

            (JsonElement listed, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/resources?f=json");
            Assert.Equal(1, listed.GetProperty("total").GetInt32());
            Assert.Equal("config/config.json", listed.GetProperty("resources")[0].GetProperty("resource").GetString());

            (JsonElement read, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/resources/config/config.json");
            Assert.Equal(2, read.GetProperty("pages").GetInt32());

            (JsonElement climbing, _) = await SendAsync(HttpMethod.Post, $"{owner}/addResources", new Dictionary<string, string>
            {
                ["resourcesPrefix"] = "..", ["fileName"] = "x.json", ["text"] = "{}", ["f"] = "json",
            });
            Assert.True(climbing.TryGetProperty("error", out _));

            // The recycle bin keeps an item's resources with it, and a restore brings them back.
            (JsonElement binned, string binnedRaw) = await SendAsync(HttpMethod.Post, $"{owner}/delete",
                new Dictionary<string, string> { ["f"] = "json" });
            Assert.True(binned.GetProperty("success").GetBoolean(), binnedRaw);
            (JsonElement restored, string restoredRaw) = await SendAsync(HttpMethod.Post, $"{owner}/restore",
                new Dictionary<string, string> { ["f"] = "json" });
            Assert.True(restored.GetProperty("success").GetBoolean(), restoredRaw);
            Assert.Equal(1, (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/resources?f=json")).Json
                .GetProperty("total").GetInt32());

            (JsonElement removed, _) = await SendAsync(HttpMethod.Post, $"{owner}/removeResources",
                new Dictionary<string, string> { ["resource"] = "config/config.json", ["f"] = "json" });
            Assert.True(removed.GetProperty("success").GetBoolean());
            Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/resources?f=json")).Json
                .GetProperty("total").GetInt32());

            // A resource the item does not have is said as ArcGIS Enterprise says it — a 404 in the body of a 200 —
            // since 2026-11-08: Field Maps asks every map for two that most maps do not have, and refused every form
            // while the body said 400.
            (JsonElement gone, string goneRaw) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{app}/resources/config/config.json?f=json");
            Assert.True(gone.GetProperty("error").GetProperty("code").GetInt32() == 404, goneRaw);
        }
        finally
        {
            await DeleteAsync(app);
        }
    }
}
