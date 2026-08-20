using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// OGC API Features, walked the way a conforming client walks it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-042](../../docs/adr/ADR-042-ogc-api-features.md) conditions 1 and 2.</b>
/// The link graph is <em>followed</em> here rather than assumed: every resource is
/// reached from the landing page by its <c>rel</c>, because that is what a client
/// does and a hard-coded path would pass against a server whose links are all
/// wrong.
/// </para>
/// <para>
/// <b>Refusals are status codes on this surface, unlike every other face here.</b>
/// So these assert the code as well as the document — a 200 carrying a problem
/// object would satisfy neither the specification nor a proxy.
/// </para>
/// </remarks>
public sealed class OgcFeaturesConformanceTests : ArcGisClient
{
    private const string Root = "/ogc/features/v1";

    private async Task<(HttpStatusCode Status, string MediaType, string Body)> FetchAsync(
        string path)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(path.StartsWith("http", StringComparison.Ordinal) ? path : root + path));

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> JsonAsync(string path)
    {
        (HttpStatusCode status, _, string body) = await FetchAsync(path);

        Assert.True(
            status == HttpStatusCode.OK,
            $"{path} answered {(int)status}: {body[..Math.Min(300, body.Length)]}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The first link with a <c>rel</c>, and optionally a media type.</summary>
    private static string? Follow(JsonElement document, string rel, string? type = null)
    {
        if (!document.TryGetProperty("links", out JsonElement links))
        {
            return null;
        }

        foreach (JsonElement link in links.EnumerateArray())
        {
            if (!string.Equals(link.GetProperty("rel").GetString(), rel, StringComparison.Ordinal))
            {
                continue;
            }

            if (type is not null
                && (!link.TryGetProperty("type", out JsonElement carried)
                    || !string.Equals(carried.GetString(), type, StringComparison.Ordinal)))
            {
                continue;
            }

            return link.GetProperty("href").GetString();
        }

        return null;
    }

    // ---------- the link graph ----------

    [Fact]
    public async Task Everything_is_reachable_from_the_landing_page()
    {
        // <b>Followed, not constructed.</b> A test that builds `/conformance` itself
        // passes against a landing page whose links all point at the wrong host,
        // which is the one defect this document exists to prevent.
        JsonElement landing = await JsonAsync(Root);

        foreach (string rel in (string[])["self", "conformance", "data", "service-desc"])
        {
            Assert.True(
                Follow(landing, rel) is { Length: > 0 },
                $"The landing page has no `{rel}` link, so a client cannot find that resource.");
        }

        JsonElement conformance = await JsonAsync(Follow(landing, "conformance")!);

        List<string> classes =
        [
            .. conformance.GetProperty("conformsTo").EnumerateArray()
                .Select(c => c.GetString()!),
        ];

        // Core is the one class whose absence makes everything else meaningless.
        Assert.Contains(
            "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core", classes);

        JsonElement collections = await JsonAsync(Follow(landing, "data")!);

        Assert.True(collections.GetProperty("collections").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_collection_leads_to_its_own_features()
    {
        // <b>A collection with features, not simply the first one.</b> The suites
        // share a server and another can restrict a service between the listing and
        // the read (D-75); the first entry is also the private one in this dataset.
        (string wanted, string _) = await AnyFeatureAsync();

        JsonElement collections = await JsonAsync(Root + "/collections");
        JsonElement first = default;

        foreach (JsonElement candidate in collections.GetProperty("collections").EnumerateArray())
        {
            if (string.Equals(
                candidate.GetProperty("id").GetString(), wanted, StringComparison.Ordinal))
            {
                first = candidate.Clone();
                break;
            }
        }

        Assert.NotEqual(JsonValueKind.Undefined, first.ValueKind);

        string id = first.GetProperty("id").GetString()!;

        // Every collection carries its own self and items links, and both work.
        Assert.True(Follow(first, "self") is { Length: > 0 });

        string items = Follow(first, "items", "application/geo+json")!;

        JsonElement page = await JsonAsync(items + "?limit=1");

        Assert.Equal("FeatureCollection", page.GetProperty("type").GetString());
        Assert.True(page.TryGetProperty("numberReturned", out _));

        JsonElement collection = await JsonAsync(Follow(first, "self")!);

        Assert.Equal(id, collection.GetProperty("id").GetString());
        Assert.Equal("feature", collection.GetProperty("itemType").GetString());

        // Part 2 §6.3: a collection publishes what it is stored in, so a client can
        // ask for that one and pay for no transformation.
        Assert.True(collection.TryGetProperty("storageCrs", out _));
        Assert.True(collection.GetProperty("crs").GetArrayLength() > 0);

        // Part 1 §7.13 makes extent required, and the spatial bbox is CRS84.
        JsonElement extent = collection.GetProperty("extent");
        Assert.Equal(
            "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
            extent.GetProperty("spatial").GetProperty("crs").GetString());
    }

    [Fact]
    public async Task A_feature_can_be_fetched_by_the_id_the_collection_gave_it()
    {
        // <b>The round trip is the only thing the `id` member is for.</b> An id that
        // does not resolve at `/items/{id}` is a member every client reads and none
        // can use.
        (string collection, string feature) = await AnyFeatureAsync();

        JsonElement one = await JsonAsync(
            $"{Root}/collections/{Uri.EscapeDataString(collection)}/items/"
            + Uri.EscapeDataString(feature));

        Assert.Equal("Feature", one.GetProperty("type").GetString());
        Assert.Equal(feature, one.GetProperty("id").GetString());
        Assert.True(Follow(one, "collection") is { Length: > 0 });
    }

    // ---------- filtering ----------

    [Fact]
    public async Task Both_axis_conventions_select_the_same_features()
    {
        // <b>The trap, for the third protocol.</b> CRS84 is longitude first and
        // `EPSG/0/4326` is latitude first. Read the wrong way round a request for
        // Turkey selects the Indian Ocean and the response is a valid empty
        // collection — no error anywhere.
        //
        // The extent is deliberately not square, because a square one passes with
        // the axes swapped.
        (string collection, double West, double South, double East, double North)? subject =
            await AnyExtentAsync();

        Assert.NotNull(subject);

        (string id, double west, double south, double east, double north) = subject!.Value;

        // A quarter of the extent, off-centre, so the box is neither square nor
        // symmetric about either axis.
        double x0 = west + ((east - west) * 0.10);
        double x1 = west + ((east - west) * 0.60);
        double y0 = south + ((north - south) * 0.20);
        double y1 = south + ((north - south) * 0.45);

        string items = $"{Root}/collections/{Uri.EscapeDataString(id)}/items?limit=1";

        JsonElement lonLat = await JsonAsync(
            $"{items}&bbox={N(x0)},{N(y0)},{N(x1)},{N(y1)}");

        JsonElement latLon = await JsonAsync(
            $"{items}&bbox-crs={Uri.EscapeDataString("http://www.opengis.net/def/crs/EPSG/0/4326")}"
            + $"&bbox={N(y0)},{N(x0)},{N(y1)},{N(x1)}");

        Assert.Equal(
            lonLat.GetProperty("numberMatched").GetInt64(),
            latLon.GetProperty("numberMatched").GetInt64());
    }

    [Fact]
    public async Task A_bbox_narrows_the_result_rather_than_being_ignored()
    {
        (string collection, double West, double South, double East, double North)? subject =
            await AnyExtentAsync();

        Assert.NotNull(subject);

        (string id, double west, double south, double east, double north) = subject!.Value;

        string items = $"{Root}/collections/{Uri.EscapeDataString(id)}/items?limit=1";

        long all = (await JsonAsync(items)).GetProperty("numberMatched").GetInt64();

        // Somewhere far away, chosen so it cannot overlap any real extent.
        long none = (await JsonAsync($"{items}&bbox=-179,-89,-178,-88"))
            .GetProperty("numberMatched").GetInt64();

        Assert.True(all > 0, "The collection has no features, so this asserted nothing.");
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task Paging_follows_next_and_does_not_repeat_a_feature()
    {
        // <b>`offset` paging is only sound against a stable order</b>, and OGC API
        // Features does not make the client ask for one. If the ordering is ever
        // dropped, page two repeats rows from page one and both pages stay
        // well-formed.
        (string collection, string _) = await AnyFeatureAsync();

        string items = $"{Root}/collections/{Uri.EscapeDataString(collection)}/items?limit=2";

        JsonElement first = await JsonAsync(items);

        if (first.GetProperty("numberMatched").GetInt64() < 4)
        {
            Assert.Fail($"`{collection}` has too few features to page, so this asserted nothing.");
            return;
        }

        string next = Follow(first, "next")!;

        Assert.False(string.IsNullOrEmpty(next), "A full page offered no `next` link.");

        JsonElement second = await JsonAsync(next);

        HashSet<string> ids = [.. Ids(first)];

        foreach (string id in Ids(second))
        {
            Assert.DoesNotContain(id, ids);
        }
    }

    [Fact]
    public async Task A_property_filter_narrows_by_that_property()
    {
        // Part 1 §7.15.4: every queryable property is a parameter of its own name,
        // and it is the whole of Core's attribute filtering.
        foreach (JsonElement collection in
            (await JsonAsync(Root + "/collections")).GetProperty("collections").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            (HttpStatusCode status, _, string body) = await FetchAsync(
                $"{Root}/collections/{Uri.EscapeDataString(id)}/items?limit=1");

            if (status != HttpStatusCode.OK)
            {
                continue;
            }

            JsonElement page = JsonDocument.Parse(body).RootElement;

            if (page.GetProperty("features").GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement properties = page.GetProperty("features")[0].GetProperty("properties");

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    continue;
                }

                string value = property.Value.GetString()!;

                JsonElement filtered = await JsonAsync(
                    $"{Root}/collections/{Uri.EscapeDataString(id)}/items"
                    + $"?{Uri.EscapeDataString(property.Name)}={Uri.EscapeDataString(value)}"
                    + "&limit=1");

                Assert.True(
                    filtered.GetProperty("numberMatched").GetInt64() > 0,
                    $"Filtering `{id}` by `{property.Name}={value}` — a value taken from one of "
                    + "its own features — matched nothing.");

                Assert.True(
                    filtered.GetProperty("numberMatched").GetInt64()
                        <= page.GetProperty("numberMatched").GetInt64(),
                    "A property filter matched more than the unfiltered collection.");

                return;
            }
        }

        Assert.Fail("No collection has a text property to filter by, so this asserted nothing.");
    }

    // ---------- Part 2 ----------

    [Fact]
    public async Task A_requested_crs_is_answered_in_and_declared()
    {
        // Part 2 §6.6: a response in any CRS says which one in a header, because
        // GeoJSON has nowhere to put it. A server that transformed and stayed silent
        // hands a client coordinates it will read as degrees.
        foreach (JsonElement collection in
            (await JsonAsync(Root + "/collections")).GetProperty("collections").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            List<string> systems =
            [
                .. collection.GetProperty("crs").EnumerateArray().Select(c => c.GetString()!),
            ];

            const string Mercator = "http://www.opengis.net/def/crs/EPSG/0/3857";

            if (!systems.Contains(Mercator))
            {
                continue;
            }

            string root = await RequireServerAsync();
            string path = $"{Root}/collections/{Uri.EscapeDataString(id)}/items"
                + $"?limit=1&crs={Uri.EscapeDataString(Mercator)}";

            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
            await AuthenticateAsync(request, root);

            using HttpResponseMessage response = await Http.SendAsync(request);

            Assert.True(
                response.Headers.TryGetValues("Content-Crs", out IEnumerable<string>? declared)
                || response.Content.Headers.TryGetValues("Content-Crs", out declared),
                $"`{id}` answered in Web Mercator without a Content-Crs header.");

            Assert.Contains("3857", string.Join(",", declared!), StringComparison.Ordinal);

            JsonElement page = JsonDocument
                .Parse(await response.Content.ReadAsStringAsync()).RootElement;

            if (page.GetProperty("features").GetArrayLength() == 0)
            {
                continue;
            }

            // Web Mercator metres, not degrees. A server that relabelled without
            // transforming would put every coordinate inside ±180.
            double coordinate = FirstNumber(
                page.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates"));

            Assert.True(
                Math.Abs(coordinate) > 180,
                $"`{id}` was asked for Web Mercator and answered with {coordinate}, which is a "
                + "degree rather than a metre — the reference was relabelled, not transformed.");

            return;
        }

        Assert.Fail("No collection offers Web Mercator, so this asserted nothing.");
    }

    [Fact]
    public async Task A_crs_the_collection_does_not_offer_is_refused()
    {
        (string collection, string _) = await AnyFeatureAsync();

        (HttpStatusCode status, string mediaType, string body) = await FetchAsync(
            $"{Root}/collections/{Uri.EscapeDataString(collection)}/items"
            + "?crs=" + Uri.EscapeDataString("http://www.opengis.net/def/crs/EPSG/0/27700"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("application/problem+json", mediaType);
        Assert.Equal(400, JsonDocument.Parse(body).RootElement.GetProperty("status").GetInt32());
    }

    // ---------- refusals ----------

    [Theory]
    [InlineData("bogus=1", 400)]
    [InlineData("limit=-1", 400)]
    [InlineData("offset=x", 400)]
    [InlineData("bbox=1,2,3", 400)]
    [InlineData("datetime=notatime", 400)]
    public async Task A_parameter_this_server_cannot_honour_is_refused_with_a_problem_document(
        string query, int expected)
    {
        // <b>Status codes, not a 200 with an error inside.</b> This is the first face
        // on this server where a refusal is visible to a proxy, a log and a monitor,
        // and it is the specification's choice rather than ours.
        (string collection, string _) = await AnyFeatureAsync();

        (HttpStatusCode status, string mediaType, string body) = await FetchAsync(
            $"{Root}/collections/{Uri.EscapeDataString(collection)}/items?{query}");

        Assert.Equal(expected, (int)status);
        Assert.Equal("application/problem+json", mediaType);

        JsonElement problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(expected, problem.GetProperty("status").GetInt32());
        Assert.False(
            string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()),
            "A problem document with no detail sends its reader to a log they cannot open.");
    }

    [Fact]
    public async Task An_unknown_collection_is_absent_rather_than_forbidden()
    {
        // <b>404, not 403.</b> Answering *forbidden* to a stranger asking for a
        // private layer confirms the layer exists and names it — the sharing model
        // leaking through a status code.
        (HttpStatusCode status, string mediaType, _) =
            await FetchAsync(Root + "/collections/no_such_collection/items");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("application/problem+json", mediaType);

        (HttpStatusCode feature, _, _) =
            await FetchAsync(Root + "/collections/no_such_collection/items/1");

        Assert.Equal(HttpStatusCode.NotFound, feature);
    }

    [Fact]
    public async Task An_unknown_feature_id_is_not_found()
    {
        (string collection, string _) = await AnyFeatureAsync();

        (HttpStatusCode status, _, _) = await FetchAsync(
            $"{Root}/collections/{Uri.EscapeDataString(collection)}/items/there-is-no-such-feature");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---------- the html class ----------

    [Fact]
    public async Task The_html_representation_exists_and_can_be_navigated()
    {
        // <b>ADR-042 condition 2.</b> Claiming the `html` class is a claim about
        // navigability. A page that renders the data and drops the link graph is a
        // dead end that still answers 200.
        (string collection, string _) = await AnyFeatureAsync();

        foreach (string path in (string[])
        [
            Root + "?f=html",
            Root + "/conformance?f=html",
            Root + "/collections?f=html",
            $"{Root}/collections/{Uri.EscapeDataString(collection)}?f=html",
            $"{Root}/collections/{Uri.EscapeDataString(collection)}/items?f=html",
        ])
        {
            (HttpStatusCode status, string mediaType, string body) = await FetchAsync(path);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("text/html", mediaType);

            Assert.Contains("<a href=", body, StringComparison.Ordinal);
        }

        // And the collections page names the collection, so a person can find it.
        (_, _, string collections) = await FetchAsync(Root + "/collections?f=html");

        Assert.Contains(collection, collections, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_definition_is_an_openapi_document_that_describes_the_operations()
    {
        // The `oas30` class is claimed, so a stub would be a false claim. These are
        // the five paths Part 1 defines.
        JsonElement api = await JsonAsync(Root + "/api");

        Assert.StartsWith("3.0", api.GetProperty("openapi").GetString()!, StringComparison.Ordinal);

        JsonElement paths = api.GetProperty("paths");

        foreach (string path in (string[])
        [
            "/", "/conformance", "/collections", "/collections/{collectionId}",
            "/collections/{collectionId}/items", "/collections/{collectionId}/items/{featureId}",
        ])
        {
            Assert.True(
                paths.TryGetProperty(path, out JsonElement operation),
                $"The API definition describes no `{path}`.");

            Assert.True(operation.GetProperty("get").TryGetProperty("responses", out _));
        }
    }

    // ---------- helpers ----------

    private static IEnumerable<string> Ids(JsonElement page)
    {
        foreach (JsonElement feature in page.GetProperty("features").EnumerateArray())
        {
            yield return feature.GetProperty("id").GetString()!;
        }
    }

    private static string N(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static double FirstNumber(JsonElement coordinates)
    {
        JsonElement at = coordinates;

        while (at.ValueKind == JsonValueKind.Array && at.GetArrayLength() > 0)
        {
            if (at[0].ValueKind == JsonValueKind.Number)
            {
                return at[0].GetDouble();
            }

            at = at[0];
        }

        return 0;
    }

    /// <summary>
    /// A collection with at least one feature, and that feature's id.
    /// </summary>
    /// <remarks>
    /// <b>A collection that answers anything but 200 is skipped rather than
    /// failed.</b> This is a search across the catalogue, not an assertion about any
    /// one entry, and the suites share a server — another one can stop or restrict a
    /// service between the listing and the read
    /// ([D-75](../../docs/architecture-debt.md)). Failing here would report a
    /// scheduling accident as a defect in whatever the caller was testing.
    /// </remarks>
    private async Task<(string Collection, string Feature)> AnyFeatureAsync()
    {
        foreach (JsonElement collection in
            (await JsonAsync(Root + "/collections")).GetProperty("collections").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            (HttpStatusCode status, _, string body) = await FetchAsync(
                $"{Root}/collections/{Uri.EscapeDataString(id)}/items?limit=1");

            if (status != HttpStatusCode.OK)
            {
                continue;
            }

            JsonElement page = JsonDocument.Parse(body).RootElement;

            if (page.GetProperty("features").GetArrayLength() > 0)
            {
                return (id, page.GetProperty("features")[0].GetProperty("id").GetString()!);
            }
        }

        Assert.Fail("No published collection has a feature, so nothing here can assert anything.");
        return (string.Empty, string.Empty);
    }

    /// <summary>A collection with a real extent, and that extent in CRS84.</summary>
    private async Task<(string Id, double West, double South, double East, double North)?>
        AnyExtentAsync()
    {
        foreach (JsonElement collection in
            (await JsonAsync(Root + "/collections")).GetProperty("collections").EnumerateArray())
        {
            JsonElement bbox = collection
                .GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0];

            double west = bbox[0].GetDouble();
            double south = bbox[1].GetDouble();
            double east = bbox[2].GetDouble();
            double north = bbox[3].GetDouble();

            // The default whole-world extent means the real one is unknown, and a
            // test slicing it would be testing nothing about this layer.
            if (east - west >= 359 || north - south >= 179 || east <= west || north <= south)
            {
                continue;
            }

            string id = collection.GetProperty("id").GetString()!;

            (HttpStatusCode status, _, string body) = await FetchAsync(
                $"{Root}/collections/{Uri.EscapeDataString(id)}/items?limit=1");

            if (status != HttpStatusCode.OK)
            {
                continue;
            }

            if (JsonDocument.Parse(body).RootElement
                .GetProperty("numberMatched").GetInt64() > 0)
            {
                return (id, west, south, east, north);
            }
        }

        return null;
    }
}
