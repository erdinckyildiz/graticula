using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Every screen of both surfaces, opened and asked whether it worked.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the generalisation of the four defects the suite was built for.</b> All
/// four were found by the owner opening a screen and pressing something, and each was
/// specific — a click dispatcher, a boot gate, a CSS rule, a shortcut. What they have in
/// common is that nothing had ever opened those screens and looked. So this does:
/// every tab of Server and Studio, in turn.
/// </para>
/// <para>
/// <b>Three questions per screen, and the third is the one a screenshot cannot answer.</b>
/// Did the view become visible; is the page's own header there; and did anything throw.
/// The console loads each section independently so that one refused endpoint cannot blank
/// the page — the right design, and it means a section that threw leaves the rest looking
/// finished. A half-loaded console and a loaded one look identical.
/// </para>
/// <para>
/// <b>It reads and does not write.</b> Every screen here is opened, not operated: the
/// harness traps writes anyway, and what is under test is that arriving somewhere works.
/// The tests that press things are the ones named after the defect they hold.
/// </para>
/// </remarks>
public sealed class EveryScreenTests : ConsoleTest
{
    /// <summary>
    /// The Server surface's screens, from `SURFACES.server.tabs` in console.js.
    /// </summary>
    /// <remarks>
    /// Written out rather than read from the page, deliberately: a list the test derives
    /// from the thing under test cannot notice a screen that stopped being offered. If a
    /// tab is added, this list is the second place to change and the failure says so.
    /// </remarks>
    private static readonly string[] ServerScreens =
        ["services", "sources", "members", "operations"];

    /// <summary>The Studio surface's screens.</summary>
    private static readonly string[] StudioScreens = ["content", "anonymous"];

    /// <summary>
    /// Every Server screen opens, paints its own heading, and throws nothing.
    /// </summary>
    [Fact]
    public async Task Every_server_screen_opens_without_the_page_failing()
    {
        (string token, _) = await SignInAsync();
        await AssertEveryScreenAsync("server", ServerScreens, token);
    }

    /// <summary>
    /// Every Studio screen opens, paints its own heading, and throws nothing.
    /// </summary>
    /// <remarks>
    /// <b>As an administrator, which is a stated limit rather than an oversight.</b> A
    /// publisher's view of Studio is <see cref="SurfaceTests"/>'s subject and needs a
    /// reader without <c>admin:manageServer</c>; what this checks is that the screens
    /// themselves work.
    /// </remarks>
    [Fact]
    public async Task Every_studio_screen_opens_without_the_page_failing()
    {
        (string token, _) = await SignInAsync();
        await AssertEveryScreenAsync("studio", StudioScreens, token);
    }

    /// <summary>
    /// Every page of a layer's editor opens, on both surfaces.
    /// </summary>
    /// <remarks>
    /// <b>The layer editor is where two of the four defects lived</b> — the settings that
    /// were on the wrong object (D-61) and the pages that became unreachable when they
    /// moved. Its pages are split across the two surfaces by ADR-034 §5c, so each surface
    /// is asked for its own.
    /// </remarks>
    [Fact]
    public async Task Every_page_of_a_layers_editor_opens_on_both_surfaces()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        Dictionary<string, string[]> pages = new()
        {
            ["server"] = ["general", "endpoints"],
            // <b>Sharing left this list on 2026-08-18.</b> A scope belongs to the service —
            // `service.sharing` is the column the serving path reads — so a Sharing page per layer
            // gave one setting as many screens as the service had layers, which is D-61's defect in
            // the one setting D-61's repair did not reach. Its absence is asserted at the end of
            // this method rather than left implicit, because *a page reappears on the wrong object*
            // is the regression this class exists for.
            ["studio"] = ["symbology", "caching", "maintenance"],
        };

        foreach ((string surface, string[] names) in pages)
        {
            foreach (string page in names)
            {
                await OpenAsync(
                    $"/{surface}/#/layer/{Uri.EscapeDataString(layer)}/{page}", token);

                await WaitForAsync(
                    "!!document.getElementById('app')"
            + " && getComputedStyle(document.getElementById('app')).display !== 'none'",
                    $"The layer editor did not open for {surface}/{page}.");

                await WaitForAsync(
                    $"document.querySelector('#editPages #page-{page}.on')",
                    $"'{page}' is listed for {surface} and its section never became the open one. "
                    + "That is the shape of the defect that left eight of nine services with no "
                    + "reachable Limits: the route exists and the screen does not arrive.");

                string[] failures = await PageErrorsAsync();

                Assert.True(
                    failures.Length == 0,
                    $"{surface}/#/layer/{layer}/{page} threw:\n  "
                    + string.Join("\n  ", failures));
            }
        }

        // <b>And Sharing is not one of a layer's pages, on either surface.</b> Asked for by address
        // rather than by reading the source: a page gone from the navigation and still reachable by
        // URL is half-moved, which is how the settings D-61 describes came to exist twice over.
        foreach (string surface in Surfaces)
        {
            await OpenAsync(
                $"/{surface}/#/layer/{Uri.EscapeDataString(layer)}/sharing", token);

            await WaitForAsync(
                "!!document.getElementById('app')"
            + " && getComputedStyle(document.getElementById('app')).display !== 'none'",
                $"The console did not finish loading for {surface} on a stale sharing address.");

            bool present = await Browser.EvaluateAsync<bool>(
                "!!document.querySelector('#editPages #page-sharing')");

            Assert.False(
                present,
                $"A layer still has a Sharing page on {surface}. A scope is the service's — one "
                + "column — and a page per layer makes one setting look like several (D-61). It "
                + "belongs on the service's pages, where ServiceSharingPageTests requires it.");
        }
    }

    /// <summary>The two surfaces, hoisted so the loop above does not allocate per call.</summary>
    private static readonly string[] Surfaces = ["server", "studio"];

    private async Task AssertEveryScreenAsync(string surface, string[] screens, string token)
    {
        foreach (string screen in screens)
        {
            await OpenAsync($"/{surface}/#/{screen}", token);

            await WaitForAsync(
                "!!document.getElementById('app')"
            + " && getComputedStyle(document.getElementById('app')).display !== 'none'",
                $"The console did not open at {surface}/#/{screen}.");

            await WaitForAsync(
                $"document.querySelector('#view-{screen}.on')",
                $"'{screen}' is a tab of {surface} and its view never became the open one — so "
                + "the tab leads somewhere that does not arrive.");

            // The page's own heading, because ADR-020 §5's rule is that every screen names
            // itself: a page whose only identifier is which table is on it is a page
            // somebody arrives at and has to deduce.
            await WaitForAsync(
                $"(document.querySelector('#view-{screen} .pagehead h2')?.textContent || '')"
                + ".trim().length > 0",
                $"{surface}/#/{screen} painted no heading of its own.");

            // <b>Given a moment to finish, because the sections load after the view
            // appears.</b> Asking immediately would report a clean page that had not yet
            // had the chance to fail, which is a test that passes for the wrong reason.
            await Task.Delay(1200);

            string[] failures = await PageErrorsAsync();

            Assert.True(
                failures.Length == 0,
                $"{surface}/#/{screen} threw or left a rejection unhandled:\n  "
                + string.Join("\n  ", failures.Distinct()));
        }
    }
}
