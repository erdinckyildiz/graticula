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
/// ADR-219, conditions 1–3: a partnership between two organisations, ArcGIS Online's partnered collaboration. A group
/// limited to partners takes a partner's member only while the partnership is active; ending it removes the partner's
/// members from such groups and demotes its managers elsewhere; a third organisation reads none of it.
/// </summary>
[Collection("server settings")]
public sealed class TwoOrganisationsPartnerBeforeTheirGroupsMixTests : ArcGisClient
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

    private async Task<HttpStatusCode> AddAsync(string admin, string group, string member, bool manager = false) =>
        (await SendAsync(HttpMethod.Put, $"/admin/groups/{group}/members/{Uri.EscapeDataString(member)}", token: admin,
            json: new { manager })).Status;

    private async Task<string[]> MembersAsync(string admin, string group, string standing)
    {
        (_, JsonElement body) = await SendAsync(HttpMethod.Get, $"/admin/groups/{group}", token: admin);
        return [.. body.GetProperty("members").EnumerateArray()
            .Where(m => m.GetProperty("standing").GetString() == standing)
            .Select(m => m.GetProperty("name").GetString()!)];
    }

    private async Task<HttpStatusCode> ChangeAsync(string? host, string token, Guid id, string action, bool? shareMembers = null) =>
        (await SendAsync(HttpMethod.Put, $"/admin/partnerships/{id}", host, token, new { action, shareMembers })).Status;

    [Fact]
    public async Task A_group_limited_to_partners_follows_the_partnership_and_ending_it_takes_the_partner_out()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string partnerKey = $"pa{run}", thirdKey = $"pc{run}";
        string partnerHost = $"{partnerKey}.localhost", thirdHost = $"{thirdKey}.localhost";
        string only = $"partnersonly{run}", any = $"anyorg{run}", theirs = $"theirs{run}", local = $"zzlocal{run}";

        (HttpStatusCode made, JsonElement partner) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key = partnerKey, name = $"Partner {run}", hosts = new[] { partnerHost }, administrator = $"paadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && partner.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");
        (_, JsonElement third) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key = thirdKey, name = $"Third {run}", hosts = new[] { thirdHost }, administrator = $"pcadmin{run}",
        });

        try
        {
            string pa = await SignedInAsync($"paadmin{run}", partner.GetProperty("administrator").GetProperty("password").GetString()!, partnerHost);
            string pc = await SignedInAsync($"pcadmin{run}", third.GetProperty("administrator").GetProperty("password").GetString()!, thirdHost);

            foreach (string m in new[] { "one", "two", "three" })
            {
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/members", partnerHost, pa,
                    new { name = $"{m}{run}", role = "user" })).Status);
            }

            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/members", thirdHost, pc,
                new { name = $"stranger{run}", role = "user" })).Status);

            // Two groups of the default organisation: one limited to partners, one open to any organisation.
            foreach (string g in new[] { only, any })
            {
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", token: admin, json: new { name = g })).Status);
            }

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{only}/tenants", token: admin,
                json: new { admitsOtherTenants = true, partnersOnly = true })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{any}/tenants", token: admin,
                json: new { admitsOtherTenants = true })).Status);

            // Invited by host name: pending, and nobody of the partner gets into the limited group yet.
            (HttpStatusCode invited, JsonElement invitation) = await SendAsync(HttpMethod.Post, "/admin/partnerships", token: admin,
                json: new { organization = partnerHost, shareMembers = false });
            Assert.Equal(HttpStatusCode.Created, invited);
            Guid id = invitation.GetProperty("id").GetGuid();
            Assert.Equal("pending", invitation.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.NotFound, await AddAsync(admin, only, $"{partnerKey}\\one{run}"));

            // A third organisation reads nothing of it.
            (_, JsonElement thirdSees) = await SendAsync(HttpMethod.Get, "/admin/partnerships", thirdHost, pc);
            Assert.Empty(thirdSees.GetProperty("partnerships").EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, await ChangeAsync(thirdHost, pc, id, "accept"));

            // Only the invited accepts; accepting, it lets the default organisation find its members.
            Assert.Equal(HttpStatusCode.Conflict, await ChangeAsync(null, admin, id, "accept"));
            Assert.Equal(HttpStatusCode.OK, await ChangeAsync(partnerHost, pa, id, "accept", shareMembers: true));

            (_, JsonElement candidates) = await SendAsync(HttpMethod.Get, $"/admin/groups/{only}/candidates", token: admin);
            Assert.Contains($"{partnerKey}\\one{run}", candidates.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(HttpStatusCode.OK, await AddAsync(admin, only, $"{partnerKey}\\one{run}"));
            Assert.Equal(HttpStatusCode.NotFound, await AddAsync(admin, only, $"{thirdKey}\\stranger{run}"));

            // A group holding a member of an organisation that is not a partner is not limited to partners.
            Assert.Equal(HttpStatusCode.OK, await AddAsync(admin, any, $"{thirdKey}\\stranger{run}"));
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, $"/admin/groups/{any}/tenants", token: admin,
                json: new { admitsOtherTenants = true, partnersOnly = true })).Status);

            // A refusal changes nothing: closed to other organisations, still holding the stranger, the group is refused
            // the limit and stays closed rather than opening to every organisation (the ux-designer's find, 2026-10-26).
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{any}/tenants", token: admin,
                json: new { admitsOtherTenants = false })).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, $"/admin/groups/{any}/tenants", token: admin,
                json: new { admitsOtherTenants = true, partnersOnly = true })).Status);
            (_, JsonElement still) = await SendAsync(HttpMethod.Get, $"/admin/groups/{any}", token: admin);
            Assert.False(still.GetProperty("admitsOtherTenants").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{any}/tenants", token: admin,
                json: new { admitsOtherTenants = true })).Status);
            Assert.Equal(HttpStatusCode.OK, await AddAsync(admin, any, $"{partnerKey}\\three{run}", manager: true));

            // Suspended: who is in stays, nobody new comes in.
            Assert.Equal(HttpStatusCode.OK, await ChangeAsync(partnerHost, pa, id, "suspend"));
            Assert.Equal(HttpStatusCode.NotFound, await AddAsync(admin, only, $"{partnerKey}\\two{run}"));
            Assert.Contains($"{partnerKey}\\one{run}", await MembersAsync(admin, only, "member"));
            Assert.Equal(HttpStatusCode.OK, await ChangeAsync(null, admin, id, "resume"));
            Assert.Equal(HttpStatusCode.OK, await AddAsync(admin, only, $"{partnerKey}\\two{run}", manager: true));

            // And the other way: the partner's own group limited to partners takes a member of this organisation.
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/members", token: admin,
                json: new { name = local, role = "user" })).Status);
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", partnerHost, pa, new { name = theirs })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{theirs}/tenants", partnerHost, pa,
                new { admitsOtherTenants = true, partnersOnly = true })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put,
                $"/admin/groups/{theirs}/members/{Uri.EscapeDataString($"default\\{local}")}", partnerHost, pa, new { manager = false })).Status);

            // What ending would do, asked first and changing nothing.
            (HttpStatusCode previewed, JsonElement ending) = await SendAsync(HttpMethod.Get, $"/admin/partnerships/{id}/ending", token: admin);
            Assert.Equal(HttpStatusCode.OK, previewed);
            Assert.Contains(ending.GetProperty("groups").EnumerateArray(), g => g.GetProperty("removed").GetInt64() > 0);
            Assert.Contains(ending.GetProperty("groups").EnumerateArray(), g => g.GetProperty("demoted").GetInt64() > 0);
            Assert.Contains($"{partnerKey}\\one{run}", await MembersAsync(admin, only, "member"));

            // Ended: the partner's members leave the limited group; its manager in the open group is a member now.
            Assert.Equal(HttpStatusCode.OK, await ChangeAsync(null, admin, id, "end"));
            Assert.DoesNotContain(await MembersAsync(admin, only, "member"), n => n.StartsWith(partnerKey, StringComparison.Ordinal));
            Assert.DoesNotContain(await MembersAsync(admin, only, "manager"), n => n.StartsWith(partnerKey, StringComparison.Ordinal));
            Assert.Contains($"{partnerKey}\\three{run}", await MembersAsync(admin, any, "member"));
            Assert.Contains($"{thirdKey}\\stranger{run}", await MembersAsync(admin, any, "member"));
            (_, JsonElement theirGroup) = await SendAsync(HttpMethod.Get, $"/admin/groups/{theirs}", partnerHost, pa);
            Assert.DoesNotContain(theirGroup.GetProperty("members").EnumerateArray(),
                m => m.GetProperty("name").GetString()!.EndsWith(local, StringComparison.Ordinal));

            Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/admin/partnerships/{id}", partnerHost, pa)).Status);
            (_, JsonElement gone) = await SendAsync(HttpMethod.Get, "/admin/partnerships", token: admin);
            Assert.DoesNotContain(gone.GetProperty("partnerships").EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{partnerKey}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{thirdKey}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/groups/{only}", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/groups/{any}", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{local}", token: admin);
        }
    }

    /// <summary>
    /// ADR-219 condition 2, from the other side: the invited organisation ends it, and both organisations' groups are
    /// cleaned — each one's partners-only group loses the other's members, and each one's open group keeps the other's
    /// manager as a member.
    /// </summary>
    [Fact]
    public async Task Ending_from_the_invited_side_cleans_both_organisations_groups()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"pe{run}", host = $"pe{run}.localhost";
        string dOnly = $"donly{run}", dAny = $"dany{run}", pOnly = $"ponly{run}", pAny = $"pany{run}";
        string here1 = $"zzhereone{run}", here2 = $"zzheretwo{run}";

        (HttpStatusCode made, JsonElement partner) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Ender {run}", hosts = new[] { host }, administrator = $"peadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && partner.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}.");

        try
        {
            string pe = await SignedInAsync($"peadmin{run}", partner.GetProperty("administrator").GetProperty("password").GetString()!, host);

            foreach (string m in new[] { "one", "two" })
            {
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/members", host, pe,
                    new { name = $"{m}{run}", role = "user" })).Status);
            }

            foreach (string m in new[] { here1, here2 })
            {
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/members", token: admin,
                    json: new { name = m, role = "user" })).Status);
            }

            // Two groups each side: one limited to partners, one open to any organisation.
            foreach ((string? at, string token, string only, string any) in new[] { ((string?)null, admin, dOnly, dAny), (host, pe, pOnly, pAny) })
            {
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", at, token, new { name = only })).Status);
                Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/admin/groups", at, token, new { name = any })).Status);
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{only}/tenants", at, token,
                    new { admitsOtherTenants = true, partnersOnly = true })).Status);
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{any}/tenants", at, token,
                    new { admitsOtherTenants = true })).Status);
            }

            (HttpStatusCode invited, JsonElement invitation) = await SendAsync(HttpMethod.Post, "/admin/partnerships", token: admin,
                json: new { organization = host, shareMembers = true });
            Assert.Equal(HttpStatusCode.Created, invited);
            Guid id = invitation.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, await ChangeAsync(host, pe, id, "accept", shareMembers: true));

            // Each side takes the other's members: one into its partners-only group, one as manager of its open group.
            string theirOne = Uri.EscapeDataString($"{key}\\one{run}"), theirTwo = Uri.EscapeDataString($"{key}\\two{run}");
            string ourOne = Uri.EscapeDataString($"default\\{here1}"), ourTwo = Uri.EscapeDataString($"default\\{here2}");

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{dOnly}/members/{theirOne}", token: admin, json: new { manager = false })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{dAny}/members/{theirTwo}", token: admin, json: new { manager = true })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{pOnly}/members/{ourOne}", host, pe, new { manager = false })).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/admin/groups/{pAny}/members/{ourTwo}", host, pe, new { manager = true })).Status);

            // The invited side ends it.
            Assert.Equal(HttpStatusCode.OK, await ChangeAsync(host, pe, id, "end"));

            async Task<(string Name, string Standing)[]> GroupAsync(string? at, string token, string group)
            {
                (_, JsonElement body) = await SendAsync(HttpMethod.Get, $"/admin/groups/{group}", at, token);
                return [.. body.GetProperty("members").EnumerateArray()
                    .Select(m => (m.GetProperty("name").GetString()!, m.GetProperty("standing").GetString()!))];
            }

            // The default organisation's groups: the partner's member gone from the limited one, its manager a member.
            Assert.DoesNotContain(await GroupAsync(null, admin, dOnly), m => m.Name.Contains($"one{run}", StringComparison.Ordinal));
            Assert.Contains(await GroupAsync(null, admin, dAny), m => m.Name.Contains($"two{run}", StringComparison.Ordinal) && m.Standing == "member");

            // And the partner's groups, which nobody on that side touched: the same, the other way round.
            Assert.DoesNotContain(await GroupAsync(host, pe, pOnly), m => m.Name.Contains(here1, StringComparison.Ordinal));
            Assert.Contains(await GroupAsync(host, pe, pAny), m => m.Name.Contains(here2, StringComparison.Ordinal) && m.Standing == "member");
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/groups/{dOnly}", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/groups/{dAny}", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{here1}", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/{here2}", token: admin);
        }
    }
}
