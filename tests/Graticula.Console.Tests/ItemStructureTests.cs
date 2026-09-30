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
    [Theory]
    [InlineData(768)]
    [InlineData(390)]
    public async Task At_narrow_widths_the_item_page_fits_and_says_its_sharing_once(int width)
    {
        (string token, _) = await SignInAsync();

        await Browser.CallAsync("Emulation.setDeviceMetricsOverride",
            new { width, height = 900, deviceScaleFactor = 1, mobile = false });

        try
        {
            await OpenAsync($"/studio/#/service/{Service()}", token);

            await WaitForAsync("!!document.querySelector('#svcFacts dt')", "The item page never drew its facts.");

            int[] widths = await Browser.EvaluateAsync<int[]>(
                "[document.documentElement.scrollWidth, document.documentElement.clientWidth]") ?? [];

            Assert.True(widths.Length == 2 && widths[0] <= widths[1],
                $"At {width} the page is {widths[0]} wide in a {widths[1]} window — it scrolls sideways (813 at 768 and 443 at 390 before ADR-102).");

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

    /// <summary>Step 5: Settings is General, Feature layer and Tile layer; deleting is General's; editing is stated.</summary>
    [Fact]
    public async Task Settings_is_General_Feature_layer_and_Tile_layer()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_LARGE") ?? "hosted/ci_many";

        await OpenAsync($"/studio/#/service/{service}?tab=settings", token);

        await WaitForAsync(
            "[...document.querySelectorAll('#serviceNav a')].map(a => a.textContent.trim()).join('|') === 'General|Feature layer|Tile layer'",
            "Settings' sections are not General, Feature layer and Tile layer, in that order.");

        await WaitForAsync(
            "(() => { const d = document.getElementById('serviceDanger'); return !!d && !d.hidden && !!d.closest('#page-general'); })()",
            "Deleting the item is not in General.");

        await ClickAsync("#serviceNav a[data-service-page=\"feature\"]");

        await WaitForAsync(
            "/Set by the server administrator/.test(document.getElementById('featureFacts')?.textContent || '')"
            + " && document.getElementById('serviceDanger').hidden",
            "Feature layer does not say who sets its editing, or the delete panel followed it there.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Owner decisions of 2026-10-01: the owner saves the edits offered and turns protection on, both stored.</summary>
    [Fact]
    public async Task The_owner_chooses_the_edits_and_the_protection_and_both_are_sent()
    {
        (string token, _) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable";

        await OpenAsync($"/studio/#/service/{service}?tab=settings&section=feature", token);

        await WaitForAsync("!!document.getElementById('offerSave') && document.querySelectorAll('[data-offer]').length === 4",
            "Feature layer does not offer the owner the four edits to choose from.");

        await ClickAsync("#offerSave");

        await WaitForAsync("(window.__writes || []).some(w => w.startsWith('PUT ') && w.includes('/editing'))",
            "Saving the edits offered sent nothing to the editing route.");

        await ClickAsync("#serviceNav a[data-service-page=\"general\"]");

        await WaitForAsync("!!document.getElementById('svcLock') && !document.getElementById('svcLock').disabled",
            "General's protection control is missing or not the owner's to change.");

        await Browser.EvaluateAsync<bool>("(document.getElementById('svcLock').click(), true)");

        await WaitForAsync("(window.__writes || []).some(w => w.startsWith('PUT ') && w.includes('/protection'))",
            "Changing the protection sent nothing to the protection route; it would have been this page's memory only.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Step 7: Overview is Portal's shape — the item and its layers on the left, its actions and details on the
    /// right — with no settings on it and no buttons on the layer rows.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-10-01, beside Portal's item page: *"neden kabiliyetler ana sayfada?"*</b> What a client may
    /// do and what one request may spend were two cards on Overview while their homes were Settings and Server.
    /// </remarks>
    [Fact]
    public async Task Overview_holds_the_item_its_layers_its_actions_and_its_details_and_no_settings()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{Service()}", token);

        await WaitForAsync(
            "!!document.querySelector('#serviceDetails .itemactions a.btn.primary') && !!document.querySelector('#svcFacts #svcUrl')",
            "Overview's right column does not hold the actions above the details and the address.");

        bool settingsShown = await Browser.EvaluateAsync<bool>(
            "['serviceOps', 'serviceSpend', 'serviceAddress'].some(id => { const e = document.getElementById(id); return !!e && e.offsetParent !== null; })");

        Assert.False(settingsShown, "Overview still shows the capabilities, the request limits or a separate address card.");

        int rowButtons = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#serviceLayerRows a.tiny, #serviceLayerRows button').length");

        Assert.Equal(0, rowButtons);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Step 9: a layer's fields and history are the item's Data views; removing a layer is Settings'.</summary>
    [Fact]
    public async Task A_layers_fields_and_history_are_Data_views_and_removing_it_is_in_Settings()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{Service()}?tab=data&layer=0&view=fields", token);

        await WaitForAsync(
            "!!document.querySelector('#dataFieldsHost #fieldsRows tr') && !!document.querySelector('#dataRows details.columns')",
            "Data › Fields does not hold the layer's field editor with the columns to add or remove under it.");

        await ClickAsync("[data-data-view=\"history\"]");

        await WaitForAsync(
            "!!document.querySelector('#dataRows #page-history') && /view=history/.test(location.hash)",
            "Data › History did not open the layer's history.");

        await OpenAsync($"/studio/#/service/{Service()}?tab=settings&section=feature", token);

        await WaitForAsync(
            "document.querySelectorAll('#featureLayers [data-delete]').length > 0",
            "Settings › Feature layer offers no way to remove a layer; it was the layer page's Maintenance.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Step 8: exporting is one action on Overview; the Data tab keeps no export buttons of its own.</summary>
    [Fact]
    public async Task Export_data_is_one_action_on_Overview()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{Service()}", token);

        await WaitForAsync("!!document.getElementById('exportDataOpen')", "Overview offers no Export data action.");

        await ClickAsync("#exportDataOpen");

        await WaitForAsync(
            "document.getElementById('exportData').open && document.querySelectorAll('#exportDataLayer option').length > 0",
            "Export data did not open a dialog that names the item's layers.");

        await ClickAsync("#exportDataGo");

        await WaitForAsync(
            "/written to/.test(document.getElementById('exportDataSays').textContent)",
            "The export dialog did not write the chosen layer.");

        await OpenAsync($"/studio/#/service/{Service()}?tab=data&layer=0", token);

        await WaitForAsync("!!document.querySelector('#dataRows table')", "The Data table never drew.");

        int exports = await Browser.EvaluateAsync<int>("document.querySelectorAll('[data-data-export]').length");

        Assert.Equal(0, exports);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Step 10: thumbnail, time column and visible range are the item's; Server's layer page holds none.</summary>
    [Fact]
    public async Task The_publishers_layer_settings_are_in_the_item_and_not_on_Server()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{Service()}", token);

        await WaitForAsync(
            "!!document.querySelector('#serviceDetails #layerThumb') && !!document.querySelector('#serviceDetails [data-redraw-thumb]')",
            "Overview does not show the item's thumbnail with a way to redraw it.");

        await OpenAsync($"/studio/#/service/{Service()}?tab=settings&section=feature", token);

        await WaitForAsync(
            "document.querySelectorAll('#featureLayers [data-time-input]').length > 0",
            "Settings › Feature layer does not hold each layer's time column.");

        string layer = await Browser.EvaluateAsync<string>(
            "document.querySelector('#featureLayers [data-time]').dataset.time") ?? "";

        await OpenAsync($"/studio/#/service/{Service()}?tab=visualization&panel=style", token);

        await WaitForAsync(
            "(() => { const m = document.querySelector('#visStyleHost .symrail #minScale'); if (!m) return false; m.scrollIntoView(); return m.offsetParent !== null && m.getBoundingClientRect().height > 0; })()",
            "The Style panel does not show the layer's visible range — for a layer nobody has styled too.");

        await OpenAsync($"/server/#/layer/{Uri.EscapeDataString(layer)}", token);

        await WaitForAsync("!!document.querySelector('#page-general h4')", "Server's layer page never drew.");

        int left = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#page-general #timeField, #page-general #minScale, #page-general [data-redraw-thumb]').length");

        Assert.Equal(0, left);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>Step 11: Studio draws no layer screen — every layer address it is given opens the item.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("/symbology")]
    [InlineData("/no-such-page")]
    public async Task Studio_opens_the_item_for_every_layer_address(string page)
    {
        (string token, _) = await SignInAsync();

        await OpenAsync($"/studio/#/service/{Service()}?tab=settings&section=feature", token);

        await WaitForAsync("!!document.querySelector('#featureLayers [data-delete]')", "The item's layers never drew.");

        string layer = await Browser.EvaluateAsync<string>(
            "document.querySelector('#featureLayers [data-delete]').dataset.delete") ?? "";

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}{page}", token);

        await WaitForAsync(
            "location.hash.startsWith('#/service/') && document.getElementById('view-service').classList.contains('on')",
            $"Studio did not open the item for #/layer/{layer}{page}.");

        bool layerScreen = await Browser.EvaluateAsync<bool>(
            "document.getElementById('view-layer').classList.contains('on')");

        Assert.False(layerScreen, "Studio drew the layer screen, which ADR-102 step 11 retired.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
