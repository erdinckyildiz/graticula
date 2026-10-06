using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-177: OGC API Records over a running server — who sees which record, what the query parameters of 20-004r1
/// Table 12 do, that a record's links answer, and that every claimed conformance class has a proof here.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no OGC executable test suite for OGC API Records</b> (ADR-177 condition 1), so this file is the claim's
/// evidence, beside the unit tests that pin the request rules. Each assertion is named for the requirement it observes.
/// </para>
/// <para>
/// <b>In the catalogue walk</b>, because it publishes a service and changes its sharing; it cleans up what it makes.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class OgcRecordsConformanceTests : ArcGisClient
{
    private const string Root = "/ogc/records/v1";
    private const string Items = Root + "/collections/catalog/items";
    private const string Conf = "http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/";

    /// <summary>Every claim, each observed by a test below.</summary>
    private static readonly string[] Proven =
    [
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core", // The_landing_page_conformance_api_and_catalogue_link_each_other
        Conf + "record-core", // A_record_is_a_feature_with_its_links_and_they_answer
        Conf + "record-collection", // The_landing_page_conformance_api_and_catalogue_link_each_other
        Conf + "record-core-query-parameters", // The_query_parameters_select_as_table_12_says
        Conf + "records-api", // A_record_is_a_feature_with_its_links_and_they_answer, Refusals_are_problems
        Conf + "searchable-catalog", // all of the above
        Conf + "json", // A_record_is_a_feature_with_its_links_and_they_answer
        Conf + "html", // Every_resource_has_an_html_page_with_its_links
        Conf + "oas30", // The_landing_page_conformance_api_and_catalogue_link_each_other
        Conf + "autodiscovery", // The_landing_page_conformance_api_and_catalogue_link_each_other
    ];

    private static readonly string[] WfsOnly = ["WFS"];

    /// <summary>
    /// A client that keeps no cookies, so that a request sent without a token is anonymous.
    /// </summary>
    /// <remarks>
    /// <b>Found by the first run of this class.</b> The fixture's shared client keeps cookies, signing a member in
    /// through it left the member's session cookie on it, and every "anonymous" request after was the member's — an
    /// organisation-shared record appeared to a stranger, which was the test and not the server.
    /// </remarks>
    private static readonly HttpClient Plain = new(new HttpClientHandler
    {
        UseCookies = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private async Task<(HttpStatusCode Status, string? Type, string Body)> GetAsync(string pathOrUrl, string? token = null)
    {
        string root = await RequireServerAsync();
        string url = pathOrUrl.StartsWith("http", StringComparison.Ordinal) ? pathOrUrl : root + pathOrUrl;

        using HttpRequestMessage request = new(HttpMethod.Get, url);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Plain.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> JsonOkAsync(string path, string? token = null)
    {
        (HttpStatusCode status, _, string body) = await GetAsync(path, token);
        Assert.True(status == HttpStatusCode.OK, $"{path} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string? Link(JsonElement document, string rel, string? type = null) =>
        document.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == rel
                && (type is null || (l.TryGetProperty("type", out JsonElement t) && t.GetString() == type)))
            .Select(l => l.GetProperty("href").GetString())
            .FirstOrDefault();

    private static string[] Ids(JsonElement page) =>
        [.. page.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("id").GetString()!)];

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string? token, HttpMethod method, string path, HttpContent? content = null)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(method, root + path) { Content = content };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Plain.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> MemberTokenAsync(string admin, string name)
    {
        (HttpStatusCode made, string created) = await SendAsync(admin, HttpMethod.Post, "/admin/members",
            Json(new { name, role = "user", userType = "creator" }));
        Assert.True(made is HttpStatusCode.OK or HttpStatusCode.Created, $"Making '{name}' answered {(int)made}: {created}");

        string issued = JsonDocument.Parse(created).RootElement.GetProperty("password").GetString()!;
        string first = await LoginAsync(name, issued);

        (HttpStatusCode changed, string said) = await SendAsync(first, HttpMethod.Post, "/rest/auth/password",
            Json(new { currentPassword = issued, newPassword = issued + "X1" }));
        Assert.True(changed == HttpStatusCode.OK, $"'{name}' could not replace the issued password: {(int)changed} {said}");

        return await LoginAsync(name, issued + "X1");
    }

    private async Task<string> LoginAsync(string name, string password)
    {
        (HttpStatusCode status, string body) = await SendAsync(null, HttpMethod.Post, "/rest/auth/login", Json(new { name, password }));
        Assert.True(status == HttpStatusCode.OK, $"Signing in as '{name}' answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task No_class_is_claimed_without_a_case_that_proves_it()
    {
        JsonElement conformance = await JsonOkAsync(Root + "/conformance");
        string[] claimed = [.. conformance.GetProperty("conformsTo").EnumerateArray().Select(c => c.GetString()!)];

        Assert.Empty(claimed.Except(Proven, StringComparer.Ordinal));
        Assert.Empty(Proven.Except(claimed, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_landing_page_conformance_api_and_catalogue_link_each_other()
    {
        JsonElement landing = await JsonOkAsync(Root);

        // Features Part 1 /req/core/root-success, and /req/autodiscovery/links: the catalogue's access point is its items.
        Assert.NotNull(Link(landing, "conformance"));
        Assert.NotNull(Link(landing, "data"));
        Assert.EndsWith(Items, Link(landing, "http://www.opengis.net/def/rel/ogc/1.0/ogc-catalog"), StringComparison.Ordinal);

        // /req/oas30: the service-desc is OpenAPI 3.0, and declares the items operation with Table 12's parameters.
        (HttpStatusCode status, string? type, string api) = await GetAsync(Link(landing, "service-desc")!);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/vnd.oai.openapi+json", type);
        JsonElement definition = JsonDocument.Parse(api).RootElement;
        Assert.StartsWith("3.0", definition.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.True(definition.GetProperty("paths").TryGetProperty("/collections/{catalogId}/items", out _));

        // /req/records-api/catalogs-response and catalog-response: one collection whose itemType is record, served as
        // application/ogc-catalog+json (/req/json/collection-response), with an items link (/req/record-collection/links-records).
        JsonElement collections = await JsonOkAsync(Link(landing, "data")!);
        JsonElement catalog = Assert.Single(collections.GetProperty("collections").EnumerateArray());
        Assert.Equal("record", catalog.GetProperty("itemType").GetString());
        Assert.Equal("Collection", catalog.GetProperty("type").GetString());

        (HttpStatusCode own, string? catalogType, string catalogBody) = await GetAsync(Root + "/collections/catalog");
        Assert.Equal(HttpStatusCode.OK, own);
        Assert.Equal("application/ogc-catalog+json", catalogType);
        JsonElement described = JsonDocument.Parse(catalogBody).RootElement;
        Assert.NotNull(Link(described, "self"));
        Assert.NotNull(Link(described, "alternate", "text/html"));
        Assert.EndsWith(Items, Link(described, "items", "application/geo+json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_resource_has_an_html_page_with_its_links()
    {
        JsonElement page = await JsonOkAsync(Items + "?limit=1");

        foreach (string path in new[] { Root, Root + "/conformance", Root + "/api", Root + "/collections", Root + "/collections/catalog", Items })
        {
            (HttpStatusCode status, string? type, string html) = await GetAsync(path + "?f=html");
            Assert.True(status == HttpStatusCode.OK, $"{path}?f=html answered {(int)status}");
            Assert.Equal("text/html", type);
            Assert.Contains("<a href=", html, StringComparison.Ordinal);
        }

        if (Ids(page) is [{ } id, ..])
        {
            (HttpStatusCode status, _, string html) = await GetAsync($"{Items}/{id}?f=html");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("/rest/services/", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_service_is_a_record_only_for_those_it_is_shared_with()
    {
        string root = await RequireServerAsync();
        string? admin = await TokenAsync(root);
        Assert.False(admin is null, "No administrator credential; set the suite's user and password.");

        string tag = Guid.NewGuid().ToString("N")[..8];
        string service = $"zz_rec_{tag}";
        string layer = $"zz_rec_{tag}_l";
        string member = $"zz_recm_{tag}";

        (int published, string why) = await PublishOneAsync(service, layer, sharing: "private");
        Assert.True(published is 200 or 201, $"Publishing answered {published}: {why}");

        try
        {
            string memberToken = await MemberTokenAsync(admin!, member);
            string search = $"{Items}?q={service}&type=Feature%20Service";

            // The administrator who owns it sees it, and so does the portal: the two listings are one (ADR-177).
            JsonElement owned = await JsonOkAsync(search, admin);
            string id = Assert.Single(Ids(owned));
            JsonElement portal = await JsonOkAsync($"/sharing/rest/search?q={service}&f=json", admin);
            Assert.Contains(portal.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetString() == id);

            // Private: absent for a stranger and for a member — from the search and by id, one 404 for both (ADR-018).
            Assert.Empty(Ids(await JsonOkAsync(search)));
            Assert.Empty(Ids(await JsonOkAsync($"{Items}?ids={id}", memberToken)));
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"{Items}/{id}")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"{Items}/{id}", memberToken)).Status);

            // Organisation: the member sees it, the stranger still does not.
            (HttpStatusCode org, string orgBody) = await SendAsync(admin, HttpMethod.Put,
                $"/admin/services/{service}/sharing?folder=", Json(new { sharing = "organization" }));
            Assert.True(org == HttpStatusCode.OK, orgBody);
            Assert.Equal([id], Ids(await JsonOkAsync($"{Items}?ids={id}", memberToken)));
            Assert.Empty(Ids(await JsonOkAsync($"{Items}?ids={id}")));

            // Public: everybody, and every address the anonymous record links answers the anonymous caller.
            (HttpStatusCode pub, string pubBody) = await SendAsync(admin, HttpMethod.Put,
                $"/admin/services/{service}/sharing?folder=", Json(new { sharing = "public" }));
            Assert.True(pub == HttpStatusCode.OK, pubBody);

            JsonElement record = await JsonOkAsync($"{Items}/{id}");
            Assert.Equal(id, record.GetProperty("id").GetString());

            string[] describes = [.. record.GetProperty("links").EnumerateArray()
                .Where(l => l.GetProperty("rel").GetString() == "describes")
                .Select(l => l.GetProperty("href").GetString()!)];
            Assert.Contains(describes, h => h.EndsWith($"/rest/services/{service}/FeatureServer?f=json", StringComparison.Ordinal));
            Assert.Contains(describes, h => h.Contains($"/rest/services/{service}/FeatureServer/WFSServer", StringComparison.Ordinal));
            Assert.Contains(describes, h => h.EndsWith($"/ogc/features/v1/collections/{layer}", StringComparison.Ordinal));

            foreach (string href in describes)
            {
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(href)).Status);
            }

            // ADR-166: turning WFS off takes the WFS address off the record, because WFS would no longer list it.
            (HttpStatusCode off, string offBody) = await SendAsync(admin, HttpMethod.Put,
                $"/admin/services/{service}/ogc?folder=", Json(new { off = WfsOnly }));
            Assert.True(off == HttpStatusCode.OK, offBody);
            JsonElement without = await JsonOkAsync($"{Items}/{id}");
            Assert.DoesNotContain(without.GetProperty("links").EnumerateArray(),
                l => l.GetProperty("href").GetString()!.Contains("/WFSServer", StringComparison.Ordinal));
        }
        finally
        {
            await UnpublishAsync(service, layer);
            await SendAsync(admin, HttpMethod.Delete, $"/admin/members/{member}");
        }
    }

    [Fact]
    public async Task A_record_is_a_feature_with_its_links_and_they_answer()
    {
        JsonElement page = await JsonOkAsync(Items + "?limit=1000");
        Assert.True(page.GetProperty("numberReturned").GetInt32() > 0, "The catalogue lists nothing to an anonymous caller; publish something public.");

        // /req/json/record-content A: a FeatureCollection; its links name the profile and the catalogue.
        Assert.Equal("FeatureCollection", page.GetProperty("type").GetString());
        Assert.Equal("http://www.opengis.net/def/profile/OGC/0/ogc-catalog", Link(page, "profile"));
        Assert.NotNull(Link(page, "collection"));

        HashSet<string> checkedLinks = new(StringComparer.Ordinal);

        foreach (JsonElement record in page.GetProperty("features").EnumerateArray())
        {
            // /req/records-api/record-response: id, type Feature, geometry present (null or a polygon), properties.
            Assert.Equal("Feature", record.GetProperty("type").GetString());
            Assert.True(record.TryGetProperty("geometry", out JsonElement geometry));
            Assert.True(geometry.ValueKind is JsonValueKind.Null || geometry.GetProperty("type").GetString() == "Polygon");
            Assert.False(string.IsNullOrEmpty(record.GetProperty("properties").GetProperty("type").GetString()));

            // /req/record-core/links: exactly one collection link; self and alternate (record-response B).
            Assert.Single(record.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "collection");
            Assert.NotNull(Link(record, "self"));
            Assert.NotNull(Link(record, "alternate", "text/html"));

            foreach (JsonElement link in record.GetProperty("links").EnumerateArray())
            {
                string href = link.GetProperty("href").GetString()!;

                if (href.StartsWith("http://www.opengis.net/", StringComparison.Ordinal) || !checkedLinks.Add(href))
                {
                    continue;
                }

                (HttpStatusCode status, _, string body) = await GetAsync(href);
                Assert.True(status == HttpStatusCode.OK, $"{link.GetProperty("rel").GetString()} {href} answered {(int)status}: {body[..Math.Min(body.Length, 300)]}");
            }
        }

        // One record by its id is the same record (/req/records-api/record-op), as application/geo+json.
        string first = Ids(page)[0];
        (HttpStatusCode one, string? type, string single) = await GetAsync($"{Items}/{first}");
        Assert.Equal(HttpStatusCode.OK, one);
        Assert.Equal("application/geo+json", type);
        Assert.Equal(first, JsonDocument.Parse(single).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_query_parameters_select_as_table_12_says()
    {
        JsonElement all = await JsonOkAsync(Items + "?limit=1000");
        JsonElement[] records = [.. all.GetProperty("features").EnumerateArray()];
        Assert.True(records.Length >= 2, "These need at least two records visible anonymously; publish something public.");
        Assert.Equal(records.Length, all.GetProperty("numberMatched").GetInt32());

        JsonElement target = records.First(r => r.GetProperty("geometry").ValueKind == JsonValueKind.Object
            && (r.GetProperty("properties").TryGetProperty("updated", out _) || r.GetProperty("properties").TryGetProperty("created", out _)));
        string id = target.GetProperty("id").GetString()!;
        string title = target.GetProperty("properties").GetProperty("title").GetString()!;
        string type = target.GetProperty("properties").GetProperty("type").GetString()!;

        // q: case-insensitive, in the title (q-response A).
        JsonElement byText = await JsonOkAsync($"{Items}?limit=1000&q={Uri.EscapeDataString(title.ToUpperInvariant())}");
        Assert.Contains(id, Ids(byText));
        Assert.All(byText.GetProperty("features").EnumerateArray(), r =>
            Assert.True(
                r.GetProperty("properties").GetProperty("title").GetString()!.Contains(title, StringComparison.OrdinalIgnoreCase)
                || (r.GetProperty("properties").TryGetProperty("description", out JsonElement d) && d.GetString()!.Contains(title, StringComparison.OrdinalIgnoreCase))
                || r.GetProperty("properties").GetProperty("keywords").EnumerateArray().Any(k => k.GetString()!.Contains(title, StringComparison.OrdinalIgnoreCase))));
        Assert.Empty(Ids(await JsonOkAsync($"{Items}?q=zz-no-such-words-{Guid.NewGuid():N}")));

        // type: equality (type-response A).
        JsonElement byType = await JsonOkAsync($"{Items}?limit=1000&type={Uri.EscapeDataString(type)}");
        Assert.Contains(id, Ids(byType));
        Assert.All(byType.GetProperty("features").EnumerateArray(), r => Assert.Equal(type, r.GetProperty("properties").GetProperty("type").GetString()));

        // ids: exactly those (ids-response A); externalIds: none carries one.
        string other = records.First(r => r.GetProperty("id").GetString() != id).GetProperty("id").GetString()!;
        Assert.Equal(new[] { id, other }.Order(), Ids(await JsonOkAsync($"{Items}?ids={id},{other}")).Order());
        Assert.Empty(Ids(await JsonOkAsync($"{Items}?externalIds=x")));

        // bbox: a box around the record's extent finds it; one in the Gulf of Guinea does not.
        JsonElement box = target.GetProperty("properties").GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0];
        string around = string.Join(',', Enumerable.Range(0, 4).Select(i => box[i].GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Contains(id, Ids(await JsonOkAsync($"{Items}?limit=1000&bbox={around}")));
        Assert.DoesNotContain(id, Ids(await JsonOkAsync($"{Items}?limit=1000&bbox=0,0,0.5,0.5")));

        // datetime: nothing changed in the future; everything that has a time is in an open interval.
        Assert.Empty(Ids(await JsonOkAsync($"{Items}?datetime=2999-01-01T00:00:00Z/..")));
        Assert.Contains(id, Ids(await JsonOkAsync($"{Items}?limit=1000&datetime=1970-01-01T00:00:00Z/..")));

        // limit and next: following `next` with limit=1 walks every record exactly once, in a stable order.
        List<string> walked = [];
        string? next = $"{Items}?limit=1";

        while (next is not null)
        {
            JsonElement page = await JsonOkAsync(next);
            Assert.True(page.GetProperty("numberReturned").GetInt32() <= 1);
            walked.AddRange(Ids(page));
            next = Link(page, "next");
            Assert.True(walked.Count <= records.Length, "next kept going past numberMatched.");
        }

        Assert.Equal(Ids(all), walked);

        JsonElement second = await JsonOkAsync($"{Items}?limit=1&offset=1");
        Assert.EndsWith("limit=1", Link(second, "prev"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusals_are_problems()
    {
        foreach ((string path, HttpStatusCode expected) in new[]
        {
            ($"{Items}/{Guid.NewGuid():N}", HttpStatusCode.NotFound),
            ($"{Root}/collections/nothing", HttpStatusCode.NotFound),
            ($"{Root}/collections/nothing/items", HttpStatusCode.NotFound),
            ($"{Items}?bbox=1,2,3", HttpStatusCode.BadRequest),
            ($"{Items}?limit=0", HttpStatusCode.BadRequest),
            ($"{Items}?datetime=yesterday", HttpStatusCode.BadRequest),
            ($"{Items}?title=roads", HttpStatusCode.BadRequest),
            ($"{Items}?f=xml", HttpStatusCode.BadRequest),
        })
        {
            (HttpStatusCode status, string? type, string body) = await GetAsync(path);
            Assert.True(status == expected, $"{path} answered {(int)status}, not {(int)expected}: {body}");
            Assert.Equal("application/problem+json", type);
            Assert.Equal((int)expected, JsonDocument.Parse(body).RootElement.GetProperty("status").GetInt32());
        }
    }
}
