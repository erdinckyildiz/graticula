using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// An item carries its terms of use and categories in the portal's document, the organisation's category schema lists
/// them, and a member keeps favourites — ADR-202.
/// </summary>
public sealed class AnItemHasTermsCategoriesAndFavoritesTests : ArcGisClient
{
    private static readonly string[] Rivers = ["/Categories/zz_Water/zz_Rivers"];

    private static string Queryable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_QUERYABLE is not set, so there is no item to annotate.");
        return qualified;
    }

    [Fact]
    public async Task Terms_and_categories_reach_the_portal_and_a_favourite_is_kept()
    {
        await RequireServerAsync();
        string item = (await GetJsonAsync($"/rest/services/{Queryable()}/FeatureServer")).GetProperty("serviceItemId").GetString()!;
        string? water = null;

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/item-categories",
                JsonSerializer.Serialize(new { name = "zz_Water" }));
            Assert.True(made == 201, $"making a category: {made} {madeBody}");
            water = JsonDocument.Parse(madeBody).RootElement.GetProperty("id").GetString();

            (int child, string childBody) = await AdminAsync(HttpMethod.Post, "/admin/item-categories",
                JsonSerializer.Serialize(new { name = "zz_Rivers", parent = water }));
            Assert.True(child == 201, $"making a child: {child} {childBody}");
            Assert.Equal("/Categories/zz_Water/zz_Rivers", JsonDocument.Parse(childBody).RootElement.GetProperty("path").GetString());

            (int set, string setBody) = await AdminAsync(HttpMethod.Put, $"/content/items/{item}/categories",
                JsonSerializer.Serialize(new { categories = Rivers }));
            Assert.True(set == 200, $"categorising: {set} {setBody}");

            (int licensed, string licensedBody) = await AdminAsync(HttpMethod.Put, $"/content/items/{item}/license",
                JsonSerializer.Serialize(new { licenseInfo = "CC BY 4.0" }));
            Assert.True(licensed == 200, $"setting the terms: {licensed} {licensedBody}");

            JsonElement document = await GetJsonAsync($"/sharing/rest/content/items/{item}?f=json");
            Assert.Contains("/Categories/zz_Water/zz_Rivers", document.GetProperty("categories").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("CC BY 4.0", document.GetProperty("licenseInfo").GetString());

            JsonElement schema = await GetJsonAsync("/sharing/rest/portals/self/categorySchema?f=json");
            Assert.Contains("zz_Water", schema.GetRawText(), StringComparison.Ordinal);

            (int liked, string likedBody) = await AdminAsync(HttpMethod.Put, $"/content/items/{item}/favorite");
            Assert.True(liked == 200, $"keeping a favourite: {liked} {likedBody}");
            (int listed, string listBody) = await AdminAsync(HttpMethod.Get, "/content/favorites");
            Assert.Equal(200, listed);
            Assert.Contains(item, listBody, StringComparison.Ordinal);

            // ADR-202 condition 3: an ArcGIS client finds them as the group its user record names.
            string favorites = (await GetJsonAsync("/sharing/rest/community/self?f=json")).GetProperty("favGroupId").GetString()!;
            JsonElement inGroup = await GetJsonAsync($"/sharing/rest/search?q=group:{favorites}&f=json");
            Assert.Contains(inGroup.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetString() == item);
            JsonElement groupSearch = await GetJsonAsync($"/sharing/rest/content/groups/{favorites}/search?f=json");
            Assert.Contains(groupSearch.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetString() == item);

            (int unliked, _) = await AdminAsync(HttpMethod.Delete, $"/content/items/{item}/favorite");
            JsonElement emptied = await GetJsonAsync($"/sharing/rest/search?q=group:{favorites}&f=json");
            Assert.DoesNotContain(emptied.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetString() == item);
            Assert.Equal(200, unliked);
            (_, string afterBody) = await AdminAsync(HttpMethod.Get, "/content/favorites");
            Assert.DoesNotContain(item, afterBody, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, $"/content/items/{item}/license", JsonSerializer.Serialize(new { licenseInfo = (string?)null }));

            if (water is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/item-categories/{water}");
            }
        }
    }
}
