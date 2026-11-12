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
/// ADR-262, from the ArcGIS veteran's review of v1.0.499 (#3): the portal answers for its users as the ArcGIS API for
/// Python's <c>gis.users</c> asks — search, list, invite, role, disable, enable, delete — through Studio's own acts. Each
/// was a 404.
/// </summary>
public sealed class PortalUsersTests : ArcGisClient
{
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IDictionary<string, string>? form = null, bool signedIn = true)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, $"{root}{path}");

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        if (signedIn)
        {
            string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static bool Refused(JsonElement answer) => answer.TryGetProperty("error", out _);

    private async Task<JsonElement?> FindAsync(string name)
    {
        JsonElement found = await SendAsync(HttpMethod.Get, $"/sharing/rest/portals/self/users/search?q={Uri.EscapeDataString(name)}&num=100&f=json");
        return found.GetProperty("users").EnumerateArray().Where(u => u.GetProperty("username").GetString() == name)
            .Select(u => (JsonElement?)u).FirstOrDefault();
    }

    [Fact]
    public async Task Nobody_signed_out_finds_a_user_and_an_administrator_sees_them_in_full()
    {
        JsonElement anonymous = await SendAsync(HttpMethod.Get, "/sharing/rest/community/users?q=ci&f=json", signedIn: false);
        Assert.Equal(0, anonymous.GetProperty("total").GetInt32());
        Assert.Empty(anonymous.GetProperty("results").EnumerateArray());

        Assert.True(Refused(await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self/users?f=json", signedIn: false)));

        JsonElement listed = await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self/users?num=100&f=json");
        Assert.True(listed.GetProperty("total").GetInt32() >= 1, listed.GetRawText());
        JsonElement first = listed.GetProperty("users")[0];
        Assert.True(first.TryGetProperty("role", out _));
        Assert.True(first.TryGetProperty("disabled", out _));
        Assert.StartsWith("org_", first.GetProperty("role").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_is_invited_given_a_role_disabled_enabled_and_deleted_through_the_portal()
    {
        string name = $"zz_inv_{Guid.NewGuid():N}"[..16];
        string list = JsonSerializer.Serialize(new
        {
            invitations = new object[]
            {
                new { username = name, password = "a long enough password", fullname = "Invited By Test", role = "org_user", userLicenseType = "creatorUT" },
                new { username = name + "x", fullname = "No Password", role = "org_user" },
            },
        });

        try
        {
            JsonElement invited = await SendAsync(HttpMethod.Post, "/sharing/rest/portals/self/invite",
                new Dictionary<string, string> { ["invitationList"] = list, ["f"] = "json" });
            Assert.True(invited.GetProperty("success").GetBoolean(), invited.GetRawText());
            Assert.Equal([name + "x"], invited.GetProperty("notInvited").EnumerateArray().Select(n => n.GetString()));

            JsonElement made = await FindAsync(name) ?? throw new InvalidOperationException($"'{name}' was not made.");
            Assert.Equal("org_user", made.GetProperty("role").GetString());
            Assert.Equal("Invited By Test", made.GetProperty("fullName").GetString());
            Assert.Null(await FindAsync(name + "x"));

            JsonElement role = await SendAsync(HttpMethod.Post, "/sharing/rest/portals/self/updateuserrole",
                new Dictionary<string, string> { ["user"] = name, ["role"] = "org_publisher", ["f"] = "json" });
            Assert.True(role.GetProperty("success").GetBoolean(), role.GetRawText());
            Assert.Equal("org_publisher", (await FindAsync(name))!.Value.GetProperty("role").GetString());

            Assert.False(Refused(await SendAsync(HttpMethod.Post, $"/sharing/rest/community/users/{name}/disable",
                new Dictionary<string, string> { ["f"] = "json" })));
            Assert.True((await FindAsync(name))!.Value.GetProperty("disabled").GetBoolean());

            Assert.False(Refused(await SendAsync(HttpMethod.Post, $"/sharing/rest/community/users/{name}/enable",
                new Dictionary<string, string> { ["f"] = "json" })));
            Assert.False((await FindAsync(name))!.Value.GetProperty("disabled").GetBoolean());

            // Nobody signed in changes anybody.
            Assert.True(Refused(await SendAsync(HttpMethod.Post, $"/sharing/rest/community/users/{name}/disable",
                new Dictionary<string, string> { ["f"] = "json" }, signedIn: false)));

            JsonElement deleted = await SendAsync(HttpMethod.Post, $"/sharing/rest/community/users/{name}/delete",
                new Dictionary<string, string> { ["f"] = "json" });
            Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.GetRawText());
            Assert.Null(await FindAsync(name));
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/members/{name}");
        }
    }

    /// <summary>
    /// ADR-284: ArcGIS's Mobile Worker and Contributor ids invite this server's two types, and the profile reports them
    /// back as ArcGIS does — <c>fieldworker</c> as <c>fieldWorkerUT</c>, the stored <c>editor</c> as <c>editorUT</c>.
    /// </summary>
    [Fact]
    public async Task A_mobile_worker_and_a_contributor_are_invited_and_reported_by_their_ArcGIS_ids()
    {
        string worker = $"zz_mw_{Guid.NewGuid():N}"[..16];
        string contributor = $"zz_ct_{Guid.NewGuid():N}"[..16];
        string list = JsonSerializer.Serialize(new
        {
            invitations = new object[]
            {
                new { username = worker, password = "a long enough password", role = "org_user", userLicenseType = "fieldWorkerUT" },
                new { username = contributor, password = "a long enough password", role = "org_user", userLicenseType = "editorUT" },
            },
        });

        try
        {
            JsonElement invited = await SendAsync(HttpMethod.Post, "/sharing/rest/portals/self/invite",
                new Dictionary<string, string> { ["invitationList"] = list, ["f"] = "json" });
            Assert.True(invited.GetProperty("success").GetBoolean(), invited.GetRawText());

            JsonElement w = await FindAsync(worker) ?? throw new InvalidOperationException($"'{worker}' was not made.");
            JsonElement c = await FindAsync(contributor) ?? throw new InvalidOperationException($"'{contributor}' was not made.");
            Assert.Equal("fieldWorkerUT", w.GetProperty("userLicenseTypeId").GetString());
            Assert.Equal("editorUT", c.GetProperty("userLicenseTypeId").GetString());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/members/{worker}");
            await SendAsync(HttpMethod.Delete, $"/admin/members/{contributor}");
        }
    }

    /// <summary>
    /// ADR-269 condition 2: ArcGIS Pro's Share Package reads <c>storageQuota</c> and <c>storageUsage</c> from the member's
    /// own profile before it uploads, and with neither present refused a 4.6 kB scene layer package with
    /// <i>ERROR 001147: There is not enough space in your online account</i> (2026-11-09). Every door to the profile —
    /// <c>community/self</c>, <c>community/users/{me}</c>, <c>portals/self</c>'s user and an administrator's member list —
    /// tells the same two numbers, and the quota leaves room.
    /// </summary>
    [Fact]
    public async Task A_members_profile_tells_their_storage_and_quota_through_every_door()
    {
        JsonElement self = await SendAsync(HttpMethod.Get, "/sharing/rest/community/self?f=json");
        string me = self.GetProperty("username").GetString()!;
        JsonElement user = await SendAsync(HttpMethod.Get, $"/sharing/rest/community/users/{me}?f=json");
        JsonElement portal = (await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json")).GetProperty("user");
        JsonElement listed = await FindAsync(me) ?? throw new InvalidOperationException($"'{me}' is not in the member list.");

        long quota = self.GetProperty("storageQuota").GetInt64();
        long usage = self.GetProperty("storageUsage").GetInt64();
        Assert.Equal(2L * 1024 * 1024 * 1024 * 1024, quota);
        Assert.InRange(usage, 0, quota - 1);

        foreach (JsonElement door in new[] { user, portal, listed })
        {
            Assert.Equal(quota, door.GetProperty("storageQuota").GetInt64());
            Assert.Equal(usage, door.GetProperty("storageUsage").GetInt64());
        }
    }
}
