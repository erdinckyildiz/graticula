using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio's phone layout of 2026-11-02 — owner decision, from the ArcGIS Enterprise phone layout — held at 390 × 844:
/// one 56-pixel bar with a drawer behind it, My content with its first row in sight and its filters in a sheet, a row's
/// ⋯ back, an item page with no breadcrumb and one row of actions, and Groups and Members as compact rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each assertion is the mechanism and not the fixture</b>: the bar's height, where focus is after Escape, whether the
/// page behind the drawer is inert, what a row's ⋯ measures. A test that read "the drawer exists" would pass with the
/// drawer drawn and unreachable, which is the shape of defect this suite has been written against since D-59.
/// </para>
/// <para>
/// <b>Opened at 390, <c>mobile</c> on</b>, as <see cref="ScreenReviewTests"/> does, so the viewport is the one a phone
/// reports; the wide layout is held by every other test in this project, which runs at 1,440.
/// </para>
/// </remarks>
public sealed class PhoneLayoutTests : ConsoleTest
{
    private const string Probe = "phone_layout_probe";

    private async Task PhoneAsync()
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width = 390,
            height = 844,
            deviceScaleFactor = 1,
            mobile = true,
        });
    }

    private async Task WidenAsync() => await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");

    private async Task PressAsync(string key, int code)
    {
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, code = key, windowsVirtualKeyCode = code });
        await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code = key, windowsVirtualKeyCode = code });
    }

    /// <summary>
    /// The header is one row 56 pixels high holding the menu, the name, search and the account; the drawer opens with focus
    /// inside it and the page behind it inert, holds the four sections and Sign out, and Escape closes it and gives the
    /// focus back to the button that opened it.
    /// </summary>
    [Fact]
    public async Task The_bar_is_one_row_and_the_drawer_is_a_dialog_that_holds_the_sections_and_sign_out()
    {
        (string token, _) = await SignInAsync();
        await PhoneAsync();

        try
        {
            await OpenAsync("/studio/#/content", token);

            await WaitForAsync(
                "(() => { const b = document.getElementById('menuOpen'); return !!b && b.getClientRects().length > 0 "
                + "&& document.querySelectorAll('#contentRows tr').length > 0; })()",
                "The phone's bar never drew its menu button.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const header = document.querySelector('#main > header').getBoundingClientRect();
                      const inside = ['menuOpen', 'brand', 'barSearch', 'barAvatar'].every(id => {
                        const r = document.getElementById(id).getBoundingClientRect();
                        return r.width > 0 && r.top >= header.top - 1 && r.bottom <= header.bottom + 1;
                      });
                      return Math.abs(header.height - 56) <= 1 && inside;
                    })()
                    """),
                "The header is not one 56-pixel row holding the menu, the name, search and the account.");

            // Closed, the drawer takes no Tab stop: a control the reader cannot see must not be reachable.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "getComputedStyle(document.getElementById('navDrawer')).visibility === 'hidden'"),
                "A closed drawer is still reachable.");

            await ClickAsync("#menuOpen");

            await WaitForAsync(
                "document.getElementById('navDrawer').classList.contains('open')",
                "Pressing Menu did not open the drawer.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const drawer = document.getElementById('navDrawer');
                      const words = [...document.querySelectorAll('#tabs a')].map(a => a.textContent.trim());
                      const out = document.getElementById('signout');
                      const r = out.getBoundingClientRect();
                      return drawer.getAttribute('role') === 'dialog' && drawer.getAttribute('aria-modal') === 'true'
                        && drawer.contains(document.activeElement)
                        && document.getElementById('app').inert === true
                        && words.join(',') === 'Content,Map,Groups,Organization'
                        && out.getClientRects().length > 0 && r.right <= window.innerWidth && r.bottom <= window.innerHeight
                        && getComputedStyle(drawer).visibility === 'visible';
                    })()
                    """),
                "The open drawer is not a modal dialog holding Content, Map, Groups, Organization and a visible Sign out, "
                + "with focus inside it and the page behind it inert.");

            await PressAsync("Escape", 27);

            await WaitForAsync(
                "!document.getElementById('navDrawer').classList.contains('open')",
                "Escape did not close the drawer.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "document.activeElement === document.getElementById('menuOpen') "
                    + "&& document.getElementById('app').inert === false"),
                "Closing the drawer did not give the focus back to the Menu button and the page back its use.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await WidenAsync();
        }
    }

    /// <summary>
    /// My content's first row is on the first screen, its ⋯ is back and at least 44 pixels square, and the notice above it
    /// is one line.
    /// </summary>
    [Fact]
    public async Task My_content_shows_its_first_row_above_400_and_a_row_menu_that_can_be_hit()
    {
        (string token, _) = await SignInAsync();
        await PhoneAsync();

        try
        {
            await OpenAsync("/studio/#/content", token);

            await WaitForAsync("document.querySelectorAll('#contentRows tr.mediarow').length > 0", "Content listed nothing.");

            double top = await Browser.EvaluateAsync<double>(
                "document.querySelector('#contentRows tr.mediarow').getBoundingClientRect().top");

            Assert.True(top < 400, $"The first row of My content starts at y={top}; the phone layout is for it to be in sight.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const s = document.querySelector('#contentRows tr.mediarow td.acts summary');
                      if (!s || !s.getClientRects().length) return false;
                      const r = s.getBoundingClientRect();
                      return r.width >= 44 && r.height >= 44 && r.right <= window.innerWidth && r.left >= 0;
                    })()
                    """),
                "A row's ⋯ is not drawn at 390, or is smaller than 44 pixels. ADR-135 hid it below 480; it is back.");

            // The ⋯ opens a menu that has something in it.
            await ClickAsync("#contentRows tr.mediarow td.acts summary");

            await WaitForAsync(
                "(() => { const s = document.querySelector('#contentRows tr.mediarow td.acts .menu[open] .sheet'); "
                + "return !!s && s.querySelectorAll('a, button').length > 0 && s.getClientRects().length > 0; })()",
                "The row's ⋯ opened nothing.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await WidenAsync();
        }
    }

    /// <summary>
    /// The filter sheet is a dialog that holds the scopes and the item types; ticking a type and pressing Show leaves the
    /// list holding exactly what the tick's count said, closes the sheet and gives focus back to the filter button.
    /// </summary>
    [Fact]
    public async Task The_filter_sheet_holds_the_scopes_and_the_types_and_Show_applies_them()
    {
        (string token, _) = await SignInAsync();
        await PhoneAsync();

        try
        {
            await OpenAsync("/studio/#/content", token);

            await WaitForAsync("document.querySelectorAll('#contentRows tr.mediarow').length > 0", "Content listed nothing.");

            // Closed, the sheet takes no room and no Tab stop.
            Assert.True(
                await Browser.EvaluateAsync<bool>("document.getElementById('contentSide').getClientRects().length === 0"),
                "The filter sheet is drawn while closed.");

            await ClickAsync("#contentFilterBtn");

            await WaitForAsync(
                "document.getElementById('contentSide').classList.contains('open')",
                "The filter button did not open the sheet.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const side = document.getElementById('contentSide');
                      const scopes = [...side.querySelectorAll('#contentScopes a[data-content-scope]')];
                      const kinds = [...side.querySelectorAll('#contentFacets [data-kind-facet]')];
                      return side.getAttribute('role') === 'dialog' && side.contains(document.activeElement)
                        && scopes.length >= 2 && scopes.every(a => a.getClientRects().length > 0 && a.getBoundingClientRect().height >= 44)
                        && kinds.length >= 2 && kinds.every(k => k.getClientRects().length > 0)
                        && document.getElementById('contentIdle').closest('#contentSide') !== null
                        && document.getElementById('contentToolbar').inert === true;
                    })()
                    """),
                "The sheet is not a dialog holding at least two scopes and two types as controls that can be hit, with the "
                + "'not used in 30 days' choice beside them.");

            // Take the first type's own count, tick it, press Show: the list is what the count said.
            int expected = await Browser.EvaluateAsync<int>(
                "parseInt(document.querySelector('#contentFacets label.check .count').textContent.replace(/[^0-9]/g, ''), 10)");

            await ClickAsync("#contentFacets [data-kind-facet]");

            await WaitForAsync(
                "document.querySelectorAll('#contentFacets [data-kind-facet]:checked').length === 1",
                "Ticking a type in the sheet did not tick it.");

            Assert.True(
                await Browser.EvaluateAsync<bool>("document.getElementById('contentSide').contains(document.activeElement)"),
                "Ticking a type in the sheet dropped the focus out of it: the list was drawn again and the tick's own "
                + "element was not put back.");

            await ClickAsync("#contentSheetShow");

            await WaitForAsync(
                "!document.getElementById('contentSide').classList.contains('open')",
                "Show did not close the sheet.");

            Assert.Equal(
                expected,
                await Browser.EvaluateAsync<int>("document.querySelectorAll('#contentRows tr.mediarow').length"));

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "document.activeElement === document.getElementById('contentFilterBtn') "
                    + "&& document.getElementById('contentFilterBtn').getAttribute('aria-label').startsWith('Filters, 1')"),
                "After Show the focus is not on the filter button, or the button does not say a filter is on.");

            // And a scope in the sheet changes the scope and the head's button says which.
            await ClickAsync("#contentFilterBtn");
            await WaitForAsync("document.getElementById('contentSide').classList.contains('open')", "The sheet did not open again.");
            await ClickAsync("#contentScopes a[data-content-scope=\"mine\"]");

            await WaitForAsync(
                "document.getElementById('contentScopeName').textContent.trim() === 'Mine'",
                "Choosing Mine in the sheet did not change the scope the page says it shows.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await WidenAsync();
        }
    }

    /// <summary>
    /// An item's page has no breadcrumb at 390 and its actions are one line: the main action, Share and ⋯.
    /// </summary>
    [Fact]
    public async Task An_item_page_has_no_breadcrumb_and_one_line_of_actions()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_MULTILAYER") ?? "hosted/ci_EarlyAlert";

        await PhoneAsync();

        try
        {
            await OpenAsync($"/studio/#/service/{service}", token);

            await WaitForAsync(
                "(() => { const a = document.getElementById('itemActions'); return !!a && !a.hidden && a.querySelectorAll('button, a').length > 0; })()",
                "The item page never drew its actions.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "document.getElementById('serviceCrumb').getClientRects().length === 0"),
                "The item page still draws its breadcrumb at 390.");

            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    """
                    (() => {
                      const row = document.getElementById('itemActions');
                      const shown = [...row.children].filter(c => c.getClientRects().length > 0);
                      if (shown.length < 2) return false;
                      const tops = shown.map(c => Math.round(c.getBoundingClientRect().top));
                      const oneLine = Math.max(...tops) - Math.min(...tops) <= 2;
                      const more = row.querySelector('details.itemmore > summary');
                      const hit = more && more.getBoundingClientRect().width >= 44 && more.getBoundingClientRect().height >= 44;
                      // Favorite is behind the ⋯ now, not beside Share.
                      const favoriteInMenu = !document.getElementById('itemFavorite') || document.getElementById('itemFavorite').closest('.sheet') !== null;
                      return oneLine && hit && favoriteInMenu && row.getBoundingClientRect().height <= 60;
                    })()
                    """),
                "The item's actions are not one line of the main action, Share and a ⋯ of 44 pixels with favorite behind it.");

            // The bar leads back to the list instead.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "(() => { const b = document.getElementById('barBack'); return !b.hidden && b.getClientRects().length > 0 "
                    + "&& b.getAttribute('href').endsWith('/content') && b.getBoundingClientRect().width >= 44; })()"),
                "The bar's first control is not a Back to Content on an item page.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await WidenAsync();
        }
    }

    /// <summary>
    /// A group's row in Groups and a member's in Members are 90 pixels or less at 390, and each is one tap on the name from
    /// its page.
    /// </summary>
    [Fact]
    public async Task Group_and_member_rows_are_compact()
    {
        (string token, _) = await SignInAsync();

        (int made, string why) = await AdminAsync(
            HttpMethod.Post,
            "/admin/groups",
            JsonSerializer.Serialize(new { name = Probe, title = "Probe group" }));

        Assert.True(made is 200 or 201, $"{made} {why}");

        await PhoneAsync();

        try
        {
            await OpenAsync("/studio/#/groups", token);

            await WaitForAsync(
                $"!!document.querySelector('#groupRows tr[data-group={Probe}]')",
                "The probe group is not listed.");

            double group = await Browser.EvaluateAsync<double>(
                $"document.querySelector('#groupRows tr[data-group={Probe}]').getBoundingClientRect().height");

            Assert.True(group <= 90, $"A group's row is {group} pixels tall at 390; the phone layout is two lines.");

            await OpenAsync("/studio/#/members", token);

            await WaitForAsync("document.querySelectorAll('#members tr').length > 0", "Members listed nothing.");

            string rows = await Browser.EvaluateAsync<string>(
                """
                [...document.querySelectorAll('#members tr')]
                  .filter(r => r.querySelector('td.name') && r.querySelector('td.name').textContent.trim().length <= 40)
                  .map(r => Math.round(r.getBoundingClientRect().height)).join(',')
                """) ?? string.Empty;

            Assert.NotEqual(string.Empty, rows);

            foreach (string height in rows.Split(','))
            {
                Assert.True(int.Parse(height) <= 90, $"A member's row is {height} pixels tall at 390 (rows: {rows}).");
            }

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await WidenAsync();
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Probe}");
        }
    }
}
