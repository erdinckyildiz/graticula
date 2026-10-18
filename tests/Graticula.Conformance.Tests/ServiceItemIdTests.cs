using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Each face's service document names its portal item by <c>serviceItemId</c>, and that item names the service back
/// by its <c>url</c> — the link both ways, 2026-10-18.
/// </summary>
public sealed class ServiceItemIdTests : ArcGisClient
{
    [Theory]
    [InlineData("FeatureServer")]
    [InlineData("MapServer")]
    [InlineData("VectorTileServer")]
    public async Task A_service_names_its_item_and_the_item_names_the_service(string type)
    {
        string? service = await FirstOfAsync(type);
        Assert.True(service is not null, $"The server lists no {type}, so there is nothing to link.");

        JsonElement document = await GetJsonAsync($"/rest/services/{service}/{type}");
        Assert.True(document.TryGetProperty("serviceItemId", out JsonElement id), $"{service}/{type} names no serviceItemId.");
        Assert.Matches("^[0-9a-f]{32}$", id.GetString());

        JsonElement item = await GetJsonAsync($"/sharing/rest/content/items/{id.GetString()}");
        Assert.EndsWith($"/{service}/{type}", item.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_service_page_links_its_item_on_Studio_s_item_page()
    {
        string? service = await FirstOfAsync("FeatureServer");
        Assert.NotNull(service);

        string id = (await GetJsonAsync($"/rest/services/{service}/FeatureServer")).GetProperty("serviceItemId").GetString()!;
        string page = await GetHtmlAsync($"/rest/services/{service}/FeatureServer");

        Assert.Contains($"/studio/item/{id}", page, StringComparison.Ordinal);
    }

    private async Task<string?> FirstOfAsync(string type)
    {
        JsonElement root = await GetJsonAsync("/rest/services");
        List<string?> folders = [null];
        foreach (JsonElement folder in root.GetProperty("folders").EnumerateArray())
        {
            folders.Add(folder.GetString());
        }

        foreach (string? folder in folders)
        {
            JsonElement listing = folder is null ? root : await GetJsonAsync($"/rest/services/{folder}");
            foreach (JsonElement entry in listing.GetProperty("services").EnumerateArray())
            {
                string name = entry.GetProperty("name").GetString()!;
                if (entry.GetProperty("type").GetString() == type
                    && !name.Contains("corpus_", StringComparison.Ordinal) && !name.Contains("zz_", StringComparison.Ordinal))
                {
                    return name;
                }
            }
        }

        return null;
    }
}
