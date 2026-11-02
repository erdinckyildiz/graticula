using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The findings of the UI sweep of 2026-11-02 that are behaviour rather than wording, each held by the mechanism and not
/// by a fixture: a title that names the screen, one <c>h1</c>, the focus a tab keeps, a count that announces itself, a
/// column that is pinned only when it is a column of actions, and no identifier in the copy.
/// </summary>
public sealed class UiSweepOf20261102Tests : ConsoleTest
{
    private static readonly string[] Screens = ["content", "groups", "members"];

    /// <summary>
    /// Each screen has a title of its own and exactly one visible <c>h1</c> — the sweep found "Default · Graticula" on
    /// every screen and headings that began at <c>h2</c>.
    /// </summary>
    [Fact]
    public async Task Every_screen_names_itself_in_the_window_title_and_has_one_visible_h1()
    {
        (string token, _) = await SignInAsync();

        foreach (string screen in Screens)
        {
            await OpenAsync($"/studio/#/{screen}", token);

            await WaitForAsync(
                $"document.querySelector('#view-{screen}.on')",
                $"'{screen}' never became the open view.");

            await WaitForAsync(
                "(() => { const h = [...document.querySelectorAll('h1')].filter(e => e.getClientRects().length);"
                + " return h.length === 1 && h[0].textContent.trim().length > 0"
                + " && document.title.startsWith(h[0].textContent.trim()) && document.title.endsWith('Graticula'); })()",
                $"/studio/#/{screen} did not have exactly one visible h1 that its window title begins with.");
        }
    }

    /// <summary>
    /// The count above a list is a live region, so a filter that changes it is heard.
    /// </summary>
    [Fact]
    public async Task The_content_and_group_counts_are_live_regions()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("document.getElementById('contentCount')", "Content drew no count.");

        Assert.Equal(
            "status",
            await Browser.EvaluateAsync<string>("document.getElementById('contentCount').getAttribute('role')"));

        await OpenAsync("/studio/#/groups", token);
        await WaitForAsync("document.getElementById('groupCount')", "Groups drew no count.");

        Assert.Equal(
            "status",
            await Browser.EvaluateAsync<string>("document.getElementById('groupCount').getAttribute('role')"));
    }

    /// <summary>
    /// The log table's scroller can be reached and is named, because at 390 pixels it scrolls.
    /// </summary>
    [Fact]
    public async Task The_log_table_scroller_is_focusable_and_named()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/logs", token);
        await WaitForAsync("document.getElementById('logRows')", "Logs drew no table.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const s = document.getElementById('logRows').closest('.widetable');"
                + " return s.getAttribute('tabindex') === '0' && s.getAttribute('role') === 'region'"
                + " && s.getAttribute('aria-label') === 'Log entries'; })()"),
            "The log table's scroller is not a named, focusable region.");
    }

    /// <summary>
    /// The skip link is the first stop of the page and leads to the main landmark.
    /// </summary>
    [Fact]
    public async Task The_first_focusable_element_skips_to_the_content()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("document.getElementById('skipToContent')", "The shell has no skip link.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const link = document.getElementById('skipToContent');"
                + " const first = [...document.querySelectorAll('a[href], button, input, select, textarea, [tabindex=\"0\"]')]"
                + ".find(e => e.tabIndex >= 0);"
                + " link.click(); return first === link && document.activeElement === document.getElementById('app'); })()"),
            "The skip link is not the first focusable element, or it did not move focus to the main landmark.");
    }

    /// <summary>
    /// A row's thumbnail does not repeat the name link as a Tab stop.
    /// </summary>
    [Fact]
    public async Task A_thumbnail_link_in_a_content_row_is_not_a_tab_stop()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("document.querySelectorAll('#contentRows tr').length > 0", "Content listed nothing.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#contentRows a.thumblink')]"
                + ".every(a => a.getAttribute('tabindex') === '-1' && a.getAttribute('aria-hidden') === 'true'"
                + " || (a.title.startsWith('Draw ') && (a.getAttribute('aria-label') || '').length > 0))"),
            "A thumbnail link that opens the same page as the name is a second Tab stop.");
    }

    /// <summary>
    /// Only a column of actions is pinned: the sticky rule is scoped to a table whose last cells hold a menu or a button,
    /// so a data table's last column is not held over the ones beside it.
    /// </summary>
    [Fact]
    public async Task The_sticky_column_rule_is_scoped_to_action_columns()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("document.getElementById('app')", "The console did not open.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const host = document.createElement('div');
                  host.className = 'widetable';
                  host.innerHTML = '<table><thead><tr><th>A</th><th>B</th></tr></thead>'
                    + '<tbody><tr><td>1</td><td id="sweepData">2</td></tr></tbody></table>';
                  document.body.appendChild(host);
                  const plain = getComputedStyle(document.getElementById('sweepData')).position;
                  host.querySelector('tbody td:last-child').innerHTML = '<button type="button">Open</button>';
                  const pinned = getComputedStyle(host.querySelector('tbody td:last-child')).position;
                  host.remove();
                  return plain !== 'sticky' && (innerWidth > 1100 || pinned === 'sticky');
                })()
                """),
            "A table of data columns pinned its last column, or a table of actions did not.");
    }

    /// <summary>
    /// The New member dialog names roles and privileges in words; the identifiers are in tooltips and in the values.
    /// </summary>
    [Fact]
    public async Task The_member_dialog_shows_role_names_and_not_identifiers()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/members", token);
        await WaitForAsync("document.querySelectorAll('#mRole option').length > 0", "Members drew no role choices.");

        string? shown = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#mRole option')].map(o => o.textContent).join('|')");

        Assert.DoesNotContain("data_editor", shown!);
        Assert.Contains("Publisher", shown!);

        // What is sent is still the identifier.
        string? values = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#mRole option')].map(o => o.value).join('|')");

        Assert.Contains("publisher", values!);

        string? carries = await Browser.EvaluateAsync<string>("document.getElementById('mGrants').textContent");

        Assert.DoesNotContain("content:", carries!);
        Assert.DoesNotContain("ADR-", carries!);
    }

    /// <summary>
    /// The role page lists privileges in words, with the identifier only in a tooltip.
    /// </summary>
    [Fact]
    public async Task The_roles_page_shows_privileges_in_words()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/roles", token);
        await WaitForAsync("document.querySelectorAll('#rolePrivileges .privilegename').length > 0", "No privileges drawn.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelectorAll('#rolePrivileges code.mono').length === 0"
                + " && [...document.querySelectorAll('#rolePrivileges .privilegename')].every(b => b.title.length > 0)"),
            "A privilege's identifier is still printed in the row, or has no tooltip.");

        Assert.DoesNotContain(
            "organisation",
            await Browser.EvaluateAsync<string>("document.getElementById('rolePrivileges').textContent"),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The window of a settings strip on a phone wraps instead of scrolling out of sight.
    /// </summary>
    [Fact]
    public async Task The_settings_strip_wraps_at_a_phone_width()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/general", token);
        await WaitForAsync("document.querySelector('#view-general nav.subpages')", "Settings drew no strip.");

        string? wrap = await Browser.EvaluateAsync<string>(
            """
            (() => {
              const rule = [...document.styleSheets].flatMap(s => { try { return [...s.cssRules]; } catch { return []; } })
                .filter(r => r.media && r.media.mediaText.includes('480px'))
                .flatMap(r => [...r.cssRules])
                .find(r => r.selectorText && r.selectorText.includes('nav.subpages'));
              return rule ? rule.style.flexWrap : '';
            })()
            """);

        Assert.Equal("wrap", wrap);
    }

    /// <summary>
    /// A heading that two surfaces share does not give two screens one window title: Organization's and System's General,
    /// Studio's and Server's Services. The verification round found "General · Graticula" twice.
    /// </summary>
    [Fact]
    public async Task Screens_that_share_a_heading_across_surfaces_have_distinct_titles()
    {
        (string token, _) = await SignInAsync();
        var titles = new System.Collections.Generic.Dictionary<string, string>();

        foreach ((string address, string view) in new[]
        {
            ("/studio/#/general", "view-general"),
            ("/system/#/settings", "view-settings"),
            ("/studio/#/services", "view-services"),
            ("/server/#/services", "view-services"),
        })
        {
            await OpenAsync(address, token);
            await WaitForAsync(
                $"document.querySelector('#{view}.on') && document.title.length > 0 && document.title !== 'Graticula console'"
                + $" && [...document.querySelectorAll('h1')].some(h => h.getClientRects().length && document.title.startsWith(h.textContent.trim()))",
                $"{address} never titled itself.");
            titles[address] = await Browser.EvaluateAsync<string>("document.title") ?? string.Empty;
        }

        Assert.Equal(titles.Count, new System.Collections.Generic.HashSet<string>(titles.Values).Count);
        Assert.Contains("System", titles["/system/#/settings"]);
        Assert.Contains("Organization", titles["/studio/#/general"]);
        Assert.Contains("Server", titles["/server/#/services"]);
    }

    /// <summary>
    /// Roles, privileges and user types are read in words on the Roles page and the Members screen; the identifiers are
    /// tooltips and values. The verification round found <c>data_editor</c>, <c>content:publishTools</c> and
    /// <c>creator</c> as visible text.
    /// </summary>
    [Fact]
    public async Task The_roles_page_and_member_filters_show_no_identifier_as_text()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/roles", token);
        await WaitForAsync("document.querySelectorAll('#roleRows button.rowname').length > 0", "No roles drawn.");
        await WaitForAsync("document.querySelectorAll('#rolePrivileges .privilegename').length > 0", "No privileges drawn.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#roleRows button.rowname')].every(b => !/[_]|^[a-z]/.test(b.textContent.trim()))"
                + " && [...document.querySelectorAll('#rolePrivileges .privilegename')].every(b => !b.textContent.includes(':'))"
                + " && [...document.querySelectorAll('#roleFromPick option')].every(o => !/[_]/.test(o.textContent))"),
            "A role or a privilege is still printed as its identifier.");

        await OpenAsync("/studio/#/members", token);
        await WaitForAsync("document.querySelectorAll('#mfTypes label').length > 0", "Members drew no user-type filter.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#mfTypes label')].every(l => /^[A-Z]/.test(l.textContent.trim()))"
                + " && [...document.querySelectorAll('#mType option')].every(o => /^[A-Z]/.test(o.textContent.trim()))"
                + " && [...document.querySelectorAll('#mType option')].every(o => /^[a-z]+$/.test(o.value))"),
            "A user type is printed in lower case, or its value changed.");
    }

    /// <summary>
    /// A service opened on the web map from its address is drawn: the first screen is inside the layer's scale range, not
    /// the whole extent under a sentence saying to zoom in.
    /// </summary>
    [Fact]
    public async Task A_service_opened_on_the_web_map_is_framed_where_it_draws()
    {
        (string token, string cookie) = await SignInAsync();
        string service = (Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings").Trim('/');

        await Browser.PlantAsync(
            """
            (() => {
              const inner = window.fetch.bind(window);
              window.fetch = async (input, init) => {
                const url = typeof input === "string" ? input : input.url;
                const path = new URL(url, location.href);
                const response = await inner(input, init);
                if (!/\/FeatureServer\/\d+$/.test(path.pathname)) return response;
                const body = await response.clone().json().catch(() => null);
                if (!body || body.error) return response;
                body.minScale = 72224;
                return new Response(JSON.stringify(body),
                  { status: response.status, headers: { "Content-Type": "application/json" } });
              };
            })();
            """);
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(service)}", token, cookie);

        await WaitForAsync(
            "typeof wmLayers === 'function' && wmLayers().length > 0 && wmLayers().every(l => (wmRuntime.get(l) || {}).info)",
            "The layer never loaded.");

        Assert.True(
            await Browser.EvaluateAsync<bool>("wmMap.getView().getResolution() * 96 * 39.37 <= 72224 * 1.05"),
            "The map opened outside the layer's scale range.");
    }

    /// <summary>
    /// The layer viewer's heading names the layer only when it differs from the service, and its window title begins with
    /// what the heading says.
    /// </summary>
    [Fact]
    public async Task The_viewer_heading_does_not_repeat_the_service_name()
    {
        (string token, string cookie) = await SignInAsync();
        string service = (Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings").Trim('/');

        await OpenAsync($"/studio/view.html?service={Uri.EscapeDataString(service)}", token, cookie);
        await WaitForAsync("(document.getElementById('title')?.textContent || '').length > 1 && document.title.endsWith('Graticula')",
            "The viewer never titled itself.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const t = document.getElementById('title').textContent.trim();"
                + " const [a, b] = t.split(' — '); return (b === undefined || a !== b) && document.title.startsWith(t); })()"),
            "The viewer repeats the service's name in its heading, or its title does not begin with it.");
    }
}
