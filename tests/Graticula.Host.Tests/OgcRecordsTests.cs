using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Api.OgcFeatures;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// OGC API Records (ADR-177) without a server: the query parameters of 20-004r1 Table 12, the shape of a record and
/// of a page, and the paging links.
/// </summary>
/// <remarks>
/// <b>The end-to-end half is <c>OgcRecordsConformanceTests</c></b>, which asks a running server who sees what. These
/// pin the rules a request is read by, because a query parameter that is parsed wrong answers 200 with the wrong records
/// and nothing downstream can tell.
/// </remarks>
public sealed class OgcRecordsTests
{
    private const string Root = "https://example.test/ogc/records/v1";

    private static RecordQuery Parse(params (string Name, string Value)[] parameters)
    {
        Dictionary<string, string> given = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        Assert.True(
            RecordQuery.TryParse(n => given.TryGetValue(n, out string? v) ? v : null, given.Keys, out RecordQuery? query, out OgcProblem? problem),
            problem?.Detail);
        return query!;
    }

    private static OgcProblem Refused(params (string Name, string Value)[] parameters)
    {
        Dictionary<string, string> given = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        Assert.False(RecordQuery.TryParse(n => given.TryGetValue(n, out string? v) ? v : null, given.Keys, out _, out OgcProblem? problem));
        return problem!;
    }

    private static CatalogRecord Record(
        string id = "a1",
        string type = "Feature Service",
        string title = "Istanbul roads",
        string? description = "Every road in the province",
        string[]? keywords = null,
        DateTimeOffset? created = null,
        DateTimeOffset? updated = null) =>
        new(id, type, title, description, keywords ?? ["transport", "hosted"], created, updated, null, []);

    [Fact]
    public void Q_terms_are_or_between_commas_and_a_phrase_within_one()
    {
        // 20-004r1 Listing 13: `ocean,climate change,desalination` — any term; a term's words together and in order.
        RecordQuery query = Parse(("q", "ocean,climate change"));

        Assert.True(query.MatchesWithoutExtent(Record(title: "Pacific OCEAN depth")));
        Assert.True(query.MatchesWithoutExtent(Record(description: "Effects of climate \t  change on coasts")));
        Assert.False(query.MatchesWithoutExtent(Record(description: "change in climate")));
        Assert.False(query.MatchesWithoutExtent(Record(title: "Roads", description: "climate")));
    }

    [Fact]
    public void Q_reads_the_title_the_description_and_the_keywords()
    {
        Assert.True(Parse(("q", "roads")).MatchesWithoutExtent(Record()));
        Assert.True(Parse(("q", "province")).MatchesWithoutExtent(Record()));
        Assert.True(Parse(("q", "TRANSPORT")).MatchesWithoutExtent(Record()));
        Assert.False(Parse(("q", "Feature")).MatchesWithoutExtent(Record()));
    }

    [Fact]
    public void A_q_term_is_text_and_never_a_pattern()
    {
        Assert.False(Parse(("q", "r.ads")).MatchesWithoutExtent(Record()));
        Assert.True(Parse(("q", "a(b")).MatchesWithoutExtent(Record(title: "x a(b y")));
    }

    [Fact]
    public void Type_and_ids_are_equality_over_a_list()
    {
        RecordQuery types = Parse(("type", "Web Map,image service"));
        Assert.True(types.MatchesWithoutExtent(Record(type: "Image Service")));
        Assert.False(types.MatchesWithoutExtent(Record(type: "Feature Service")));

        RecordQuery ids = Parse(("ids", "b2, a1"));
        Assert.True(ids.MatchesWithoutExtent(Record(id: "a1")));
        Assert.False(ids.MatchesWithoutExtent(Record(id: "a")));
    }

    [Fact]
    public void External_ids_match_nothing_because_no_record_carries_one() =>
        Assert.False(Parse(("externalIds", "doi:10.1/x")).MatchesWithoutExtent(Record()));

    [Fact]
    public void Datetime_is_tested_against_the_last_change_and_then_the_creation()
    {
        DateTimeOffset may = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        RecordQuery query = Parse(("datetime", "2026-05-01T00:00:00Z/2026-05-31T00:00:00Z"));

        Assert.True(query.MatchesWithoutExtent(Record(created: may.AddYears(-1), updated: may)));
        Assert.False(query.MatchesWithoutExtent(Record(created: may, updated: may.AddMonths(2))));
        Assert.True(query.MatchesWithoutExtent(Record(created: may)));
        Assert.False(query.MatchesWithoutExtent(Record()));
        Assert.True(Parse(("datetime", "2026-05-01T00:00:00Z/..")).MatchesWithoutExtent(Record(updated: may)));
    }

    [Fact]
    public void Bbox_needs_an_extent_and_crosses_the_antimeridian()
    {
        RecordQuery query = Parse(("bbox", "28,40,30,42"));
        Assert.True(query.NeedsExtent);
        Assert.True(query.MatchesExtent(new Envelope(29, 41, 29.5, 41.5)));
        Assert.False(query.MatchesExtent(new Envelope(32, 39, 33, 40)));
        Assert.False(query.MatchesExtent(null));

        RecordQuery pacific = Parse(("bbox", "170,-20,-170,0"));
        Assert.True(pacific.MatchesExtent(new Envelope(-175, -10, -172, -5)));
        Assert.True(pacific.MatchesExtent(new Envelope(172, -10, 175, -5)));
        Assert.False(pacific.MatchesExtent(new Envelope(0, -10, 5, -5)));
        Assert.False(Parse().NeedsExtent);
    }

    [Fact]
    public void Limit_is_clamped_and_its_default_is_ten()
    {
        Assert.Equal(10, Parse().Limit);
        Assert.Equal(1000, Parse(("limit", "50000")).Limit);
        Assert.Equal(400, Refused(("limit", "0")).Status);
        Assert.Equal(400, Refused(("offset", "-1")).Status);
    }

    [Fact]
    public void An_unknown_parameter_or_a_bad_value_is_refused_and_named()
    {
        Assert.Contains("`title`", Refused(("title", "roads")).Detail, StringComparison.Ordinal);
        Assert.Contains("`bbox-crs`", Refused(("bbox-crs", OgcNames.Crs84)).Detail, StringComparison.Ordinal);
        Assert.Equal(400, Refused(("bbox", "1,2,3")).Status);
        Assert.Equal(400, Refused(("datetime", "yesterday")).Status);

        // The ArcGIS credential is read before this and is not a filter.
        Assert.Equal(10, Parse(("token", "abc")).Limit);
    }

    [Fact]
    public void Paging_links_keep_the_query_and_move_the_offset()
    {
        RecordQuery query = Parse(("q", "roads"), ("limit", "2"), ("offset", "2"));
        List<KeyValuePair<string, string>> parameters = [new("q", "roads"), new("limit", "2"), new("offset", "2"), new("f", "json")];

        using JsonDocument page = JsonDocument.Parse(
            RecordDocuments.Items(Root, [Record(id: "c"), Record(id: "d")], 5, query, parameters, DateTimeOffset.UnixEpoch));

        Dictionary<string, string> links = page.RootElement.GetProperty("links").EnumerateArray()
            .GroupBy(l => l.GetProperty("rel").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("href").GetString()!);

        Assert.Equal($"{Root}/collections/catalog/items?q=roads&limit=2&offset=4", links["next"]);
        Assert.Equal($"{Root}/collections/catalog/items?q=roads&limit=2", links["prev"]);
        Assert.Equal($"{Root}/collections/catalog/items?q=roads&limit=2&offset=2", links["self"]);
        Assert.Equal($"{Root}/collections/catalog/items?q=roads&limit=2&offset=2&f=html", links["alternate"]);
        Assert.Equal(5, page.RootElement.GetProperty("numberMatched").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("numberReturned").GetInt32());
        Assert.Equal("FeatureCollection", page.RootElement.GetProperty("type").GetString());
        Assert.Contains(page.RootElement.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "profile" && l.GetProperty("href").GetString() == RecordNames.Profile);
    }

    [Fact]
    public void The_last_page_has_no_next_and_the_first_no_prev()
    {
        RecordQuery query = Parse(("limit", "2"));

        using JsonDocument page = JsonDocument.Parse(
            RecordDocuments.Items(Root, [Record(id: "a"), Record(id: "b")], 2, query, [new("limit", "2")], DateTimeOffset.UnixEpoch));

        string[] rels = [.. page.RootElement.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!)];
        Assert.DoesNotContain("next", rels);
        Assert.DoesNotContain("prev", rels);
    }

    [Fact]
    public void A_record_is_a_geojson_feature_with_the_core_properties_and_its_links()
    {
        CatalogRecord record = Record(
            created: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3)),
            updated: new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero)) with
        {
            Extent = new Envelope(28, 40, 30, 42),
            Links = [new RecordLink("https://example.test/rest/services/roads/FeatureServer?f=json", "describes", "application/json", "ArcGIS FeatureServer")],
        };

        using JsonDocument document = JsonDocument.Parse(RecordDocuments.Record(Root, record));
        JsonElement root = document.RootElement;

        Assert.Equal("a1", root.GetProperty("id").GetString());
        Assert.Equal("Feature", root.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("time").ValueKind);
        Assert.Equal("Polygon", root.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(5, root.GetProperty("geometry").GetProperty("coordinates")[0].GetArrayLength());

        JsonElement properties = root.GetProperty("properties");
        Assert.Equal("Feature Service", properties.GetProperty("type").GetString());
        Assert.Equal("Istanbul roads", properties.GetProperty("title").GetString());
        Assert.Equal(["transport", "hosted"], properties.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));

        // RFC 3339 in UTC, as /req/record-core/time-zone asks of timestamps.
        Assert.Equal("2026-01-02T00:04:05Z", properties.GetProperty("created").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", properties.GetProperty("updated").GetString());
        Assert.Equal("ArcGIS FeatureServer", properties.GetProperty("formats")[0].GetProperty("name").GetString());
        Assert.Equal(28, properties.GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0][0].GetDouble());
        Assert.False(properties.TryGetProperty("contacts", out _));
        Assert.False(properties.TryGetProperty("license", out _));

        string[] rels = [.. root.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!)];
        Assert.Equal(1, rels.Count(r => r == "collection"));
        Assert.Contains("self", rels);
        Assert.Contains("alternate", rels);
        Assert.Contains("describes", rels);
    }

    [Fact]
    public void A_record_with_no_known_extent_has_a_null_geometry_and_no_extent()
    {
        using JsonDocument document = JsonDocument.Parse(RecordDocuments.Record(Root, Record()));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("geometry").ValueKind);
        Assert.False(document.RootElement.GetProperty("properties").TryGetProperty("extent", out _));
    }

    [Fact]
    public void The_catalogue_is_a_collection_of_records_linking_its_items()
    {
        using JsonDocument document = JsonDocument.Parse(RecordDocuments.Catalog(Root));
        JsonElement root = document.RootElement;

        Assert.Equal("catalog", root.GetProperty("id").GetString());
        Assert.Equal("Collection", root.GetProperty("type").GetString());
        Assert.Equal("record", root.GetProperty("itemType").GetString());
        Assert.Contains(root.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "items" && l.GetProperty("href").GetString() == $"{Root}/collections/catalog/items");
        Assert.Contains(root.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "self" && l.GetProperty("type").GetString() == RecordNames.CatalogMediaType);
    }

    [Fact]
    public void The_landing_page_says_where_the_searchable_catalogue_is()
    {
        using JsonDocument document = JsonDocument.Parse(RecordDocuments.Landing(Root));
        string[] rels = [.. document.RootElement.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()!)];

        Assert.Contains("service-desc", rels);
        Assert.Contains("conformance", rels);
        Assert.Contains("data", rels);
        Assert.Contains(document.RootElement.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == RecordNames.CatalogRelation
                && l.GetProperty("href").GetString() == $"{Root}/collections/catalog/items");
    }

    [Fact]
    public void The_api_definition_declares_every_parameter_the_items_resource_reads()
    {
        using JsonDocument document = JsonDocument.Parse(RecordDocuments.OpenApi(Root));
        JsonElement parameters = document.RootElement.GetProperty("components").GetProperty("parameters");

        foreach (string name in new[] { "bbox", "datetime", "limit", "offset", "q", "type", "ids", "externalIds" })
        {
            Assert.True(parameters.TryGetProperty(name, out _), $"`{name}` is read and not declared.");
        }

        Assert.Equal(
            RecordNames.Types,
            parameters.GetProperty("type").GetProperty("schema").GetProperty("items").GetProperty("enum").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public void A_record_says_what_its_portal_item_says()
    {
        // The item object is the one a portal search writes; the record must read it rather than re-describe the map.
        DateTimeOffset made = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WebMap map = new("0123456789abcdef0123456789abcdef", "Ferries", null, Guid.NewGuid(), "someone", SharingScope.Public, null, made, made);
        object item = new
        {
            id = map.Id,
            title = "Ferries of the Bosphorus",
            type = "Web Map",
            description = (string?)null,
            snippet = "Routes and piers",
            tags = new[] { "sea" },
            thumbnail = "thumbnail/thumbnail.png",
            created = made.ToUnixTimeMilliseconds(),
            modified = made.AddDays(1).ToUnixTimeMilliseconds(),
        };

        DefaultHttpContext context = new();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");

        CatalogRecord record = OgcRecordsEndpoints.RecordOf(
            context, new PortalEndpoints.PortalListed(map.Id, item, Map: map), new HashSet<string>());

        Assert.Equal("Ferries of the Bosphorus", record.Title);
        Assert.Equal("Routes and piers", record.Description);
        Assert.Equal("Web Map", record.Type);
        Assert.Equal(["sea"], record.Keywords);
        Assert.Equal(made.AddDays(1), record.Updated);
        Assert.Contains(record.Links, l => l.Rel == "describes" && l.Href == $"https://example.test/sharing/rest/content/items/{map.Id}/data");
        Assert.Contains(record.Links, l => l.Rel == "preview" && l.Href.EndsWith("/info/thumbnail/thumbnail.png", StringComparison.Ordinal));
    }

    [Fact]
    public void A_catalogue_request_is_filed_under_no_service() =>
        Assert.Null(RequestFacts.Service(new PathString("/ogc/records/v1/collections/catalog/items"), new QueryString("?q=roads")));
}
