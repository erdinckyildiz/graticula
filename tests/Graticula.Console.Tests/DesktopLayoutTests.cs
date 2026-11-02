using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The desktop layout of 2026-11-02 — owner decision after the approved mockups, ADR-255: one 56-pixel bar whose account menu
/// holds the switch between surfaces, an item page of title, one meta line and three actions, a services list with a bulk
/// bar, and a sign-in page with the mark. Held at the harness's default width, which is above 720 pixels.
/// </summary>
/// <remarks>
/// Each assertion reads a mechanism — where a node is, what it measures, what a click reaches — rather than the fixture's data.
/// </remarks>
public sealed class DesktopLayoutTests : ConsoleTest
{
    /// <summary>
    /// The bar is one row of 56 pixels holding the name, the sections and the account; the sections carry the underline on the
    /// current one; the switch between surfaces and the health live in the account menu, which opens to show them.
    /// </summary>
    [Fact]
    public async Task One_bar_of_56_pixels_and_an_account_menu_that_holds_the_surface_switch()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#tabs a[data-tab], #tabs a[data-link]').length >= 3 && document.getElementById('userName').textContent.length > 0",
            "The bar never drew its sections and the account.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const header = document.querySelector('#main > header').getBoundingClientRect();
                  const inside = ['brand', 'tabs', 'userMenu'].every(id => {
                    const r = document.getElementById(id).getBoundingClientRect();
                    return r.width > 0 && r.top >= header.top - 1 && r.bottom <= header.bottom + 1;
                  });
                  return Math.abs(header.height - 56) <= 1 && inside;
                })()
                """),
            "The header is not one 56-pixel row holding the name, the sections and the account.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const a = document.querySelector('#tabs a[aria-current]'); "
                + "return !!a && parseFloat(getComputedStyle(a).borderBottomWidth) >= 3; })()"),
            "The current section has no underline.");

        // The switch and the health moved into the account menu: closed they take no room, open they are offered.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "!!document.querySelector('#userMenu .sheet #surfaces') && !!document.querySelector('#userMenu .sheet #healthDot') "
                + "&& document.getElementById('surfaces').getClientRects().length === 0"),
            "The surface switch and the health are not inside the closed account menu.");

        await ClickAsync("#userMenu summary");

        await WaitForAsync(
            "[...document.querySelectorAll('#userMenu #surfaces a')].filter(a => a.getClientRects().length > 0).length >= 2",
            "Opening the account menu did not offer the other surfaces.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "['themeToggle', 'signout'].every(id => document.getElementById(id).getClientRects().length > 0) "
                + "&& document.getElementById('healthDot').getClientRects().length > 0"),
            "The account menu does not hold the theme, health and Sign out.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// An item's page has no breadcrumb, a head of one meta line with at most three controls at the right (Open, Share, ⋯),
    /// underlined tabs, and Item information to the right of Description and Layers.
    /// </summary>
    [Fact]
    public async Task An_item_page_has_three_actions_and_tabs_and_information_on_the_right()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content", token);

        await WaitForAsync("!!document.querySelector('#contentRows a[href*=\"/studio/item/\"]')", "Content listed nothing.");
        string href = await Browser.EvaluateAsync<string>(
            "document.querySelector('#contentRows a[href*=\"/studio/item/\"]').getAttribute('href').split('?')[0]") ?? string.Empty;
        await OpenAsync(href.Replace("/studio/", "/studio/#/", System.StringComparison.Ordinal), token);

        await WaitForAsync(
            "!!document.querySelector('#itemActions:not([hidden]) details.itemmore') && document.querySelectorAll('#serviceTabs a').length >= 3",
            "The item page never drew its actions and tabs.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('.crumbs')].every(c => c.getClientRects().length === 0)"),
            "A breadcrumb is drawn on the item page.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const row = document.getElementById('itemActions');
                  const shown = [...row.children].filter(c => c.getClientRects().length > 0);
                  const more = row.querySelector('details.itemmore > summary');
                  const mr = more.getBoundingClientRect();
                  const rr = row.getBoundingClientRect();
                  return shown.length <= 3 && mr.width >= 30 && mr.right <= rr.right + 1;
                })()
                """),
            "The item's action row is not Open, Share and ⋯.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const info = document.getElementById('serviceDetails').getBoundingClientRect();
                  const left = document.getElementById('serviceHead').getBoundingClientRect();
                  const tab = document.querySelector('#serviceTabs a[aria-current]');
                  return info.left > left.right - 1 && info.width > 200 && !!tab;
                })()
                """),
            "Item information is not to the right of the description.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Ticking two services draws the bulk bar naming them; clearing the selection takes it away.</summary>
    [Fact]
    public async Task Selecting_services_draws_the_bulk_bar()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/server/#/services", token);

        await WaitForAsync("document.querySelectorAll('#view-services input[data-svc-pick]').length >= 2", "Server listed fewer than two services.");

        Assert.True(
            await Browser.EvaluateAsync<bool>("document.getElementById('serviceBulk').hidden"),
            "The bulk bar is drawn with nothing selected.");

        await Browser.EvaluateAsync<bool>(
            "(() => { [...document.querySelectorAll('#view-services input[data-svc-pick]')].slice(0, 2).forEach(c => c.click()); return true; })()");

        await WaitForAsync(
            "(() => { const b = document.getElementById('serviceBulk'); return !b.hidden && /2 selected/.test(b.textContent) && b.getClientRects().length > 0; })()",
            "Ticking two services did not draw the bulk bar.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The sign-in page draws the mark on the navy panel and the card beside it, and the card's second step (the code row) lies
    /// inside the card and is a field a person can use when it is shown.
    /// </summary>
    [Fact]
    public async Task Sign_in_shows_the_mark_and_the_card_and_keeps_its_second_step()
    {
        await OpenAsync("/studio/", token: null);

        await WaitForAsync("document.getElementById('signinForm').getClientRects().length > 0", "The sign-in card was never drawn.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const panel = document.querySelector('.signin-brand');
                  const mark = document.querySelector('.signin-mark').getBoundingClientRect();
                  const card = document.getElementById('signinForm').getBoundingClientRect();
                  const p = panel.getBoundingClientRect();
                  return getComputedStyle(panel).backgroundColor === 'rgb(15, 29, 51)' && mark.width >= 40 && mark.right <= p.right
                    && card.left >= p.right - 1 && /Your maps, your data, your server\./.test(panel.textContent)
                    && document.querySelector('.signin-mark').getAttribute('aria-label') === 'Graticula';
                })()
                """),
            "The sign-in page has no navy panel with the mark beside the card.");

        // The second step, as the server asks for it.
        await Browser.EvaluateAsync<bool>("(() => { document.getElementById('signinCodeRow').hidden = false; return true; })()");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const row = document.getElementById('signinCodeRow').getBoundingClientRect();
                  const card = document.getElementById('signinForm').getBoundingClientRect();
                  const field = document.getElementById('signinCode').getBoundingClientRect();
                  return row.left >= card.left && row.right <= card.right && field.height >= 40;
                })()
                """),
            "The code step does not lie inside the card or is too small to use.");
    }
}
