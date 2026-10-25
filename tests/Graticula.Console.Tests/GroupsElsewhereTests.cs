using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A member's own Studio › Groups lists the groups of other organizations they are in — ADR-204 §5e item 17.4, ArcGIS
/// Online's external groups — under their own, each linking to the group's page at that organization's address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted as the reader who needs it</b>: a plain <c>user</c> of the default organization, added to a group of a
/// second one. And its absence twice — on a server of one organization, and to a member in no such group — because a
/// heading over nothing is the first-run state most deployments are in.
/// </para>
/// <para>
/// <b>The link is followed, not only read.</b> Its address is opened at the other organization's host with the
/// member's token from there, and the group's own page has to draw: a link to a route Studio does not have would pass
/// every assertion about its <c>href</c>.
/// </para>
/// <para>
/// <b>Every organization made here is purged in <c>finally</c></b>, for the reason <see cref="OrganizationReadersTests"/>
/// gives: while a second one exists the whole server answers as several.
/// </para>
/// </remarks>
public sealed class GroupsElsewhereTests : ConsoleTest
{
    private async Task<(int Status, string Body)> AtHostAsync(
        string host, string? token, HttpMethod method, string path, object? body = null)
    {
        Uri root = new(Root);
        using HttpRequestMessage request = new(method, new Uri($"{Root}{path}"));
        request.Headers.Host = $"{host}:{root.Port}";

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Signs in with an issued password, replaces it, and signs in again: a usable token and its password.</summary>
    private async Task<(string Token, string Password)> SettledTokenAsync(string host, string name, string issued)
    {
        (int s, string b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = issued });
        Assert.True(s == 200, $"Signing in as {name} at {host} answered {s}.");
        string first = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        (s, _) = await AtHostAsync(host, first, HttpMethod.Post, "/rest/auth/password",
            new { currentPassword = issued, newPassword = chosen });
        Assert.True(s is >= 200 and < 300, $"Replacing {name}'s issued password answered {s}.");

        (s, b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = chosen });
        Assert.True(s == 200, $"Signing in again as {name} at {host} answered {s}.");
        return (JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!, chosen);
    }

    private async Task<(string Token, string Password)> MemberAsync(string name)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members",
            JsonSerializer.Serialize(new { name, role = "user" }));
        Assert.True(made == 201, $"Making {name} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;
        return await SettledTokenAsync(new Uri(Root).Host, name, issued);
    }

    private async Task<string[]> TenantKeysAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.True(status == 200, $"GET /admin/tenants answered {status}.");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];
    }

    /// <summary>Opens Studio › Groups at home as this member and waits until the list has drawn.</summary>
    private async Task OpenGroupsAsync(string token)
    {
        await OpenAsync("/studio/groups", token);
        await WaitForAsync("!!document.querySelector('#groupRows td')", "Studio's Groups list did not draw.");
    }

    [Fact]
    public async Task A_members_groups_in_another_organization_are_listed_under_their_own_and_open_there()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string run = Guid.NewGuid().ToString("N")[..8];
        string key = "zze" + run;
        string member = "zz_ext_" + run;
        string lonely = "zz_lone_" + run;
        string group = "zz_partners_" + run;
        const string Title = "Zz partner group";
        const string Organization = "Zz Partners";

        try
        {
            (string token, string password) = await MemberAsync(member);
            (string lonelyToken, _) = await MemberAsync(lonely);

            // <b>A server of one organization says nothing about others</b>, to the same member who will later need it.
            await OpenGroupsAsync(token);
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groupsElsewhere")),
                "A server of one organization shows a section for groups of other organizations.");

            string host = $"{key}.localhost";
            (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/tenants",
                JsonSerializer.Serialize(new { key, name = Organization, hosts = new[] { host }, administrator = $"{key}_admin" }));

            if (made == 409 && answer.Contains("superuser", StringComparison.Ordinal))
            {
                return;
            }

            Assert.True(made == 201, $"Making {key} answered {made}.");
            string issued = JsonDocument.Parse(answer).RootElement.GetProperty("administrator").GetProperty("password").GetString()!;
            (string adminToken, _) = await SettledTokenAsync(host, $"{key}_admin", issued);

            (int s, _) = await AtHostAsync(host, adminToken, HttpMethod.Post, "/admin/groups", new { name = group, title = Title, itemUpdate = "none" });
            Assert.True(s == 201, $"Making {group} answered {s}.");
            (s, _) = await AtHostAsync(host, adminToken, HttpMethod.Put, $"/admin/groups/{group}/tenants", new { admitsOtherTenants = true });
            Assert.True(s == 200, $"Admitting other organizations to {group} answered {s}.");
            string qualified = $"default\\{member}";
            (s, _) = await AtHostAsync(host, adminToken, HttpMethod.Put, $"/admin/groups/{group}/members/{Uri.EscapeDataString(qualified)}", new { });
            Assert.True(s == 200, $"Adding {qualified} to {group} answered {s}.");

            // <b>At home, under their own groups.</b>
            await OpenGroupsAsync(token);
            await WaitForAsync(Shown("#groupsElsewhere"), "The member's own Groups page does not list the group of another organization they are in.");
            string row = await Browser.EvaluateAsync<string>(
                "document.querySelector('#groupsElsewhereRows tr')?.innerText || ''") ?? string.Empty;
            Assert.Contains(Title, row, StringComparison.Ordinal);
            Assert.Contains(Organization, row, StringComparison.Ordinal);
            Assert.Contains("member", row, StringComparison.Ordinal);
            Assert.Equal(1, await Browser.EvaluateAsync<int>("document.querySelectorAll('#groupsElsewhereRows tr').length"));

            // The section comes after the reader's own list, not inside it: these rows are not this organization's.
            Assert.True(await Browser.EvaluateAsync<bool>(
                "!document.querySelector('#groupRows [data-elsewhere]') && !!(document.getElementById('groupRows').compareDocumentPosition("
                + "document.getElementById('groupsElsewhere')) & Node.DOCUMENT_POSITION_FOLLOWING)"),
                "Groups of other organizations are mixed into, or drawn above, the reader's own.");

            // It says the name to sign in with there, the one the other host's sign-in card asks for.
            Assert.Contains(qualified, await Browser.EvaluateAsync<string>("document.getElementById('groupsElsewhereLede').textContent") ?? string.Empty,
                StringComparison.Ordinal);

            // <b>The link: the group's own page at that organization's address, in a new tab that says so.</b>
            Uri root = new(Root);
            string href = await Browser.EvaluateAsync<string>("document.querySelector('#groupsElsewhereRows a')?.href || ''") ?? string.Empty;
            Assert.Equal($"{root.Scheme}://{host}:{root.Port}/studio/group/{group}", href);
            Assert.Equal("_blank", await Browser.EvaluateAsync<string>("document.querySelector('#groupsElsewhereRows a').getAttribute('target')"));
            Assert.Contains("noopener", await Browser.EvaluateAsync<string>("document.querySelector('#groupsElsewhereRows a').getAttribute('rel')") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Contains("opens in a new tab", await Browser.EvaluateAsync<string>("document.querySelector('#groupsElsewhereRows a').textContent") ?? string.Empty,
                StringComparison.Ordinal);

            // Nothing here manages it: no Delete, no Leave, no settings, and no row the list's own click handler takes.
            Assert.Equal(0, await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#groupsElsewhere button, #groupsElsewhere tr[data-group]').length"));

            // The search box reads them, and says when it hides them.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const f = document.getElementById("groupFilter");
                  f.value = "nothing-is-called-this";
                  f.dispatchEvent(new Event("input", { bubbles: true }));
                  return true;
                })()
                """);
            await WaitForAsync("(document.getElementById('groupsElsewhereRows').textContent || '').includes('None of them matches')",
                "Searching does not narrow the groups of other organizations.");

            NothingWentWrong(await PageErrorsAsync());

            // <b>Followed</b>: the member, signed in at that host by the qualified name, lands on the group's page.
            (s, string b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name = qualified, password });
            Assert.True(s == 200, $"{qualified} could not sign in at {host}: {s}.");
            string outsiderToken = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;
            await OpenOnHostAsync(host, new Uri(href).PathAndQuery, outsiderToken);
            await WaitForAsync($"document.getElementById('groupTitle')?.textContent === {JsonSerializer.Serialize(Title)}",
                "The link does not open the group's page at the other organization's address.");
            NothingWentWrong(await PageErrorsAsync());

            // <b>A member in no such group sees no section</b>, on the same server of two organizations.
            await OpenGroupsAsync(lonelyToken);
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groupsElsewhere")),
                "A member in no group of another organization is shown a section for them.");
            Assert.Equal("no groups", await Browser.EvaluateAsync<string>("document.getElementById('groupCount').textContent"));
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if ((await TenantKeysAsync()).Contains(key))
            {
                (int purged, _) = await AdminAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true");
                Assert.True(purged == 200, $"Purging {key} answered {purged}. The server still holds a second organization.");
            }

            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{lonely}");
        }
    }
}
