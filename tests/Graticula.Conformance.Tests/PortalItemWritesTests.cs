using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The portal's item writes do what they answer — the ArcGIS veteran's second review, N2: <c>update</c> answered
/// success for anything that was not a web map and dropped a web map's <c>text</c>, so <c>WebMap.save()</c> and the
/// Python API's <c>item.update(data=…)</c> reported success and lost the change; delete and unshare were absent.
/// </summary>
public sealed class PortalItemWritesTests : ArcGisClient
{
    private const string Empty =
        "{\"operationalLayers\":[],\"baseMap\":{\"baseMapLayers\":[],\"title\":\"None\"},\"version\":\"2.31\"}";

    private const string Saved =
        "{\"operationalLayers\":[{\"id\":\"saved\",\"title\":\"Saved by WebMap.save\",\"url\":\"https://example.com/x/FeatureServer/0\"}],"
        + "\"baseMap\":{\"baseMapLayers\":[],\"title\":\"None\"},\"version\":\"2.31\"}";

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

    private async Task<string> AddMapAsync(string title)
    {
        JsonElement added = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem?f=json",
            new Dictionary<string, string> { ["type"] = "Web Map", ["title"] = title, ["text"] = Empty });

        return added.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_web_maps_document_is_saved_by_update_with_the_fields_a_client_echoes()
    {
        string title = $"zz_save_{Guid.NewGuid():N}"[..24];
        string id = await AddMapAsync(title);

        try
        {
            // What PortalItem.update({data}) sends: the document as `text`, beside every property it read.
            JsonElement updated = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string>
                {
                    ["title"] = title + " saved", ["text"] = Saved, ["type"] = "Web Map",
                    ["typeKeywords"] = "ArcGIS API for JavaScript,Web Map", ["extent"] = "-180,-90,180,90",
                });

            Assert.True(updated.TryGetProperty("success", out JsonElement success) && success.GetBoolean(), updated.GetRawText());

            JsonElement data = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}/data?f=json");
            Assert.Equal("saved", data.GetProperty("operationalLayers")[0].GetProperty("id").GetString());

            JsonElement item = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(title + " saved", item.GetProperty("title").GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    [Fact]
    public async Task A_field_this_portal_does_not_keep_is_refused_by_name_and_nothing_is_written()
    {
        string title = $"zz_whole_{Guid.NewGuid():N}"[..24];
        string id = await AddMapAsync(title);

        try
        {
            JsonElement refused = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["title"] = "never", ["notAField"] = "x" });

            Assert.Contains("'notAField'", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

            JsonElement item = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(title, item.GetProperty("title").GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    [Fact]
    public async Task An_id_that_names_nothing_is_not_answered_with_success()
    {
        JsonElement answer = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{new string('0', 32)}/update?f=json",
            new Dictionary<string, string> { ["title"] = "x" });

        Assert.True(answer.TryGetProperty("error", out _), answer.GetRawText());
        Assert.False(answer.TryGetProperty("success", out _), answer.GetRawText());
    }

    [Fact]
    public async Task A_map_deleted_through_the_portal_goes_to_the_recycle_bin_and_a_missing_one_says_so()
    {
        string id = await AddMapAsync($"zz_gone_{Guid.NewGuid():N}"[..24]);

        JsonElement results = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/deleteItems?f=json",
            new Dictionary<string, string> { ["items"] = $"{id},{new string('1', 32)}" });

        Assert.True(results.GetProperty("results")[0].GetProperty("success").GetBoolean(), results.GetRawText());
        Assert.False(results.GetProperty("results")[1].GetProperty("success").GetBoolean(), results.GetRawText());

        JsonElement read = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
        Assert.True(read.TryGetProperty("error", out _), read.GetRawText());

        JsonElement bin = await SendAsync(HttpMethod.Get, "/admin/recycle-bin");
        Assert.Contains(id, bin.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsharing_from_a_group_it_is_not_in_names_that_group()
    {
        string id = await AddMapAsync($"zz_unshare_{Guid.NewGuid():N}"[..24]);
        string group = Guid.NewGuid().ToString("N");

        try
        {
            JsonElement answer = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/unshare?f=json",
                new Dictionary<string, string> { ["groups"] = group });

            Assert.Equal(group, answer.GetProperty("notUnsharedFrom")[0].GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }
}
