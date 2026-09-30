using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The service-wide style override is on the service's screen and addressed by its name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found on 2026-09-03 by being asked where the symbol editors are.</b> There were two: the
/// layer's Symbology page, and a second box headed *Style* on the same layer's **Caching** page.
/// The second one posts to <c>/admin/services/{name}/style</c> — a service endpoint — and was
/// given the **layer's** name. Measured across the fixture: `ci_buildings` answered 200 because
/// a single-layer hosted service shares its layer's name, and `ci_EarlyAlert_routes`,
/// `ci_EarlyAlert_sites` answered 404. It had worked by coincidence wherever anybody had tried
/// it.
/// </para>
/// <para>
/// <b>It is a service control and it now lives on the service.</b> ADR-033 §5d keeps the
/// per-service style as an override because ordering and filtering *across* layers is
/// cartography a per-layer document cannot express. That makes its scope the service, and
/// putting it inside one layer's settings said the opposite as well as sending the wrong name.
/// </para>
/// <para>
/// <b>Both halves are asserted, because either alone would pass a broken screen.</b> A test that
/// only checked the layer page would pass if the control had been deleted; one that only checked
/// the service page would pass while the layer page still carried a second, broken copy.
/// </para>
/// </remarks>
public sealed class ServiceStyleOverrideTests : ConsoleTest
{
    /// <summary>
    /// Wherever the service-wide override sits, it is addressed with the service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asked *is it absent from a layer's pages* and now asks *is it addressed
    /// right*.</b> The defect was never the location: it was that a control on a layer's Caching
    /// page sent the **layer's** name to a service endpoint, so it answered 200 for a one-layer
    /// hosted service — where the two names happen to match — and 404 for every layer in a
    /// service with more than one. Measured across the fixture at the time: `ci_buildings` 200,
    /// `ci_EarlyAlert_routes` 404.
    /// </para>
    /// <para>
    /// <b>Handoff revision 2026-09-04 put it back on a layer's page deliberately</b>, at the foot
    /// of the symbology editor's rail, because it is the exception to everything above it. So the
    /// absence test would now fail for the design being right, and the address test is the one
    /// that was load-bearing all along. It is asked of a **multi-layer** service, which is the
    /// only place the two names differ and therefore the only place the question has an answer.
    /// </para>
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task The_service_wide_override_is_addressed_with_the_service_not_the_layer()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        string bare = service.Contains('/', StringComparison.Ordinal)
            ? service[(service.LastIndexOf('/') + 1)..]
            : service;

        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(service)}?tab=symbology", token);

        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            "The symbology editor never filled.");

        // <b>The layer this opened on, so the assertion can say the two are different.</b> A
        // control addressed with the layer would be indistinguishable from a correct one if they
        // were the same word.
        string open = await Browser.EvaluateAsync<string>(
            "(typeof editing !== 'undefined' && editing && editing.name) || ''") ?? string.Empty;

        Assert.NotEqual(bare, open);

        string found = await Browser.EvaluateAsync<string>(
            "document.querySelector('[data-style]')?.getAttribute('data-style') || ''")
            ?? string.Empty;

        Assert.True(
            found == bare,
            $"The service-wide style control on '{open}' is addressed with '{found}' rather than "
            + $"with its service '{bare}'. Addressed with a layer it answers 404 for every layer "
            + "whose name differs from its service's, which is every layer in a service with "
            + "more than one — and it looks correct on a one-layer service, where the two words "
            + "are the same.");

        // <b>And the address answers</b>, because a name can be well formed and wrong.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(async () => {"
                + " const node = document.querySelector('[data-style]');"
                + " const name = node.getAttribute('data-style');"
                + " const folder = node.getAttribute('data-folder') || '';"
                + " const t = sessionStorage.getItem('gis-token');"
                + " const r = await fetch('/admin/services/' + encodeURIComponent(name) + '/style'"
                + "   + '?folder=' + encodeURIComponent(folder),"
                + "   { headers: t ? { Authorization: 'Bearer ' + t } : {} });"
                + " return r.ok; })()"),
            $"The override is addressed with a name the server does not know, so Fetch, Store "
            + "and Back all answer 404.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task The_service_page_carries_the_override_and_names_the_service()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        // <b>The foot of the symbology editor's own rail, and it has moved twice.</b> It was on
        // a layer's Caching page (addressed with the layer's name, which is the defect this file
        // exists for), then the service's Visualization tab, then that service's Symbology tab —
        // and the handoff's revision on 2026-09-04 removed the tab altogether. It sits under
        // everything about one layer's appearance because it is the exception to all of it:
        // unless this document is stored, none of those layers is what the tile face draws.
        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(service)}?tab=symbology", token);

        // <b>`?tab=symbology` is a link people already have</b>; since ADR-102 it lands on Visualization with the
        // Style panel open, which is where the editor is.
        await WaitForAsync(
            "location.hash.includes('panel=style')",
            "An address asking for the Symbology tab did not reach the editor, so every link "
            + "anybody already had is now a page that draws nothing.");

        // <b>Waited on the form filling, not on the element existing.</b> `#serviceStyle` is
        // static markup in the editor's rail, so it is in the document from the first byte and a
        // wait on it passes before the layer has been read at all. Measured twice on two
        // different screens: the container is never the readiness signal.
        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            "The symbology editor never filled, so the style override could not be looked for.");

        await WaitForAsync(
            "document.querySelector('#serviceStyle [data-style]') !== null",
            "The symbology editor's rail offers no style override, so the one cartographic "
            + "thing a per-layer document cannot express has nowhere to be written.");

        // <b>On screen, not merely in the document.</b> This console has shipped a control
        // that existed and rendered nowhere three separate times, and moving one between
        // screens is exactly the change that does it: the new home may be inside a section
        // whose tab is never shown.
        // <b>The textarea is behind *Write one…*, so it is opened before it is measured.</b> A
        // control inside a closed disclosure has no `offsetParent` and is not a control that
        // failed to render; asserting on it while closed would be a test that fails for being
        // right about the design.
        await ClickAsync("#symOverrideHead");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => {"
                + " const box = document.querySelector('#serviceStyle [data-style]');"
                + " const area = document.getElementById('styleDoc');"
                + " return !!box && box.offsetParent !== null"
                + " && !!area && area.offsetParent !== null; })()"),
            "The style override is in the document and not on screen, which passes every "
            + "assertion that asks whether an element exists.");

        // <b>The service's bare name, which is what the endpoint takes.</b> The whole defect was
        // an identifier of the wrong kind, so the identifier is what is asserted.
        string bare = service.Contains('/', StringComparison.Ordinal)
            ? service[(service.LastIndexOf('/') + 1)..]
            : service;

        await WaitForAsync(
            "(document.querySelector('#serviceStyle [data-style]')"
            + ".getAttribute('data-style') || '') === "
            + JsonSerializer.Serialize(bare),
            $"The override is not addressed with '{bare}'.");

        // <b>And the address answers.</b> Asserting on the attribute alone would pass for a name
        // that is well formed and wrong, which is exactly what the defect was.
        bool answered = await Browser.EvaluateAsync<bool>(
            "(async () => {"
            + " const node = document.querySelector('#serviceStyle [data-style]');"
            + " const name = node.getAttribute('data-style');"
            + " const folder = node.getAttribute('data-folder') || '';"
            + " const t = sessionStorage.getItem('gis-token');"
            + " const r = await fetch('/admin/services/' + encodeURIComponent(name) + '/style'"
            + "   + '?folder=' + encodeURIComponent(folder),"
            + "   { headers: t ? { Authorization: 'Bearer ' + t } : {} });"
            + " return r.ok; })()");

        Assert.True(
            answered,
            $"The style override on '{service}' is addressed with a name the server does not "
            + "know, so Fetch, Store and Back all answer 404.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Opening the override reads the service's styles and offers them by name — ADR-094.
    /// </summary>
    /// <remarks>
    /// <b>The chooser is read from the server, not drawn empty.</b> A service may carry several styles
    /// since ADR-094, and the buttons act on the one chosen, so a chooser that never filled would send every
    /// Store to a style nobody picked. Asserted on what the server's list says, and only for what it
    /// already holds: this test stores nothing.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task Opening_the_override_lists_the_services_styles()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(service)}?tab=symbology", token);

        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            "The symbology editor never filled, so the style chooser could not be looked for.");

        await ClickAsync("#symOverrideHead");

        await WaitForAsync(
            "typeof styleNamesListed !== 'undefined' && styleNamesListed !== null",
            "Opening the override did not read the service's styles.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => {"
                + " const box = document.getElementById('styleName');"
                + " const names = (styleNamesListed.styles || []).map(s => s.name);"
                + " const offered = [...box.options].map(o => o.value).filter(v => v);"
                + " return box.offsetParent !== null"
                + "   && names.length === offered.length && names.every(n => offered.includes(n))"
                + "   && [...box.options].some(o => o.value === ''); })()"),
            "The style chooser does not offer exactly the service's styles and a new one.");

        Assert.False(
            string.IsNullOrWhiteSpace(await Browser.EvaluateAsync<string>(
                "document.getElementById('styleNameNote').textContent")),
            "The chooser says nothing about which style root.json serves.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The two things this screen can store are two differently named buttons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner, 2026-09-05, looking at the override open: *what is this used? why store
    /// again*.</b> The editor's header carries <c>Store</c> for the layer's symbology and this
    /// panel carried <c>Store</c> for the service's style document — the same word, the same
    /// prominence, two different objects, both on screen at once. The question is the evidence:
    /// a reader who has to ask which one they already pressed has been given one control
    /// wearing two meanings.
    /// </para>
    /// <para>
    /// <b>The label names the object rather than the act.</b> *Store the override* is longer and
    /// says what is stored; the header's <c>Store</c> stays as it is, because it is the page's
    /// subject and the panel is the exception to it.
    /// </para>
    /// <para>
    /// <b>Asserted as a count, not as a string.</b> Pinning the new wording would pass for a
    /// second button called *Store the override* somewhere else on the page. What is wrong is
    /// two of anything answering to the same name, so that is what is measured — and only among
    /// controls that are actually on screen, because a duplicate hidden behind a closed fold is
    /// not one a reader can confuse.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Only_one_control_on_the_symbology_editor_is_called_Store()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(service)}?tab=symbology", token);

        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null",
            "The symbology editor never filled, so its buttons could not be counted.");

        // The override's own button is behind *Write one…*; opening it is what puts the second
        // candidate on screen, and a count taken with it closed would find nothing to report.
        await ClickAsync("#symOverrideHead");

        await WaitForAsync(
            "document.querySelector('#serviceStyle [data-style-put]')?.offsetParent != null",
            "The override did not open, so the button this test is about is not on screen.");

        string named = await Browser.EvaluateAsync<string>(
            "[...document.querySelectorAll('button, input[type=submit]')]"
            + ".filter(b => b.offsetParent !== null)"
            + ".map(b => (b.textContent || b.value || '').trim())"
            + ".filter(t => t.toLowerCase() === 'store').join(' | ')") ?? string.Empty;

        Assert.True(
            named.Length == 0 || !named.Contains('|', StringComparison.Ordinal),
            $"Two controls on this screen are both called Store: [{named}]. One keeps the "
            + "layer's symbology and one keeps the service's style document, and a reader "
            + "cannot tell from the label which they pressed.");

        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(document.querySelector('#serviceStyle [data-style-put]')?.textContent || '')"
                + ".trim().toLowerCase().includes('override')"),
            "The override's button does not name what it stores, so the only thing separating "
            + "it from the header's Store is where it happens to sit.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A refusal on the group drawer is said, and a success empties the name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by a design review on 2026-09-06, by forcing refusals against a live server.</b>
    /// `createService` and `createGroupLayer` called `api(...)` with no `try`, so a 409 for a
    /// duplicate name and a 400 for a nesting target that is not a group both surfaced as an
    /// uncaught exception: no toast, no message, no change to the form. The server had written
    /// a sentence for the person reading it and nothing put it on the screen. The delete
    /// handler two hundred lines below had done this correctly since the day it was written.
    /// </para>
    /// <para>
    /// <b>And the second press made a duplicate.</b> Nothing disabled the button while the
    /// request was in flight and nothing emptied the field afterwards, so two clicks on
    /// *Create group layer* produced two groups with one name — verified in that review against
    /// the live fixture, then removed again.
    /// </para>
    /// <para>
    /// <b>The service half of that finding is gone with the form it was on.</b> ADR-057 §5h took
    /// the empty-service form off this drawer on 2026-09-06 — a service is composed on Publish
    /// now. The group form beside it carried the identical defect and the identical repair,
    /// which is [D-46](../../docs/architecture-debt.md)'s whole subject, so the fact is asserted
    /// here on the half that is still on the screen.
    /// </para>
    /// <para>
    /// <b>Stubbed, because the harness answers every write with `{}` and a 200.</b> A real 409
    /// cannot be produced from here — the conformance suite has that half — so what is under
    /// test is the half this harness can see: that a refusal reaches the screen at all, and that
    /// a success leaves the form unable to repeat itself.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_refused_group_says_why_and_a_created_one_clears_its_name()
    {
        (string token, _) = await SignInAsync();

        await OpenAsync("/server/#/services", token);

        await WaitForAsync(
            Shown("#newService"),
            "The Services screen offers no Group layers action.");

        await ClickAsync("#newService");

        await WaitForAsync(
            Shown("#gName"),
            "The group drawer did not open.");

        // <b>The server's own sentence, which is what has to reach the screen.</b>
        await Browser.EvaluateAsync<bool>("""
        (() => {
          const real = window.fetch;
          window.__refuse = true;
          window.fetch = async (input, init) => {
            const method = ((init && init.method) || "GET").toUpperCase();
            const where = typeof input === "string" ? input : (input && input.url) || "";
            if (method !== "POST" || !where.includes("/groups")) {
              return real(input, init);
            }
            if (!window.__refuse) {
              return new Response(
                JSON.stringify({ name: "ZZZFine", layerId: 7, parentLayerId: -1 }),
                { status: 200, headers: { "Content-Type": "application/json" } });
            }
            return new Response(
              JSON.stringify({ error: { code: 409, message:
                "A layer called ZZZTaken already exists in that service." } }),
              { status: 409, headers: { "Content-Type": "application/json" } });
          };
          return true;
        })();
        """);

        await Browser.EvaluateAsync<bool>("""
        (document.getElementById("gService").value = "hosted/anything",
         document.getElementById("gName").value = "ZZZTaken", true)
        """);

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("grpForm").requestSubmit(), true)""");

        await WaitForAsync(
            "(document.getElementById('toast')?.textContent || '').includes('already exists')",
            "A refused creation said nothing. The server answered 409 with a sentence and the "
            + "form sat unchanged, which is indistinguishable from the button doing nothing.");

        // <b>And the name survives a refusal.</b> Clearing it here would make the operator
        // retype what was rejected before they could change one letter of it.
        Assert.Equal(
            "ZZZTaken",
            await Browser.EvaluateAsync<string>("document.getElementById('gName').value"));

        // <b>The other half: a success empties it, so a second press is a second group.</b>
        await Browser.EvaluateAsync<bool>("(window.__refuse = false, true)");

        await Browser.EvaluateAsync<bool>(
            """(document.getElementById("grpForm").requestSubmit(), true)""");

        await WaitForAsync(
            "document.getElementById('gName').value === ''",
            "The name stayed in the box after the group was created, so pressing the button "
            + "again asks for the same group a second time — which is how the review ended up "
            + "with two groups of one name.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// Every service opens on Overview, and the way back goes where the reader came from.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-09-03, standing on a Settings tab they had not chosen.</b> Two faults, one
    /// screen: the tab was remembered across services, so leaving one on Settings and opening
    /// the next put somebody on a delete button; and the first breadcrumb said *Services* on
    /// both surfaces and pointed at `#/services`, which is a Server screen — so on Studio, where
    /// they had arrived from *My content*, the way back led nowhere.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task A_service_opens_on_overview_and_the_crumb_goes_back_to_my_content()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        // Leave one service on a tab that is not Overview.
        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(service)}?tab=settings", token);

        await WaitForAsync(
            "(() => { const p = document.getElementById('serviceSettings');"
            + " return !!p && !p.hidden; })()",
            "The Settings tab never opened, so this test cannot set up what it is about.");

        // <b>The same tab, opened again without asking for one.</b> Navigating within the one
        // document is how a reader moves between services, and it is where the memory lived.
        await Browser.EvaluateAsync<bool>(
            $"(() => {{ location.hash = '#/service/{Uri.EscapeDataString(service)}';"
            + " return true; })()");

        await WaitForAsync(
            "(() => { const p = document.getElementById('serviceOverview');"
            + " return !!p && !p.hidden; })()",
            "Opening a service left the tab where the last one was, so a reader who had been on "
            + "Settings arrives on a delete button.");

        // <b>And the way back is where they came from.</b>
        string crumb = await Browser.EvaluateAsync<string>(
            "document.querySelector('#serviceCrumb a')?.getAttribute('href') || ''")
            ?? string.Empty;

        Assert.Equal("#/content", crumb);

        Assert.Equal(
            "My content",
            await Browser.EvaluateAsync<string>(
                "document.querySelector('#serviceCrumb a')?.textContent.trim() || ''"));

        // <b>Followed, not just read.</b> A link with the right text and a dead target is the
        // fault this replaces.
        await ClickAsync("#serviceCrumb a");

        await WaitForAsync(
            "document.getElementById('contentRows') !== null"
            + " && document.getElementById('contentRows').offsetParent !== null",
            "The way back does not reach My content.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A service has a Symbology tab, and it says how each of its layers is drawn.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-09-03: *symbology'nin kendi sekmesi olsun*.</b> It was reachable only by
    /// opening a layer and noticing a tab there, and the question is asked of the service.
    /// Symbology is still stored per layer — the tab answers *how is this service drawn* by
    /// listing them, which is the question people actually arrive with.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task A_service_is_styled_from_Visualization_and_names_every_layers_appearance()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync($"/studio/#/service/{Uri.EscapeDataString(service)}?tab=visualization", token);

        // <b>ADR-102: styling is Visualization's, beside the map — the owner's *"ArcGIS symbology'i webmap'e
        // yıkmıştı"*.</b> The Symbology tab, which left the item, is gone; the way in is where the map is.
        await WaitForAsync(
            "(() => { const b = document.getElementById('visStyle'); return !!b && !b.hidden && b.offsetParent !== null; })()",
            "Visualization offers no Style control beside the map.");

        await ClickAsync("#visStyle");

        await WaitForAsync(
            "typeof symModel !== 'undefined' && symModel !== null && location.hash.includes('panel=style')",
            "Pressing Style opened nothing, or did not write the panel into the address.");

        await WaitForAsync(
            "document.querySelectorAll('#symLayerPick .symlayerpick').length > 1",
            "The editor's rail lists no layers, so a service of several has no way to move between them.");

        await WaitForAsync(
            "[...document.querySelectorAll('#symLayerPick .rowmeta')]"
            + ".every(r => r.textContent.trim() && r.textContent.trim() !== 'reading…')",
            "A layer's entry never said how it is drawn.");

        // Each entry picks its own layer, as the item's layer.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => { const all = [...document.querySelectorAll('#symLayerPick .symlayerpick')];"
                + " const ids = all.map(a => a.dataset.visLayer);"
                + " return all.every(a => a.offsetParent !== null) && ids.every(Boolean) && new Set(ids).size === ids.length; })()"),
            "An entry offers no visible way to its layer, or two entries pick the same one.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The Style control beside the map styles the layer the map is showing.
    /// </summary>
    /// <remarks>
    /// <b>Owner, standing on the Visualization tab: *nerede ya*.</b> *How is this drawn* is asked while looking
    /// at the map, so the way in is beside it and follows the layer chosen — or on a service with several
    /// layers it opens somebody else's and looks like it worked.
    /// </remarks>
    [Fact]
    public async Task The_map_screen_styles_the_layer_it_is_showing()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync($"/studio/#/service/{Uri.EscapeDataString(service)}?tab=visualization", token);

        await WaitForAsync(
            "!!document.querySelector('#itemLayer option') && !document.getElementById('visStyle').hidden",
            "The Visualization tab has no Layer select or no Style control.");

        string last = await Browser.EvaluateAsync<string>(
            "(() => { const s = document.getElementById('itemLayer'); const o = s.options[s.options.length - 1];"
            + " s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); return o.textContent.trim(); })()") ?? "";

        await ClickAsync("#visStyle");

        await WaitForAsync(
            $"typeof editing !== 'undefined' && editing && editing.name === {JsonSerializer.Serialize(last)}",
            $"Style opened another layer than the one the map was showing ({last}).");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// The item has one way into styling, not one per screen.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-10-01: *"neden her yerde bir symbology düğmesi var"*.</b> There were four doors to one
    /// editor — the item's tab, a button on every layer row, a link beside the map and the layer page's menu —
    /// each added when the editor was found unreachable, none removed. ADR-102 keeps one: Style, beside the map.
    /// </remarks>
    [Fact]
    public async Task The_item_has_one_way_into_styling()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync($"/studio/#/service/{Uri.EscapeDataString(service)}", token);

        await WaitForAsync(
            "document.querySelectorAll('#serviceLayerRows tr').length > 0",
            "The service page lists no layers.");

        int doors = await Browser.EvaluateAsync<int>(
            "[...document.querySelectorAll('#view-service a, #view-service button')]"
            + ".filter(e => e.offsetParent !== null && (/symbology/i.test(e.textContent) || /\\/symbology/.test(e.getAttribute('href') || ''))).length");

        Assert.True(doors == 0, $"The item page still shows {doors} Symbology door(s); styling is Visualization's Style.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A published service with more than one layer.
    /// </summary>
    /// <remarks>
    /// <b>More than one, because that is where the defect showed.</b> A single-layer hosted
    /// service shares its layer's name, so it answered correctly through the wrong identifier
    /// and would have passed this test against the broken screen.
    /// </remarks>
    /// <param name="token">The reader's token.</param>
    /// <returns>Its qualified name.</returns>
    private async Task<string> AMultiLayerServiceAsync(string token)
    {
        await OpenAsync("/studio/#/", token);

        string found = await Browser.EvaluateAsync<string>(
            "(async () => {"
            + " const t = sessionStorage.getItem('gis-token');"
            + " const h = t ? { Authorization: 'Bearer ' + t } : {};"
            + " const r = await fetch('/admin/layers', { headers: h });"
            + " if (!r.ok) return '';"
            + " const d = await r.json();"
            + " const all = Array.isArray(d) ? d : (d.layers || []);"
            + " const count = {};"
            + " for (const l of all) {"
            + "   if (l.service) { count[l.service] = (count[l.service] || 0) + 1; }"
            + " }"
            + " for (const [name, n] of Object.entries(count)) { if (n > 1) return name; }"
            + " return ''; })()") ?? string.Empty;

        Assert.False(
            string.IsNullOrEmpty(found),
            "No published service has more than one layer, so this test cannot drive the case "
            + "the defect lived in. It FAILS rather than skips.");

        return found;
    }
}
