using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-204 §5e item 17.4: a member's own Groups page lists the groups of other organisations they are in, as ArcGIS
/// Online lists external groups — the group, whose it is and where to open it — and only those that still admit other
/// organisations' members. The other organisation reads nothing of it.
/// </summary>
[Collection("server settings")]
public sealed class AMembersGroupsElsewhereAreListedAtHomeTests : ArcGisClient
{
    private string _root = string.Empty;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host = null, string? token = null, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = json is null ? null : JsonContent.Create(json);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> SignedInAsync(string name, string issued, string? host = null)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = own });
        return again.GetProperty("token").GetString()!;
    }

    private async Task<JsonElement[]> ElsewhereAsync(string token, string? host = null)
    {
        (HttpStatusCode status, JsonElement body) = await SendAsync(HttpMethod.Get, "/admin/groups", host, token);
        Assert.Equal(HttpStatusCode.OK, status);
        return [.. body.GetProperty("elsewhere").EnumerateArray()];
    }

    [Fact]
    public async Task A_group_of_another_organisation_is_listed_on_the_members_own_groups_page()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"else{run}";
        string host = $"{key}.localhost";
        string member = $"zzelse{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Elsewhere {run}", hosts = new[] { host }, administrator = $"elseadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");

        try
        {
            string jv = await SignedInAsync($"elseadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);
            (_, JsonElement issued) = await SendAsync(HttpMethod.Post, "/admin/members", token: admin, json: new { name = member, role = "user" });
            string mine = await SignedInAsync(member, issued.GetProperty("password").GetString()!);
            string outsider = Uri.EscapeDataString($"default\\{member}");

            // Two groups of the JV take the member; one is then closed to other organisations again.
            foreach (string group in new[] { $"open{run}", $"closed{run}" })
            {
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", host, jv,
                    new { name = group, title = $"Partners {group}" })).Status);
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{group}/tenants", host, jv,
                    new { admitsOtherTenants = true })).Status);
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{group}/members/{outsider}", host, jv,
                    new { })).Status);
            }

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/closed{run}/tenants", host, jv,
                new { admitsOtherTenants = false })).Status);

            // At home: the open group, with whose it is and where to open it; not the closed one.
            JsonElement listed = Assert.Single(await ElsewhereAsync(mine), g => g.GetProperty("organization").GetProperty("key").GetString() == key);
            Assert.Equal($"open{run}", listed.GetProperty("name").GetString());
            Assert.Equal("member", listed.GetProperty("standing").GetString());
            Assert.Contains(host, listed.GetProperty("url").GetString(), StringComparison.Ordinal);

            // Its own groups are not listed as elsewhere, and the JV's administrator is in no group elsewhere; at the
            // JV's host the member is an outsider, and nothing is listed as elsewhere to them there.
            Assert.Empty(await ElsewhereAsync(jv, host));
            Assert.Empty(await ElsewhereAsync(mine, host));
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{member}", token: admin);
        }
    }
}
