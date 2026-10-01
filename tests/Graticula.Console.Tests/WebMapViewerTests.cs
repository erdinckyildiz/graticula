using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The web map viewer — ADR-079 — opened on a saved map and asked whether it ran and drew its layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same three questions <see cref="ViewerPageTests"/> asks of the other two viewers</b>, for
/// the same reason: a page with its own script is outside <see cref="EveryScreenTests"/>, and a parse
/// error in it leaves a static header over an empty panel with nothing to say why. So: the page threw
/// nothing, and the layer list its script fills is filled.
/// </para>
/// <para>
/// <b>The map is saved through the API, not through the page.</b> This harness answers every write the
/// page makes with an empty document and never sends it, so a map saved by clicking would not exist to
/// open. Saving is asserted by <c>WebMapEndpointTests</c>; this is about opening.
/// </para>
/// <para>
/// <b>One of its layers is one nobody can read</b>, because ADR-079 §3 says such a layer is shown as
/// <em>not available to you</em> rather than hidden, and a viewer that dropped it would pass every
/// other assertion here.
/// </para>
/// </remarks>
public sealed class WebMapViewerTests : ConsoleTest
{
    /// <summary>A client of its own, because the harness keeps its own private.</summary>
    private static readonly HttpClient Reader = new(new HttpClientHandler
    {
        // The suite's server presents a self-signed certificate, as the harness's own client allows.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private const string LayerNames =
        "[...document.querySelectorAll('#layerList > li[data-layer] .lhead label')].map(l => l.textContent.trim())";

    [Fact]
    public async Task A_saved_map_opens_draws_its_layers_and_names_the_one_it_cannot_read()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-079 console test",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new { id = "readable", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "Readable layer", visibility = true, opacity = 1 },
                    new
                    {
                        id = "unreadable",
                        layerType = "ArcGISFeatureLayer",
                        url = $"{Root}/rest/services/zz_adr079_nobody/FeatureServer/0",
                        title = "Nobody's layer",
                        visibility = true,
                        opacity = 1,
                    },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);

            await WaitForAsync(
                $"{LayerNames}.length === 2",
                "The layer list never showed the map's two layers. The script either did not run or did "
                + "not reach the document.");

            // <b>Both of the viewer's waiting words, because it has two.</b> A layer that has not been
            // asked for yet says *Waiting to load…* and one being read says *Loading…* — this waited for
            // the second alone, so it returned while the first was still on the screen and the assertion
            // below read a list nothing had resolved. Caught on CI 2026-09-23, and it is a wait that was
            // passing for the wrong reason rather than a new defect.
            await WaitForAsync(
                "!/Waiting to load|Loading/.test(document.getElementById('layerList').innerText)",
                "A layer is still loading after the wait; its document was never read.");

            string[] names = await Browser.EvaluateAsync<string[]>(LayerNames) ?? [];

            // Top of the list is top of the map, which is the document's last layer.
            Assert.Equal(["Nobody's layer", "Readable layer"], names);

            string said = await Browser.EvaluateAsync<string>(
                "document.getElementById('layerList').innerText") ?? string.Empty;

            Assert.Contains("Not available to you", said, StringComparison.Ordinal);

            Assert.Equal(
                "ADR-079 console test",
                (await Browser.EvaluateAsync<string>("document.getElementById('title').textContent") ?? "").Trim());

            await WaitForAsync(
                Shown("#layerList"),
                "The layer list is in the markup and not on screen.");

            // <b>The recurring defect: a redraw drops focus to the body.</b> Moving the readable layer up
            // redraws the list; the keyboard must still be on that layer's controls.
            await Browser.EvaluateAsync<string>(
                "(() => { const b = document.querySelector('#layerList button[data-act=up][data-layer=readable]'); b.focus(); b.click(); return 'ok'; })()");

            await WaitForAsync(
                "(document.activeElement && document.activeElement.dataset.layer) === 'readable'",
                "After moving a layer the keyboard focus was not on that layer any more — the redraw dropped it.");

            // <b>Save is on screen without opening the Map tab</b> — design review 2026-09-19.
            await WaitForAsync(
                Shown("#headSave"),
                "There is no Save in the header. A reader who changed the map had nothing on screen saying "
                + "how to keep it unless they opened the Map tab.");

            // <b>Add keeps the keyboard</b> — the pressed button was disabled, and focus fell to <body>.
            await Browser.EvaluateAsync<string>("(() => { document.getElementById('addLayer').click(); return 'ok'; })()");

            // <b>The first one a person could press</b>, not the first in the document: a service whose
            // features are already on the map offers its other kinds inside a closed disclosure, and a
            // button there cannot take focus. CI's fixture put that service first; this machine's did not.
            // <b>`offsetParent` does not say so</b> — measured 2026-09-19, a button inside a closed
            // `<details>` still has one in Chrome, which is how the first repair of this test failed again
            // in CI. Being outside every closed disclosure is the test that means *on screen*.
            const string VisibleAdd =
                "[...document.querySelectorAll('#addList button[data-add]')]"
                + ".find(b => !b.closest('details:not([open])') && b.getClientRects().length > 0)";

            await WaitForAsync(
                $"!!{VisibleAdd}",
                "The Add layer list never showed a service to add.");

            await Browser.EvaluateAsync<string>(
                $"(() => {{ const b = {VisibleAdd}; b.focus(); b.click(); return 'ok'; }})()");

            await WaitForAsync(
                "document.activeElement && document.activeElement !== document.body"
                + " && document.getElementById('addList').contains(document.activeElement)"
                + " && !document.querySelector('#addList [aria-disabled=true]')",
                "After Add the keyboard focus left the list it was in — dropped to the body by a disabled button.");

            string[] failures = await PageErrorsAsync();

            Assert.True(failures.Length == 0, "webmap.html threw:\n  " + string.Join("\n  ", failures));

            // Moving a layer is an edit to the map, not a write to the server.
            Assert.Empty(await WritesAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>ADR-104: a layer is styled in the map, saved in its document, and can become the layer's default.</summary>
    [Fact]
    public async Task A_layer_is_styled_in_the_map_and_can_become_its_default()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-104 console test",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new { id = "styled", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "Styled layer", visibility = true, opacity = 1 },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);

            await WaitForAsync("!!document.querySelector('#layerList button[data-act=style][data-layer=styled]')",
                "A feature layer on the map offers no Style.");

            await ClickAsync("#layerList button[data-act=style][data-layer=styled]");

            await WaitForAsync("!!document.getElementById('styHow-styled')", "Style did not open the layer's style panel.");

            await Browser.EvaluateAsync<bool>(
                "(() => { const s = document.getElementById('styHow-styled'); s.value = 'single'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

            await WaitForAsync("!!document.getElementById('styColour-styled')", "One colour did not offer a colour.");

            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('styColour-styled').value = '#aa3377'; return true; })()");

            await ClickAsync("#layerList button[data-act=styleApply][data-layer=styled]");

            await WaitForAsync(
                "(wmLayers()[0].layerDefinition || {}).drawingInfo?.renderer?.type === 'simple'",
                "Apply did not put the map's own renderer in the map's document.");

            // Saved with the map, where ArcGIS clients read it. <b>The body is read by wrapping fetch here</b>,
            // because the harness records a write's method and address and, for a JSON body, nothing else — and
            // what this asserts is precisely what the body carries.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const inner = window.fetch;
                  window.__bodies = [];
                  window.fetch = (input, init) => {
                    if (init && typeof init.body === 'string') window.__bodies.push(init.body);
                    return inner(input, init);
                  };
                  return true;
                })()
                """);

            await ClickAsync("#headSave");

            await WaitForAsync(
                "(window.__bodies || []).some(b => b.includes('drawingInfo'))",
                "Saving the map did not send the layer's style in its document.");

            // The layer's default, from the map: the same drawingInfo, to the layer's symbology.
            await WaitForAsync("!!document.querySelector('#layerList button[data-act=styleDefault][data-layer=styled]')",
                "A styled layer offers no Save as the layer's default to a role that may publish.");

            await ClickAsync("#layerList button[data-act=styleDefault][data-layer=styled]");

            await WaitForAsync(
                "(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/symbology'))",
                "Save as the layer's default did not send the style to the layer.");

            await WaitForAsync(
                "!(wmLayers()[0].layerDefinition || {}).drawingInfo",
                "After becoming the default, the map kept its own copy instead of following the layer.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    private static readonly int[] Red = [200, 30, 30, 255];

    /// <summary>ADR-104: a map reopened with a layer styled by value opens its Style on that, not on one colour.</summary>
    [Fact]
    public async Task A_reopened_maps_style_panel_starts_from_the_style_it_has()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-104 reopened",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new
                    {
                        id = "byvalue", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "By value", visibility = true, opacity = 1,
                        layerDefinition = new
                        {
                            drawingInfo = new
                            {
                                renderer = new
                                {
                                    type = "uniqueValue", field1 = "objectid",
                                    uniqueValueInfos = new object[]
                                    {
                                        new { value = "1", label = "One", symbol = new { type = "esriSMS", style = "esriSMSCircle", color = Red, size = 8 } },
                                    },
                                },
                            },
                        },
                    },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);

            await WaitForAsync("!!document.querySelector('#layerList button[data-act=style][data-layer=byvalue]')",
                "The styled layer offers no Style.");

            await ClickAsync("#layerList button[data-act=style][data-layer=byvalue]");

            await WaitForAsync("document.getElementById('styHow-byvalue')?.value === 'unique'",
                "The Style panel of a layer styled by value opened on another kind, where one Apply would replace it.");

            string legend = await Browser.EvaluateAsync<string>("document.querySelector('#sty-byvalue .lslegend')?.innerText || ''") ?? "";

            Assert.Contains("One", legend, StringComparison.Ordinal);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>A web map has its own item page: what it holds with the way to each layer, sharing, and delete.</summary>
    [Fact]
    public async Task A_web_map_has_an_item_page_with_its_layers_and_its_sharing()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "Item page test",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new { id = "one", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "Its layer", visibility = true, opacity = 1 },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/#/map/{id}", token, cookie);

            await WaitForAsync(
                "document.getElementById('mapTitle').textContent === 'Item page test'"
                + " && !!document.querySelector('#mapLayers a[href^=\"#/service/\"]')"
                + " && !!document.querySelector('#mapSide a[href*=\"webmap.html?id=\"]')",
                "The map's item page did not show its title, its layer with the way to that layer's item, and Open in Map Viewer.");

            await ClickAsync("#mapShareOpen");

            await WaitForAsync("document.getElementById('mapShare').open", "Share did not open.");

            await Browser.EvaluateAsync<bool>("(() => { const r = document.querySelector('input[name=mapShareScope][value=organization]'); r.checked = true; r.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

            await ClickAsync("#mapShareSave");

            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/content/webmaps/{id}'))",
                "Saving the map's sharing sent nothing.");

            // Delete protection, as a service has it: the box sends the protection, and Delete waits for it.
            await WaitForAsync("!!document.getElementById('mapProtect')", "The map's page offers no delete protection.");
            await ClickAsync("#mapProtect");
            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/content/webmaps/{id}/protection'))",
                "Protect from deletion sent nothing.");

            // Groups: the fourth scope, with the groups this user may put it in (ADR-079 condition 4). The test
            // makes the group it offers — the fixture has none on CI, and a test that leaned on a leftover group
            // passed locally and failed there (2026-10-01).
            (int grouped, string groupBody) = await AdminAsync(HttpMethod.Post, "/admin/groups",
                JsonSerializer.Serialize(new { name = $"zz_mapshare_{id[..8]}" }));
            Assert.True(grouped is 200 or 201, $"Making a group answered {grouped}: {groupBody}");

            await OpenAsync($"/studio/#/map/{id}", token, cookie);
            await WaitForAsync("!!document.getElementById('mapShareOpen')", "The map's item page did not come back.");

            await ClickAsync("#mapShareOpen");

            await WaitForAsync("document.getElementById('mapShare').open", "Share did not open again.");

            await Browser.EvaluateAsync<bool>(
                "(() => { const r = document.querySelector('input[name=mapShareScope][value=group]'); r.checked = true; r.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

            await WaitForAsync(
                "!document.getElementById('mapShareGroups').hidden && !!document.querySelector('[data-map-group]')",
                "Choosing Groups did not list the groups the map may go into.");

            await Browser.EvaluateAsync<bool>(
                "(() => { const b = document.querySelector('[data-map-group]'); b.checked = true; b.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

            await ClickAsync("#mapShareSave");

            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/maps/{id}'))",
                "Sharing the map into a group sent nothing to the group.");

            // ADR-119: a description, edited with the title and summary.
            // A fresh page: the share's save redraws this one, and an edit opened under it would be drawn over.
            await OpenAsync($"/studio/#/map/{id}", token, cookie);
            await WaitForAsync("!!document.getElementById('mapEditOpen')", "The map's page offers no way to edit it.");
            await ClickAsync("#mapEditOpen");
            await WaitForAsync("!!document.getElementById('mapEditDescription')", "Editing a map offers no description.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('mapEditDescription').value = 'What it is for.', true)");
            await ClickAsync("#mapEditSave");
            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('PUT') && w.includes('/content/webmaps/{id}/description'))",
                "Saving the map's details did not send its description.");

            // And the Map Viewer takes the map's picture — a flat colour is not sent, a view of features is
            // (asserted in A_layer_is_labelled_in_the_map_and_drawn_from_it).
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);
            await WaitForAsync("typeof wmSendThumbnail === 'function' && !!wmMap.getSize()", "The Map Viewer did not open the map.");
            await Browser.EvaluateAsync<bool>($"(window.__writes = [], window.__thumb = null, wmSendThumbnail('{id}').then(r => window.__thumb = r), true)");
            await WaitForAsync("window.__thumb !== null", "Taking the map's picture never finished.");
            // It finishes even though this map holds a layer it cannot read, whose drawing never completes; and it
            // sends a picture only when there is one.
            string thumb = await Browser.EvaluateAsync<string>("window.__thumb") ?? "";
            Assert.True(thumb is "taken" or "empty", $"Taking the picture ended '{thumb}'.");
            Assert.Equal(thumb == "taken", (await WritesAsync()).Any(w => w.Contains("/thumbnail", StringComparison.Ordinal)));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/zz_mapshare_{id[..8]}");
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>ADR-110: a layer's pop-up is set in the map — a title with a field in it, the fields shown.</summary>
    [Fact]
    public async Task A_layers_pop_up_is_set_in_the_map_and_drawn_from_it()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-110 console test",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new { id = "pops", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "Popped", visibility = true, opacity = 1 },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);

            await WaitForAsync("!!document.querySelector('#layerList button[data-act=popup][data-layer=pops]')",
                "A feature layer offers no Pop-up.");

            await ClickAsync("#layerList button[data-act=popup][data-layer=pops]");

            await WaitForAsync("document.querySelectorAll('#pop-pops [data-pop-field]').length > 0",
                "The Pop-up panel lists none of the layer's fields.");

            // Only the first field, and a title made of it.
            string first = await Browser.EvaluateAsync<string>("""
                (() => {
                  const boxes = [...document.querySelectorAll('#pop-pops [data-pop-field]')];
                  boxes.forEach((b, i) => b.checked = i === 0);
                  document.getElementById('popTitle-pops').value = 'Feature {' + boxes[0].dataset.popField + '}';
                  return boxes[0].dataset.popField;
                })()
                """) ?? "";

            // A field that is not there is said, and nothing is applied.
            await Browser.EvaluateAsync<bool>("(() => { const t = document.getElementById('popTitle-pops'); t.dataset.good = t.value; t.value = 'X {nosuchfield}'; return true; })()");

            await ClickAsync("#layerList button[data-act=popupApply][data-layer=pops]");

            await WaitForAsync(
                "/nosuchfield/.test(document.getElementById('popSays-pops')?.textContent || '') && !wmLayers()[0].popupInfo",
                "A title naming a field the layer does not have was applied, or not said.");

            await Browser.EvaluateAsync<bool>($$"""
                (() => {
                  const boxes = [...document.querySelectorAll('#pop-pops [data-pop-field]')];
                  boxes.forEach((b, i) => b.checked = i === 0);
                  document.getElementById('popTitle-pops').value = 'Feature {{{first}}}';
                  return true;
                })()
                """);

            await ClickAsync("#layerList button[data-act=popupApply][data-layer=pops]");

            await WaitForAsync("!!wmLayers()[0].popupInfo && wmLayers()[0].popupInfo.fieldInfos.filter(f => f.visible).length === 1",
                "Apply did not put the pop-up in the map's document.");

            string drawn = await Browser.EvaluateAsync<string>(
                $"wmPopupMarkup(wmLayers()[0], {{ '{first}': 'seven', other: 'hidden' }}, null)") ?? "";

            Assert.Contains("Feature seven", drawn, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", drawn, StringComparison.Ordinal);

            // ADR-118: a number and a date in the format the pop-up gives them.
            string formatted = await Browser.EvaluateAsync<string>("""
                wmPopupMarkup(
                  { popupInfo: { fieldInfos: [
                    { fieldName: 'n', label: 'N', visible: true, format: { places: 2, digitSeparator: true } },
                    { fieldName: 'd', label: 'D', visible: true, format: { dateFormat: 'longMonthDayYear' } } ] } },
                  { n: 1234567.891, d: Date.UTC(1997, 11, 21, 12) },
                  { fields: [{ name: 'n', type: 'esriFieldTypeDouble' }, { name: 'd', type: 'esriFieldTypeDate' }] })
                """) ?? "";

            Assert.Contains("1,234,567.89", formatted, StringComparison.Ordinal);
            Assert.Contains("December 21, 1997", formatted, StringComparison.Ordinal);

            // An unformatted date is still a date, not milliseconds.
            string plain = await Browser.EvaluateAsync<string>(
                "wmFormatValue(Date.UTC(1997, 11, 21, 12), null, 'esriFieldTypeDate')") ?? "";
            Assert.Matches(@"^12/2\d/1997 \d{1,2}:\d{2} (AM|PM)$", plain);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>ADR-117: a layer is labelled in the map with a field's value, written where ArcGIS clients read it.</summary>
    [Fact]
    public async Task A_layer_is_labelled_in_the_map_and_drawn_from_it()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-117 console test",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new { id = "labs", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "Labelled", visibility = true, opacity = 1 },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);
        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");
        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);

            await WaitForAsync("!!document.querySelector('#layerList button[data-act=labels][data-layer=labs]')",
                "A feature layer offers no Labels.");
            await ClickAsync("#layerList button[data-act=labels][data-layer=labs]");
            await WaitForAsync("document.querySelectorAll('#labField-labs option').length > 0",
                "The Labels panel offers none of the layer's fields.");

            // Show labels starts ticked, and a change takes effect at once.
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('labOn-labs').checked"),
                "A layer with no labels opens the panel with Show labels off.");

            string field = await Browser.EvaluateAsync<string>("""
                (() => {
                  const size = document.getElementById('labSize-labs');
                  size.value = '12';
                  size.dispatchEvent(new Event('change', { bubbles: true }));
                  return document.getElementById('labField-labs').value;
                })()
                """) ?? "";

            await WaitForAsync(
                "wmLayers()[0].showLabels === true && wmLayers()[0].layerDefinition.drawingInfo.labelingInfo[0].symbol.font.size === 12",
                "Apply did not put the labels in the map's document.");

            Assert.Equal(field, await Browser.EvaluateAsync<string>("wmLabelOf(wmLayers()[0]).field"));
            Assert.Contains(field, await Browser.EvaluateAsync<string>(
                "wmLayers()[0].layerDefinition.drawingInfo.labelingInfo[0].labelExpressionInfo.expression") ?? "",
                StringComparison.Ordinal);

            // The label is drawn by its own decluttered layer over the same features, so a symbol cannot crowd it out.
            Assert.True(await Browser.EvaluateAsync<bool>("wmRuntime.get(wmLayers()[0]).ol instanceof ol.layer.Group"),
                "The labels are not a layer of their own.");
            Assert.True(await Browser.EvaluateAsync<bool>($$"""
                (() => {
                  const f = new ol.Feature({ geometry: new ol.geom.Point([0, 0]), '{{field}}': 'Ankara' });
                  const style = wmLabelStyle(wmLabelOf(wmLayers()[0]))(f, 1);
                  return !!style && style.getText().getText() === 'Ankara';
                })()
                """), "The labelled style does not draw the field's value.");

            // ADR-119: the picture taken of this view is of something — the layer's features — not one flat colour.
            await ClickAsync("#layerList button[data-act=zoom][data-layer=labs]");
            await WaitForAsync(
                "(() => { const o = wmRuntime.get(wmLayers()[0]).ol; const src = o.getSource(); return src.getFeatures().length > 0; })()",
                "The layer drew no features to take a picture of.");
            await Browser.EvaluateAsync<bool>($"(window.__thumb = null, wmSendThumbnail('{id}').then(r => window.__thumb = r), true)");
            await WaitForAsync("window.__thumb !== null", "Taking the map's picture never finished.");
            Assert.Equal("taken", await Browser.EvaluateAsync<string>("window.__thumb"));

            await ClickAsync("#layerList button[data-act=labelsReset][data-layer=labs]");
            await WaitForAsync("!wmLayers()[0].showLabels && !((wmLayers()[0].layerDefinition || {}).drawingInfo || {}).labelingInfo",
                "Remove labels left them in the document.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>The address of some feature layer this server publishes.</summary>
    private async Task<string> AnyFeatureLayerUrlAsync(string token)
    {
        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();
        HashSet<string> images = await ImageServicesAsync();

        foreach (string service in folders.SelectMany(f => f.Services).Where(s => !images.Contains(s)))
        {
            string at = $"{Root}/rest/services/{service}/FeatureServer";

            using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{at}?f=json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Reader.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using JsonDocument described = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (described.RootElement.TryGetProperty("layers", out JsonElement layers)
                && layers.ValueKind == JsonValueKind.Array
                && layers.EnumerateArray().FirstOrDefault(l =>
                    !(l.TryGetProperty("type", out JsonElement t) && t.GetString() == "Group Layer")) is
                    { ValueKind: JsonValueKind.Object } layer)
            {
                return $"{at}/{layer.GetProperty("id").GetInt32()}";
            }
        }

        Assert.Fail("No feature layer is published, so there is nothing to put on the map.");
        return string.Empty;
    }
}
