using System;
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
/// The ten defects the §66 gates found on 2026-08-20, each with a test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept together on purpose.</b> They are not one subject — an axis rule, an
/// output CRS, a metadata document, a timestamp, a capability string, a style name,
/// an error message, a truncated document, a misdiagnosed refusal and a search — but
/// they share a provenance, and a reader asking <em>what did the gates find</em>
/// should be able to read the answer in one place rather than ten.
/// </para>
/// <para>
/// <b>Five came from the correctness gate.</b> Each was a wrong answer with a 200 on
/// it — the class that gate went looking for, and not one of them raised an error
/// anywhere: transposed coordinates, a colour the server does not draw, an empty
/// result for a moment that exists, and an operation promised in a document and
/// missing from the route table.
/// </para>
/// <para>
/// <b>Three came from the failure gate, and they are what the server says when
/// something has gone wrong.</b> A coordinate system no projection database has
/// produced a successful-looking WFS document claiming a thousand features and
/// carrying none; the same mistake on the ArcGIS faces was reported as a database
/// outage, sending an operator to check a network that was fine; and the portal's
/// wildcard search — the first query an ArcGIS client sends — answered that a portal
/// with twelve items was empty.
/// </para>
/// <para>
/// <b>Two came from the consistency sweep, and they are disagreements rather than
/// wrong answers.</b> One WMS request refused a style name that a second request
/// accepted on the same layer, and a WFS refusal named an operation the same server
/// advertises and answers. Neither is wrong in isolation; both are wrong beside their
/// neighbour, which is the only way that class of defect is ever visible.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because this class walks the catalogue.</b>
/// xUnit runs test classes in parallel, and another class in this assembly publishes,
/// deletes and reconfigures services. A walker outside the collection sees the
/// catalogue mid-change and reports it as a defect in whatever it was testing —
/// [D-75](../../docs/architecture-debt.md), three times on 2026-08-20.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class GateFindingsTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> FetchAsync(string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---------- finding 1: the axis rule knew one code ----------

    [Theory]
    [InlineData("EPSG::4326")]
    [InlineData("EPSG::4258")]
    [InlineData("EPSG::4269")]
    public async Task Every_geographic_reference_is_written_latitude_first(string crs)
    {
        // <b>AxisOrder.IsLatitudeFirst answered true for 4326 alone.</b> EPSG:4258 is
        // ETRS89, the standard geographic system across most of Europe, with the
        // identical authoritative axis order — and WFS wrote it longitude-first: a
        // valid 200 with every coordinate transposed and no error anywhere.
        string? layer = await AnyPointLayerAsync();

        Assert.NotNull(layer);

        (HttpStatusCode status, string body) = await FetchAsync(
            $"/wfs?service=WFS&version=2.0.0&request=GetFeature"
            + $"&typeNames=graticula:{Uri.EscapeDataString(layer!)}&count=1"
            + $"&srsName=urn:ogc:def:crs:{crs}");

        Assert.Equal(HttpStatusCode.OK, status);

        XElement? position = XDocument.Parse(body).Descendants()
            .FirstOrDefault(e => e.Name.LocalName is "pos" or "posList");

        Assert.NotNull(position);

        string[] parts = position!.Value.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        double first = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double second = double.Parse(parts[1], CultureInfo.InvariantCulture);

        // The fixture is in Turkey: latitude about 40, longitude about 33. Both are
        // plausible numbers alone, which is why this asserts the pair rather than a
        // range — a transposition is only visible when the two are compared.
        Assert.True(
            first > second,
            $"In {crs} the first ordinate is {first} and the second {second}. This layer's "
            + "latitude is the larger of the two, so latitude is not first: the coordinates are "
            + "transposed and no client can tell.");
    }

    // ---------- finding 2: the negotiated CRS changed the header and not the order ----------

    [Fact]
    public async Task An_ogc_response_in_epsg_4326_is_latitude_first_and_crs84_is_not()
    {
        // <b>Part 2 §6.4.</b> CRS84 and EPSG/0/4326 are the same datum in opposite
        // orders. Asking for the second returned the first's coordinates under the
        // second's name in Content-Crs — and a conforming client trusts that header to
        // know how to read the geometry, so it placed every point in the wrong
        // hemisphere. Worse than not offering the class, because the class was claimed.
        string? collection = await AnyPointLayerAsync();

        Assert.NotNull(collection);

        string items =
            $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection!)}/items?limit=1";

        double[] lonLat = await FirstPositionAsync(items);

        double[] latLon = await FirstPositionAsync(
            items + "&crs=" + Uri.EscapeDataString("http://www.opengis.net/def/crs/EPSG/0/4326"));

        Assert.Equal(lonLat[0], latLon[1], 9);
        Assert.Equal(lonLat[1], latLon[0], 9);
    }

    // ---------- finding 3: one layer, two answers about its own appearance ----------

    [Fact]
    public async Task The_map_server_document_reports_the_style_the_server_actually_draws()
    {
        // <b>Four faces were asked about one layer and one disagreed.</b> The MapServer
        // layer document called the two-argument DrawingInfo, which always synthesises
        // an appearance from the name and geometry, so it reported a colour nothing
        // draws while the legend, the rendered map and the FeatureServer document all
        // agreed on the stored one. Most ArcGIS clients read this document and never
        // fetch the legend.
        foreach (string service in await EveryServiceNameAsync())
        {
            (HttpStatusCode featureStatus, string featureBody) =
                await FetchAsync($"/rest/services/{service}/FeatureServer/0?f=json");

            if (featureStatus != HttpStatusCode.OK)
            {
                continue;
            }

            JsonElement feature = JsonDocument.Parse(featureBody).RootElement;

            // Only a layer with a stored style can show the difference: where the style
            // is generated, both paths generate the same thing.
            if (!feature.TryGetProperty("drawingInfoGenerated", out JsonElement generated)
                || generated.GetBoolean())
            {
                continue;
            }

            (HttpStatusCode mapStatus, string mapBody) =
                await FetchAsync($"/rest/services/{service}/MapServer/0?f=json");

            if (mapStatus != HttpStatusCode.OK)
            {
                continue;
            }

            JsonElement map = JsonDocument.Parse(mapBody).RootElement;

            Assert.Equal(
                Colour(feature.GetProperty("drawingInfo")),
                Colour(map.GetProperty("drawingInfo")));

            return;
        }

        Assert.Fail("No layer has a stored style, so this asserted nothing.");
    }

    // ---------- finding 4: an instant that exists matched nothing ----------

    [Fact]
    public async Task An_exact_instant_matches_the_feature_that_carries_it()
    {
        // <b>A .NET tick is 100 nanoseconds and PostgreSQL resolves to a
        // microsecond.</b> The upper bound was parsed.AddTicks(1), which round-trips
        // through the database back to parsed — so the predicate became
        // "column >= X AND column < X", unsatisfiable by construction, and every
        // exact-instant datetime returned nothing. The most natural query anybody makes
        // of a temporal layer, answered empty, silently.
        (string Collection, string Moment)? subject = await AnyTemporalAsync();

        if (subject is null)
        {
            Assert.Fail("No collection is temporal, so this asserted nothing.");
            return;
        }

        (string id, string at) = subject.Value;

        (HttpStatusCode status, string body) = await FetchAsync(
            $"/ogc/features/v1/collections/{Uri.EscapeDataString(id)}/items"
            + $"?datetime={Uri.EscapeDataString(at)}");

        Assert.Equal(HttpStatusCode.OK, status);

        Assert.True(
            JsonDocument.Parse(body).RootElement.GetProperty("numberMatched").GetInt64() > 0,
            $"datetime={at} is a value taken from one of {id}'s own features and it matched "
            + "nothing.");
    }

    // ---------- finding 5: a capability promised and missing ----------

    [Fact]
    public async Task Every_capability_the_map_server_claims_is_one_it_answers()
    {
        // <b>The document said Map,Query,Data and /MapServer/{id}/query was a 404.</b>
        // A capabilities string is a machine-readable contract a client checks before
        // it acts; the one thing it must never be is untrue.
        foreach (string service in await EveryServiceNameAsync())
        {
            (HttpStatusCode status, string body) =
                await FetchAsync($"/rest/services/{service}/MapServer/0?f=json");

            if (status != HttpStatusCode.OK)
            {
                continue;
            }

            string claimed = JsonDocument.Parse(body).RootElement
                .GetProperty("capabilities").GetString()!;

            if (!claimed.Contains("Query", StringComparison.Ordinal))
            {
                continue;
            }

            (HttpStatusCode query, _) = await FetchAsync(
                $"/rest/services/{service}/MapServer/0/query"
                + "?where=1%3D1&returnCountOnly=true&f=json");

            Assert.True(
                query == HttpStatusCode.OK,
                $"{service}'s MapServer claims '{claimed}' and its query answered "
                + $"{(int)query}. Either the route exists or the claim goes.");

            return;
        }
    }

    // ---------- consistency S1: two doors, one lock ----------

    [Fact]
    public async Task An_unknown_style_is_refused_by_the_legend_as_well_as_by_the_map()
    {
        // <b>GetMap refused it and GetLegendGraphic drew the default swatch.</b> A
        // legend is read by a human and believed; one that silently describes a
        // different style than the map beside it is worse than an error.
        const string Nonsense = "no-such-style-9e3f";

        string? layer = await FirstWmsLayerAsync();

        if (layer is null)
        {
            return;
        }

        string escaped = Uri.EscapeDataString(layer);

        (_, string map) = await FetchAsync(
            "/wms?service=WMS&version=1.3.0&request=GetMap"
            + $"&layers={escaped}&styles={Nonsense}&crs=EPSG:4326"
            + "&bbox=35,25,43,45&width=200&height=150&format=image%2Fpng");

        Assert.Equal("StyleNotDefined", RefusalCode(map));

        (_, string legend) = await FetchAsync(
            "/wms?service=WMS&version=1.3.0&request=GetLegendGraphic"
            + $"&layer={escaped}&style={Nonsense}&format=image%2Fpng");

        // <b>The code, not the status.</b> A WMS refusal is a ServiceExceptionReport
        // carried by a 200 — that is what the specification asks for and what CITE
        // checks — so a test that asserted on the status would pass for the wrong
        // reason on one face and fail for the wrong reason on the other. This one did:
        // it read the legend's correct refusal as a success because the 200 was there.
        Assert.Equal("StyleNotDefined", RefusalCode(legend));
    }

    // ---------- consistency S3: a refusal that named an implemented operation ----------

    [Fact]
    public async Task The_wfs_refusal_never_names_an_operation_the_capabilities_advertise()
    {
        // <b>The message said GetPropertyValue is not implemented while the same
        // server advertised and answered it.</b> An error message is the only
        // documentation many clients read.
        (_, string capabilities) = await FetchAsync(
            "/wfs?service=WFS&version=2.0.0&request=GetCapabilities");

        string[] advertised =
        [
            .. XDocument.Parse(capabilities)
                .Descendants()
                .Where(e => e.Name.LocalName == "Operation")
                .Select(e => e.Attribute("name")?.Value)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!),
        ];

        Assert.NotEmpty(advertised);

        (_, string refusal) = await FetchAsync(
            "/wfs?service=WFS&version=2.0.0&request=NoSuchOperation");

        foreach (string operation in advertised)
        {
            Assert.False(
                refusal.Contains($"{operation} are not", StringComparison.Ordinal)
                    || refusal.Contains($"{operation} is not", StringComparison.Ordinal),
                $"the refusal says {operation} is not implemented and GetCapabilities "
                + $"advertises it. Full text: {refusal}");
        }
    }

    // ---------- failure F5: a bad CRS produced a document that lied ----------

    [Fact]
    public async Task An_unusable_reference_system_is_refused_before_the_body_begins()
    {
        // <b>It answered 200 with a well-formed WFS collection announcing a thousand
        // features and carrying none.</b> `srsName` was checked for spelling, the
        // response header was written, and the database refused the transform on the
        // first row — after which XmlWriter closed the open element on its way out. A
        // client that tolerates a truncated chunked stream, and many do, read a
        // complete, successful, empty answer. Silent data loss presented as success.
        const string Nonsense = "urn:ogc:def:crs:EPSG::999999";

        string? layer = await AnyPointLayerAsync();

        Assert.NotNull(layer);

        (HttpStatusCode status, string body) = await FetchAsync(
            "/wfs?service=WFS&version=2.0.0&request=GetFeature"
            + $"&typeNames=graticula:{Uri.EscapeDataString(layer!)}&count=1"
            + $"&srsName={Uri.EscapeDataString(Nonsense)}");

        Assert.NotEqual(HttpStatusCode.OK, status);

        XDocument document = XDocument.Parse(body);

        Assert.Equal("ExceptionReport", document.Root!.Name.LocalName);
        Assert.Equal(
            "srsName",
            document.Descendants()
                .First(e => e.Name.LocalName == "Exception")
                .Attribute("locator")?.Value);
    }

    // ---------- failure F4: a caller's mistake reported as an outage ----------

    [Fact]
    public async Task A_reference_system_the_database_rejects_is_the_callers_fault()
    {
        // <b>It answered 503 "a database this server depends on is unreachable" while
        // the database was up and answering everything else.</b> PostGIS raises XX000
        // for an invalid SRID, XX000 was not classified, and the general Npgsql branch
        // caught it — so four surfaces sent an operator to check the network. 503 also
        // tells a client to retry, and retrying a bad parameter never works.
        foreach (string service in await EveryServiceNameAsync())
        {
            (HttpStatusCode status, string body) = await FetchAsync(
                $"/rest/services/{service}/FeatureServer/0/query"
                + "?where=1%3D1&outFields=*&outSR=999999&resultRecordCount=1&f=json");

            if (status == HttpStatusCode.NotFound)
            {
                continue;
            }

            Assert.True(
                status == HttpStatusCode.BadRequest,
                $"{service} answered {(int)status} to an SRID no projection database has. "
                + $"A caller's parameter is a 400. Body: {body}");

            return;
        }

        Assert.Fail("No service answered, so this asserted nothing.");
    }

    // ---------- failure F10: the portal's default query returned an empty portal ----------

    [Fact]
    public async Task The_wildcard_search_returns_what_a_field_search_returns()
    {
        // <b>`q=*` is what an ArcGIS client sends first, and it answered total 0.</b>
        // The wildcard was treated as a literal word to find in the title, with nothing
        // to say the syntax was unsupported — so a portal with twelve items looked
        // empty to the query that discovers it.
        static long Total(string body) =>
            JsonDocument.Parse(body).RootElement.GetProperty("total").GetInt64();

        (_, string wildcard) = await FetchAsync("/sharing/rest/search?q=*&f=json");
        (_, string owned) = await FetchAsync("/sharing/rest/search?q=owner%3Aroot&f=json");

        Assert.True(
            Total(wildcard) >= Total(owned) && Total(owned) > 0,
            $"`q=*` found {Total(wildcard)} and `q=owner:root` found {Total(owned)}. "
            + "The wildcard cannot see less than a filter.");
    }

    // ---------- helpers ----------

    private static string RefusalCode(string body)
    {
        XDocument document = XDocument.Parse(body);

        Assert.True(
            document.Root!.Name.LocalName == "ServiceExceptionReport",
            $"answered with <{document.Root.Name.LocalName}> rather than a refusal.");

        return document.Descendants()
            .First(e => e.Name.LocalName == "ServiceException")
            .Attribute("code")!.Value;
    }

    private async Task<string?> FirstWmsLayerAsync()
    {
        (_, string body) = await FetchAsync(
            "/wms?service=WMS&version=1.3.0&request=GetCapabilities");

        return XDocument.Parse(body)
            .Descendants()
            .Where(e => e.Name.LocalName == "Layer")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "Name")?.Value)
            .FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static string Colour(JsonElement drawingInfo) =>
        drawingInfo.GetProperty("renderer").GetProperty("symbol").GetProperty("color").ToString();

    private async Task<double[]> FirstPositionAsync(string path)
    {
        (HttpStatusCode status, string body) = await FetchAsync(path);

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement at = JsonDocument.Parse(body).RootElement
            .GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");

        while (at.ValueKind == JsonValueKind.Array && at[0].ValueKind == JsonValueKind.Array)
        {
            at = at[0];
        }

        return [at[0].GetDouble(), at[1].GetDouble()];
    }

    /// <summary>
    /// A published point layer, whose single position makes a transposition visible.
    /// </summary>
    private async Task<string?> AnyPointLayerAsync()
    {
        (HttpStatusCode status, string body) = await FetchAsync("/ogc/features/v1/collections");

        if (status != HttpStatusCode.OK)
        {
            return null;
        }

        foreach (JsonElement collection in
            JsonDocument.Parse(body).RootElement.GetProperty("collections").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            (HttpStatusCode page, string items) = await FetchAsync(
                $"/ogc/features/v1/collections/{Uri.EscapeDataString(id)}/items?limit=1");

            if (page != HttpStatusCode.OK)
            {
                continue;
            }

            JsonElement features = JsonDocument.Parse(items).RootElement.GetProperty("features");

            if (features.GetArrayLength() > 0
                && string.Equals(
                    features[0].GetProperty("geometry").GetProperty("type").GetString(),
                    "Point",
                    StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>A temporal collection, and an instant one of its features carries.</summary>
    /// <remarks>
    /// <b>The instant comes from a feature, not from the extent.</b> A value read off
    /// the collection's declared interval is a value the data is only claimed to hold;
    /// one read off a feature provably exists, which is what the assertion needs.
    /// </remarks>
    private async Task<(string Collection, string Moment)?> AnyTemporalAsync()
    {
        (HttpStatusCode status, string body) = await FetchAsync("/ogc/features/v1/collections");

        if (status != HttpStatusCode.OK)
        {
            return null;
        }

        foreach (JsonElement collection in
            JsonDocument.Parse(body).RootElement.GetProperty("collections").EnumerateArray())
        {
            JsonElement interval = collection
                .GetProperty("extent").GetProperty("temporal").GetProperty("interval")[0];

            if (interval[0].ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            string id = collection.GetProperty("id").GetString()!;

            (HttpStatusCode page, string items) = await FetchAsync(
                $"/ogc/features/v1/collections/{Uri.EscapeDataString(id)}/items?limit=1");

            if (page != HttpStatusCode.OK)
            {
                continue;
            }

            JsonElement features = JsonDocument.Parse(items).RootElement.GetProperty("features");

            if (features.GetArrayLength() == 0)
            {
                continue;
            }

            foreach (JsonProperty property in
                features[0].GetProperty("properties").EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(
                        property.Value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset moment)
                    && moment.Year is > 1900 and < 2200)
                {
                    return (id, moment.UtcDateTime.ToString(
                        "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                }
            }
        }

        return null;
    }
}
