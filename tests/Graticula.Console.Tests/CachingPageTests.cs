using System;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Caching page says what the cache does before it asks for numbers, and a publisher can clear it.
/// </summary>
/// <remarks>
/// <b>Rebuilt 2026-09-30 on the owner's word — the caching logic reads as *çağ dışı* — and the design
/// review that found why:</b> the engine builds tiles on first view and empties them on an edit, while the
/// page led with seconds, hours, megabytes and level numbers 0 to 22. These check the shape that replaced
/// it: the sentence first, levels as scales, the tuning under Advanced, and a clear control that sends
/// the request the server answers.
/// </remarks>
public sealed class CachingPageTests : ConsoleTest
{
    private const string Layer = "GRATICULA_TEST_LARGE";

    [Fact]
    public async Task The_page_leads_with_what_the_cache_does_and_offers_levels_as_scales()
    {
        (string token, _) = await SignInAsync();
        string layer = LayerName();

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/caching", token);

        await WaitForAsync(
            "!!document.querySelector('#seedFrom option') && !!document.querySelector('#tilesStatus dl')",
            "The Caching page never drew its status and pre-build form.");

        string lead = await Browser.EvaluateAsync<string>(
            "document.querySelector('#page-tiles .lede').textContent") ?? "";

        Assert.Contains("first time somebody views an area", lead, StringComparison.Ordinal);

        string[] levels = await Browser.EvaluateAsync<string[]>(
            "[...document.querySelectorAll('#seedFrom option')].map(o => o.textContent)") ?? [];

        Assert.Contains(levels, l => l.StartsWith("1:", StringComparison.Ordinal) && l.Contains("(level 0)", StringComparison.Ordinal));

        // The tuning is present and closed: a reader sees it on asking, not on arriving.
        bool closed = await Browser.EvaluateAsync<bool>(
            "(() => { const d = document.querySelector('#page-tiles details.advanced');"
            + " return !!d && !d.open && !!d.querySelector('#cacheLayers input') && !!d.querySelector('#cacheQuota'); })()");

        Assert.True(closed, "The lifetime and quota are not under a closed Advanced section.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task Clear_cached_tiles_asks_the_server_to_clear_the_service()
    {
        (string token, _) = await SignInAsync();
        string layer = LayerName();

        // <b>Something cached first, so there is something to clear</b> — the control is disabled on an empty cache,
        // and a fixture whose cache happens to be empty made this pass locally and fail in CI. One tile asked for
        // is one entry, drawn or empty.
        string service = Environment.GetEnvironmentVariable(Layer) ?? "hosted/ci_many";
        await AdminAsync(HttpMethod.Get, $"/rest/services/{service}/VectorTileServer/tile/0/0/0.pbf");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/caching", token);

        await WaitForAsync("!!document.getElementById('tilesClear')", "There is no Clear cached tiles control.");

        await Browser.EvaluateAsync<bool>("(window.confirm = () => true, true)");
        await ClickAsync("#tilesClear");

        await WaitForAsync(
            "(window.__writes || []).some(w => /^POST .*\\/cache\\/clear/.test(w))",
            "Pressing Clear cached tiles sent no POST to the service's cache/clear route.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task Choosing_an_area_on_a_map_shows_a_map_to_choose_it_on()
    {
        (string token, _) = await SignInAsync();
        string layer = LayerName();

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/caching", token);

        await WaitForAsync("!!document.getElementById('seedArea')", "The pre-build form has no area choice.");

        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.getElementById('seedArea'); s.value = 'map';"
            + " s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

        // Visible, by its box rather than by `hidden` alone: a control that exists and cannot be seen is the
        // fault this suite has missed three times (groups-screen-first-run-bug).
        await WaitForAsync(
            "(() => { const m = document.getElementById('seedMap'); const r = m && m.getBoundingClientRect();"
            + " return !!r && r.width > 100 && r.height > 100 && !!m.querySelector('canvas'); })()",
            "Choosing 'the area I show on a map' did not show a map. It used to depend on a map on another tab.");

        NothingWentWrong(await PageErrorsAsync());
    }

    private static string LayerName()
    {
        string service = Environment.GetEnvironmentVariable(Layer) ?? "hosted/ci_many";
        return service[(service.LastIndexOf('/') + 1)..];
    }
}
