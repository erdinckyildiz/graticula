using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-102: the Studio item is one page, and every setting has one home — checked step by step.
/// </summary>
/// <remarks>
/// <b>Each test is one step's promise, asserted on the mechanism</b> — widths measured, addresses read back,
/// doors counted against what the server says the item holds — so that it passes on any fixture and fails on
/// the fault it names.
/// </remarks>
public sealed class ItemStructureTests : ConsoleTest
{
    private static string Service() =>
        Environment.GetEnvironmentVariable("GRATICULA_TEST_MULTILAYER") ?? "hosted/ci_EarlyAlert";

    /// <summary>Step 0: at 768 pixels the item page does not scroll sideways, and states its sharing once.</summary>
    [Fact]
    public async Task At_768_the_item_page_fits_and_says_its_sharing_once()
    {
        (string token, _) = await SignInAsync();

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride",
            new { width = 768, height = 900, deviceScaleFactor = 1, mobile = false });

        try
        {
            await OpenAsync($"/studio/#/service/{Service()}", token);

            await WaitForAsync("!!document.querySelector('#svcFacts dt')", "The item page never drew its facts.");

            int[] widths = await Browser.EvaluateAsync<int[]>(
                "[document.documentElement.scrollWidth, document.documentElement.clientWidth]") ?? [];

            Assert.True(widths.Length == 2 && widths[0] <= widths[1],
                $"At 768 the page is {widths[0]} wide in a {widths[1]} window — it scrolls sideways (813 before ADR-102).");

            bool pill = await Browser.EvaluateAsync<bool>(
                "(() => { const p = document.getElementById('serviceScope'); return !!p && !p.hidden && p.offsetParent !== null; })()");

            Assert.False(pill, "The header still shows a sharing pill; in Studio the level is stated in Overview's details.");
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride", new { });
        }
    }

    /// <summary>Step 1: one Layer select for the item; the address keeps tab and layer; no /limits refusal.</summary>
    [Fact]
    public async Task The_layer_is_the_items_and_the_address_keeps_it()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{Service()}?tab=data", token);

        await WaitForAsync(
            "!!document.querySelector('#itemLayer option') && !document.getElementById('itemLayerField').hidden",
            "The item head has no Layer select on the Data tab of a service with several layers.");

        // The last layer the select offers — from the page's own list, not from a name this test knows.
        string last = await Browser.EvaluateAsync<string>(
            "(() => { const s = document.getElementById('itemLayer'); const v = s.options[s.options.length - 1].value;"
            + " s.value = v; s.dispatchEvent(new Event('change', { bubbles: true })); return v; })()") ?? "";

        await WaitForAsync($"/[?&]layer={last}(&|$)/.test(location.hash)", "Choosing a layer did not write it into the address.");

        await ClickAsync("[data-service-tab=\"visualization\"]");

        await WaitForAsync(
            $"document.getElementById('itemLayer').value === '{last}' && /tab=visualization/.test(location.hash)",
            "On Visualization the item's layer is not the one chosen on Data.");

        await Browser.EvaluateAsync<bool>("(location.reload(), true)");

        await WaitForAsync(
            $"!!document.querySelector('#itemLayer option') && document.getElementById('itemLayer').value === '{last}'"
            + " && !document.getElementById('serviceVis').hidden",
            "After a reload the item did not open on the same tab and layer.");

        bool asked = await Browser.EvaluateAsync<bool>(
            "performance.getEntriesByType('resource').some(e => e.name.includes('/limits?'))");

        Assert.False(asked, "The item page asked /limits of a feature service, which answers 404 on every page.");
    }

    /// <summary>Step 6: Manage tiles on Overview opens Settings › Tile layer, and Pre-built counts only what is cached.</summary>
    [Fact]
    public async Task Manage_tiles_opens_the_Tile_layer_section_and_prebuilt_is_what_is_cached()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_LARGE") ?? "hosted/ci_many";

        await OpenAsync($"/studio/#/service/{service}", token);

        await WaitForAsync("!!document.querySelector('[data-manage-tiles]')",
            "Overview has no Manage tiles button for a service with tiles; the cache was reachable only through a layer.");

        await ClickAsync("[data-manage-tiles]");

        await WaitForAsync(
            "!!document.querySelector('#page-tiles.on #tilesStatus dl') && /section=tiles/.test(location.hash)",
            "Manage tiles did not open Settings › Tile layer with the cache's status.");

        // The verification blocker, as the mechanism: a level seeded and since emptied is not pre-built.
        bool[] summary = await Browser.EvaluateAsync<bool[]>(
            "(() => { const a = prebuiltSummary([{ zoom: 3, lastSeeded: '2026-09-30T00:00:00Z', cached: 0 }]);"
            + " const b = prebuiltSummary([{ zoom: 3, lastSeeded: '2026-09-30T00:00:00Z', cached: 1 }]);"
            + " return [a.warm.length === 0, a.cleared, b.warm.length === 1, !b.cleared]; })()") ?? [];

        Assert.Equal([true, true, true, true], summary);

        NothingWentWrong(await PageErrorsAsync());
    }
}
