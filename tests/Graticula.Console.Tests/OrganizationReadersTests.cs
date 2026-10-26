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
/// What the console offers each reader once a server holds a second organization — ADR-204 §5d and the owner's decisions
/// of 2026-10-22: an organization's own administrator has its own Server (services, publishing, data sources, its own
/// logs) and only Organizations, Site › Status, Site › General and the server's process log stay the system
/// administrator's; a member of another organization owns and makes nothing here.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is: no control that answers 403, and nothing that is somebody else's.</b> So each page is opened as the
/// reader, at their organization's own host name, and the console's own record of what it asked the server
/// (<c>window.__calls</c>) is read for a refusal — a screen that looks right while its loader was refused is the failure
/// this suite exists for, because the 403 toast is gone seven seconds later.
/// </para>
/// <para>
/// <b>Every organization made here is purged in <c>finally</c>.</b> While a second one exists the whole server answers
/// differently, and every other test in this suite would be measuring that instead.
/// </para>
/// </remarks>
public sealed class OrganizationReadersTests : ConsoleTest
{
    /// <summary>A request at another host name of this server, with a token issued there.</summary>
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

    /// <summary>
    /// Signs in with a password the server issued, replaces it as the server requires before anything else, and signs
    /// in again: the token of an account that can be used.
    /// </summary>
    private async Task<(string Token, string Password)> SettledTokenAsync(string host, string name, string issued)
    {
        (int s, string b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = issued });
        Assert.True(s == 200, $"Signing in as {name} at {host} answered {s}: {b}");
        string first = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        (s, b) = await AtHostAsync(host, first, HttpMethod.Post, "/rest/auth/password",
            new { currentPassword = issued, newPassword = chosen });
        Assert.True(s is >= 200 and < 300, $"Replacing {name}'s issued password answered {s}: {b}");

        (s, b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name, password = chosen });
        Assert.True(s == 200, $"Signing in again as {name} at {host} answered {s}: {b}");
        return (JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!, chosen);
    }

    private async Task<string[]> TenantKeysAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.True(status == 200, $"GET /admin/tenants answered {status}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];
    }

    /// <summary>
    /// Makes an organization and returns its host and its administrator's usable token — or null where the server
    /// connects to its store as a role the floor cannot bind, and refuses a second organization saying so (ADR-204
    /// condition 9). The conformance suite's tenancy tests return there too: that refusal is the behaviour.
    /// </summary>
    private async Task<(string Host, string Token)?> MakeOrganizationAsync(string key)
    {
        string host = $"{key}.localhost";
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/tenants",
            JsonSerializer.Serialize(new { key, name = "Zz Readers", hosts = new[] { host }, administrator = $"{key}_admin" }));

        if (made == 409 && answer.Contains("superuser", StringComparison.Ordinal))
        {
            return null;
        }

        Assert.True(made == 201, $"Making {key} answered {made}: {answer}");

        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("administrator").GetProperty("password").GetString()!;
        return (host, (await SettledTokenAsync(host, $"{key}_admin", issued)).Token);
    }

    private async Task PurgeAsync(string key)
    {
        if ((await TenantKeysAsync()).Contains(key))
        {
            (int purged, string said) = await AdminAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true");
            Assert.True(purged == 200, $"Purging {key} answered {purged}: {said}. The server still holds a second organization.");
        }
    }

    /// <summary>Every request this page made that the server refused with 403.</summary>
    private async Task<string> RefusedAsync() =>
        await Browser.EvaluateAsync<string>(
            "(window.__calls || []).filter(c => c.s === 403).map(c => c.u).join(', ')") ?? string.Empty;

    private async Task NothingRefusedAsync(string page)
    {
        string refused = await RefusedAsync();
        Assert.True(refused.Length == 0, $"{page} asked for something the server refuses this reader: {refused}");
    }

    [Fact]
    public async Task An_organizations_administrator_has_services_data_sources_and_its_own_logs_and_nothing_of_the_systems()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string key = "zzr" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (await MakeOrganizationAsync(key) is not (string host, string token))
            {
                return;
            }

            // Services: Server's bar is Services, Site and Logs — no Organizations — and the health pill, which reads
            // the process's health, is not drawn.
            await OpenOnHostAsync(host, "/server/#/services", token);
            await WaitForAsync("document.getElementById('view-services')?.classList.contains('on') && !!document.querySelector('#services tr')",
                "Services did not draw for an organization's administrator.");
            Assert.Equal("services,sources,logs", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#tabs a')].filter(a => a.getClientRects().length).map(a => a.dataset.tab).join(',')"));
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#healthDot")), "The health pill is drawn, over a read only the system administrator may make.");

            // The geometry service is the server's: no Stop and no sharing to change, and the row says whose it is.
            await OpenOnHostAsync(host, "/server/#/services/Utilities", token);
            await WaitForAsync("!!document.querySelector('#services tr[data-service=\"Utilities/Geometry\"]')", "The Utilities folder listed no Geometry service.");
            Assert.False(await Browser.EvaluateAsync<bool>(
                "!!document.querySelector('#services [data-system-status], #services select[data-service-share], #services [data-svc-pick=\"Utilities/Geometry\"]')"),
                "The server's own geometry service offers an organization's administrator Stop, sharing or selection — each acts for every organization.");
            Assert.Contains("system administrator", await Browser.EvaluateAsync<string>(
                "document.querySelector('#services tr[data-service=\"Utilities/Geometry\"]').textContent") ?? string.Empty, StringComparison.Ordinal);
            await NothingRefusedAsync("Services");

            // Data sources is a tab of its own since ADR-221 — Status and General went to System, so no strip is left;
            // the managed datastore is listed and cannot be paused from here, by anybody.
            await OpenOnHostAsync(host, "/server/#/sources", token);
            await WaitForAsync("!!document.querySelector('#sources [data-probe]')", "Data sources listed nothing for an organization's administrator.");
            Assert.Equal(0, await Browser.EvaluateAsync<int>("document.querySelectorAll('#view-sources nav.subpages a').length"));
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#sources [data-source-quiesce], #sources [data-source-resume]')"),
                "The shared datastore offers Pause to an organization's administrator, which pauses every organization's hosted layers.");
            await NothingRefusedAsync("Data sources");

            // Logs: the organization's three, not the server's process log.
            await OpenOnHostAsync(host, "/server/#/logs", token);
            await WaitForAsync("!!document.querySelector('#logSources [data-log-source=audit]')", "Logs drew no sources.");
            Assert.Equal("audit,requests,studio", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#logSources [data-log-source]')].map(b => b.dataset.logSource).join(',')"));
            Assert.DoesNotContain("Server warnings", await Browser.EvaluateAsync<string>("document.getElementById('logLede').textContent") ?? string.Empty, StringComparison.Ordinal);
            await NothingRefusedAsync("Logs");

            // A system screen asked for by its old address goes back to Services: it is System's, which is not theirs.
            await OpenOnHostAsync(host, "/server/#/settings", token);
            await WaitForAsync("document.getElementById('view-services')?.classList.contains('on') && location.pathname.startsWith('/server/')"
                + " && (document.getElementById('toast').textContent || '').includes('System')",
                "Site › General's old address opened for an organization's administrator instead of returning to Services.");

            // Organization › Map: the gallery is theirs and is read; the ground card is the server's and is absent.
            await OpenOnHostAsync(host, "/studio/#/orgsettings", token);
            await WaitForAsync("!document.getElementById('basemapGallery').disabled",
                "The basemap gallery was never read — the page stopped on a refused read before it.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groundCard")), "The map ground card is shown to an organization's administrator; it is the server's.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#groundWhere")), "Nothing says whose the map ground is.");
            Assert.Contains("system administrator", await Browser.EvaluateAsync<string>("document.getElementById('groundWhere').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#groundWhere a')"), "The map ground's note links into System, which this reader does not enter.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#view-orgsettings nav.subpages a[href$=\"utilities\"]')"),
                "Settings offers Utility services, whose one setting is the server's.");
            await NothingRefusedAsync("Organization › Map");

            // Member roles — ADR-204 §5e item 18: the built-in roles are every organization's and fixed, and the
            // organization makes its own. The custom role is made and deleted through the screen, for real, because a
            // trapped write would answer 200 to a request the server refuses this reader, which is the failure here.
            await OpenOnHostAsync(host, "/studio/#/roles", token);
            await WaitForAsync("!!document.querySelector('#roleRows tr[data-role=publisher]') && !document.getElementById('roleEditor').hidden",
                "Member roles drew no roles.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#roleNew")), "New role is not offered; an organization's administrator makes its own roles.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#rolesWhose")), "Nothing says the built-in roles are shared and custom roles are this organization's.");
            Assert.Contains("Custom roles are this organization's own", await Browser.EvaluateAsync<string>(
                "document.getElementById('rolesWhose').textContent") ?? string.Empty, StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#view-roles .pagehead p:not(#rolesWhose)")),
                "The single-organization line, which says every role but the administrator's can be changed, is shown.");

            await ClickAsync("#roleRows tr[data-role='publisher'] td.name");
            await WaitForAsync(Shown("#roleCopy") + " && (document.getElementById('roleEditorName').textContent || '').includes('publisher')",
                "A shared built-in role offers no Copy as a custom role, so the page names a way out it does not draw.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#roleSave")), "Save privileges is offered on a built-in role every organization shares.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#roleRows [data-role-delete]')"), "Delete is offered on a built-in role.");
            Assert.True(await Browser.EvaluateAsync<bool>("[...document.querySelectorAll('#rolePrivileges input[data-privilege]')].every(b => b.disabled)"),
                "A shared built-in role's privilege can be ticked.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#roleFixedWhy")), "Nothing beside the ticks says why they are fixed.");
            int publisherHolds = await Browser.EvaluateAsync<int>("document.querySelectorAll('#rolePrivileges input[data-privilege]:checked').length");

            const string Crew = "zz_crew";
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/admin/roles'], true)");
            await ClickAsync("#roleCopy");
            await WaitForAsync("document.getElementById('roleDialog').open && document.activeElement?.id === 'newRoleName'",
                "Copy as a custom role did not open the dialog with the name to give.");
            Assert.Contains("publisher", await Browser.EvaluateAsync<string>("document.getElementById('roleDialogTitle').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            await FilterAsync("newRoleName", Crew);
            await ClickAsync("#roleNewSave");
            await WaitForAsync($"!!document.querySelector('#roleRows tr[data-role={Crew}]') && (document.getElementById('roleEditorName').textContent || '').includes('{Crew}')",
                "The copy was not made and opened.");
            Assert.Equal(publisherHolds, await Browser.EvaluateAsync<int>("document.querySelectorAll('#rolePrivileges input[data-privilege]:checked').length"));
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#roleSave")), "The organization's own role cannot be saved.");
            Assert.True(await Browser.EvaluateAsync<bool>("[...document.querySelectorAll('#rolePrivileges input[data-privilege]')].every(b => !b.disabled)"),
                "The organization's own role has a privilege that cannot be ticked.");
            Assert.Equal(Crew, await Browser.EvaluateAsync<string>("document.activeElement?.dataset?.role || ''"));
            await NothingRefusedAsync("Member roles");

            // New role afterwards is New role again, not a second copy of publisher.
            await ClickAsync("#roleNew");
            await WaitForAsync("document.getElementById('roleDialog').open", "New role did not open.");
            Assert.Equal("New role", await Browser.EvaluateAsync<string>("document.getElementById('roleDialogTitle').textContent"));
            Assert.Contains("starts granting nothing", await Browser.EvaluateAsync<string>("document.getElementById('newRoleHint').textContent") ?? string.Empty,
                StringComparison.Ordinal);
            await ClickAsync("#roleNewCancel");

            // The administrator is not offered as a template, shared or not.
            await ClickAsync("#roleRows tr[data-role='administrator'] td.name");
            await WaitForAsync("(document.getElementById('roleEditorName').textContent || '').includes('administrator')", "The administrator did not open.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('roleCopy')"), "The administrator offers Copy as a custom role.");

            // It is listed where a member is given a role, and only in this organization.
            await OpenOnHostAsync(host, "/studio/#/members", token);
            await WaitForAsync("document.querySelectorAll('#mRole option').length > 0", "Members drew no role choices.");
            Assert.Contains(Crew, (await Browser.EvaluateAsync<string>("[...document.querySelectorAll('#mRole option')].map(o => o.value).join(',')") ?? string.Empty).Split(','));
            (int listed, string roles) = await AdminAsync(HttpMethod.Get, "/admin/roles");
            Assert.True(listed == 200 && !roles.Contains(Crew, StringComparison.Ordinal), $"The default organization sees {Crew}: {listed}.");

            // And deleted from the screen, which nobody holding it allows.
            await OpenOnHostAsync(host, "/studio/#/roles", token);
            await WaitForAsync($"!!document.querySelector('#roleRows [data-role-delete={Crew}]')", $"{Crew} offers no Delete.");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/admin/roles'], true)");
            await ClickAsync($"#roleRows [data-role-delete={Crew}]");
            await WaitForAsync($"!document.querySelector('#roleRows tr[data-role={Crew}]')", $"{Crew} was not deleted.");
            await NothingRefusedAsync("Member roles");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
        }
    }

    [Fact]
    public async Task A_member_of_another_organization_is_offered_nothing_that_makes_or_owns_content_there()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string key = "zzr" + Guid.NewGuid().ToString("N")[..8];
        string outsider = "zz_out_" + Guid.NewGuid().ToString("N")[..8];
        string group = key + "_shared";

        try
        {
            if (await MakeOrganizationAsync(key) is not (string host, string adminToken))
            {
                return;
            }

            (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = outsider, role = "publisher", userType = "creator" }));
            Assert.True(made == 201, $"Making {outsider} answered {made}: {answer}");
            string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;
            // Settled at home, where the account lives; it is a member of this organization only through the group.
            (_, string password) = await SettledTokenAsync(new Uri(Root).Host, outsider, issued);

            (int s, string b) = await AtHostAsync(host, adminToken, HttpMethod.Post, "/admin/groups", new { name = group, title = "Zz shared", itemUpdate = "none" });
            Assert.True(s == 201, $"Making {group} answered {s}: {b}");
            (s, b) = await AtHostAsync(host, adminToken, HttpMethod.Put, $"/admin/groups/{group}/tenants", new { admitsOtherTenants = true });
            Assert.True(s == 200, $"Admitting other organizations to {group} answered {s}: {b}");
            // Named with their organization (ADR-204 §5e item 16): a bare name is one of this organization's members.
            string qualified = $"default\\{outsider}";
            (s, b) = await AtHostAsync(host, adminToken, HttpMethod.Put, $"/admin/groups/{group}/members/{Uri.EscapeDataString(qualified)}", new { });
            Assert.True(s == 200, $"Adding {qualified} to {group} answered {s}: {b}");

            // Admitted, the account signs in at the organization's host — as an outsider, by that name.
            (s, b) = await AtHostAsync(host, null, HttpMethod.Post, "/rest/auth/login", new { name = qualified, password });
            Assert.True(s == 200, $"{outsider} could not sign in at {host}: {s} {b}");
            string token = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

            await OpenOnHostAsync(host, "/studio/#/content", token);
            await WaitForAsync(Shown("#outsiderNotice") + " && !!document.querySelector('#contentRows td')",
                "Content did not draw for a member of another organization.");

            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#newLayer")), "New item is offered to a member of another organization, who holds no privilege here.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#newMap")), "New map is offered to a member of another organization.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#contentRows [data-empty-new]')"),
                "The empty list offers New item or New map to a member of another organization.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#contentFolders")), "Folders are offered to somebody who owns nothing here.");
            Assert.Equal("all,group,public", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#contentScopes a')].map(a => a.getAttribute('href').split('/').pop()).join(',')"));

            await OpenOnHostAsync(host, "/studio/#/groups", token);
            await WaitForAsync($"!!document.querySelector('#view-groups a[href*=\"{group}\"]')", "Groups did not list the group that admits them.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#groupNew")), "New group is offered to a member of another organization.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{outsider}");
        }
    }

    /// <summary>
    /// ADR-221 §5.2: System is the system administrator's. An organization's own administrator at its host is not
    /// offered it, and an address of it sends them to Server with the reason; the system administrator at home is
    /// offered it while a second organization exists, and Organizations is there.
    /// </summary>
    [Fact]
    public async Task System_is_offered_to_the_system_administrator_and_refused_to_an_organizations_own()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string key = "zzr" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (await MakeOrganizationAsync(key) is not (string host, string token))
            {
                return;
            }

            // Their switch has Server and Studio, and no System.
            await OpenOnHostAsync(host, "/server/services", token);
            await WaitForAsync("document.getElementById('view-services')?.classList.contains('on') && !document.getElementById('surfaces').hidden",
                "Server did not draw for an organization's administrator.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#surfaces a[data-surface=system]")),
                "The surface switch offers System to an organization's administrator.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#surfaces a[data-surface=studio]")), "The switch lost Studio.");

            // Typed, System sends them to Server and says why — after the page load, where the reader can read it.
            await OpenOnHostAsync(host, "/system/operations", token);
            await WaitForAsync("location.pathname.startsWith('/server/') && document.getElementById('view-services')?.classList.contains('on')",
                "/system/operations did not send an organization's administrator to Server.");
            await WaitForAsync("(document.getElementById('toast').textContent || '').includes('system administrator')",
                "Nothing said why System was refused.");
            await NothingRefusedAsync("A refused System");

            // The system administrator at home is offered System while there are two organizations, with Organizations.
            (string admin, _) = await SignInAsync();
            await OpenAsync("/server/services", admin);
            await WaitForAsync(Shown("#surfaces a[data-surface=system]"), "The system administrator is not offered System.");
            await ClickAsync("#surfaces a[data-surface=system]");
            await WaitForAsync("location.pathname.startsWith('/system/') && document.getElementById('view-operations')?.classList.contains('on')",
                "System did not open on Status.");
            Assert.Equal("operations,organizations,utilities,settings,logs", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#tabs a')].map(a => a.dataset.tab).join(',')"));

            // Status counts the whole server, not the default organization's listings: two organizations.
            await WaitForAsync("/Organizations\\s*2/.test(document.getElementById('statusCounts').textContent || '')",
                "System › Status does not count the server's two organizations.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#emptySweep")), "System › Status still offers an organization's Empty services.");
            await OpenAsync("/server/organizations", admin);
            await WaitForAsync($"location.pathname === '/system/organizations' && (document.getElementById('tnRows').textContent || '').includes('{key}')",
                "Server › Organizations' old address did not open System › Organizations with the second organization in it.");
            await NothingRefusedAsync("System › Organizations");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
        }
    }

    /// <summary>
    /// ADR-221 §5.5 and condition 3: an organization's administrator names and describes it from Studio › Organization ›
    /// Settings › General, at its own host, and the bar says the new name at once.
    /// </summary>
    [Fact]
    public async Task An_organizations_administrator_renames_and_describes_it_from_studio()
    {
        Assert.Equal(["default"], await TenantKeysAsync());
        string key = "zzr" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            if (await MakeOrganizationAsync(key) is not (string host, string token))
            {
                return;
            }

            await OpenOnHostAsync(host, "/studio/general", token);
            await WaitForAsync(Shown("#orgName") + " && document.getElementById('orgName').value === 'Zz Readers'",
                "General did not offer its administrator the organization's name to change.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('orgProfileSave').disabled"), "Save is offered with nothing changed.");
            Assert.Equal("general", await Browser.EvaluateAsync<string>(
                "document.querySelector('#view-general nav.subpages a[aria-current]')?.getAttribute('href').split('/').pop() || ''"));

            // Empty is said before it is sent, on the box that needs it.
            await FilterAsync("orgName", "");
            await ClickAsync("#orgProfileSave");
            await WaitForAsync("document.getElementById('orgName').getAttribute('aria-invalid') === 'true'"
                + " && (document.getElementById('orgProfileSays').textContent || '').includes('name')",
                "An empty name was not refused on its box.");

            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/admin/organization'], true)");
            await FilterAsync("orgName", "Zz Readers Renamed");
            await FilterAsync("orgDescription", "Surveys for the joint venture.");
            await ClickAsync("#orgProfileSave");
            await WaitForAsync("(document.getElementById('orgProfileSays').textContent || '').startsWith('Saved')",
                "Saving the name and description said nothing.");
            Assert.Equal("Zz Readers Renamed", await Browser.EvaluateAsync<string>("document.getElementById('tenantNameText').textContent"));
            Assert.Equal("orgProfileSays", await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

            (int read, string body) = await AtHostAsync(host, token, HttpMethod.Get, "/admin/organization");
            Assert.True(read == 200, $"GET /admin/organization answered {read}.");
            JsonElement org = JsonDocument.Parse(body).RootElement;
            Assert.Equal("Zz Readers Renamed", org.GetProperty("name").GetString());
            Assert.Equal("Surveys for the joint venture.", org.GetProperty("description").GetString());

            await NothingRefusedAsync("Organization › General");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PurgeAsync(key);
        }
    }
}
