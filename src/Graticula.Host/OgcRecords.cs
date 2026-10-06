using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Graticula.Api.OgcFeatures;
using Graticula.Geometries;

namespace Graticula.Host;

/// <summary>
/// The constants OGC API – Records – Part 1: Core (OGC 20-004r1, 1.0) is spelled with — ADR-177.
/// </summary>
/// <remarks>
/// <b>One place, for the reason <see cref="OgcNames"/> gives</b>: a conformance URI or a link relation with a typo in it is
/// a claim no client recognises, and it reads as <i>not implemented</i> rather than as <i>spelled wrong</i>. Every value
/// here is copied from the published standard, which is the citation; nothing is taken from another server's catalogue.
/// </remarks>
internal static class RecordNames
{
    /// <summary>Where the face lives, beside <c>/ogc/features/v1</c> and <c>/ogc/tiles/v1</c> (ADR-042 §5.1).</summary>
    public const string Base = "/ogc/records/v1";

    /// <summary>The one catalogue's id.</summary>
    /// <remarks>
    /// <b>One catalogue, because there is one listing</b> — ADR-177 §2. Every record is an item the portal lists, and a
    /// second catalogue per item type would split what a client searches in one request into five, for a <c>type</c>
    /// parameter that already does it. (INFERRED, 2026-10-06.)
    /// </remarks>
    public const string CatalogId = "catalog";

    /// <summary>A catalogue's media type — 20-004r1 <c>/req/json/collection-response</c>.</summary>
    public const string CatalogMediaType = "application/ogc-catalog+json";

    /// <summary>The profile a record response names — 20-004r1 <c>/req/json/record-content</c> D.</summary>
    public const string Profile = "http://www.opengis.net/def/profile/OGC/0/ogc-catalog";

    /// <summary>The relation that points from a page to its searchable catalogue — <c>/req/autodiscovery/links</c>.</summary>
    public const string CatalogRelation = "http://www.opengis.net/def/rel/ogc/1.0/ogc-catalog";

    private const string Conf = "http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/";

    /// <summary>The conformance classes this face claims.</summary>
    /// <remarks>
    /// <para>
    /// <b>A searchable catalogue — 20-004r1 §8.3 — and the common components it is made of.</b>
    /// <c>/req/searchable-catalog/conformance</c> B requires Features Part 1 <c>core</c> and <c>searchable-catalog</c>; C,
    /// D, E and F require <c>json</c>, <c>html</c>, <c>oas30</c> and <c>autodiscovery</c> of a server that offers them; and
    /// <c>/req/searchable-catalog/common</c> makes <c>records-api</c>, <c>record-core</c>, <c>record-collection</c> and
    /// <c>record-core-query-parameters</c> prerequisites, so they are declared too.
    /// </para>
    /// <para>
    /// <b>Not claimed:</b> <c>sorting</c>, <c>filtering</c> (CQL2 over records) and the profile parameter, which this face
    /// does not implement; <c>crawlable-catalog</c> and the local-resources classes, which describe other deployments.
    /// </para>
    /// </remarks>
    public static readonly string[] ConformsTo =
    [
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        Conf + "record-core",
        Conf + "record-collection",
        Conf + "record-core-query-parameters",
        Conf + "records-api",
        Conf + "searchable-catalog",
        Conf + "json",
        Conf + "html",
        Conf + "oas30",
        Conf + "autodiscovery",
    ];

    /// <summary>The values a record's <c>type</c> takes: the portal's item types, which are this server's vocabulary.</summary>
    /// <remarks>
    /// <b>The portal's words rather than Annex C's URIs — INFERRED, ADR-177 §2.</b> Annex C is informative and names
    /// protocols (<c>…/serviceType/ogc/wms</c>), and one item here is reachable through several of them; the portal's
    /// type says what the item is, is already what ArcGIS clients filter on, and is listed in the API definition as
    /// <c>/rec/record-core-query-parameters/param-type-definition</c> recommends.
    /// </remarks>
    public static readonly string[] Types =
        ["Feature Service", "Map Service", "Vector Tile Service", "Image Service", "Web Map"];
}

/// <summary>One link on a record or a document.</summary>
/// <param name="Href">Where it goes.</param>
/// <param name="Rel">What it is.</param>
/// <param name="Type">The media type behind it, or null.</param>
/// <param name="Title">Something for a person to read, or null.</param>
internal readonly record struct RecordLink(string Href, string Rel, string? Type = null, string? Title = null);

/// <summary>
/// One catalogue record: one portal item, as OGC API Records describes it — ADR-177.
/// </summary>
/// <remarks>
/// <b>Every field is read from the portal item or the thing it was made from, and nothing is invented.</b> No
/// <c>contacts</c>, because the only person the catalogue knows is the owner and the portal withholds owners from
/// strangers (Q-127); no <c>license</c>, because nothing records one; no <c>themes</c>, because tags are free words and
/// a theme names a controlled vocabulary this server does not have.
/// </remarks>
/// <param name="Id">The portal item's id.</param>
/// <param name="Type">The portal item's type — one of <see cref="RecordNames.Types"/>.</param>
/// <param name="Title">Its title.</param>
/// <param name="Description">Its description, or its summary when it has none, or null.</param>
/// <param name="Keywords">Its tags.</param>
/// <param name="Created">When it was created, or null.</param>
/// <param name="Updated">When it last changed, or null.</param>
/// <param name="Rights">Its owner's statement of who may use its OGC faces and how (ADR-167), or null.</param>
/// <param name="Links">Where the item is: its addresses, its portal item and its picture.</param>
internal sealed record CatalogRecord(
    string Id,
    string Type,
    string Title,
    string? Description,
    IReadOnlyList<string> Keywords,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    string? Rights,
    IReadOnlyList<RecordLink> Links)
{
    /// <summary>Its extent in CRS84, or null while it has not been read or when it is not known.</summary>
    public Envelope? Extent { get; init; }

    /// <summary>
    /// The instant <c>datetime</c> is tested against: when the record last changed, or when it was made.
    /// </summary>
    /// <remarks>
    /// <b>The record's own time, because the resource's is not known — INFERRED, ADR-177 §2.</b> 20-004r1 Table 12 asks
    /// whether <i>the temporal extent of the record</i> intersects <c>datetime</c>, and Features Part 1 leaves which
    /// temporal property to the server. A service's data may have no time at all; its item always has a creation and a
    /// change, which is what a person searching a catalogue for <i>what changed this week</i> means. <c>time</c> stays
    /// null on the record (<c>/rec/record-core/time</c> B), because it is about the resource.
    /// </remarks>
    public DateTimeOffset? Moment => Updated ?? Created;
}

/// <summary>
/// The parameters of <c>/collections/{catalogId}/items</c> — 20-004r1 Table 12.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every parameter of Table 12 that names something a record has</b>: <c>bbox</c>, <c>datetime</c>, <c>limit</c> with
/// <c>offset</c>, <c>q</c>, <c>type</c>, <c>ids</c> and <c>externalIds</c>, joined by AND
/// (<c>/req/records-api/query-params</c>). <c>bbox</c>, <c>datetime</c>, <c>limit</c> and <c>offset</c> are read by the
/// features face's own readers (<see cref="OgcRequest.TryBbox"/> and its neighbours), because Records inherits them
/// from Features Part 1 and a second reading would be a second rule.
/// </para>
/// <para>
/// <b><c>externalIds</c> is read and matches nothing</b>, which is the specification's answer for a catalogue whose
/// records carry no external identifier (<c>/req/record-core-query-parameters/externalIds-response</c> A).
/// </para>
/// <para>
/// <b>An unknown parameter is refused rather than ignored</b>, as on the features face: Table 12's
/// <c>prop=value</c> names a queryable, and this face advertises none.
/// </para>
/// </remarks>
internal sealed class RecordQuery
{
    /// <summary>Every parameter the items resource reads. <c>token</c> is the ArcGIS credential, read before this.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "f", "bbox", "datetime", "limit", "offset", "q", "type", "ids", "externalIds", "token",
    };

    private RecordQuery()
    {
    }

    /// <summary>How many records to return.</summary>
    public int Limit { get; private init; }

    /// <summary>How many to skip.</summary>
    public int Offset { get; private init; }

    /// <summary>The box to filter by, in CRS84, or its western half across the antimeridian; null for none.</summary>
    public Envelope? Bbox { get; private init; }

    /// <summary>The eastern half of a box that crosses the antimeridian, or null.</summary>
    public Envelope? BboxEast { get; private init; }

    /// <summary>The first instant included, or null.</summary>
    public DateTimeOffset? From { get; private init; }

    /// <summary>The first instant excluded, or null.</summary>
    public DateTimeOffset? Until { get; private init; }

    /// <summary>Whether a time filter was asked for.</summary>
    public bool HasDateTime => From is not null || Until is not null;

    /// <summary>The <c>q</c> terms, any one of which a record must contain; empty for no text filter.</summary>
    public IReadOnlyList<Regex> Terms { get; private init; } = [];

    /// <summary>The types asked for, or null for any.</summary>
    public IReadOnlySet<string>? Types { get; private init; }

    /// <summary>The record ids asked for, or null for any.</summary>
    public IReadOnlySet<string>? Ids { get; private init; }

    /// <summary>Whether <c>externalIds</c> was given — which no record here can satisfy.</summary>
    public bool AsksExternalIds { get; private init; }

    /// <summary>Whether answering needs every candidate's extent, which costs a read per service.</summary>
    public bool NeedsExtent => Bbox is not null;

    /// <summary>Reads an items request.</summary>
    /// <param name="parameter">Reads one parameter by name, as written.</param>
    /// <param name="names">Every parameter the request carried.</param>
    /// <param name="query">The query.</param>
    /// <param name="problem">Why not, when it did not parse.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        Func<string, string?> parameter,
        IEnumerable<string> names,
        out RecordQuery? query,
        out OgcProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(names);

        query = null;

        foreach (string name in names)
        {
            if (!Known.Contains(name))
            {
                problem = OgcProblem.BadRequest(
                    $"`{name}` is not a parameter of this resource. It reads bbox, datetime, limit, offset, q, type, ids "
                    + "and externalIds; one it does not read is refused rather than ignored, because a client whose filter "
                    + "was silently dropped has been told the filter worked.");
                return false;
            }
        }

        if (!OgcRequest.TryLimit(parameter("limit"), OgcLimits.Default, out int limit, out problem)
            || !OgcRequest.TryOffset(parameter("offset"), out int offset, out problem)
            || !OgcRequest.TryBbox(parameter("bbox"), latitudeFirst: false, out Envelope? bbox, out Envelope? east, out problem)
            || !OgcRequest.TryDateTime(parameter("datetime"), out DateTimeOffset? from, out DateTimeOffset? until, out problem))
        {
            return false;
        }

        query = new RecordQuery
        {
            Limit = limit,
            Offset = offset,
            Bbox = bbox,
            BboxEast = east,
            From = from,
            Until = until,
            Terms = TermsOf(parameter("q")),
            Types = SetOf(parameter("type")),
            Ids = SetOf(parameter("ids")),
            AsksExternalIds = SetOf(parameter("externalIds")) is not null,
        };

        return true;
    }

    /// <summary>
    /// Reads <c>q</c>: terms separated by commas, any of which may match; words within a term must appear together, in
    /// order, separated by white space — <c>/req/record-core-query-parameters/q-definition</c> C, D and E.
    /// </summary>
    /// <param name="value">The parameter, or null.</param>
    /// <returns>One case-insensitive pattern per term.</returns>
    internal static IReadOnlyList<Regex> TermsOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<Regex> terms = [];

        foreach (string term in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] words = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // A phrase is its words in order with any run of white space between them — Listing 14's `\s+`. The words
            // are escaped, so a term is text to find and never a pattern a caller writes.
            terms.Add(new Regex(
                string.Join(@"\s+", words.Select(Regex.Escape)),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250)));
        }

        return terms;
    }

    /// <summary>A comma-separated list as a set, or null when the parameter is absent or empty.</summary>
    private static HashSet<string>? SetOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        HashSet<string> set = new(
            value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        return set.Count == 0 ? null : set;
    }

    /// <summary>Whether a record satisfies every predicate except <c>bbox</c>, which needs its extent.</summary>
    /// <param name="record">The record.</param>
    /// <returns>Whether it is in the result set as far as these can say.</returns>
    public bool MatchesWithoutExtent(CatalogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (AsksExternalIds)
        {
            return false;
        }

        if (Ids is not null && !Ids.Contains(record.Id))
        {
            return false;
        }

        // <b>Equality, without regard to case</b>: `type=feature service` is the same request a person meant.
        if (Types is not null && !Types.Contains(record.Type))
        {
            return false;
        }

        if (HasDateTime)
        {
            if (record.Moment is not { } at || (From is { } from && at < from) || (Until is { } until && at >= until))
            {
                return false;
            }
        }

        return Terms.Count == 0 || Terms.Any(term => ContainsTerm(record, term));
    }

    /// <summary>Whether a record's extent meets the box; a record whose extent is unknown does not.</summary>
    /// <param name="extent">The record's extent in CRS84, or null.</param>
    /// <returns>Whether it is in the result set.</returns>
    public bool MatchesExtent(Envelope? extent)
    {
        if (Bbox is not { } box)
        {
            return true;
        }

        // Features Part 1 /req/core/fc-bbox-response: only what has a geometry that intersects. A record whose extent
        // nobody can read has no geometry, and claiming it is somewhere would be the false answer.
        return extent is { IsEmpty: false } known
            && (known.Intersects(box) || (BboxEast is { } east && known.Intersects(east)));
    }

    /// <summary>
    /// Whether a term appears in the title, the description or a keyword — <c>/rec/record-core-query-parameters/param-q</c>.
    /// </summary>
    private static bool ContainsTerm(CatalogRecord record, Regex term) =>
        term.IsMatch(record.Title)
        || (record.Description is { } description && term.IsMatch(description))
        || record.Keywords.Any(term.IsMatch);
}

/// <summary>
/// The documents of OGC API Records: landing page, conformance, the catalogue, records and the API definition.
/// </summary>
/// <remarks>
/// <b>Absolute links, built from the request, as on the features face</b> (<see cref="OgcDocuments"/>): a record is
/// stored and resolved later by whoever harvested it, and a relative link means nothing once it has left the page.
/// </remarks>
internal static class RecordDocuments
{
    /// <summary>The landing page.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>The JSON.</returns>
    public static string Landing(string root) => Write(json =>
    {
        json.WriteString("title", "Graticula catalogue");
        json.WriteString(
            "description",
            "A searchable catalogue of what this server publishes and you may see: its feature, map, vector tile and "
            + "image services and its saved web maps — the items its portal lists to you — each linked to its ArcGIS "
            + "REST and OGC addresses.");

        WriteLinks(json,
        [
            new RecordLink(root, "self", OgcNames.Json, "This document"),
            new RecordLink(root + "?f=html", "alternate", OgcNames.Html, "This document as HTML"),
            new RecordLink(root + "/api", "service-desc", OgcNames.OpenApi, "The API definition"),
            new RecordLink(root + "/api?f=html", "service-doc", OgcNames.Html, "The API documentation"),
            new RecordLink(root + "/conformance", "conformance", OgcNames.Json, "Conformance classes"),
            new RecordLink(root + "/collections", "data", OgcNames.Json, "Catalogues"),

            // /req/autodiscovery/links: the searchable catalogue's access point is its items.
            new RecordLink(ItemsOf(root), RecordNames.CatalogRelation, OgcNames.GeoJson, "The searchable catalogue"),
        ]);
    });

    /// <summary>The conformance declaration.</summary>
    /// <returns>The JSON.</returns>
    public static string Conformance() => Write(json =>
    {
        json.WriteStartArray("conformsTo");

        foreach (string uri in RecordNames.ConformsTo)
        {
            json.WriteStringValue(uri);
        }

        json.WriteEndArray();
    });

    /// <summary>The list of catalogues, which has one.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>The JSON.</returns>
    public static string Collections(string root) => Write(json =>
    {
        WriteLinks(json,
        [
            new RecordLink(root + "/collections", "self", OgcNames.Json, "This document"),
            new RecordLink(root + "/collections?f=html", "alternate", OgcNames.Html, "This document as HTML"),
        ]);

        json.WriteStartArray("collections");
        json.WriteStartObject();
        WriteCatalog(json, root);
        json.WriteEndObject();
        json.WriteEndArray();
    });

    /// <summary>The catalogue's own document.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>The JSON.</returns>
    public static string Catalog(string root) => Write(json => WriteCatalog(json, root));

    /// <summary>The address of the catalogue's records.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>The URL.</returns>
    public static string ItemsOf(string root) => $"{root}/collections/{RecordNames.CatalogId}/items";

    /// <summary>The address of one record.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <param name="id">The record's id.</param>
    /// <returns>The URL.</returns>
    public static string RecordOf(string root, string id) => $"{ItemsOf(root)}/{Uri.EscapeDataString(id)}";

    /// <summary>
    /// A catalogue: <c>/req/records-api/catalog-response</c> over Features' collection, <c>itemType</c> fixed to
    /// <c>record</c>.
    /// </summary>
    private static void WriteCatalog(Utf8JsonWriter json, string root)
    {
        string self = $"{root}/collections/{RecordNames.CatalogId}";

        json.WriteString("id", RecordNames.CatalogId);
        json.WriteString("type", "Collection");
        json.WriteString("itemType", "record");
        json.WriteString("title", "Published items");
        json.WriteString(
            "description",
            "Every item this server's portal lists to you — feature, map, vector tile and image services and saved web "
            + "maps — one record each.");

        json.WriteStartArray("crs");
        json.WriteStringValue(OgcNames.Crs84);
        json.WriteEndArray();

        // <b>No `extent`.</b> It is optional (Table 11), and the honest one is the union of every item's extent, which
        // costs a read of every layer's source to say what a client can learn by asking with `bbox`.
        WriteLinks(json,
        [
            new RecordLink(self, "self", RecordNames.CatalogMediaType, "This catalogue"),
            new RecordLink(self + "?f=html", "alternate", OgcNames.Html, "This catalogue as HTML"),
            new RecordLink(ItemsOf(root), "items", OgcNames.GeoJson, "Its records"),
            new RecordLink(ItemsOf(root) + "?f=html", "items", OgcNames.Html, "Its records as HTML"),
            new RecordLink(root, "root", OgcNames.Json, "The landing page"),
        ]);
    }

    /// <summary>A page of records, as a GeoJSON feature collection — <c>/req/json/record-content</c> A.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <param name="page">The records on this page, extents read.</param>
    /// <param name="matched">How many records the query matched in all.</param>
    /// <param name="query">The query, for the paging links.</param>
    /// <param name="parameters">The request's parameters, kept on the paging links.</param>
    /// <param name="now">The response's time stamp.</param>
    /// <returns>The JSON.</returns>
    public static string Items(
        string root,
        IReadOnlyList<CatalogRecord> page,
        int matched,
        RecordQuery query,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(parameters);

        string items = ItemsOf(root);

        return Write(json =>
        {
            json.WriteString("type", "FeatureCollection");
            json.WriteString("timeStamp", Moment(now));
            json.WriteNumber("numberMatched", matched);
            json.WriteNumber("numberReturned", page.Count);

            json.WriteStartArray("features");

            foreach (CatalogRecord record in page)
            {
                json.WriteStartObject();
                WriteRecord(json, root, record);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            List<RecordLink> links =
            [
                new RecordLink(items + Rebuild(parameters, query.Offset), "self", OgcNames.GeoJson, "This page"),
                new RecordLink(items + Rebuild(parameters, query.Offset, html: true), "alternate", OgcNames.Html, "This page as HTML"),
                new RecordLink($"{root}/collections/{RecordNames.CatalogId}", "collection", RecordNames.CatalogMediaType, "The catalogue"),
                new RecordLink(RecordNames.Profile, "profile", null, "OGC catalogue profile"),
            ];

            // <b>`next` exactly when there is more, and `prev` when this is not the first page</b> — Features Part 1
            // /req/core/fc-next-1 and its recommendation, read for records.
            if (query.Offset + page.Count < matched)
            {
                links.Add(new RecordLink(items + Rebuild(parameters, query.Offset + query.Limit), "next", OgcNames.GeoJson, "Next page"));
            }

            if (query.Offset > 0)
            {
                links.Add(new RecordLink(
                    items + Rebuild(parameters, Math.Max(0, query.Offset - query.Limit)), "prev", OgcNames.GeoJson, "Previous page"));
            }

            WriteLinks(json, links);
        });
    }

    /// <summary>One record, as a GeoJSON feature — <c>/req/records-api/record-response</c>.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <param name="record">The record, its extent read.</param>
    /// <returns>The JSON.</returns>
    public static string Record(string root, CatalogRecord record) => Write(json => WriteRecord(json, root, record));

    private static void WriteRecord(Utf8JsonWriter json, string root, CatalogRecord record)
    {
        json.WriteString("id", record.Id);
        json.WriteString("type", "Feature");

        // /rec/record-core/time B: included, and null, because the resource's own time is not known.
        json.WriteNull("time");

        json.WritePropertyName("geometry");

        if (record.Extent is { IsEmpty: false } box)
        {
            WritePolygon(json, box);
        }
        else
        {
            // GeoJSON's own answer for a feature with no place (RFC 7946 §3.2), which the record schema allows.
            json.WriteNullValue();
        }

        json.WriteStartArray("conformsTo");
        json.WriteStringValue("http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-core");
        json.WriteEndArray();

        json.WriteStartObject("properties");
        json.WriteString("type", record.Type);
        json.WriteString("title", record.Title);

        if (record.Description is { Length: > 0 } description)
        {
            json.WriteString("description", description);
        }

        json.WriteStartArray("keywords");

        foreach (string keyword in record.Keywords)
        {
            json.WriteStringValue(keyword);
        }

        json.WriteEndArray();

        if (record.Created is { } created)
        {
            json.WriteString("created", Moment(created));
        }

        if (record.Updated is { } updated)
        {
            json.WriteString("updated", Moment(updated));
        }

        // `formats`: one per address the resource is reached at, named for the protocol, so a harvester that reads
        // formats rather than links still learns how to bind to it.
        json.WriteStartArray("formats");

        foreach (RecordLink link in record.Links.Where(l => l.Rel == "describes"))
        {
            json.WriteStartObject();
            json.WriteString("name", link.Title);

            if (link.Type is { Length: > 0 } type)
            {
                json.WriteString("mediaType", type);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();

        if (record.Rights is { Length: > 0 } rights)
        {
            json.WriteString("rights", rights);
        }

        // <b>`extent` beside `geometry`</b> — /req/json/record-response C says the bounding extent is carried, and
        // QGIS's MetaSearch (3.28) reads a record's box from `properties.extent.spatial.bbox`, not from the geometry.
        if (record.Extent is { IsEmpty: false } extent)
        {
            json.WriteStartObject("extent");
            json.WriteStartObject("spatial");
            json.WriteStartArray("bbox");
            json.WriteStartArray();
            json.WriteNumberValue(extent.MinX);
            json.WriteNumberValue(extent.MinY);
            json.WriteNumberValue(extent.MaxX);
            json.WriteNumberValue(extent.MaxY);
            json.WriteEndArray();
            json.WriteEndArray();
            json.WriteString("crs", OgcNames.Crs84);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();

        string self = RecordOf(root, record.Id);

        WriteLinks(json,
        [
            new RecordLink(self, "self", OgcNames.GeoJson, "This record"),
            new RecordLink(self + "?f=html", "alternate", OgcNames.Html, "This record as HTML"),
            new RecordLink($"{root}/collections/{RecordNames.CatalogId}", "collection", RecordNames.CatalogMediaType, "The catalogue"),
            new RecordLink(RecordNames.Profile, "profile", null, "OGC catalogue profile"),
            .. record.Links,
        ]);
    }

    private static void WritePolygon(Utf8JsonWriter json, Envelope box)
    {
        json.WriteStartObject();
        json.WriteString("type", "Polygon");
        json.WriteStartArray("coordinates");
        json.WriteStartArray();

        // Counter-clockwise, as RFC 7946 §3.1.6 asks of an exterior ring.
        foreach ((double x, double y) in new[]
        {
            (box.MinX, box.MinY), (box.MaxX, box.MinY), (box.MaxX, box.MaxY), (box.MinX, box.MaxY), (box.MinX, box.MinY),
        })
        {
            json.WriteStartArray();
            json.WriteNumberValue(x);
            json.WriteNumberValue(y);
            json.WriteEndArray();
        }

        json.WriteEndArray();
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// The query string of a page: the request's parameters as given, except <c>offset</c> and <c>f</c>.
    /// </summary>
    /// <remarks>
    /// <b>The caller's parameters are kept, a <c>token</c> among them</b>, as the features face keeps them: a client that
    /// authenticated in the query string and followed <c>next</c> without it would be shown the anonymous catalogue on
    /// page two. This face is outside the compression allowlist, so a credential echoed in its body gives a
    /// BREACH-style probe nothing to measure (<see cref="ResponseCompressionPolicy"/>).
    /// </remarks>
    /// <param name="parameters">The request's parameters.</param>
    /// <param name="offset">The page's offset.</param>
    /// <param name="html">Whether the link is to the HTML page.</param>
    /// <returns>The query string, with its <c>?</c>, or empty.</returns>
    internal static string Rebuild(IReadOnlyList<KeyValuePair<string, string>> parameters, int offset, bool html = false)
    {
        List<string> parts = [];

        foreach (KeyValuePair<string, string> pair in parameters)
        {
            if (pair.Key is "offset" or "f")
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        if (offset > 0)
        {
            parts.Add("offset=" + offset.ToString(CultureInfo.InvariantCulture));
        }

        if (html)
        {
            parts.Add("f=html");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// The OpenAPI 3.0 definition — the <c>oas30</c> class, written by hand for the reason
    /// <see cref="OpenApiDocument"/> gives: it describes what the specification requires and what this face honours.
    /// </summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>The JSON.</returns>
    public static string OpenApi(string root) => Write(json =>
    {
        json.WriteString("openapi", "3.0.3");

        json.WriteStartObject("info");
        json.WriteString("title", "Graticula — OGC API Records");
        json.WriteString(
            "description",
            "A searchable catalogue of the items this server publishes, read-only. OGC API - Records - Part 1: Core "
            + "(OGC 20-004r1).");
        json.WriteString("version", "1.0.0");
        json.WriteStartObject("license");
        json.WriteString("name", "Elastic-2.0");
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteStartArray("servers");
        json.WriteStartObject();
        json.WriteString("url", root);
        json.WriteString("description", "This server");
        json.WriteEndObject();
        json.WriteEndArray();

        json.WriteStartObject("paths");
        Path(json, "/", "getLandingPage", "The landing page", [], OgcNames.Json);
        Path(json, "/conformance", "getConformanceDeclaration", "The conformance classes this face implements", [], OgcNames.Json);
        Path(json, "/api", "getApiDefinition", "This document", [], OgcNames.OpenApi);
        Path(json, "/collections", "getCollections", "The catalogues: one", [], OgcNames.Json);
        Path(json, "/collections/{catalogId}", "describeCatalog", "The catalogue", ["catalogId"], RecordNames.CatalogMediaType);
        Path(json, "/collections/{catalogId}/items", "getRecords", "Records, filtered and paged",
            ["catalogId", "bbox", "datetime", "limit", "offset", "q", "type", "ids", "externalIds"], OgcNames.GeoJson);
        Path(json, "/collections/{catalogId}/items/{recordId}", "getRecord", "One record", ["catalogId", "recordId"], OgcNames.GeoJson);
        json.WriteEndObject();

        json.WriteStartObject("components");
        json.WriteStartObject("parameters");

        Parameter(json, "catalogId", "path", $"The catalogue's id, which is `{RecordNames.CatalogId}`.", Schema.String, required: true);
        Parameter(json, "recordId", "path", "The record's id, which is its portal item's id.", Schema.String, required: true);
        Parameter(json, "bbox", "query",
            "Only records whose extent intersects this box: minx,miny,maxx,maxy in CRS84, or six numbers with the "
            + "elevations between. A record whose extent is not known is not in the answer.", Schema.Numbers);
        Parameter(json, "datetime", "query",
            "Only records whose last change (or creation, when never changed) falls in this instant or interval; `..` "
            + "opens an end.", Schema.String);
        Parameter(json, "limit", "query",
            $"How many records to return: 1 to {OgcLimits.Default.MaximumLimit}, a larger value reduced rather than "
            + $"refused; default {OgcLimits.Default.DefaultLimit}.", Schema.Limit);
        Parameter(json, "offset", "query", "How many records to skip.", Schema.Offset);
        Parameter(json, "q", "query",
            "Comma-separated terms, any of which a record must contain in its title, description or keywords, without "
            + "regard to case; the words of one term must appear together and in order.", Schema.Strings);
        Parameter(json, "type", "query", "Only records of these types.", Schema.Types);
        Parameter(json, "ids", "query", "Only records with these ids.", Schema.Strings);
        Parameter(json, "externalIds", "query",
            "Only records with one of these external identifiers. No record here carries one, so any value matches "
            + "nothing.", Schema.Strings);

        json.WriteEndObject();
        json.WriteEndObject();
    });

    /// <summary>The schemas a parameter is declared with.</summary>
    private enum Schema
    {
        String,
        Strings,
        Numbers,
        Limit,
        Offset,
        Types,
    }

    private static void Path(
        Utf8JsonWriter json, string path, string operationId, string summary, string[] parameters, string mediaType)
    {
        json.WriteStartObject(path);
        json.WriteStartObject("get");
        json.WriteString("operationId", operationId);
        json.WriteString("summary", summary);

        json.WriteStartArray("parameters");

        foreach (string parameter in parameters)
        {
            json.WriteStartObject();
            json.WriteString("$ref", "#/components/parameters/" + parameter);
            json.WriteEndObject();
        }

        // `f` is how a browser asks for the HTML page, and how a link names either representation.
        json.WriteStartObject();
        json.WriteString("name", "f");
        json.WriteString("in", "query");
        json.WriteBoolean("required", false);
        json.WriteString("description", "The representation: json, or html. Overrides Accept.");
        json.WriteStartObject("schema");
        json.WriteString("type", "string");
        json.WriteStartArray("enum");
        json.WriteStringValue("json");
        json.WriteStringValue("html");
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteEndArray();

        json.WriteStartObject("responses");

        json.WriteStartObject("200");
        json.WriteString("description", summary);
        json.WriteStartObject("content");
        json.WriteStartObject(mediaType);
        json.WriteEndObject();
        json.WriteStartObject(OgcNames.Html);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();

        foreach ((string status, string what) in new[]
        {
            ("400", "A parameter is not one this resource reads, or its value is not valid."),
            ("404", "No such catalogue or record, or none this caller may see."),
            ("503", "The catalogue cannot be read just now."),
        })
        {
            json.WriteStartObject(status);
            json.WriteString("description", what);
            json.WriteStartObject("content");
            json.WriteStartObject(OgcNames.Problem);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void Parameter(
        Utf8JsonWriter json, string name, string where, string description, Schema schema, bool required = false)
    {
        json.WriteStartObject(name);
        json.WriteString("name", name);
        json.WriteString("in", where);
        json.WriteBoolean("required", required);
        json.WriteString("description", description);

        // 20-004r1 gives q, type, ids and externalIds as arrays in form style, not exploded — comma-separated.
        if (schema is Schema.Strings or Schema.Numbers or Schema.Types)
        {
            json.WriteString("style", "form");
            json.WriteBoolean("explode", false);
        }

        json.WriteStartObject("schema");

        switch (schema)
        {
            case Schema.String:
                json.WriteString("type", "string");
                break;

            case Schema.Strings:
                json.WriteString("type", "array");
                json.WriteStartObject("items");
                json.WriteString("type", "string");
                json.WriteEndObject();
                break;

            case Schema.Types:
                json.WriteString("type", "array");
                json.WriteStartObject("items");
                json.WriteString("type", "string");
                json.WriteStartArray("enum");

                foreach (string type in RecordNames.Types)
                {
                    json.WriteStringValue(type);
                }

                json.WriteEndArray();
                json.WriteEndObject();
                break;

            case Schema.Numbers:
                json.WriteString("type", "array");
                json.WriteNumber("minItems", 4);
                json.WriteNumber("maxItems", 6);
                json.WriteStartObject("items");
                json.WriteString("type", "number");
                json.WriteEndObject();
                break;

            case Schema.Limit:
                json.WriteString("type", "integer");
                json.WriteNumber("minimum", 1);
                json.WriteNumber("maximum", OgcLimits.Default.MaximumLimit);
                json.WriteNumber("default", OgcLimits.Default.DefaultLimit);
                break;

            case Schema.Offset:
                json.WriteString("type", "integer");
                json.WriteNumber("minimum", 0);
                json.WriteNumber("default", 0);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(schema), schema, "A parameter schema this writer does not know.");
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static string Moment(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void WriteLinks(Utf8JsonWriter json, IReadOnlyList<RecordLink> links)
    {
        json.WriteStartArray("links");

        foreach (RecordLink link in links)
        {
            json.WriteStartObject();
            json.WriteString("href", link.Href);
            json.WriteString("rel", link.Rel);

            if (link.Type is { Length: > 0 } type)
            {
                json.WriteString("type", type);
            }

            if (link.Title is { Length: > 0 } title)
            {
                json.WriteString("title", title);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using System.IO.MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
