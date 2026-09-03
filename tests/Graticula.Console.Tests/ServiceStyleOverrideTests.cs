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
    [Fact]
    public async Task A_layers_own_pages_offer_no_service_wide_control()
    {
        (string token, _) = await SignInAsync();
        string layer = await AnyLayerAsync();

        await OpenAsync($"/studio/#/layer/{Uri.EscapeDataString(layer)}/caching", token);

        // <b>Waited on the pages, not on the container.</b> `#editPages` is in `index.html`
        // and exists before anything is drawn into it, so waiting on the element passes against
        // an empty editor and every assertion below then asks nothing. Measured: this test went
        // green with the defect deliberately put back.
        await WaitForAsync(
            "document.querySelectorAll('#editPages .page').length > 0",
            "The layer editor never drew its pages.");

        string found = await Browser.EvaluateAsync<string>(
            "(() => {"
            + " const box = document.querySelector('#editPages [data-style]');"
            + " if (!box) return '';"
            + " return box.getAttribute('data-style') || '(empty)'; })()") ?? string.Empty;

        Assert.True(
            found.Length == 0,
            "A layer's own settings still carry a service-wide style control, addressed with "
            + $"'{found}'. It answers 404 for every layer whose name differs from its service's, "
            + "which is every layer in a service with more than one.");
    }

    [Fact]
    public async Task The_service_page_carries_the_override_and_names_the_service()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync(
            $"/studio/#/service/{Uri.EscapeDataString(service)}?tab=visualization", token);

        // <b>Waited on the page opening, not on the element existing.</b> `#serviceStyle` is
        // static markup in `index.html`, so it is in the document from the first byte and a wait
        // on it passes before the service has been read at all. Measured twice today, on two
        // different screens: the container is never the readiness signal.
        await WaitForAsync(
            "(() => { const vis = document.getElementById('serviceVis');"
            + " return !!vis && !vis.hidden; })()",
            "The service's Visualization page never opened, so the style override could not be "
            + "looked for.");

        await WaitForAsync(
            "document.querySelector('#serviceStyle [data-style]') !== null",
            "The service's Visualization page offers no style override, so the one cartographic "
            + "thing a per-layer document cannot express has nowhere to be written.");

        // <b>On screen, not merely in the document.</b> This console has shipped a control
        // that existed and rendered nowhere three separate times, and moving one between
        // screens is exactly the change that does it: the new home may be inside a section
        // whose tab is never shown.
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
            + " const name = document.querySelector('#serviceStyle [data-style]')"
            + "   .getAttribute('data-style');"
            + " const t = sessionStorage.getItem('gis-token');"
            + " const r = await fetch('/admin/services/' + encodeURIComponent(name) + '/style',"
            + "   { headers: t ? { Authorization: 'Bearer ' + t } : {} });"
            + " return r.ok; })()");

        Assert.True(
            answered,
            $"The style override on '{service}' is addressed with a name the server does not "
            + "know, so Fetch, Store and Back all answer 404.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A service's layer table offers the way to each layer's symbology.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-09-03: *arayüzde yok düğmesi. Gönül gözüyle mi bakacağım.*</b> The
    /// Symbology page existed and was reachable only by opening a layer — which lands on
    /// *Maintenance* — and then finding the tab. From a service, which is what *My content*
    /// actually lists, there was no route to it at all. A screen that can only be reached by
    /// somebody who already knows it is there is a screen that does not exist.
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task A_services_layers_each_offer_the_way_to_their_symbology()
    {
        (string token, _) = await SignInAsync();
        string service = await AMultiLayerServiceAsync(token);

        await OpenAsync($"/studio/#/service/{Uri.EscapeDataString(service)}", token);

        await WaitForAsync(
            "document.querySelectorAll('a[href*=\"#/layer/\"]').length > 0",
            "The service page lists no layers, so this test cannot look for the way on.");

        await WaitForAsync(
            "document.querySelectorAll('a[href*=\"/symbology\"]').length > 0",
            "A service's layer table offers no way to any layer's symbology. The page can only "
            + "be reached by somebody who already knows it is there.");

        // <b>Visible, and one per layer.</b> A single link at the bottom of the table would
        // pass a count and answer for the wrong layer.
        int layers = await Browser.EvaluateAsync<int>(
            "document.querySelectorAll('a[href*=\"#/layer/\"]:not([href*=\"/symbology\"])').length");

        int ways = await Browser.EvaluateAsync<int>(
            "[...document.querySelectorAll('a[href*=\"/symbology\"]')]"
            + ".filter(a => a.offsetParent !== null).length");

        Assert.Equal(layers, ways);

        // <b>And it goes to that layer.</b> An easy way to have the right count and the wrong
        // targets is to build every link from the first row.
        Assert.True(
            await Browser.EvaluateAsync<bool>(
                "(() => {"
                + " const seen = new Set();"
                + " for (const a of document.querySelectorAll('a[href*=\"/symbology\"]'))"
                + "   seen.add(a.getAttribute('href'));"
                + " return seen.size === document.querySelectorAll("
                + "   'a[href*=\"/symbology\"]').length; })()"),
            "Two layers share a symbology link, so at least one of them opens somebody else's.");

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
