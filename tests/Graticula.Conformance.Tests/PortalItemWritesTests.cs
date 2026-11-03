using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// ADR-260, from the ArcGIS veteran's review of v1.0.499 (#9): a service item's title is the item's. An update
    /// writes it and leaves the service's name — its address — alone; search finds it by the title; Studio's door
    /// returns it to the name. ADR-214 §5.4 refused any other title.
    /// </summary>
    [Fact]
    public async Task A_service_items_title_is_its_own_and_its_name_stays_its_address()
    {
        const string Service = "hosted/ci_buildings";
        JsonElement listing = await SendAsync(HttpMethod.Get, "/content/items");
        string id = listing.GetProperty("items").EnumerateArray()
            .First(i => i.TryGetProperty("name", out JsonElement n) && n.GetString() == Service).GetProperty("itemId").GetString()!;
        string title = $"Buildings of the test city {Guid.NewGuid():N}"[..40];

        try
        {
            JsonElement updated = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["title"] = title });
            Assert.True(updated.GetProperty("success").GetBoolean(), updated.GetRawText());

            JsonElement item = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(title, item.GetProperty("title").GetString());
            Assert.Equal("ci_buildings", item.GetProperty("name").GetString());
            Assert.EndsWith("/rest/services/hosted/ci_buildings/FeatureServer", item.GetProperty("url").GetString());

            JsonElement found = await SendAsync(HttpMethod.Get,
                $"/sharing/rest/search?f=json&q={Uri.EscapeDataString($"title:\"{title}\"")}");
            Assert.Contains(found.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetString() == id);

            JsonElement tooLong = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["title"] = new string('t', 251) });
            Assert.True(tooLong.TryGetProperty("error", out _), tooLong.GetRawText());

            // Studio's door: an empty title is the service's name again.
            Assert.Equal("ci_buildings", (await RetitleAsync(string.Empty)).GetProperty("title").GetString());
        }
        finally
        {
            await RetitleAsync(string.Empty);
        }
    }

    private async Task<JsonElement> RetitleAsync(string title)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        using HttpRequestMessage request = new(HttpMethod.Put, $"{root}/admin/services/ci_buildings/title?folder=hosted")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { title }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage answer = await Http.SendAsync(request);
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone();
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

    /// <summary>
    /// ADR-214 condition 1: the bin has a screen, so a delete that asks to skip it is not the only way back, and
    /// <c>permanentDelete</c> means what Enterprise 11.4 means by it. It was recycled all the same before.
    /// </summary>
    [Fact]
    public async Task A_permanent_delete_through_the_portal_leaves_nothing_in_the_recycle_bin()
    {
        string single = await AddMapAsync($"zz_perm_{Guid.NewGuid():N}"[..24]);
        string several = await AddMapAsync($"zz_perms_{Guid.NewGuid():N}"[..24]);

        JsonElement one = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{single}/delete?f=json",
            new Dictionary<string, string> { ["permanentDelete"] = "true" });
        Assert.True(one.GetProperty("success").GetBoolean(), one.GetRawText());

        JsonElement results = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/deleteItems?f=json",
            new Dictionary<string, string> { ["items"] = several, ["permanentDelete"] = "true" });
        Assert.True(results.GetProperty("results")[0].GetProperty("success").GetBoolean(), results.GetRawText());

        string bin = (await SendAsync(HttpMethod.Get, "/admin/recycle-bin")).GetRawText();
        Assert.DoesNotContain(single, bin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(several, bin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ADR-214 §5c: the bin through the portal, as the ArcGIS API for Python reaches it — <c>portals/self</c> says it is
    /// on, <c>content/users/{u}?inRecycleBin=true</c> lists it, <c>restore</c> brings an item back and a permanent delete
    /// of what is already in it empties its entry.
    /// </summary>
    [Fact]
    public async Task The_recycle_bin_is_listed_restored_from_and_emptied_through_the_portal()
    {
        JsonElement self = await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json");
        Assert.True(self.GetProperty("recycleBinSupported").GetBoolean());
        Assert.True(self.GetProperty("recycleBinEnabled").GetBoolean());

        string kept = await AddMapAsync($"zz_bin_{Guid.NewGuid():N}"[..24]);
        string gone = await AddMapAsync($"zz_bin_{Guid.NewGuid():N}"[..24]);

        try
        {
            foreach (string id in new[] { kept, gone })
            {
                JsonElement deleted = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/delete?f=json",
                    new Dictionary<string, string>());
                Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.GetRawText());
            }

            JsonElement bin = await SendAsync(HttpMethod.Get,
                $"/sharing/rest/content/users/{User}?f=json&inRecycleBin=true&foldersContent=true&start=1&num=100&sortField=modified&sortOrder=desc");
            List<JsonElement> listed = [.. bin.GetProperty("items").EnumerateArray()];
            JsonElement entry = listed.Single(i => i.GetProperty("id").GetString() == kept);
            Assert.Equal("Web Map", entry.GetProperty("type").GetString());
            Assert.Equal(User, entry.GetProperty("owner").GetString(), StringComparer.OrdinalIgnoreCase);
            Assert.Contains(listed, i => i.GetProperty("id").GetString() == gone);

            JsonElement restored = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{kept}/restore?f=json",
                new Dictionary<string, string>());
            Assert.True(restored.GetProperty("success").GetBoolean(), restored.GetRawText());
            Assert.Equal(kept, restored.GetProperty("itemId").GetString());
            Assert.Equal(kept, (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{kept}?f=json")).GetProperty("id").GetString());

            JsonElement purged = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{gone}/delete?f=json",
                new Dictionary<string, string> { ["permanentDelete"] = "true" });
            Assert.True(purged.GetProperty("success").GetBoolean(), purged.GetRawText());

            string after = (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/users/{User}?f=json&inRecycleBin=true&num=100")).GetRawText();
            Assert.DoesNotContain(gone, after, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(kept, after, StringComparison.OrdinalIgnoreCase);

            // Restoring what is not in the bin is refused, not answered success.
            JsonElement again = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{gone}/restore?f=json",
                new Dictionary<string, string>());
            Assert.True(again.TryGetProperty("error", out _), again.GetRawText());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{kept}");
        }
    }

    /// <summary>
    /// ADR-214 §5b: <c>created</c> and <c>modified</c> ranges are answered, and a range on a field with none is refused —
    /// both answered <c>total: 0</c> on the showcase while <c>q=*</c> found twenty-four items.
    /// </summary>
    [Fact]
    public async Task A_date_range_search_finds_what_q_star_finds_and_a_range_without_a_field_is_refused()
    {
        string id = await AddMapAsync($"zz_range_{Guid.NewGuid():N}"[..24]);

        try
        {
            JsonElement all = await SendAsync(HttpMethod.Get, "/sharing/rest/search?f=json&num=100&q="
                + Uri.EscapeDataString($"id:{id} AND created:[0 TO 9999999999999]"));
            Assert.Contains(all.GetProperty("results").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            JsonElement recent = await SendAsync(HttpMethod.Get, "/sharing/rest/search?f=json&num=100&q="
                + Uri.EscapeDataString($"id:{id} AND modified:[{now - 3_600_000:D19} TO {now + 3_600_000:D19}]"));
            Assert.Contains(recent.GetProperty("results").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

            JsonElement refused = await SendAsync(HttpMethod.Get,
                "/sharing/rest/search?f=json&q=" + Uri.EscapeDataString("title:[a TO z]"));
            Assert.True(refused.TryGetProperty("error", out _), refused.GetRawText());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
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

    /// <summary>
    /// <c>community/createGroup</c> — found missing 2026-11-01, sharing to a group from Pro at a second organisation:
    /// the portal shared into groups but answered 404 when asked to make one, which is the Python API's
    /// <c>gis.groups.create</c>.
    /// </summary>
    [Fact]
    public async Task A_group_made_through_the_portal_is_owned_by_its_maker_with_the_settings_sent()
    {
        string title = $"ZZ Portal Group {Guid.NewGuid():N}"[..28];
        JsonElement made = await SendAsync(HttpMethod.Post, "/sharing/rest/community/createGroup?f=json",
            new Dictionary<string, string>
            {
                ["title"] = title, ["access"] = "org", ["isInvitationOnly"] = "true", ["isViewOnly"] = "false",
                ["tags"] = "not kept", ["description"] = "made by a portal client",
            });

        Assert.True(made.TryGetProperty("success", out JsonElement success) && success.GetBoolean(), made.GetRawText());
        JsonElement group = made.GetProperty("group");
        string id = group.GetProperty("id").GetString()!;

        try
        {
            Assert.Equal(title, group.GetProperty("title").GetString());
            Assert.Equal("org", group.GetProperty("access").GetString());
            Assert.True(group.GetProperty("isInvitationOnly").GetBoolean(), made.GetRawText());
            Assert.False(group.GetProperty("isViewOnly").GetBoolean(), made.GetRawText());
            Assert.Equal("owner", group.GetProperty("userMembership").GetProperty("memberType").GetString());

            JsonElement found = await SendAsync(HttpMethod.Get, $"/sharing/rest/community/groups/{id}?f=json");
            Assert.Equal(title, found.GetProperty("title").GetString());
        }
        finally
        {
            // The name the portal made from the title: lower case, a hyphen for each run of anything else.
            await SendAsync(HttpMethod.Delete, $"/admin/groups/{title.ToLowerInvariant().Replace(' ', '-')}");
        }
    }
}
