using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Members are searched in the database, sorted into categories and have profiles (ADR-190); a designed layer's
/// field takes a shared domain as it is made (ADR-189).
/// </summary>
/// <remarks>
/// Written 2026-10-17 with the change. <b>In the catalogue walk</b> because one test publishes a layer and the walk
/// reads every one.
/// </remarks>
[Collection("catalogue walk")]
public sealed class MembersProfilesAndCategoriesTests : ArcGisClient
{
    private static string Me => Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "root";

    [Fact]
    public async Task A_member_search_pages_in_the_database_and_says_when_each_last_signed_in()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;

        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/members?num=1&q={Uri.EscapeDataString(Me)}&sort=lastSignIn&order=desc", token, null);

        Assert.True(status == HttpStatusCode.OK, $"The search answered {(int)status}: {body}");
        JsonElement answer = JsonDocument.Parse(body).RootElement;
        Assert.True(answer.GetProperty("total").GetInt32() >= 1, body);

        JsonElement[] members = [.. answer.GetProperty("members").EnumerateArray()];
        Assert.Single(members);

        // The caller signed in to ask, so their last sign-in is known.
        JsonElement mine = members[0];
        Assert.Equal(JsonValueKind.String, mine.GetProperty("lastSignIn").ValueKind);

        // A search that matches nobody is an empty page with a total of nought, not a refusal.
        (status, body) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/members?num=25&q=zz_nobody_{Guid.NewGuid():N}", token, null);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, JsonDocument.Parse(body).RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_member_in_a_child_category_is_found_by_its_parent_and_leaves_with_it()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string tag = Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/member-categories", token, JsonSerializer.Serialize(new { name = $"zz parent {tag}" }));
        Assert.True(status == HttpStatusCode.Created, $"Making a category answered {(int)status}: {body}");
        string parent = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            (status, body) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/member-categories", token,
                JsonSerializer.Serialize(new { name = "child", parentId = parent }));
            Assert.Equal(HttpStatusCode.Created, status);
            string child = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

            // A second category of the same name beside it is refused, whatever its case.
            (status, _) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/member-categories", token,
                JsonSerializer.Serialize(new { name = "CHILD", parentId = parent }));
            Assert.Equal(HttpStatusCode.Conflict, status);

            (status, body) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/members/categorize", token,
                JsonSerializer.Serialize(new { members = new[] { Me }, add = new[] { child } }));
            Assert.True(status == HttpStatusCode.OK, $"Categorizing answered {(int)status}: {body}");

            (status, body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/members?num=50&category={parent}", token, null);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains(JsonDocument.Parse(body).RootElement.GetProperty("members").EnumerateArray(),
                m => m.GetProperty("name").GetString() == Me);
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/member-categories/{parent}", token, null);
        }

        // Deleting the parent took the child, and with it the member's place in it.
        (status, _) = await RequestAsync(HttpMethod.Get, $"{root}/admin/members?num=50&category={parent}", token, null);
        Assert.Equal(HttpStatusCode.OK, status);
        (_, body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/member-categories", token, null);
        Assert.DoesNotContain(tag, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_is_read_by_whom_its_visibility_says_and_changed_only_by_its_member_or_an_administrator()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string path = $"/rest/members/{Uri.EscapeDataString(Me)}/profile";

        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, root + path, token, null);
        Assert.True(status == HttpStatusCode.OK, $"Reading one's own profile answered {(int)status}: {body}");
        JsonElement before = JsonDocument.Parse(body).RootElement;
        string? bio = before.TryGetProperty("bio", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        string visibility = before.GetProperty("visibility").GetString()!;

        try
        {
            (status, body) = await RequestAsync(HttpMethod.Put, root + path, token,
                JsonSerializer.Serialize(new { bio = "A conformance run.", visibility = "public" }));
            Assert.True(status == HttpStatusCode.OK, $"Saving the profile answered {(int)status}: {body}");

            (status, body) = await AnonymousAsync(path);
            Assert.True(status == HttpStatusCode.OK, $"A public profile answered a stranger {(int)status}: {body}");
            Assert.Equal("A conformance run.", JsonDocument.Parse(body).RootElement.GetProperty("bio").GetString());

            await RequestAsync(HttpMethod.Put, root + path, token,
                JsonSerializer.Serialize(new { bio = "A conformance run.", visibility = "organization" }));
            (status, _) = await AnonymousAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, status);

            using HttpRequestMessage edit = new(HttpMethod.Put, root + path)
            {
                Content = new StringContent("{\"bio\":\"not mine\"}", System.Text.Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage refused = await Http.SendAsync(edit);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            (status, _) = await RequestAsync(HttpMethod.Put, root + path, token,
                JsonSerializer.Serialize(new { visibility = "everyone" }));
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }
        finally
        {
            await RequestAsync(HttpMethod.Put, root + path, token, JsonSerializer.Serialize(new
            {
                displayName = before.GetProperty("displayName").ValueKind == JsonValueKind.String
                    ? before.GetProperty("displayName").GetString() : null,
                email = before.GetProperty("email").ValueKind == JsonValueKind.String ? before.GetProperty("email").GetString() : null,
                bio,
                visibility,
            }));
        }
    }

    [Fact]
    public async Task A_photo_is_kept_only_when_its_bytes_are_the_image_it_claims()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string path = $"{root}/rest/members/{Uri.EscapeDataString(Me)}/photo";

        // The smallest PNG: its signature and nothing a browser would draw — the type check reads the signature.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

        Assert.Equal(HttpStatusCode.BadRequest, await PutPhotoAsync(path, token, "image/png", [0xFF, 0xD8, 0xFF, 0]));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await PutPhotoAsync(path, token, "image/svg+xml", png));

        try
        {
            Assert.Equal(HttpStatusCode.NoContent, await PutPhotoAsync(path, token, "image/png", png));

            using HttpRequestMessage read = new(HttpMethod.Get, path);
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage got = await Http.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
            Assert.Equal("image/png", got.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png, await got.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, path, token, null);
        }
    }

    [Fact]
    public async Task A_designed_field_takes_a_shared_domain_as_the_layer_is_made_and_a_misfit_is_refused_first()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string tag = Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/domains", token,
            JsonSerializer.Serialize(new
            {
                domain = new
                {
                    type = "codedValue",
                    name = $"zz_state_{tag}",
                    codedValues = new[] { new { code = "open", name = "Open" }, new { code = "closed", name = "Closed" } },
                },
            }));
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, $"Making a domain answered {(int)status}: {body}");
        string domain = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        string layer = "zz_designed_domain_" + tag;
        string? feature = null;

        try
        {
            // A list of text cannot hold an integer column: refused, and no layer is made.
            (status, body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/hosted/define", token,
                JsonSerializer.Serialize(new
                {
                    name = layer,
                    geometryType = "Point",
                    fields = new object[] { new { name = "count", type = "Integer", nullable = true, domain } },
                }));
            Assert.True(status == HttpStatusCode.BadRequest, $"A misfit domain answered {(int)status}: {body}");

            (status, body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/hosted/define", token,
                JsonSerializer.Serialize(new
                {
                    name = layer,
                    geometryType = "Point",
                    fields = new object[] { new { name = "State", type = "Text", nullable = true, domain } },
                }));
            Assert.True(status == HttpStatusCode.Created, $"Defining with a domain answered {(int)status}: {body}");
            feature = JsonDocument.Parse(body).RootElement.GetProperty("services").GetProperty("feature").GetString()!;

            JsonElement document = await GetJsonAsync(feature);
            JsonElement state = document.GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "state");
            Assert.Equal($"zz_state_{tag}", state.GetProperty("domain").GetProperty("name").GetString());
        }
        finally
        {
            if (feature is not null)
            {
                string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
                await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token, null);
            }

            await RequestAsync(HttpMethod.Delete, $"{root}/admin/domains/{domain}", token, null);
        }
    }

    private async Task<HttpStatusCode> PutPhotoAsync(string url, string token, string type, byte[] bytes)
    {
        using HttpRequestMessage request = new(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return response.StatusCode;
    }
}
