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
/// ADR-267, the ArcGIS veteran's review of v1.0.499 (#18): an item carries a metadata document — uploaded through
/// <c>update</c>, read at <c>info/metadata/metadata.xml</c>, replaced — and members' comments, added, listed, read and
/// deleted by the people Portal lets delete them. Both were 404s.
/// </summary>
public sealed class PortalItemMetadataCommentsTests : ArcGisClient
{
    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private const string ArcGisMetadata =
        """<?xml version="1.0" encoding="UTF-8"?><metadata xml:lang="en"><Esri><ArcGISFormat>1.0</ArcGISFormat></Esri><dataIdInfo><idPurp>MARKER</idPurp></dataIdInfo></metadata>""";

    private async Task<(HttpStatusCode Status, JsonElement Json, string Text, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, HttpContent? content = null, string? token = "", bool anonymous = false)
    {
        string root = await RequireServerAsync();
        token = anonymous ? null : token == "" ? await TokenAsync(root) : token;

        using HttpRequestMessage request = new(method, $"{root}{path}");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = content;
        HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonElement json = text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (response.StatusCode, json, text, response);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("f", "json")));

    private static MultipartFormDataContent MetadataPart(string xml)
    {
        MultipartFormDataContent form = new() { { new StringContent("json"), "f" } };
        ByteArrayContent file = new(Encoding.UTF8.GetBytes(xml));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        form.Add(file, "metadata", "metadata.xml");
        return form;
    }

    private async Task<string> AddMapAsync(string title)
    {
        var (_, added, raw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem",
            Form(("type", "Web Map"), ("title", title), ("text", """{"operationalLayers":[],"version":"2.31"}""")));
        Assert.True(added.TryGetProperty("id", out JsonElement id), raw);
        return id.GetString()!;
    }

    private async Task DeleteAsync(string id) =>
        await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{id}/delete", Form(("permanentDelete", "true")));

    private async Task<(string Name, string Token)> MemberAsync()
    {
        string root = await RequireServerAsync();
        string name = $"zz_talk_{Guid.NewGuid():N}"[..20];
        var (_, created, raw, _) = await SendAsync(HttpMethod.Post, "/admin/members",
            new StringContent(JsonSerializer.Serialize(new { name, role = "user", userType = "creator" }), Encoding.UTF8, "application/json"));
        string issued = created.GetProperty("password").GetString() ?? throw new InvalidOperationException(raw);

        async Task<string> LoginAsync(string password)
        {
            using HttpResponseMessage response = await Http.PostAsync($"{root}/rest/auth/login",
                new StringContent(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        }

        string first = await LoginAsync(issued);
        await SendAsync(HttpMethod.Post, "/rest/auth/password", new StringContent(
            JsonSerializer.Serialize(new { currentPassword = issued, newPassword = issued + "X1" }), Encoding.UTF8, "application/json"), first);
        return (name, await LoginAsync(issued + "X1"));
    }

    [Fact]
    public async Task Metadata_is_uploaded_read_replaced_kept_through_the_recycle_bin_and_a_dtd_is_refused()
    {
        string map = await AddMapAsync($"zz metadata {Guid.NewGuid():N}"[..24]);
        string update = $"/sharing/rest/content/users/{User}/items/{map}/update";
        string address = $"/sharing/rest/content/items/{map}/info/metadata/metadata.xml";

        try
        {
            // None yet: refused with the sentence the ArcGIS API for Python reads as "no metadata".
            var (noneStatus, _, noneText, _) = await SendAsync(HttpMethod.Get, address);
            Assert.Equal(HttpStatusCode.BadRequest, noneStatus);
            Assert.Contains("Metadata for item not found", noneText, StringComparison.Ordinal);

            var (_, uploaded, uploadedRaw, _) = await SendAsync(HttpMethod.Post, update, MetadataPart(ArcGisMetadata.Replace("MARKER", "first")));
            Assert.True(uploaded.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), uploadedRaw);

            var (status, _, text, response) = await SendAsync(HttpMethod.Get, address);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("<idPurp>first</idPurp>", text, StringComparison.Ordinal);
            Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

            var (_, item, _, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{map}?f=json");
            Assert.Contains("Metadata", item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));

            // Replaced, and an ISO 19139 document is kept as well as ArcGIS's.
            const string Iso = """<gmd:MD_Metadata xmlns:gmd="http://www.isotc211.org/2005/gmd"><gmd:fileIdentifier>second</gmd:fileIdentifier></gmd:MD_Metadata>""";
            var (_, replaced, replacedRaw, _) = await SendAsync(HttpMethod.Post, update, MetadataPart(Iso));
            Assert.True(replaced.TryGetProperty("success", out _), replacedRaw);
            Assert.Contains("second", (await SendAsync(HttpMethod.Get, address)).Text, StringComparison.Ordinal);

            // XXE and an entity expansion are refused, whole: the metadata held is unchanged.
            const string Xxe = """<?xml version="1.0"?><!DOCTYPE metadata [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><metadata><idPurp>&xxe;</idPurp></metadata>""";
            const string Laughs = """<?xml version="1.0"?><!DOCTYPE metadata [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><metadata>&b;</metadata>""";
            foreach (string hostile in new[] { Xxe, Laughs, "<html><script>alert(1)</script></html>", "<metadata>" })
            {
                var (_, refused, refusedRaw, _) = await SendAsync(HttpMethod.Post, update, MetadataPart(hostile));
                Assert.True(refused.TryGetProperty("error", out _), refusedRaw);
            }

            string held = (await SendAsync(HttpMethod.Get, address)).Text;
            Assert.Contains("second", held, StringComparison.Ordinal);
            Assert.DoesNotContain("[fonts]", held, StringComparison.OrdinalIgnoreCase);

            // An anonymous caller does not read a private item's metadata.
            Assert.DoesNotContain("second", (await SendAsync(HttpMethod.Get, address, anonymous: true)).Text, StringComparison.Ordinal);

            // The recycle bin keeps it and the item's comments with the item, and a restore brings them back.
            Assert.True((await SendAsync(HttpMethod.Post, $"/sharing/rest/content/items/{map}/addComment", Form(("comment", "kept")))).Json
                .TryGetProperty("commentId", out _));
            var (_, binned, binnedRaw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{map}/delete", Form());
            Assert.True(binned.GetProperty("success").GetBoolean(), binnedRaw);
            var (_, restored, restoredRaw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{map}/restore", Form());
            Assert.True(restored.GetProperty("success").GetBoolean(), restoredRaw);
            Assert.Contains("second", (await SendAsync(HttpMethod.Get, address)).Text, StringComparison.Ordinal);
            Assert.Equal(1, (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{map}?f=json")).Json.GetProperty("numComments").GetInt32());
        }
        finally
        {
            await DeleteAsync(map);
        }
    }

    [Fact]
    public async Task Comments_are_added_listed_read_and_deleted_by_their_author_the_owner_or_nobody_else()
    {
        string map = await AddMapAsync($"zz comments {Guid.NewGuid():N}"[..24]);
        string items = $"/sharing/rest/content/items/{map}";
        (string member, string memberToken) = await MemberAsync();

        try
        {
            var (_, first, firstRaw, _) = await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", "<b>owner's</b> note")));
            string ownerComment = first.TryGetProperty("commentId", out JsonElement c1) ? c1.GetString()! : throw new InvalidOperationException(firstRaw);

            // Kept as written, and read back one by one and in a page.
            var (_, one, _, _) = await SendAsync(HttpMethod.Get, $"{items}/comments/{ownerComment}?f=json");
            Assert.Equal("<b>owner's</b> note", one.GetProperty("comment").GetString());
            Assert.Equal(User, one.GetProperty("owner").GetString());

            // A stranger and an anonymous caller neither read nor add to a private item's comments.
            Assert.True((await SendAsync(HttpMethod.Get, $"{items}/comments?f=json", token: memberToken)).Json.TryGetProperty("error", out _));
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", "x")), token: memberToken)).Json
                .TryGetProperty("error", out _));
            Assert.True((await SendAsync(HttpMethod.Get, $"{items}/comments?f=json", anonymous: true)).Json.TryGetProperty("error", out _));

            // Shared with the organization: the member reads and comments; an anonymous caller still adds nothing.
            var (_, shared, sharedRaw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{map}/share",
                Form(("everyone", "false"), ("org", "true")));
            Assert.False(shared.TryGetProperty("error", out _), sharedRaw);

            var (_, theirs, theirsRaw, _) = await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", "member's")), token: memberToken);
            string memberComment = theirs.TryGetProperty("commentId", out JsonElement c2) ? c2.GetString()! : throw new InvalidOperationException(theirsRaw);
            var (_, second, _, _) = await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", "member's second")), token: memberToken);
            string memberSecond = second.GetProperty("commentId").GetString()!;
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", "anon")), anonymous: true)).Json
                .TryGetProperty("error", out _));

            var (_, listed, _, _) = await SendAsync(HttpMethod.Get, $"{items}/comments?f=json", token: memberToken);
            Assert.Equal(3, listed.GetProperty("total").GetInt32());
            Assert.Equal(-1, listed.GetProperty("nextStart").GetInt32());
            Assert.Equal(3, (await SendAsync(HttpMethod.Get, $"{items}?f=json")).Json.GetProperty("numComments").GetInt32());

            // Too long, or empty, is refused.
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", new string('x', 2049))))).Json
                .TryGetProperty("error", out _));
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/addComment", Form(("comment", "  ")))).Json.TryGetProperty("error", out _));

            // The member may not delete the owner's comment; may delete their own; the owner deletes the member's.
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/comments/{ownerComment}/delete", Form(), token: memberToken)).Json
                .TryGetProperty("error", out _));
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/comments/{memberComment}/delete", Form(), token: memberToken)).Json
                .GetProperty("success").GetBoolean());
            Assert.True((await SendAsync(HttpMethod.Post, $"{items}/comments/{memberSecond}/delete", Form())).Json
                .GetProperty("success").GetBoolean());

            Assert.Equal(1, (await SendAsync(HttpMethod.Get, $"{items}?f=json")).Json.GetProperty("numComments").GetInt32());
            Assert.True((await SendAsync(HttpMethod.Get, $"{items}/comments/{memberComment}?f=json")).Json.TryGetProperty("error", out _));
        }
        finally
        {
            await DeleteAsync(map);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{member}");
        }
    }

    [Fact]
    public async Task AddItem_takes_metadata_and_listings_filter_and_sort_by_what_the_item_page_says()
    {
        // ADR-267 condition 3: addItem's metadata part, checked as update checks it.
        MultipartFormDataContent add = MetadataPart(ArcGisMetadata.Replace("MARKER", "added"));
        add.Add(new StringContent("Web Map"), "type");
        add.Add(new StringContent($"zz described {Guid.NewGuid():N}"[..24]), "title");
        add.Add(new StringContent("""{"operationalLayers":[],"version":"2.31"}"""), "text");
        var (_, added, addedRaw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", add);
        string described = added.TryGetProperty("id", out JsonElement id) ? id.GetString()! : throw new InvalidOperationException(addedRaw);
        string plain = await AddMapAsync($"zz plain {Guid.NewGuid():N}"[..22]);

        try
        {
            Assert.Contains("<idPurp>added</idPurp>",
                (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{described}/info/metadata/metadata.xml")).Text, StringComparison.Ordinal);

            // A DTD is refused before anything is added.
            MultipartFormDataContent hostile = MetadataPart("""<?xml version="1.0"?><!DOCTYPE m [<!ENTITY x "y">]><m>&x;</m>""");
            hostile.Add(new StringContent("Web Map"), "type");
            hostile.Add(new StringContent("zz refused"), "title");
            hostile.Add(new StringContent("""{"operationalLayers":[],"version":"2.31"}"""), "text");
            var (_, refused, refusedRaw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/addItem", hostile);
            Assert.True(refused.TryGetProperty("error", out _) && !refused.TryGetProperty("id", out _), refusedRaw);

            for (int i = 0; i < 2; i++)
            {
                Assert.True((await SendAsync(HttpMethod.Post, $"/sharing/rest/content/items/{described}/addComment", Form(("comment", $"c{i}")))).Json
                    .TryGetProperty("commentId", out _));
            }

            // ADR-267 condition 2: the search filters by the Metadata keyword and says numComments, as the item's page does.
            async Task<JsonElement> SearchAsync(string q) =>
                (await SendAsync(HttpMethod.Get, $"/sharing/rest/search?f=json&num=100&q={Uri.EscapeDataString(q)}")).Json;

            JsonElement found = await SearchAsync($"id:{described} AND typekeywords:Metadata");
            Assert.Equal(1, found.GetProperty("total").GetInt32());
            Assert.Equal(2, found.GetProperty("results")[0].GetProperty("numComments").GetInt32());
            Assert.Equal(0, (await SearchAsync($"id:{plain} AND typekeywords:Metadata")).GetProperty("total").GetInt32());

            // My Content carries them too, and sorts by the comment count it says.
            JsonElement mine = (await SendAsync(HttpMethod.Get,
                $"/sharing/rest/content/users/{User}?f=json&num=100&sortField=numComments&sortOrder=desc")).Json;
            int[] counts = [.. mine.GetProperty("items").EnumerateArray().Select(item => item.TryGetProperty("numComments", out JsonElement n) ? n.GetInt32() : 0)];
            Assert.Equal(counts.OrderByDescending(c => c), counts);
            Assert.Contains(mine.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("id").GetString() == described && item.GetProperty("numComments").GetInt32() == 2
                && item.GetProperty("typeKeywords").EnumerateArray().Any(k => k.GetString() == "Metadata"));
        }
        finally
        {
            await DeleteAsync(described);
            await DeleteAsync(plain);
        }
    }

    [Fact]
    public async Task An_image_services_item_takes_comments_and_metadata_as_a_feature_services_does()
    {
        // ADR-267 condition 1: an image service's item is its coverage's, and its comments and metadata are kept under
        // the service, as a listing counts them.
        JsonElement found = (await SendAsync(HttpMethod.Get,
            $"/sharing/rest/search?f=json&num=100&q={Uri.EscapeDataString("type:\"Image Service\"")}")).Json;
        string? image = found.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).FirstOrDefault();
        Assert.False(image is null, "The fixture lists no image service to comment on.");

        var (_, made, madeRaw, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/items/{image}/addComment", Form(("comment", "zz on an image")));
        string comment = made.TryGetProperty("commentId", out JsonElement id) ? id.GetString()! : throw new InvalidOperationException(madeRaw);

        try
        {
            Assert.Equal(1, (await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{image}/comments?f=json")).Json
                .GetProperty("comments").EnumerateArray().Count(c => c.GetProperty("id").GetString() == comment));
            JsonElement listed = (await SendAsync(HttpMethod.Get,
                $"/sharing/rest/search?f=json&num=100&q={Uri.EscapeDataString($"id:{image}")}")).Json;
            Assert.True(listed.GetProperty("results")[0].GetProperty("numComments").GetInt32() >= 1, "The search does not count the image's comment.");
        }
        finally
        {
            await SendAsync(HttpMethod.Post, $"/sharing/rest/content/items/{image}/comments/{comment}/delete", Form());
        }
    }

    [Fact]
    public async Task An_organization_that_turns_comments_off_takes_none_and_says_so()
    {
        string map = await AddMapAsync($"zz quiet {Guid.NewGuid():N}"[..22]);

        try
        {
            Assert.True((await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json")).Json.GetProperty("commentsEnabled").GetBoolean());

            var (offStatus, _, offRaw, _) = await SendAsync(HttpMethod.Put, "/admin/settings/item-comments",
                new StringContent("""{"enabled":false}""", Encoding.UTF8, "application/json"));
            Assert.True(offStatus == HttpStatusCode.OK, offRaw);

            Assert.False((await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json")).Json.GetProperty("commentsEnabled").GetBoolean());
            Assert.True((await SendAsync(HttpMethod.Post, $"/sharing/rest/content/items/{map}/addComment", Form(("comment", "quiet")))).Json
                .TryGetProperty("error", out _));
        }
        finally
        {
            await SendAsync(HttpMethod.Put, "/admin/settings/item-comments",
                new StringContent("""{"enabled":true}""", Encoding.UTF8, "application/json"));
            await DeleteAsync(map);
        }
    }
}
