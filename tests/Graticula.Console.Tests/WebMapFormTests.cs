using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A map layer's form — ADR-313: the map viewer's Forms tool writes the layer's <c>formInfo</c>, and the editing card
/// draws the form and evaluates its expressions as values change.
/// </summary>
/// <remarks>
/// The harness answers every write with an empty document and never sends it, so a save is asserted by what the page
/// tried to send. The form's logic itself is tested headless in <c>tests/studio-js</c>; this is about the page.
/// </remarks>
public sealed class WebMapFormTests : ConsoleTest
{
    private const string Loaded =
        "!!wmState.doc && wmLayers().length > 0 && (wmRuntime.get(wmLayers()[0]) || {}).status === 'ok'";

    /// <summary>Sets a control's value as a person typing would, so the page's input listeners run.</summary>
    private static string Type(string selector, string value) =>
        $"(() => {{ const e = document.querySelector('{selector}'); e.focus(); e.value = {System.Text.Json.JsonSerializer.Serialize(value)};"
        + " e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()";

    [Fact]
    public async Task The_forms_tool_drafts_a_form_tests_an_expression_and_applies_it_to_the_layer()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync("/studio/webmap.html?service=hosted%2Fci_editable", token, cookie);
        await WaitForAsync(Loaded, "The editable layer did not load.");

        await OpenLayerToolAsync("form");
        await WaitForAsync("!!document.querySelector('[data-wmf-act=create]')", "The Forms tool did not offer to create a form.");
        await ClickAsync("[data-wmf-act=create]");
        await WaitForAsync("document.querySelectorAll('.wmf-pick').length >= 2", "Create did not draft a form of the layer's fields.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement.classList.contains('wmf-pick')"),
            "After Create the focus is not on the form's first element.");

        // The label: read-only, and calculated from the reading.
        await Browser.EvaluateAsync<bool>(
            "(() => { const b = [...document.querySelectorAll('.wmf-pick')].find(b => b.textContent.includes('label'));"
            + " if (b.getAttribute('aria-pressed') !== 'true') b.click(); return true; })()");
        await WaitForAsync("!!document.querySelector('[data-wmf-mode=editableExpression]')", "The label's settings did not open.");
        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.querySelector('[data-wmf-mode=editableExpression]'); s.value = 'false';"
            + " s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await WaitForAsync("document.activeElement === document.querySelector('[data-wmf-mode=editableExpression]')",
            "Changing a mode redrew the panel and lost the focus.");
        await Browser.EvaluateAsync<bool>(Type("[data-wmf-expr=valueExpression]", "IIf($feature.reading > 10, 'big', 'small')"));
        await Browser.EvaluateAsync<bool>(Type("[data-wmf-sample]", "{\"reading\": 20}"));
        await ClickAsync("[data-wmf-act=test][data-wmf-prop=valueExpression]");
        await WaitForAsync(
            "document.querySelector('.wmf-expr:has([data-wmf-expr=valueExpression]) .wmf-answer').textContent.includes('Calculates \"big\"')",
            "Testing the calculated value against the sample did not answer it.");

        // A function the engine does not have is named in the answer.
        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.querySelector('[data-wmf-mode=visibilityExpression]'); s.value = 'expression';"
            + " s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await Browser.EvaluateAsync<bool>(Type("textarea[data-wmf-expr=visibilityExpression]", "Area($feature) > 1"));
        await ClickAsync("[data-wmf-act=test][data-wmf-prop=visibilityExpression]");
        await WaitForAsync(
            "document.querySelector('.wmf-expr:has(textarea[data-wmf-expr=visibilityExpression]) .wmf-answer').textContent.includes('Area is not built')",
            "An expression calling a function Studio does not have did not say which.");
        await Browser.EvaluateAsync<bool>(Type("textarea[data-wmf-expr=visibilityExpression]", "$feature.reading > 0"));

        // A redraw from elsewhere keeps the focus where it was.
        await Browser.EvaluateAsync<bool>("(document.querySelector('[data-wmf-expr=valueExpression]').focus(), wmDrawLayerList(), true)");
        Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement === document.querySelector('[data-wmf-expr=valueExpression]')"),
            "A redraw of the layer list took the focus out of the expression being written.");
        // What the designer says is announced from a region outside the markup a redraw replaces (UX review, finding 7).
        Assert.True(await Browser.EvaluateAsync<bool>(
            "(() => { const r = document.getElementById('layerToolSays'); const tool = document.getElementById('layerTool');"
            + " return r.getAttribute('aria-live') === 'polite' && !tool.contains(r) && !tool.querySelector('[aria-live], [role=status]'); })()"),
            "The designer's status is not a live region outside #layerTool, or a live region is drawn inside it.");

        // The preview is the card's markup for the sample, and follows what is typed (UX review, finding 2).
        await Browser.EvaluateAsync<bool>(Type("[data-wmf-el=label]", "Station"));
        await WaitForAsync("document.querySelector('.wmf-preview .wmf-pvform') && document.querySelector('.wmf-preview').textContent.includes('Station')"
            + " && !document.querySelector('.wmf-preview [data-field]') && document.activeElement === document.querySelector('[data-wmf-el=label]')",
            "The preview did not follow the label typed, drew controls the card reads, or took the focus.");

        await ClickAsync("[data-wmf-act=apply]");
        await WaitForAsync("document.querySelector('.wmf-said').textContent.includes('is set in this map')"
            + " && document.getElementById('layerToolSays').textContent.includes('is set in this map')", "Apply did not say the form was set.");
        Assert.True(await Browser.EvaluateAsync<bool>(
            "(() => { const f = wmLayers()[0].formInfo; const label = f.formElements.find(e => e.fieldName === 'label');"
            + " const info = n => f.expressionInfos.find(e => e.name === n);"
            + " return label.editableExpression === 'expr/system/false' && info(label.valueExpression).expression.startsWith('IIf(')"
            + " && info(label.visibilityExpression).returnType === 'boolean' && wmState.dirty; })()"),
            "The layer's formInfo is not what the tool was told, or the map is not marked unsaved.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task The_card_draws_the_layers_form_and_follows_its_expressions_as_values_change()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync("/studio/webmap.html?service=hosted%2Fci_editable", token, cookie);
        await WaitForAsync(Loaded, "The editable layer did not load.");

        // The form a saved map would hold: the reading required, a group shown above 5 holding the label and a text.
        await Browser.EvaluateAsync<bool>("""
            (() => {
              wmLayers()[0].formInfo = {
                title: "Readings",
                formElements: [
                  { type: "field", fieldName: "reading", label: "Reading", description: "Metres above the datum", inputType: { type: "text-box" }, requiredExpression: "expr/system/true" },
                  { type: "group", label: "Details", visibilityExpression: "expr/0", formElements: [
                    { type: "field", fieldName: "label", label: "Label", inputType: { type: "text-box" }, hint: "a name" },
                    { type: "text", textFormat: "markdown", text: "**Reading** is {reading}" } ] } ],
                expressionInfos: [
                  { name: "expr/system/true", expression: "true", returnType: "boolean" },
                  { name: "expr/0", expression: "$feature.reading > 5", returnType: "boolean" } ] };
              window.__bodies = [];
              const trapped = window.fetch;
              window.fetch = (input, init) => { if (init && init.body) window.__bodies.push(String(init.body)); return trapped(input, init); };
              return true;
            })()
            """);

        await Browser.EvaluateAsync<bool>(
            "(window.__asked = null, wmFetch(wmLayers()[0].url + '/query?where=1%3D1&outFields=objectid&returnGeometry=true&outSR=3857&resultRecordCount=1&f=json')"
            + ".then(p => { const g = WM_ESRI.readFeatures(p)[0].getGeometry(); wmFit(ol.extent.buffer(g.getExtent(), 50));"
            + " return wmIdentify(ol.extent.getCenter(g.getExtent())); }).then(() => window.__asked = true), true)");
        await WaitForAsync("window.__asked === true && !!document.querySelector('#identify [data-edit]')", "The card offers no Edit.");
        await ClickAsync("#identify [data-edit]");
        await WaitForAsync("!!document.querySelector('#identify form.wmf-form')", "Edit did not draw the layer's form.");

        Assert.Equal("a name", await Browser.EvaluateAsync<string>("document.getElementById('edit-label').placeholder"));
        Assert.Equal("true", await Browser.EvaluateAsync<string>("document.getElementById('edit-reading').getAttribute('aria-required')"));

        // A reading above 5 shows the group; below, it hides it and clears its field, and the live region says so.
        await Browser.EvaluateAsync<bool>(Type("#edit-reading", "9"));
        await WaitForAsync("!document.querySelector('details.wmf-group').hidden && document.querySelector('.wmf-text').textContent === 'Reading is 9'",
            "A reading of 9 did not show the group, or its text did not follow the reading.");
        await Browser.EvaluateAsync<bool>(Type("#edit-label", "to be cleared"));
        await Browser.EvaluateAsync<bool>(Type("#edit-reading", "1"));
        await WaitForAsync(
            "document.querySelector('details.wmf-group').hidden && document.getElementById('edit-label').value === ''"
            + " && document.getElementById('wmfLive').textContent === '1 question was hidden.'",
            "A reading of 1 did not hide the group, clear its field and say so.");
        Assert.True(await Browser.EvaluateAsync<bool>("document.activeElement === document.getElementById('edit-reading')"),
            "Evaluating the form took the focus out of the field being typed in.");

        // An empty required field is refused, named under itself, and nothing is sent.
        await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
        await Browser.EvaluateAsync<bool>(Type("#edit-reading", ""));
        await ClickAsync("#identify form.wmf-form button[type=submit]");
        await WaitForAsync("document.getElementById('edit-reading-says').textContent === 'Reading is required.'"
            + " && document.activeElement === document.getElementById('edit-reading')", "An empty required field was not refused on itself.");
        Assert.Empty(await WritesAsync());
        Assert.Equal("edit-reading-desc edit-reading-says", await Browser.EvaluateAsync<string>("document.getElementById('edit-reading').getAttribute('aria-describedby')"));

        // The error goes as soon as the field holds a value, not at the next save (UX review, finding 5).
        await Browser.EvaluateAsync<bool>(Type("#edit-reading", "3"));
        await WaitForAsync("!document.getElementById('edit-reading').hasAttribute('aria-invalid') && document.getElementById('edit-reading-says').textContent === ''",
            "A required field that was filled in still says it is required.");

        // A valid form sends the reading and the cleared label through applyEdits.
        await ClickAsync("#identify form.wmf-form button[type=submit]");
        await WaitForAsync("window.__writes.some(w => w.includes('/FeatureServer/0/applyEdits'))", "Saving the form sent nothing to applyEdits.");
        string sent = (await Browser.EvaluateAsync<string[]>("window.__bodies") ?? []).Last();
        string updates = System.Uri.UnescapeDataString(sent.Replace('+', ' '));
        Assert.Contains("\"reading\":3", updates);
        Assert.Contains("\"label\":null", updates);

        NothingWentWrong(await PageErrorsAsync());
    }
}
