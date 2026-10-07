using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Graticula.Api.ArcGis;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Tiles;
using Graticula.Geometries;
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>A stylesheet a style can be fetched as — ADR-176.</summary>
internal enum StyleEncoding
{
    /// <summary>A Mapbox / MapLibre style, version 8.</summary>
    Mapbox,

    /// <summary>OGC SLD 1.1.0 with Symbology Encoding 1.1.</summary>
    Sld11,

    /// <summary>OGC SLD 1.0.0.</summary>
    Sld10,
}

/// <summary>What a request for a style asked to be answered with.</summary>
internal enum StyleAnswer
{
    /// <summary>The Mapbox stylesheet.</summary>
    Mapbox,

    /// <summary>The SLD 1.1 stylesheet.</summary>
    Sld11,

    /// <summary>The SLD 1.0 stylesheet.</summary>
    Sld10,

    /// <summary>A page for a person: the style's metadata, with a link to every stylesheet.</summary>
    Html,
}

/// <summary>One style of one service, as OGC API Styles lists it — ADR-176.</summary>
/// <param name="Id">Its <c>styleId</c> — <see cref="OgcStylesDocuments.StyleId"/>.</param>
/// <param name="CollectionId">Its service's id on the standard faces — <see cref="TileDocuments.CollectionId"/>.</param>
/// <param name="Service">Its service's qualified name, which is what a person reads.</param>
/// <param name="ServiceDescription">Its service's description, or null.</param>
/// <param name="Name">The stored style's name, or null for the style the layers' own symbology draws.</param>
/// <param name="IsDefault">Whether <c>resources/styles/root.json</c> serves it.</param>
/// <param name="Updated">When the stored style was last written, when the store knows.</param>
/// <param name="Layers">The service's source layers, each with the kind of geometry it holds.</param>
internal sealed record OgcStyle(
    string Id,
    string CollectionId,
    string Service,
    string? ServiceDescription,
    string? Name,
    bool IsDefault,
    DateTimeOffset? Updated,
    IReadOnlyList<(string Id, GeometryKind Geometry)> Layers)
{
    /// <summary>Whether this is the style the layers' own symbology draws, rather than a stored one.</summary>
    public bool IsSymbology => Name is null;

    /// <summary>
    /// The stylesheets it can be fetched as, the native one first.
    /// </summary>
    /// <remarks>
    /// <b>SLD only for the symbology style.</b> A stored style is a Mapbox document its author wrote, and it is served
    /// as written (ADR-028); translating one into SLD would be a converter from one styling language to another,
    /// with losses nobody here has measured, presented as the author's style. The layers' symbology already has an
    /// SLD form — WMS GetStyles writes it (ADR-171) — and a Mapbox form — the generated <c>root.json</c> — and both
    /// are this server's derivations of the same CIM document, so offering both makes no claim the server has not
    /// already made elsewhere.
    /// </remarks>
    public IReadOnlyList<StyleEncoding> Encodings => IsSymbology
        ? [StyleEncoding.Mapbox, StyleEncoding.Sld11, StyleEncoding.Sld10]
        : [StyleEncoding.Mapbox];

    /// <summary>What a person reads.</summary>
    public string Title => IsSymbology
        ? $"{Service} — the layers' own symbology"
        : $"{Service} — {Name}";
}

/// <summary>
/// The OGC API Styles documents — landing page, conformance, the style list, style metadata and the API definition —
/// and the rules that name and negotiate a style — ADR-176.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written against the 1.0.0 draft 1 of OGC 20-009 (May 2021) and read against draft 2 (May 2026)</b>, because no
/// version of it is approved and the two disagree. Draft 1 has <c>/styles/{styleId}/metadata</c> in <c>core</c>,
/// <c>f=mapbox|sld10|sld11</c> and the conformance classes <c>sld-10</c> and <c>sld-11</c>; draft 2 drops the
/// metadata resource, merges SLD into <c>sld-se</c>, names the formats <c>mbgl</c> and <c>sld</c>, and adds a
/// <c>default</c> member to the list. Both spellings of <c>f</c> are read; the metadata resource is served, which
/// draft 2 does not forbid; the list carries what both drafts require of it (INFERRED, ADR-176 §5.2).
/// </para>
/// <para>
/// <b>Read-only.</b> <c>manage-styles</c> and <c>style-validation</c> are not implemented: styles are written
/// through <c>/admin/services/{name}/styles</c>, behind the ownership check (ADR-094), and a second write path to the
/// same rows would be a second set of rules for who may restyle a service.
/// </para>
/// </remarks>
internal static partial class OgcStylesDocuments
{
    /// <summary>Where OGC API Styles lives: beside <c>/ogc/features/v1</c> and <c>/ogc/tiles/v1</c>.</summary>
    public const string Base = "/ogc/styles/v1";

    /// <summary>
    /// The <c>styleId</c> suffix of the style a service's layers draw with their own symbology.
    /// </summary>
    /// <remarks>
    /// <b>Not a name a stored style can have</b>: <see cref="StyleNames"/> starts every name with a letter or digit,
    /// so <c>_symbology</c> can never be taken by an author, and the id of this style never changes meaning when
    /// somebody stores one called <c>symbology</c> — or <c>default</c>, the name a style stored without one gets.
    /// </remarks>
    public const string SymbologySuffix = "_symbology";

    /// <summary>A Mapbox style — 20-009 §11.</summary>
    public const string MapboxMediaType = "application/vnd.mapbox.style+json";

    /// <summary>SLD 1.1 — 20-009 §11; the <c>version</c> parameter carries the major and first minor number.</summary>
    public const string Sld11MediaType = "application/vnd.ogc.sld+xml;version=1.1";

    /// <summary>SLD 1.0 — 20-009 §11.</summary>
    public const string Sld10MediaType = "application/vnd.ogc.sld+xml;version=1.0";

    /// <summary>Relation: the styles of a base resource — 20-009 <c>/req/core/base-resource-link</c>.</summary>
    public const string RelStyles = "http://www.opengis.net/def/rel/ogc/1.0/styles";

    /// <summary>
    /// The same relation as draft 1 spells it, with <c>OGC</c> in capitals. The OGC register's spelling is the lower
    /// case one; a client written against draft 1's text compares the string it read there.
    /// </summary>
    public const string RelStylesDraft1 = "http://www.opengis.net/def/rel/OGC/1.0/styles";

    /// <summary>The OGC API Styles conformance-class prefix.</summary>
    public const string StylesConf = "http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/";

    /// <summary>The OGC API Common Part 1 conformance-class prefix.</summary>
    public const string CommonConf = "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/";

    /// <summary>
    /// What this face claims. Each has a proof in <c>OgcStylesConformanceTests</c>, in both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not claimed, and why.</b> <c>manage-styles</c>, <c>style-validation</c>, <c>resources</c>,
    /// <c>manage-resources</c>: nothing here writes a style or serves a resource list. <c>mapbox-styles</c>,
    /// <c>sld-10</c>, <c>sld-11</c> (draft 1) and <c>sld-se</c> (draft 2): <b>every requirement of those classes is
    /// about a POST or PUT accepting the encoding</b>, and in draft 2 the Mapbox class depends on
    /// <c>manage-styles</c>. A read-only server meets them only vacuously, and claiming them would tell a client
    /// that it may upload SLD here. The encodings a style is offered in are said where the specification puts them —
    /// the <c>type</c> of each <c>stylesheet</c> link, <c>/req/core/styles-success</c> E — which is what a client
    /// choosing one reads.
    /// </para>
    /// <para>
    /// <b><c>html</c></b> is draft 1's class: every JSON answer has a <c>text/html</c> form. Draft 1's
    /// requirements-class table names it with a Testbed-15 URI (<c>http://www.opengis.net/t15/opf-styles-1/…</c>),
    /// which is an editing slip rather than a second class; the URI claimed is the <c>conf/</c> form the draft's
    /// own conformance example uses for every other class (INFERRED).
    /// </para>
    /// </remarks>
    public static readonly string[] ConformsTo =
    [
        CommonConf + "core",
        CommonConf + "landing-page",
        CommonConf + "json",
        CommonConf + "html",
        CommonConf + "oas30",
        StylesConf + "core",
        StylesConf + "html",
    ];

    /// <summary>A style's id: its service's id on the standard faces, a dot, and the style's name.</summary>
    /// <param name="collectionId">The service's id — <see cref="TileDocuments.CollectionId"/>.</param>
    /// <param name="name">The stored style's name, or null for the symbology style.</param>
    /// <returns>The id.</returns>
    /// <remarks>
    /// <para>
    /// <b>Service-qualified, because a style's name is unique only within its service</b> — every service may have a
    /// <c>dark</c>, and <c>default</c> is what most of them call theirs (<see cref="StyleNames.Default"/>). The
    /// service part is the id OGC API Tiles and WMTS already give it, <c>folder.name</c>, so the same string finds the
    /// tiles and the style.
    /// </para>
    /// <para>
    /// <b>Read back by its last dot.</b> A style name has no dot in it (<see cref="StyleNames"/>), so whatever follows
    /// the last one is the style and whatever precedes it the service, however many dots the folder and service
    /// carry. Nothing in it needs escaping in a URL path: the service part is the tile faces' id, and the name is
    /// letters, digits, <c>-</c> and <c>_</c>.
    /// </para>
    /// <para>
    /// <b>Stable</b> for as long as the service keeps its folder and name and the style its name — which is as stable
    /// as the ArcGIS address <c>resources/styles/{name}.json</c> the same style already has.
    /// </para>
    /// </remarks>
    public static string StyleId(string collectionId, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(collectionId);
        return collectionId + "." + (name ?? SymbologySuffix);
    }

    /// <summary>Reads a style id back into its service's id and its style's name.</summary>
    /// <param name="styleId">The id.</param>
    /// <param name="collectionId">The service's id.</param>
    /// <param name="name">The stored style's name, or null for the symbology style.</param>
    /// <returns>False when it cannot be an id of this face's — the caller answers 404.</returns>
    public static bool TryParseStyleId(string? styleId, out string collectionId, out string? name)
    {
        collectionId = string.Empty;
        name = null;

        int dot = styleId?.LastIndexOf('.') ?? -1;

        if (styleId is null || dot <= 0 || dot == styleId.Length - 1)
        {
            return false;
        }

        string suffix = styleId[(dot + 1)..];

        if (!string.Equals(suffix, SymbologySuffix, StringComparison.OrdinalIgnoreCase)
            && !StyleNames.TryValidate(suffix, out _))
        {
            return false;
        }

        collectionId = styleId[..dot];
        name = string.Equals(suffix, SymbologySuffix, StringComparison.OrdinalIgnoreCase) ? null : suffix;
        return true;
    }

    /// <summary>The media type a stylesheet is served and linked with.</summary>
    /// <param name="encoding">The encoding.</param>
    /// <returns>The media type.</returns>
    public static string MediaType(StyleEncoding encoding) => encoding switch
    {
        StyleEncoding.Mapbox => MapboxMediaType,
        StyleEncoding.Sld11 => Sld11MediaType,
        _ => Sld10MediaType,
    };

    /// <summary>The <c>f</c> value a link to a stylesheet carries — draft 1's spelling, which every draft-1 client sends.</summary>
    /// <param name="encoding">The encoding.</param>
    /// <returns>The value.</returns>
    public static string FormatValue(StyleEncoding encoding) => encoding switch
    {
        StyleEncoding.Mapbox => "mapbox",
        StyleEncoding.Sld11 => "sld11",
        _ => "sld10",
    };

    /// <summary>A style's address.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <param name="styleId">The style's id.</param>
    /// <returns>The URL.</returns>
    public static string StyleUrl(string root, string styleId) => $"{root}/styles/{Uri.EscapeDataString(styleId)}";

    /// <summary>
    /// What a request for a stylesheet is answered with, or the refusal — <c>f</c> first, then <c>Accept</c>.
    /// </summary>
    /// <param name="f">The <c>f</c> parameter, or null.</param>
    /// <param name="accept">The <c>Accept</c> header, or null.</param>
    /// <param name="style">The style.</param>
    /// <returns>The answer, or the problem to answer with.</returns>
    /// <remarks>
    /// <para>
    /// <b><c>f</c> wins over <c>Accept</c></b>, as on the features and tiles faces (OGC 17-069 §7.2), and both drafts'
    /// spellings are read: <c>mapbox</c> and <c>mbgl</c>, <c>sld11</c> and <c>sld</c> (draft 2 has one SLD value,
    /// and SLD 1.1 is this server's SLD, ADR-171), <c>sld10</c>, and <c>html</c>. An <c>f</c> this face does not know
    /// is a 400 naming the values it does; one it knows and this style is not offered in is a 406.
    /// </para>
    /// <para>
    /// <b>Without <c>f</c>, the most preferred acceptable type the style has</b>, by <c>q</c> and then by order — 406
    /// when none is. <c>application/json</c> is a Mapbox style, which is JSON; <c>application/xml</c> and
    /// <c>text/xml</c> are SLD 1.1; an SLD type with no <c>version</c> is SLD 1.1, the version 20-009 §11 says a
    /// server should honour when asked and may substitute when not. A wildcard, or no <c>Accept</c> at all, is the
    /// style's native stylesheet, so a MapLibre client given the bare address — which sends <c>*/*</c> — gets the
    /// style it can draw. <c>text/html</c> is the metadata page, which is what a browser asking for a style is
    /// shown, and is what <c>/req/html</c> asks of a resource with a JSON form.
    /// </para>
    /// </remarks>
    public static (StyleAnswer? Answer, OgcProblem? Refusal) Negotiate(string? f, string? accept, OgcStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (!string.IsNullOrWhiteSpace(f))
        {
            StyleAnswer? asked = f.Trim().ToLowerInvariant() switch
            {
                "mapbox" or "mbgl" => StyleAnswer.Mapbox,
                "sld11" or "sld" => StyleAnswer.Sld11,
                "sld10" => StyleAnswer.Sld10,
                "html" => StyleAnswer.Html,
                _ => null,
            };

            if (asked is not { } known)
            {
                return (null, OgcProblem.BadRequest(
                    $"`f={f}` is not a format of this face. A style is asked for with f=mapbox (or mbgl), f=sld11 (or "
                    + "sld), f=sld10 or f=html; which of them a style has is in the `type` of its `stylesheet` links "
                    + $"at {Base}/styles."));
            }

            return Offers(style, known)
                ? (known, null)
                : (null, NotOffered(style, known.ToString()));
        }

        if (string.IsNullOrWhiteSpace(accept)
            || !MediaTypeHeaderValue.TryParseList([accept], out IList<MediaTypeHeaderValue>? ranges)
            || ranges.Count == 0)
        {
            return (Native(style), null);
        }

        foreach (MediaTypeHeaderValue range in ranges
                     .Select((r, i) => (Range: r, Index: i))
                     .Where(r => (r.Range.Quality ?? 1) > 0)
                     .OrderByDescending(r => r.Range.Quality ?? 1)
                     .ThenBy(r => r.Index)
                     .Select(r => r.Range))
        {
            if (Matches(range, style) is { } answer && Offers(style, answer))
            {
                return (answer, null);
            }
        }

        return (null, NotOffered(style, accept));
    }

    private static StyleAnswer Native(OgcStyle style) => style.Encodings[0] switch
    {
        StyleEncoding.Mapbox => StyleAnswer.Mapbox,
        StyleEncoding.Sld11 => StyleAnswer.Sld11,
        _ => StyleAnswer.Sld10,
    };

    private static StyleAnswer? Matches(MediaTypeHeaderValue range, OgcStyle style)
    {
        string type = range.MediaType.Value?.ToLowerInvariant() ?? string.Empty;

        switch (type)
        {
            case "*/*":
            case "application/*":
                return Native(style);

            case MapboxMediaType:
            case "application/json":
                return StyleAnswer.Mapbox;

            case "text/html":
                return StyleAnswer.Html;

            case "application/xml":
            case "text/xml":
                return StyleAnswer.Sld11;

            case "application/vnd.ogc.sld+xml":
                string? version = range.Parameters
                    .FirstOrDefault(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase))?.Value.Value?.Trim('"');

                return version switch
                {
                    null or "1.1" or "1.1.0" => StyleAnswer.Sld11,
                    "1.0" or "1.0.0" => StyleAnswer.Sld10,
                    _ => null,
                };

            default:
                return null;
        }
    }

    private static bool Offers(OgcStyle style, StyleAnswer answer) => answer switch
    {
        StyleAnswer.Html => true,
        StyleAnswer.Mapbox => style.Encodings.Contains(StyleEncoding.Mapbox),
        StyleAnswer.Sld11 => style.Encodings.Contains(StyleEncoding.Sld11),
        _ => style.Encodings.Contains(StyleEncoding.Sld10),
    };

    private static OgcProblem NotOffered(OgcStyle style, string asked) =>
        OgcProblem.NotAcceptable(
            $"`{style.Id}` is offered as {string.Join(", ", style.Encodings.Select(MediaType))} (and text/html), and "
            + $"`{asked}` is none of them."
            + (style.IsSymbology
                ? string.Empty
                : $" It is a Mapbox style its author stored, served as written; the SLD this server can write is of "
                  + $"the layers' own symbology, `{StyleId(style.CollectionId, null)}`."));

    /// <summary>
    /// A Mapbox stylesheet with every relative address in it made absolute against the address it was written for.
    /// </summary>
    /// <param name="style">The stylesheet.</param>
    /// <param name="writtenFor">
    /// Where its relative addresses were meant to be read from: the service's
    /// <c>VectorTileServer/resources/styles/root.json</c>.
    /// </param>
    /// <param name="tileJson">The service's TileJSON, which a source naming the service itself is pointed at, or null.</param>
    /// <returns>The stylesheet, unchanged when nothing in it is relative.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why anything is changed at all.</b> A style for a VectorTileServer names its source as <c>../../</c>, its
    /// glyphs as <c>../fonts/…</c> and its sprite as <c>../sprites/sprite</c> — the generated style always, and a
    /// stored one usually, because it was written for <c>resources/styles/</c>. A client resolves those against the
    /// address it fetched the style from, and from <c>/ogc/styles/v1/styles/{id}</c> they lead to nothing. So the
    /// addresses a style uses to fetch — each source's <c>url</c>, <c>tiles</c> and <c>data</c>, the <c>sprite</c>
    /// (a string or draft 8's array of <c>{id, url}</c>) and the <c>glyphs</c> — are resolved against the address
    /// the style was written for, and nothing else in the document is touched (INFERRED, ADR-176 §5.4).
    /// </para>
    /// <para>
    /// <b>Byte for byte when nothing is relative</b>, which is ADR-028's rule for a stored style: an author whose
    /// addresses are all absolute gets back the file they stored. A style that is not JSON is passed through as it
    /// is; the stored ones were validated when written and again when served (ADR-028 condition 3).
    /// </para>
    /// <para>
    /// <b><c>{fontstack}</c>, <c>{range}</c>, <c>{z}</c>, <c>{x}</c> and <c>{y}</c> stay as they were.</b> They are the
    /// client's placeholders, and a URL resolver percent-encodes braces.
    /// </para>
    /// <para>
    /// <b>A source that is the service's own VectorTileServer is pointed at its TileJSON</b> when the caller has one —
    /// the OGC API Tiles tileset's, for a service tiled on WebMercatorQuad. The VectorTileServer document names its
    /// tiles relatively (<c>tile/{z}/{y}/{x}.pbf</c>), which ArcGIS's JavaScript API resolves against the document and
    /// MapLibre does not: given the absolute VectorTileServer address, MapLibre asked for <c>tile/14/6203/9688.pbf</c>
    /// and drew nothing (measured 2026-10-06, ADR-176 condition 3). The TileJSON's tiles are absolute.
    /// </para>
    /// </remarks>
    public static string AbsoluteAddresses(string style, Uri writtenFor, string? tileJson = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(writtenFor);

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(style);
        }
        catch (JsonException)
        {
            return style;
        }

        if (root is not JsonObject document)
        {
            return style;
        }

        bool changed = false;

        void Resolve(JsonNode? parent, string member)
        {
            if (parent is JsonObject holder
                && holder[member] is JsonValue value
                && value.TryGetValue(out string? address)
                && Absolute(address, writtenFor) is { } absolute)
            {
                holder[member] = absolute;
                changed = true;
            }
        }

        string serviceRoot = new Uri(writtenFor, "../../").AbsoluteUri.TrimEnd('/');

        if (document["sources"] is JsonObject sources)
        {
            foreach ((string _, JsonNode? source) in sources.ToList())
            {
                if (tileJson is not null
                    && source is JsonObject holder
                    && holder["url"] is JsonValue value
                    && value.TryGetValue(out string? address)
                    && string.Equals((Absolute(address, writtenFor) ?? address).TrimEnd('/'), serviceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    holder["url"] = tileJson;
                    changed = true;
                    continue;
                }

                Resolve(source, "url");
                Resolve(source, "data");

                if (source?["tiles"] is JsonArray tiles)
                {
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (tiles[i] is JsonValue tile && tile.TryGetValue(out string? template)
                            && Absolute(template, writtenFor) is { } absolute)
                        {
                            tiles[i] = absolute;
                            changed = true;
                        }
                    }
                }
            }
        }

        Resolve(document, "glyphs");

        if (document["sprite"] is JsonArray sprites)
        {
            foreach (JsonNode? sprite in sprites)
            {
                Resolve(sprite, "url");
            }
        }
        else
        {
            Resolve(document, "sprite");
        }

        return changed ? document.ToJsonString() : style;
    }

    /// <summary>A relative address made absolute, or null when it already is one.</summary>
    internal static string? Absolute(string? address, Uri writtenFor)
    {
        // A scheme makes it absolute — https:, and mapbox: too, which only a Mapbox client resolves. Checked by its
        // shape rather than by Uri.TryCreate, which takes "/fonts/…" for a file path on Linux.
        if (string.IsNullOrEmpty(address) || HasScheme().IsMatch(address))
        {
            return null;
        }

        return new Uri(writtenFor, address).AbsoluteUri
            .Replace("%7B", "{", StringComparison.OrdinalIgnoreCase)
            .Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex HasScheme();

    // ---------- documents ----------

    /// <summary>The landing page.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Landing(string root) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("title", "Graticula — OGC API Styles");
        json.WriteString(
            "description",
            "The styles of this server's vector tile services: each service's stored Mapbox styles, and the style its "
            + "layers' own symbology draws, as Mapbox and as SLD. Read-only; styles are written through the "
            + "administration API.");
        WriteLinks(json,
        [
            ("self", OgcNames.Json, root, "This document"),
            ("alternate", "text/html", root + "?f=html", "This document as HTML"),
            ("service-desc", OgcNames.OpenApi, root + "/api", "The API definition"),
            ("service-doc", "text/html", root + "/api?f=html", "The API definition as HTML"),
            ("conformance", OgcNames.Json, root + "/conformance", "Conformance classes"),
            (TileNames.RelConformance, OgcNames.Json, root + "/conformance", "Conformance classes"),
            (RelStyles, OgcNames.Json, root + "/styles", "The styles"),
            (RelStylesDraft1, OgcNames.Json, root + "/styles", "The styles"),
        ]);
        json.WriteEndObject();
    });

    /// <summary>The conformance declaration.</summary>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Conformance() => Write(json =>
    {
        json.WriteStartObject();
        json.WriteStartArray("conformsTo");

        foreach (string uri in ConformsTo)
        {
            json.WriteStringValue(uri);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>The styles — 20-009 <c>/req/core/styles-success</c>.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <param name="styles">The styles the caller may see.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// <b>No <c>default</c> member.</b> Draft 2 allows one, for the base resource's default style; this base resource
    /// is the whole server, whose services each have a default and none of which is the server's. Which style a
    /// service serves as its own default is said on each style, as <c>graticula:default</c>, because that is a
    /// statement about the service rather than about this list.
    /// </remarks>
    public static byte[] Styles(string root, IReadOnlyList<OgcStyle> styles) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(styles);

        json.WriteStartObject();
        json.WriteStartArray("styles");

        foreach (OgcStyle style in styles)
        {
            json.WriteStartObject();
            json.WriteString("id", style.Id);
            json.WriteString("title", style.Title);
            json.WriteBoolean("graticula:default", style.IsDefault);

            if (style.Updated is { } updated)
            {
                json.WriteString("updated", updated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            }

            WriteLinks(json, [.. StyleLinks(root, style)]);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteLinks(json,
        [
            ("self", OgcNames.Json, root + "/styles", "This document"),
            ("alternate", "text/html", root + "/styles?f=html", "This document as HTML"),
        ]);
        json.WriteEndObject();
    });

    /// <summary>
    /// A style's metadata — draft 1's <c>/req/core/style-md-success</c>.
    /// </summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <param name="tilesRoot">OGC API Tiles' absolute base URL, where each layer's data is.</param>
    /// <param name="style">The style.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// <b>Only what is known.</b> No keywords, contact or access constraints are invented: a service here has none of
    /// those as a style's, and an empty field filled with a guess is a claim. Each layer's <c>sampleData</c> is the
    /// service's vector tileset on OGC API Tiles, which is what the style draws — draft 1's
    /// <c>/rec/core/style-md-sample-data</c> names the <c>tilesets</c> relation for exactly this.
    /// </remarks>
    public static byte[] Metadata(string root, string tilesRoot, OgcStyle style) => Write(json =>
    {
        ArgumentNullException.ThrowIfNull(style);

        json.WriteStartObject();
        json.WriteString("id", style.Id);
        json.WriteString("title", style.Title);
        json.WriteString(
            "description",
            style.IsSymbology
                ? $"How the layers of {style.Service} draw with their own symbology — the style the service's "
                  + "VectorTileServer generates when no stored style is its default, and the style WMS GetStyles "
                  + "writes as SLD. Both stylesheets are this server's derivations of each layer's symbology; what one "
                  + "of them cannot say is named inside it."
                  + (style.ServiceDescription is { Length: > 0 } about ? " " + about : string.Empty)
                : $"The style `{style.Name}` stored on {style.Service}, served as its author wrote it, with relative "
                  + "addresses made absolute."
                  + (style.ServiceDescription is { Length: > 0 } of ? " " + of : string.Empty));
        json.WriteString("scope", "style");

        if (style.Updated is { } updated)
        {
            json.WriteStartObject("dates");
            json.WriteString("revision", updated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }

        json.WriteStartArray("stylesheets");

        foreach (StyleEncoding encoding in style.Encodings)
        {
            json.WriteStartObject();
            json.WriteString("title", encoding switch
            {
                StyleEncoding.Mapbox => "Mapbox Style",
                StyleEncoding.Sld11 => "OGC SLD 1.1",
                _ => "OGC SLD 1.0",
            });
            json.WriteString("version", encoding switch
            {
                StyleEncoding.Mapbox => "8",
                StyleEncoding.Sld11 => "1.1.0",
                _ => "1.0.0",
            });

            // The specifications 20-009 §11 itself cites for each media type.
            json.WriteString("specification", encoding switch
            {
                StyleEncoding.Mapbox => "https://docs.mapbox.com/mapbox-gl-js/style-spec/",
                StyleEncoding.Sld11 => "http://portal.opengeospatial.org/files/?artifact_id=22364",
                _ => "http://portal.opengeospatial.org/files/?artifact_id=1188",
            });

            // A stored style's Mapbox document is what its author wrote; the symbology style's stylesheets are both
            // derived from a CIM document neither of them is.
            json.WriteBoolean("native", !style.IsSymbology && encoding == StyleEncoding.Mapbox);
            json.WritePropertyName("link");
            WriteLink(json, "stylesheet", MediaType(encoding),
                $"{StyleUrl(root, style.Id)}?f={FormatValue(encoding)}", null);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        json.WriteStartArray("layers");

        foreach ((string id, GeometryKind geometry) in style.Layers)
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("type", geometry switch
            {
                GeometryKind.Point or GeometryKind.MultiPoint => "point",
                GeometryKind.LineString or GeometryKind.MultiLineString => "line",
                GeometryKind.Polygon or GeometryKind.MultiPolygon => "polygon",
                _ => "geometry",
            });
            json.WritePropertyName("sampleData");
            WriteLink(json, TileNames.RelTilesetsVector, OgcNames.Json,
                TileDocuments.TilesUrl(tilesRoot, style.CollectionId), "The vector tilesets this layer is in");
            json.WriteEndObject();
        }

        json.WriteEndArray();

        WriteLinks(json,
        [
            ("self", OgcNames.Json, $"{StyleUrl(root, style.Id)}/metadata", "This document"),
            ("alternate", "text/html", $"{StyleUrl(root, style.Id)}/metadata?f=html", "This document as HTML"),
            .. StyleLinks(root, style).Where(l => l.Rel == "stylesheet"),
            (TileNames.RelTilesetsVector, OgcNames.Json, TileDocuments.TilesUrl(tilesRoot, style.CollectionId),
                "The vector tilesets this style draws"),
        ]);
        json.WriteEndObject();
    });

    private static IEnumerable<(string Rel, string Type, string Href, string Title)> StyleLinks(string root, OgcStyle style)
    {
        foreach (StyleEncoding encoding in style.Encodings)
        {
            yield return ("stylesheet", MediaType(encoding), $"{StyleUrl(root, style.Id)}?f={FormatValue(encoding)}",
                encoding switch
                {
                    StyleEncoding.Mapbox => "Mapbox Style",
                    StyleEncoding.Sld11 => "OGC SLD 1.1",
                    _ => "OGC SLD 1.0",
                });
        }

        yield return ("describedby", OgcNames.Json, $"{StyleUrl(root, style.Id)}/metadata", "The style's metadata");
    }

    /// <summary>The OpenAPI 3.0 definition at <c>/api</c>.</summary>
    /// <param name="root">The face's absolute base URL.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// <b>Written by hand</b>, for the reason <see cref="OpenApiDocument"/> gives: it says what the specification
    /// requires and this face honours, so a disagreement with the routes is a finding rather than something a
    /// generator would paper over. Nothing that is not implemented appears.
    /// </remarks>
    public static byte[] OpenApi(string root) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("openapi", "3.0.3");

        json.WriteStartObject("info");
        json.WriteString("title", "Graticula — OGC API Styles");
        json.WriteString("description", "The styles of this server's vector tile services, read-only.");
        json.WriteString("version", "1.0.0");
        json.WriteStartObject("license");
        json.WriteString("name", "Elastic-2.0");
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteStartArray("servers");
        json.WriteStartObject();
        json.WriteString("url", root);
        json.WriteEndObject();
        json.WriteEndArray();

        json.WriteStartObject("paths");

        Operation(json, "/", "getLandingPage", "The landing page", withId: false, [Json, Html], "fJson");
        Operation(json, "/conformance", "getConformanceDeclaration", "The conformance classes this face implements",
            withId: false, [Json, Html], "fJson");
        Operation(json, "/api", "getApiDefinition", "This document", withId: false,
            [OgcNames.OpenApi, Html], "fJson");
        Operation(json, "/styles", "getStyles", "The styles the caller may see", withId: false, [Json, Html], "fJson");
        Operation(json, "/styles/{styleId}", "getStyle",
            "A style's stylesheet, in the encoding asked for by f or Accept", withId: true,
            [MapboxMediaType, Sld11MediaType, Sld10MediaType, Html], "fStyle");
        Operation(json, "/styles/{styleId}/metadata", "getStyleMetadata", "A style's metadata", withId: true,
            [Json, Html], "fJson");

        json.WriteEndObject();

        json.WriteStartObject("components");
        json.WriteStartObject("parameters");

        json.WriteStartObject("styleId");
        json.WriteString("name", "styleId");
        json.WriteString("in", "path");
        json.WriteBoolean("required", true);
        json.WriteString(
            "description",
            "A style's id: its service's OGC API Tiles collection id, a dot, and the style's name — or `_symbology` "
            + "for the style the layers' own symbology draws.");
        json.WriteStartObject("schema");
        json.WriteString("type", "string");
        json.WriteEndObject();
        json.WriteEndObject();

        FormatParameter(json, "fJson", ["json", "html"]);
        FormatParameter(json, "fStyle", ["mapbox", "mbgl", "sld11", "sld", "sld10", "html"]);

        json.WriteEndObject();

        json.WriteStartObject("schemas");
        json.WriteStartObject("problem");
        json.WriteString("type", "object");
        json.WriteStartObject("properties");

        foreach (string member in (string[])["type", "title", "detail"])
        {
            json.WriteStartObject(member);
            json.WriteString("type", "string");
            json.WriteEndObject();
        }

        json.WriteStartObject("status");
        json.WriteString("type", "integer");
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();

        json.WriteEndObject();
        json.WriteEndObject();
    });

    private const string Json = "application/json";

    private const string Html = "text/html";

    private static void Operation(
        Utf8JsonWriter json, string path, string id, string summary, bool withId, string[] media, string format)
    {
        json.WriteStartObject(path);
        json.WriteStartObject("get");
        json.WriteString("operationId", id);
        json.WriteString("summary", summary);
        json.WriteStartArray("parameters");

        if (withId)
        {
            json.WriteStartObject();
            json.WriteString("$ref", "#/components/parameters/styleId");
            json.WriteEndObject();
        }

        json.WriteStartObject();
        json.WriteString("$ref", "#/components/parameters/" + format);
        json.WriteEndObject();
        json.WriteEndArray();

        json.WriteStartObject("responses");
        json.WriteStartObject("200");
        json.WriteString("description", summary);
        json.WriteStartObject("content");

        foreach (string type in media)
        {
            json.WriteStartObject(type);
            json.WriteStartObject("schema");
            json.WriteString("type", type.Contains("json", StringComparison.Ordinal) ? "object" : "string");
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();

        foreach ((string status, string description) in withId
                     ? [("400", "An f this face does not know"), ("404", "No style of that id the caller may see"),
                        ("406", "The style is not offered in the encoding asked for"),
                        ("503", "The catalogue cannot say")]
                     : (IEnumerable<(string, string)>)[("503", "The catalogue cannot say")])
        {
            json.WriteStartObject(status);
            json.WriteString("description", description);
            json.WriteStartObject("content");
            json.WriteStartObject(OgcNames.Problem);
            json.WriteStartObject("schema");
            json.WriteString("$ref", "#/components/schemas/problem");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void FormatParameter(Utf8JsonWriter json, string name, string[] values)
    {
        json.WriteStartObject(name);
        json.WriteString("name", "f");
        json.WriteString("in", "query");
        json.WriteBoolean("required", false);
        json.WriteString("description", "The representation, which wins over Accept.");
        json.WriteStartObject("schema");
        json.WriteString("type", "string");
        json.WriteStartArray("enum");

        foreach (string value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteLinks(Utf8JsonWriter json, IReadOnlyList<(string Rel, string Type, string Href, string Title)> links)
    {
        json.WriteStartArray("links");

        foreach ((string rel, string type, string href, string title) in links)
        {
            WriteLink(json, rel, type, href, title);
        }

        json.WriteEndArray();
    }

    private static void WriteLink(Utf8JsonWriter json, string rel, string type, string href, string? title)
    {
        json.WriteStartObject();
        json.WriteString("rel", rel);
        json.WriteString("type", type);
        json.WriteString("href", href);

        if (title is not null)
        {
            json.WriteString("title", title);
        }

        json.WriteEndObject();
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            body(json);
        }

        return stream.ToArray();
    }
}
