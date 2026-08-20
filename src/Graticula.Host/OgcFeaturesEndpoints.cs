using System;
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
internal static class OgcFeaturesEndpoints
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
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        List<CollectionMetadata> collections =
            await DescribeAllAsync(context, catalog, contexts, projector, cancellation)
                .ConfigureAwait(false);

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
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

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
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

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

        OgcFeatureWriter writer = new(collection, self);

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

        await writer.WriteAsync(
            context.Response.Body,
            source.ReadAsync(features!, cancellation),
            matched,
            request,
            query,
            cancellation).ConfigureAwait(false);
    }

    private static async Task ItemAsync(
        HttpContext context,
        string collectionId,
        string featureId,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        (PublishedLayer? layer, CollectionMetadata? collection) =
            await FindAsync(context, catalog, contexts, projector, collectionId, cancellation)
                .ConfigureAwait(false);

        if (layer is null || collection is null)
        {
            await RefuseAsync(context, Missing(collectionId)).ConfigureAwait(false);
            return;
        }

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        int srid = Graticula.Geometries.AxisOrder.Wgs84;
        string crsUri = OgcNames.Crs84;

        if (context.Request.Query["crs"] is { Count: > 0 } asked
            && !string.IsNullOrWhiteSpace(asked.ToString()))
        {
            if (OgcNames.SridOf(asked.ToString(), out _) is not { } code
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

    private static async Task<IReadOnlyList<PublishedLayer>> VisibleAsync(
        HttpContext context, PostgresLayerCatalog catalog, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        IReadOnlyList<PublishedService> services =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

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

    private static async Task<(PublishedLayer? Layer, CollectionMetadata? Collection)> FindAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        string collectionId,
        CancellationToken cancellation)
    {
        foreach (PublishedLayer layer in
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false))
        {
            if (!string.Equals(layer.Definition.Name, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (
                layer,
                await DescribeAsync(contexts, projector, layer, cancellation).ConfigureAwait(false));
        }

        return (null, null);
    }

    private static async Task<List<CollectionMetadata>> DescribeAllAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        CancellationToken cancellation)
    {
        List<CollectionMetadata> collections = [];

        foreach (PublishedLayer layer in
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false))
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
        (_, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        Envelope? geographic = await GeographicAsync(
            projector, described.Extent, layer.Definition.Srid, cancellation).ConfigureAwait(false);

        string? temporal = Graticula.Api.Wms.TimeDimension.FieldOf(described.Fields);

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
            temporal);
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
            return box;
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

            return whole.IsEmpty ? null : whole;
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
            if (Graticula.Api.Wms.TimeDimension.FieldOf(described.Fields) is not { } field)
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
                        Widened(usable, request.BboxSrid, layer.Definition.Srid)));
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

    /// <summary>
    /// A bounding box widened by the error a coordinate transformation carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An exact edge is not a test a transformed coordinate can pass.</b> A client
    /// that sends a collection's own published extent as its <c>bbox</c> is asking
    /// the one query guaranteed to match everything — and against a layer stored in
    /// another reference system it matched **nothing**, because the extent was
    /// produced by projecting the data one way and the filter is projected back the
    /// other, and the two disagree in the last few digits. Every feature sits exactly
    /// on an edge, and every edge test fails.
    /// </para>
    /// <para>
    /// <b>Found by the CITE suite</b>, which asks precisely that question, and it is
    /// the third defect this dataset's shape has produced today: the WMS face had to
    /// pad degenerate extents for a related reason.
    /// </para>
    /// <para>
    /// <b>A micro-degree, or a centimetre — and only where a transformation
    /// happens.</b> Ten centimetres is far below anything this server stores and far
    /// above a projection's round-trip error, which is the whole range the epsilon
    /// has to sit in. A box already in the layer's own reference system is compared
    /// exactly, because nothing has moved. A degenerate axis is widened either way:
    /// a zero-area box is an equality test on a coordinate, and no stored coordinate
    /// survives one.
    /// </para>
    /// </remarks>
    private static Envelope Widened(Envelope box, int bboxSrid, int layerSrid)
    {
        bool geographic = bboxSrid is 4326 or 4258 or 4269;
        bool transforming = bboxSrid != layerSrid;

        double epsilon = geographic ? 0.000001 : 0.01;

        double x = transforming || box.Width <= 0 ? epsilon : 0;
        double y = transforming || box.Height <= 0 ? epsilon : 0;

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
