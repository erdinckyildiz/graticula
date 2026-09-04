using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The Symbology page, which is the only screen that shows a conversion's losses.
/// </summary>
public sealed class SymbologyPageTests : ConsoleTest
{
    /// <summary>
    /// The page reads itself and shows all three faces of a layer's appearance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three things have to be on the screen at once, and that is the whole design
    /// of the page.</b> ADR-033 §7's second condition is that the derivation reports
    /// its losses; a page showing only the document a person wrote would leave them to
    /// find the losses from a client's rendering later, which is the failure the
    /// condition exists to prevent. So: what is stored, what an ArcGIS client
    /// receives, and what the second cannot carry.
    /// </para>
    /// <para>
    /// <b>An unstyled layer is the case asserted here, because it is the common
    /// one.</b> §5b makes a generated appearance a real answer with a version of 0 —
    /// and a reader who sees an empty editor cannot tell that from a style that failed
    /// to load, which is why the state line says <em>Generated</em> in words.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_symbology_page_shows_what_a_client_receives()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync(
            $"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        // <b>Waiting for the derived document, not for the page.</b> The editor is in
        // the markup from the first paint; what proves the chain worked is that the
        // server answered with a `drawingInfo` and the page rendered it.
        await WaitForAsync(
            "(document.getElementById('symDerived')?.textContent || '').includes('renderer')",
            "The Symbology page never showed what an ArcGIS client receives, so either the "
            + "endpoint refused or the page did not ask.");

        string state = await Browser.EvaluateAsync<string>(
            "document.getElementById('symState')?.textContent || ''") ?? string.Empty;

        Assert.DoesNotContain("Reading", state, StringComparison.Ordinal);

        // Either shape is correct — the layer may or may not carry a document — but
        // the line must say which, in words rather than by being empty.
        Assert.True(
            state.Contains("Generated", StringComparison.OrdinalIgnoreCase)
            || state.Contains("stored document", StringComparison.OrdinalIgnoreCase),
            $"The state line reads '{state}', which says neither that the appearance is "
            + "generated nor that a document is stored. ADR-033 §5b asks for the difference to "
            + "be stated: an empty editor and a failed load look the same.");
    }

    /// <summary>
    /// Storing an Esri <c>drawingInfo</c> puts its losses on the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the condition tested where an operator meets it.</b>
    /// <c>SymbologyConversionTests</c> asserts that the conversion reports a
    /// zoom-varying width; this asserts that a person pasting a style is shown the
    /// report — which is the half a unit test cannot reach, and the half that decides
    /// whether the mitigation is real.
    /// </para>
    /// <para>
    /// <b>It writes, and that is deliberate in a suite that traps writes.</b> The
    /// harness answers non-<c>GET</c> requests from the page itself, so nothing
    /// reaches the server: what is under test is that the page renders the losses it
    /// is handed, not that the server computes them. The server's half is measured in
    /// <c>SymbologyConversionTests</c> and against the running server in ADR-033 §5i.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_conversion_loss_is_shown_under_the_editor()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync(
            $"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "(document.getElementById('symDerived')?.textContent || '').includes('renderer')",
            "The page did not load before the write was attempted.");

        // The trapped answer the page will be handed. Two losses, so the assertion
        // cannot pass on an accidental single line of text.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.fetch = async (input, init) => {
            const method = ((init && init.method) || "GET").toUpperCase();
            if (method !== "PUT") return real(input, init);
            return new Response(JSON.stringify({
              name: "x", service: "x", geometry: "LineString", from: "MapLibre", bytes: 120,
              replaced: false,
              symbology: { version: 8, layers: [] },
              drawingInfo: { renderer: { type: "simple" } },
              losses: [
                "`line-width` varies with zoom. An Esri symbol carries one value.",
                "The style has a `symbol` layer, so it labels features."
              ],
            }), { status: 200, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """);

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("symDoc").value = '{"version":8,"layers":[]}', true)""");

        await ClickAsync($"button[data-symbology-put={JsonSerializer.Serialize(layer)}]");

        await WaitForAsync(
            "!document.getElementById('symLoss').hidden",
            "The losses were returned and the page did not show them. ADR-033's whole "
            + "mitigation for a lossy conversion is that it says what it lost.");

        Assert.Equal(
            2,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#symLossList li').length"));

        Assert.Contains(
            "varies with zoom",
            await Browser.EvaluateAsync<string>(
                "document.getElementById('symLossList').textContent") ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A Store that lands while the page is still reading keeps its answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written from a CI failure that no local run reproduced.</b> Opening this
    /// screen is five round trips — the map's frame, the document, the service's
    /// override, the fields, the picture — and the last of them finishes long after
    /// the screen looks ready. On a warm machine the read is done before anybody can
    /// type; on the runner it was not, and the tail of that read landed on top of a
    /// Store: the losses cleared, and the state line went back to saying nothing was
    /// stored under a document that had just been stored.
    /// </para>
    /// <para>
    /// <b>So the race is made deterministic rather than waited out.</b> The picture is
    /// held until the test lets it go, which puts the read at exactly the point CI
    /// caught it, and the Store happens while it is held. Timing this with a delay
    /// would be the flaky test that teaches its reader to run the suite again, which
    /// is D-60's lesson and stated in <c>ClickAsync</c>'s own remarks.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_store_is_not_undone_by_the_read_it_overtook()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync(
            $"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "(document.getElementById('symDerived')?.textContent || '').includes('renderer')",
            "The page did not load at all, so there was no read to overtake.");

        // The PUT is answered here; the picture — the read's last step before it
        // writes — is held open, and released by the test.
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.__held = false;
          window.__release = null;
          window.__released = false;
          window.fetch = async (input, init) => {
            const method = ((init && init.method) || "GET").toUpperCase();
            if (method === "PUT") {
              return new Response(JSON.stringify({
                name: "x", service: "x", geometry: "LineString", from: "MapLibre", bytes: 120,
                replaced: false,
                symbology: { version: 8, layers: [] },
                drawingInfo: { renderer: { type: "simple" } },
                losses: [
                  "`line-width` varies with zoom. An Esri symbol carries one value.",
                  "The style has a `symbol` layer, so it labels features."
                ],
              }), { status: 200, headers: { "Content-Type": "application/json" } });
            }
            const answer = await real(input, init);
            if (String(input).includes("/symbology/preview") && !window.__held) {
              window.__held = true;
              await new Promise(go => { window.__release = go; });
              window.__released = true;
            }
            return answer;
          };
          return true;
        })();
        """);

        // A read of the same layer, left running on purpose.
        await Browser.EvaluateAsync<bool>(
            $"(loadSymbology({JsonSerializer.Serialize(layer)}), true)");

        await WaitForAsync(
            "window.__held",
            "The read never reached the picture, so it was never in the state CI caught.");

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("symDoc").value = '{"version":8,"layers":[]}', true)""");

        await ClickAsync($"button[data-symbology-put={JsonSerializer.Serialize(layer)}]");

        await WaitForAsync(
            "!document.getElementById('symLoss').hidden",
            "The Store's losses were never drawn, so this test proves nothing about "
            + "whether the read would have cleared them.");

        // Now let the read finish. Everything it writes from here is about the document
        // that was on the server before the Store, and none of it may reach the page.
        await Browser.EvaluateAsync<bool>("(window.__release(), true)");

        await WaitForAsync(
            "window.__released",
            "The held read never resumed.");

        Assert.Equal(
            2,
            await Browser.EvaluateAsync<int>(
                "document.querySelectorAll('#symLossList li').length"));

        Assert.False(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('symLoss').hidden"),
            "The read that the Store overtook cleared the losses on its way out. ADR-033's "
            + "mitigation for a lossy conversion is that the page says what was lost, and a "
            + "page that says it for a quarter of a second has not said it.");
    }
}
