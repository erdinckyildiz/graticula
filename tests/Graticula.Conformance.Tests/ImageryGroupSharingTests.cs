using System;
using System.IO;
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
/// ADR-124: an image service is shared to a group as a feature service is — its members read it, a stranger is told
/// it does not exist, and Portal's search lists it for the one and not the other.
/// </summary>
/// <remarks>
/// <b>Found by the ArcGIS reviewer's second imagery pass, 2026-10-01</b>: the group rows were written against the
/// service and never read for a coverage, so a group-shared image service answered nobody but its owner.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ImageryGroupSharingTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string? token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Whether a search answer's results — not its echo of the query — hold an item of this name.</summary>
    private static bool Lists(string answer, string name)
    {
        using JsonDocument said = JsonDocument.Parse(answer);
        return said.RootElement.TryGetProperty("results", out JsonElement results)
            && results.EnumerateArray().Any(r => r.TryGetProperty("name", out JsonElement n) && n.GetString() == name);
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private async Task<string> MemberTokenAsync(string root, string admin, string name)
    {
        (HttpStatusCode made, string created) = await SendAsync(root, admin, HttpMethod.Post, "/admin/members",
            Json(new { name, role = "user", userType = "creator" }));
        Assert.True(made is HttpStatusCode.OK or HttpStatusCode.Created, $"Making '{name}' answered {(int)made}: {created}");

        string issued = JsonDocument.Parse(created).RootElement.GetProperty("password").GetString()!;
        string first = await LoginAsync(root, name, issued);

        (HttpStatusCode changed, string said) = await SendAsync(root, first, HttpMethod.Post, "/rest/auth/password",
            Json(new { currentPassword = issued, newPassword = issued + "X1" }));
        Assert.True(changed == HttpStatusCode.OK, $"'{name}' could not replace the issued password: {(int)changed} {said}");

        return await LoginAsync(root, name, issued + "X1");
    }

    private async Task<string> LoginAsync(string root, string name, string password)
    {
        (HttpStatusCode status, string body) = await SendAsync(root, null, HttpMethod.Post, "/rest/auth/login", Json(new { name, password }));
        Assert.True(status == HttpStatusCode.OK, $"Signing in as '{name}' answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task A_group_shared_image_service_answers_its_members_and_not_a_stranger()
    {
        string root = await RequireServerAsync();
        string? admin = await TokenAsync(root);

        Assert.False(admin is null, "No administrator credential; set the suite's user and password.");

        string tag = Guid.NewGuid().ToString("N")[..8];
        string name = $"zz_imggrp_{tag}";
        string group = $"zz_imagegroup_{tag}";
        string member = $"zz_imgin_{tag}";
        string stranger = $"zz_imgout_{tag}";

        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        byte[] tiff = File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "rgb-byte-deflate.tif"));

        try
        {
            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(tiff), "file", "g.tif");
                form.Add(new StringContent(name), "name");
                (HttpStatusCode made, string madeBody) = await SendAsync(root, admin, HttpMethod.Post, "/admin/coverages/upload", form);
                Assert.True(made == HttpStatusCode.Created, $"Uploading answered {(int)made}: {madeBody}");
            }

            string memberToken = await MemberTokenAsync(root, admin!, member);
            string strangerToken = await MemberTokenAsync(root, admin!, stranger);

            (HttpStatusCode grouped, string groupBody) = await SendAsync(root, admin, HttpMethod.Post, "/admin/groups", Json(new { name = group }));
            Assert.True(grouped is HttpStatusCode.OK or HttpStatusCode.Created, $"Making the group answered {(int)grouped}: {groupBody}");
            (HttpStatusCode joined, string joinBody) = await SendAsync(root, admin, HttpMethod.Put,
                $"/admin/groups/{group}/members/{member}", Json(new { manager = false }));
            Assert.True(joined is HttpStatusCode.OK or HttpStatusCode.Created, $"Adding the member answered {(int)joined}: {joinBody}");

            (HttpStatusCode scoped, string scopeBody) = await SendAsync(root, admin, HttpMethod.Put,
                $"/admin/services/{name}/sharing?folder=hosted", Json(new { sharing = "group" }));
            Assert.True(scoped == HttpStatusCode.OK, $"Setting the scope to group answered {(int)scoped}: {scopeBody}");
            (HttpStatusCode shared, string shareBody) = await SendAsync(root, admin, HttpMethod.Put,
                $"/admin/groups/{group}/items/{name}?folder=hosted");
            Assert.True(shared == HttpStatusCode.OK, $"Sharing into the group answered {(int)shared}: {shareBody}");

            string document = $"/rest/services/hosted/{name}/ImageServer?f=json";
            (_, string inside) = await SendAsync(root, memberToken, HttpMethod.Get, document);
            Assert.DoesNotContain("\"error\"", inside, StringComparison.Ordinal);
            (_, string outside) = await SendAsync(root, strangerToken, HttpMethod.Get, document);
            Assert.Contains("\"error\"", outside, StringComparison.Ordinal);

            string search = $"/sharing/rest/search?q={Uri.EscapeDataString(name)}&f=json";
            (_, string found) = await SendAsync(root, memberToken, HttpMethod.Get, search);
            Assert.True(Lists(found, name), $"Portal search does not list the image service for a member of its group: {found}");
            (_, string hidden) = await SendAsync(root, strangerToken, HttpMethod.Get, search);
            Assert.False(Lists(hidden, name), $"Portal search lists the image service for a stranger: {hidden}");

            (HttpStatusCode unshared, _) = await SendAsync(root, admin, HttpMethod.Delete, $"/admin/groups/{group}/items/{name}?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, unshared);
            (_, string after) = await SendAsync(root, memberToken, HttpMethod.Get, document);
            Assert.Contains("\"error\"", after, StringComparison.Ordinal);
        }
        finally
        {
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/groups/{group}");
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/members/{member}");
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/members/{stranger}");
        }
    }
}
