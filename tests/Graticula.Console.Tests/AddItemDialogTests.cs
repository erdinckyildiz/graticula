using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The New item dialog: one action per surface, a reachable primary, and focus that follows a screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three findings from the design review of 2026-08-19, each turned into the check that would have
/// caught it.</b> [ADR-034](../../docs/adr/ADR-034-server-and-studio.md) §5j rebuilt the console's
/// create surface as a three-screen dialog on the owner's reference; the review that followed found
/// two defects in my execution of it and one older one underneath. A fix without a test is how
/// [D-46](../../docs/architecture-debt.md), D-71, D-74 and D-83 all came back.
/// </para>
/// <para>
/// <b>What is deliberately not asserted here is the shape.</b> Whether the second screen uses radio
/// rows and a <c>Next</c> is the owner's decision, recorded in §5j against my own objection, and a
/// test that pinned it would be a test asserting a preference. These three assert that the thing on
/// screen can be operated.
/// </para>
/// </remarks>
public sealed class AddItemDialogTests : ConsoleTest
{
    /// <summary>
    /// A surface's page action is one element, not two with one id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-91, and it had been true since the router was written.</b> <c>route()</c> wrote the
    /// action's markup into both page-head slots — <c>#pageAction</c> on Server's services screen and
    /// <c>#pageActionContent</c> on Studio's content screen — with a comment explaining that asking for
    /// both saved the router from knowing which view was visible. The cost it did not name is that the
    /// document then held two <c>id="newLayer"</c> nodes, one of them inside a hidden section.
    /// </para>
    /// <para>
    /// <b>Nobody had been bitten, which is why it survived.</b> Click handling reads
    /// <c>event.target.id</c> and a person cannot press what they cannot see, so the defect was
    /// invisible from the outside. <c>getElementById</c> is not: it returns the first in document
    /// order, which is the hidden one. The review's own first script pressed it and timed out.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/studio/#/content", "newLayer")]
    [InlineData("/server/#/services", "newService")]
    public async Task A_surface_has_one_page_action_and_it_is_the_visible_one(string address, string id)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync(address, token);

        // <b>One expression, because two left a gap and the gap was real.</b> Asking *is it visible*
        // and then *how many are there* failed on Server with the button visible and the count zero:
        // the console redraws when its own data arrives, so the two questions were answered about two
        // different documents. `ClickAsync` learned this first and its comment says the same thing —
        // polling one atomic claim removes the gap rather than narrowing it.
        //
        // The claim is both halves at once: exactly one, and it is the one on screen. A count alone
        // would pass if the visible copy were deleted and the hidden one kept.
        await WaitForAsync(
            $$"""
              (() => {
                const all = document.querySelectorAll('#{{id}}');
                return all.length === 1 && all[0].offsetParent !== null;
              })()
              """,
            $"'{address}' never had exactly one visible #{id}. Either the action never rendered, or "
            + "there are two — which is what this test exists for: the router used to write the "
            + "markup into both page heads, leaving a copy inside a hidden view for getElementById "
            + "to find first. D-91.");

        // Reported rather than asserted, because the wait above is the assertion. This makes a
        // failure elsewhere in the class readable instead of leaving a bare count behind.
        string where = await Browser.EvaluateAsync<string>(
            $$"""
              (() => {
                const e = document.getElementById('{{id}}');
                return (e?.closest('.view')?.id || 'nowhere')
                     + (e?.closest('.view')?.classList.contains('on') ? ' (on)' : ' (hidden)');
              })()
              """) ?? "nothing";

        Assert.EndsWith("(on)", where, StringComparison.Ordinal);

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    /// <summary>
    /// The button that finishes the work is in the dialog's footer, and on screen without scrolling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured off the bottom of the screen, not argued.</b> The three route forms were lifted
    /// whole out of the drawer they used to live in, each keeping its own inline submit at the end of
    /// its markup. In a drawer the whole panel scrolled together; in this dialog the chrome is fixed
    /// and only the body scrolls, so <c>Publish</c> sat below the fold at 1024×720 in the form's
    /// <em>default</em> state — while <c>Back</c> and <c>Cancel</c>, which throw the work away, stayed
    /// pinned in view.
    /// </para>
    /// <para>
    /// <b>1024×720 is the size the finding was made at</b>, and it is a real laptop rather than a
    /// contrived one. The viewport is overridden through the debugger rather than by launching a second
    /// browser, and cleared afterwards so the next test in the class sees the suite's own window.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("design", "Create empty layer")]
    [InlineData("registered", "Publish")]
    [InlineData("import", "Import and publish")]
    public async Task The_primary_is_in_the_footer_and_above_the_fold(string route, string label)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new
        {
            width = 1024,
            height = 720,
            deviceScaleFactor = 1,
            mobile = false,
        });

        try
        {
            await WalkToAsync(route);

            // <b>In the footer, which is the fix rather than a restatement of it.</b> A primary that
            // is visible today because the form happens to be short is not fixed; a primary in the
            // chrome cannot scroll away whatever the body does.
            Assert.True(
                await Browser.EvaluateAsync<bool>(
                    "!!document.querySelector('#addItemFoot #itemSubmit')"),
                $"The {route} route's primary is not in the dialog's footer. It was inline at the end "
                + "of the form, which is how it left the screen at this size.");

            Assert.Equal(
                label,
                await Browser.EvaluateAsync<string>(
                    "document.getElementById('itemSubmit').textContent.trim()"));

            // On screen, and asked of the geometry rather than of `offsetParent` — a control below the
            // fold is visible by every other measure, which is exactly why this defect shipped.
            bool onScreen = await Browser.EvaluateAsync<bool>(
                """
                (() => {
                  const box = document.getElementById('itemSubmit').getBoundingClientRect();
                  return box.top >= 0 && box.bottom <= window.innerHeight
                      && box.left >= 0 && box.right <= window.innerWidth;
                })()
                """);

            Assert.True(
                onScreen,
                $"The {route} route's primary is outside a 1024×720 viewport. Back and Cancel are in "
                + "the footer, so the two buttons that abandon the work are reachable and the one "
                + "that completes it is not.");

            string[] errors = await PageErrorsAsync();
            NothingWentWrong(errors);
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    /// <summary>
    /// Changing screen leaves focus inside the dialog rather than on the document body.
    /// </summary>
    /// <remarks>
    /// <b>Because a redraw dropped it.</b> Each screen replaces <c>#addItemBody</c>'s markup wholesale,
    /// which destroys whatever held focus — so <c>document.activeElement</c> was <c>&lt;body&gt;</c>
    /// after the tile and again after <c>Next</c>. A screen-reader user was told nothing about the
    /// screen having changed, and a keyboard user's focus ring vanished until the next Tab. The repair
    /// focuses the heading, which is what names the new screen and does nothing when pressed.
    /// </remarks>
    [Fact]
    public async Task Focus_follows_the_screen_rather_than_falling_to_the_body()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/studio/#/content", token);

        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered.");

        await ClickAsync("#newLayer");

        await WaitForAsync(
            "document.getElementById('kindFeatureLayer')?.offsetParent !== null",
            "The dialog never opened.");

        // <b>Opening lands on the drop zone's button, not the close cross.</b> `showModal` honours
        // `autofocus` inside the dialog; the browser's own default is the first tabbable element,
        // which is the ✕ — and a stray Enter on opening then dismissed the dialog.
        Assert.Equal(
            "fromDevice",
            await Browser.EvaluateAsync<string>("document.activeElement?.id || ''"));

        await ClickAsync("#kindFeatureLayer");

        await WaitForAsync(
            "document.querySelectorAll('.pickrow').length === 3",
            "The Create a feature layer screen never drew its options.");

        await AssertFocusIsInsideAsync("after the Feature layer tile");

        await ClickAsync("#itemNext");

        await WaitForAsync(
            "document.getElementById('dName')?.offsetParent !== null",
            "Next never reached the first route's form.");

        await AssertFocusIsInsideAsync("after Next");

        string[] errors = await PageErrorsAsync();
        NothingWentWrong(errors);
    }

    private async Task AssertFocusIsInsideAsync(string when)
    {
        string where = await Browser.EvaluateAsync<string>(
            """
            (() => {
              const active = document.activeElement;
              if (!active) return 'nothing';
              const inside = document.getElementById('addItem')?.contains(active);
              return (inside ? 'inside:' : 'outside:') + (active.id || active.tagName);
            })()
            """) ?? "nothing";

        Assert.StartsWith("inside:", where, StringComparison.Ordinal);
    }

    /// <summary>Walks New item, Feature layer, the named option, Next.</summary>
    private async Task WalkToAsync(string route)
    {
        await WaitForAsync(
            "document.querySelectorAll('#contentScopes a').length > 0",
            "The content screen never rendered, so its page action is not there to press.");

        await ClickAsync("#newLayer");

        await WaitForAsync(
            "document.getElementById('kindFeatureLayer')?.offsetParent !== null",
            "The New item dialog did not open.");

        await ClickAsync("#kindFeatureLayer");

        await WaitForAsync(
            $"document.querySelector('.pickrow input[value=\"{route}\"]')?.offsetParent !== null",
            $"There is no '{route}' option on the Create a feature layer screen.");

        await ClickAsync($".pickrow input[value=\"{route}\"]");
        await ClickAsync("#itemNext");

        await WaitForAsync(
            "document.getElementById('itemSubmit') !== null",
            "Next did not reach a form with a primary action.");
    }
}
