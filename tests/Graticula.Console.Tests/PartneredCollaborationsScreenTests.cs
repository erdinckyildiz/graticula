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
/// Organization › Settings › Collaborations — ADR-219 condition 4, ArcGIS Online's partnered collaboration: an
/// organization's administrator invites, the other's accepts at its own host, either suspends, resumes and ends it, and a
/// group's Other organizations choice has three answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted as the reader who needs it, and its absence first.</b> On a server of one organization the column does not
/// offer the page and the page says why; the first-run state of a server of two is the empty list, not a table with no
/// rows. Visibility is <c>getClientRects</c>, which a closed <c>details</c> or <c>display: none</c> defeats where
/// <c>offsetParent</c> does not.
/// </para>
/// <para>
/// <b>The group's choice is compared with the server, not with what was pressed.</b> After the refusal the test reads the
/// group back and asserts the chosen radio is what the server holds, whatever that is: a refused change is not proof
/// that nothing changed.
/// </para>
/// <para>
/// Every organization made here is purged in <c>finally</c>, for the reason <see cref="OrganizationReadersTests"/> gives:
/// while a second one exists the whole server answers as several.
/// </para>
/// </remarks>
public sealed class PartneredCollaborationsScreenTests : ConsoleTest
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

    private async Task<string> SettledTokenAsync(string host, string name, string issued)
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
        return JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;
    }

    private async Task<string[]> TenantKeysAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.True(status == 200, $"GET /admin/tenants answered {status}.");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];
    }

    /// <summary>Makes a second organization and signs its administrator in there; null where the store cannot.</summary>
    private async Task<string?> OrganizationAsync(string key, string name)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/tenants",
            JsonSerializer.Serialize(new { key, name, hosts = new[] { $"{key}.localhost" }, administrator = $"{key}_admin" }));

        if (made == 409 && answer.Contains("superuser", StringComparison.Ordinal))
        {
            return null;
        }

        Assert.True(made == 201, $"Making {key} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("administrator").GetProperty("password").GetString()!;
        return await SettledTokenAsync($"{key}.localhost", $"{key}_admin", issued);
    }

    private async Task PurgeAsync(string key)
    {
        if ((await TenantKeysAsync()).Contains(key))
        {
            (int purged, _) = await AdminAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true");
            Assert.True(purged == 200, $"Purging {key} answered {purged}. The server still holds a second organization.");
        }
    }

    private const string Drawn =
        "document.getElementById('pcLists')?.getAttribute('aria-busy') !== 'true'";

    private const string Said = "(document.getElementById('pcSays')?.textContent || '')";

    private const string Focused = "(document.activeElement?.dataset || {})";

    /// <summary>
    /// Lets this screen's own writes reach the server, and no other: the harness answers every write itself unless a
    /// test names the route (ADR-218's pin). This test owes the server its undo, which <c>finally</c> pays.
    /// </summary>
    private async Task LetWritesAsync() =>
        await Browser.EvaluateAsync<bool>("(() => { window.__letWritesTo = ['/admin/partnerships', '/tenants']; return true; })()");

    private async Task ConfirmAsync(string mustSay)
    {
        await WaitForAsync("document.getElementById('serverConfirm')?.open", "No confirmation was asked.");
        string body = await Browser.EvaluateAsync<string>("document.getElementById('serverConfirmBody').textContent") ?? string.Empty;
        Assert.Contains(mustSay, body, StringComparison.Ordinal);
        await ClickAsync("#serverConfirmGo");
    }

    [Fact]
    public async Task An_administrator_invites_the_other_accepts_at_its_host_and_either_suspends_resumes_and_ends_it()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        (string token, _) = await SignInAsync();
        string key = "zzp" + Guid.NewGuid().ToString("N")[..8];
        string host = $"{key}.localhost";
        const string Partner = "Zz Partner Surveying";
        string group = "zz_partnered_" + key[3..];
        const string GroupTitle = "Zz partnered crews";

        try
        {
            // <b>A server of one organization</b>: the column does not offer the page, and the address says why.
            await OpenAsync("/studio/orgsettings", token);
            await LetWritesAsync();
            await WaitForAsync("document.querySelectorAll('.view.on > nav.subpages a').length > 0", "Settings drew no column.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown(".view.on > nav.subpages a[href$='/collaborations']")),
                "A server of one organization offers Collaborations.");
            await OpenAsync("/studio/collaborations", token);
            await LetWritesAsync();
            await WaitForAsync(Drawn, "Collaborations never drew.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#pcNew")), "A server of one organization offers to start a collaboration.");
            Assert.Contains("one organization", await Browser.EvaluateAsync<string>("document.getElementById('pcLists').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            NothingWentWrong(await PageErrorsAsync());

            string? theirs = await OrganizationAsync(key, Partner);
            if (theirs is null)
            {
                return;
            }

            // <b>First run on a server of two</b>: the column offers it, and the page is the empty state with the limit.
            await OpenAsync("/studio/orgsettings", token);
            await LetWritesAsync();
            await WaitForAsync(Shown(".view.on > nav.subpages a[href$='/collaborations']"),
                "The column does not offer Collaborations to the administrator of a server of two organizations.");
            await ClickAsync(".view.on > nav.subpages a[href$='/collaborations']");
            await WaitForAsync($"document.getElementById('view-collaborations')?.classList.contains('on') && {Drawn} && !!document.querySelector('.pcempty')",
                "Collaborations did not open on its empty state.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#pcNew")), "Start a collaboration is not shown.");
            Assert.Equal("0 of 20 collaborations", await Browser.EvaluateAsync<string>("document.getElementById('pcCount').textContent"));

            // <b>Invite</b>, by a pasted address, letting them search for our members.
            await ClickAsync("#pcNew");
            await WaitForAsync("document.getElementById('pcDialog').open", "Start a collaboration opened no dialog.");
            await Browser.EvaluateAsync<bool>($"(() => {{ document.getElementById('pcOrg').value = 'https://{host}/studio/'; "
                + "document.querySelector('input[name=pcShare][value=yes]').checked = true; return true; })()");
            await ClickAsync("#pcSend");
            await WaitForAsync("!!document.querySelector('#pcOutgoing tr[data-pc]')", "The invitation is not listed as sent.");
            Assert.Contains("Invitation sent", await Browser.EvaluateAsync<string>(Said) ?? string.Empty, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>($"!!{Focused}.pcWithdraw"), "Focus did not land on the new row.");
            Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('pcSays').getAttribute('aria-live')"));
            NothingWentWrong(await PageErrorsAsync());

            // <b>Accepted at the other organization's host by its administrator.</b>
            await OpenOnHostAsync(host, "/studio/collaborations", theirs);
            await LetWritesAsync();
            await WaitForAsync("!!document.querySelector('#pcIncoming [data-pc-accept]')", "The invitation does not reach the invited organization.");
            Assert.Contains("Their members: Searchable by us", await Browser.EvaluateAsync<string>(
                "document.querySelector('#pcIncoming tr[data-pc]').innerText") ?? string.Empty, StringComparison.Ordinal);
            await ClickAsync("#pcIncoming [data-pc-accept]");
            await WaitForAsync("document.getElementById('pcAccept').open", "Accept opened no dialog.");
            await ClickAsync("#pcAcceptGo");
            await WaitForAsync("!!document.querySelector('#pcPartners [data-pc-suspend]')", "Accepting did not make them partners.");
            Assert.Contains("You are partners", await Browser.EvaluateAsync<string>(Said) ?? string.Empty, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>($"!!{Focused}.pcSuspend"), "Focus was dropped by the redraw after accepting.");
            NothingWentWrong(await PageErrorsAsync());

            // <b>Back home: suspend, resume, change our visibility, end, delete.</b>
            await OpenAsync("/studio/collaborations", token);
            await LetWritesAsync();
            await WaitForAsync("!!document.querySelector('#pcPartners [data-pc-suspend]')", "The partner is not listed at home.");
            Assert.Contains("Our members: Searchable by them", await Browser.EvaluateAsync<string>(
                "document.querySelector('#pcPartners tr[data-pc]').innerText") ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Partners since", await Browser.EvaluateAsync<string>(
                "document.querySelector('#pcPartners tr[data-pc]').innerText") ?? string.Empty, StringComparison.Ordinal);

            await ClickAsync("#pcPartners [data-pc-suspend]");
            await ConfirmAsync("Members already in groups stay");
            await WaitForAsync("!!document.querySelector('#pcPartners [data-pc-resume]')", "Suspending did not suspend.");
            Assert.Contains("Suspended", await Browser.EvaluateAsync<string>("document.querySelector('#pcPartners .pill').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            await WaitForAsync($"!!{Focused}.pcResume", "Focus did not move to Resume.");
            Assert.Contains("Suspended since", await Browser.EvaluateAsync<string>(
                "document.querySelector('#pcPartners tr[data-pc]').innerText") ?? string.Empty, StringComparison.Ordinal);

            await ClickAsync("#pcPartners [data-pc-resume]");
            await WaitForAsync("!!document.querySelector('#pcPartners [data-pc-suspend]')", "Resuming did not resume.");
            Assert.Contains("active again", await Browser.EvaluateAsync<string>(Said) ?? string.Empty, StringComparison.Ordinal);

            await ClickAsync("#pcPartners [data-pc-share]");
            await WaitForAsync("(document.querySelector('#pcPartners tr[data-pc]')?.innerText || '').includes('Our members: Not searchable by them')",
                "Stopping them searching for our members did not change it.");
            Assert.True(await Browser.EvaluateAsync<bool>($"!!{Focused}.pcShare"), "Focus was dropped after changing visibility.");

            // <b>End lists what it changes before it is confirmed</b>: a group of ours limited to partners, holding a member
            // of theirs. Cancel first, which must change nothing.
            (int made, _) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = group, title = GroupTitle }));
            Assert.True(made == 201, $"Making {group} answered {made}.");
            (made, _) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{group}/tenants",
                JsonSerializer.Serialize(new { admitsOtherTenants = true, partnersOnly = true }));
            Assert.True(made == 200, $"Limiting {group} to partners answered {made}.");
            (made, _) = await AtHostAsync(host, theirs, HttpMethod.Post, "/admin/members", new { name = "zz_partner_one", role = "user" });
            Assert.True(made == 201, $"Making a member of {key} answered {made}.");
            (made, _) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{group}/members/{Uri.EscapeDataString($"{key}\\zz_partner_one")}", "{}");
            Assert.True(made == 200, $"Adding the partner's member to {group} answered {made}.");

            await ClickAsync("#pcPartners [data-pc-end]");
            await WaitForAsync("document.getElementById('pcEnd')?.open", "End opened no dialog.");
            Assert.Contains("cannot be undone", await Browser.EvaluateAsync<string>("document.getElementById('pcEndLede').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            string affected = await Browser.EvaluateAsync<string>("document.getElementById('pcEndGroups').innerText") ?? string.Empty;
            Assert.Contains(GroupTitle, affected, StringComparison.Ordinal);
            Assert.Contains("Your group", affected, StringComparison.Ordinal);
            Assert.Contains("1 member of " + Partner + " leaves", affected, StringComparison.Ordinal);
            Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement?.id === 'pcEndCancel'"), "End's dialog does not start on Cancel.");
            await ClickAsync("#pcEndCancel");
            await WaitForAsync("!document.getElementById('pcEnd').open", "Cancel did not close End's dialog.");
            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#pcPartners [data-pc-end]')"), "Cancel ended it anyway.");
            await WaitForAsync($"!!{Focused}.pcEnd", "Focus did not return to End after Cancel.");

            await ClickAsync("#pcPartners [data-pc-end]");
            await WaitForAsync("document.getElementById('pcEnd')?.open", "End opened no dialog the second time.");
            await ClickAsync("#pcEndGo");
            await WaitForAsync("!!document.querySelector('#pcEnded [data-pc-delete]')", "Ending did not end it.");
            Assert.Contains("has ended. 1 group membership was removed", await Browser.EvaluateAsync<string>(Said) ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Ended on", await Browser.EvaluateAsync<string>(
                "document.querySelector('#pcEnded tr[data-pc]').innerText") ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("0 of 20 collaborations", await Browser.EvaluateAsync<string>("document.getElementById('pcCount').textContent"));

            await ClickAsync("#pcEnded [data-pc-delete]");
            await ConfirmAsync("both organizations");
            await WaitForAsync("!!document.querySelector('.pcempty')", "Deleting the ended one did not leave the empty state.");
            NothingWentWrong(await PageErrorsAsync());

            (int s, string b) = await AdminAsync(HttpMethod.Get, "/admin/partnerships");
            Assert.Equal(200, s);
            Assert.Empty(JsonDocument.Parse(b).RootElement.GetProperty("partnerships").EnumerateArray());
        }
        finally
        {
            await PurgeAsync(key);
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{group}");
        }
    }

    [Fact]
    public async Task A_groups_other_organizations_choice_has_three_answers_and_says_the_refusal_in_place()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        (string token, _) = await SignInAsync();
        string run = Guid.NewGuid().ToString("N")[..8];
        string key = "zzq" + run;
        string group = "zz_three_" + run;
        string host = $"{key}.localhost";

        try
        {
            string? theirs = await OrganizationAsync(key, "Zz Not A Partner");
            if (theirs is null)
            {
                return;
            }

            (int s, _) = await AtHostAsync(host, theirs, HttpMethod.Post, "/admin/members", new { name = "zz_stranger", role = "user" });
            Assert.True(s == 201, $"Making a member of {key} answered {s}.");
            (s, _) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = group, title = "Zz three" }));
            Assert.True(s == 201, $"Making {group} answered {s}.");

            async Task<string> ServerSaysAsync()
            {
                (int got, string body) = await AdminAsync(HttpMethod.Get, $"/admin/groups/{group}");
                Assert.Equal(200, got);
                JsonElement g = JsonDocument.Parse(body).RootElement;
                return !g.GetProperty("admitsOtherTenants").GetBoolean() ? "only"
                    : g.GetProperty("partnersOnly").GetBoolean() ? "partners" : "any";
            }

            const string Checked = "document.querySelector('#gsAdmitsTo input:checked')?.value || ''";

            await OpenAsync($"/studio/group/{group}/settings", token);

            await LetWritesAsync();
            await WaitForAsync(Shown("#gsAdmitsTo"), "Settings shows no choice of other organizations.");
            Assert.Equal(3, await Browser.EvaluateAsync<int>("document.querySelectorAll('#gsAdmitsTo input[type=radio]').length"));
            Assert.Equal("only", await Browser.EvaluateAsync<string>(Checked));
            await WaitForAsync("(document.getElementById('gsPartnersNow')?.textContent || '').includes('no active partner')",
                "Partner organizations only does not say there is no partner yet.");

            // Any organization, and somebody of an organization that is not a partner joins.
            await ClickAsync("#gsAdmitsTo input[value=any]");
            await WaitForAsync("(document.getElementById('gsAdmitsSays').textContent || '').startsWith('Saved')", "Choosing any organization did not save.");
            Assert.Equal("any", await ServerSaysAsync());
            Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement?.name === 'gsAdmitsTo'"), "Focus left the choice after saving.");
            (s, _) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{group}/members/{Uri.EscapeDataString($"{key}\\zz_stranger")}", "{}");
            Assert.True(s == 200, $"Adding a member of {key} answered {s}.");

            // Limited to partners: refused, said in place with the way to Members, and the choice is the server's.
            await ClickAsync("#gsAdmitsTo input[value=partners]");
            await WaitForAsync("(document.getElementById('gsAdmitsSays').textContent || '').includes('not a partner')",
                "The refusal to limit the group to partners is not said beside the choice.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#gsAdmitsWay a")), "The way to Members is not shown with the refusal.");
            Assert.Equal("status", await Browser.EvaluateAsync<string>("document.getElementById('gsAdmitsSays').getAttribute('role')"));
            Assert.Equal("any", await ServerSaysAsync());
            Assert.Equal("any", await Browser.EvaluateAsync<string>(Checked));
            Assert.Contains("has a member of an organization that is not a partner", await Browser.EvaluateAsync<string>("document.getElementById('gsAdmitsSays').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('gsAdmitsTo').disabled"), "The choice stayed disabled.");

            // Only this organization.
            await ClickAsync("#gsAdmitsTo input[value=only]");
            await WaitForAsync("(document.getElementById('gsAdmitsSays').textContent || '').startsWith('Saved')", "Choosing only this organization did not save.");
            Assert.Equal("only", await ServerSaysAsync());
            Assert.Equal("only", await Browser.EvaluateAsync<string>(Checked));

            // From closed, too: the stranger is still a member, and a refusal changes nothing.
            await ClickAsync("#gsAdmitsTo input[value=partners]");
            await WaitForAsync("(document.getElementById('gsAdmitsSays').textContent || '').includes('not a partner')",
                "Limiting a closed group that holds a stranger was not refused in place.");
            Assert.Equal("only", await ServerSaysAsync());
            Assert.Equal("only", await Browser.EvaluateAsync<string>(Checked));

            // Drawn again from the server: what was saved is what the page shows.
            await OpenAsync($"/studio/group/{group}/settings", token);
            await LetWritesAsync();
            await WaitForAsync(Shown("#gsAdmitsTo"), "Settings did not draw again.");
            Assert.Equal(await ServerSaysAsync(), await Browser.EvaluateAsync<string>(Checked));
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{group}");
        }
    }
}
