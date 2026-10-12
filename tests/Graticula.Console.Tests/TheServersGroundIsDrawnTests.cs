using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>The map ground changes every map on the server, so what sets it runs alone.</summary>
[CollectionDefinition("server ground", DisableParallelization = true)]
public sealed class ServerGroundRunsAlone;

/// <summary>
/// The operator's ground is what the map pages draw when the browser has not chosen one — Q-110, ADR-086.
/// </summary>
/// <remarks>
/// <b>Set through the real API, not through the screen</b>, because this suite never lets the screen's writes
/// reach the server; what is asserted is what the pages do with a ground that is really stored. Three pages
/// build a ground and each once had its own copy of the rule, which is why each is opened.
/// </remarks>
[Collection("server ground")]
public sealed class TheServersGroundIsDrawnTests : ConsoleTest
{
    private async Task<string[]> PublicTileServicesAsync()
    {
        using HttpResponseMessage response = await Http.GetAsync(new Uri($"{Root}/rest/services/hosted?f=json"));
        using JsonDocument directory = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        string[] names = directory.RootElement.GetProperty("services").EnumerateArray()
            .Where(s => s.GetProperty("type").GetString() == "VectorTileServer")
            .Select(s => s.GetProperty("name").GetString()!)
            .ToArray();

        // Two: one to be the ground, one to look at — the viewer leaves the layer it shows out of its grounds.
        Assert.True(names.Length >= 2, "The fixture has fewer than two public vector tile services.");
        return names;
    }

    [Fact]
    public async Task Every_map_page_draws_the_servers_ground_and_says_it_is_the_servers()
    {
        (string token, string cookie) = await SignInAsync();

        string[] services = await PublicTileServicesAsync();
        string ground = services[0];
        string other = services[1];

        try
        {
            (int set, string said) = await AdminAsync(
                HttpMethod.Put, "/admin/settings/ground", JsonSerializer.Serialize(new { services = new[] { ground } }));

            Assert.True(set == 200, $"Setting the ground answered {set}: {said}");

            // ---- the SDK page names the ground it drew, and it is the server's ----
            await OpenAsync($"/studio/map.html?service={Uri.EscapeDataString(ground)}", token, cookie);

            await WaitForAsync(
                $"(document.getElementById('ground')?.textContent || '').includes({JsonSerializer.Serialize(ground)})",
                "map.html did not draw the server's ground: its caption does not name it.");

            // ---- the OpenLayers viewer draws it too, and says whose it is ----
            await OpenAsync($"/studio/view.html?service={Uri.EscapeDataString(other)}", token, cookie);

            await WaitForAsync(
                "(document.getElementById('grounds')?.textContent || '').includes(\"the server's ground\")",
                "view.html did not draw the server's ground, or did not say it is the server's.");

            // Instead of OpenStreetMap, as the other pages draw it — not over it.
            Assert.DoesNotContain(
                "OpenStreetMap",
                await Browser.EvaluateAsync<string>("document.getElementById('grounds')?.textContent || ''") ?? string.Empty,
                StringComparison.Ordinal);
            Assert.True(
                await Browser.EvaluateAsync<bool>($"document.querySelector('#grounds button[data-ground=\"{ground}\"]')?.getAttribute('aria-pressed') === 'true'"),
                "The viewer draws the server's ground but shows its button as off.");

            // ---- a new web map starts on it, as a new map in Map Viewer starts on the portal's default basemap ----
            // ADR-086 condition 3. Written on 2026-09-30 with ADR-101 and never held by a test, so the condition stood
            // open over code that already did it. The document is read rather than the pixels: what is saved is what
            // Map Viewer and Pro will open, and a ground drawn over an OpenStreetMap document would save the wrong one.
            await OpenAsync("/studio/webmap.html", token, cookie);

            await WaitForAsync(
                "typeof wmState === 'object' && !!wmState.doc && !!wmState.doc.baseMap",
                "The web map editor never made a document for a new map.");

            string[] basemap = await Browser.EvaluateAsync<string[]>(
                "(wmState.doc.baseMap.baseMapLayers || []).map(l => l.layerType + ' ' + (l.id || ''))") ?? [];

            Assert.True(
                basemap.SequenceEqual([$"VectorTileLayer {ground}"]),
                $"A new web map's basemap is [{string.Join(", ", basemap)}], not the server's ground {ground}.");

            Assert.True(
                await Browser.EvaluateAsync<bool>("wmGround.getVisible() && !wmBase.getVisible()"),
                "The new map's document names the server's ground, but the page draws OpenStreetMap under it.");

            // ---- a browser that chose for itself keeps its choice ----
            await Browser.EvaluateAsync<bool>("(() => { localStorage.setItem('gis-ground-tiles', '[]'); return true; })()");
            await OpenAsync($"/studio/map.html?service={Uri.EscapeDataString(ground)}", token, cookie);

            await WaitForAsync(
                "(document.getElementById('ground')?.textContent || '').includes('OpenStreetMap')",
                "A browser that chose no imported ground was given the server's anyway.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await Browser.EvaluateAsync<bool>("(() => { localStorage.removeItem('gis-ground-tiles'); return true; })()");
            await AdminAsync(HttpMethod.Put, "/admin/settings/ground", JsonSerializer.Serialize(new { services = Array.Empty<string>() }));
        }
    }
}
