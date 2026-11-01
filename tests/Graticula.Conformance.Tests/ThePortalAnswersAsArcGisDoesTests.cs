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
/// The portal and the hosting server's admin listing answer an ArcGIS client as ArcGIS does — the ArcGIS veteran's
/// review of 2026-11-02, which read the code and made no request; these make the requests.
/// </summary>
/// <remarks>
/// <b>Each test creates what it changes</b>, under <c>zz_</c> names, and deletes it. In the catalogue walk's collection
/// because one of them imports a service and one compares two listings of the whole catalogue.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ThePortalAnswersAsArcGisDoesTests : ArcGisClient
{
    /// <summary>The member the suite's environment has beside its administrator (ChangeOwnerConformanceTests).</summary>
    private const string Member = "ci_ordinary";

    private const string Empty =
        "{\"operationalLayers\":[],\"baseMap\":{\"baseMapLayers\":[],\"title\":\"None\"},\"version\":\"2.31\"}";

    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content = null, bool signedIn = true)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };

        if (signedIn)
        {
            string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private Task<JsonElement> PostAsync(string path, IDictionary<string, string> form) =>
        SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form));

    private async Task<string> AddMapAsync(string title)
    {
        JsonElement added = await PostAsync($"/sharing/rest/content/users/{User}/addItem?f=json",
            new Dictionary<string, string> { ["type"] = "Web Map", ["title"] = title, ["text"] = Empty });

        return added.GetProperty("id").GetString()!;
    }

    private static bool Succeeded(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("success", out JsonElement success) && success.GetBoolean();

    [Fact]
    public async Task An_administrator_changes_reads_and_deletes_a_members_item_through_the_members_own_path()
    {
        string title = $"zz_theirs_{Guid.NewGuid():N}"[..24];
        string id = await AddMapAsync(title);

        try
        {
            await SendAsync(HttpMethod.Put, $"/content/webmaps/{id}/owner",
                new StringContent(JsonSerializer.Serialize(new { to = Member }), Encoding.UTF8, "application/json"));
            Assert.Equal(Member, (await SendAsync(HttpMethod.Get, $"/content/webmaps/{id}")).GetProperty("owner").GetString());

            // What the ArcGIS API for Python sends for item.update(): the owner's path, not the caller's.
            JsonElement updated = await PostAsync($"/sharing/rest/content/users/{Member}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["title"] = title + " changed" });
            Assert.True(Succeeded(updated), updated.GetRawText());

            JsonElement read = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/users/{Member}/items/{id}?f=json");
            Assert.Equal(title + " changed", read.GetProperty("item").GetProperty("title").GetString());

            // A name that is nobody's is still refused as a user that does not exist.
            JsonElement nobody = await PostAsync($"/sharing/rest/content/users/zz_no_such_member/items/{id}/update?f=json",
                new Dictionary<string, string> { ["title"] = "never" });
            Assert.True(nobody.TryGetProperty("error", out _), nobody.GetRawText());

            JsonElement deleted = await PostAsync($"/sharing/rest/content/users/{Member}/items/{id}/delete?f=json",
                new Dictionary<string, string>());
            Assert.True(Succeeded(deleted), deleted.GetRawText());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    [Fact]
    public async Task An_item_is_moved_out_of_a_folder_through_the_folders_own_path()
    {
        JsonElement made = await PostAsync($"/sharing/rest/content/users/{User}/createFolder?f=json",
            new Dictionary<string, string> { ["title"] = $"zz_move_{Guid.NewGuid():N}"[..20] });
        string folder = made.GetProperty("folder").GetProperty("id").GetString()!;
        string id = await AddMapAsync($"zz_moved_{Guid.NewGuid():N}"[..24]);

        try
        {
            JsonElement into = await PostAsync($"/sharing/rest/content/users/{User}/items/{id}/move?f=json",
                new Dictionary<string, string> { ["folder"] = folder });
            Assert.True(Succeeded(into), into.GetRawText());

            // Where the ArcGIS API for Python sends item.move() for an item in a folder: 404 until 2026-11-02.
            JsonElement back = await PostAsync($"/sharing/rest/content/users/{User}/{folder}/items/{id}/move?f=json",
                new Dictionary<string, string> { ["folder"] = "/" });
            Assert.True(Succeeded(back), back.GetRawText());
            Assert.Equal(JsonValueKind.Null, back.GetProperty("folder").ValueKind);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
            await PostAsync($"/sharing/rest/content/users/{User}/{folder}/delete?f=json", new Dictionary<string, string>());
        }
    }

    [Fact]
    public async Task A_service_items_summary_and_description_changed_to_two_texts_are_refused_and_nothing_is_written()
    {
        string name = $"zz_text_{Guid.NewGuid():N}"[..18];
        string feature = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[29,41]},"properties":{"n":1}}]}""";

        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(feature)), "file", "one.geojson");
        form.Add(new StringContent(name), "name");
        await SendAsync(HttpMethod.Post, "/admin/hosted/import", form);

        try
        {
            JsonElement found = await SendAsync(HttpMethod.Get,
                $"/sharing/rest/search?q={Uri.EscapeDataString($"title:\"{name}\"")}&num=100&f=json");
            string id = found.GetProperty("results").EnumerateArray()
                .First(r => r.GetProperty("title").GetString() == name && r.GetProperty("type").GetString() == "Feature Service")
                .GetProperty("id").GetString()!;

            JsonElement refused = await PostAsync($"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["snippet"] = "a summary", ["description"] = "a description" });
            Assert.Contains("'snippet'", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

            JsonElement unchanged = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.NotEqual("a description", unchanged.GetProperty("description").ToString());

            JsonElement summary = await PostAsync($"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["snippet"] = "a summary" });
            Assert.True(Succeeded(summary), summary.GetRawText());

            JsonElement written = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal("a summary", written.GetProperty("snippet").GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    private static HashSet<string> Directory(JsonElement document) =>
        [.. document.GetProperty("services").EnumerateArray()
            .Select(s => $"{s.GetProperty("name").GetString()!.Split('/')[^1]}|{s.GetProperty("type").GetString()}")];

    private static HashSet<string> Admin(JsonElement document) =>
        [.. document.GetProperty("services").EnumerateArray()
            .Select(s => $"{s.GetProperty("serviceName").GetString()}|{s.GetProperty("type").GetString()}")];

    [Fact]
    public async Task The_hosting_servers_admin_listing_lists_what_the_services_directory_lists()
    {
        JsonElement root = await SendAsync(HttpMethod.Get, "/arcgis/admin/services?f=json");
        JsonElement directory = await SendAsync(HttpMethod.Get, "/rest/services?f=json");

        Assert.Equal("/", root.GetProperty("folderName").GetString());
        Assert.Equal(Directory(directory), Admin(root));

        foreach (JsonElement service in root.GetProperty("services").EnumerateArray())
        {
            Assert.Equal("/", service.GetProperty("folderName").GetString());
        }

        JsonElement hosted = await SendAsync(HttpMethod.Get, "/arcgis/admin/services/hosted?f=json");
        JsonElement hostedDirectory = await SendAsync(HttpMethod.Get, "/rest/services/hosted?f=json");

        Assert.Equal(Directory(hostedDirectory), Admin(hosted));
        Assert.All(hosted.GetProperty("services").EnumerateArray(),
            s => Assert.Equal("hosted", s.GetProperty("folderName").GetString(), ignoreCase: true));

        JsonElement missing = await SendAsync(HttpMethod.Get, $"/arcgis/admin/services/zz_no_folder_{Guid.NewGuid():N}?f=json");
        Assert.Equal("error", missing.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_search_narrowed_by_what_this_portal_cannot_read_is_refused_and_by_categories_is_narrowed()
    {
        JsonElement bbox = await SendAsync(HttpMethod.Get, "/sharing/rest/search?q=&bbox=-10,-10,10,10&f=json");
        Assert.Contains("'bbox'", bbox.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        JsonElement filter = await SendAsync(HttpMethod.Get, "/sharing/rest/search?q=&filter=title%3Aroads&f=json");
        Assert.Contains("'filter'", filter.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        JsonElement none = await SendAsync(HttpMethod.Get,
            $"/sharing/rest/search?q=&categories=/Categories/zz_none_{Guid.NewGuid():N}&f=json");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_service_definition_just_added_is_read_back_by_its_owner_and_by_nobody_else()
    {
        string title = $"zz_sd_{Guid.NewGuid():N}"[..20];
        JsonElement added = await PostAsync($"/sharing/rest/content/users/{User}/addItem?f=json",
            new Dictionary<string, string> { ["type"] = "Service Definition", ["title"] = title });
        string id = added.GetProperty("id").GetString()!;

        try
        {
            // What gis.content.add() reads next.
            JsonElement item = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal("Service Definition", item.GetProperty("type").GetString());
            Assert.Equal(title, item.GetProperty("title").GetString());
            Assert.Equal(User, item.GetProperty("owner").GetString(), ignoreCase: true);

            JsonElement mine = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/users/{User}/items/{id}?f=json");
            Assert.Equal(id, mine.GetProperty("item").GetProperty("id").GetString());

            JsonElement anonymous = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json", signedIn: false);
            Assert.True(anonymous.TryGetProperty("error", out _), anonymous.GetRawText());
        }
        finally
        {
            await PostAsync($"/sharing/rest/content/users/{User}/items/{id}/delete?f=json", new Dictionary<string, string>());
        }
    }
}
