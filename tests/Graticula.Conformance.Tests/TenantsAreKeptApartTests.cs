using System;
using System.Collections.Generic;
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
/// Two organisations in one deployment, each at its own host name — ADR-204: what one publishes the other cannot
/// read, a member of one is nobody at the other's host, the machine's administration is the system administrator's,
/// and a group that admits other tenants is the one route between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two answers, both asserted.</b> Over a platform store connected as a superuser or a <c>BYPASSRLS</c> role — CI's
/// fixture — the floor binds nothing, and making a second tenant is refused with the reason (condition 9); that refusal
/// is the test. Over a bound role, the tenant is made and every rule below is walked, and then purged.
/// </para>
/// <para>
/// <b>Alone</b>, in the collection that runs by itself: while a second tenant exists the whole server routes by tenant
/// and keeps the machine's administration to the system administrator, which would change what tests beside it see.
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed class TenantsAreKeptApartTests : ArcGisClient
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

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string?> SignInAsync(string name, string password, string? host = null)
    {
        (HttpStatusCode status, JsonElement body) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password });
        return status == HttpStatusCode.OK ? body.GetProperty("token").GetString() : null;
    }

    /// <summary>Signs in with an issued password, replaces it, and signs in again.</summary>
    private async Task<string> SettleAsync(string name, string issued, string? host = null)
    {
        string first = await SignInAsync(name, issued, host) ?? throw new InvalidOperationException($"{name} could not sign in.");
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first, new { currentPassword = issued, newPassword = own });
        return await SignInAsync(name, own, host) ?? throw new InvalidOperationException($"{name} could not sign in again.");
    }

    [Fact]
    public async Task Two_organisations_are_kept_apart_and_meet_only_in_a_group_that_admits_the_other()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"jv{run}";
        string jvHost = $"{key}.localhost";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"JV {run}", hosts = new[] { jvHost }, administrator = $"jvadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            // Condition 9: the store's role is not bound by the floor, so a second organisation is refused — and says why.
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making a tenant answered {(int)made}: {tenant}");

        try
        {
            string jv = await SettleAsync($"jvadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, jvHost);

            // Each organisation is its own portal.
            (_, JsonElement home) = await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json");
            (_, JsonElement theirs) = await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json", jvHost);
            Assert.NotEqual(home.GetProperty("id").GetString(), theirs.GetProperty("id").GetString());
            // Each is a single-tenant portal to its clients, the main one included, while there are two (§5e item 12).
            Assert.Equal("singletenant", theirs.GetProperty("portalMode").GetString());
            Assert.Equal("singletenant", home.GetProperty("portalMode").GetString());

            // What the JV publishes, the default organisation cannot read or find.
            foreach (string name in new[] { $"jv_shared_{run}", $"jv_org_{run}" })
            {
                (HttpStatusCode defined, JsonElement design) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", jvHost, jv, new
                {
                    name, geometryType = "Point", srid = 4326, fields = new[] { new { name = "label", type = "text", nullable = true, length = 20 } },
                });
                Assert.True(defined == HttpStatusCode.Created, $"Defining {name} at the JV answered {(int)defined}: {design}");
            }

            string shared = $"/rest/services/hosted/jv_shared_{run}/FeatureServer/0?f=json";
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, shared, jvHost, jv)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, shared, token: admin)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(
                HttpMethod.Get, $"/rest/services/hosted/jv_shared_{run}/MapServer?f=json", token: admin)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(
                HttpMethod.Get, $"/rest/services/hosted/jv_shared_{run}/VectorTileServer?f=json", token: admin)).Status);
            (_, JsonElement search) = await SendAsync(HttpMethod.Get, $"/sharing/rest/search?q=jv_shared_{run}&f=json", token: admin);
            Assert.Equal(0, search.GetProperty("total").GetInt32());

            // A member of one organisation is an outsider at the other's host — since ADR-220 a member of any organisation
            // is somebody everywhere, for what is shared with every organisation — told their own organisation's id and
            // holding nothing here; and the machine is the system's.
            (HttpStatusCode selfStatus, JsonElement self) = await SendAsync(HttpMethod.Get, "/sharing/rest/community/self?f=json", token: jv);
            Assert.Equal(HttpStatusCode.OK, selfStatus);
            Assert.Equal(theirs.GetProperty("id").GetString(), self.GetProperty("orgId").GetString());
            (_, JsonElement who) = await SendAsync(HttpMethod.Get, "/rest/whoami", token: jv);
            Assert.True(who.GetProperty("outsider").GetBoolean());
            Assert.Empty(who.GetProperty("privileges").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/members", token: jv)).Status);
            // Its own logs are its own (owner decision 2026-10-22); the server's process log is the system's.
            (HttpStatusCode logs, JsonElement index) = await SendAsync(HttpMethod.Get, "/admin/logs", jvHost, jv);
            Assert.Equal(HttpStatusCode.OK, logs);
            Assert.DoesNotContain("server", index.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/logs/server", jvHost, jv)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/admin/tenants", jvHost, jv)).Status);

            // The one route between them: a group of the JV that admits other organisations.
            (_, JsonElement member) = await SendAsync(HttpMethod.Post, "/admin/members", token: admin, json: new { name = $"partner{run}", role = "user" });
            string partner = await SettleAsync($"partner{run}", member.GetProperty("password").GetString()!);

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(
                HttpMethod.Put, $"/admin/services/jv_org_{run}/sharing?folder=hosted", jvHost, jv, new { sharing = "organization" })).Status);
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", jvHost, jv, new { name = $"partners{run}" })).Status);
            // Another organisation's member is named with their organisation (§5e item 16); a bare name is the JV's own.
            string outsider = Uri.EscapeDataString($"default\\partner{run}");
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(
                HttpMethod.Put, $"/admin/groups/partners{run}/members/{outsider}", jvHost, jv, new { })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(
                HttpMethod.Put, $"/admin/groups/partners{run}/tenants", jvHost, jv, new { admitsOtherTenants = true })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(
                HttpMethod.Put, $"/admin/groups/partners{run}/members/partner{run}", jvHost, jv, new { })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(
                HttpMethod.Put, $"/admin/groups/partners{run}/members/{outsider}", jvHost, jv, new { })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(
                HttpMethod.Put, $"/admin/groups/partners{run}/items/jv_shared_{run}?folder=hosted", jvHost, jv, new { })).Status);

            // The partner reads what the group reaches, and nothing the JV shared with its own organisation, and may do
            // nothing there.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, shared, jvHost, partner)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(
                HttpMethod.Get, $"/rest/services/hosted/jv_org_{run}/FeatureServer/0?f=json", jvHost, partner)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/admin/hosted/define", jvHost, partner, new
            {
                name = $"x{run}", geometryType = "Point", srid = 4326, fields = Array.Empty<object>(),
            })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, shared, jvHost)).Status);
        }
        finally
        {
            (HttpStatusCode purged, JsonElement said) = await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);

            // The partner is the main organisation's member and outlives the purge; left behind, every run pushed the
            // Members page past its first page and another suite's newest member onto the second.
            await SendAsync(HttpMethod.Delete, $"/admin/members/partner{run}", token: admin);

            Assert.True(purged == HttpStatusCode.OK, $"Purging the tenant answered {(int)purged}: {said}");
        }

        // And with it gone, the deployment is one organisation again.
        (_, JsonElement after) = await SendAsync(HttpMethod.Get, "/sharing/rest/portals/self?f=json");
        Assert.Equal("singletenant", after.GetProperty("portalMode").GetString());
    }
}
