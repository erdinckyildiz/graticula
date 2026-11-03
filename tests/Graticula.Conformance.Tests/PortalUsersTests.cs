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
}
