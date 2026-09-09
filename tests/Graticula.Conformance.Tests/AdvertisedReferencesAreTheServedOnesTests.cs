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
/// Per face: the coordinate references a document advertises, against the ones the face will
/// actually serve.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-060](../../docs/adr/ADR-060-the-axis-order-comes-from-the-register.md) condition 4,
/// and it is the condition that is *not* discharged.</b> ADR-060 fixed which ordinate comes
/// first for 1,444 more codes; this file is about a different failure, which that decision
/// widened rather than caused. A client reads a capabilities document, picks a reference from
/// it and is refused — or, worse, picks one that appears in no document anywhere and is served.
/// The second is the one nobody notices: the answer is a 200 carrying real coordinates, and the
/// only evidence that the server never agreed to produce them is a document the client already
/// read.
/// </para>
/// <para>
/// <b>The two sets are computed in different places on every face, which is the whole of the
/// defect.</b> On OGC API Features they are the same expression —
/// <c>CollectionMetadata.CoordinateSystems</c> writes the document and <c>OgcRequest.TryCrs</c>
/// refuses anything outside it — so that face cannot drift. On WFS the document writes one
/// <c>DefaultCRS</c> per feature type and the request path asks the projector whether it knows
/// the code at all; on WMS the document writes three and <c>WmsRequest.TrySrid</c> accepts any
/// <c>EPSG:n</c>. Those two are not near-misses, they are unbounded: every code in PROJ's
/// register is served by a document that named one or three.
/// </para>
/// <para>
/// <b>So this suite asserts two faces hold and pins the two that do not.</b> Asserting only the
/// halves that pass would read as a closed loop over a face list that is half open. The gap test
/// fails the day either face starts refusing an unadvertised reference — which is the point:
/// whoever repairs it has to come back here and to condition 4 in the same commit, rather than
/// leaving a register saying the gap is still open.
/// </para>
/// <para>
/// <b>The ArcGIS faces are named rather than covered, because the format has nowhere to put the
/// claim.</b> A FeatureServer or MapServer document publishes one <c>spatialReference</c> — its
/// own — and the REST API defines <c>outSR</c> as any wkid, with no key anywhere that advertises
/// a set of output references. Measured: the document says <c>3857</c> and <c>outSR=5253</c>,
/// <c>32636</c> and <c>2039</c> all answer 200 in the reference asked for. There is no
/// advertised set to be inconsistent with, so there is no assertion to write; a test that
/// invented one would be testing this suite's opinion of ArcGIS rather than the server.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because this class walks the catalogue.</b> It reads
/// three capabilities documents and a collection list, and another class in this assembly
/// publishes and reconfigures services while it does — [D-75](../../docs/architecture-debt.md).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AdvertisedReferencesAreTheServedOnesTests : ArcGisClient
{
    private const string Features = "/ogc/features/v1";
    private const string Wfs = "http://www.opengis.net/wfs/2.0";

    /// <summary>Codes to ask for that no fixture document advertises.</summary>
    /// <remarks>
    /// <b>Three, so that a deployment storing its data in one of them still has a probe.</b>
    /// Each is in PROJ's register everywhere, so *the server does not know it* is never the
    /// reason a request is refused — which is the confusion this suite has to avoid, because a
    /// refusal for that reason looks exactly like the refusal condition 4 asks for. 5253 is
    /// TUREF / TM30, northing-first and outside 4000–4999, which is the family ADR-060 is about;
    /// 32636 and 2039 are ordinary easting-first projected grids, so the probe is not secretly
    /// testing the axis rule instead.
    /// </remarks>
    private static readonly int[] Unadvertised = [5253, 32636, 2039];

    // ---------- the faces where the two sets are the same expression ----------

    /// <summary>
    /// A collection serves every reference it lists and refuses every one it does not, on all
    /// three paths that take a reference.
    /// </summary>
    /// <remarks>
    /// <b>All three paths, because there are two implementations of the rule.</b>
    /// <c>OgcRequest.TryCrs</c> serves <c>items</c> and <c>bbox-crs</c>; the single-feature path
    /// checks the collection's list itself, in <c>OgcFeaturesEndpoints</c>. Two copies of one
    /// rule is how a face comes to hold on the path somebody tested and not on the one beside
    /// it, so both are driven here rather than one standing in for the other.
    /// </remarks>
    [Fact]
    public async Task Ogc_api_features_serves_every_reference_it_advertises_and_refuses_the_rest()
    {
        JsonElement collections = await GetJsonAsync($"{Features}/collections");

        List<string> wrong = [];
        string? driven = null;

        foreach (JsonElement collection in collections.GetProperty("collections").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            List<string> advertised =
            [
                .. collection.GetProperty("crs").EnumerateArray().Select(c => c.GetString()!),
            ];

            string storage = collection.GetProperty("storageCrs").GetString()!;

            // Free, and it is the advertisement contradicting itself rather than the server
            // contradicting the advertisement: Part 2 §6.3 has a client ask for the storage
            // reference to get no transformation at all, and it can only ask for what is listed.
            if (!advertised.Contains(storage, StringComparer.Ordinal))
            {
                wrong.Add(
                    $"`{id}` says it is stored in `{storage}` and does not list it in `crs`, so "
                    + "the one reference a client asks for to avoid a transform is the one it "
                    + "may not ask for.");
            }

            // <b>One collection is driven, not nine.</b> Every request below is a query against
            // a live database and the rule under test is one expression shared by all of them;
            // walking the catalogue with seven requests each is a load test wearing a
            // conformance suite's clothes. The cheap half above still runs for every collection.
            if (driven is not null)
            {
                continue;
            }

            driven = id;

            foreach (string crs in advertised)
            {
                (HttpStatusCode status, string body) = await FetchAsync(
                    $"{Features}/collections/{Escape(id)}/items?limit=1&crs={Escape(crs)}");

                if (status != HttpStatusCode.OK)
                {
                    wrong.Add(
                        $"`{id}` advertises `{crs}` and answered {(int)status} for it: "
                        + Short(body) + " An advertised reference that is refused is a client "
                        + "picking from a list the server wrote and being told it was wrong.");
                }
            }

            string probe = OgcUri(Probe(advertised.Select(OgcCode).Where(c => c is not null)!));

            foreach ((string parameter, string path) in ((string, string)[])
            [
                ("crs on items",
                    $"{Features}/collections/{Escape(id)}/items?limit=1&crs={Escape(probe)}"),
                ("bbox-crs on items",
                    $"{Features}/collections/{Escape(id)}/items?limit=1&bbox=0,0,1,1"
                    + $"&bbox-crs={Escape(probe)}"),
                ("crs on one feature",
                    $"{Features}/collections/{Escape(id)}/items/{await FirstFeatureAsync(id)}"
                    + $"?crs={Escape(probe)}"),
            ])
            {
                (HttpStatusCode status, string body) = await FetchAsync(path);

                if (status == HttpStatusCode.OK)
                {
                    wrong.Add(
                        $"`{id}` does not advertise `{probe}` and served it anyway — {parameter}. "
                        + "The response carries coordinates in a reference the collection "
                        + "document never offered, and the client has nothing to tell it so.");
                }
                else if (status != HttpStatusCode.BadRequest)
                {
                    wrong.Add(
                        $"`{id}` refused the unadvertised `{probe}` with {(int)status} rather "
                        + $"than 400: {Short(body)} A client cannot tell a reference it may not "
                        + "ask for from a server that is broken.");
                }
            }
        }

        Assert.NotNull(driven);
        NothingIsWrong(wrong, "collections whose advertised references are not the served ones");
    }

    /// <summary>
    /// A vector tile service names one reference, and there is no second one to ask for.
    /// </summary>
    /// <remarks>
    /// <b>The invariant holds here by having nowhere to break.</b> <c>tileInfo</c> carries the
    /// scheme's reference, <c>fullExtent</c> carries the same one, and the tile route takes no
    /// reference parameter at all — so the set advertised and the set served are both the same
    /// singleton. Asserting the bytes are unchanged by a parameter is the part worth pinning:
    /// a tile route that quietly grew an <c>outSR</c> would be a second reference served by a
    /// document that names one.
    /// </remarks>
    [Fact]
    public async Task A_vector_tile_service_advertises_one_reference_and_serves_only_that_one()
    {
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE")!;

        Assert.False(
            string.IsNullOrWhiteSpace(service),
            "GRATICULA_TEST_TILE_SERVICE is not set, so this FAILS rather than skips: a tile "
            + "service comes only from hosted data and cannot be discovered from the catalogue.");

        JsonElement document =
            await GetJsonAsync($"/rest/services/{service}/VectorTileServer");

        int scheme = document.GetProperty("tileInfo").GetProperty("spatialReference")
            .GetProperty("latestWkid").GetInt32();

        int extent = document.GetProperty("fullExtent").GetProperty("spatialReference")
            .GetProperty("latestWkid").GetInt32();

        Assert.True(
            scheme == extent,
            $"The tiling scheme is in {scheme} and the extent is reported in {extent}. A client "
            + "that frames its first request from the extent frames it in the wrong reference.");

        (int z, int y, int x) = await PopulatedTileAsync(service, document);
        string tile = $"/rest/services/{service}/VectorTileServer/tile/{z}/{y}/{x}.pbf";

        (HttpStatusCode plain, byte[] before) = await BytesAsync(tile);
        (HttpStatusCode asked, byte[] after) =
            await BytesAsync($"{tile}?outSR=5253&crs=EPSG:5253&bboxSR=5253");

        Assert.Equal(plain, asked);

        Assert.True(
            before.SequenceEqual(after),
            $"A tile asked for in EPSG:5253 came back different from the same tile asked for "
            + $"plainly ({before.Length} bytes against {after.Length}). This face advertises "
            + $"{scheme} and nothing else, so either it has grown a second reference it does "
            + "not publish, or it is honouring a parameter it does not understand.");
    }

    // ---------- the faces where they are not, which is condition 4 ----------

    /// <summary>
    /// WFS and WMS serve references they never advertised, which is what ADR-060 condition 4
    /// says and what keeps it open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserts a defect, deliberately, and it is the only kind of test that keeps
    /// one honest.</b> Condition 4 is written down and nothing measures it, so the register can
    /// say *open* long after the code says otherwise — in either direction. When either face
    /// starts refusing an unadvertised reference this fails, naming the face, and the person who
    /// repaired it discharges the condition and deletes the arm rather than discovering months
    /// later that the ADR was stale. That is [D-130](../../docs/architecture-debt.md)'s shape
    /// caught by a build instead of by a sweep.
    /// </para>
    /// <para>
    /// <b>Measured 2026-09-09 against the fixture</b>, whose layers are stored in 3857 and 4326:
    /// WFS advertises one <c>DefaultCRS</c> per feature type and zero <c>OtherCRS</c>, and
    /// answers <c>srsName=urn:ogc:def:crs:EPSG::5253</c> with a document declaring exactly that
    /// <c>srsName</c>; WMS advertises <c>CRS:84</c>, <c>EPSG:4326</c> and <c>EPSG:3857</c> and
    /// draws the layer in EPSG:5253, on 1.3.0 and 1.1.1, on <c>GetMap</c> and
    /// <c>GetFeatureInfo</c> alike.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Wfs_and_wms_still_serve_references_they_do_not_advertise()
    {
        List<string> repaired = [];

        // ---- WFS ----
        XDocument capabilities =
            await XmlAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");

        List<int> advertised =
        [
            .. capabilities.Descendants()
                .Where(e => e.Name.LocalName is "DefaultCRS" or "OtherCRS")
                .Select(e => UrnCode(e.Value))
                .Where(c => c is not null)
                .Select(c => c!.Value),
        ];

        string type = capabilities.Descendants(XName.Get("FeatureType", Wfs))
            .Select(t => t.Element(XName.Get("Name", Wfs))!.Value)
            .First();

        int code = Probe(advertised.Cast<int?>());
        string urn = $"urn:ogc:def:crs:EPSG::{code.ToString(CultureInfo.InvariantCulture)}";

        (HttpStatusCode status, string body) = await FetchAsync(
            "/wfs?service=WFS&version=2.0.0&request=GetFeature"
            + $"&typeNames={Escape(type)}&count=1&srsName={Escape(urn)}");

        if (status != HttpStatusCode.OK || !body.Contains(urn, StringComparison.Ordinal))
        {
            repaired.Add(
                $"WFS answered {(int)status} for `srsName={urn}` against `{type}`, and the "
                + "capabilities document advertises it nowhere. If that is a refusal, ADR-060 "
                + "condition 4's WFS half is repaired: discharge it and delete this arm.");
        }

        // ---- WMS ----
        XDocument map = await XmlAsync("/wms?service=WMS&version=1.3.0&request=GetCapabilities");

        List<string> named =
        [
            .. map.Descendants().Where(e => e.Name.LocalName == "CRS").Select(e => e.Value.Trim()),
        ];

        XElement layer = map.Descendants()
            .First(e => e.Name.LocalName == "Layer"
                && e.Elements().Any(c => c.Name.LocalName == "Name"));

        string name = layer.Elements().First(e => e.Name.LocalName == "Name").Value;

        int wms = Probe(
            named.Select(c => c.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(c[5..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int parsed)
                ? parsed
                : (int?)null));

        (HttpStatusCode drawn, string image) = await FetchAsync(
            "/wms?service=WMS&version=1.3.0&request=GetMap"
            + $"&layers={Escape(name)}&styles=&crs=EPSG:{wms.ToString(CultureInfo.InvariantCulture)}"
            + "&bbox=0,0,1000000,1000000&width=64&height=64&format=image/png");

        // A refusal is XML on this face whatever the status: WMS puts its exceptions in a
        // ServiceExceptionReport, and 1.3.0 §6.10 lets a server send one with 200.
        bool refused = image.Contains("ServiceException", StringComparison.Ordinal);

        if (drawn != HttpStatusCode.OK || refused)
        {
            repaired.Add(
                $"WMS answered `crs=EPSG:{wms.ToString(CultureInfo.InvariantCulture)}` on "
                + $"`{name}` with {(int)drawn}{(refused ? " and a ServiceException" : string.Empty)}"
                + ", and its capabilities document advertises "
                + $"{string.Join(", ", named.Distinct(StringComparer.Ordinal))} and nothing else. "
                + "If that is `InvalidCRS`, ADR-060 condition 4's WMS half is repaired: "
                + "discharge it and delete this arm.");
        }

        NothingIsWrong(
            repaired,
            "faces that used to serve unadvertised references and have stopped — ADR-060 "
            + "condition 4 has moved and the ADR has not");
    }

    // ---------- plumbing ----------

    /// <summary>Fails with every entry on its own line rather than the first fifty characters.</summary>
    /// <remarks>
    /// <b>[D-173](../../docs/architecture-debt.md), reaching a third suite.</b> Each entry here is
    /// a written sentence naming a face, what was asked and what came back; xUnit renders a
    /// non-empty collection as the head of its first item, so <c>Assert.Empty</c> throws all of it
    /// away — and on a CI run the message is the only evidence there is.
    /// </remarks>
    private static void NothingIsWrong(List<string> found, string what) =>
        Assert.True(found.Count == 0, $"{what} ({found.Count}):\n  " + string.Join("\n  ", found));

    /// <summary>The first probe code that is not already advertised.</summary>
    private static int Probe(IEnumerable<int?> advertised)
    {
        HashSet<int> known = [.. advertised.Where(c => c is not null).Select(c => c!.Value)];

        foreach (int candidate in Unadvertised)
        {
            if (!known.Contains(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail(
            "Every probe code is advertised by this deployment, so there is nothing left to ask "
            + $"for that the document does not offer. Add a code to {nameof(Unadvertised)} that "
            + "PROJ knows and this fixture does not publish.");

        throw new InvalidOperationException();
    }

    private static string OgcUri(int code) =>
        "http://www.opengis.net/def/crs/EPSG/0/" + code.ToString(CultureInfo.InvariantCulture);

    /// <summary>The EPSG code in an OGC CRS URI, or null when it names something else.</summary>
    private static int? OgcCode(string uri)
    {
        const string Prefix = "http://www.opengis.net/def/crs/EPSG/0/";

        return uri.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(
                uri[Prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int code)
            ? code
            : null;
    }

    /// <summary>The EPSG code in a <c>urn:ogc:def:crs:EPSG::n</c>, or null.</summary>
    private static int? UrnCode(string urn)
    {
        int last = urn.LastIndexOf(':');

        return last >= 0
            && int.TryParse(
                urn[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
            ? code
            : null;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Short(string body) =>
        body.Length <= 200 ? body : body[..200] + "…";

    private async Task<string> FirstFeatureAsync(string id)
    {
        JsonElement page = await GetJsonAsync($"{Features}/collections/{Escape(id)}/items?limit=1");

        JsonElement features = page.GetProperty("features");

        Assert.True(
            features.GetArrayLength() > 0,
            $"`{id}` has no features, so the single-feature path cannot be driven against it. "
            + "The fixture is supposed to seed rows; a suite that shrugs here reports green on "
            + "nothing.");

        return features[0].GetProperty("id").ToString();
    }

    private async Task<(HttpStatusCode Status, string Body)> FetchAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, byte[] Body)> BytesAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private async Task<XDocument> XmlAsync(string path)
    {
        (HttpStatusCode status, string body) = await FetchAsync(path);

        Assert.True(
            status == HttpStatusCode.OK,
            $"{path} answered {(int)status}: {Short(body)}");

        return XDocument.Parse(body);
    }

    /// <summary>A tile inside the service's own declared extent that has bytes in it.</summary>
    /// <remarks>
    /// <b>Not the centre one.</b> An extent is a bounding box and says nothing about what is in
    /// the middle of it — <c>VectorTileConformanceTests</c> learned that against a layer whose
    /// centroid landed in a gap between clusters. This walks a bounded block and takes the first
    /// tile with anything in it, so the comparison below is between two real tiles rather than
    /// between two empty responses.
    /// </remarks>
    private async Task<(int Z, int Y, int X)> PopulatedTileAsync(string service, JsonElement document)
    {
        JsonElement extent = document.GetProperty("fullExtent");
        JsonElement origin = document.GetProperty("tileInfo").GetProperty("origin");

        double left = origin.GetProperty("x").GetDouble();
        double top = origin.GetProperty("y").GetDouble();

        const int Zoom = 12;
        int side = 1 << Zoom;
        double size = Math.Abs(left) * 2 / side;

        int x0 = Math.Clamp((int)((extent.GetProperty("xmin").GetDouble() - left) / size), 0, side - 1);
        int x1 = Math.Clamp((int)((extent.GetProperty("xmax").GetDouble() - left) / size), 0, side - 1);
        int y0 = Math.Clamp((int)((top - extent.GetProperty("ymax").GetDouble()) / size), 0, side - 1);
        int y1 = Math.Clamp((int)((top - extent.GetProperty("ymin").GetDouble()) / size), 0, side - 1);

        const int Limit = 4;

        for (int x = x0; x <= Math.Min(x1, x0 + Limit); x++)
        {
            for (int y = y0; y <= Math.Min(y1, y0 + Limit); y++)
            {
                (_, byte[] bytes) = await BytesAsync(
                    $"/rest/services/{service}/VectorTileServer/tile/{Zoom}/{y}/{x}.pbf");

                if (bytes.Length > 0)
                {
                    return (Zoom, y, x);
                }
            }
        }

        Assert.Fail(
            "No zoom-12 tile inside this service's own declared extent returned any bytes, so "
            + "there is nothing to compare. Either the extent is wrong or the tile path is.");

        throw new InvalidOperationException();
    }
}
