using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-259: the Map Viewer in ArcGIS Map Viewer's shape — the map's tools at the left, the selected layer's settings and
/// the map tools at the right, a layer as a row with ⋯.
/// </summary>
public sealed class WebMapToolbarsTests : ConsoleTest
{
    private static string Service() =>
        (Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings").Trim('/');

    private const string Selected =
        "document.querySelector('#layerList li.sel button[data-act=select]')?.getAttribute('aria-pressed') === 'true'";

    /// <summary>
    /// Selecting a layer opens its Properties at the right; Styles shows that layer's style there; the same tool pressed
    /// again closes the panel and gives the map its column back.
    /// </summary>
    [Fact]
    public async Task Selecting_a_layer_opens_its_settings_in_the_right_panel_and_the_tool_closes_it()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(Service())}", token, cookie);
        await WaitForAsync("wmLayers().length === 1 && (wmRuntime.get(wmLayers()[0]) || {}).status === 'ok'",
            "The service's layer never became readable.");

        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#side")), "The right panel is open before any tool was pressed.");
        double before = await Browser.EvaluateAsync<double>("wmMap.getSize()[0]");

        await ClickAsync("#layerList button[data-act=select]");
        await WaitForAsync(Selected, "Pressing a layer's name did not select it.");
        await WaitForAsync(Shown("#side") + " && document.getElementById('tool-properties').getAttribute('aria-expanded') === 'true'"
            + " && !!document.querySelector('#layerTool input[data-act=opacity]')",
            "Selecting a layer did not open its Properties, with its opacity, at the right.");
        await WaitForAsync($"wmMap.getSize()[0] < {before}", "The map was not told it lost the right panel's column.");

        await ClickAsync("#tool-style");
        await WaitForAsync("!!document.querySelector('#layerTool select[data-style-how]')"
            + " && document.getElementById('sideTitle').textContent.startsWith('Styles')",
            "Styles did not show the selected layer's style in the right panel.");
        Assert.Equal("false", await Browser.EvaluateAsync<string>("document.getElementById('tool-properties').getAttribute('aria-expanded')"));

        await ClickAsync("#tool-style");
        await WaitForAsync("!" + Shown("#side") + $" && wmMap.getSize()[0] === {before}",
            "Styles pressed again did not close the panel and give the map its width back.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A layer's ⋯ opens its actions with the keyboard on the first one, and Escape closes them and gives the keyboard
    /// back to ⋯ — a menu that kept focus when closed is the defect this page has met before.
    /// </summary>
    [Fact]
    public async Task A_layers_menu_takes_the_keyboard_and_Escape_gives_it_back()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(Service())}", token, cookie);
        await WaitForAsync("wmLayers().length === 1 && (wmRuntime.get(wmLayers()[0]) || {}).status === 'ok'",
            "The service's layer never became readable.");

        await ClickAsync("#layerList button[data-act=menu]");
        await WaitForAsync("document.activeElement?.dataset.act === 'zoom'"
            + " && document.querySelector('#layerList button[data-act=menu]').getAttribute('aria-expanded') === 'true'",
            "⋯ did not open the layer's actions with the keyboard on Zoom to.");

        await Browser.EvaluateAsync<bool>(
            "(document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })), true)");
        await WaitForAsync("document.activeElement?.dataset.act === 'menu' && document.querySelector('.lmenu:not([hidden])') === null",
            "Escape did not close the actions and give the keyboard back to ⋯.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A new map with no layers: the right toolbar's layer tools say there is nothing to act on and how to add one,
    /// rather than an empty panel (ADR-259 condition 2's first-run state).
    /// </summary>
    [Fact]
    public async Task On_a_new_map_a_layer_tool_says_how_to_add_a_layer()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync("/studio/webmap.html", token, cookie);
        await WaitForAsync("!!wmState.doc", "The new map never started.");

        Assert.Equal("true", await Browser.EvaluateAsync<string>("document.getElementById('tool-style').getAttribute('aria-disabled')"));
        await ClickAsync("#tool-style");
        await WaitForAsync("!!document.querySelector('#layerTool [data-act=openAdd]')",
            "Styles on a map with no layers did not say how to add one.");

        await ClickAsync("#layerTool [data-act=openAdd]");
        await WaitForAsync(Shown("#pane-add") + " && document.getElementById('tab-add').getAttribute('aria-selected') === 'true'",
            "The way to add a layer did not open Add.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A service's vector tiles are offered on its own row, in sight, and Add can be narrowed to vector tiles.
    /// </summary>
    /// <remarks>
    /// 2026-11-03: the owner could not add the server's vector tile services — Add put them under a closed <i>Other ways
    /// to draw it</i> on each service's row, while the portal lists them as items of their own.
    /// </remarks>
    [Fact]
    public async Task Add_offers_a_services_vector_tiles_in_sight_and_filters_by_kind()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync("/studio/webmap.html", token, cookie);
        await WaitForAsync("!!wmState.doc", "The new map never started.");

        await ClickAsync("#tab-add");
        const string Tiles = "#addList button[data-type=VectorTileServer]";
        await WaitForAsync($"[...document.querySelectorAll('{Tiles}')].some(b => b.getClientRects().length > 0)",
            "Add offered no service's vector tiles where they can be seen.");

        // The panel says which tool has it, and a folder is said once over its services rather than on every row (UX
        // review of ADR-259).
        Assert.Equal("Add layers", await Browser.EvaluateAsync<string>("document.getElementById('panelTitle').textContent"));
        Assert.True(await Browser.EvaluateAsync<bool>(
            "[...document.querySelectorAll('#addList li.folder > h3')].filter(h => h.textContent === 'hosted').length === 1"
            + " && [...document.querySelectorAll('#addList .name')].every(n => !n.textContent.includes('/'))"),
            "Add did not put the hosted services under one hosted heading.");

        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.getElementById('addType'); s.value = 'VectorTileServer'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await WaitForAsync("document.querySelectorAll('#addList > li').length > 0"
            + " && [...document.querySelectorAll('#addList [data-type]')].every(b => b.dataset.type === 'VectorTileServer')",
            "Choosing Vector tiles did not narrow Add to vector tiles.");

        await ClickAsync(Tiles);
        await WaitForAsync("wmLayers().some(l => l.layerType === 'VectorTileLayer')",
            "A service's vector tiles were not added to the map as a vector tile layer.");

        // What the Add did stays said: the list's redraw after it wrote the service count over it, errors included
        // (UX review 2026-11-03).
        await WaitForAsync("!document.querySelector('#addList [aria-disabled=true]')", "The Add never finished.");
        Assert.StartsWith("Added 1 layer", await Browser.EvaluateAsync<string>("document.getElementById('addStatus').textContent") ?? "");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Transparency is what the slider says and moves: none at full opacity, more to the right (ADR-259's desktop review,
    /// 2026-11-04, found the label reading the opacity once the slider moved). And one Escape closes one thing: a pop-up
    /// open over a tool's panel closes alone.
    /// </summary>
    [Fact]
    public async Task Transparency_reads_as_it_moves_and_one_Escape_closes_one_thing()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(Service())}", token, cookie);
        await WaitForAsync("wmLayers().length === 1 && (wmRuntime.get(wmLayers()[0]) || {}).status === 'ok'",
            "The service's layer never became readable.");

        await ClickAsync("#layerList button[data-act=select]");
        await WaitForAsync("!!document.querySelector('#layerTool input[data-act=opacity]')", "Properties did not show the layer's transparency.");
        Assert.Equal("0", await Browser.EvaluateAsync<string>("document.querySelector('#layerTool input[data-act=opacity]').value"));

        await Browser.EvaluateAsync<bool>("""
            (() => {
              const slider = document.querySelector('#layerTool input[data-act=opacity]');
              slider.value = '30';
              slider.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """);
        await WaitForAsync("Math.abs(wmLayers()[0].opacity - 0.7) < 1e-9"
            + " && document.querySelector('#layerTool [id^=opSaid-]').textContent === '30%'"
            + " && document.querySelector('#layerTool input[data-act=opacity]').getAttribute('aria-valuetext') === '30% transparent'",
            "Thirty per cent transparency did not draw the layer at 70 % opacity and say 30 %.");

        await Browser.EvaluateAsync<bool>("""
            (() => {
              document.getElementById('identify').hidden = false;
              document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
              return true;
            })()
            """);
        await WaitForAsync("document.getElementById('identify').hidden", "Escape did not close the pop-up.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#side")), "One Escape closed the pop-up and the tool's panel together.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// On a phone the right toolbar is a column at the map's edge and both toolbars open the same sheet, one at a time.
    /// </summary>
    [Fact]
    public async Task On_a_phone_the_two_toolbars_share_one_sheet()
    {
        (string token, string cookie) = await SignInAsync();
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });

        try
        {
            await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(Service())}", token, cookie);
            await WaitForAsync("wmLayers().length === 1", "The service's layer was not added.");

            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#panel")), "On a phone the left panel is open before a tool was pressed.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "(() => { const t = document.getElementById('rtools').getBoundingClientRect(), m = document.getElementById('map').getBoundingClientRect();"
                + " return t.right <= m.right && t.left > m.left + m.width / 2 && t.top >= m.top; })()"),
                "On a phone the right toolbar is not at the map's right edge.");

            await ClickAsync("#tab-layers");
            await WaitForAsync(Shown("#panel"), "Layers did not open its sheet.");

            // One Layer tool in the column; the sheet switches between the layer's settings (UX review 2026-11-03: eight
            // unlabelled icons, half of them under a tall sheet).
            Assert.False(await Browser.EvaluateAsync<bool>(Shown("#tool-filter")), "On a phone the column still has a tool per layer setting.");
            await ClickAsync("#tool-properties");
            await WaitForAsync(Shown("#side") + " && !" + Shown("#panel"), "The Layer tool opened its sheet over Layers' instead of in its place.");
            await ClickAsync("#layerSwitch [data-tool=filter]");
            await WaitForAsync("document.getElementById('sideTitle').textContent.startsWith('Filter')"
                + " && document.querySelector('#layerSwitch [data-tool=filter]').getAttribute('aria-pressed') === 'true'",
                "The sheet's switch did not show the layer's Filter.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "(() => { const t = document.getElementById('rtools').getBoundingClientRect(), s = document.getElementById('side').getBoundingClientRect();"
                + " return t.bottom <= s.top; })()"),
                "On a phone the sheet covers the right toolbar.");

            // × from a setting whose own button is not shown on a phone gives the keyboard to Properties, which stands
            // for it, rather than to the page (ADR-259's phone review, 2026-11-04).
            await ClickAsync("#sideClose");
            await WaitForAsync("!" + Shown("#side") + " && document.activeElement?.id === 'tool-properties'",
                "× from Filter did not give the keyboard back to the Layer tool.");

            await ClickAsync("#tab-layers");
            await WaitForAsync(Shown("#panel") + " && !" + Shown("#side"), "Layers did not open its sheet.");

            // The table is not opened under the sheet, where all but its first row were hidden.
            await ClickAsync("#layerList button[data-act=menu]");
            await ClickAsync("#layerList [data-act=table]");
            await WaitForAsync(Shown("#tablePanel") + " && !" + Shown("#panel"),
                "Show table opened the table under the Layers sheet.");
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }
}
