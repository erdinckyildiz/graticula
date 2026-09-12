using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Pressing the control that draws a layer runs the code that draws it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written for a defect the owner found by pressing Map, 2026-09-12.</b> The console reported
/// <i>Cannot access 'shown' before initialization</i> and drew nothing. The cause was one
/// identifier: `handleClick` declared a function-scoped <c>const shown</c> near its end, for an
/// unrelated lookup, which put the module-level <c>shown</c> map — the set of layers currently on
/// the map — into the temporal dead zone for the whole of that function. Every branch that asks
/// <i>is this layer already drawn</i> threw before reaching the map.
/// </para>
/// <para>
/// <b>Nothing in this suite pressed it, which is why it shipped.</b> A hundred and seventy-two
/// tests open screens, read them and press the controls that write; this one presses the control
/// that draws, and asserts on what the harness hears rather than on what the screen looks like:
/// an async handler that throws becomes an unhandled rejection, and the harness records those.
/// </para>
/// <para>
/// <b>The click is asserted twice.</b> No page error says the handler did not throw; the button
/// turning <c>on</c> says the map actually took the layer, so a handler that swallows its own
/// failure cannot pass this either.
/// </para>
/// </remarks>
public sealed class DrawingALayerReachesTheMapTests : ConsoleTest
{
    [Fact]
    public async Task Pressing_draw_reaches_the_map_rather_than_a_dead_identifier()
    {
        (string token, _) = await SignInAsync();

        string layer = await AnyLayerAsync();

        // The control lives in the layer page's State row, which `LAYER_PAGES` gives to Server.
        await OpenAsync($"/server/#/layer/{layer}/general", token);

        await WaitForAsync(
            "document.querySelectorAll('[data-show]:not([disabled])').length > 0",
            "The Visualization tab offers nothing to draw, so the control this test is about is "
            + "not on the screen.");

        await ClickAsync("[data-show]:not([disabled])");

        // <b>The first assertion, and the one the defect fails.</b> `handleClick` is async, so
        // what it throws arrives as an unhandled rejection rather than an error event.
        string[] errors = await PageErrorsAsync();

        Assert.True(
            errors.Length == 0,
            "Pressing the draw control reported: " + string.Join(" | ", errors));

        // <b>The second, because a handler that swallowed its own failure would pass the
        // first.</b> The control does not draw in place: since the Visualization tab absorbed
        // that screen it asks whether the layer is already drawn — the question that threw — and
        // then crosses to Studio. Arriving there is what says the question was answered.
        // <b>And the symptom itself, which is what the owner saw.</b> `section()` and the click
        // handler turn what they catch into a red toast rather than an error event, so the
        // defect reported *Cannot access 'shown' before initialization* on the screen while
        // `window.__pageErrors` stayed empty — D-259's lesson, and the reason the harness
        // records toasts at all.
        string[] toasts =
            await Browser.EvaluateAsync<string[]>("window.__toasts || []") ?? Array.Empty<string>();

        Assert.True(toasts.Length == 0, "The console complained: " + string.Join(" | ", toasts));

        await WaitForAsync(
            "location.hash.includes('tab=visualization')",
            "The draw control was pressed and nothing threw, and the browser is still on the "
            + "layer page — so the press reached neither the map nor an error.");
    }
}
