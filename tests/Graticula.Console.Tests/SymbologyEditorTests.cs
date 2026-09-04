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

        await OpenSymbologyAsync(layer, token);

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

        // <b>The claim is the meaning, and it used to be the wording.</b> This read
        // `includes('Not stored')` and broke on 2026-09-04 when that sentence was removed — it
        // was written on every render and corrected by nothing, so it went on saying *not stored
        // yet* under a document that had just been stored ([D-219](../../docs/architecture-debt.md)).
        // What the test is actually for is unchanged: after an edit, the caption has to
        // distinguish the picture from what is stored. This test paints it — it plants a real
        // PNG, so the caption survives to be read — while the derivation itself is
        // `Editing_moves_the_caption_off_the_stored_appearance`, where the write trap answers
        // the preview with JSON and the painted caption cannot be trusted.
        await WaitForAsync(
            "(document.getElementById('symPreviewState')?.textContent || '')"
            + ".includes('Store would keep')",
            "The preview does not say it is unsaved, so a reader cannot tell what they are "
            + "looking at from what would be kept.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_preview_that_is_not_a_picture_is_said_rather_than_shown()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenSymbologyAsync(layer, token);

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

        // <b>The caption over the picture, not the state line beside Store.</b> Handoff
        // 2026-09-04 splits the two, and they answer two questions: the strip says which of the
        // two appearances the picture is of, and the caption says whether there is a picture and
        // why not when there is not. They were one element, so a preview that failed to draw
        // replaced the sentence that says whether anything is stored.
        await WaitForAsync(
            "(document.getElementById('symPreviewCap')?.textContent || '')"
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

        await OpenSymbologyAsync(layer, token);

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

        // <b>The generated screen, and it has to say the word.</b> A layer nobody has styled is
        // a state rather than an empty form (ADR-033 §5b), so this is what somebody meets — and
        // it is only worth having if it says which state it is in.
        await WaitForAsync(
            "document.getElementById('symEmpty')?.offsetParent != null",
            $"'{layer}' has no stored symbology and its Symbology page did not open on the "
            + "generated screen, so a reader cannot tell an unstyled layer from a styled one.");

        string said = await Browser.EvaluateAsync<string>(
            "document.getElementById('symEmpty')?.textContent || \"\"") ?? string.Empty;

        Assert.Contains("generated", said, StringComparison.OrdinalIgnoreCase);

        // <b>And the way in reveals every control, which is the half that used to be asserted.</b>
        // The form behind this screen is already holding the generated document; pressing the
        // button only stands out of its way, and this is where *the controls are in the document
        // and none of them is on screen* would still be caught.
        await ClickAsync("#symStartGenerated");

        bool visible = await Browser.EvaluateAsync<bool>(
            "(() => {"
            + " const fill = document.querySelector('#symClasses .symfill');"
            + " const kind = document.querySelector('input[name=\"symKind\"]');"
            + " const cards = document.querySelector('.symkinds');"
            + " return !!fill && fill.offsetParent !== null"
            + " && !!kind && !!cards && cards.offsetParent !== null; })()");

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
    /// Opens a layer's Symbology page with its controls on the screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An unstyled layer opens on the generated screen, and every fixture here is
    /// unstyled.</b> Handoff 2026-09-04: §5b makes a generated appearance a real state, so a
    /// layer nobody has styled is met by a sentence saying so and two ways in, rather than by a
    /// form already filled with a document nobody wrote. That screen stands in front of the
    /// three columns, so a test that opens a layer and reaches for a control finds it in the
    /// document and not on the screen — which is true, and is what the screen is for.
    /// </para>
    /// <para>
    /// <b>The step is here rather than in each test, and it is not skipped when it is not
    /// needed.</b> A styled layer never shows the screen; pressing a button that is not there
    /// would be the kind of step that quietly does nothing, so it is asked for first.
    /// <see cref="An_unstyled_layer_opens_with_visible_controls_and_says_it_is_generated"/> is
    /// where that screen is the subject rather than the obstacle.
    /// </para>
    /// </remarks>
    /// <param name="layer">The layer.</param>
    /// <param name="token">The session.</param>
    /// <returns>The task.</returns>
    private async Task OpenSymbologyAsync(string layer, string token)
    {
        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/symbology", token);

        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            $"'{layer}' never filled the symbology form.");

        bool generated = await Browser.EvaluateAsync<bool>(
            "document.getElementById('symEmpty')?.offsetParent != null");

        if (generated)
        {
            await ClickAsync("#symStartGenerated");
        }

        await WaitForAsync(
            "document.getElementById('symCols')?.offsetParent != null",
            $"'{layer}' shows neither the generated screen nor the editor, so the Symbology page "
            + "opened on nothing at all.");
    }

    /// <summary>
    /// Chooses a renderer family the way somebody with a mouse does.
    /// </summary>
    /// <remarks>
    /// <b>Three radio cards, and they were a `select`.</b> Handoff 2026-09-04: the family is the
    /// biggest decision on the page and the three answers look different from each other, so
    /// they are shown rather than named behind a click. Setting `value` on a radio group does
    /// nothing — there is no element to set — which is exactly the kind of silently dead test
    /// step this helper exists to prevent from being written three times.
    /// </remarks>
    /// <param name="family">`simple`, `uniqueValue` or `classBreaks`.</param>
    /// <returns>The task.</returns>
    private async Task PickRendererAsync(string family)
    {
        bool moved = await Browser.EvaluateAsync<bool>(
            "(() => { const k = document.querySelector("
            + $"'input[name=\"symKind\"][value=\"{family}\"]');"
            + " if (!k) return false;"
            + " k.checked = true;"
            + " k.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

        Assert.True(moved, $"There is no renderer card for '{family}'.");
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

        await OpenSymbologyAsync(layer, token);

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
    /// A colour's opacity can be set, and it is the fourth number of the colour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner question 2026-09-04: *bir de renklere alpha verebiliyor muyuz?*</b> The format
    /// always could — <c>CIMRGBColor.values</c> is <c>[r, g, b, alpha]</c>, both derived faces
    /// carry it, and the renderer draws it — but this console could not say it, because an
    /// <c>input type="color"</c> hands back six hex digits and has no fourth channel. So the
    /// answer was *the server yes, the editor no*, which is not an answer anybody can use.
    /// </para>
    /// <para>
    /// <b>Nought to a hundred, which is CIM's scale.</b> The ArcGIS REST face puts alpha on
    /// 0–255 with the other three; the stored document does not, and this box edits the stored
    /// document. The test asserts the stored number rather than a published one for that reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_opacity_reaches_the_colour_it_belongs_to()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenSymbologyAsync(layer, token);

        await WaitForAsync(
            "document.querySelector('#symStack .symalpha') !== null",
            "The symbol's layers never carried an opacity box.");

        // <b>Set through the box the way a person sets it</b>, rather than by calling the
        // handler: the point in question is whether the control is wired at all.
        await Browser.EvaluateAsync<bool>(
            "(() => { const a = document.querySelector('#symStack .symalpha');"
            + " a.value = '35';"
            + " a.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(() => { const d = document.getElementById('symDoc');"
            + " if (!d) return false;"
            + " return JSON.stringify(JSON.parse(d.value)).includes(',35]'); })()",
            "The opacity never reached the document as the fourth number of a colour.");

        // <b>And it is the fourth number, not a fifth property.</b> A colour that grew a
        // separate `opacity` beside its values would be read by nothing downstream.
        bool onTheColour = await Browser.EvaluateAsync<bool>(
            "(() => { const d = JSON.parse(document.getElementById('symDoc').value);"
            + " const walk = o => {"
            + "   if (!o || typeof o !== 'object') return false;"
            + "   if (o.type === 'CIMRGBColor' && Array.isArray(o.values))"
            + "     return o.values.length === 4 && o.values[3] === 35;"
            + "   return Object.values(o).some(walk); };"
            + " return walk(d); })()");

        Assert.True(
            onTheColour,
            "The document has 35 in it somewhere, but not as the alpha of a CIMRGBColor, which "
            + "is the only place anything reads it from.");

        // <b>Changing the colour afterwards keeps the opacity.</b> The swatch rebuilds the
        // colour from six hex digits and would drop the fourth number unless it is carried,
        // which is the fault this control exists to make visible.
        await Browser.EvaluateAsync<bool>(
            "(() => { const c = document.querySelector('#symStack .symlayercolour');"
            + " c.value = '#123456';"
            + " c.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(() => { const d = JSON.parse(document.getElementById('symDoc').value);"
            + " const walk = o => {"
            + "   if (!o || typeof o !== 'object') return false;"
            + "   if (o.type === 'CIMRGBColor' && Array.isArray(o.values))"
            + "     return o.values[0] === 18 && o.values[3] === 35;"
            + "   return Object.values(o).some(walk); };"
            + " return walk(d); })()",
            "Choosing a new colour reset the opacity to opaque, so the two controls fight.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// One opacity reaches every class, and setting it twice leaves it where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>*alpha da bir şeye yaramıyor gibi* — the opacity doesn't seem to do anything.</b> It
    /// did: it set the opacity of the one class that happened to be selected, out of eighty-one,
    /// on a row that was not among the ten on screen. Every control on this page acted on
    /// exactly one class, and the work somebody classifying a map by province is doing is
    /// almost never about one province.
    /// </para>
    /// <para>
    /// <b>It sets rather than scales, and the test says so.</b> A control that multiplied each
    /// class's alpha by a fraction would keep the relative differences somebody had chosen, and
    /// would not be idempotent: 50 % twice leaves 25 %, so the number in the box would have no
    /// stable meaning. Pressing Set twice here has to leave the document exactly as one press
    /// left it, and that is asserted rather than described.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task One_opacity_reaches_every_class_and_pressing_it_twice_changes_nothing()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenSymbologyAsync(layer, token);

        // <b>Waited on the model, not on the element.</b> `#symKind` is in the static markup, so
        // it exists before the layer's document has been fetched — and the change handler returns
        // early while the form is still filling, so a click here does nothing and the failure
        // arrives several assertions later wearing somebody else's name.
        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            "The symbology form never filled.");

        // <b>Three classes, so *every class* is a claim with something to prove.</b>
        await PickRendererAsync("uniqueValue");

        await WaitForAsync(
            "document.getElementById('symAddClass')?.offsetParent != null",
            "A classified renderer offers no way to add a class.");

        await ClickAsync("#symAddClass");
        await ClickAsync("#symAddClass");

        await WaitForAsync(
            "document.querySelectorAll('#symClasses .symclass').length === 3",
            "Adding classes did not add rows.");

        await WaitForAsync(
            "document.getElementById('symAllRow')?.offsetParent != null",
            "The control that acts on every class is not on the screen, which is the whole of "
            + "the fault it exists to fix.");

        await Browser.EvaluateAsync<bool>(
            "(() => { const a = document.getElementById('symAllAlpha');"
            + " a.value = '37.5'; return true; })()");

        await ClickAsync("#symAllAlphaApply");

        // <b>Every colour in the document, not the first one found.</b>
        await WaitForAsync(
            "(() => { const d = JSON.parse(document.getElementById('symDoc').value);"
            + " const all = [];"
            + " const walk = o => {"
            + "   if (!o || typeof o !== 'object') return;"
            + "   if (o.type === 'CIMRGBColor' && Array.isArray(o.values)) all.push(o.values[3]);"
            + "   Object.values(o).forEach(walk); };"
            + " walk(d);"
            + " return all.length >= 3 && all.every(v => v === 37.5); })()",
            "Some class kept its own opacity, so the control acts on a selection rather than on "
            + "every class.");

        string once = await Browser.EvaluateAsync<string>(
            "document.getElementById('symDoc').value") ?? "";

        await ClickAsync("#symAllAlphaApply");

        await WaitForAsync(
            "document.getElementById('symAllSays').textContent.length > 0",
            "The second press said nothing, so it may not have run at all.");

        string twice = await Browser.EvaluateAsync<string>(
            "document.getElementById('symDoc').value") ?? "";

        Assert.Equal(once, twice);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The symbol panel never names a class the list is not showing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner could not read this screen: *ya ben bu ekranı cidden anlayamıyorum. çok
    /// karmaşık*.</b> A class list bounded to ten visible rows out of as many as 256 sat above a
    /// permanently rendered symbol editor, and the editor said which class it was editing in its
    /// own heading — *Symbol layers — Ankara* — because nothing else could. Ankara was usually
    /// not one of the ten, so the panel was titled after a row nobody could see.
    /// </para>
    /// <para>
    /// <b>D-217 answered that by showing one or the other, and the handoff answers it by
    /// adjacency.</b> 2026-09-04: the list and the stack are both in the inspector's 336-pixel
    /// column, the chosen row keeps its accent edge, and the heading names the class. What is
    /// asserted here is therefore the property both answers were protecting rather than either
    /// answer's mechanism — <b>the panel's subject is a row that is on the screen</b> — because
    /// that is the thing the owner reported and the thing a third rearrangement could break.
    /// </para>
    /// <para>
    /// <b>Asked of `offsetParent`, not of `hidden`.</b> This console has shipped a control that
    /// existed and could not be seen four times, the fourth being an earlier attempt at this
    /// very change: hiding the class container to show the editor also hid a simple renderer's
    /// one colour row, which lives in the same container. That is why the simple case is
    /// asserted here beside the classified one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_symbol_panel_names_a_class_that_is_on_the_screen()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenSymbologyAsync(layer, token);

        // <b>Waited on the model, not on the element.</b> `#symKind` is in the static markup, so
        // it exists before the layer's document has been fetched — and the change handler returns
        // early while the form is still filling, so a click here does nothing and the failure
        // arrives several assertions later wearing somebody else's name.
        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            "The symbology form never filled.");

        // <b>A simple renderer shows both, because its list is one colour and there is nowhere
        // to go back to.</b>
        await PickRendererAsync("simple");

        await WaitForAsync(
            "document.querySelector('#symClasses .symfill')?.offsetParent != null"
            + " && document.getElementById('symStack')?.offsetParent != null",
            "A simple renderer must show its colour and its symbol at once. Something here is in "
            + "the document and not on the screen.");

        // <b>A simple renderer has one symbol and no classes, so the panel names neither.</b>
        Assert.Equal(
            "Symbol",
            (await Browser.EvaluateAsync<string>(
                "document.getElementById('symDetailWhich').textContent") ?? "").Trim());

        // <b>Classified: the list and the stack, both on the screen.</b>
        await PickRendererAsync("uniqueValue");

        await WaitForAsync(
            "document.getElementById('symAddClass')?.offsetParent != null",
            "A classified renderer offers no way to add a class.");

        await ClickAsync("#symAddClass");
        await ClickAsync("#symAddClass");

        await WaitForAsync(
            "document.querySelectorAll('#symClasses .symclass').length === 3"
            + " && document.getElementById('symClasses')?.offsetParent != null"
            + " && document.getElementById('symStack')?.offsetParent != null",
            "The class list and the symbol that belongs to the chosen class must be readable "
            + "together: the panel is what says what a class is made of, and reaching it must "
            + "not cost the list.");

        // <b>The subject of the panel is a row that is on the screen — the whole point.</b>
        // Choosing the last of three and asking the panel who it is talking about is the same
        // question the owner asked of *Symbol layers — Ankara*, and this is the answer that
        // matters: whatever the panel names, the reader can see the row it names.
        await ClickAsync("#symClasses .symclass:last-child .symlabel");

        await WaitForAsync(
            """
            (() => {
              const which = document.getElementById('symDetailWhich').textContent || '';
              const row = document.querySelector('#symClasses .symclass.symchosen');
              if (!row || !which.startsWith('Symbol')) return false;
              const list = document.getElementById('symClasses');
              const a = row.getBoundingClientRect();
              const b = list.getBoundingClientRect();
              return row.offsetParent !== null && a.bottom > b.top && a.top < b.bottom;
            })()
            """,
            "The symbol panel names a class whose row is not visible in the list, which is the "
            + "fault this screen was reported for.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The picture's caption follows the document, and never claims a state that has passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner's screenshot held five sentences about saving and two of them were wrong.</b>
    /// A design review reproduced it: after storing 256 classes, the preview caption still read
    /// *Not stored yet — this is what Store would keep* and the classify line still read *Nothing
    /// is stored yet — press Store to keep them*, while the state line and the toast both
    /// correctly said it was stored. Each sentence was written once by whatever function produced
    /// it and never revisited by the one that made it false.
    /// </para>
    /// <para>
    /// <b>The caption answers a different question now.</b> Whether a document is stored is the
    /// state line's job; the caption says which of the two appearances the picture is of, which
    /// has three states — generated, stored, and edited-since — and is derived in one place
    /// rather than assigned in three.
    /// </para>
    /// <para>
    /// <b>Asked of the derivation, not of the painted caption.</b> `ConsoleTest.Plant` traps
    /// every non-`GET`, and the preview is a `POST` — so in this suite the caption always ends
    /// up carrying the preview's own failure message, whatever it said a moment earlier. What is
    /// provable here is the half that was broken: that the sentence is *derived* from the two
    /// facts rather than written by hand in three places, that an edit moves it, and that the
    /// wording which could go stale is gone from the file. The painted caption is the preview
    /// tests' subject, and storing is `Graticula.Conformance.Tests`'.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Editing_moves_the_caption_off_the_stored_appearance()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenSymbologyAsync(layer, token);

        await WaitForAsync(
            "document.querySelector('#symStack .symalpha') !== null",
            "The symbology form never filled.");

        string atRest = await Browser.EvaluateAsync<string>("symPreviewSays()") ?? "";

        Assert.DoesNotContain("Edited", atRest, StringComparison.Ordinal);

        await Browser.EvaluateAsync<bool>(
            "(() => { const a = document.querySelector('#symStack .symalpha');"
            + " a.value = '40';"
            + " a.dispatchEvent(new Event('change', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "symPreviewSays().startsWith('Edited')",
            "The caption still describes the stored appearance after an edit, so the picture "
            + "beside the form would claim to be something it is not.");

        // <b>And it says what Store would do, because that is the question an edited picture
        // raises.</b>
        string edited = await Browser.EvaluateAsync<string>("symPreviewSays()") ?? "";

        Assert.Contains("Store would keep", edited, StringComparison.Ordinal);

        // <b>The wording that could go stale is gone, not merely corrected.</b> *Not stored yet*
        // was written on every preview render and revisited by nothing; a page that still
        // contains the sentence can still print it.
        Assert.False(
            await Browser.EvaluateAsync<bool>(
                "document.body.innerHTML.includes('Not stored yet')"),
            "The sentence that went stale is still in the page.");

        // <b>The classify line no longer answers the storage question at all.</b> It kept its own
        // copy of that fact and never cleared it; the state line is the single place now.
        string says = await Browser.EvaluateAsync<string>(
            "document.getElementById('symClassifySays')?.textContent || ''") ?? "";

        Assert.DoesNotContain("stored", says, StringComparison.OrdinalIgnoreCase);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The opacity box shows what is stored, not a rounding of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stored opacity is very often fractional, and nobody chose it.</b> An ArcGIS document
    /// carries alpha as one byte of 255, so a symbol meaning 45 % arrives as 115 and
    /// <c>Cim.Percent</c> converts it to 45.1. Every generated fixture in this repository has
    /// one: <c>ci_parcels</c> holds 45.1 and <c>ci_editable</c> 90.2. This is the first-run
    /// state, not an edge case.
    /// </para>
    /// <para>
    /// <b>The box was written `step="1"`</b>, which put 45.1 in front of a native spinner that
    /// snapped it to 46 on the first press — a value nobody typed, overwriting one nobody could
    /// see was different. The alternative that was rejected is rounding the display and keeping
    /// the stored value, which leaves what is on the screen and what is in the column as two
    /// different numbers, only one of which anybody edits.
    /// </para>
    /// <para>
    /// <b>The document is planted rather than found, and the first version was not.</b> It read
    /// whatever layer <c>AnyLayerAsync</c> returned and compared the box against that document,
    /// which is a true property and an empty test: the layer it picked has a whole-number alpha,
    /// so putting <c>Math.round</c> back into the display path <b>did not fail it</b>. A
    /// falsification that passes is a fault in the test, so this one supplies the fraction
    /// itself and no longer depends on which fixture the fixture list happens to start with.
    /// </para>
    /// <para>
    /// <b>Typed into the document box, which is a real path a person uses.</b> The editor adopts
    /// what is typed there after a pause and refills the form from it, so this is the same route
    /// a pasted ArcGIS symbol takes into the controls.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_opacity_box_shows_the_stored_number_exactly()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenSymbologyAsync(layer, token);

        await WaitForAsync(
            "document.querySelector('#symStack .symalpha') !== null",
            "The symbol's layers never carried an opacity box.");

        // <b>45.1 is not an invented number.</b> It is what an ArcGIS symbol meaning 45 % becomes
        // when its alpha travels as one byte of 255: 115, and 115 / 255 is 45.098.
        await Browser.EvaluateAsync<bool>(
            "(() => { const box = document.getElementById('symDoc');"
            + " box.value = JSON.stringify({ type: 'CIMSimpleRenderer',"
            + "   symbol: { type: 'CIMSymbolReference', symbol: {"
            + "     type: 'CIMPolygonSymbol', symbolLayers: [{"
            + "       type: 'CIMSolidFill', enable: true,"
            + "       color: { type: 'CIMRGBColor', values: [220, 50, 40, 45.1] } }] } } });"
            + " box.dispatchEvent(new Event('input', { bubbles: true }));"
            + " return true; })()");

        await WaitForAsync(
            "(() => { const b = document.querySelector('#symStack .symalpha');"
            + " return b !== null && b.value === '45.1'; })()",
            "The box does not show 45.1, so a stored opacity is being rounded on its way to the "
            + "screen — which puts a number nobody typed in front of a control that will write "
            + "it back.");

        // <b>And the box can express what it is showing.</b> A whole-number step on a fractional
        // value is the fault itself: the number is right until the first press of an arrow.
        string step = await Browser.EvaluateAsync<string>(
            "document.querySelector('#symStack .symalpha').step") ?? "";

        Assert.NotEqual("1", step);

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A property can be made to slide with a number, and it reaches the document.
    /// </summary>
    /// <remarks>
    /// <b>ADR-052 §3.6, the second axis.</b> The renderer has drawn a continuous colour since
    /// ADR-041 — `SymbologyPlan` compiles `interpolate` and evaluates it per feature — and
    /// nothing could ask for one, because no vocabulary the server accepted could say it. This
    /// is the asking.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task A_colour_can_be_made_to_slide_with_a_field()
    {
        (string token, _) = await SignInAsync();
        string layer = await ALayerOfAsync(token, "Polygon");

        await OpenSymbologyAsync(layer, token);

        await WaitForAsync(
            "document.getElementById('symVaryWhat') !== null",
            "The Symbology page offers no way to vary a property with a number, so half of what "
            + "ArcGIS calls a style cannot be authored.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('symVaryWhat').offsetParent !== null"),
            "The control is in the document and not on screen.");

        // <b>Hidden until something is chosen.</b> Two stop rows above an empty select is a
        // form asking a question nobody has been asked yet.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('symVaryRows').hidden"),
            "The stops are shown before anything is being varied.");

        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.getElementById('symVaryWhat');"
            + " s.value = 'colour';"
            + " s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

        await WaitForAsync(
            "!document.getElementById('symVaryRows').hidden",
            "Choosing to vary the colour did not show the stops.");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '')"
            + ".includes('CIMColorVisualVariable')",
            "The variable never reached the document, so Store would keep a symbol that does "
            + "not vary.");

        // <b>The two ends are editable and both land.</b> A form that stored its defaults and
        // ignored the boxes would pass every assertion above.
        await Browser.EvaluateAsync<bool>(
            "(() => { const to = document.getElementById('symVaryToColour');"
            + " to.value = '#123456';"
            + " to.dispatchEvent(new Event('change', { bubbles: true }));"
            + " const at = document.getElementById('symVaryTo');"
            + " at.value = '4321';"
            + " at.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '').includes('4321')",
            "The upper stop never reached the document.");

        await WaitForAsync(
            "(document.getElementById('symDoc')?.value || '').includes('86')",
            "The upper colour never reached the document.");

        // <b>Colour boxes for a colour, numbers for a size.</b> Showing both is how a form
        // gets a value nobody meant.
        //
        // <b>Asked of the layout, not of the attribute.</b> This read `symVaryToNumber.hidden`
        // and broke on 2026-09-04 when the box and its unit were wrapped in one element so that
        // a narrow row could not break between them — the wrapper carries `hidden` now and the
        // box does not, so the assertion failed while the screen was correct. An attribute is
        // one of several ways a thing becomes invisible; `offsetParent` is whether it *is*.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('symVaryToNumber').offsetParent === null"),
            "A colour variable offers a number box beside its colour.");

        // <b>And the unit went with it.</b> Two elements hidden by two rules is two chances to
        // hide one of them; the `pt` used to be a sibling of the box and could be left behind.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.getElementById('symVaryToUnit').offsetParent === null"),
            "The number box is hidden and its unit is still on the row, so a colour variable "
            + "shows a stray `pt` with nothing to measure.");

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

        await OpenSymbologyAsync(layer, token);

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

        await OpenSymbologyAsync(points, token);

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

        await OpenSymbologyAsync(points, token);

        await WaitForAsync(
            "document.querySelector('#symStack .symsize') !== null",
            $"'{points}' draws markers and its symbol offers no size, so the one dimension a "
            + "point symbol has cannot be set.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "document.querySelector('#symStack .symsize').offsetParent !== null"),
            "The marker's size is in the document and not on screen.");

        string lines = await ALayerOfAsync(token, "LineString");

        await OpenSymbologyAsync(lines, token);

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
