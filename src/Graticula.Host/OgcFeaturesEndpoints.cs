using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.OgcFeatures;
using Graticula.Api.Wms;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// OGC API Features, Part 1 (Core) and Part 2 (CRS).
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-042](../../docs/adr/ADR-042-ogc-api-features.md), and this one resumes a
/// plan rather than reversing one.</b>
/// [ADR-005](../../docs/adr/ADR-005-api-architecture.md) chose OGC API Features as
/// the *native* surface with everything else in a compatibility layer;
/// [v1-scope](../../docs/v1-scope.md) §4 inverted that for v1 and said so, marking
/// ADR-005 `REOPENED`. This is the inversion being unwound.
/// </para>
/// <para>
/// <b>Sharing and capability limits are the catalogue's, as on every other
/// face.</b> The same four checks: running, shared, privileged, and the feature face
/// switched on. A collection a caller may not see is **absent**, not forbidden —
/// answering 403 would name a private layer to a stranger.
/// </para>
/// </remarks>
internal static partial class OgcFeaturesEndpoints
{
    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        const string Root = OgcNames.Base;

        app.MapGet(Root, LandingAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Root}/conformance", ConformanceAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Root}/api", ApiAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Root}/collections", CollectionsAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Root}/collections/{{collectionId}}", CollectionAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Root}/collections/{{collectionId}}/items", ItemsAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Root}/collections/{{collectionId}}/items/{{featureId}}", ItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);

        // <b>The write surface — [Q-44](../../docs/open-questions.md), owner decision
        // 2026-08-25.</b> Its own file because it is its own concern, and because a read
        // surface and a write surface sharing one file is how a reviewer stops being able
        // to see which routes mutate.
        MapWrites(app);
    }

    /// <summary>The link a REST directory page offers to this surface.</summary>
    /// <param name="collectionId">The collection to open, or null for the landing page.</param>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink(string? collectionId) =>
        collectionId is null
            ? ("OGC API", OgcNames.Base)
            : ("OGC API",
                $"{OgcNames.Base}/collections/{Uri.EscapeDataString(collectionId)}/items");

    private static string Origin(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

    /// <summary>
    /// Whether the caller asked for HTML.
    /// </summary>
    /// <remarks>
    /// <b><c>f</c> beats <c>Accept</c>, which is Part 1 §7.2's own rule.</b> A
    /// browser sends an <c>Accept</c> header that prefers HTML for every request it
    /// makes, so a server that only read the header would hand a browser-based client
    /// HTML for the JSON it asked for in code.
    /// </remarks>
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

    private static async Task JsonAsync(HttpContext context, string document, string mediaType)
    {
        context.Response.ContentType = mediaType + "; charset=utf-8";
        await context.Response.WriteAsync(document).ConfigureAwait(false);
    }

    // ---------- metadata ----------

    private static async Task LandingAsync(HttpContext context)
    {
        string document = OgcDocuments.Landing(Origin(context));

        if (WantsHtml(context))
        {
            await Results.Content(
                OgcHtml.Landing(context.Request.Path, document), "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await JsonAsync(context, document, OgcNames.Json).ConfigureAwait(false);
    }

    private static async Task ConformanceAsync(HttpContext context)
    {
        string document = OgcDocuments.Conformance();

        if (WantsHtml(context))
        {
            await Results.Content(
                OgcHtml.Document(context.Request.Path, "Conformance", document),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await JsonAsync(context, document, OgcNames.Json).ConfigureAwait(false);
    }

    private static async Task ApiAsync(HttpContext context)
    {
        string document = OpenApiDocument.Write(Origin(context), OgcLimits.Default);

        if (WantsHtml(context))
        {
            await Results.Content(
                OgcHtml.Document(context.Request.Path, "API definition", document),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await JsonAsync(context, document, OgcNames.OpenApi).ConfigureAwait(false);
    }

    private static async Task CollectionsAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        List<CollectionMetadata>? collections =
            await DescribeAllAsync(context, catalog, contexts, projector, cancellation)
                .ConfigureAwait(false);

        // Null means the refusal is already written. An empty collections document would
        // say this server publishes nothing, which is a different answer.
        if (collections is null)
        {
            return;
        }

        string document = OgcDocuments.Collections(Origin(context), collections);

        if (WantsHtml(context))
        {
            await Results.Content(
                OgcHtml.Collections(context.Request.Path, collections),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await JsonAsync(context, document, OgcNames.Json).ConfigureAwait(false);
    }

    private static async Task CollectionAsync(
        HttpContext context,
        string collectionId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection, bool refused) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

        // Refused, not missing: the catalogue could not be listed, and *no such collection*
        // would be a claim about one that probably exists.
        if (refused)
        {
            return;
        }

        if (layer is null || collection is null)
        {
            await RefuseAsync(context, Missing(collectionId)).ConfigureAwait(false);
            return;
        }

        string document = OgcDocuments.Collection(Origin(context), collection);

        if (WantsHtml(context))
        {
            await Results.Content(
                OgcHtml.Collection(context.Request.Path, collection),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await JsonAsync(context, document, OgcNames.Json).ConfigureAwait(false);
    }

    // ---------- features ----------

    private static async Task ItemsAsync(
        HttpContext context,
        string collectionId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection, bool refused) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

        // Refused, not missing: the catalogue could not be listed, and *no such collection*
        // would be a claim about one that probably exists.
        if (refused)
        {
            return;
        }

        if (layer is null || collection is null)
        {
            await RefuseAsync(context, Missing(collectionId)).ConfigureAwait(false);
            return;
        }

        OgcLimits limits = new(
            Math.Min(OgcLimits.Default.DefaultLimit, settings.MaximumRecordCount),
            Math.Min(OgcLimits.Default.MaximumLimit, settings.MaximumRecordCount));

        Dictionary<string, string> query = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair
            in context.Request.Query)
        {
            query[pair.Key] = pair.Value.ToString();
        }

        if (!OgcRequest.TryParse(
                name => query.TryGetValue(name, out string? value) ? value : null,
                query.Keys,
                collection,
                limits,
                out OgcRequest? request,
                out OgcProblem? problem))
        {
            await RefuseAsync(context, problem!).ConfigureAwait(false);
            return;
        }

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        if (!TryQuery(
                request!, layer, described, collection,
                out FeatureQuery? features, out bool empty, out problem))
        {
            await RefuseAsync(context, problem!).ConfigureAwait(false);
            return;
        }

        long matched = empty
            ? 0
            : await source.CountAsync(features!, cancellation).ConfigureAwait(false);

        string self = $"{Origin(context)}{context.Request.Path}";

        OgcFeatureWriter writer = new(collection, self, request!.LatitudeFirst);

        if (WantsHtml(context))
        {
            List<Feature> page = [];

            await foreach (Feature feature in
                source.ReadAsync(features!, cancellation).ConfigureAwait(false))
            {
                page.Add(feature);
            }

            await Results.Content(
                OgcHtml.Items(context.Request.Path, collection, page, request!, matched),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        context.Response.ContentType = OgcNames.GeoJson;

        // Part 2 §6.6: a response in any CRS says which one, in a header, because
        // GeoJSON itself has nowhere to put it.
        context.Response.Headers["Content-Crs"] = $"<{request!.CrsUri}>";

        await StreamAsync(context, "ogc", () => writer.WriteAsync(
            context.Response.Body,
            source.ReadAsync(features!, cancellation),
            matched,
            request,
            query,
            cancellation)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a streaming write so that a failure partway through is recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because nothing was recording it.</b> Once the response has started, ASP.NET's
    /// exception-handler middleware declines — it logs *the response has already started,
    /// the error handler will not be executed* and stops — so
    /// <see cref="ErrorResponse.WriteAsync"/> never runs and its
    /// <see cref="ErrorResponse.LogTruncated"/> branch is unreachable from here. The
    /// second failure gate hung up on twenty requests and found **no line at all** in
    /// either the access log or the failure log for this face. An operator investigating
    /// client-reported truncation had nothing to read.
    /// </para>
    /// <para>
    /// <b>Abort rather than finish the document.</b> The bytes are past recall, so the
    /// only question is whether the client learns that what it has is incomplete. A
    /// truncated transfer it can detect; a well-formed document that ends early it
    /// cannot — which is exactly how a bad `srsName` came to return a WFS collection
    /// announcing a thousand features and carrying none.
    /// </para>
    /// </remarks>
    private static async Task StreamAsync(
        HttpContext context, string name, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception e) when (context.Response.HasStarted)
        {
            ErrorResponse.LogTruncated(
                context,
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(name),
                e);

            context.Abort();
        }
    }

    private static async Task ItemAsync(
        HttpContext context,
        string collectionId,
        string featureId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection, bool refused) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

        // Refused, not missing: the catalogue could not be listed, and *no such collection*
        // would be a claim about one that probably exists.
        if (refused)
        {
            return;
        }

        if (layer is null || collection is null)
        {
            await RefuseAsync(context, Missing(collectionId)).ConfigureAwait(false);
            return;
        }

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        int srid = Graticula.Geometries.AxisOrder.Wgs84;
        string crsUri = OgcNames.Crs84;
        bool latitudeFirst = false;

        if (context.Request.Query["crs"] is { Count: > 0 } asked
            && !string.IsNullOrWhiteSpace(asked.ToString()))
        {
            if (OgcNames.SridOf(asked.ToString(), out latitudeFirst) is not { } code
                || !collection.CoordinateSystems.Contains(asked.ToString(), StringComparer.Ordinal))
            {
                await RefuseAsync(context, OgcProblem.BadRequest(
                    $"`crs={asked}` is not one of `{collection.Id}`'s reference systems."))
                    .ConfigureAwait(false);

                return;
            }

            srid = code;
            crsUri = asked.ToString();
        }

        if (!TryIdentity(layer, described, featureId, out AttributePredicate? predicate, out OgcProblem? problem)
            || !PredicateSql.TryEmit(
                predicate!,
                [.. described.Fields.Select(f => f.Name)],
                LayerDefinition.Quote,
                out ParsedWhere where,
                out _))
        {
            await RefuseAsync(
                context,
                problem ?? OgcProblem.NotFound(
                    $"`{featureId}` is not a feature of `{collection.Id}`."))
                .ConfigureAwait(false);

            return;
        }

        FeatureQuery query = new(
            limit: 1,
            fields: [.. described.Fields.Select(f => f.Name)],
            includeGeometry: true,
            outSrid: srid == layer.Definition.Srid ? null : srid,
            where: where);

        await foreach (Feature feature in source.ReadAsync(query, cancellation).ConfigureAwait(false))
        {
            string root = Origin(context) + OgcNames.Base;
            string collectionUrl = $"{root}/collections/{Uri.EscapeDataString(collection.Id)}";

            string document = OgcFeatureWriter.WriteOne(
                feature,
                latitudeFirst: latitudeFirst,
                links:
                [
                    new OgcDocuments.Link(
                        Origin(context) + context.Request.Path, "self", OgcNames.GeoJson,
                        collection.Title),
                    new OgcDocuments.Link(
                        Origin(context) + context.Request.Path + "?f=html", "alternate",
                        OgcNames.Html, "This feature as HTML"),
                    new OgcDocuments.Link(
                        collectionUrl, "collection", OgcNames.Json, collection.Title),
                ]);

            if (WantsHtml(context))
            {
                await Results.Content(
                    OgcHtml.Document(context.Request.Path, featureId, document),
                    "text/html; charset=utf-8")
                    .ExecuteAsync(context).ConfigureAwait(false);

                return;
            }

            context.Response.ContentType = OgcNames.GeoJson;
            context.Response.Headers["Content-Crs"] = $"<{crsUri}>";

            await context.Response.WriteAsync(document, cancellation).ConfigureAwait(false);
            return;
        }

        await RefuseAsync(
            context,
            OgcProblem.NotFound($"`{featureId}` is not a feature of `{collection.Id}`."))
            .ConfigureAwait(false);
    }

    // ---------- the catalogue ----------

    private static OgcProblem Missing(string collectionId) =>
        OgcProblem.NotFound(
            $"`{collectionId}` is not a collection this server publishes to you.");

    private static async Task<IReadOnlyList<PublishedLayer>?> VisibleAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        CatalogListing listing =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        // <b>Null is not empty.</b> A collections document with no collections says this
        // server publishes none, and a 404 about one says it never had it. Neither is true
        // during an outage. [D-127](../../docs/architecture-debt.md).
        if (listing.Services is not { } services)
        {
            await RefuseAsync(
                    context,
                    OgcProblem.Unavailable(
                        "The catalogue is not reachable and this server has no remembered "
                        + "listing to answer from, so it cannot say which collections it "
                        + "publishes. Retry shortly; see /healthz/ready."))
                .ConfigureAwait(false);

            return null;
        }

        if (listing.Blind)
        {
            ServiceLookup.SayAge(context, listing.Age);
        }

        List<PublishedLayer> layers = [];

        foreach (PublishedService service in services)
        {
            if ((!service.IsRunning && !seesStopped)
                || !service.Limits.AllowsFeatures(dataSupportsIt: true))
            {
                continue;
            }

            if (!LayerAccess
                .Evaluate(
                    service.Sharing,
                    service.Owner,
                    current.Principal,
                    current.Authorization,
                    service.SharedWith)
                .IsAllowed())
            {
                continue;
            }

            foreach (PublishedLayer layer in service.Layers)
            {
                if (layer.Definition.GeometryColumn is { Length: > 0 })
                {
                    layers.Add(layer);
                }
            }
        }

        return layers;
    }

    /// <summary>The collection at this id, or why there is none.</summary>
    /// <remarks>
    /// <b><c>Refused</c> is a third answer and it is not the same as not found.</b> Without it
    /// an unreadable catalogue would arrive at the caller as <c>(null, null)</c> and be reported
    /// as *no such collection*, which is a claim this server is in no position to make.
    /// [D-127](../../docs/architecture-debt.md).
    /// </remarks>
    private static async Task<(PublishedLayer? Layer, CollectionMetadata? Collection, bool Refused)>
        FindAsync(
            HttpContext context,
            CatalogFallback catalog,
            ServiceContexts contexts,
            IProjector projector,
            string collectionId,
            CancellationToken cancellation)
    {
        if (await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false)
            is not { } visible)
        {
            return (null, null, true);
        }

        foreach (PublishedLayer layer in visible)
        {
            if (!string.Equals(layer.Definition.Name, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (
                layer,
                await DescribeAsync(contexts, projector, layer, cancellation).ConfigureAwait(false),
                false);
        }

        return (null, null, false);
    }

    /// <summary>Every collection, or null when the catalogue could not be listed.</summary>
    private static async Task<List<CollectionMetadata>?> DescribeAllAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        if (await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false)
            is not { } visible)
        {
            return null;
        }

        List<CollectionMetadata> collections = [];

        foreach (PublishedLayer layer in visible)
        {
            collections.Add(
                await DescribeAsync(contexts, projector, layer, cancellation).ConfigureAwait(false));
        }

        return collections;
    }

    /// <summary>
    /// One layer as a collection, with its extent in WGS 84.
    /// </summary>
    /// <remarks>
    /// <b>The extent is required to be in CRS84 whatever the layer is stored in</b>,
    /// which is Part 1 §7.13's rule and the reason this needs a projector where the
    /// WFS face did not. Reprojection is the database's.
    /// </remarks>
    private static async Task<CollectionMetadata> DescribeAsync(
        ServiceContexts contexts,
        IProjector projector,
        PublishedLayer layer,
        CancellationToken cancellation)
    {
        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        Envelope? geographic = await GeographicAsync(
            projector, described.Extent, layer.Definition.Srid, cancellation).ConfigureAwait(false);

        // <b>The same measurement the WMS face makes, from the same cache.</b> This
        // reported the column and left the interval empty, so a collection document
        // said the collection had no time while its own `datetime` filter worked on
        // it — and a client reads the document to decide whether to ask.
        Graticula.Api.Wms.TimeDimension? time = await WmsEndpoints
            .TimeOfAsync(source, layer, described, contexts, cancellation).ConfigureAwait(false);

        return new CollectionMetadata(
            layer.Definition.Name,
            layer.Folder is { Length: > 0 } folder
                ? $"{folder}/{layer.ServiceName} — {layer.Definition.Name}"
                : $"{layer.ServiceName} — {layer.Definition.Name}",
            Description: null,
            layer.Definition.Srid,
            layer.GeometryType,
            geographic,
            described.Fields,
            time?.Field,
            time?.From,
            time?.Until);
    }

    private static async Task<Envelope?> GeographicAsync(
        IProjector projector, Envelope? extent, int srid, CancellationToken cancellation)
    {
        if (extent is not { IsEmpty: false } box)
        {
            return null;
        }

        if (srid == Graticula.Geometries.AxisOrder.Wgs84)
        {
            return Rounded(box);
        }

        try
        {
            (IReadOnlyList<Geometry> projected, _) = await projector.ProjectAsync(
                [
                    new Graticula.Geometries.Point(box.MinX, box.MinY),
                    new Graticula.Geometries.Point(box.MaxX, box.MinY),
                    new Graticula.Geometries.Point(box.MaxX, box.MaxY),
                    new Graticula.Geometries.Point(box.MinX, box.MaxY),
                ],
                srid,
                Graticula.Geometries.AxisOrder.Wgs84,
                cancellation).ConfigureAwait(false);

            Envelope whole = Envelope.Empty;

            foreach (Geometry corner in projected)
            {
                whole = whole.IsEmpty ? corner.Envelope : whole.Union(corner.Envelope);
            }

            return whole.IsEmpty ? null : Rounded(whole);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A CRS this deployment cannot transform leaves the collection without a
            // geographic extent rather than without a document.
            return null;
        }
    }

    // ---------- the query ----------

    private static bool TryQuery(
        OgcRequest request,
        PublishedLayer layer,
        LayerDescription described,
        CollectionMetadata collection,
        out FeatureQuery? query,
        out bool empty,
        out OgcProblem? problem)
    {
        query = null;
        empty = false;
        problem = null;

        List<AttributePredicate> clauses = [];

        if (request.HasDateTime)
        {
            // Q-129: the layer's declared column when it has one, the derivation when not.
            if (Graticula.Api.Wms.TimeDimension.FieldOf(described.Fields, layer.TimeField)
                is not { } field)
            {
                problem = OgcProblem.BadRequest(
                    $"`{layer.Definition.Name}` has no time to filter by. A collection is "
                    + "temporal when it has exactly one date column; this one has none, or "
                    + "several with no way to tell which carries the phenomenon time.");

                return false;
            }

            if (request.From is { } from)
            {
                clauses.Add(new AttributePredicate.Comparison(
                    field, ComparisonOperator.GreaterThanOrEqual, from));
            }

            if (request.Until is { } until)
            {
                clauses.Add(new AttributePredicate.Comparison(
                    field, ComparisonOperator.LessThan, until));
            }
        }

        foreach (KeyValuePair<string, string> property in request.Properties)
        {
            if (!TryValue(described, property.Key, property.Value, out object? value, out problem))
            {
                return false;
            }

            clauses.Add(new AttributePredicate.Comparison(
                property.Key, ComparisonOperator.Equal, value));
        }

        ParsedWhere? where = null;

        if (clauses.Count > 0)
        {
            AttributePredicate predicate = clauses[0];

            for (int i = 1; i < clauses.Count; i++)
            {
                predicate = new AttributePredicate.Conjunction(predicate, clauses[i]);
            }

            if (!PredicateSql.TryEmit(
                    predicate,
                    [.. described.Fields.Select(f => f.Name)],
                    LayerDefinition.Quote,
                    out ParsedWhere emitted,
                    out string? error))
            {
                problem = OgcProblem.BadRequest(error ?? "The filter could not be compiled.");
                return false;
            }

            where = emitted;
        }

        SpatialFilter? spatial = null;
        int? filterSrid = null;

        if (request.Bbox is { } bbox)
        {
            List<Polygon> boxes = [];

            foreach (Envelope? part in (Envelope?[])[bbox, request.BboxEast])
            {
                if (part is not { } box)
                {
                    continue;
                }

                if (Clamped(box, request.BboxSrid, collection) is { } usable)
                {
                    boxes.Add(Rectangle(
                        Padded(usable, request.BboxSrid)));
                }
            }

            if (boxes.Count == 0)
            {
                // The filter and the data do not meet at all. Answering nothing is
                // the same result the query would give, without the round trip.
                empty = true;
            }

            spatial = new SpatialFilter(
                boxes.Count == 1 ? boxes[0] : new MultiPolygon(boxes));

            filterSrid = request.BboxSrid == layer.Definition.Srid ? null : request.BboxSrid;
        }

        query = new FeatureQuery(
            limit: request.Limit,
            fields: [.. described.Fields.Select(f => f.Name)],
            offset: request.Offset,
            includeGeometry: true,

            // <b>Paging by offset needs a stable order and the client does not have
            // to ask for one.</b> Without it a feature inserted between page one and
            // page two pushes another off the end of page two, where nobody sees it.
            // The identity column is the order every layer has.
            orderBy: [new Graticula.Features.SortKey(layer.Definition.IdentityColumn, Descending: false)],
            spatial: spatial,
            outSrid: request.Srid == layer.Definition.Srid ? null : request.Srid,
            where: where,
            filterSrid: filterSrid);

        return true;
    }

    /// <summary>
    /// A property filter's value, converted to the column's own type.
    /// </summary>
    /// <remarks>
    /// <b>Converted rather than compared as text.</b> A text comparison against an
    /// integer column is an error at the database rather than an empty result, and
    /// the message it produces names a type nobody asked about.
    /// </remarks>
    private static bool TryValue(
        LayerDescription described,
        string column,
        string text,
        out object? value,
        out OgcProblem? problem)
    {
        value = text;
        problem = null;

        FieldDescription? field = described.Find(column);

        if (field is not { } description)
        {
            problem = OgcProblem.BadRequest($"`{column}` is not a property of this collection.");
            return false;
        }

        switch (description.Type)
        {
            case FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger:
                if (!long.TryParse(
                    text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    problem = OgcProblem.BadRequest($"`{column}={text}` is not a whole number.");
                    return false;
                }

                value = whole;
                return true;

            case FieldType.Single or FieldType.Double:
                if (!double.TryParse(
                    text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    problem = OgcProblem.BadRequest($"`{column}={text}` is not a number.");
                    return false;
                }

                value = number;
                return true;

            case FieldType.Boolean:
                if (!bool.TryParse(text, out bool flag))
                {
                    problem = OgcProblem.BadRequest($"`{column}={text}` is not true or false.");
                    return false;
                }

                value = flag;
                return true;

            case FieldType.Date:
                if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset moment))
                {
                    problem = OgcProblem.BadRequest($"`{column}={text}` is not a date.");
                    return false;
                }

                value = moment;
                return true;

            case FieldType.Guid:
                if (!Guid.TryParse(text, out Guid id))
                {
                    problem = OgcProblem.BadRequest($"`{column}={text}` is not a uuid.");
                    return false;
                }

                value = id;
                return true;

            case FieldType.Binary:
                problem = OgcProblem.BadRequest(
                    $"`{column}` holds bytes, and a byte column is not something a query string "
                    + "can compare against.");

                return false;

            default:
                return true;
        }
    }

    private static bool TryIdentity(
        PublishedLayer layer,
        LayerDescription described,
        string featureId,
        out AttributePredicate? predicate,
        out OgcProblem? problem)
    {
        predicate = null;
        problem = null;

        string column = layer.Definition.IdentityColumn;

        if (!TryValue(described, column, featureId, out object? value, out _))
        {
            // A malformed identifier is *not found* rather than *bad request*: the
            // path names a resource, and a resource named by an unusable identifier
            // is one that does not exist.
            problem = OgcProblem.NotFound(
                $"`{featureId}` is not an identifier of `{layer.Definition.Name}`.");

            return false;
        }

        predicate = new AttributePredicate.Comparison(column, ComparisonOperator.Equal, value);
        return true;
    }

    /// <summary>An extent rounded outward to a millionth of a degree.</summary>
    /// <remarks>
    /// <para>
    /// <b>A published extent has to contain its own data, and this one did not.</b>
    /// For a layer stored in a projected reference system the extent is the hull of
    /// its corners transformed into CRS84, and a client that sends that box straight
    /// back gets it transformed the other way. The two disagree in the last few
    /// digits, so every feature lands exactly on an edge — and three of five layers
    /// answered a client's own extent with an empty collection. Found by the CITE
    /// suite, which asks precisely that.
    /// </para>
    /// <para>
    /// <b>Six decimals is about a tenth of a metre.</b> Far below anything this
    /// server stores, and orders of magnitude above a projection's round-trip error,
    /// so the rounded box strictly contains the measured one. Part 1 §7.13 asks for
    /// the extent and does not ask for it to be tight: an extent is an upper bound,
    /// and a slightly generous one costs a client nothing while an exact one that
    /// excludes its own features costs it everything.
    /// </para>
    /// <para>
    /// <b>Here rather than where the document is written, and that distinction was
    /// measured rather than reasoned.</b> Rounding at the point of printing left
    /// <c>Clamped</c> intersecting the request with the *un*rounded extent, which
    /// erased the rounding before the transformation and put back the exact defect
    /// this repairs — the six-feature layer answered its own extent with four. **The
    /// invariant is that the extent a client is given is the extent the server clamps
    /// to**, and one number satisfies it where two do not.
    /// </para>
    /// <para>
    /// <b>Applied to a CRS84 layer as well, where it changes nothing.</b> That box
    /// needs no transformation and is already exact; one rule that is harmlessly
    /// generous beats two rules that have to be told apart.
    /// </para>
    /// <para>
    /// <b>Q-133 chose this over widening the filter</b>
    /// ([ADR-042](../../docs/adr/ADR-042-ogc-api-features.md) §7). See
    /// <see cref="Padded"/> for what the filter no longer does and why.
    /// </para>
    /// </remarks>
    private static Envelope Rounded(Envelope box)
    {
        const double Scale = 1_000_000;

        return new Envelope(
            Math.Floor(box.MinX * Scale) / Scale,
            Math.Floor(box.MinY * Scale) / Scale,
            Math.Ceiling(box.MaxX * Scale) / Scale,
            Math.Ceiling(box.MaxY * Scale) / Scale);
    }

    /// <summary>
    /// A bounding box with a degenerate axis given something to intersect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A zero-area box is an equality test on a coordinate, and no stored
    /// coordinate survives one.</b> A client asking for a point, or for a line of
    /// longitude, means *what is here*, and an exact comparison against a stored
    /// double answers *nothing* however close the data is. So an axis with no width
    /// is given a micro-degree, or a centimetre in a projected system — far below
    /// anything this server stores and far above a projection's round-trip error.
    /// </para>
    /// <para>
    /// <b>What this used to do as well, and no longer does: widen every box that had
    /// to be transformed.</b> That was the repair for a real defect — a client sending
    /// a collection's own published extent back as its <c>bbox</c> got **nothing**
    /// from a layer stored in another reference system, because the extent was made by
    /// projecting the data one way and the filter is projected back the other, so
    /// every feature sat exactly on an edge and every edge test failed. It worked, and
    /// it made the same box mean two different things depending on which reference
    /// system it was written in: the CITE suite's <c>verifyBboxCrsParameter</c> sends
    /// both spellings and requires the same features, and a feature within ten
    /// centimetres of the edge came back for one and not the other.
    /// </para>
    /// <para>
    /// <b>Q-133 chose the other repair, and it is
    /// [ADR-042](../../docs/adr/ADR-042-ogc-api-features.md) §7.</b> The defect was
    /// never really in the filter: it was that the *published extent* did not contain
    /// its own data once round-tripped. So the extent is published rounded outward
    /// (<see cref="Rounded"/>) and the filter is compared exactly in every
    /// reference system. A client sending the extent back is answered from a box that
    /// provably contains every feature; a client sending any other box gets an exact
    /// comparison, which is the only kind that means the same thing twice.
    /// </para>
    /// <para>
    /// <b>The alternative was to widen both spellings.</b> It also makes them agree,
    /// and it does it by making every filter in the product ten centimetres wrong —
    /// a false positive on a feature outside the box the client asked for, in the one
    /// operation a client uses to decide what is inside something.
    /// </para>
    /// </remarks>
    private static Envelope Padded(Envelope box, int bboxSrid)
    {
        double epsilon = Graticula.Geometries.AxisOrder.IsGeographic(bboxSrid) ? 0.000001 : 0.01;

        double x = box.Width <= 0 ? epsilon : 0;
        double y = box.Height <= 0 ? epsilon : 0;

        return x == 0 && y == 0
            ? box
            : new Envelope(box.MinX - x, box.MinY - y, box.MaxX + x, box.MaxY + y);
    }

    /// <summary>
    /// A bounding box narrowed to where the data actually is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because a valid request produced a 503, and the reason is
    /// worth stating exactly.</b> The CITE suite asks for
    /// <c>bbox=-180,-90,180,-85</c>, which is legitimate in CRS84 and untransformable
    /// into Web Mercator: EPSG:3857 is undefined below about −85.06°, and PostGIS
    /// answers <c>transform: tolerance condition error</c> rather than an empty
    /// result. The client did nothing wrong, so a refusal would be a wrong answer
    /// too.
    /// </para>
    /// <para>
    /// <b>Intersecting the filter with the collection's own extent cannot change
    /// which features match</b> — nothing lies outside its own extent — and the
    /// result is by construction transformable, because its corners came from the
    /// layer's own data. That is general: it needs no table of areas of use and no
    /// special case for Mercator.
    /// </para>
    /// <para>
    /// <b>Only for geographic filters.</b> A box already in the layer's CRS needs no
    /// transformation, and the collection's extent is published in CRS84, so this
    /// can only narrow a box that is in CRS84 to begin with.
    /// </para>
    /// </remarks>
    private static Envelope? Clamped(Envelope box, int bboxSrid, CollectionMetadata collection)
    {
        if (bboxSrid != Graticula.Geometries.AxisOrder.Wgs84
            || collection.Extent is not { IsEmpty: false } extent)
        {
            return box;
        }

        double minX = Math.Max(box.MinX, extent.MinX);
        double minY = Math.Max(box.MinY, extent.MinY);
        double maxX = Math.Min(box.MaxX, extent.MaxX);
        double maxY = Math.Min(box.MaxY, extent.MaxY);

        // Disjoint. A zero-area overlap is kept: two things can touch along an edge,
        // and a degenerate extent is exactly what two of this server's layers have.
        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        return new Envelope(minX, minY, maxX, maxY);
    }

    private static Polygon Rectangle(Envelope extent) =>
        new(new LinearRing(XySequence.Wrap(
        [
            extent.MinX, extent.MinY,
            extent.MaxX, extent.MinY,
            extent.MaxX, extent.MaxY,
            extent.MinX, extent.MaxY,
            extent.MinX, extent.MinY,
        ])));
}
