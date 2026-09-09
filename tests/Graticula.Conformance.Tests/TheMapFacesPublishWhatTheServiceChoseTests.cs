using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// WMS and WFS advertise the reference their service named, and answer in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner, 2026-09-09:</b> *"wms ve wfs map'in projeksiyonunda yayınlanacak."* Recorded as
/// [ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5c, and it closes the
/// half of [D-229](../../docs/architecture-debt.md) that was left open. Before it, a service could
/// name a reference — <c>PUT /admin/services/{name}/srid</c>, migration 39 — and these two
/// faces
/// did not read it: <c>wfs:DefaultCRS</c> came from the table, and WMS wrote a fixed
/// <c>EPSG:4326 · EPSG:3857 · CRS:84</c> on the root whatever anybody chose. Measured that day
/// with <c>ci_buildings</c> served in 5253 and nothing else changed: both documents were
/// **byte-identical** to the ones the same server wrote with the service unset.
/// </para>
/// <para>
/// <b>Three states, and the unset one is the one worth asserting first.</b> Null means *this
/// layer's own* and is what every service published before migration 39 still holds, so a change
/// that only moved the chosen case would be a change that broke every existing deployment
/// quietly. Set must move both documents. And a client that names a reference per request must
/// still win, because ADR-057 §5c is explicit that **published in** and **capable of** are
/// different claims and only the first is what a capabilities document is for.
/// </para>
/// <para>
/// <b>What this does not assert, deliberately.</b> It is not
/// [ADR-060](../../docs/adr/ADR-060-the-axis-order-comes-from-the-register.md) condition 4: these
/// faces still serve references no document offers, and
/// <c>AdvertisedReferencesAreTheServedOnesTests</c> is where that gap is pinned. Changing *which*
/// reference is advertised does not change that the advertised set is smaller than the served
/// one — if anything it makes the WMS gap wider, because the root's fixed three became one.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because it reconfigures a service other classes read.</b>
/// It sets a reference on the fixture named by <c>GRATICULA_TEST_QUERYABLE</c> and restores it in
/// a <c>finally</c> — [D-75](../../docs/architecture-debt.md), and the same reason
/// <c>DataSourceLifecycleConformanceTests</c> gives for the ArcGIS half of this behaviour.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class TheMapFacesPublishWhatTheServiceChoseTests : ArcGisClient
{
    private const string Wfs = "http://www.opengis.net/wfs/2.0";

    /// <summary>
    /// Every reference a WMS layer states a box in is a reference it says it supports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The invariant that decided what the per-layer <c>CRS</c> list became.</b> A layer's own
    /// <c>BoundingBox</c> is written in the table's reference —
    /// <c>EmptyLayerStillHasABoundingBoxTests</c> holds that, and it is right: replacing an
    /// extent with the world makes every document
    /// conformant and every extent useless. So the moment the advertised reference stopped being
    /// the table's, a layer could state a box in a code the same document said it did not support:
    /// numbers a client can read and may not ask in.
    /// </para>
    /// <para>
    /// <b>Inheritance counts, which is why this walks upwards.</b> WMS 1.3.0 §7.2.4.6.7 makes a
    /// layer's references its own plus its parents'; the root states one and each named layer adds
    /// what it is published in. A test that looked only at the layer's own elements would fail on
    /// the world box every empty layer inherits.
    /// </para>
    /// <para>
    /// <b>Both versions, because they spell it differently and the writer has two methods.</b>
    /// 1.3.0 says <c>CRS</c> and 1.1.1 says <c>SRS</c>, and the whole argument for two writers is
    /// that a mistake in one of them is invisible from the other.
    /// </para>
    /// </remarks>
    /// <param name="version">The version to ask for.</param>
    /// <returns>The task.</returns>
    [Theory]
    [InlineData("1.3.0")]
    [InlineData("1.1.1")]
    public async Task A_wms_layer_states_no_box_in_a_reference_it_does_not_publish(string version)
    {
        XDocument document = await CapabilitiesAsync(
            $"/wms?service=WMS&version={version}&request=GetCapabilities");

        string element = version == "1.3.0" ? "CRS" : "SRS";
        List<string> wrong = [];

        foreach (XElement layer in Named(document))
        {
            HashSet<string> supported = Supported(layer, element);

            foreach (XElement box in layer.Elements().Where(e => Is(e, "BoundingBox")))
            {
                string named = (box.Attribute(element) ?? box.Attribute("CRS")
                    ?? box.Attribute("SRS"))?.Value ?? "(none)";

                if (!supported.Contains(named))
                {
                    wrong.Add(
                        $"`{Name(layer)}` states a BoundingBox in `{named}` and publishes "
                        + string.Join(", ", supported.OrderBy(c => c, StringComparer.Ordinal))
                        + ". "
                        + "The box is numbers in a reference the same document says a client may "
                        + "not ask for.");
                }
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"layers whose boxes are outside what they publish ({wrong.Count}):\n  "
            + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The root layer states one reference, not a fixed list nobody chose.
    /// </summary>
    /// <remarks>
    /// <b>The owner, of a longer list: *"listelensin istemiyorum"*.</b> This element carried
    /// <c>EPSG:4326</c>, <c>EPSG:3857</c> and <c>CRS:84</c> for every deployment, so two of the
    /// three references every layer offered had nothing to do with what any service was published
    /// in. What survives is the one every client can be assumed to understand and the one this
    /// document already writes its geographic boxes in; the layers say the rest for themselves.
    /// <b>Not zero</b>: 1.3.0 §7.3.3.1 makes <c>CRS</c> a required <c>GetMap</c> parameter, so on
    /// this face the advertised set is the publication, and a document offering only a national
    /// grid turns away every client that cannot look one up.
    /// </remarks>
    /// <param name="version">The version to ask for.</param>
    /// <param name="expected">The one reference that version spells it with.</param>
    /// <returns>The task.</returns>
    [Theory]
    [InlineData("1.3.0", "CRS:84")]
    [InlineData("1.1.1", "EPSG:4326")]
    public async Task The_root_layer_offers_one_reference(string version, string expected)
    {
        XDocument document = await CapabilitiesAsync(
            $"/wms?service=WMS&version={version}&request=GetCapabilities");

        XElement root = document.Descendants().First(e => Is(e, "Layer"));

        Assert.Null(root.Elements().FirstOrDefault(e => Is(e, "Name")));

        string[] stated =
        [
            .. root.Elements()
                .Where(e => Is(e, "CRS") || Is(e, "SRS"))
                .Select(e => e.Value.Trim()),
        ];

        Assert.True(
            stated.Length == 1 && stated[0] == expected,
            $"The root layer of the {version} document states "
            + $"{string.Join(", ", stated)} and should state {expected} and nothing else. Every "
            + "named layer inherits this element, so anything extra here is a reference offered "
            + "for every layer on the server that no service was published in — the fixed list "
            + "ADR-057 §5c replaced.");
    }

    /// <summary>
    /// A service that names a reference is advertised and answered in it on both map faces, and
    /// a request that names another still wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The document and the default are asserted together, which is the point.</b> On WFS they
    /// are the same claim — <c>DefaultCRS</c> *is* what an omitted <c>srsName</c> means — and
    /// they
    /// were computed in two places, so they agreed by accident until a service chose something.
    /// Asserting only one of them would let them come apart again silently, which is exactly what
    /// D-229 cost a day over on the ArcGIS face.
    /// </para>
    /// <para>
    /// <b>The reference is picked against the table's rather than written down.</b> 4326 and 3857
    /// are the two codes any deployment of this server can reach, so this asks for whichever the
    /// fixture is not stored in and cannot be made to pass by a fixture that happens to match.
    /// </para>
    /// <para>
    /// <b>Restored in a finally, and the restoration is asserted.</b> A service left published in
    /// another reference is a puzzle for whichever class in this collection runs next.
    /// </para>
    /// </remarks>
    /// <returns>The task.</returns>
    [Fact]
    public async Task A_service_that_names_a_reference_is_published_in_it_by_wms_and_wfs()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE")
            ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            "GRATICULA_TEST_QUERYABLE is not set, so this FAILS rather than skips: there is no "
            + "service to name a reference on.");

        string bare = qualified.Contains('/', StringComparison.Ordinal)
            ? qualified[(qualified.LastIndexOf('/') + 1)..]
            : qualified;

        // <b>The unset state, which is the control and is what every service still holds.</b>
        int stored = await DefaultCrsAsync(bare);

        Assert.True(
            stored > 0,
            $"WFS advertises no DefaultCRS for `{bare}`, so there is nothing to compare against.");

        IReadOnlyList<string> unset = await WmsReferencesAsync(bare);

        Assert.True(
            unset.Contains(
                $"EPSG:{stored.ToString(CultureInfo.InvariantCulture)}", StringComparer.Ordinal),
            $"With nothing chosen, `{bare}` should publish the table's own EPSG:{stored} and its "
            + $"WMS layer states {string.Join(", ", unset)}. Either the fallback is gone — which "
            + "would change every service published before migration 39 — or a previous test in "
            + "this collection left a reference set on the fixture.");

        Assert.Equal(stored, await AnsweredCrsAsync(bare, srsName: null));

        int other = stored == 4326 ? 3857 : 4326;

        try
        {
            (HttpStatusCode set, string said) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/services/{bare}/srid", token!,
                JsonSerializer.Serialize(new { srid = other }));

            Assert.True(
                set == HttpStatusCode.OK, $"Naming a reference answered {(int)set}: {said}");

            // ---- WFS: the document, then the default, which are one claim ----
            int advertised = await DefaultCrsAsync(bare);

            Assert.True(
                advertised == other,
                $"`{bare}` is published in EPSG:{other} and its wfs:DefaultCRS says "
                + $"EPSG:{advertised}. That element is the only place WFS has to state the "
                + "reference a service chose, and until 2026-09-09 it was written from the "
                + "table.");

            int answered = await AnsweredCrsAsync(bare, srsName: null);

            Assert.True(
                answered == other,
                $"`{bare}` advertises EPSG:{advertised} and a GetFeature with no srsName came "
                + $"back in EPSG:{answered}. On this face the advertised reference *is* the "
                + "default, so the two are one claim computed in one place — or they agree by "
                + "accident until a service chooses something.");

            // ---- WMS: the layer publishes it, and the table's stays only for the box ----
            IReadOnlyList<string> references = await WmsReferencesAsync(bare);

            Assert.True(
                references.Count > 0
                    && references[0] == $"EPSG:{other.ToString(CultureInfo.InvariantCulture)}",
                $"`{bare}` is published in EPSG:{other} and its WMS layer states "
                + $"{(references.Count == 0 ? "nothing" : string.Join(", ", references))}. The "
                + "first entry is what the service chose; a layer stating the table's code there "
                + "is this face not reading `service.srid` at all, which is what D-229's "
                + "remaining half was.");

            // <b>The stored code is still there and is not a second offer.</b> It is there
            // because this layer states its own BoundingBox in it; the theory above is what
            // holds the two together, and this only pins that the list did not grow past two.
            Assert.True(
                references.Count <= 2,
                "A layer published in one reference states "
                + $"{string.Join(", ", references)} — the list the owner declined.");

            // ---- and a request that names one still wins, on both faces ----
            Assert.Equal(stored, await AnsweredCrsAsync(bare, stored));

            (HttpStatusCode drawn, string body, string? type) = await GetAsync(
                $"/wms?service=WMS&version=1.3.0&request=GetMap&layers={Escape(bare)}&styles="
                + $"&crs=EPSG:{stored.ToString(CultureInfo.InvariantCulture)}"
                + $"&bbox={await StoredBoxAsync(bare)}"
                + "&width=64&height=64&format=image/png");

            Assert.True(
                drawn == HttpStatusCode.OK
                    && type is not null
                    && type.StartsWith("image/png", StringComparison.Ordinal)
                    && !body.Contains("ServiceException", StringComparison.Ordinal),
                $"GetMap in EPSG:{stored} — the reference this layer is stored in — answered "
                + $"{(int)drawn} as {type ?? "nothing"} while the service publishes EPSG:{other}. "
                + "Publishing in one reference is not the same claim as being capable of only "
                + "that one, and ADR-057 §5c says so in as many words.");

            // ---- OGC API Features: offered, never the default, and storageCrs unmoved ----
            //
            // <b>A third shape, and the difference is the specification's rather than
            // ours.</b> The owner's sentence names WMS and WFS, where the service's
            // reference becomes the default. It cannot become the default here: this face
            // publishes GeoJSON, whose default is CRS84, and a bare `/items` answering in
            // EPSG:5253 would be non-conforming. So the repair is that the choice is
            // *askable* — before 2026-09-09 the collection offered CRS84, 4326 and the
            // table's, and `crs=` naming the service's reference was refused with *not one
            // of this collection's reference systems*.
            (IReadOnlyList<string> offered, string storage) = await CollectionCrsAsync(bare);

            Assert.True(
                offered.Contains(CrsUri(other), StringComparer.Ordinal),
                $"`{bare}` is published in EPSG:{other} and its OGC collection offers "
                + $"{string.Join(", ", offered)}. A reference a client cannot name is a "
                + "reference this face does not have, whatever two other faces advertise.");

            Assert.True(
                offered.Count > 0 && offered[0] == Crs84,
                $"The first entry is the default and it reads {(offered.Count == 0 ? "nothing" : offered[0])}. "
                + "GeoJSON is longitude-first CRS84 by the specification, so a collection "
                + "that made the service's reference its default would be non-conforming — "
                + "this is a no rather than a not-yet.");

            Assert.True(
                storage == CrsUri(stored),
                $"`storageCrs` reads {storage} while the table holds EPSG:{stored}. Part 2's "
                + "word is *storage*: moving it to the service's choice would be a false "
                + "statement about the database rather than a repair, and this is the one "
                + "place in the server where the two can differ.");
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Put, $"{root}/admin/services/{bare}/srid", token!,
                JsonSerializer.Serialize(new { srid = (int?)null }));
        }

        Assert.Equal(stored, await DefaultCrsAsync(bare));
        Assert.Equal(stored, await AnsweredCrsAsync(bare, srsName: null));
    }

    // ---------- plumbing ----------

    /// <summary>CRS84, which is what a GeoJSON collection defaults to.</summary>
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";

    /// <summary>One EPSG code, spelled the way OGC API Features spells one.</summary>
    private static string CrsUri(int code) =>
        "http://www.opengis.net/def/crs/EPSG/0/" + code.ToString(CultureInfo.InvariantCulture);

    /// <summary>What an OGC collection offers, and what it says it is stored in.</summary>
    private async Task<(IReadOnlyList<string> Offered, string Storage)> CollectionCrsAsync(
        string collection)
    {
        (HttpStatusCode code, string body, _) = await GetAsync(
            $"/ogc/features/v1/collections/{Escape(collection)}?f=json");

        Assert.True(
            code == HttpStatusCode.OK,
            $"The OGC collection `{collection}` answered {(int)code}: {body}");

        using JsonDocument document = JsonDocument.Parse(body);

        return (
            [.. document.RootElement.GetProperty("crs").EnumerateArray()
                .Select(c => c.GetString() ?? string.Empty)],
            document.RootElement.GetProperty("storageCrs").GetString() ?? string.Empty);
    }

    /// <summary>The EPSG code in this feature type's <c>DefaultCRS</c>.</summary>
    private async Task<int> DefaultCrsAsync(string type)
    {
        XDocument document =
            await CapabilitiesAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");

        XElement? found = document.Descendants(XName.Get("FeatureType", Wfs))
            .FirstOrDefault(t => t.Element(XName.Get("Name", Wfs))!.Value
                .EndsWith(":" + type, StringComparison.OrdinalIgnoreCase));

        Assert.True(found is not null, $"WFS lists no feature type named `{type}`.");

        return Code(found!.Element(XName.Get("DefaultCRS", Wfs))!.Value);
    }

    /// <summary>The references a WMS layer publishes, its own first.</summary>
    /// <remarks>
    /// <b>Its own only, not the inherited one.</b> The root's entry is a server-wide capability
    /// and is the same for every layer; what this asks about is what the layer says for itself,
    /// which is where the service's choice lands.
    /// </remarks>
    private async Task<IReadOnlyList<string>> WmsReferencesAsync(string layer)
    {
        XDocument document =
            await CapabilitiesAsync("/wms?service=WMS&version=1.3.0&request=GetCapabilities");

        XElement? found = Named(document)
            .FirstOrDefault(l => string.Equals(Name(l), layer, StringComparison.OrdinalIgnoreCase));

        Assert.True(found is not null, $"WMS lists no layer named `{layer}`.");

        return [.. found!.Elements().Where(e => Is(e, "CRS")).Select(e => e.Value.Trim())];
    }

    /// <summary>The reference a <c>GetFeature</c> actually answered in.</summary>
    private async Task<int> AnsweredCrsAsync(string type, int? srsName)
    {
        string urn = srsName is { } code
            ? "&srsName=" + Escape(
                "urn:ogc:def:crs:EPSG::" + code.ToString(CultureInfo.InvariantCulture))
            : string.Empty;

        (HttpStatusCode status, string body, _) = await GetAsync(
            "/wfs?service=WFS&version=2.0.0&request=GetFeature&count=1"
            + $"&typeNames={Escape("graticula:" + type)}{urn}");

        Assert.True(
            status == HttpStatusCode.OK, $"GetFeature answered {(int)status}: {Short(body)}");

        XDocument document = XDocument.Parse(body);

        string? said = document.Descendants()
            .Select(e => e.Attribute("srsName")?.Value)
            .FirstOrDefault(v => v is not null);

        Assert.True(
            said is not null,
            "No srsName anywhere in the response, so there is no way to tell what reference the "
            + $"coordinates are in. The fixture layer `{type}` is supposed to have rows.");

        return Code(said!);
    }

    /// <summary>The layer's own advertised box, as a 1.3.0 <c>BBOX</c> value.</summary>
    /// <remarks>
    /// <b>Read off the document rather than computed here.</b> The attributes are already in the
    /// axis order the reference defines — that is what makes 1.3.0 delicate — so taking the
    /// numbers as written removes this test's own opinion about axis order from a test that is
    /// not about axis order.
    /// </remarks>
    private async Task<string> StoredBoxAsync(string layer)
    {
        XDocument document =
            await CapabilitiesAsync("/wms?service=WMS&version=1.3.0&request=GetCapabilities");

        XElement box = Named(document)
            .First(l => string.Equals(Name(l), layer, StringComparison.OrdinalIgnoreCase))
            .Elements()
            .First(e => Is(e, "BoundingBox"));

        return string.Join(
            ',',
            box.Attribute("minx")!.Value,
            box.Attribute("miny")!.Value,
            box.Attribute("maxx")!.Value,
            box.Attribute("maxy")!.Value);
    }

    private async Task<XDocument> CapabilitiesAsync(string path)
    {
        (HttpStatusCode status, string body, _) = await GetAsync(path);

        Assert.True(status == HttpStatusCode.OK, $"{path} answered {(int)status}: {Short(body)}");

        return XDocument.Parse(body);
    }

    private async Task<(HttpStatusCode Status, string Body, string? MediaType)> GetAsync(
        string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Every layer with a name, which is every layer a client may ask for.</summary>
    private static IEnumerable<XElement> Named(XDocument document) =>
        document.Descendants()
            .Where(e => Is(e, "Layer") && e.Elements().Any(c => Is(c, "Name")));

    /// <summary>This layer's references plus every one it inherits.</summary>
    private static HashSet<string> Supported(XElement layer, string element)
    {
        HashSet<string> supported = new(StringComparer.Ordinal);

        for (XElement? at = layer; at is not null; at = at.Parent)
        {
            if (!Is(at, "Layer"))
            {
                continue;
            }

            foreach (XElement each in at.Elements().Where(e => Is(e, element)))
            {
                supported.Add(each.Value.Trim());
            }
        }

        return supported;
    }

    private static string Name(XElement layer) =>
        layer.Elements().First(e => Is(e, "Name")).Value;

    private static bool Is(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

    /// <summary>The EPSG code at the end of a URN or a prefixed code.</summary>
    private static int Code(string reference)
    {
        int last = reference.LastIndexOf(':');

        Assert.True(
            last >= 0
                && int.TryParse(
                    reference[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out _),
            $"`{reference}` does not end in an EPSG code, so this test cannot read it.");

        return int.Parse(
            reference[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Short(string body) => body.Length <= 200 ? body : body[..200] + "…";
}
