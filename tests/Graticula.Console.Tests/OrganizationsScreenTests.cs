using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Server › Organizations, and what Studio says about organizations — ADR-204 §5c, §5d: the system administrator lists,
/// makes, changes, suspends and removes organizations; an organization's administrator lets a group admit members of
/// other organizations, which is offered only where the server holds more than one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Visible as the reader who needs it, not as the DOM has it.</b> Three controls in this console existed and could
/// not be seen while their tests were green — one inside a closed <c>details</c>. So a control here is asserted to have
/// a box, to be scrolled into the window, and to be the element the browser would hit at its own centre.
/// </para>
/// <para>
/// <b>A test that makes an organization removes it in <c>finally</c>, with everything it owns.</b> While a second
/// organization exists the whole server answers differently — every host it does not list goes to the default, Server
/// is the system administrator's alone — and every other test in this suite would be measuring that instead.
/// </para>
/// </remarks>
public sealed class OrganizationsScreenTests : ConsoleTest
{
    /// <summary>True when the control is on screen and is what a pointer at its centre would reach.</summary>
    private static string Reachable(string selector)
    {
        string quoted = JsonSerializer.Serialize(selector);

        return $$"""
            (() => {
              const e = document.querySelector({{quoted}});
              if (!e || e.getClientRects().length === 0) return false;
              e.scrollIntoView({ block: "center" });
              const r = e.getBoundingClientRect();
              if (r.width < 1 || r.height < 1) return false;
              const s = getComputedStyle(e);
              if (s.visibility === "hidden" || Number(s.opacity) === 0) return false;
              const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
              return !!hit && (hit === e || e.contains(hit) || hit.contains(e) && hit.tagName === "LABEL");
            })()
            """;
    }

    private async Task<bool> SystemAdministratorAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/rest/whoami");
        Assert.True(status == 200, $"whoami answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.TryGetProperty("systemAdministrator", out JsonElement flag)
            && flag.ValueKind == JsonValueKind.True;
    }

    private async Task<string[]> TenantKeysAsync()
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.True(status == 200, $"GET /admin/tenants answered {status}: {body}");
        return
        [
            .. JsonDocument.Parse(body).RootElement.GetProperty("tenants").EnumerateArray()
                .Select(t => t.GetProperty("key").GetString()!),
        ];
    }

    [Fact]
    public async Task The_system_administrator_finds_organizations_in_systems_bar_and_it_lists_the_default()
    {
        (string token, _) = await SignInAsync();

        Assert.True(
            await SystemAdministratorAsync(),
            "The configured user is not the system administrator, so Organizations is not theirs to see and this test "
            + "cannot ask about it. The first administrator of a deployment is the system administrator (ADR-204 §5d).");

        Assert.Equal(["default"], await TenantKeysAsync());

        // System's bar since ADR-221 (Server's until then).
        await OpenAsync("/system/", token);

        await WaitForAsync(Reachable("#tabs a[data-tab=organizations]"), "System's bar offers no Organizations tab to the system administrator.");
        await ClickAsync("#tabs a[data-tab=organizations]");

        await WaitForAsync(
            "document.getElementById('view-organizations')?.classList.contains('on')"
            + " && !!document.querySelector('#tnRows tr[data-tn=default]')",
            "Organizations opened without the default organization in its list.");

        Assert.Equal("organizations", await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#tabs a[aria-current]')].map(a => a.dataset.tab).join(',')"));

        string row = await Browser.EvaluateAsync<string>(
            "document.querySelector('#tnRows tr[data-tn=default]').textContent.replace(/\\s+/g, ' ')") ?? string.Empty;

        Assert.Contains("Default", row, StringComparison.Ordinal);
        Assert.Contains("Active", row, StringComparison.Ordinal);
        Assert.Contains("Every host name no other organization lists", row, StringComparison.Ordinal);

        // The server's own organization is changed and never suspended or removed: the server refuses both.
        Assert.True(await Browser.EvaluateAsync<bool>(Reachable("[data-tn-edit=default]")), "Change is not reachable on the default organization.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('[data-tn-suspend=default], [data-tn-remove=default]')"),
            "The default organization offers Suspend or Remove, which the server refuses.");

        Assert.True(await Browser.EvaluateAsync<bool>(Reachable("#tnNew")), "New organization is not reachable on the first-run screen.");

        // What an organization is, and what a host name needs, said on the screen itself.
        string head = await Browser.EvaluateAsync<string>("document.querySelector('#view-organizations .pagehead p').textContent") ?? string.Empty;
        Assert.Contains("host name", head, StringComparison.Ordinal);
        Assert.Contains("DNS", head, StringComparison.Ordinal);

        // A server of one organization names none in the bar: there is nothing to tell apart.
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#tenantName")), "The bar names an organization on a server that holds one.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task Creating_an_organization_shows_its_administrators_password_once_and_where_to_sign_in()
    {
        (string token, _) = await SignInAsync();
        Assert.True(await SystemAdministratorAsync(), "The configured user is not the system administrator.");
        Assert.Equal(["default"], await TenantKeysAsync());

        string key = "zzt" + Guid.NewGuid().ToString("N")[..8];
        string host = $"{key}.localhost";
        string administrator = $"{key}_admin";
        int port = new Uri(Root).Port;

        try
        {
            await OpenAsync("/server/#/organizations", token);
            await WaitForAsync("!!document.querySelector('#tnRows tr[data-tn=default]')", "Organizations listed nothing.");

            // <b>The server's own answer, not one written here.</b> The harness answers every write with `{}`, in every
            // frame, so a click cannot change the server — and the password exists only in the answer to the create. So
            // the organization is made through the API with the body the form will send, and the page's trapped POST is
            // given that real 201. What the screen sends is recorded and compared, so a form that sent something else
            // still fails.
            string sent = JsonSerializer.Serialize(new { key, name = "Zz Console Test", hosts = new[] { host }, administrator });
            (int created, string answer) = await AdminAsync(HttpMethod.Post, "/admin/tenants", sent);

            // A store whose role the floor cannot bind refuses a second organization, saying so (ADR-204 condition 9).
            if (created == 409 && answer.Contains("superuser", StringComparison.Ordinal))
            {
                return;
            }

            Assert.True(created == 201, $"Making {key} answered {created}: {answer}");

            Assert.True(await Browser.EvaluateAsync<bool>(
                $$"""
                (() => {
                  const answer = {{JsonSerializer.Serialize(answer)}};
                  const trapped = window.fetch;
                  window.__tenantPosts = [];
                  window.fetch = (input, init) => {
                    const url = typeof input === "string" ? input : input.url;
                    const method = ((init && init.method) || "GET").toUpperCase();
                    if (method !== "POST" || !/\/admin\/tenants$/.test(url)) return trapped(input, init);
                    window.__tenantPosts.push(String(init.body));
                    return Promise.resolve(new Response(answer, { status: 201, headers: { "Content-Type": "application/json" } }));
                  };
                  return true;
                })()
                """), "Could not stand in for the create's answer.");

            await ClickAsync("#tnNew");
            await WaitForAsync(Reachable("#tnName"), "New organization opened no form with a reachable Name.");

            await Browser.EvaluateAsync<bool>(
                $$"""
                (() => {
                  const set = (id, v) => { const f = document.getElementById(id); f.value = v; f.dispatchEvent(new Event("input", { bubbles: true })); };
                  set("tnName", "Zz Console Test");
                  set("tnKey", {{JsonSerializer.Serialize(key)}});
                  set("tnHosts", {{JsonSerializer.Serialize(host)}});
                  set("tnAdmin", {{JsonSerializer.Serialize(administrator)}});
                  return true;
                })()
                """);

            await ClickAsync("#tnSave");

            await WaitForAsync(
                "document.getElementById('tnIssued')?.open && document.getElementById('tnIssuedPassword').value.length > 0",
                "Making an organization did not show its administrator's password. The form said: "
                + "(read #tnFormSays in the diagnosis below)");

            // The form sent exactly what the organization was made with.
            using (JsonDocument posted = JsonDocument.Parse(await Browser.EvaluateAsync<string>("window.__tenantPosts[0] || '{}'") ?? "{}"))
            {
                JsonElement p = posted.RootElement;
                Assert.Equal(key, p.GetProperty("key").GetString());
                Assert.Equal("Zz Console Test", p.GetProperty("name").GetString());
                Assert.Equal([host], p.GetProperty("hosts").EnumerateArray().Select(x => x.GetString()!).ToArray());
                Assert.Equal(administrator, p.GetProperty("administrator").GetString());
            }

            Assert.True(await Browser.EvaluateAsync<bool>(Reachable("#tnIssuedPassword")), "The issued password is not on screen.");
            Assert.True(await Browser.EvaluateAsync<bool>(Reachable("#tnIssuedCopy")), "The password's Copy button is not reachable.");
            Assert.Equal("tnIssuedPassword", await Browser.EvaluateAsync<string>("document.activeElement?.id"));

            string password = await Browser.EvaluateAsync<string>("document.getElementById('tnIssuedPassword').value") ?? string.Empty;
            Assert.True(password.Length >= 8, "The issued password is implausibly short.");

            string once = await Browser.EvaluateAsync<string>("document.getElementById('tnIssuedOnce').textContent.replace(/\\s+/g, ' ')") ?? string.Empty;
            Assert.Contains("shown only now", once, StringComparison.Ordinal);
            Assert.Contains(administrator, once, StringComparison.Ordinal);
            Assert.Contains("changed the first time they sign in", once, StringComparison.Ordinal);

            Assert.Equal($"https://{host}:{port}/studio/", await Browser.EvaluateAsync<string>("document.getElementById('tnIssuedAt').value"));

            await ClickAsync("#tnIssuedDone");

            await WaitForAsync(
                "!document.getElementById('tnIssued').open && !!document.querySelector(" + JsonSerializer.Serialize($"#tnRows tr[data-tn={key}]") + ")",
                "Closing the password did not leave the new organization in the list.");

            // <b>Once.</b> Closed, the secret is nowhere in the page — not in the field, not in any attribute or text.
            Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.getElementById('tnIssuedPassword').value"));
            Assert.False(
                await Browser.EvaluateAsync<bool>($"document.documentElement.outerHTML.includes({JsonSerializer.Serialize(password)})"),
                "The issued password is still in the page after its dialog was closed.");

            Assert.Equal($"Change Zz Console Test", await Browser.EvaluateAsync<string>("document.activeElement?.getAttribute('aria-label')"));
            Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('tnSays').getAttribute('aria-live')"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if ((await TenantKeysAsync()).Contains(key))
            {
                (int purged, string said) = await AdminAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true");
                Assert.True(purged == 200, $"Purging {key} answered {purged}: {said}. The server still holds a second organization.");
            }
        }
    }

    [Fact]
    public async Task On_a_server_of_one_organization_a_group_offers_no_admission_of_other_organizations()
    {
        (string token, _) = await SignInAsync();
        Assert.Equal(["default"], await TenantKeysAsync());

        string name = "zz_one_org_" + Guid.NewGuid().ToString("N")[..8];
        (int made, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/groups",
            JsonSerializer.Serialize(new { name, title = "Zz one organization", itemUpdate = "none" }));
        Assert.True(made == 201, $"Making {name} answered {made}: {body}");

        try
        {
            await OpenAsync($"/studio/#/group/{name}/settings", token);

            // The settings an owner does see are drawn, so an absence below is an absence and not a page still loading.
            await WaitForAsync(Reachable("#gsTitle"), "The group's Settings did not draw.");
            await WaitForAsync(Reachable("#gsLock"), "The group's Settings drew no deletion card.");

            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('gsAdmits')"),
                "A server of one organization offers to admit members of other organizations, which the server refuses (409).");
            Assert.DoesNotContain("other organizations",
                await Browser.EvaluateAsync<string>("document.getElementById('groupSettings').textContent") ?? string.Empty,
                StringComparison.Ordinal);

            // Nor does Add member offer a member of another organization.
            await OpenAsync($"/studio/#/group/{name}/members", token);
            await WaitForAsync(Reachable("#groupAdd"), "Members offers no Add member to the group's owner.");
            await ClickAsync("#groupAdd");
            await Task.Delay(800);
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('groupPickOther')"),
                "Add member offers a member of another organization on a server of one.");

            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#outsiderNotice")), "A member of this organization is told they are an outsider.");
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#tenantName")), "The bar names an organization on a server that holds one.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{name}");
        }
    }
}
