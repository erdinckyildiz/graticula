using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// What the WMS capabilities document says about itself beyond the required minimum.
/// </summary>
/// <remarks>
/// <para>
/// <b>WMS 1.3.0 recommends five things this document did not carry, and three of them
/// were free.</b> The CITE suite's recommended profile checks for service keywords,
/// contact information, a layer abstract, a layer keyword list and a MetadataURL. Run on
/// 2026-08-23 it reported 191 of 202; with the three that need no information this server
/// does not already hold, 194.
/// </para>
/// <para>
/// <b>The value is not the assertion, it is a layer picker with a search box.</b> A title
/// of <c>tr_il</c> tells a person nothing and *polygon features in EPSG:4326, with a time
/// dimension* tells them whether it is the layer they want. What this test guards is that
/// every word of it stays derived from the layer — a keyword list somebody typed once is a
/// list describing what the layer used to hold.
/// </para>
/// </remarks>
public sealed class WmsCapabilitiesRecommendationTests : ArcGisClient
{
    private static readonly XNamespace Wms = "http://www.opengis.net/wms";

    private async Task<XElement> CapabilitiesAsync()
    {
        // <b>Fetched raw rather than through GetHtmlAsync</b>, which asserts text/html —
        // a WMS capabilities document is text/xml, and the first version of this class
        // failed four tests on the media type of its own helper.
        string root = await RequireServerAsync();

        using System.Net.Http.HttpRequestMessage request = new(
            System.Net.Http.HttpMethod.Get,
            root + "/wms?service=WMS&version=1.3.0&request=GetCapabilities");

        await AuthenticateAsync(request, root);

        using System.Net.Http.HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return XElement.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The service says what it is, in words that distinguish it.
    /// </summary>
    /// <remarks>
    /// <b>Asserted as *these words*, not as *some words*.</b> The recommendation invites
    /// "GIS, maps, spatial" — true of every server of this kind and therefore useless for
    /// telling one from another. These name operations a client can call.
    /// </remarks>
    [Fact]
    public async Task The_service_publishes_keywords_that_say_what_it_answers()
    {
        XElement capabilities = await CapabilitiesAsync();

        XElement service = Assert.Single(capabilities.Elements(Wms + "Service"));

        List<string> keywords =
        [
            .. service.Elements(Wms + "KeywordList")
                .Elements(Wms + "Keyword")
                .Select(k => k.Value),
        ];

        Assert.Contains("GetMap", keywords);
        Assert.Contains("GetFeatureInfo", keywords);
    }

    /// <summary>
    /// Every named layer describes itself and can be searched for.
    /// </summary>
    /// <remarks>
    /// <b>Every layer, not most.</b> A recommendation met on some layers is a document a
    /// client cannot rely on, and the ones that would be missed are the ones added after
    /// somebody stopped looking — which is why this walks the document rather than
    /// sampling it.
    /// </remarks>
    [Fact]
    public async Task Every_named_layer_carries_an_abstract_and_keywords()
    {
        XElement capabilities = await CapabilitiesAsync();

        List<string> without = [];
        int named = 0;

        foreach (XElement layer in capabilities.Descendants(Wms + "Layer"))
        {
            if (layer.Element(Wms + "Name") is not { } name)
            {
                // An unnamed Layer is a grouping element; the recommendation is about the
                // ones a client can request.
                continue;
            }

            named++;

            if (layer.Element(Wms + "Abstract") is not { Value.Length: > 20 })
            {
                without.Add($"{name.Value}: no abstract");
            }

            if (!layer.Elements(Wms + "KeywordList").Elements(Wms + "Keyword").Any())
            {
                without.Add($"{name.Value}: no keywords");
            }
        }

        Assert.True(named > 0, "the capabilities document named no layers at all");

        Assert.True(
            without.Count == 0,
            "Every named layer needs an abstract and a keyword list, because a title alone "
            + "does not tell a person whether it is the layer they want:\n  "
            + string.Join("\n  ", without));
    }

    /// <summary>
    /// A layer's abstract is read off the layer rather than repeating its title.
    /// </summary>
    /// <remarks>
    /// <b>This is the assertion that stops the abstract becoming filler.</b> A document
    /// where every abstract is the same sentence passes the test above and helps nobody;
    /// what makes these useful is that each one names the geometry and the reference
    /// system that layer actually holds, so it changes when the layer does.
    /// </remarks>
    [Fact]
    public async Task A_layers_abstract_names_what_that_layer_holds()
    {
        XElement capabilities = await CapabilitiesAsync();

        int checked_ = 0;

        foreach (XElement layer in capabilities.Descendants(Wms + "Layer"))
        {
            if (layer.Element(Wms + "Name") is null
                || layer.Element(Wms + "Abstract") is not { } summary
                || layer.Element(Wms + "CRS") is not { } crs)
            {
                continue;
            }

            checked_++;

            Assert.Contains(crs.Value, summary.Value, StringComparison.Ordinal);
        }

        Assert.True(checked_ > 0, "no layer had both a CRS and an abstract to compare");
    }

    /// <summary>
    /// Contact information is absent until a deployment supplies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserts the absence, which is the unusual half.</b> The CITE suite
    /// recommends <c>ContactInformation</c> and the cheap way to satisfy it is to write
    /// something plausible — and a client that reads an address and finds nobody there has
    /// been actively misled, while one that finds no address knows to look elsewhere. So
    /// this server publishes nothing until an operator sets
    /// <c>Graticula:WmsContactPerson</c> and its siblings.
    /// </para>
    /// <para>
    /// <b>The other half was measured rather than asserted here</b>, because it needs a
    /// differently configured process: with those settings set, the same CITE run went
    /// from 194 of 202 to 195. That is recorded in
    /// [ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) rather than tested, since a
    /// test that restarts the server to prove a setting works is testing the settings
    /// reader, which has its own tests.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Contact_information_is_omitted_rather_than_invented()
    {
        XElement capabilities = await CapabilitiesAsync();

        XElement service = Assert.Single(capabilities.Elements(Wms + "Service"));

        XElement? contact = service.Element(Wms + "ContactInformation");

        if (contact is null)
        {
            return;
        }

        // If a deployment has supplied it, it must be a deployment's own words rather
        // than a placeholder that shipped with the product.
        string text = contact.Value;

        foreach (string placeholder in
            (string[])["example.com", "example.invalid", "your-org", "TODO", "changeme"])
        {
            Assert.DoesNotContain(placeholder, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
