using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-221: the system administrator's surface, `/system/`, on a server of one organization — what moved there from
/// Server and Studio, that every old address of a moved screen still opens it, who is offered it, and Studio ›
/// Organization › Settings › General read by a member who administers nothing.
/// </summary>
/// <remarks>
/// The several-organization cases — an organization's own administrator refused System, and renaming an organization
/// at its own host — are in <see cref="OrganizationReadersTests"/>, which has the helpers that make and purge one.
/// </remarks>
public sealed class SystemSurfaceTests : ConsoleTest
{
    /// <summary>Every request this page made that the server refused with 403.</summary>
    private async Task NothingRefusedAsync(string page)
    {
        string refused = await Browser.EvaluateAsync<string>(
            "(window.__calls || []).filter(c => c.s === 403).map(c => c.u).join(', ')") ?? string.Empty;
        Assert.True(refused.Length == 0, $"{page} asked for something the server refuses this reader: {refused}");
    }

    /// <summary>A member of the default organization with a usable token: issued, replaced, signed in.</summary>
    private async Task<(string Name, string Token)> MemberAsync(string role)
    {
        string name = "zz_sys_" + Guid.NewGuid().ToString("N")[..8];
        (int made, string answer) = await AdminAsync(
            HttpMethod.Post, "/admin/members", JsonSerializer.Serialize(new { name, role, userType = "creator" }));
        Assert.True(made is 200 or 201, $"Adding a {role} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;

        using HttpResponseMessage login = await Http.PostAsync(new Uri($"{Root}/rest/auth/login"),
            new StringContent(JsonSerializer.Serialize(new { name, password = issued }), Encoding.UTF8, "application/json"));
        Assert.True(login.IsSuccessStatusCode, $"Signing in as the new {role} answered {(int)login.StatusCode}.");
        string token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        using HttpRequestMessage replace = new(HttpMethod.Post, new Uri($"{Root}/rest/auth/password"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { currentPassword = issued, newPassword = chosen }),
                Encoding.UTF8, "application/json"),
        };
        replace.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage replaced = await Http.SendAsync(replace);
        Assert.True(replaced.IsSuccessStatusCode, $"Replacing the new {role}'s issued password answered {(int)replaced.StatusCode}.");

        // Signed in again: a session opened on an issued password holds only what replacing it needs.
        using HttpResponseMessage again = await Http.PostAsync(new Uri($"{Root}/rest/auth/login"),
            new StringContent(JsonSerializer.Serialize(new { name, password = chosen }), Encoding.UTF8, "application/json"));
        Assert.True(again.IsSuccessStatusCode, $"Signing in again as the new {role} answered {(int)again.StatusCode}.");
        return (name, JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task System_holds_the_machines_screens_and_server_keeps_the_organizations()
    {
        (string token, _) = await SignInAsync();

        // Server: Services, Data sources, Logs — and the switch offers System.
        await OpenAsync("/server/services", token);
        await WaitForAsync("document.getElementById('view-services')?.classList.contains('on') && !!document.querySelector('#tabs a')",
            "Server did not draw.");
        Assert.Equal("services,sources,logs", await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a')].map(a => a.dataset.tab).join(',')"));
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#surfaces a[data-surface=system]")), "The switch does not offer System.");

        // Server's Logs are the organization's three; the server's process log is System's alone.
        await OpenAsync("/server/logs", token);
        await WaitForAsync("!!document.querySelector('#logSources [data-log-source=audit]')", "Server › Logs drew no sources.");
        Assert.Equal("audit,requests,studio", await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#logSources [data-log-source]')].map(b => b.dataset.logSource).join(',')"));
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#logLede a[href='/system/logs']")), "Server › Logs does not say where the server's own log went.");

        // System: its five tabs, Status first, and the health pill opens it.
        await OpenAsync("/system/", token);
        await WaitForAsync("document.getElementById('view-operations')?.classList.contains('on')", "/system/ did not open on Status.");
        Assert.Equal("operations,organizations,utilities,machines,settings,backups,logs", await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a')].map(a => a.dataset.tab).join(',')"));
        Assert.Equal("System", await Browser.EvaluateAsync<string>("document.getElementById('surfaceBadge').textContent"));
        Assert.Equal("page", await Browser.EvaluateAsync<string>("document.querySelector('#surfaces a[data-surface=system]').getAttribute('aria-current')"));
        Assert.Equal("/system/operations", await Browser.EvaluateAsync<string>("new URL(document.getElementById('healthDot').href).pathname"));
        await WaitForAsync(Shown("#datastoreAct [data-source-quiesce], #datastoreAct [data-source-resume]"),
            "Status offers the managed datastore no Pause: it left Data sources for here.");

        // Utility services: the server's own services with their verbs, and the geometry service clients use.
        await OpenAsync("/system/utilities", token);
        await WaitForAsync("!!document.querySelector('#sysRows [data-system-status=Geometry]')", "Utility services listed no Geometry service with Start or Stop.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#sysRows select[data-service-share=Geometry]")), "The Geometry service's sharing is not offered.");
        await WaitForAsync("!document.getElementById('geometryService').disabled", "The geometry service clients use was never read.");

        // General: the page size, style sources and, from Studio's Map page, the map ground with its own Save.
        await OpenAsync("/system/settings", token);
        await WaitForAsync("document.querySelectorAll('#groundList input[type=checkbox]').length > 0", "The map ground is not on System › General.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#setPageSize")), "The page size is not on System › General.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#styleOrigins")), "Style sources are not on System › General.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('groundSave').disabled"), "The ground's Save is on with nothing changed.");

        // System › Logs is the server's process log, with nothing to choose between.
        await OpenAsync("/system/logs", token);
        await WaitForAsync("(document.getElementById('logLede').textContent || '').includes('Server warnings')", "System › Logs is not the server's log.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#logSources")), "System › Logs offers a choice of one log.");

        // The server's own service opens here with Stop, and its limits can be changed; in Server it is read.
        await OpenAsync("/system/service/Utilities/Geometry", token);
        await WaitForAsync(Shown("#serverHero [data-system-status]") + " && " + Shown("#limDeadline"),
            "System's Geometry page offers no Stop or no limits.");
        Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('limDeadline').disabled"), "The limits cannot be changed in System.");
        await OpenAsync("/server/service/Utilities/Geometry", token);
        await WaitForAsync(Shown("#limDeadline"), "Server's Geometry page shows no limits.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('limDeadline').disabled"), "Server's Geometry page sets the server's limits.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#serverHero [data-system-status]")), "Server's Geometry page offers Stop.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#limNote a")), "Nothing on Server's Geometry page says where its limits are set.");

        await NothingRefusedAsync("System");
        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Every address a moved screen had opens it in its new place — ADR-221 §5.6.</summary>
    [Theory]
    [InlineData("/server/operations", "/system/operations", "view-operations")]
    [InlineData("/server/#/operations", "/system/operations", "view-operations")]
    [InlineData("/server/settings", "/system/settings", "view-settings")]
    [InlineData("/server/organizations", "/system/organizations", "view-organizations")]
    [InlineData("/studio/utilities", "/system/utilities", "view-utilities")]
    [InlineData("/server/logs?source=server", "/system/logs", "view-logs")]
    public async Task An_old_address_of_a_moved_screen_opens_it_in_system(string old, string now, string view)
    {
        (string token, _) = await SignInAsync();
        await OpenAsync(old, token);
        await WaitForAsync($"location.pathname === '{now}' && document.getElementById('{view}')?.classList.contains('on')",
            $"{old} did not open {now}.");
        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-221 §5.2, INFERRED: on a server of one organization, any holder of admin:manageServer enters System — the
    /// reader who saw these screens in Server — and is not shown Organizations, which is the system administrator's.
    /// </summary>
    /// <remarks>
    /// <b>The fixture's administrator with the flag edited out of <c>/rest/whoami</c></b>, because a member made here
    /// has the default user type, <c>creator</c>, whose ceiling never administers: given the administrator role it
    /// answers nine content and sharing privileges and no <c>admin:*</c> (measured 2026-10-27). That is by design —
    /// Portal's split of user type and role; an administrator needs the <c>unrestricted</c> type. The console decides
    /// from <c>whoami</c>, which is what is varied.
    /// </remarks>
    [Fact]
    public async Task On_a_server_of_one_an_administrator_who_is_not_the_system_administrator_enters_system()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/content", token);
        await Browser.PlantAsync("""
            (() => {
              const inner = window.fetch;
              window.fetch = async (input, init) => {
                const response = await inner(input, init);
                const url = typeof input === "string" ? input : input.url;
                if (!/\/rest\/whoami/.test(url) || !response.ok) return response;
                const me = await response.clone().json();
                me.systemAdministrator = false;
                return new Response(JSON.stringify(me), { status: 200, headers: { "Content-Type": "application/json" } });
              };
            })();
            """);

        await OpenAsync("/server/services", token);
        // The services view is `on` in the markup before any script runs, so the wait is for the bar the router draws.
        await WaitForAsync("document.querySelectorAll('#tabs a').length > 0 && typeof whoNow !== 'undefined' && !!whoNow",
            "Server did not draw.");
        if (await Browser.EvaluateAsync<bool>("typeof whoNow !== 'undefined' && !!whoNow?.organisation?.multiTenant"))
        {
            return; // The fixture holds a second organization: that is OrganizationReadersTests' case, not this one.
        }

        Assert.False(await Browser.EvaluateAsync<bool>("typeof whoNow !== 'undefined' && !!whoNow?.systemAdministrator"),
            "whoami still says system administrator, so this test measures the wrong reader.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#surfaces a[data-surface=system]")),
            "A holder of admin:manageServer on a server of one is not offered System.");

        await OpenAsync("/system/settings", token);
        await WaitForAsync("document.getElementById('view-settings')?.classList.contains('on') && document.getElementById('setPageSizeSource').textContent.length > 0",
            "System › General did not open for an administrator on a server of one.");
        Assert.Equal("operations,utilities,machines,settings,backups,logs", await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a')].map(a => a.dataset.tab).join(',')"));
        await NothingRefusedAsync("System › General");
        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-221 §5.5: every member reads the organization's name and description; one who administers nothing sees
    /// Organization as that page alone, read-only, and is offered neither System nor Server.
    /// </summary>
    [Fact]
    public async Task A_member_reads_the_organizations_name_and_cannot_change_it()
    {
        (string name, string token) = await MemberAsync("user");

        try
        {
            await OpenAsync("/studio/content", token);
            await WaitForAsync("document.getElementById('view-content')?.classList.contains('on')", "Studio did not draw for a member.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#tabs a[data-tab=members]")), "A member is not shown Organization.");
            Assert.EndsWith("/studio/general", await Browser.EvaluateAsync<string>("document.querySelector('#tabs a[data-tab=members]').href") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#surfaces")), "A member is offered a surface switch.");

            // Members is the administrators': asked for, it opens General.
            await OpenAsync("/studio/members", token);
            await WaitForAsync(Shown("#orgNameShown") + " && (document.getElementById('orgNameShown').textContent || '').length > 0",
                "A member was not shown the organization's name.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#orgName")), "A member is offered the name to change.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#orgProfileSave")), "A member is offered Save.");
            Assert.Equal("general", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#view-general nav.subpages a')].map(a => a.getAttribute('href').split('/').pop()).join(',')"));
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#view-general nav.orgstrip a[href$=\"members\"]')"),
                "A member is offered Members.");

            (int read, string body) = await AdminAsync(HttpMethod.Get, "/admin/organization");
            Assert.True(read == 200, $"GET /admin/organization answered {read}.");
            Assert.Equal(JsonDocument.Parse(body).RootElement.GetProperty("name").GetString(),
                await Browser.EvaluateAsync<string>("document.getElementById('orgNameShown').textContent"));

            // And System, typed, sends them to Studio with the reason.
            await OpenAsync("/system/", token);
            await WaitForAsync("location.pathname.startsWith('/studio/') && (document.getElementById('toast')?.textContent || '').includes('system administrator')",
                "A member who typed /system/ was not sent to Studio with the reason.");

            await NothingRefusedAsync("Organization › General");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{name}?deleteOwned=true");
        }
    }
}
