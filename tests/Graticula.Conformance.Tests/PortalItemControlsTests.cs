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
/// Four portal doors onto what Studio already does — the ArcGIS veteran's review of 2026-11-02 (ADR-214 §5a): the
/// ArcGIS API for Python's <c>item.protect()</c>, <c>item.reassign_to()</c>, <c>Folder.rename()</c> and
/// <c>item.shared_with</c> each met a 404, although delete protection, Change owner, folder rename and group sharing all
/// exist behind Studio's routes.
/// </summary>
/// <remarks>
/// <b>Each test makes what it changes</b>, under <c>zz_</c> names, and removes it, protection first. The receiver of a
/// reassignment is <c>ci_ordinary</c>, the member <see cref="ChangeOwnerConformanceTests"/> gives items to.
/// </remarks>
[Collection("catalogue walk")]
public sealed class PortalItemControlsTests : ArcGisClient
{
    private const string Receiver = "ci_ordinary";

    private const string Empty =
        "{\"operationalLayers\":[],\"baseMap\":{\"baseMapLayers\":[],\"title\":\"None\"},\"version\":\"2.31\"}";

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

    private static string ErrorOf(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("error", out JsonElement error)
            ? error.GetProperty("message").GetString() ?? string.Empty
            : throw new Xunit.Sdk.XunitException($"Expected a portal error, and was answered {answer.GetRawText()}");

    [Fact]
    public async Task A_map_is_protected_and_unprotected_through_the_portal_and_the_item_says_so()
    {
        string id = await AddMapAsync($"zz_protect_{Guid.NewGuid():N}"[..24]);

        try
        {
            JsonElement protect = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/protect?f=json",
                new Dictionary<string, string>());
            Assert.True(protect.TryGetProperty("success", out JsonElement done) && done.GetBoolean(), protect.GetRawText());

            JsonElement item = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.True(item.GetProperty("protected").GetBoolean(), item.GetRawText());

            // The protection is the one Studio's delete honours, through the portal's delete too.
            JsonElement refused = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/delete?f=json",
                new Dictionary<string, string>());
            Assert.NotEmpty(ErrorOf(refused));

            JsonElement unprotect = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/unprotect?f=json",
                new Dictionary<string, string>());
            Assert.True(unprotect.GetProperty("success").GetBoolean(), unprotect.GetRawText());

            item = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.False(item.GetProperty("protected").GetBoolean(), item.GetRawText());

            JsonElement missing = await SendAsync(HttpMethod.Post,
                $"/sharing/rest/content/users/{User}/items/{new string('0', 32)}/protect?f=json", new Dictionary<string, string>());
            Assert.Equal("Item does not exist or is inaccessible.", ErrorOf(missing));
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, $"/content/webmaps/{id}/protection", "{\"protected\":false}");
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    [Fact]
    public async Task A_map_is_reassigned_to_another_member_and_a_name_that_is_nobody_is_refused()
    {
        string id = await AddMapAsync($"zz_reassign_{Guid.NewGuid():N}"[..24]);

        try
        {
            JsonElement nobody = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/reassign?f=json",
                new Dictionary<string, string> { ["targetUsername"] = "zz_no_such_member" });
            Assert.Contains("zz_no_such_member", ErrorOf(nobody), StringComparison.Ordinal);

            JsonElement outsider = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/reassign?f=json",
                new Dictionary<string, string> { ["targetUsername"] = $"elsewhere\\{Receiver}" });
            Assert.NotEmpty(ErrorOf(outsider));

            JsonElement noFolder = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/reassign?f=json",
                new Dictionary<string, string> { ["targetUsername"] = Receiver, ["targetFoldername"] = "zz no such folder" });
            Assert.Contains("zz no such folder", ErrorOf(noFolder), StringComparison.Ordinal);

            JsonElement before = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(User, before.GetProperty("owner").GetString(), ignoreCase: true);

            JsonElement given = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/reassign?f=json",
                new Dictionary<string, string> { ["targetUsername"] = Receiver });
            Assert.True(given.TryGetProperty("success", out JsonElement done) && done.GetBoolean(), given.GetRawText());
            Assert.Equal(id, given.GetProperty("itemId").GetString());

            // Read back where Studio reads it, as ChangeOwnerConformanceTests does, not from the door's own answer.
            JsonElement after = await SendAsync(HttpMethod.Get, $"/content/webmaps/{id}");
            Assert.Equal(Receiver, after.GetProperty("owner").GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    [Fact]
    public async Task A_folder_is_renamed_by_the_rules_it_was_created_by()
    {
        string first = $"zz_folder_{Guid.NewGuid():N}"[..24];
        string second = $"zz_folder_{Guid.NewGuid():N}"[..24];
        string renamed = $"zz_renamed_{Guid.NewGuid():N}"[..24];
        List<string> made = [];

        try
        {
            foreach (string title in new[] { first, second })
            {
                JsonElement created = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/createFolder?f=json",
                    new Dictionary<string, string> { ["title"] = title });
                made.Add(created.GetProperty("folder").GetProperty("id").GetString()!);
            }

            JsonElement answer = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/{made[0]}/updateFolder?f=json",
                new Dictionary<string, string> { ["title"] = renamed });
            Assert.True(answer.TryGetProperty("success", out JsonElement done) && done.GetBoolean(), answer.GetRawText());
            Assert.Equal(renamed, answer.GetProperty("folder").GetProperty("title").GetString());

            JsonElement content = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/users/{User}?f=json");
            Assert.Contains(content.GetProperty("folders").EnumerateArray(),
                f => f.GetProperty("id").GetString() == made[0] && f.GetProperty("title").GetString() == renamed);

            JsonElement taken = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/{made[1]}/updateFolder?f=json",
                new Dictionary<string, string> { ["title"] = renamed });
            Assert.Equal($"Folder '{renamed}' already exists.", ErrorOf(taken));

            JsonElement blank = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/{made[1]}/updateFolder?f=json",
                new Dictionary<string, string> { ["title"] = " " });
            Assert.NotEmpty(ErrorOf(blank));
        }
        finally
        {
            foreach (string folder in made)
            {
                await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/{folder}/delete?f=json", new Dictionary<string, string>());
            }
        }
    }

    [Fact]
    public async Task An_items_groups_are_the_ones_it_is_shared_with_listed_by_where_the_caller_stands()
    {
        string group = $"zz_shared_{Guid.NewGuid():N}"[..24];
        string id = await AddMapAsync($"zz_groups_{Guid.NewGuid():N}"[..24]);

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = group }));
            Assert.True(made is 200 or 201, $"making {group}: {made} {madeBody}");

            JsonElement unshared = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}/groups?f=json");
            Assert.Empty(unshared.GetProperty("admin").EnumerateArray());
            Assert.Empty(unshared.GetProperty("member").EnumerateArray());
            Assert.Empty(unshared.GetProperty("other").EnumerateArray());

            (int shared, string sharedBody) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{group}/maps/{id}");
            Assert.True(shared == 200, $"sharing with {group}: {shared} {sharedBody}");

            JsonElement groups = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}/groups?f=json");
            JsonElement mine = Assert.Single(groups.GetProperty("admin").EnumerateArray());
            Assert.Equal(group, mine.GetProperty("title").GetString());
            Assert.Equal(32, mine.GetProperty("id").GetString()!.Length);

            JsonElement missing = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{new string('0', 32)}/groups?f=json");
            Assert.Equal("Item does not exist or is inaccessible.", ErrorOf(missing));

            (System.Net.HttpStatusCode _, string anonymous) = await AnonymousAsync($"/sharing/rest/content/items/{id}/groups?f=json");
            Assert.Contains("does not exist or is inaccessible", anonymous, StringComparison.Ordinal);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{group}");
        }
    }
}
