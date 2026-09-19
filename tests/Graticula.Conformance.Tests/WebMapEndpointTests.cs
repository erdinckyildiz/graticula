using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Saved web maps over HTTP — ADR-079 §5.3: the Studio API, the sharing rule, and the portal items an
/// ArcGIS client opens a map through.
/// </summary>
/// <remarks>
/// <para>
/// <b>The round trip is asserted through the portal, not only through the API that saved it.</b>
/// <c>items/{id}/data</c> is what ArcGIS Pro and the Maps SDK read to open a map, so a document that
/// came back unchanged from <c>/content/webmaps</c> and changed from <c>/sharing/rest</c> would be the
/// failure that matters, reported by the test that did not look.
/// </para>
/// <para>
/// <b>A second member, because the administrator reads everything.</b> <c>admin:viewAllContent</c>
/// makes every private map readable to the account this suite signs in as, so the 404 a stranger gets
/// can only be seen from somebody who is not one — the same reason
/// <see cref="MyContentIsWhatTheCallerOwnsTests"/> makes one.
/// </para>
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class WebMapEndpointTests : ArcGisClient
{
    /// <summary>The member this class makes and removes; a leftover is obviously this test's.</summary>
    private const string Member = "adr079_map_reader";

    /// <summary>A document carrying fields no Graticula reader knows, which must survive.</summary>
    private const string Document = """
        {
          "operationalLayers": [
            {
              "id": "a",
              "layerType": "ArcGISFeatureLayer",
              "url": "https://example.test/rest/services/hosted/x/FeatureServer/0",
              "title": "X",
              "visibility": true,
              "opacity": 0.5,
              "layerDefinition": { "definitionExpression": "kind = 'b'" },
              "popupInfo": { "title": "{name}" }
            }
          ],
          "baseMap": { "baseMapLayers": [], "title": "None" },
          "somethingProWrote": { "nested": [1, 2.5, "three", null, true] },
          "version": "2.31"
        }
        """;

    [Fact]
    public async Task A_map_is_created_read_changed_and_deleted_and_a_portal_client_reads_it_unchanged()
    {
        string root = await RequireServerAsync();
        string token = await AdministratorTokenAsync(root);

        string id = await CreateAsync(root, token, "ADR-079 round trip", "organization");

        try
        {
            // ---- the API that saved it ----
            JsonElement read = await JsonAsync(HttpMethod.Get, $"{root}/content/webmaps/{id}", token);

            Assert.Equal("ADR-079 round trip", read.GetProperty("title").GetString());
            Assert.Equal("organization", read.GetProperty("sharing").GetString());
            Assert.True(read.GetProperty("mine").GetBoolean());
            Assert.True(
                JsonElement.DeepEquals(Parsed(Document), read.GetProperty("document")),
                "The document read back from /content/webmaps is not the one saved:\n"
                + read.GetProperty("document").GetRawText());

            // ---- the listing ----
            JsonElement listed = await JsonAsync(HttpMethod.Get, $"{root}/content/webmaps", token);

            Assert.Contains(
                listed.GetProperty("webMaps").EnumerateArray(),
                m => m.GetProperty("id").GetString() == id && !m.TryGetProperty("document", out _));

            // ---- the portal: search, the owner's content, the item, its data ----
            JsonElement search = await JsonAsync(
                HttpMethod.Get, $"{root}/sharing/rest/search?q={Uri.EscapeDataString("type:\"Web Map\"")}&f=json", token);

            JsonElement item = search.GetProperty("results").EnumerateArray()
                .Single(i => i.GetProperty("id").GetString() == id);

            Assert.Equal("Web Map", item.GetProperty("type").GetString());
            Assert.Equal("org", item.GetProperty("access").GetString());
            Assert.Contains("Web Map", item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));
            Assert.Contains("Explorer Web Map", item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));

            string owner = item.GetProperty("owner").GetString()!;

            JsonElement content = await JsonAsync(
                HttpMethod.Get, $"{root}/sharing/rest/content/users/{Uri.EscapeDataString(owner)}?f=json", token);

            Assert.Contains(content.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

            JsonElement single = await JsonAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{id}?f=json", token);

            Assert.Equal("Web Map", single.GetProperty("type").GetString());
            Assert.Equal("ADR-079 round trip", single.GetProperty("title").GetString());

            JsonElement data = await JsonAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{id}/data?f=json", token);

            Assert.True(
                JsonElement.DeepEquals(Parsed(Document), data),
                "items/{id}/data is not the document that was saved — which is what an ArcGIS client opens "
                + $"the map from. It answered:\n{data.GetRawText()}");

            // ---- a change ----
            JsonElement changed = await JsonAsync(
                HttpMethod.Put,
                $"{root}/content/webmaps/{id}",
                token,
                Body("ADR-079 renamed", "private", """{"operationalLayers":[],"kept":{"x":1}}"""));

            Assert.Equal("ADR-079 renamed", changed.GetProperty("title").GetString());
            Assert.Equal("private", changed.GetProperty("sharing").GetString());
            Assert.Equal(1, changed.GetProperty("document").GetProperty("kept").GetProperty("x").GetInt32());
        }
        finally
        {
            (HttpStatusCode deleted, string said) = await RequestAsync(HttpMethod.Delete, $"{root}/content/webmaps/{id}", token, null);
            Assert.True(deleted == HttpStatusCode.OK, $"Deleting the map answered {(int)deleted}: {said}");
        }

        (HttpStatusCode gone, _) = await RequestAsync(HttpMethod.Get, $"{root}/content/webmaps/{id}", token, null);
        Assert.Equal(HttpStatusCode.NotFound, gone);

        (_, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}/sharing/rest/content/items/{id}/data?f=json", token, null);
        Assert.Contains("does not exist or is inaccessible", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_that_is_not_an_object_or_is_over_a_megabyte_is_refused()
    {
        string root = await RequireServerAsync();
        string token = await AdministratorTokenAsync(root);

        (HttpStatusCode array, string arraySaid) = await RequestAsync(
            HttpMethod.Post, $"{root}/content/webmaps", token, Body("Bad", "private", "[1, 2]"));

        Assert.True(array == HttpStatusCode.BadRequest, $"An array document answered {(int)array}: {arraySaid}");

        string large = "{\"padding\":\"" + new string('x', (1024 * 1024) + 10) + "\"}";

        (HttpStatusCode big, string bigSaid) = await RequestAsync(
            HttpMethod.Post, $"{root}/content/webmaps", token, Body("Big", "private", large));

        Assert.True(big == HttpStatusCode.RequestEntityTooLarge, $"A 1 MB+ document answered {(int)big}: {bigSaid}");

        (HttpStatusCode group, _) = await RequestAsync(
            HttpMethod.Post, $"{root}/content/webmaps", token, Body("Grouped", "group", "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, group);
    }

    [Fact]
    public async Task A_map_somebody_may_not_read_is_a_404_everywhere_and_a_readable_one_is_not_theirs_to_change()
    {
        string root = await RequireServerAsync();
        string token = await AdministratorTokenAsync(root);

        string hidden = await CreateAsync(root, token, "ADR-079 private", "private");
        string shared = await CreateAsync(root, token, "ADR-079 organization", "organization");
        string open = await CreateAsync(root, token, "ADR-079 public", "public");

        string? reader = null;

        try
        {
            reader = await SecondMemberAsync(root, token);

            // ---- a private map, to somebody else ----
            (HttpStatusCode status, _) = await RequestAsync(HttpMethod.Get, $"{root}/content/webmaps/{hidden}", reader, null);
            Assert.Equal(HttpStatusCode.NotFound, status);

            (_, string item) = await RequestAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{hidden}?f=json", reader, null);
            Assert.Contains("does not exist or is inaccessible", item, StringComparison.Ordinal);

            (_, string data) = await RequestAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{hidden}/data?f=json", reader, null);
            Assert.Contains("does not exist or is inaccessible", data, StringComparison.Ordinal);

            (_, string found) = await RequestAsync(HttpMethod.Get, $"{root}/sharing/rest/search?q=ADR-079&f=json", reader, null);
            Assert.DoesNotContain(hidden, found, StringComparison.Ordinal);
            Assert.Contains(shared, found, StringComparison.Ordinal);

            (HttpStatusCode change, _) = await RequestAsync(
                HttpMethod.Put, $"{root}/content/webmaps/{hidden}", reader, Body("Mine now", "private", "{}"));
            Assert.Equal(HttpStatusCode.NotFound, change);

            // ---- an organization map: readable, and not theirs to change or delete ----
            (HttpStatusCode readShared, _) = await RequestAsync(HttpMethod.Get, $"{root}/content/webmaps/{shared}", reader, null);
            Assert.Equal(HttpStatusCode.OK, readShared);

            (HttpStatusCode overwrite, string why) = await RequestAsync(
                HttpMethod.Put, $"{root}/content/webmaps/{shared}", reader, Body("Mine now", "organization", "{}"));
            Assert.True(overwrite == HttpStatusCode.Forbidden, $"A non-owner's update answered {(int)overwrite}: {why}");

            (HttpStatusCode remove, _) = await RequestAsync(HttpMethod.Delete, $"{root}/content/webmaps/{shared}", reader, null);
            Assert.Equal(HttpStatusCode.Forbidden, remove);

            // ---- anonymous: the public map only, and no owner's name with it ----
            (HttpStatusCode anonymousPrivate, _) = await StrangerAsync($"{root}/content/webmaps/{shared}");
            Assert.Equal(HttpStatusCode.NotFound, anonymousPrivate);

            (HttpStatusCode anonymousPublic, string publicBody) = await StrangerAsync($"{root}/content/webmaps/{open}");
            Assert.Equal(HttpStatusCode.OK, anonymousPublic);
            Assert.Equal(JsonValueKind.Null, Parsed(publicBody).GetProperty("owner").ValueKind);

            (HttpStatusCode anonymousData, string anonymousDocument) = await StrangerAsync($"{root}/sharing/rest/content/items/{open}/data?f=json");
            Assert.Equal(HttpStatusCode.OK, anonymousData);
            Assert.Equal("None", Parsed(anonymousDocument).GetProperty("baseMap").GetProperty("title").GetString());

            (HttpStatusCode anonymousList, _) = await StrangerAsync($"{root}/content/webmaps");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousList);
        }
        finally
        {
            foreach (string id in new[] { hidden, shared, open })
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/webmaps/{id}", token, null);
            }

            await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Member}?deleteOwned=true", token, null);
        }
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>A client that holds no cookie, for the anonymous half.</summary>
    /// <remarks>
    /// <b>Not <see cref="ArcGisClient.AnonymousAsync"/>, whose client keeps cookies</b>: the sign-ins
    /// this class makes set <c>gis-session</c> on it, and the "anonymous" request then arrived as the
    /// member who had just signed in — which read an organisation map and passed for the wrong reason
    /// until the assertion was pointed at a scope the member could read.
    /// </remarks>
    private static readonly HttpClient Stranger = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseCookies = false,
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<(HttpStatusCode Status, string Body)> StrangerAsync(string url)
    {
        using HttpResponseMessage response = await Stranger.GetAsync(new Uri(url));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> AdministratorTokenAsync(string root)
    {
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set GRATICULA_TEST_USER and GRATICULA_TEST_PASSWORD.");
        return token!;
    }

    private async Task<string> CreateAsync(string root, string token, string title, string sharing)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post, $"{root}/content/webmaps", token, Body(title, sharing, Document));

        Assert.True(status == HttpStatusCode.Created, $"Creating '{title}' answered {(int)status}: {body}");

        string id = Parsed(body).GetProperty("id").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", id);

        return id;
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string url, string token, string? json = null)
    {
        (HttpStatusCode status, string body) = await RequestAsync(method, url, token, json);

        Assert.True(status == HttpStatusCode.OK, $"{method} {url} answered {(int)status}: {body}");
        Assert.False(body.Contains("\"error\":", StringComparison.Ordinal) && !url.Contains("/data", StringComparison.Ordinal),
            $"{method} {url} answered an error envelope: {body}");

        return Parsed(body);
    }

    private static string Body(string title, string sharing, string document) =>
        $$"""{"title":{{JsonSerializer.Serialize(title)}},"snippet":"ADR-079 test","sharing":"{{sharing}}","document":{{document}}}""";

    private static JsonElement Parsed(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A publisher who is not the administrator, signed in, with their own password.</summary>
    private async Task<string> SecondMemberAsync(string root, string administrator)
    {
        string create = JsonSerializer.Serialize(new { name = Member, displayName = "ADR-079", role = "publisher", userType = "creator" });

        (HttpStatusCode made, string said) = await RequestAsync(HttpMethod.Post, $"{root}/admin/members", administrator, create);

        if (made == HttpStatusCode.Conflict)
        {
            // A leftover from an interrupted run: this name is this test's own.
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Member}?deleteOwned=true", administrator, null);
            (made, said) = await RequestAsync(HttpMethod.Post, $"{root}/admin/members", administrator, create);
        }

        Assert.True(made == HttpStatusCode.Created, $"Creating the second member answered {(int)made}: {said}");

        string issued = Parsed(said).GetProperty("password").GetString()!;
        string first = await LoginAsync(root, issued);

        (HttpStatusCode changed, string why) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/rest/auth/password",
            first,
            JsonSerializer.Serialize(new { currentPassword = issued, newPassword = issued + "X1" }));

        Assert.True(changed == HttpStatusCode.OK, $"The second member could not set a password: {(int)changed} {why}");

        return await LoginAsync(root, issued + "X1");
    }

    private async Task<string> LoginAsync(string root, string password)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/rest/auth/login",
            token: string.Empty,
            JsonSerializer.Serialize(new { name = Member, password }));

        Assert.True(status == HttpStatusCode.OK, $"Signing in as {Member} answered {(int)status}: {body}");

        return Parsed(body).GetProperty("token").GetString()!;
    }
}
