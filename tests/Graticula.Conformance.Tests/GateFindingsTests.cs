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
/// The five defects the §66 gates found on 2026-08-20, each with a test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept together on purpose.</b> They are not one subject — an axis rule, an
/// output CRS, a metadata document, a timestamp and a capability string — but they
/// share a provenance, and a reader asking <em>what did the gate find</em> should be
/// able to read the answer in one place rather than five.
/// </para>
/// <para>
/// <b>Every one was a wrong answer with a 200 on it.</b> That is the defect class
/// the correctness gate went looking for, and not one of the five raised an error
/// anywhere: transposed coordinates, a colour the server does not draw, an empty
/// result for a moment that exists, and an operation promised in a document and
/// missing from the route table.
/// </para>
/// </remarks>
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

    // ---------- helpers ----------

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
