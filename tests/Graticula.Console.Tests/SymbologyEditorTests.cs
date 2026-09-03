using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The symbology editor's controls change the document, ask for a picture, and show it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner request 2026-09-03: *bana gui editör lazım*.</b> The page had a JSON box, the derived
/// `drawingInfo` and colour swatches — an editor for somebody who already knows MapLibre. What it
/// did not have is controls that name what they do and a picture of the result, and the picture is
/// the half that decides: a swatch says what colour, not what the map looks like at the size
/// anybody sees it.
/// </para>
/// <para>
/// <b>This suite never lets a click reach the server</b> — <c>ConsoleTest.Plant</c> traps every
/// non-`GET` and answers `200 {}`, so that no test can change a shared fixture by accident. That
/// is not an obstacle to work around: it decides where each half of this screen is proved. The
/// page's half — does it ask, with what, and does it show what came back — is here, against an
/// answer this test hands it. The server's half — that a candidate document really is drawn, and
/// differently from the stored one — is
/// <c>Graticula.Conformance.Tests.SymbologyPreviewDrawsTheCandidateTests</c>, where pixels can be
/// counted. Neither pretends to cover the other.
/// </para>
/// <para>
/// <b>The trap is also why the preview checks its content type.</b> Answering an `&lt;img&gt;`
/// with `application/json` produces a broken-image glyph, which reads as *this style draws
/// nothing* — a worse message than the truth. Measured here first, and the console now says
/// *not available* instead.
/// </para>
/// </remarks>
public sealed class SymbologyEditorTests : ConsoleTest
{
    /// <summary>
    /// A one-pixel PNG, with room to make each answer distinguishable from the last.
    /// </summary>
    /// <remarks>
    /// <b>Trailing bytes after `IEND` are ignored by every decoder</b>, so the same picture can
    /// be handed back at a different length. That is what lets the test tell *the page drew the
    /// answer to this request* from *the page is still showing the previous one*, without the
    /// test having to encode a real map.
    /// </remarks>
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAE"
        + "hQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task Choosing_a_colour_rewrites_the_document_and_asks_for_a_new_picture()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        // <b>Waited on the control, not on the state line.</b> The page writes the state as
        // soon as it knows it and fills the form afterwards, so *not Reading* is true before
        // the controls exist — which is a readiness signal for a different thing.
        await WaitForAsync(
            "document.querySelector('#symClasses .symfill') !== null",
            "The Symbology page never drew its controls, so the editor is a document box again.");

        await AnswerPreviewsAsync();

        // <b>The controls are the ones a reader would use</b>, driven the way a reader drives
        // them: set the value, fire `change`, and let the page do the rest.
        bool set = await Browser.EvaluateAsync<bool>(
            "(() => {"
            + " const fill = document.querySelector('#symClasses .symfill');"
            + " if (!fill) return false;"
            + " fill.value = '#dc143c';"
            + " fill.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        Assert.True(set, "There is no colour control on the Symbology page.");

        // <b>The document box is the single source of truth, so it must have been rewritten.</b>
        // If the controls and the box could disagree, Store would be a coin toss.
        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '').includes('220')",
            "Choosing a colour did not rewrite the document below it, so the controls and the "
            + "thing that gets stored are two different states.");

        // <b>The picture, which is what somebody choosing a colour is choosing.</b>
        await WaitForAsync(
            "document.getElementById('symPreview')"
            + " && !document.getElementById('symPreview').hidden"
            + " && document.getElementById('symPreview').naturalWidth > 0",
            "The preview never drew. The editor can show a colour without showing the map, "
            + "which is the state this screen was in before the editor existed.");

        string src = await Browser.EvaluateAsync<string>(
            "document.getElementById('symPreview').src") ?? string.Empty;

        Assert.StartsWith("data:image/png", src, StringComparison.Ordinal);

        // <b>What was asked for, not merely that something was asked.</b> A preview that posts
        // the stored document on every edit would look identical from the outside and would show
        // the same picture forever.
        string asked = await Browser.EvaluateAsync<string>(
            "(window.__previewBodies || []).join(' ')") ?? string.Empty;

        Assert.Contains("220", asked, StringComparison.Ordinal);

        // <b>And the picture is the answer to *that* request.</b> Changing the colour again has
        // to change what is on screen; a page that drew once and stopped passes every assertion
        // above.
        int length = src.Length;

        await Browser.EvaluateAsync<bool>(
            "(() => {"
            + " const fill = document.querySelector('#symClasses .symfill');"
            + " fill.value = '#2e8b57';"
            + " fill.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(window.__previewBodies || []).length >= 2",
            "Changing the colour a second time asked for no new picture, so the preview draws "
            + "once and then stops following the controls.");

        await WaitForAsync(
            "document.getElementById('symPreview').src.length !== " + length,
            "The picture did not change when the colour did, so the preview is showing one "
            + "answer regardless of what was asked.");

        await WaitForAsync(
            "(document.getElementById('symPreviewState')?.textContent || '')"
            + ".includes('Not stored')",
            "The preview does not say it is unsaved, so a reader cannot tell what they are "
            + "looking at from what would be kept.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_preview_that_is_not_a_picture_is_said_rather_than_shown()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "document.querySelector('#symClasses .symfill') !== null",
            "The Symbology page never drew its controls.");

        // <b>No stub here, so the suite's own write trap answers `200 {}`.</b> That is the
        // shape anything in front of this console can produce — a proxy, a portal's sign-in
        // page — and the console must not hand it to an `<img>`.
        await Browser.EvaluateAsync<bool>(
            "(() => {"
            + " const fill = document.querySelector('#symClasses .symfill');"
            + " fill.value = '#dc143c';"
            + " fill.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(document.getElementById('symPreviewState')?.textContent || '')"
            + ".includes('not available')",
            "A JSON answer to the preview was not reported. An empty frame beside a colour "
            + "reads as *this style draws nothing*, which is a worse message than the truth.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('symPreview').hidden"),
            "A non-picture was assigned to the preview image, which shows a broken-image glyph.");

        // <b>The controls still work.</b> Losing the picture is not losing the editor, and the
        // document is what gets stored either way.
        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '').includes('220')",
            "The document stopped following the controls when the preview failed.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task Store_sends_what_the_controls_built()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "document.querySelector('#symClasses .symfill') !== null",
            "The Symbology page never drew its controls.");

        await Browser.EvaluateAsync<bool>(
            "(() => {"
            + " const fill = document.querySelector('#symClasses .symfill');"
            + " fill.value = '#2e8b57';"
            + " fill.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '').includes('46')",
            "The colour never reached the document.");

        // <b>What the button sends is read back before it is clicked</b>, because the trap
        // answers every write the same way and the assertion has to be about the request.
        string sending = await Browser.EvaluateAsync<string>(
            "document.getElementById('symDoc').value") ?? string.Empty;

        Assert.Contains("46", sending, StringComparison.Ordinal);
        Assert.Contains("139", sending, StringComparison.Ordinal);
        Assert.Contains("87", sending, StringComparison.Ordinal);

        await ClickAsync($"button[data-symbology-put={JsonSerializer.Serialize(layer)}]");

        await WaitForAsync(
            "(window.__writes || []).some(w => w.startsWith('PUT ')"
            + " && w.includes('/symbology'))",
            "Store did not `PUT` the symbology, so the button that keeps an appearance keeps "
            + "nothing.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A layer with nothing stored opens with controls that describe what it is drawn with now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first-run state, which is the state most layers are in.</b> ADR-033 gives an
    /// unstyled layer a `GeneratedSymbology` derived from its identity, so there is always
    /// something to show — but *always* is a claim about a code path, and the page that shows it
    /// is a different one from the page that shows a stored document.
    /// </para>
    /// <para>
    /// <b>Visible, not merely present.</b> This console has shipped a control that existed in
    /// the document and rendered nowhere three separate times, and every time the tests were
    /// green because they asked whether the element was there. `offsetParent` is the question
    /// that would have failed.
    /// </para>
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task An_unstyled_layer_opens_with_visible_controls_and_says_it_is_generated()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnUnstyledLayerAsync(token);

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "document.querySelector('#symClasses .symfill') !== null",
            $"'{layer}' has no stored symbology and its Symbology page drew no controls, so the "
            + "editor only works on layers somebody has already styled.");

        bool visible = await Browser.EvaluateAsync<bool>(
            "(() => {"
            + " const fill = document.querySelector('#symClasses .symfill');"
            + " const kind = document.getElementById('symKind');"
            + " return !!fill && fill.offsetParent !== null"
            + " && !!kind && kind.offsetParent !== null; })()");

        Assert.True(
            visible,
            "The controls are in the document and none of them is on screen. That is the exact "
            + "shape this console has shipped three times, and it passes every assertion that "
            + "asks whether an element exists.");

        string state = await Browser.EvaluateAsync<string>(
            "document.getElementById('symState')?.textContent || \"\"") ?? string.Empty;

        Assert.Contains("enerated", state, StringComparison.Ordinal);
    }

    /// <summary>
    /// A symbol is edited layer by layer, and the layers can be reordered.
    /// </summary>
    /// <remarks>
    /// <b>ADR-052 §3.7, shaped after the reference the owner named.</b> The form used to offer
    /// one outline and one size shared by every class, which is a symbol one layer deep — and
    /// the canonical document has held more than that since the CIM reversal. The only way to
    /// author a road with a casing under it was to type JSON.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task A_symbol_is_built_from_layers_that_can_be_added_and_reordered()
    {
        (string token, _) = await SignInAsync();
        string layer = await ALayerOfAsync(token, "LineString");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "document.querySelectorAll('#symStack .symlayer').length > 0",
            "The Symbology page drew no symbol layers, so a symbol cannot be built from more "
            + "than one.");

        int before = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('#symStack .symlayer').length");

        // <b>A second stroke is the case the whole reversal was for.</b> A road is a wide
        // casing under a narrow fill, and it is two strokes or it is not a road.
        await ClickAsync("#symStackActions [data-add-layer='CIMSolidStroke']");

        await WaitForAsync(
            $"document.querySelectorAll('#symStack .symlayer').length === {before + 1}",
            "Adding a stroke did not add a row.");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '')"
            + ".split('CIMSolidStroke').length - 1 >= 2",
            "The document does not carry two strokes, so the row was drawn and not stored.");

        // <b>Order is the point of a stack.</b> Two layers whose order cannot be changed are
        // two layers only one of which anybody sees.
        string first = await Browser.EvaluateAsync<string>(
            "document.querySelector('#symStack .symlayer .symlayercolour').value") ?? "";

        await Browser.EvaluateAsync<bool>(
            "(() => { const c = document.querySelectorAll('#symStack .symlayercolour');"
            + " c[c.length - 1].value = '#1e1e1e';"
            + " c[c.length - 1].dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '').includes('30')",
            "The layer's own colour never reached the document.");

        await ClickAsync("#symStack .symlayer:last-child .symup");

        await WaitForAsync(
            "document.querySelector('#symStack .symlayer .symlayercolour').value === '#1e1e1e'",
            "Moving a layer up did not move it: the row that was last is not now first.");

        Assert.NotEqual("#1e1e1e", first);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The library offers this geometry's symbols and applying one changes the document.
    /// </summary>
    /// <remarks>
    /// <b>ADR-052 §3.8.</b> A complex symbol is not something anybody builds twice by hand, so
    /// the console ships sets. Only the ones the geometry can be drawn with: a line layer
    /// offered an area fill is a gallery that mostly does not work, and finding that out costs
    /// a click each time.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task The_library_offers_this_geometrys_symbols_and_applying_one_is_stored()
    {
        (string token, _) = await SignInAsync();
        string layer = await ALayerOfAsync(token, "LineString");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "document.querySelectorAll('#symGallery .symcard').length > 0",
            "The Symbology page shows no symbol sets, so a complex symbol has to be typed.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector('#symGallery .symcard').offsetParent !== null"),
            "The library is in the document and not on screen.");

        // <b>Each card draws itself.</b> A grid of empty boxes is a gallery nobody can choose
        // from, and it would pass an assertion that only counted the cards.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#symGallery .symcard')]"
                + ".every(c => c.querySelector('svg') && c.querySelector('svg').children.length > 0)"),
            "A symbol card drew nothing, so the gallery is a row of blank buttons.");

        // <b>The road with a casing is the case ADR-052 was decided for.</b>
        await ClickAsync("#symGallery [data-symbol='line-casing']");

        await WaitForAsync(
            "document.querySelectorAll('#symStack .symlayer').length === 2",
            "Choosing the road with a casing did not put two layers in the stack.");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '')"
            + ".split('CIMSolidStroke').length - 1 === 2",
            "The chosen symbol never reached the document, so Store would keep the old one.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A point layer is not offered line symbols.
    /// </summary>
    /// <returns>The task.</returns>
    [Fact]
    public async Task The_library_shows_only_the_sets_this_geometry_can_be_drawn_with()
    {
        (string token, _) = await SignInAsync();
        string points = await ALayerOfAsync(token, "Point");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(points)}/symbology", token);

        await WaitForAsync(
            "document.querySelectorAll('#symGallery .symcard').length > 0",
            "The point layer's Symbology page shows no symbol sets.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector(\"#symGallery [data-symbol='line-casing']\") === null"),
            "A point layer is offered a line symbol, which it cannot be drawn with.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector(\"#symGallery [data-symbol='point-haloed']\") !== null"),
            "A point layer is not offered any point symbol.");
    }

    /// <summary>
    /// The layers a geometry can carry are the ones offered, and a marker has a size.
    /// </summary>
    /// <returns>The task.</returns>
    [Fact]
    public async Task The_stack_offers_what_the_geometry_can_be_drawn_with()
    {
        (string token, _) = await SignInAsync();

        string points = await ALayerOfAsync(token, "Point");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(points)}/symbology", token);

        await WaitForAsync(
            "document.querySelector('#symStack .symsize') !== null",
            $"'{points}' draws markers and its symbol offers no size, so the one dimension a "
            + "point symbol has cannot be set.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector('#symStack .symsize').offsetParent !== null"),
            "The marker's size is in the document and not on screen.");

        string lines = await ALayerOfAsync(token, "LineString");

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(lines)}/symbology", token);

        await WaitForAsync(
            "document.querySelector('#symStack .symwidth') !== null",
            $"'{lines}' is drawn as strokes and its symbol offers no width.");

        // <b>A line has no size and a marker has no width.</b> Offering both to both is the
        // cheap thing to build and it is what makes a form read as generated.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector('#symStack .symsize') === null"),
            "A line symbol is offered a marker size, which it has no use for.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>A published layer with no stored symbology.</summary>
    /// <param name="token">The reader's token.</param>
    /// <returns>Its name.</returns>
    private async Task<string> AnUnstyledLayerAsync(string token) =>
        await ALayerWhereAsync(token, "s => s.stored === false");

    /// <summary>A published layer of one geometry, with no stored symbology.</summary>
    /// <param name="token">The reader's token.</param>
    /// <param name="geometry">What it is made of.</param>
    /// <returns>Its name.</returns>
    private async Task<string> ALayerOfAsync(string token, string geometry) =>
        await ALayerWhereAsync(
            token,
            $"s => s.geometry === {JsonSerializer.Serialize(geometry)}");

    /// <summary>
    /// The first layer whose symbology answers a test.
    /// </summary>
    /// <remarks>
    /// <b>Asked of the server the console talks to</b>, rather than named as a constant, so that
    /// this fails as *no such layer is published* rather than as a 404 about a fixture that
    /// moved.
    /// </remarks>
    /// <param name="token">The reader's token.</param>
    /// <param name="predicate">A JavaScript test over one symbology document.</param>
    /// <returns>The layer's name.</returns>
    private async Task<string> ALayerWhereAsync(string token, string predicate)
    {
        await OpenAsync("/studio/#/", token);

        string found = await Browser.EvaluateAsync<string>(
            "(async () => {"
            + " const t = sessionStorage.getItem('gis-token');"
            + " const h = t ? { Authorization: 'Bearer ' + t } : {};"
            + " const r = await fetch('/admin/layers', { headers: h });"
            + " if (!r.ok) return '';"
            + " const d = await r.json();"
            + " const all = (Array.isArray(d) ? d : (d.layers || [])).map(l => l.name);"
            + " for (const n of all) {"
            + "   const s = await fetch('/admin/layers/' + encodeURIComponent(n) + '/symbology',"
            + "     { headers: h });"
            + "   if (!s.ok) continue;"
            + "   const doc = await s.json();"
            + $"   if (({predicate})(doc)) return n;"
            + " }"
            + " return ''; })()") ?? string.Empty;

        Assert.False(
            string.IsNullOrEmpty(found),
            $"No published layer satisfies `{predicate}`, so this test cannot run. It FAILS "
            + "rather than skips: a green tick that means *not checked* is worse than a red one.");

        return found;
    }

    /// <summary>
    /// Hands the page a real picture for every preview, and records what it asked for.
    /// </summary>
    /// <remarks>
    /// <b>Layered over the plant's trap rather than replacing it.</b> Everything that is not a
    /// preview still goes to the trap, so this test cannot write to the fixture by taking the
    /// safety net off to see its own screen.
    /// </remarks>
    private async Task AnswerPreviewsAsync() =>
        await Browser.EvaluateAsync<bool>(
            $$"""
            (() => {
              const trapped = window.fetch;
              const png = atob({{JsonSerializer.Serialize(OnePixelPng)}});

              window.__previewBodies = [];

              window.fetch = async (input, init) => {
                const url = typeof input === "string" ? input : input.url;

                if (!url.includes("/symbology/preview")) { return trapped(input, init); }

                window.__previewBodies.push(String((init && init.body) || ""));

                // <b>A multiple of three.</b> Base64 rounds up to the next group of four
                // characters, so one extra byte and two extra bytes encode to a string of
                // exactly the same length — measured here as *the picture did not change*
                // against a page that had redrawn it correctly.
                const extra = 3 * window.__previewBodies.length;
                const bytes = new Uint8Array(png.length + extra);

                for (let i = 0; i < png.length; i++) { bytes[i] = png.charCodeAt(i); }

                return new Response(bytes, {
                  status: 200,
                  headers: { "Content-Type": "image/png" },
                });
              };

              return true;
            })();
            """);
}
