using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A layer larger than the viewer's ceiling is read for what the view shows, and read again when the
/// view moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-10-10, when 1,270,971 Istanbul buildings drew as rectangular blocks that no pan
/// or zoom changed.</b> <c>view.js</c> read every layer once from <c>where=1=1</c> and stopped at
/// 50,000 rows, so the picture was the first 50,000 rows of a spatially sorted file wherever the
/// owner went. Nothing on the page was wrong except the picture, and the header even said
/// <em>more exist</em>.
/// </para>
/// <para>
/// <b>The count is made large in the browser rather than by seeding a large layer.</b> The fixture's
/// biggest layer is six hundred squares, and seeding fifty thousand rows to cross a constant in a
/// script would test the seed. What is under test is the viewer's branch on the count and what it
/// asks for afterwards, so the count answer is rewritten and every query the page sends is kept.
/// </para>
/// </remarks>
public sealed class ViewerReadsALargeLayerForTheViewTests : ConsoleTest
{
    private const string Facts = "(document.getElementById('facts')?.textContent || '')";

    /// <summary>
    /// Every count answer says sixty thousand, and every layer document can be given a minimum scale.
    /// </summary>
    private const string LargeLayer = """
        (() => {
          const inner = window.fetch.bind(window);
          window.__queries = [];
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const path = new URL(url, location.href);
            if (path.pathname.endsWith("/query")) {
              if (path.searchParams.get("returnCountOnly") === "true") {
                return new Response(JSON.stringify({ count: 60000 }),
                  { status: 200, headers: { "Content-Type": "application/json" } });
              }
              window.__queries.push(path.search);
            }
            const response = await inner(input, init);
            if (window.__minScale && /\/FeatureServer\/\d+$/.test(path.pathname)) {
              const body = await response.clone().json().catch(() => null);
              if (body && !body.error) {
                body.minScale = window.__minScale;
                return new Response(JSON.stringify(body),
                  { status: response.status, headers: { "Content-Type": "application/json" } });
              }
            }
            return response;
          };
        })();
        """;

    [Fact]
    public async Task A_layer_over_the_ceiling_is_read_for_the_view_and_again_when_it_moves()
    {
        (string token, string cookie) = await SignInAsync();
        string service = await AnyQueryableServiceAsync();

        await Browser.PlantAsync(LargeLayer);
        await OpenAsync($"/studio/view.html?service={Uri.EscapeDataString(service)}", token, cookie);

        await WaitForAsync(
            $"{Facts}.includes('read for the view') && window.__queries.length > 0 && !{Facts}.includes('Reading')",
            "A layer the count said was over the ceiling was not read for the view. The viewer either "
            + "read it from its first row, as it did before 2026-10-10, or never asked at all.");

        Assert.True(
            await Browser.EvaluateAsync<bool>("window.__queries.every(q => q.includes('geometry='))"),
            "A query for a layer over the ceiling went out without the view's envelope — that is the "
            + "read of the first 50,000 rows that drew the same blocks wherever the map was.");

        int before = await Browser.EvaluateAsync<int>("window.__queries.length");
        string first = await Browser.EvaluateAsync<string>("window.__queries[window.__queries.length - 1]") ?? "";

        await Browser.EvaluateAsync<string>("""
            (() => {
              const view = map.getView();
              const [x, y] = view.getCenter();
              view.setCenter([x + view.getResolution() * 400, y]);
              return 'panned';
            })()
            """);

        await WaitForAsync(
            $"window.__queries.length > {before}",
            "Panning asked for nothing. The picture stays what the first view read, which is the "
            + "defect this test exists for.");

        string after = await Browser.EvaluateAsync<string>("window.__queries[window.__queries.length - 1]") ?? "";

        Assert.NotEqual(first, after);
    }

    [Fact]
    public async Task Outside_the_layers_visible_range_nothing_is_asked_and_the_page_says_how_far_to_come_in()
    {
        (string token, string cookie) = await SignInAsync();
        string service = await AnyQueryableServiceAsync();

        // One to ten: closer than any framing of a layer will land, so the opening view is outside it.
        await Browser.PlantAsync(LargeLayer + "\nwindow.__minScale = 10;");
        await OpenAsync($"/studio/view.html?service={Uri.EscapeDataString(service)}", token, cookie);

        await WaitForAsync(
            $"{Facts}.includes('Zoom in to 1:10')",
            "Zoomed out past the layer's minimum scale, the viewer did not say how far to come in.");

        Assert.Equal(0, await Browser.EvaluateAsync<int>("window.__queries.length"));
    }

    /// <summary>The fixture's queryable layer, whose service has features to draw.</summary>
    private static Task<string> AnyQueryableServiceAsync()
    {
        string? named = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE");

        Assert.True(
            !string.IsNullOrWhiteSpace(named),
            "GRATICULA_TEST_QUERYABLE is not set, so there is no layer known to hold features.");

        return Task.FromResult(named!);
    }
}
