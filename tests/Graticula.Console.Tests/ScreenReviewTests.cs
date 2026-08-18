using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// What four design reviews found on 2026-08-19, turned into the checks that would have caught it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reviews drove a real browser over every screen; these are the findings a suite can hold.</b>
/// Not all of them can: *the symbology page has no visual affordance* is a judgement, and
/// [D-99](../../docs/architecture-debt.md) is where it lives instead. What is here is the set that has
/// a measurable claim — a page that fits, a route that exists, a state that survives a click.
/// </para>
/// <para>
/// <b>The first test is the one that matters most, and it guards a data-loss path.</b> D-97: switching
/// tabs on a service's settings page unchecked its capabilities and Save wrote that. There was no
/// symptom until after the write, which is exactly the kind of defect a suite exists for and a reader
/// does not.
/// </para>
/// </remarks>
public sealed class ScreenReviewTests : ConsoleTest
{
    /// <summary>
    /// A service's settings survive a trip to another tab and back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-97.</b> The tab handler re-derived the folder from the text of the breadcrumb's <c>b</c>
    /// element, which holds only the bare name — so `folder` was null for every service outside the site
    /// root, the refetch 404'd, and the checkboxes stayed at their unchecked default. `saveServiceSettings`
    /// reads those boxes.
    /// </para>
    /// <para>
    /// <b>Asserted on a foldered service, because a root one passes either way.</b> That is what made
    /// this survivable for as long as it did: the case that works is the case a quick look uses.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Leaving_a_service_settings_tab_and_returning_keeps_what_was_there()
    {
        (string token, _) = await SignInAsync();

        string service = await AnyFolderedServiceAsync();

        await OpenAsync($"/server/#/service/{service}", token);

        await WaitForAsync(
            "document.querySelectorAll('#serviceNav a').length > 1",
            "The service page never drew more than one settings tab, so there is no tab to leave.");

        await WaitForAsync(
            "document.querySelectorAll('#serviceEdit input[type=checkbox]').length > 0",
            "Capabilities never rendered its checkboxes.");

        string before = await BoxesAsync();

        await ClickAsync("#serviceNav a:nth-child(2)");

        await WaitForAsync(
            "document.querySelector('#serviceNav a:nth-child(2)')?.getAttribute('aria-current') "
            + "=== 'page'",
            "The second settings tab never became current.");

        await ClickAsync("#serviceNav a:nth-child(1)");

        // <b>Waited for, not read once — and reading it once is how this test first failed against a
        // console that was working.</b> The checkboxes are in the DOM the instant the tab is redrawn and
        // are filled when the request returns, so a read taken immediately sees the unchecked defaults.
        // The whole test finished in 465 ms and reported the defect it was written to guard.
        //
        // <b>The claim is that the state comes back, so that is the condition.</b> When D-97 is present
        // it never does: the review measured the wiped boxes sitting there after a three-second settle,
        // because the request 404'd and nothing was going to fill them.
        await WaitForAsync(
            $"[...document.querySelectorAll('#serviceEdit input[type=checkbox]')]"
            + $".map(b => b.checked ? '1' : '0').join('') === '{before}'",
            $"Coming back to the first settings tab did not restore '{before}'. This is D-97: the tab "
            + "handler used to re-derive the folder from the breadcrumb's rendered text, which holds "
            + "only the bare name, so the refetch 404'd for any foldered service and the boxes stayed "
            + "at their unchecked default. `saveServiceSettings` reads these boxes.");

        string after = await BoxesAsync();

        // <b>The toast is read before the comparison, so a failure says what the page said.</b> The
        // symptom of D-97 was a refusal beside the wiped boxes — *No service at the root* — and a bare
        // string difference sends the next reader to a browser to discover that again.
        string toast = await Browser.EvaluateAsync<string>(
            "document.getElementById('toast')?.textContent || ''") ?? string.Empty;

        Assert.True(
            before == after,
            $"'{before}' became '{after}' on service '{service}', and the page said: \"{toast}\".");

        Assert.DoesNotContain("No service", toast, StringComparison.OrdinalIgnoreCase);

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// Studio's Sharing page opens, from the link Server offers to it.
    /// </summary>
    /// <remarks>
    /// <b>D-98, first half.</b> `SCREEN_SURFACE.service` named Server as the owner of every service
    /// address, so Studio's only service page could never be in the page set and both links to it landed
    /// on Capabilities — silently, which is what made it survive. The table it lived in already carried a
    /// note saying `layer` had been left out for the same reason.
    /// </remarks>
    [Fact]
    public async Task The_sharing_page_opens_from_the_link_that_offers_it()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services/hosted", token);

        await WaitForAsync(
            "document.querySelectorAll('#services tr').length > 0",
            "Server's services list never rendered, so there is no sharing link to press.");

        await WaitForAsync(
            "!!document.querySelector('#services a[href*=\"/studio/\"]')",
            "No link from Server's services list crosses to Studio. The sharing scope is set on "
            + "Studio's page and this is the only route this console offers to it.");

        await ClickAsync("#services a[href*=\"/studio/\"]");

        await WaitForAsync(
            "location.pathname === '/studio/' "
            + "&& document.querySelector('#serviceNav a[aria-current=\"page\"]')?.textContent"
            + "?.trim() === 'Sharing'",
            "The link did not open Studio's Sharing page. It used to land on Capabilities, because the "
            + "router forced the surface before the page set was chosen.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// A layer's own page is reachable by clicking, not only by typing its address.
    /// </summary>
    /// <remarks>
    /// <b>D-98, second half.</b> Nothing in this console linked to <c>#/layer/…</c> except the editor's
    /// own tabs, so Maintenance, Symbology, Caching, General and Endpoints were reachable only by
    /// address — using a bare layer name that no screen displayed. The comment that removed the service
    /// page's layer list had asserted the content list was the route.
    /// </remarks>
    [Fact]
    public async Task A_content_row_opens_the_layer_it_holds()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentRows tr').length > 0",
            "Studio's content list never rendered.");

        // <b>Only a single-layer item carries the route</b>, deliberately: a service with three layers
        // has no one answer, and opening the cover layer's appearance settings would be a guess. So this
        // waits for one rather than clicking the first row.
        await WaitForAsync(
            "!!document.querySelector('#contentRows tr[data-pick]')",
            "No content row offers a layer route. A single-layer item is what an import makes and what "
            + "most of these are, so an absence here means the route is gone rather than absent.");

        await ClickAsync("#contentRows tr[data-pick] td.thumbcell");

        await WaitForAsync(
            "document.getElementById('view-layer')?.classList.contains('on') "
            + "&& location.hash.startsWith('#/layer/')",
            "Clicking a single-layer content row did not open the layer editor.");

        // <b>And Cancel does not leave the surface.</b> It was hardcoded to Server's services list, so a
        // publisher pressing *nevermind* crossed the product — and one without `admin:manageServer` got
        // a refusal toast for their trouble.
        Assert.Equal(
            "#/content",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('editCancel')?.getAttribute('href') || ''"));

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>
    /// No screen scrolls sideways in a 1024-pixel window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, and it took three attempts to find the cause.</b> Not the tables, which fitted; not
    /// only the closed row menus, though those had a 450-pixel layout box; it was
    /// <c>grid-template-columns: 1fr</c> in the narrow media query. A bare <c>1fr</c> is
    /// <c>minmax(auto, 1fr)</c>, and <c>auto</c> as a minimum is <b>min-content</b> — so the column could
    /// not be narrower than the widest thing in it and <c>overflow-x: auto</c> on the panel was
    /// powerless. The desktop rule beside it already said <c>minmax(0, 1fr)</c>.
    /// </para>
    /// <para>
    /// <b>1024×720 through the debugger rather than a second browser</b>, and cleared afterwards so the
    /// rest of the class sees the suite's own window. The harm the reviews described is what makes this
    /// worth a test: the page took the navigation column with it, so *Graticula* read as *aticula*.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/server/#/services/hosted")]
    [InlineData("/server/#/operations")]
    [InlineData("/server/#/members")]
    [InlineData("/server/#/roles")]
    [InlineData("/studio/#/content")]
    [InlineData("/studio/#/anonymous")]
    public async Task A_screen_fits_a_1024_pixel_window(string address)
    {
        (string token, _) = await SignInAsync();

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width = 1024,
            height = 720,
            deviceScaleFactor = 1,
            mobile = false,
        });

        try
        {
            await OpenAsync(address, token);

            // The screen has to have drawn something before its width means anything.
            await WaitForAsync(
                "document.querySelector('.view.on') !== null "
                + "&& document.querySelector('.view.on').textContent.trim().length > 40",
                $"'{address}' never rendered, so its width proves nothing.");

            await WaitForAsync(
                "document.body.scrollWidth <= window.innerWidth",
                $"'{address}' scrolls sideways in a 1024-pixel window. A table that needs the width "
                + "scrolls inside its own container; the page must not, because it carries the "
                + "navigation column out with it.");

            string[] errors = await PageErrorsAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// A role can be reached and operated without a mouse.
    /// </summary>
    /// <remarks>
    /// <b>The roles table had no keyboard path to its own function.</b> Its rows were clickable
    /// <c>tr</c>s with no <c>tabindex</c> and no key handler, so tabbing from *New role* skipped all five
    /// and landed in the editor below — a total loss of the screen's purpose rather than a degraded one.
    /// The name is a real control now: a button where the row opens something on this page, a link where
    /// the row is an address.
    /// </remarks>
    [Fact]
    public async Task A_role_is_reachable_from_the_keyboard()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/roles", token);

        await WaitForAsync(
            "document.querySelectorAll('#roleRows tr').length > 0",
            "The roles list never rendered.");

        await WaitForAsync(
            "(() => { const b = document.querySelector('#roleRows .rowname');"
            + " if (!b) return false; b.focus(); return document.activeElement === b; })()",
            "A role's name is not focusable, so the roles screen cannot be operated without a mouse.");

        string[] errors = await PageErrorsAsync();
        Assert.Empty(errors);
    }

    /// <summary>Every capability checkbox on the open settings tab, as a string.</summary>
    private async Task<string> BoxesAsync() =>
        await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('#serviceEdit input[type=checkbox]')]"
            + ".map(b => b.checked ? '1' : '0').join('')") ?? string.Empty;

    /// <summary>
    /// A service that lives in a folder, which is the case the defect needed.
    /// </summary>
    /// <remarks>
    /// <b>Read from the server rather than named in a constant.</b> A hardcoded service is a fixture
    /// another suite can delete — which is [D-89](../../docs/architecture-debt.md), and it has already
    /// failed three tests in this repository this week.
    /// </remarks>
    private async Task<string> AnyFolderedServiceAsync()
    {
        // <b>From the content listing, not `/admin/services`.</b> That one answers with the *system*
        // services — GeometryServer and its kind — which hold no layers, so `showService` reports
        // *no layers* and draws no settings tabs at all. The first version of this test picked
        // `Utilities/Geometry` and failed on a screen that was working.
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/content/items");

        Assert.Equal(200, status);

        string found = await Browser.EvaluateAsync<string>(
            $"(() => {{ const d = {body};"
            + " const all = (d.items || []).filter(i => i.name && String(i.name).includes('/')"
            + " && (i.layers || 0) > 0 && !String(i.name).includes('zz_'));"
            + " return all.length ? all[0].name : ''; })()")
            ?? string.Empty;

        Assert.False(
            string.IsNullOrEmpty(found),
            "This server has no feature service inside a folder, and that is the only case the defect "
            + "this test guards could occur in — a service at the site root passes either way, which is "
            + "what let it survive.");

        return found;
    }
}
