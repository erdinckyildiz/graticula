using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A web map shared with a group opens for its members and for nobody else — ADR-079 condition 4.
/// </summary>
/// <remarks>
/// <b>Read as the member and as a stranger, not as the administrator</b>, who may read everything and so proves
/// nothing about who else may. The test makes its own group, map and two members, and removes them.
/// </remarks>
[Collection("catalogue walk")]
public sealed class WebMapGroupSharingTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string? token, HttpMethod method, string path, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}")
        {
            Content = json is null ? null : new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Makes a member and signs them in, replacing the password they must replace.</summary>
    private async Task<string> MemberTokenAsync(string root, string admin, string name)
    {
        (HttpStatusCode made, string created) = await SendAsync(root, admin, HttpMethod.Post, "/admin/members",
            new { name, role = "user", userType = "creator" });
        Assert.True(made is HttpStatusCode.OK or HttpStatusCode.Created, $"Making '{name}' answered {(int)made}: {created}");

        string issued = JsonDocument.Parse(created).RootElement.GetProperty("password").GetString()!;
        string first = await LoginAsync(root, name, issued);

        (HttpStatusCode changed, string said) = await SendAsync(root, first, HttpMethod.Post, "/rest/auth/password",
            new { currentPassword = issued, newPassword = issued + "X1" });
        Assert.True(changed == HttpStatusCode.OK, $"'{name}' could not replace the issued password: {(int)changed} {said}");

        return await LoginAsync(root, name, issued + "X1");
    }

    private async Task<string> LoginAsync(string root, string name, string password)
    {
        (HttpStatusCode status, string body) = await SendAsync(root, null, HttpMethod.Post, "/rest/auth/login", new { name, password });
        Assert.True(status == HttpStatusCode.OK, $"Signing in as '{name}' answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task A_group_shared_map_opens_for_its_members_and_not_for_a_stranger()
    {
        string root = await RequireServerAsync();
        string? admin = await TokenAsync(root);

        Assert.False(admin is null, "No administrator credential; set the suite's user and password.");

        string tag = Guid.NewGuid().ToString("N")[..8];
        string group = $"zz_mapgroup_{tag}";
        string member = $"zz_in_{tag}";
        string stranger = $"zz_out_{tag}";
        string? map = null;

        try
        {
            string memberToken = await MemberTokenAsync(root, admin!, member);
            string strangerToken = await MemberTokenAsync(root, admin!, stranger);

            (HttpStatusCode grouped, string groupBody) = await SendAsync(root, admin, HttpMethod.Post, "/admin/groups", new { name = group });
            Assert.True(grouped is HttpStatusCode.OK or HttpStatusCode.Created, $"Making the group answered {(int)grouped}: {groupBody}");

            (HttpStatusCode joined, string joinBody) = await SendAsync(root, admin, HttpMethod.Put,
                $"/admin/groups/{group}/members/{member}", new { manager = false });
            Assert.True(joined is HttpStatusCode.OK or HttpStatusCode.Created, $"Adding the member answered {(int)joined}: {joinBody}");

            (HttpStatusCode created, string createdBody) = await SendAsync(root, admin, HttpMethod.Post, "/content/webmaps", new
            {
                title = $"zz group map {tag}", sharing = "group",
                document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
            });
            Assert.True(created == HttpStatusCode.Created, $"A group-shared map could not be saved: {(int)created} {createdBody}");
            map = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString();

            (HttpStatusCode shared, string shareBody) = await SendAsync(root, admin, HttpMethod.Put, $"/admin/groups/{group}/maps/{map}");
            Assert.True(shared == HttpStatusCode.OK, $"Sharing the map into the group answered {(int)shared}: {shareBody}");

            // The member opens it; the stranger is told it does not exist.
            (HttpStatusCode inside, _) = await SendAsync(root, memberToken, HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Equal(HttpStatusCode.OK, inside);

            (HttpStatusCode outside, _) = await SendAsync(root, strangerToken, HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Equal(HttpStatusCode.NotFound, outside);

            // The owner sees which groups; the group lists the map.
            (_, string owned) = await SendAsync(root, admin, HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Contains(group, owned, StringComparison.Ordinal);

            (_, string detail) = await SendAsync(root, admin, HttpMethod.Get, $"/admin/groups/{group}");
            Assert.Contains($"\"mapId\":\"{map}\"", detail.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);

            // Taken out again, the member loses it.
            (HttpStatusCode unshared, _) = await SendAsync(root, admin, HttpMethod.Delete, $"/admin/groups/{group}/maps/{map}");
            Assert.Equal(HttpStatusCode.OK, unshared);

            (HttpStatusCode after, _) = await SendAsync(root, memberToken, HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.Equal(HttpStatusCode.NotFound, after);
        }
        finally
        {
            if (map is not null) await SendAsync(root, admin, HttpMethod.Delete, $"/content/webmaps/{map}");
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/groups/{group}");
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/members/{member}");
            await SendAsync(root, admin, HttpMethod.Delete, $"/admin/members/{stranger}");
        }
    }

    /// <summary>A protected map is not deleted until its protection is turned off (2026-10-01).</summary>
    [Fact]
    public async Task A_protected_map_is_not_deleted_until_the_protection_is_off()
    {
        string root = await RequireServerAsync();
        string? admin = await TokenAsync(root);

        Assert.False(admin is null, "No administrator credential; set the suite's user and password.");

        (HttpStatusCode created, string body) = await SendAsync(root, admin, HttpMethod.Post, "/content/webmaps", new
        {
            title = "zz protected map", sharing = "private",
            document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
        });
        Assert.Equal(HttpStatusCode.Created, created);
        string map = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            (HttpStatusCode on, _) = await SendAsync(root, admin, HttpMethod.Put, $"/content/webmaps/{map}/protection", new { @protected = true });
            Assert.Equal(HttpStatusCode.OK, on);

            (_, string read) = await SendAsync(root, admin, HttpMethod.Get, $"/content/webmaps/{map}");
            Assert.True(JsonDocument.Parse(read).RootElement.GetProperty("deleteProtected").GetBoolean());

            (HttpStatusCode refused, _) = await SendAsync(root, admin, HttpMethod.Delete, $"/content/webmaps/{map}");
            Assert.Equal(HttpStatusCode.Conflict, refused);

            (HttpStatusCode off, _) = await SendAsync(root, admin, HttpMethod.Put, $"/content/webmaps/{map}/protection", new { @protected = false });
            Assert.Equal(HttpStatusCode.OK, off);

            (HttpStatusCode gone, _) = await SendAsync(root, admin, HttpMethod.Delete, $"/content/webmaps/{map}");
            Assert.Equal(HttpStatusCode.OK, gone);
            map = string.Empty;
        }
        finally
        {
            if (map.Length > 0)
            {
                await SendAsync(root, admin, HttpMethod.Put, $"/content/webmaps/{map}/protection", new { @protected = false });
                await SendAsync(root, admin, HttpMethod.Delete, $"/content/webmaps/{map}");
            }
        }
    }
}
