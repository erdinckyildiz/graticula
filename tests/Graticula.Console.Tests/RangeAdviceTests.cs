using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// D-267: a layer drawn into a tile denser than its range is measured for shows a suggestion beside its Visible range,
/// with Measure and Keep inside it — and answering it, or setting the range, leaves the Studio item where it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-10-29 from the design review</b>, which pressed Keep and was left on a blank page: the range's
/// branches called <c>loadLayers()</c>, the Server editor's refresh, which threw Studio's item view away. Set and No
/// limit had done the same since the range moved into Studio's Style rail, and nothing had pressed them there.
/// </para>
/// <para>
/// The note is put on the page's own listing rather than made on the server, so the suite's layer is only read; the
/// harness traps both writes. What the server notes is the conformance suite's <c>ALayerOutgrowingItsRangeIsSuggestedTests</c>.
/// </para>
/// </remarks>
public sealed class RangeAdviceTests : ConsoleTest
{
    private static string Service() =>
        (Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings").Trim('/');

    [Fact]
    public async Task The_suggestion_is_answered_and_the_range_set_without_leaving_the_item()
    {
        (string token, _) = await SignInAsync();
        string service = Service();
        string serviceName = service[(service.LastIndexOf('/') + 1)..];

        await OpenAsync($"/studio/#/service/{service}", token);
        await WaitForAsync(
            $"known.some(l => String(l.service).toLowerCase() === '{serviceName.ToLowerInvariant()}')",
            "The administrative listing never arrived, so the range has nothing to read.");

        await Browser.EvaluateAsync<bool>($$"""
            (() => {
              const l = known.find(k => String(k.service).toLowerCase() === '{{serviceName.ToLowerInvariant()}}');
              l.denseTile = { level: 0, x: 0, y: 0, features: 12345, noted: new Date().toISOString(), kept: false };
              location.hash = '#/service/{{service}}?tab=visualization&layer=0&panel=style';
              return true;
            })()
            """);

        await WaitForAsync("!!document.getElementById('rangeAdvice')", "A layer with a dense tile showed no suggestion.");
        Assert.True(await Browser.EvaluateAsync<bool>(
            "!!document.querySelector('#rangeAdvice [data-range-suggest]') && !!document.querySelector('#rangeAdvice [data-range-keep]')"),
            "The suggestion does not hold both Measure and Keep.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#rangeMeasure")),
            "Measure is offered twice: in the suggestion and in the row under it.");

        // The Style panel goes on drawing after the rail is in. One run in six on 2026-10-29 found focus elsewhere after
        // Keep, and the five after it did not; what took it was not caught, so the page is let settle before the press.
        await Task.Delay(1500);
        await ClickAsync("#rangeAdvice [data-range-keep]");
        await WaitForAsync("!document.getElementById('rangeAdvice')", "Keep left the suggestion on the page.");
        await WaitForAsync("document.activeElement && document.activeElement.id === 'minScale'", "Keep did not leave focus on the range.");

        // Settled, then looked at: the old refresh moved focus and hid the view a few milliseconds after the box went.
        await Task.Delay(500);
        Assert.True(await Browser.EvaluateAsync<bool>(Shown(".visrange") + " && " + Shown("#rangeSaved")),
            "Keep threw the item view away.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement && document.activeElement.id === 'minScale'"),
            "Focus left the range after Keep: " + await Browser.EvaluateAsync<string>("(() => { const a = document.activeElement; return a ? a.tagName + '#' + a.id + '.' + a.className + ' ' + (a.textContent || '').slice(0, 40) : 'none'; })()"));
        Assert.Contains(await WritesAsync(), w => w.Contains("/visible-range/keep", StringComparison.Ordinal));
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#rangeMeasure")), "With the suggestion gone, the row has no Measure.");

        await ClickAsync(".visrange button[data-range]:not([data-clear])");
        await WaitForAsync("(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/visible-range'))", "Set sent nothing.");
        await Task.Delay(500);
        Assert.True(await Browser.EvaluateAsync<bool>(Shown(".visrange") + " && " + Shown("#rangeSaved")),
            "Set threw the item view away.");
        Assert.DoesNotContain("stays as it is", await Browser.EvaluateAsync<string>("document.getElementById('rangeSays').textContent") ?? "",
            StringComparison.Ordinal);
        Assert.True(await Browser.EvaluateAsync<bool>(
            "document.activeElement && document.activeElement.matches('.visrange button[data-range]:not([data-clear])')"),
            "Set did not keep focus on itself.");
        Assert.Empty(await PageErrorsAsync());
    }

    [Fact]
    public async Task Overviews_pointer_lands_on_the_suggestion_with_focus_on_Measure()
    {
        (string token, _) = await SignInAsync();
        string service = Service();
        string serviceName = service[(service.LastIndexOf('/') + 1)..];

        await OpenAsync($"/studio/#/service/{service}", token);
        await WaitForAsync(
            $"known.some(l => String(l.service).toLowerCase() === '{serviceName.ToLowerInvariant()}')",
            "The administrative listing never arrived, so the range has nothing to read.");

        // The address Overview's link carries (drawServiceLayers), with the note put on the page's own listing.
        await Browser.EvaluateAsync<bool>($$"""
            (() => {
              const l = known.find(k => String(k.service).toLowerCase() === '{{serviceName.ToLowerInvariant()}}');
              l.denseTile = { level: 0, x: 0, y: 0, features: 12345, noted: new Date().toISOString(), kept: false };
              location.hash = '#/service/{{service}}?tab=visualization&layer=0&panel=style&focus=range';
              return true;
            })()
            """);

        await WaitForAsync(
            "document.activeElement && document.activeElement.matches('#rangeAdvice [data-range-suggest]')",
            "Following the pointer did not put focus on the suggestion's Measure.");
        Assert.True(await Browser.EvaluateAsync<bool>(
            "(() => { const r = document.getElementById('rangeAdvice').getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight; })()"),
            "The suggestion is not in view after following the pointer.");
    }
}
