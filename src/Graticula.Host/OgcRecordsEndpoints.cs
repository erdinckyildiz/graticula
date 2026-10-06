using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Tiles;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// OGC API – Records – Part 1: Core, read-only, as a searchable catalogue of what this server publishes — ADR-177.
/// </summary>
/// <remarks>
/// <para>
/// <b>At <c>/ogc/records/v1</c>, beside the features and tiles faces</b>, which ADR-042 §5.1 versioned in the path so
/// that others could sit beside them. The owner asked for it on 2026-10-06 — <i>"hadi ogc yi de bitirelim"</i>, choosing
/// Records among the OGC APIs still missing.
/// </para>
/// <para>
/// <b>A record is a portal item, and the listing is the portal's</b> (<see cref="PortalEndpoints.ListAsync"/>): the
/// same services, web maps and image services, under the same sharing rule, for the same caller. So a record exists
/// exactly when <c>/sharing/rest/search</c> would show its item, and its id is the item's id — nothing is stored, and
/// there is no second catalogue to fall out of step with the first.
/// </para>
/// <para>
/// <b>What a record adds is where the item can be reached</b>: its ArcGIS REST address, and each OGC address that
/// answers for it — WFS, WMS, OGC API Features, OGC API Tiles, WMTS and WCS — decided by the rules those faces read
/// (<see cref="ServiceFaces"/>, <see cref="TileFaces.Serves"/>, <see cref="PublishedCoverage.OffersOgc"/>), so a record
/// never links an address its own face would refuse.
/// </para>
/// <para>
/// <b>Refusals are RFC 7807 problems with truthful status codes</b>, as on the other OGC API faces: 400 for a
/// parameter this face does not read or cannot parse, 404 for a catalogue or record the caller cannot see or that does
/// not exist — one answer for both (ADR-018) — and 503 while the catalogue cannot be listed (D-127).
/// </para>
/// </remarks>
internal static class OgcRecordsEndpoints
{
    private const string Root = RecordNames.Base;

    /// <summary>Maps the face.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // <b>Filtering, as every standard face is governed</b>: each route lists what the caller may see, so an item
        // the caller may not see is absent from every answer.
        app.MapGet(Root, Landing).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/conformance", Conformance).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/api", Api).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections", Collections).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{catalogId}", Collection).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{catalogId}/items", ItemsAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet(Root + "/collections/{catalogId}/items/{recordId}", ItemAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>The link the REST directory's root page offers to this face.</summary>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink() => ("OGC API Records", Root);

    /// <summary>The server's absolute address, with the path base a proxy puts in front of it.</summary>
    private static string Origin(HttpContext context) => TileFaces.Origin(context);

    private static string Base(HttpContext context) => Origin(context) + Root;

    private static Task Landing(HttpContext context) =>
        AnswerAsync(context, "OGC API Records", RecordDocuments.Landing(Base(context)), OgcNames.Json);

    private static Task Conformance(HttpContext context) =>
        AnswerAsync(context, "Conformance", RecordDocuments.Conformance(), OgcNames.Json);

    private static Task Api(HttpContext context) =>
        AnswerAsync(context, "API definition", RecordDocuments.OpenApi(Base(context)), OgcNames.OpenApi);

    private static Task Collections(HttpContext context) =>
        AnswerAsync(context, "Catalogues", RecordDocuments.Collections(Base(context)), OgcNames.Json);

    private static Task Collection(HttpContext context, string catalogId) =>
        IsCatalog(catalogId)
            ? AnswerAsync(context, "Published items", RecordDocuments.Catalog(Base(context)), RecordNames.CatalogMediaType)
            : RefuseAsync(context, NoCatalog(catalogId));

    /// <summary>The records, filtered and paged.</summary>
    /// <remarks>
    /// <para>
    /// <b>Cheap predicates first, the extent last and only where it is needed.</b> A service's extent is read from each
    /// layer's described shape (<see cref="PortalEndpoints.ExtentAsync"/>), which a cold cache reads from the source —
    /// the reason the portal leaves it off a search. Here it is read for every candidate only when <c>bbox</c> asks, and
    /// otherwise for the page being written: ten services, not the catalogue.
    /// </para>
    /// <para>
    /// <b>Ordered by title, then id</b>, so that <c>offset</c> pages through a stable list; the portal's own order is the
    /// catalogue's, which a page boundary has no reason to follow.
    /// </para>
    /// </remarks>
    private static async Task ItemsAsync(
        HttpContext context,
        string catalogId,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (!IsCatalog(catalogId))
        {
            await RefuseAsync(context, NoCatalog(catalogId)).ConfigureAwait(false);
            return;
        }

        if (!FormatIsKnown(context, out OgcProblem? format))
        {
            await RefuseAsync(context, format!).ConfigureAwait(false);
            return;
        }

        if (!RecordQuery.TryParse(
                name => context.Request.Query.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value)
                    ? value.ToString()
                    : null,
                context.Request.Query.Keys,
                out RecordQuery? query,
                out OgcProblem? problem))
        {
            await RefuseAsync(context, problem!).ConfigureAwait(false);
            return;
        }

        if (await PortalEndpoints.ListAsync(context, catalog, maps, coverages, null, cancellation).ConfigureAwait(false)
            is not { } listed)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return;
        }

        Extents extents = new(maps, contexts, projector);
        IReadOnlySet<string> ambiguous = AmbiguousTileIds(listed);
        List<(PortalEndpoints.PortalListed Listed, CatalogRecord Record)> candidates = [];

        foreach (PortalEndpoints.PortalListed entry in listed)
        {
            CatalogRecord record = RecordOf(context, entry, ambiguous);

            if (!query!.MatchesWithoutExtent(record))
            {
                continue;
            }

            if (query.NeedsExtent)
            {
                record = record with { Extent = await extents.OfAsync(entry, cancellation).ConfigureAwait(false) };

                if (!query.MatchesExtent(record.Extent))
                {
                    continue;
                }
            }

            candidates.Add((entry, record));
        }

        List<(PortalEndpoints.PortalListed Listed, CatalogRecord Record)> ordered =
        [
            .. candidates
                .OrderBy(c => c.Record.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Record.Id, StringComparer.OrdinalIgnoreCase),
        ];

        List<CatalogRecord> page = [];

        foreach ((PortalEndpoints.PortalListed entry, CatalogRecord record) in ordered.Skip(query!.Offset).Take(query.Limit))
        {
            page.Add(record.Extent is null
                ? record with { Extent = await extents.OfAsync(entry, cancellation).ConfigureAwait(false) }
                : record);
        }

        List<KeyValuePair<string, string>> parameters =
            [.. context.Request.Query.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.ToString()))];

        await AnswerAsync(
                context,
                "Records",
                RecordDocuments.Items(Base(context), page, ordered.Count, query, parameters, DateTimeOffset.UtcNow),
                OgcNames.GeoJson,
                profile: true)
            .ConfigureAwait(false);
    }

    /// <summary>One record.</summary>
    private static async Task ItemAsync(
        HttpContext context,
        string catalogId,
        string recordId,
        CatalogFallback catalog,
        IWebMapStore maps,
        ICoverageCatalog coverages,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (!IsCatalog(catalogId))
        {
            await RefuseAsync(context, NoCatalog(catalogId)).ConfigureAwait(false);
            return;
        }

        if (!FormatIsKnown(context, out OgcProblem? format))
        {
            await RefuseAsync(context, format!).ConfigureAwait(false);
            return;
        }

        if (await PortalEndpoints.ListAsync(context, catalog, maps, coverages, null, cancellation).ConfigureAwait(false)
            is not { } listed)
        {
            await RefuseAsync(context, Unavailable).ConfigureAwait(false);
            return;
        }

        if (listed.FirstOrDefault(entry => string.Equals(entry.Id, recordId, StringComparison.OrdinalIgnoreCase))
            is not { } found)
        {
            // <b>The same answer whether it does not exist or is not visible</b> — the portal's rule, and ADR-018's.
            await RefuseAsync(
                    context,
                    OgcProblem.NotFound(
                        $"`{recordId}` is not a record in this catalogue that you may see. See {Root}/collections/"
                        + $"{RecordNames.CatalogId}/items."))
                .ConfigureAwait(false);
            return;
        }

        Extents extents = new(maps, contexts, projector);
        CatalogRecord record = RecordOf(context, found, AmbiguousTileIds(listed)) with
        {
            Extent = await extents.OfAsync(found, cancellation).ConfigureAwait(false),
        };

        await AnswerAsync(context, record.Title, RecordDocuments.Record(Base(context), record), OgcNames.GeoJson, profile: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A portal item as a record: its fields from the item, its links from what it was made from.
    /// </summary>
    /// <remarks>
    /// <b>The fields are read from the item object a portal search returns</b> (<see cref="PortalQuery.Field"/>), not
    /// from the service again, so a record and its item cannot disagree about a title, a description or a tag.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="entry">The portal item.</param>
    /// <param name="ambiguousTiles">OGC API Tiles ids two services answer to, which serve neither.</param>
    /// <returns>The record, its extent not yet read.</returns>
    internal static CatalogRecord RecordOf(
        HttpContext context, PortalEndpoints.PortalListed entry, IReadOnlySet<string> ambiguousTiles)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entry);

        object item = entry.Item;
        string origin = Origin(context);

        string? description = Text(PortalQuery.Field(item, "description")) ?? Text(PortalQuery.Field(item, "snippet"));

        List<RecordLink> links = [.. ResourceLinks(origin, entry, ambiguousTiles)];

        string portalItem = $"{origin}{PortalEndpoints.Path}/content/items/{Uri.EscapeDataString(entry.Id)}";
        links.Add(new RecordLink(portalItem + "?f=json", "related", OgcNames.Json, "Portal item (ArcGIS REST API)"));

        if (Text(PortalQuery.Field(item, "thumbnail")) is { } thumbnail)
        {
            links.Add(new RecordLink($"{portalItem}/info/{thumbnail}", "preview", "image/png", "Thumbnail"));
        }

        return new CatalogRecord(
            entry.Id,
            Text(PortalQuery.Field(item, "type")) ?? string.Empty,
            Text(PortalQuery.Field(item, "title")) ?? entry.Id,
            description,
            PortalQuery.Field(item, "tags") is IEnumerable<string> tags ? [.. tags] : [],
            Epoch(PortalQuery.Field(item, "created")),
            Epoch(PortalQuery.Field(item, "modified")),
            entry.Service?.OgcAccessConstraints ?? entry.Coverage?.OgcAccessConstraints,
            links);
    }

    /// <summary>
    /// Where the resource is: <c>describes</c> links to each address that answers for it, by the faces' own rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>describes</c>, as 20-004r1 <c>/rec/record-core/links</c> A asks of a link to the resource a record
    /// describes</b>, typed with what the address answers. A face is linked only while its service runs: the portal shows
    /// a stopped service to an administrator, and a stopped service's OGC addresses list nothing.
    /// </para>
    /// <para>
    /// <b>Not linked</b>: <c>generateKml</c>, which is an operation rather than an address a catalogue client binds to;
    /// <c>/wmts</c> for a vector tile service, which is the whole server's capabilities rather than the service's.
    /// </para>
    /// </remarks>
    private static IEnumerable<RecordLink> ResourceLinks(
        string origin, PortalEndpoints.PortalListed entry, IReadOnlySet<string> ambiguousTiles)
    {
        if (entry.Map is { } map)
        {
            // A web map's content is its document, which is what an ArcGIS client opens (ADR-079 §5.3).
            yield return new RecordLink(
                $"{origin}{PortalEndpoints.Path}/content/items/{map.Id}/data", "describes", OgcNames.Json, "ArcGIS Web Map");
            yield return new RecordLink(
                $"{origin}/studio/webmap.html?id={map.Id}", "related", OgcNames.Html, "Open in the map viewer");
            yield break;
        }

        if (entry.Coverage is { } coverage)
        {
            string image = $"{origin}/rest/services/{coverage.QualifiedName}/ImageServer";
            bool started = coverage.Status == ServiceStatus.Started;

            yield return new RecordLink(image + "?f=json", "describes", OgcNames.Json, "ArcGIS ImageServer");

            if (started && coverage.OffersOgc("WMS"))
            {
                yield return new RecordLink(
                    image + "/WMSServer?service=WMS&request=GetCapabilities", "describes", "text/xml", "OGC WMS");
            }

            if (started && coverage.OffersOgc("WMTS"))
            {
                yield return new RecordLink(
                    image + "/WMTS/1.0.0/WMTSCapabilities.xml", "describes", "application/xml", "OGC WMTS");
            }

            if (started && coverage.OffersOgc("WCS"))
            {
                yield return new RecordLink(
                    image + "/WCSServer?service=WCS&request=GetCapabilities", "describes", "application/xml", "OGC WCS");
            }

            yield break;
        }

        if (entry.Service is not { } service || entry.Face is not { } face)
        {
            yield break;
        }

        string address = $"{origin}/rest/services/{service.QualifiedName}/{face}";

        yield return new RecordLink(address + "?f=json", "describes", OgcNames.Json, "ArcGIS " + face);

        if (!service.IsRunning)
        {
            yield break;
        }

        switch (face)
        {
            case "FeatureServer":
                if (ServiceFaces.OffersWfs(service))
                {
                    yield return new RecordLink(
                        address + "/WFSServer?service=WFS&request=GetCapabilities", "describes", "text/xml", "OGC WFS");
                }

                if (ServiceFaces.OffersOgcFeatures(service))
                {
                    foreach (PublishedLayer layer in service.Layers
                        .Where(l => l.Definition.GeometryColumn is { Length: > 0 })
                        .OrderBy(l => l.LayerIndex))
                    {
                        yield return new RecordLink(
                            $"{origin}{OgcNames.Base}/collections/{Uri.EscapeDataString(layer.Definition.Name)}",
                            "describes",
                            OgcNames.Json,
                            "OGC API Features: " + layer.Definition.Name);
                    }
                }

                break;

            case "MapServer":
                if (ServiceFaces.OffersWms(service))
                {
                    yield return new RecordLink(
                        address + "/WMSServer?service=WMS&request=GetCapabilities", "describes", "text/xml", "OGC WMS");
                }

                break;

            case "VectorTileServer":
                string tiles = TileDocuments.CollectionId(service.Folder, service.Name);

                if (TileFaces.Serves(service) && !ambiguousTiles.Contains(tiles))
                {
                    yield return new RecordLink(
                        $"{origin}{TileNames.OgcBase}/collections/{Uri.EscapeDataString(tiles)}",
                        "describes",
                        OgcNames.Json,
                        "OGC API Tiles");
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The OGC API Tiles ids two listed services answer to — which that face serves neither under
    /// (<see cref="TileFaces.VisibleAsync"/>), so neither record may link one.
    /// </summary>
    private static HashSet<string> AmbiguousTileIds(IReadOnlyList<PortalEndpoints.PortalListed> listed) =>
        new(
            listed
                .Where(e => e.Face == "VectorTileServer" && e.Service is { } s && TileFaces.Serves(s))
                .GroupBy(e => TileDocuments.CollectionId(e.Service!.Folder, e.Service.Name), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads each item's extent once per request, a service's once for all of its faces.</summary>
    private sealed class Extents(IWebMapStore maps, ServiceContexts contexts, IProjector projector)
    {
        private readonly Dictionary<Guid, Envelope?> _services = [];

        public async Task<Envelope?> OfAsync(PortalEndpoints.PortalListed entry, CancellationToken cancellation)
        {
            if (entry.Service is { } service)
            {
                if (!_services.TryGetValue(service.Id, out Envelope? known))
                {
                    known = Box(await PortalEndpoints.ExtentAsync(service, contexts, projector, cancellation).ConfigureAwait(false));
                    _services[service.Id] = known;
                }

                return known;
            }

            if (entry.Coverage is { } coverage)
            {
                return Box(await PortalEndpoints.CoverageExtentAsync(coverage, projector, cancellation).ConfigureAwait(false));
            }

            // A listed map carries no document; its view is read from the one saved (ADR-079), as the item reads it.
            if (entry.Map is { } map
                && await maps.FindAsync(map.Id, cancellation).ConfigureAwait(false) is { } saved)
            {
                return Box(PortalEndpoints.MapExtent(saved.Document));
            }

            return null;
        }

        private static Envelope? Box(double[][] corners) =>
            corners is [[double minX, double minY], [double maxX, double maxY]]
                ? new Envelope(minX, minY, maxX, maxY)
                : null;
    }

    private static string? Text(object? value) => value is string { Length: > 0 } text ? text : null;

    private static DateTimeOffset? Epoch(object? value) =>
        value is long milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    private static bool IsCatalog(string catalogId) =>
        string.Equals(catalogId, RecordNames.CatalogId, StringComparison.Ordinal);

    private static OgcProblem NoCatalog(string catalogId) =>
        OgcProblem.NotFound(
            $"`{catalogId}` is not a catalogue of this server. It has one, `{RecordNames.CatalogId}`: see {Root}/collections.");

    /// <summary>The answer while the catalogue can say nothing — not a 404 and not an empty page, both of which are claims.</summary>
    private static OgcProblem Unavailable => OgcProblem.Unavailable(
        "The catalogue is not reachable and this server has no remembered answer, so it cannot say what it publishes. "
        + "Retry shortly; see /healthz/ready.");

    /// <summary>Whether <c>f</c> names a representation this face writes: json or html, or none.</summary>
    private static bool FormatIsKnown(HttpContext context, out OgcProblem? problem)
    {
        string? asked = context.Request.Query["f"];
        problem = null;

        if (string.IsNullOrWhiteSpace(asked)
            || string.Equals(asked, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(asked, "html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        problem = OgcProblem.BadRequest($"`f={asked}` is not a representation of this resource: it is json or html.");
        return false;
    }

    /// <summary>A JSON document, or its HTML page when the caller asked for one.</summary>
    /// <param name="context">The request.</param>
    /// <param name="title">The HTML page's title.</param>
    /// <param name="document">The JSON.</param>
    /// <param name="mediaType">What the JSON is served as.</param>
    /// <param name="profile">Whether to name the catalogue profile in a <c>Link</c> header — <c>/rec/json/header-profile-link</c>.</param>
    private static Task AnswerAsync(HttpContext context, string title, string document, string mediaType, bool profile = false)
    {
        if (!FormatIsKnown(context, out OgcProblem? format))
        {
            return RefuseAsync(context, format!);
        }

        if (WantsHtml(context))
        {
            return Results.Content(
                    OgcHtml.Document(context.Request.Path, title, document),
                    "text/html; charset=utf-8")
                .ExecuteAsync(context);
        }

        if (profile)
        {
            context.Response.Headers.Append("Link", $"<{RecordNames.Profile}>; rel=\"profile\"");
        }

        context.Response.ContentType = mediaType + "; charset=utf-8";
        return context.Response.WriteAsync(document, Encoding.UTF8, context.RequestAborted);
    }

    /// <summary><c>f</c> wins over <c>Accept</c>, as on the features and tiles faces.</summary>
    private static bool WantsHtml(HttpContext context)
    {
        string? asked = context.Request.Query["f"];

        if (!string.IsNullOrWhiteSpace(asked))
        {
            return string.Equals(asked, "html", StringComparison.OrdinalIgnoreCase);
        }

        return RestDirectory.WantsHtml(default, context.Request.Headers.Accept);
    }

    private static Task RefuseAsync(HttpContext context, OgcProblem problem)
    {
        context.Response.StatusCode = problem.Status;
        context.Response.ContentType = OgcNames.Problem;
        return context.Response.WriteAsync(problem.ToJson());
    }
}
