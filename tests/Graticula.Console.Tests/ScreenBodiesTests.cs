using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The bodies of the screens, rebuilt to their mockups on 2026-11-02 (ADR-255 §6): System's General with one save bar, Status
/// as a verdict and a grid, Backups in two columns, Logs as one filter row and, on a phone, as blocks; Security's sections as
/// sub-pages; a group's head holding Add items and Add member; Groups, Members, a profile, Member roles and a file's page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each assertion reads a mechanism</b> — where a node is, what it measures, what a press reaches — rather than the
/// fixture's data, as <see cref="DesktopLayoutTests"/> does. The wide layout is the harness's own width (1,440); the phone is
/// 390 × 844 with <c>mobile</c> on, as in <see cref="PhoneLayoutTests"/>.
/// </para>
/// </remarks>
public sealed class ScreenBodiesTests : ConsoleTest
{
    private const string Probe = "screen_bodies_probe";

    private async Task PhoneAsync() => await Browser.CallAsync(
        "Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });

    private async Task WidenAsync() => await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");

    /// <summary>
    /// System's General has one save bar: the three cards' own Save buttons are not drawn, the bar counts what has been typed,
    /// and Discard puts the boxes back; a bar's Save is on only while something is waiting.
    /// </summary>
    [Fact]
    public async Task System_general_has_one_save_bar_that_counts_what_is_waiting()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/#/settings", token);

        await WaitForAsync(
            "document.getElementById('generalSaveBar')?.getClientRects().length > 0 && !document.getElementById('styleOrigins').disabled",
            "System's General never drew its save bar.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const saves = [...document.querySelectorAll('#view-settings button')]
                    .filter(b => b.getClientRects().length > 0 && /^Save/.test(b.textContent.trim()));
                  return saves.length === 1 && saves[0].id === 'generalSave' && saves[0].disabled
                    && document.getElementById('generalDirty').textContent === 'No unsaved changes';
                })()
                """),
            "General does not have exactly one Save, off with nothing to save.");

        // Typed into two of the three cards, the bar counts two.
        await Browser.EvaluateAsync<bool>(
            """
            (() => {
              const size = document.getElementById('setPageSize');
              size.value = '777'; size.dispatchEvent(new Event('input', { bubbles: true }));
              const origins = document.getElementById('styleOrigins');
              origins.value = 'https://tiles.example.com'; origins.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('generalDirty').textContent === '2 unsaved changes' && !document.getElementById('generalSave').disabled"),
            "The bar did not count the two edited cards, or Save stayed off.");

        await ClickAsync("#generalDiscard");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => document.getElementById('setPageSize').value === document.getElementById('setPageSize').dataset.was
                  && document.getElementById('styleOrigins').value === document.getElementById('styleOrigins').dataset.was
                  && document.getElementById('generalSave').disabled
                  && document.activeElement.id === 'generalDirty')()
                """),
            "Discard did not put the boxes back, switch Save off and keep focus on the bar's sentence.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Status is a verdict card with its counts on the left and its services on the right, then cards in a grid of three: the
    /// first two cards share a row, and a deliberate stop is drawn in the warning colour, not the fault's.
    /// </summary>
    [Fact]
    public async Task System_status_is_a_verdict_and_a_grid_of_cards()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);

        await WaitForAsync(
            "document.querySelectorAll('#view-operations .opscard').length >= 3 && document.getElementById('statusCounts').children.length >= 4",
            "Status never drew its verdict and cards.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const cards = [...document.querySelectorAll('#view-operations > .opscards > .opscard, #view-operations #resourceWidget')]
                    .filter(c => c.getClientRects().length > 0);
                  const tops = new Set(cards.map(c => Math.round(c.getBoundingClientRect().top)));
                  const verdict = getComputedStyle(document.getElementById('statusVerdict'));
                  return cards.length >= 3 && tops.size < cards.length && verdict.display === 'grid';
                })()
                """),
            "Status's cards are not in a grid under a two-column verdict.");

        // A deliberate stop is amber and a fault red: the rule that decides it is the two classes' own colours.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const probe = (cls) => { const d = document.createElement('div'); d.className = cls;
                    d.innerHTML = '<dt>x</dt><dd>0</dd>'; const l = document.createElement('dl'); l.className = 'verdictcounts';
                    l.appendChild(d); document.getElementById('statusVerdict').appendChild(l);
                    const c = getComputedStyle(d.querySelector('dd')).color; l.remove(); return c; };
                  return probe('bad') !== probe('warn');
                })()
                """),
            "A stopped service is drawn in the fault's colour.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Backups' schedule is to the right of the list at 1,440.</summary>
    [Fact]
    public async Task System_backups_has_the_schedule_beside_the_list()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/system/operations", token);
        await Browser.EvaluateAsync<bool>("(navigateTo('#/backups'), true)");

        await WaitForAsync(
            "document.getElementById('bkScheduleCard')?.getClientRects().length > 0 && !!document.querySelector('#view-backups .panel.widetable')",
            "System has no Backups screen.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const list = document.querySelector('#view-backups .panel.widetable').getBoundingClientRect();
                  const card = document.getElementById('bkScheduleCard').getBoundingClientRect();
                  return card.left >= list.right - 1 && card.width >= 300;
                })()
                """),
            "The schedule is not to the right of the list of backups.");
    }

    /// <summary>
    /// Logs on a wide window: the log being read is a segmented control in the head, the filters are one row, and the paragraph that
    /// says which log is which is a footnote under the table, where its link to System › Logs is kept.
    /// </summary>
    [Fact]
    public async Task Logs_has_the_source_in_the_head_and_one_row_of_filters()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/logs", token);

        await WaitForAsync("document.querySelectorAll('#logSources [role=tab]').length >= 2 && !!document.getElementById('logText')", "Logs drew no sources.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const h1 = document.querySelector('#view-logs h1').getBoundingClientRect();
                  const src = document.getElementById('logSources').getBoundingClientRect();
                  const tabs = [...document.querySelectorAll('#logSources [role=tab]')].map(b => b.getBoundingClientRect());
                  const tops = ['logText', 'logWho', 'logSince'].map(id => document.getElementById(id).getBoundingClientRect().top);
                  const table = document.querySelector('#view-logs .widetable').getBoundingClientRect();
                  const lede = document.getElementById('logLede');
                  return src.top < h1.bottom + 20 && src.left > h1.right && tabs.every(t => Math.abs(t.top - tabs[0].top) < 2)
                    && Math.max(...tops) - Math.min(...tops) < 12 && table.top > src.bottom
                    && lede.getClientRects().length > 0 && lede.getBoundingClientRect().top >= table.bottom - 1;
                })()
                """),
            "Logs does not have its source in the head and its filters in one row.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Logs on a phone: the filters are folded behind a 44-pixel button that opens them, and an entry is a block of When, What,
    /// Who · From and Resource.
    /// </summary>
    [Fact]
    public async Task Logs_on_a_phone_folds_its_filters_and_draws_blocks()
    {
        (string token, _) = await SignInAsync();
        await PhoneAsync();

        try
        {
            await OpenAsync("/server/#/logs", token);

            await WaitForAsync("document.querySelectorAll('#logRows tr.logrow').length > 0", "The audit trail listed nothing at 390.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const f = document.getElementById('logFilters');
                      const t = document.getElementById('logFiltersToggle');
                      return f.getClientRects().length === 0 && t.getClientRects().length > 0
                        && t.getBoundingClientRect().height >= 44 && t.getAttribute('aria-expanded') === 'false';
                    })()
                    """),
                "The filters are not folded behind a Filters button of 44 pixels.");

            await ClickAsync("#logFiltersToggle");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "document.getElementById('logFilters').getClientRects().length > 0 && document.getElementById('logFiltersToggle').getAttribute('aria-expanded') === 'true'"),
                "The Filters button did not open the filters.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const row = document.querySelector('#logRows tr.logrow');
                      const labels = [...row.querySelectorAll('td[data-l]')].filter(c => c.getClientRects().length > 0)
                        .map(c => c.dataset.l);
                      const thead = document.querySelector('#view-logs thead').getBoundingClientRect();
                      return getComputedStyle(row).display === 'grid' && labels.join('|') === 'When|What|Who · From|Resource'
                        && thead.width <= 1 && document.documentElement.scrollWidth <= 391;
                    })()
                    """),
                "An entry is not a block of When, What, Who · From and Resource, or the page scrolls sideways.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await WidenAsync();
        }
    }

    /// <summary>
    /// Security's four sections are sub-pages: one is drawn at a time under a segmented control, the pressed one is marked, and
    /// the old Apps address opens that one.
    /// </summary>
    [Fact]
    public async Task Security_shows_one_section_at_a_time_under_a_switch()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/signin", token);

        await WaitForAsync(
            "document.getElementById('view-signin').getClientRects().length > 0 && [...document.querySelectorAll('#secSwitch button')].filter(b => !b.hidden).length >= 2",
            "Security never drew its switch.");

        const string Shown = """
            [...document.querySelectorAll('#view-signin .secpage')].filter(s => s.getClientRects().length > 0).map(s => s.dataset.secpage).join(',')
            """;

        string first = await Browser.EvaluateAsync<string>(Shown) ?? string.Empty;
        Assert.DoesNotContain(",", first, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, first);

        await ClickAsync("[data-secpage-go=apps]");

        Assert.Equal("apps", await Browser.EvaluateAsync<string>(Shown));
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector('[data-secpage-go=apps]').getAttribute('aria-pressed') === 'true' "
                + "&& document.querySelector('[data-secpage-go=idp]').getAttribute('aria-pressed') === 'false' "
                + "&& document.getElementById('appNew').getClientRects().length > 0"),
            "The Apps section's switch is not marked, or its Register button is not drawn.");

        await ClickAsync("[data-secpage-go=idp]");
        Assert.Equal("idp", await Browser.EvaluateAsync<string>(Shown));

        // The old address of Apps opens that section.
        await OpenAsync("/studio/#/apps", token);
        await WaitForAsync("document.getElementById('view-signin').dataset.secpage === 'apps'", "/apps did not open Security on its Apps section.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A group's head holds Add items (the address of the Content tab's own link) and Add member (which opens the one picker),
    /// from every tab; on a phone they are side by side at 44 pixels.
    /// </summary>
    [Fact]
    public async Task A_groups_head_holds_add_items_and_add_member()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));
        Assert.True(made is 200 or 201, $"{made} {why}");

        try
        {
            await OpenAsync($"/studio/#/group/{Probe}", token);

            await WaitForAsync(
                "!document.getElementById('groupHeadVerbs').hidden && document.getElementById('groupHeadAdd').getClientRects().length > 0",
                "The group's head drew no verbs.");

            string head = await Browser.EvaluateAsync<string>(
                $$"""
                (() => {
                  const head = document.getElementById('groupPagehead').getBoundingClientRect();
                  const share = document.getElementById('groupHeadShare');
                  const add = document.getElementById('groupHeadAdd');
                  const inside = [share, add].every(e => { const r = e.getBoundingClientRect(); return r.top >= head.top - 1 && r.bottom <= head.bottom + 1; });
                  const checks = {
                    inside,
                    texts: share.textContent.trim() === 'Add items' && add.textContent.trim() === 'Add member',
                    href: share.getAttribute('href'),
                    tile: document.getElementById('groupHeadTile').textContent,
                    counts: [...document.querySelectorAll('#groupTabs a')].some(a => a.querySelector('.count')),
                  };
                  return JSON.stringify(checks);
                })()
                """) ?? string.Empty;

            JsonElement checks = JsonDocument.Parse(head).RootElement;
            Assert.True(
                checks.GetProperty("inside").GetBoolean() && checks.GetProperty("texts").GetBoolean()
                && (checks.GetProperty("href").GetString() ?? string.Empty).EndsWith($"/group/{Probe}/add", StringComparison.Ordinal)
                && checks.GetProperty("tile").GetString() == "P" && checks.GetProperty("counts").GetBoolean(),
                $"The group's head does not hold Add items and Add member with the tile and the tabs' counts: {head}");

            // Pressed from the Overview tab, Add member opens the picker and Cancel gives the focus back to the head's button.
            await ClickAsync("#groupHeadAdd");
            await WaitForAsync("!document.getElementById('groupPicker').hidden", "Add member opened no picker.");
            await ClickAsync("#groupPickCancel");

            Assert.True(
                await Browser.EvaluateAsync<bool>("document.activeElement.id === 'groupHeadAdd'"),
                "Cancelling the picker did not return the focus to Add member.");

            await PhoneAsync();
            await OpenAsync($"/studio/#/group/{Probe}", token);
            await WaitForAsync("document.getElementById('groupHeadAdd').getClientRects().length > 0", "The phone's head drew no verbs.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const a = document.getElementById('groupHeadShare').getBoundingClientRect();
                      const b = document.getElementById('groupHeadAdd').getBoundingClientRect();
                      return a.height >= 44 && b.height >= 44 && Math.abs(a.top - b.top) < 2 && b.left >= a.right - 1
                        && document.documentElement.scrollWidth <= 391;
                    })()
                    """),
                "On a phone the head's two verbs are not side by side at 44 pixels.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await WidenAsync();
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }

    /// <summary>
    /// Groups is a table with its New group and its search in the head's row, at the right, and Members has its filters on the
    /// left of a compact table.
    /// </summary>
    [Fact]
    public async Task Groups_and_members_have_the_verbs_in_the_head_and_the_filters_on_the_left()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/groups", token);

        await WaitForAsync("document.getElementById('groupNew')?.getClientRects().length > 0 && !!document.querySelector('#view-groups table')", "Groups never drew.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const h1 = document.querySelector('#view-groups h1').getBoundingClientRect();
                  const add = document.getElementById('groupNew').getBoundingClientRect();
                  const find = document.getElementById('groupFilter').getBoundingClientRect();
                  const table = document.querySelector('#view-groups table').getBoundingClientRect();
                  return add.top < h1.bottom + 20 && find.left > h1.right && add.left > h1.right && table.top > add.bottom
                    && getComputedStyle(document.querySelector('#view-groups table')).display === 'table';
                })()
                """),
            "Groups' New group and search are not in the head's row over a table.");

        await OpenAsync("/studio/#/members", token);
        await WaitForAsync("document.querySelectorAll('#members tr').length > 0", "Members listed nothing.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const side = document.getElementById('memberSide').getBoundingClientRect();
                  const main = document.querySelector('#view-members .membersmain').getBoundingClientRect();
                  const rows = [...document.querySelectorAll('#members tr')].filter(r => r.getClientRects().length > 0);
                  const bar = document.querySelector('#view-members .membersmain > .toolbar').getBoundingClientRect();
                  return side.right <= main.left + 1 && side.width >= 180 && rows.every(r => r.getBoundingClientRect().height <= 90)
                    && document.getElementById('memberNew').getBoundingClientRect().left < bar.left + bar.width / 2;
                })()
                """),
            "Members' filters are not on the left of a compact table, or New member is not first in its row.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A profile is two columns at 1,440 — who, and what is known of them, on the left; the form and the second factor on the
    /// right — and one column on a phone.
    /// </summary>
    [Fact]
    public async Task A_profile_is_two_columns_wide_and_one_on_a_phone()
    {
        (string token, string who) = await WhoAsync();
        await OpenAsync($"/studio/#/user/{who}", token);

        await WaitForAsync("!!document.getElementById('pfSave') && document.querySelector('#view-user .profilefacts')", "The profile never drew its form.");

        const string Columns = """
            (() => {
              const head = document.querySelector('#view-user .profilehead').getBoundingClientRect();
              const facts = document.querySelector('#view-user .profilefacts').getBoundingClientRect();
              const form = document.querySelector('#view-user .profileform').getBoundingClientRect();
              return { head: head.left, facts: facts.left, form: form.left, formTop: form.top, headBottom: head.bottom, factsBottom: facts.bottom };
            })()
            """;

        JsonElement wide = await Browser.EvaluateAsync<JsonElement>(Columns);
        Assert.True(wide.GetProperty("form").GetDouble() > wide.GetProperty("head").GetDouble() + 300, "The form is not to the right of who it is about.");
        Assert.Equal(wide.GetProperty("head").GetDouble(), wide.GetProperty("facts").GetDouble(), 1);

        await PhoneAsync();

        try
        {
            await OpenAsync($"/studio/#/user/{who}", token);
            await WaitForAsync("!!document.getElementById('pfSave')", "The phone's profile never drew its form.");
            JsonElement narrow = await Browser.EvaluateAsync<JsonElement>(Columns);
            Assert.True(narrow.GetProperty("formTop").GetDouble() >= narrow.GetProperty("factsBottom").GetDouble() - 1, "On a phone the form is not under the facts.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.documentElement.scrollWidth <= 391"), "The profile scrolls sideways at 390.");
        }
        finally
        {
            await WidenAsync();
        }
    }

    private static async Task<(string Token, string Who)> WhoAsync()
    {
        (string token, _) = await SignInAsync();
        return (token, Environment.GetEnvironmentVariable(UserVariable) ?? "admin");
    }

    /// <summary>
    /// Member roles lists a section's privileges in two columns and keeps its save bar at the foot of the window.
    /// </summary>
    [Fact]
    public async Task Member_roles_draws_privileges_in_two_columns_with_a_bar_that_stays()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/roles", token);

        await WaitForAsync("document.querySelectorAll('#rolePrivileges .rolesection .rolegroup').length >= 2", "The roles page drew no privileges.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const groups = [...document.querySelectorAll('#rolePrivileges .rolesection:first-child .rolegroup')].map(g => g.getBoundingClientRect());
                  return groups.length >= 2 && groups[1].left > groups[0].right - 1 && Math.abs(groups[1].top - groups[0].top) < 2
                    && getComputedStyle(document.getElementById('roleSaveBar')).position === 'sticky';
                })()
                """),
            "The privileges are not in two columns, or the save bar does not stay.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A file's page puts Summary and Description on the left and Item information on the right, and a search has no paragraph
    /// under its title.
    /// </summary>
    [Fact]
    public async Task A_files_page_has_its_information_on_the_right_and_search_has_no_paragraph()
    {
        (string token, _) = await SignInAsync();

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/content/documents"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "zz-bodies.pdf");
        form.Add(new StringContent("zz_bodies_doc"), "title");
        request.Content = form;
        using HttpResponseMessage response = await Http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Filing a document returned {(int)response.StatusCode}.");
        string id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/#/item/{id}", token);
            await WaitForAsync("document.getElementById('docSide')?.getClientRects().length > 0", "The file's page never drew.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const side = document.getElementById('docSide').getBoundingClientRect();
                      const main = document.querySelector('#docOverview > .mapmain').getBoundingClientRect();
                      return side.left >= main.right - 1 && side.width >= 300 && Math.abs(side.top - main.top) < 2;
                    })()
                    """),
                "Item information is not to the right of the summary and description.");

            await OpenAsync("/studio/#/search", token);
            await WaitForAsync("document.getElementById('searchQ')?.getClientRects().length > 0", "Search never drew.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "[...document.querySelectorAll('#view-search .pagehead p')].every(p => p.getClientRects().length === 0)"),
                "Search draws a paragraph under its title.");
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        }
    }
}
