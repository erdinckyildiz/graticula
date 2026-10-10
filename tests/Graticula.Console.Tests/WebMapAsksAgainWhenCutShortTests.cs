using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The map viewer asks a cut-short feature layer again when the view moves, and asks nothing outside the layer's
/// visible range.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-10-10, when the owner zoomed to street level over 1,270,971 Istanbul buildings and nothing came.</b>
/// OpenLayers' <c>bbox</c> strategy remembers what it has loaded, and the first read was the whole city — answered with
/// its first thousand rows and <c>exceededTransferLimit</c>. Every view inside the city then counted as loaded, so no
/// zoom or pan asked again. The layer's own minimum scale, which would have stopped that first read, was not read.
/// </para>
/// <para>
/// <b>The answers are made cut short in the browser rather than by seeding a large layer</b>, for the reason
/// <see cref="ViewerReadsALargeLayerForTheViewTests"/> gives: what is under test is what the page asks for next.
/// </para>
/// </remarks>
public sealed class WebMapAsksAgainWhenCutShortTests : ConsoleTest
{
    /// <summary>Every query answer says more exist, and a layer document can be given a minimum scale.</summary>
    private const string CutShort = """
        (() => {
          const inner = window.fetch.bind(window);
          window.__queries = [];
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const path = new URL(url, location.href);
            const response = await inner(input, init);
            const query = path.pathname.endsWith("/query") && path.searchParams.has("geometry");
            const layer = window.__minScale && /\/FeatureServer\/\d+$/.test(path.pathname);
            if (!query && !layer) return response;
            if (query) window.__queries.push(path.searchParams.get("geometry"));
            const body = await response.clone().json().catch(() => null);
            if (!body || body.error) return response;
            if (query) body.exceededTransferLimit = true;
            if (layer) body.minScale = window.__minScale;
            return new Response(JSON.stringify(body),
              { status: response.status, headers: { "Content-Type": "application/json" } });
          };
        })();
        """;

    private const string State = "(document.querySelector('.lstate')?.textContent || '')";

    [Fact]
    public async Task A_layer_whose_answer_was_cut_short_is_asked_again_when_the_view_moves()
    {
        (string token, string cookie) = await SignInAsync();
        string service = QueryableService();

        await Browser.PlantAsync(CutShort);
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(service)}", token, cookie);

        await WaitForAsync(
            $"window.__queries.length > 0 && {State}.includes('Showing the first')",
            "The map never drew the layer's first answer and said it was cut short.");

        int before = await Browser.EvaluateAsync<int>("window.__queries.length");

        await Browser.EvaluateAsync<string>("""
            (() => {
              const view = wmMap.getView();
              view.setZoom(view.getZoom() + 1);
              return 'zoomed';
            })()
            """);

        await WaitForAsync(
            $"window.__queries.length > {before}",
            "Zooming in over a layer whose answer was cut short asked for nothing. The view counts as loaded because "
            + "a wider one was, and the reader is left with the first page wherever they go — the defect of 2026-10-10.");
    }

    [Fact]
    public async Task Outside_the_layers_visible_range_nothing_is_asked_and_the_list_says_to_zoom_in()
    {
        (string token, string cookie) = await SignInAsync();
        string service = QueryableService();

        // One to ten: closer than any framing of a layer will land.
        await Browser.PlantAsync(CutShort + "\nwindow.__minScale = 10;");
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(service)}", token, cookie);

        await WaitForAsync(
            $"{State}.includes('Zoom in past 1:10')",
            "Outside the layer's minimum scale, the layer list did not say how far to zoom in.");

        Assert.Equal(0, await Browser.EvaluateAsync<int>("window.__queries.length"));
    }

    private static string QueryableService()
    {
        string? named = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE");

        Assert.True(
            !string.IsNullOrWhiteSpace(named),
            "GRATICULA_TEST_QUERYABLE is not set, so there is no layer known to hold features.");

        return named!;
    }
}
