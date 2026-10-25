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
/// ADR-204 §5e item 18 and condition 16: an organisation defines roles of its own. One custom role name in two
/// organisations is two roles with their own privileges, each organisation's members hold their own's, and the
/// built-in roles — every organisation's while there are several — are changed by none.
/// </summary>
[Collection("server settings")]
public sealed class AnOrganisationsRolesAreItsOwnTests : ArcGisClient
{
    private static readonly string[] Create = ["content:create"];
    private static readonly string[] CreateAndEdit = ["content:create", "features:edit"];
    private static readonly string[] GroupsOnly = ["groups:create"];

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

    private async Task<string[]> PrivilegesAsync(string token, string? host)
    {
        (_, JsonElement who) = await SendAsync(HttpMethod.Get, "/rest/whoami", host, token);
        return [.. who.GetProperty("privileges").EnumerateArray().Select(p => p.GetString()!)];
    }

    [Fact]
    public async Task One_custom_role_name_in_two_organisations_is_two_roles_and_the_built_ins_are_fixed()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"roles{run}";
        string host = $"{key}.localhost";
        string role = $"zz field editor {run}";
        string mainMember = $"zzrolemain{run}", jvMember = $"zzrolejv{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Roles {run}", hosts = new[] { host }, administrator = $"rolesadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");

        try
        {
            string jv = await SignedInAsync($"rolesadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/roles", token: admin,
                json: new { name = role, description = "main", privileges = Create })).Status);
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/roles", host, jv,
                json: new { name = role, description = "jv", privileges = CreateAndEdit })).Status);

            // Each organisation lists one role of the name, bare.
            foreach ((string? at, string token) in new[] { ((string?)null, admin), (host, jv) })
            {
                (_, JsonElement roles) = await SendAsync(HttpMethod.Get, "/admin/roles", at, token);
                string[] names = [.. roles.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];
                Assert.Single(names, n => n == role);
                Assert.DoesNotContain(names, n => n.Contains('\\', StringComparison.Ordinal));
            }

            // Each organisation's member holds its own role's privileges.
            (_, JsonElement a) = await SendAsync(HttpMethod.Post, "/admin/members", token: admin, json: new { name = mainMember, role });
            (_, JsonElement b) = await SendAsync(HttpMethod.Post, "/admin/members", host, jv, new { name = jvMember, role });
            string[] mainHolds = await PrivilegesAsync(await SignedInAsync(mainMember, a.GetProperty("password").GetString()!), null);
            string[] jvHolds = await PrivilegesAsync(await SignedInAsync(jvMember, b.GetProperty("password").GetString()!, host), host);
            Assert.Contains("features:edit", jvHolds);
            Assert.DoesNotContain("features:edit", mainHolds);

            // A built-in role is every organisation's while there are several: changed by none, and not a custom name.
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, "/admin/roles/publisher/privileges", host, jv,
                new { privileges = Create })).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, "/admin/roles/publisher/privileges", token: admin,
                json: new { privileges = Create })).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/admin/roles", host, jv,
                new { name = "Publisher", description = "x", privileges = Array.Empty<string>() })).Status);

            // The JV changes its own role; the main organisation's keeps its privileges.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/roles/{Uri.EscapeDataString(role)}/privileges", host, jv,
                new { privileges = GroupsOnly })).Status);
            (_, JsonElement mainRoles) = await SendAsync(HttpMethod.Get, "/admin/roles", token: admin);
            JsonElement mine = mainRoles.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("name").GetString() == role);
            string[] kept = [.. mine.GetProperty("privileges").EnumerateArray().Select(p => p.GetString()!)];
            Assert.Equal<string>(Create, kept);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{mainMember}", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/roles/{Uri.EscapeDataString(role)}", token: admin);
        }
    }
}
