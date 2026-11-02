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
/// Portal group writes go through Studio's privilege — ADR-256, owner decision 2026-11-02 (Q-224): a group is made,
/// changed, peopled, joined, left and deleted through <c>community/…</c>, each step is what Studio's own
/// <c>/admin/groups</c> then reads, and a member whose role does not hold Studio's privilege is refused at the portal
/// as Studio refuses them.
/// </summary>
public sealed class PortalGroupWritesTests : ArcGisClient
{
    private const string Member = "zz_pgw_member";
    private const string MemberPassword = "a-long-enough-test-password-256";
    private const string Viewer = "zz_pgw_viewer";
    private const string ViewerPassword = "a-long-enough-test-password-256v";

    [Fact]
    public async Task A_group_is_made_changed_peopled_and_deleted_through_the_portal_and_Studio_reads_each_step()
    {
        string root = await RequireServerAsync();
        string admin = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string title = $"ZZ Portal Writes {Guid.NewGuid():N}"[..30];
        string name = title.ToLowerInvariant().Replace(' ', '-');

        try
        {
            await MakeMemberAsync(Member, MemberPassword, "user", "creator");
            string viewer = await MakeMemberAsync(Viewer, ViewerPassword, "user", "viewer");

            // Neither join flag, as the ArcGIS API for Python sends by default: made, and invitation-only (ADR-256 §5.3).
            JsonElement made = await PortalAsync(root, admin, "community/createGroup", new()
            {
                ["title"] = title, ["access"] = "org", ["isInvitationOnly"] = "false", ["autoJoin"] = "false",
                ["tags"] = "not kept", ["snippet"] = "made by a portal client",
            });
            Assert.True(made.TryGetProperty("success", out JsonElement success) && success.GetBoolean(), made.GetRawText());
            string id = made.GetProperty("group").GetProperty("id").GetString()!;
            Assert.True(made.GetProperty("group").GetProperty("isInvitationOnly").GetBoolean(), made.GetRawText());

            JsonElement studio = await StudioAsync(name);
            Assert.Equal(title, studio.GetProperty("title").GetString());
            Assert.Equal("organization", studio.GetProperty("visibility").GetString());
            Assert.Equal("invitation", studio.GetProperty("joinPolicy").GetString());

            // update changes what is sent and nothing else.
            JsonElement updated = await PortalAsync(root, admin, $"community/groups/{id}/update", new()
            {
                ["access"] = "private", ["isViewOnly"] = "true", ["description"] = "changed by a portal client",
            });
            Assert.Equal(id, updated.GetProperty("groupId").GetString());
            studio = await StudioAsync(name);
            Assert.Equal("members", studio.GetProperty("visibility").GetString());
            Assert.Equal("managers", studio.GetProperty("contribute").GetString());
            Assert.Equal("changed by a portal client", studio.GetProperty("description").GetString());
            Assert.Equal("made by a portal client", studio.GetProperty("summary").GetString());
            Assert.Equal(title, studio.GetProperty("title").GetString());

            // addUsers answers the names it did not add, and Studio sees the one it did.
            JsonElement added = await PortalAsync(root, admin, $"community/groups/{id}/addUsers", new()
            {
                ["users"] = $"{Member},zz_pgw_nobody",
            });
            Assert.Equal(["zz_pgw_nobody"], added.GetProperty("notAdded").EnumerateArray().Select(n => n.GetString()));
            Assert.Contains(Member, MembersOf(await StudioAsync(name)));

            // A member whose role does not hold Studio's privilege is refused at the portal as Studio refuses them.
            JsonElement refusedCreate = await PortalAsync(root, viewer, "community/createGroup", new() { ["title"] = $"{title} v" });
            Assert.True(refusedCreate.TryGetProperty("error", out JsonElement createError), refusedCreate.GetRawText());
            Assert.Contains("groups:create", createError.GetProperty("message").GetString(), StringComparison.Ordinal);

            JsonElement refusedAdd = await PortalAsync(root, viewer, $"community/groups/{id}/addUsers", new() { ["users"] = Viewer });
            Assert.True(refusedAdd.TryGetProperty("error", out _), refusedAdd.GetRawText());
            Assert.DoesNotContain(Viewer, MembersOf(await StudioAsync(name)));

            // A member of the group who neither owns nor manages it is refused a change.
            string member = await SignInAsync(root, Member, MemberPassword);
            JsonElement refusedUpdate = await PortalAsync(root, member, $"community/groups/{id}/update", new() { ["title"] = "taken" });
            Assert.True(refusedUpdate.TryGetProperty("error", out _), refusedUpdate.GetRawText());
            Assert.Equal(title, (await StudioAsync(name)).GetProperty("title").GetString());

            JsonElement refusedDelete = await PortalAsync(root, member, $"community/groups/{id}/delete", []);
            Assert.True(refusedDelete.TryGetProperty("error", out _), refusedDelete.GetRawText());

            // removeUsers, then delete — both read back through Studio.
            JsonElement removed = await PortalAsync(root, admin, $"community/groups/{id}/removeUsers", new() { ["users"] = Member });
            Assert.Empty(removed.GetProperty("notRemoved").EnumerateArray());
            Assert.DoesNotContain(Member, MembersOf(await StudioAsync(name)));

            JsonElement deleted = await PortalAsync(root, admin, $"community/groups/{id}/delete", []);
            Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.GetRawText());
            (int gone, _) = await AdminAsync(HttpMethod.Get, $"/admin/groups/{name}");
            Assert.Equal(404, gone);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{name}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Viewer}?deleteOwned=true");
        }
    }

    [Fact]
    public async Task A_member_joins_a_group_that_lets_them_and_leaves_it_and_is_refused_one_that_does_not()
    {
        string root = await RequireServerAsync();
        string admin = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string run = Guid.NewGuid().ToString("N")[..8];
        string open = $"ZZ Open Group {run}";
        string closed = $"ZZ Closed Group {run}";
        const string Joiner = Member + "_j";

        try
        {
            string member = await MakeMemberAsync(Joiner, MemberPassword, "user", "creator");

            string openId = (await PortalAsync(root, admin, "community/createGroup", new()
            {
                ["title"] = open, ["access"] = "org", ["isInvitationOnly"] = "false", ["autoJoin"] = "true",
            })).GetProperty("group").GetProperty("id").GetString()!;
            string closedId = (await PortalAsync(root, admin, "community/createGroup", new()
            {
                ["title"] = closed, ["access"] = "org", ["isInvitationOnly"] = "true",
            })).GetProperty("group").GetProperty("id").GetString()!;

            JsonElement joined = await PortalAsync(root, member, $"community/groups/{openId}/join", []);
            Assert.True(joined.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), joined.GetRawText());
            Assert.Contains(Joiner, MembersOf(await StudioAsync(Name(open))));

            JsonElement refused = await PortalAsync(root, member, $"community/groups/{closedId}/join", []);
            Assert.True(refused.TryGetProperty("error", out JsonElement error), refused.GetRawText());
            Assert.Contains("invitation", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Joiner, MembersOf(await StudioAsync(Name(closed))));

            JsonElement left = await PortalAsync(root, member, $"community/groups/{openId}/leave", []);
            Assert.True(left.TryGetProperty("success", out JsonElement leftOk) && leftOk.GetBoolean(), left.GetRawText());
            Assert.DoesNotContain(Joiner, MembersOf(await StudioAsync(Name(open))));

            // The owner cannot leave their own group, as in Studio.
            JsonElement ownerLeaves = await PortalAsync(root, admin, $"community/groups/{openId}/leave", []);
            Assert.True(ownerLeaves.TryGetProperty("error", out _), ownerLeaves.GetRawText());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Name(open)}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Name(closed)}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Joiner}?deleteOwned=true");
        }
    }

    [Fact]
    public async Task What_portals_self_says_about_making_a_group_is_what_createGroup_then_does()
    {
        string root = await RequireServerAsync();
        string title = $"ZZ Said And Done {Guid.NewGuid():N}"[..30];

        try
        {
            foreach ((string who, string password, string userType) in new[]
                     {
                         (Member, MemberPassword, "creator"),
                         (Viewer, ViewerPassword, "viewer"),
                     })
            {
                string token = await MakeMemberAsync(who, password, "user", userType);
                JsonElement self = await PortalAsync(root, token, "portals/self", null);
                bool said = self.GetProperty("user").GetProperty("privileges").EnumerateArray()
                    .Any(p => p.GetString() == "portal:user:createGroup");

                JsonElement made = await PortalAsync(root, token, "community/createGroup", new() { ["title"] = $"{title} {userType}" });
                bool done = made.TryGetProperty("success", out JsonElement success) && success.GetBoolean();

                Assert.True(said == done, $"{userType}: portals/self says createGroup {said}, createGroup did it {done}");
                Assert.Contains("portal:user:joinGroup", self.GetProperty("user").GetProperty("privileges").EnumerateArray()
                    .Select(p => p.GetString()));

                if (userType == "viewer")
                {
                    Assert.False(done, made.GetRawText());
                }
            }
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Name($"{title} creator")}");
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Name($"{title} viewer")}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Viewer}?deleteOwned=true");
        }
    }

    /// <summary>
    /// ADR-256 §5a: a group's members are read through <c>community/groups/{id}/users</c> and <c>userList</c> — the Python
    /// API's <c>group.get_members()</c> and <c>group.user_list()</c>, both 404 until v1.0.499's review — and a group that
    /// hides its members hides them here as Studio does.
    /// </summary>
    [Fact]
    public async Task A_groups_members_are_read_through_the_portal_and_hidden_where_Studio_hides_them()
    {
        string root = await RequireServerAsync();
        string admin = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string title = $"ZZ Portal Members {Guid.NewGuid():N}"[..30];
        const string Reader = Member + "_r";

        try
        {
            string member = await MakeMemberAsync(Reader, MemberPassword, "user", "creator");
            string id = (await PortalAsync(root, admin, "community/createGroup", new() { ["title"] = title, ["access"] = "org" }))
                .GetProperty("group").GetProperty("id").GetString()!;
            await PortalAsync(root, admin, $"community/groups/{id}/addUsers", new() { ["users"] = Reader });

            JsonElement users = await PortalAsync(root, admin, $"community/groups/{id}/users", []);
            string owner = users.GetProperty("owner").GetString()!;
            Assert.Contains(owner, users.GetProperty("admins").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal([Reader], users.GetProperty("users").EnumerateArray().Select(u => u.GetString()));

            JsonElement list = await PortalAsync(root, admin, $"community/groups/{id}/userList", new() { ["start"] = "1", ["num"] = "100" });
            Assert.Equal("owner", list.GetProperty("owner").GetProperty("memberType").GetString());
            JsonElement row = Assert.Single(list.GetProperty("users").EnumerateArray());
            Assert.Equal(Reader, row.GetProperty("username").GetString());
            Assert.Equal("member", row.GetProperty("memberType").GetString());
            Assert.Equal(-1, list.GetProperty("nextStart").GetInt32());
            Assert.Empty((await PortalAsync(root, admin, $"community/groups/{id}/userList", new() { ["memberType"] = "admin" }))
                .GetProperty("users").EnumerateArray());

            // A member reads them too, until the group hides its members from all but its owner and managers.
            Assert.Equal([Reader], (await PortalAsync(root, member, $"community/groups/{id}/users", []))
                .GetProperty("users").EnumerateArray().Select(u => u.GetString()));
            await PortalAsync(root, admin, $"community/groups/{id}/update", new() { ["hiddenMembers"] = "true" });
            JsonElement hidden = await PortalAsync(root, member, $"community/groups/{id}/users", []);
            Assert.Equal(403, hidden.GetProperty("error").GetProperty("code").GetInt32());
            JsonElement listHidden = await PortalAsync(root, member, $"community/groups/{id}/userList", null);
            Assert.True(listHidden.TryGetProperty("error", out _), listHidden.GetRawText());
            Assert.Equal([Reader], (await PortalAsync(root, admin, $"community/groups/{id}/users", []))
                .GetProperty("users").EnumerateArray().Select(u => u.GetString()));
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Name(title)}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Reader}?deleteOwned=true");
        }
    }

    private static string Name(string title) => title.ToLowerInvariant().Replace(' ', '-');

    private static IEnumerable<string> MembersOf(JsonElement group) =>
        group.TryGetProperty("members", out JsonElement members) && members.ValueKind == JsonValueKind.Array
            ? members.EnumerateArray().Select(m => m.GetProperty("name").GetString()!)
            : [];

    private async Task<JsonElement> StudioAsync(string name)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, $"/admin/groups/{name}");
        Assert.True(status == 200, $"reading '{name}' through Studio: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> PortalAsync(string root, string token, string path, Dictionary<string, string>? form)
    {
        using HttpRequestMessage request = new(form is null ? HttpMethod.Get : HttpMethod.Post, $"{root}/sharing/rest/{path}?f=json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Makes a member, removing a leftover of the same name first, and signs them in.</summary>
    private async Task<string> MakeMemberAsync(string name, string password, string role, string userType)
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/members/{name}?deleteOwned=true");
        (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name, role, userType }));
        Assert.True(made is 200 or 201, $"making '{name}': {made}");

        string root = await RequireServerAsync();
        string issued = JsonDocument.Parse(madeBody).RootElement.GetProperty("password").GetString()!;
        string once = await SignInAsync(root, name, issued);

        (HttpStatusCode changed, _) = await RequestAsync(HttpMethod.Post, $"{root}/rest/auth/password", once,
            JsonSerializer.Serialize(new { currentPassword = issued, newPassword = password }));
        Assert.True(changed is HttpStatusCode.OK or HttpStatusCode.NoContent, $"changing the password: {changed}");

        return await SignInAsync(root, name, password);
    }

    private async Task<string> SignInAsync(string root, string name, string password)
    {
        using StringContent body = new(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(new Uri($"{root}/rest/auth/login"), body);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }
}
