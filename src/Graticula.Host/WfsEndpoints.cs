using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.Wfs;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using SortKey = Graticula.Features.SortKey;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Graticula.Host;

/// <summary>
/// The WFS 2.0 surface — one endpoint, five operations, read only.
/// </summary>
/// <remarks>
/// <para>
/// <b>One URL for the whole server</b>
/// ([ADR-039](../../docs/adr/ADR-039-wfs-is-the-first-surface-after-v1.md) §5). A
/// WFS client's model is a single service address whose capabilities list the
/// feature types, so folders become namespace prefixes inside it rather than
/// separate endpoints. The address an administrator pastes is the same one for
/// every layer, which is the property <c>/rest/services</c> has and a per-service
/// endpoint would lose.
/// </para>
/// <para>
/// <b>The catalogue filters; it does not refuse.</b> An anonymous caller sees the
/// public feature types and nothing else — the same rule the services directory
/// follows, and the reason this route is
/// <see cref="SharingGovernedExtensions.ByFiltering"/> rather than by service.
/// Asking for a type the caller may not see is answered as *no such type*, which
/// is what a caller who cannot see it should observe.
/// </para>
/// </remarks>
internal static class WfsEndpoints
{
    /// <summary>Where the surface lives.</summary>
    public const string Path = "/wfs";

    /// <summary>
    /// The link a REST directory page offers to this surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in the renderer, because this class owns the URL
    /// shape.</b> A directory page that builds a WFS request out of string
    /// fragments is a second description of this surface, and the two drift the
    /// first time a parameter is renamed.
    /// </para>
    /// <para>
    /// <b>The service page gets the capabilities and a layer page gets its own
    /// schema.</b> Capabilities on a layer page would say nothing about the layer,
    /// and the schema is the layer-scoped document that costs no data to fetch —
    /// unlike GetFeature, which a person clicking a link in a browser has not
    /// asked to download.
    /// </para>
    /// </remarks>
    /// <param name="layerName">
    /// The layer's name, which is also its WFS type name, or null for the service.
    /// </param>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink(string? layerName) =>
        layerName is null
            ? ("WFS", $"{Path}?service={WfsNames.Service}&request=GetCapabilities")
            : ("WFS",
                $"{Path}?service={WfsNames.Service}&version={WfsNames.Version}"
                + "&request=DescribeFeatureType&typeNames="
                + Uri.EscapeDataString($"{WfsNames.Prefix}:{layerName}"));

    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, GetAsync).Governed(SharingGovernedExtensions.ByFiltering);
        app.MapPost(Path, PostAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    private static async Task GetAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        HostSettings settings,
        CancellationToken cancellation)
    {
        Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair
            in context.Request.Query)
        {
            parameters[pair.Key] = pair.Value.ToString();
        }

        await DispatchAsync(context, catalog, contexts, settings, parameters, cancellation)
            .ConfigureAwait(false);
    }

    private static async Task PostAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        HostSettings settings,
        CancellationToken cancellation)
    {
        // <b>Buffered first, and bounded while buffering.</b> Kestrel refuses
        // synchronous reads on a request body and the XML reader reads
        // synchronously, so the two cannot meet on a live stream — found by
        // POSTing to the running server, where it was a 500 rather than a
        // refusal. Copying it also gives the byte ceiling somewhere to be
        // enforced: an unbounded copy from an unauthenticated caller is a way to
        // exhaust this process.
        using System.IO.MemoryStream body = new();

        long copied = await CopyBoundedAsync(context.Request.Body, body, cancellation)
            .ConfigureAwait(false);

        if (copied > SafeXml.MaximumRequestBytes)
        {
            await RefuseAsync(
                    context,
                    WfsFault.Invalid(
                        "request",
                        $"The request body is larger than {SafeXml.MaximumRequestBytes} bytes. A "
                        + "filter that long is usually generated; send the identifiers in a "
                        + "ResourceId list instead, which is bounded and indexed."),
                    cancellation)
                .ConfigureAwait(false);

            return;
        }

        body.Position = 0;

        if (!WfsXmlRequest.TryRead(
                body,
                out IReadOnlyDictionary<string, string> parameters,
                out WfsFault? fault))
        {
            await RefuseAsync(context, fault!, cancellation).ConfigureAwait(false);
            return;
        }

        await DispatchAsync(context, catalog, contexts, settings, parameters, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a body, stopping one byte past the ceiling so the caller can tell.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>CopyToAsync</c>, which has no ceiling.</b> Reading the whole body
    /// and then checking its length is the check arriving after the cost it was
    /// meant to prevent.
    /// </remarks>
    private static async Task<long> CopyBoundedAsync(
        System.IO.Stream from, System.IO.Stream into, CancellationToken cancellation)
    {
        byte[] buffer = new byte[16 * 1024];
        long total = 0;

        while (total <= SafeXml.MaximumRequestBytes)
        {
            int read = await from.ReadAsync(buffer, cancellation).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;

            await into.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
        }

        return total;
    }

    private static async Task DispatchAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        HostSettings settings,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellation)
    {
        if (!WfsRequest.TryParse(parameters, out WfsRequest? request, out WfsFault? fault))
        {
            await RefuseAsync(context, fault!, cancellation).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<PublishedLayer> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        switch (request!.Operation)
        {
            case WfsOperation.GetCapabilities:
                await CapabilitiesAsync(context, contexts, visible, cancellation)
                    .ConfigureAwait(false);
                return;

            case WfsOperation.ListStoredQueries:
            case WfsOperation.DescribeStoredQueries:
                await StoredQueriesAsync(context, request, visible, cancellation)
                    .ConfigureAwait(false);
                return;

            case WfsOperation.DescribeFeatureType:
                await SchemaAsync(context, contexts, request, visible, cancellation)
                    .ConfigureAwait(false);
                return;

            case WfsOperation.GetFeature:
            case WfsOperation.GetPropertyValue:
                await FeaturesAsync(context, contexts, settings, request, visible, cancellation)
                    .ConfigureAwait(false);
                return;

            default:
                await RefuseAsync(
                        context,
                        new WfsFault(
                            WfsFaultCode.OperationNotSupported,
                            "request",
                            $"'{request.Operation}' is not implemented."),
                        cancellation)
                    .ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Every layer this caller may read, from every running service.
    /// </summary>
    /// <remarks>
    /// <b>The same three checks the rest of the server applies, in the same
    /// order.</b> A stopped service is invisible unless the caller may manage the
    /// server, sharing is evaluated with <see cref="LayerAccess"/> rather than
    /// re-implemented, and a layer with no integer object id is included — WFS has
    /// no such requirement, which is the asymmetry
    /// [Q-57](../../docs/open-questions.md) recorded and this surface is the first
    /// to benefit from.
    /// </remarks>
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
            if (!service.IsRunning && !seesStopped)
            {
                continue;
            }

            // <b>A face switched off is switched off on every door.</b> Found
            // 2026-08-20 while adding the WFS link to the REST directory: a
            // service with `ServesFeatures` false answers 404 at
            // `/rest/services/…/FeatureServer` (ADR-031 condition 2, asserted in
            // both `ServiceLookup.LayerAsync` and the service document) and this
            // surface read its layers anyway. That is not a WFS decision — it is
            // the ArcGIS decision, unenforced, and a second protocol quietly
            // reopening a door the operator closed is exactly the failure a new
            // surface is most likely to introduce. D-123.
            if (!service.Limits.AllowsFeatures(dataSupportsIt: true))
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

            layers.AddRange(service.Layers);
        }

        return layers;
    }

    /// <summary>
    /// Turns a layer into a feature type, describing it only when asked to.
    /// </summary>
    /// <remarks>
    /// <b>The describe is what makes this expensive, so the catalogue-wide
    /// document does not do it.</b> A shape costs a round trip on a cold layer
    /// (D-17 measured 4–6 ms), and at the stated scale of 100–1,000 services a
    /// capabilities document that described every layer would open a thousand of
    /// them to produce one page. So the fields arrive empty for the listing and
    /// full for the two operations that need them.
    /// </remarks>
    private static async Task<WfsFeatureType> TypeOfAsync(
        ServiceContexts contexts,
        PublishedLayer layer,
        bool describe,
        CancellationToken cancellation)
    {
        IReadOnlyList<FieldDescription> fields = [];
        Graticula.Geometries.Envelope? extent = null;

        if (describe)
        {
            (_, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            fields = described.Fields;
            extent = described.Extent;
        }

        return new WfsFeatureType(
            layer.Definition.Name,
            TitleOf(layer),
            Abstract: null,
            layer.Definition.Srid,
            layer.GeometryType,
            layer.Definition.GeometryColumn,
            fields,
            extent);
    }

    private static async Task CapabilitiesAsync(
        HttpContext context,
        ServiceContexts contexts,
        IReadOnlyList<PublishedLayer> visible,
        CancellationToken cancellation)
    {
        List<WfsFeatureType> types = [];

        foreach (PublishedLayer layer in visible)
        {
            // Described only where the extent could be published: the bounding box
            // this document carries is WGS 84 by definition, and a layer in another
            // reference would need a projection to produce one. See the writer,
            // and Q-125 for the general case.
            types.Add(await TypeOfAsync(
                    contexts, layer, describe: layer.Definition.Srid == 4326, cancellation)
                .ConfigureAwait(false));
        }

        context.Response.ContentType = "text/xml; charset=utf-8";

        await CapabilitiesDocument
            .WriteAsync(
                context.Response.Body,
                Endpoint(context),
                "Graticula",
                Ordered(types),
                cancellation)
            .ConfigureAwait(false);
    }

    private static async Task StoredQueriesAsync(
        HttpContext context,
        WfsRequest request,
        IReadOnlyList<PublishedLayer> visible,
        CancellationToken cancellation)
    {
        List<WfsFeatureType> types =
        [
            .. visible.Select(layer => new WfsFeatureType(
                layer.Definition.Name,
                TitleOf(layer),
                null,
                layer.Definition.Srid,
                layer.GeometryType,
                layer.Definition.GeometryColumn,
                [],
                null)),
        ];

        context.Response.ContentType = "text/xml; charset=utf-8";

        if (request.Operation == WfsOperation.ListStoredQueries)
        {
            await StoredQueries.WriteListAsync(context.Response.Body, Ordered(types), cancellation)
                .ConfigureAwait(false);

            return;
        }

        await StoredQueries
            .WriteDescriptionAsync(context.Response.Body, Ordered(types), cancellation)
            .ConfigureAwait(false);
    }

    private static async Task SchemaAsync(
        HttpContext context,
        ServiceContexts contexts,
        WfsRequest request,
        IReadOnlyList<PublishedLayer> visible,
        CancellationToken cancellation)
    {
        List<PublishedLayer> wanted = [];

        if (request.TypeNames.Count == 0)
        {
            wanted.AddRange(visible);
        }
        else
        {
            foreach (string name in request.TypeNames)
            {
                if (!TryFind(visible, name, request.Namespaces, out PublishedLayer? layer, out WfsFault? fault))
                {
                    await RefuseAsync(context, fault!, cancellation).ConfigureAwait(false);
                    return;
                }

                wanted.Add(layer!);
            }
        }

        List<WfsFeatureType> types = [];

        foreach (PublishedLayer layer in wanted)
        {
            types.Add(await TypeOfAsync(contexts, layer, describe: true, cancellation)
                .ConfigureAwait(false));
        }

        context.Response.ContentType = "text/xml; charset=utf-8";

        await FeatureTypeSchema
            .WriteAsync(context.Response.Body, Ordered(types), cancellation)
            .ConfigureAwait(false);
    }

    private static async Task FeaturesAsync(
        HttpContext context,
        ServiceContexts contexts,
        HostSettings settings,
        WfsRequest request,
        IReadOnlyList<PublishedLayer> visible,
        CancellationToken cancellation)
    {
        IReadOnlyList<string> resourceIds = request.ResourceIds;
        IReadOnlyList<string> typeNames = request.TypeNames;

        // <b>The stored query, which is the whole of Simple WFS's query surface.</b>
        // GetFeatureById takes one identifier of the form the writer produced, so
        // the type it names is the type to read and the rest is the identity.
        if (request.StoredQueryId is { Length: > 0 } storedQuery)
        {
            if (!string.Equals(
                    storedQuery, WfsRequest.GetFeatureByIdQuery, StringComparison.Ordinal))
            {
                await RefuseAsync(
                        context,
                        WfsFault.Invalid(
                            "STOREDQUERY_ID",
                            $"'{storedQuery}' is not a stored query this server holds. It holds "
                            + $"'{WfsRequest.GetFeatureByIdQuery}'."),
                        cancellation)
                    .ConfigureAwait(false);

                return;
            }

            if (resourceIds.Count != 1
                || !WfsFeatureType.TrySplitResourceId(
                    resourceIds[0], out string storedType, out string storedId))
            {
                await RefuseAsync(
                        context,
                        WfsFault.Invalid(
                            "id",
                            "GetFeatureById takes one 'id' of the form <typeName>.<identity> — "
                            + "the same string this server writes as gml:id."),
                        cancellation)
                    .ConfigureAwait(false);

                return;
            }

            typeNames = [storedType];
            resourceIds = [storedId];
        }

        if (typeNames.Count != 1)
        {
            await RefuseAsync(
                    context,
                    typeNames.Count == 0
                        ? WfsFault.Missing("typeNames")
                        : new WfsFault(
                            WfsFaultCode.OperationNotSupported,
                            "typeNames",
                            "One feature type per request. Reading two at once is a join, and "
                            + "ImplementsStandardJoins is FALSE in the capabilities."),
                    cancellation)
                .ConfigureAwait(false);

            return;
        }

        if (!TryFind(
                visible, typeNames[0], request.Namespaces,
                out PublishedLayer? layer, out WfsFault? notFound))
        {
            await RefuseAsync(context, notFound!, cancellation).ConfigureAwait(false);
            return;
        }

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer!, cancellation).ConfigureAwait(false);

        WfsFeatureType type = new(
            layer!.Definition.Name,
            TitleOf(layer),
            null,
            layer.Definition.Srid,
            layer.GeometryType,
            layer.Definition.GeometryColumn,
            described.Fields,
            described.Extent);

        if (!TryQuery(
                request, layer, described, resourceIds, settings,
                out FeatureQuery? query, out int outputSrid, out WfsFault? bad))
        {
            await RefuseAsync(context, bad!, cancellation).ConfigureAwait(false);
            return;
        }

        // <b>Asked before a byte is written, because after is too late on this face.</b>
        // `srsName` is checked for spelling when the request is parsed, and spelling is
        // all a parser can check: `urn:ogc:def:crs:EPSG::999999` is well-formed and names
        // nothing. The transform then failed on the first row — after the 200, after the
        // header, after `numberReturned="1000"` — and `XmlWriter` closed the open element
        // on its way out, so a client received a **well-formed, complete-looking WFS
        // document claiming a thousand features and carrying none**. Silent data loss
        // presented as success, from one bad query parameter. Found by the second failure
        // gate.
        //
        // OGC API Features never had this bug because it validates against the
        // collection's advertised list first. WFS cannot do the same — it advertises one
        // `DefaultCRS` per feature type and no `OtherCRS`, while happily and usefully
        // serving any system PostGIS knows — so the question it asks is the weaker and
        // truer one: is this a system this deployment can project into at all.
        if (request.Srid is { } asked
            && asked != layer.Definition.Srid
            && !await context.RequestServices.GetRequiredService<IProjector>()
                .KnowsAsync(asked, cancellation).ConfigureAwait(false))
        {
            await RefuseAsync(
                    context,
                    WfsFault.Invalid(
                        "srsName",
                        $"EPSG:{asked} is not a coordinate reference system this deployment "
                        + "can project into. It is spelled correctly and the projection "
                        + "database does not have it."),
                    cancellation)
                .ConfigureAwait(false);

            return;
        }

        long matched = await source.CountAsync(query!, cancellation).ConfigureAwait(false);

        // <b>`count=0` asks for none, and it is not the same as omitting count.</b>
        // WFS lets a client ask for the metadata in the results shape rather than
        // the hits shape, and `FeatureQuery` cannot express a limit of zero — its
        // constructor refuses one, correctly, because an unbounded read is the
        // thing that limit exists to prevent. So the zero is honoured here instead
        // of being clamped up to one, which is what it was: `count=0` returned a
        // feature.
        bool none = request.HitsOnly || request.Count == 0;

        long returned = none
            ? 0
            : Math.Clamp(matched - request.StartIndex, 0, query!.Limit);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        IAsyncEnumerable<Feature> features = none
            ? Nothing()
            : source.ReadAsync(query!, cancellation);

        if (request.Operation == WfsOperation.GetPropertyValue)
        {
            if (!TryValueProperty(
                    request, described, layer, out string property, out bool isGeometry,
                    out bool isIdentifier, out WfsFault? badReference))
            {
                await RefuseAsync(context, badReference!, cancellation).ConfigureAwait(false);
                return;
            }

            context.Response.ContentType = WfsNames.GmlMediaType + "; charset=utf-8";

            await new ValueCollectionWriter(type, property, isGeometry, isIdentifier, outputSrid)
                .WriteAsync(context.Response.Body, features, matched, returned, now, cancellation)
                .ConfigureAwait(false);

            return;
        }

        if (request.Format == WfsOutputFormat.GeoJson)
        {
            context.Response.ContentType = WfsNames.GeoJsonMediaType + "; charset=utf-8";

            await StreamAsync(context, "wfs", () =>
                    new GeoJsonFeatureCollectionWriter(type)
                        .WriteAsync(
                            context.Response.Body, features, matched, returned, now, cancellation))
                .ConfigureAwait(false);

            return;
        }

        context.Response.ContentType = WfsNames.GmlMediaType + "; charset=utf-8";

        await StreamAsync(context, "wfs", () =>
                new GmlFeatureCollectionWriter(type, outputSrid, Endpoint(context))
                    .WriteAsync(
                        context.Response.Body, features, matched, returned, now, cancellation))
            .ConfigureAwait(false);
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
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(name),
                e);

            context.Abort();
        }
    }

    /// <summary>
    /// Builds the query, which is where every part of the request meets the engine.
    /// </summary>
    /// <remarks>
    /// <b>GeoJSON decides its own reference and says so if it cannot.</b> RFC 7946
    /// pins GeoJSON to WGS 84 and this repository already enforces that on import;
    /// so a GeoJSON response reprojects, and a request that asks for GeoJSON in
    /// another reference is refused rather than served in one of the two.
    /// </remarks>
    private static bool TryQuery(
        WfsRequest request,
        PublishedLayer layer,
        LayerDescription described,
        IReadOnlyList<string> resourceIds,
        HostSettings settings,
        out FeatureQuery? query,
        out int outputSrid,
        out WfsFault? fault)
    {
        query = null;
        fault = null;
        outputSrid = request.Srid ?? layer.Definition.Srid;

        if (request.Format == WfsOutputFormat.GeoJson)
        {
            if (request.Srid is { } asked && asked != GeoJsonFeatureCollectionWriter.Srid)
            {
                fault = WfsFault.Invalid(
                    "srsName",
                    $"GeoJSON is written in WGS 84 (EPSG:{GeoJsonFeatureCollectionWriter.Srid}) "
                    + "and the request asks for another reference. Ask for GML if the data must "
                    + "stay in its own reference.");

                return false;
            }

            outputSrid = GeoJsonFeatureCollectionWriter.Srid;
        }

        if (!FilterReader.TryRead(
                request.Filter, described.Fields, layer.Definition.Srid,
                out ParsedFilter filter, out fault, layer.Definition.GeometryColumn))
        {
            return false;
        }

        if (!WfsBoundingBox.TryParse(
                request.BoundingBox, layer.Definition.Srid,
                out SpatialFilter? box, out int boxSrid, out fault))
        {
            return false;
        }

        if (box is not null && filter.Spatial is not null)
        {
            fault = new WfsFault(
                WfsFaultCode.OperationNotSupported,
                "bbox",
                "A bbox parameter and a spatial predicate in the filter cannot both be applied: "
                + "a query carries one spatial restriction. Put the extent inside the filter.");

            return false;
        }

        SpatialFilter? spatial = filter.Spatial ?? box;

        int? filterSrid = filter.Spatial is not null
            ? filter.FilterSrid
            : box is not null && boxSrid != layer.Definition.Srid ? boxSrid : null;

        List<string> identities = [.. resourceIds, .. filter.ResourceIds];

        AttributePredicate? predicate = filter.Predicate;

        if (identities.Count > 0)
        {
            if (!TryIdentity(layer, described, identities, out AttributePredicate? byId, out fault))
            {
                return false;
            }

            predicate = predicate is null
                ? byId
                : new AttributePredicate.Conjunction(predicate, byId!);
        }

        if (!TrySort(request.SortBy, described, out List<SortKey> order, out fault))
        {
            return false;
        }

        if (!TryFields(
                request.PropertyNames,
                described,
                layer.Definition.GeometryColumn,
                out IReadOnlyList<string>? fields,
                out fault))
        {
            return false;
        }

        int limit = Math.Clamp(
            request.Count ?? settings.DefaultRecordCount, 1, settings.MaximumRecordCount);

        // <b>Paging needs a stable order and WFS does not require the client to
        // ask for one.</b> FeatureQuery.Offset is only sound against a stable
        // order, and a provider's natural order is not one — so a request with a
        // startIndex and no sortBy is ordered by identity, which every layer has
        // and which cannot change under a reader mid-page.
        if (order.Count == 0 && request.StartIndex > 0)
        {
            order.Add(new SortKey(layer.Definition.IdentityColumn, Descending: false));
        }

        ParsedWhere? where = null;

        if (predicate is not null)
        {
            if (!PredicateSql.TryEmit(
                    predicate,

                    // The geometry column joins the list because a null check may now name
                    // it — see `FilterReader.TryNull`. Nothing else can reach it: the
                    // reader refuses the geometry for every other predicate before this.
                    [.. described.Fields.Select(f => f.Name), layer.Definition.GeometryColumn],
                    LayerDefinition.Quote,
                    out ParsedWhere emitted,
                    out string? error))
            {
                fault = WfsFault.Invalid("filter", error ?? "The filter could not be compiled.");
                return false;
            }

            where = emitted;
        }

        query = new FeatureQuery(
            limit: limit,
            fields: fields,
            offset: request.StartIndex,
            includeGeometry: true,
            orderBy: order,
            spatial: spatial,
            outSrid: outputSrid == layer.Definition.Srid ? null : outputSrid,
            where: where,
            filterSrid: filterSrid);

        return true;
    }

    /// <summary>
    /// Turns identifiers into a predicate over the layer's own identity column.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>FeatureQuery.ObjectIds</c>, which takes integers.</b> A WFS
    /// identity is a string because [Q-57](../../docs/open-questions.md) made
    /// identity a declared column that may hold a uuid or text, so the identifier
    /// route has to go through the predicate. Where the column is an integer the
    /// values are converted to one, because a text comparison against an integer
    /// column is an error at the database rather than an empty result.
    /// </remarks>
    private static bool TryIdentity(
        PublishedLayer layer,
        LayerDescription described,
        List<string> identities,
        out AttributePredicate? predicate,
        out WfsFault? fault)
    {
        predicate = null;
        fault = null;

        string column = layer.Definition.IdentityColumn;

        FieldDescription? field = described.Find(column);

        if (field is null)
        {
            fault = new WfsFault(
                WfsFaultCode.OperationProcessingFailed,
                null,
                $"This layer's identity column '{column}' is not among the columns the database "
                + "reports, so a feature cannot be addressed by identifier.");

            return false;
        }

        bool whole = field.Value.Type
            is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger;

        List<object?> values = new(identities.Count);

        foreach (string given in identities)
        {
            // <b>A ResourceId is a gml:id, and a gml:id carries its type.</b> The
            // stored query splits it; the `resourceId` parameter and
            // `fes:ResourceId` were reaching here whole, so a conforming client's
            // own identifier — the exact string this server had written as the
            // feature's gml:id — was refused as not being one. Found by sending
            // this server its own output back.
            string identity = given;

            if (WfsFeatureType.TrySplitResourceId(given, out string named, out string bare))
            {
                if (!string.Equals(
                        named, layer.Definition.Name, StringComparison.OrdinalIgnoreCase))
                {
                    fault = WfsFault.Invalid(
                        "resourceId",
                        $"'{given}' names the feature type '{named}' and this request is for "
                        + $"'{layer.Definition.Name}'. One request reads one feature type.");

                    return false;
                }

                identity = bare;
            }

            if (!whole)
            {
                values.Add(identity);
                continue;
            }

            if (!long.TryParse(
                    identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            {
                /*
                  <b>An identifier nothing holds is an empty answer, not a refusal.</b> WFS
                  2.0 §7.9.2.4.2 says a `ResourceId` naming a resource the server does not
                  have selects nothing — the request is well formed and the answer is a
                  collection with no members. This refused it with a 400, on the grounds
                  that the identity column holds whole numbers and the text was not one,
                  which is true and is not the question the client asked.

                  <b>The two look the same from here and are not.</b> A malformed *type
                  name* in the identifier is still refused above: `other_layer.4` in a
                  request for this layer is a client asking the wrong resource, and
                  answering it with silence would hide a real mistake. What is skipped is
                  only the value, and only when the column could never hold it.

                  Found on 2026-08-21 by re-running the OGC suite, which charges 25
                  failures for it — one per feature type.
                */
                continue;
            }

            values.Add(number);
        }

        // Every identifier named a resource this feature type could not hold, so the
        // answer is an empty collection rather than a refusal — see the `continue` above.
        predicate = values.Count == 0
            ? new AttributePredicate.MatchesNothing()
            : new AttributePredicate.OneOf(column, values, Negated: false);

        return true;
    }

    private static bool TrySort(
        IReadOnlyList<string> sortBy,
        LayerDescription described,
        out List<SortKey> order,
        out WfsFault? fault)
    {
        order = [];
        fault = null;

        foreach (string entry in sortBy)
        {
            string[] parts = entry.Split(
                [' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            string name = parts[0];

            int colon = name.LastIndexOf(':');

            if (colon >= 0)
            {
                name = name[(colon + 1)..];
            }

            if (described.Find(name) is not { } field)
            {
                FieldDescription? insensitive = described.Fields
                    .Cast<FieldDescription?>()
                    .FirstOrDefault(f => string.Equals(
                        f!.Value.Name, name, StringComparison.OrdinalIgnoreCase));

                if (insensitive is null)
                {
                    fault = WfsFault.Invalid(
                        "sortBy", $"'{name}' is not a property of this feature type.");

                    return false;
                }

                field = insensitive.Value;
            }

            // 'D' and 'DESC' both appear; anything else is ascending, which is the
            // specification's own default rather than a guess.
            bool descending = parts.Length > 1
                && parts[1].StartsWith('D');

            order.Add(new SortKey(field.Name, descending));
        }

        return true;
    }

    /// <summary>
    /// The property GetPropertyValue was asked for.
    /// </summary>
    /// <remarks>
    /// <b>The geometry is a property to a client and is not a field here</b>, so it
    /// is resolved separately rather than being missing. Everything else must be a
    /// column the layer has, and a name that is neither is refused — the operation
    /// exists to return one property's values, so there is no sensible answer to a
    /// request for a property that does not exist.
    /// </remarks>
    private static bool TryValueProperty(
        WfsRequest request,
        LayerDescription described,
        PublishedLayer layer,
        out string property,
        out bool isGeometry,
        out bool isIdentifier,
        out WfsFault? fault)
    {
        property = string.Empty;
        isGeometry = false;
        isIdentifier = false;

        if (string.IsNullOrWhiteSpace(request.PropertyValueReference))
        {
            fault = WfsFault.Missing("valueReference");
            return false;
        }

        if (!ValueReference.TryResolve(
                request.PropertyValueReference,
                out ValueReference.Kind kind,
                out string name,
                out fault))
        {
            return false;
        }

        if (kind == ValueReference.Kind.Attribute)
        {
            property = "id";
            isIdentifier = true;
            return true;
        }

        if (string.Equals(
                name, layer.Definition.GeometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            property = layer.Definition.GeometryColumn;
            isGeometry = true;
            return true;
        }

        foreach (FieldDescription field in described.Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = field.Name;
                return true;
            }
        }

        fault = WfsFault.Invalid(
            "valueReference",
            $"'{name}' is not a property of this feature type. DescribeFeatureType lists the "
            + "properties it has.");

        return false;
    }

    private static bool TryFields(
        IReadOnlyList<string> propertyNames,
        LayerDescription described,
        string geometryProperty,
        out IReadOnlyList<string>? fields,
        out WfsFault? fault)
    {
        fault = null;
        fields = null;

        if (propertyNames.Count == 0)
        {
            fields = [.. described.Fields.Select(f => f.Name)];
            return true;
        }

        List<string> chosen = [];

        foreach (string property in propertyNames)
        {
            string name = property;

            int colon = name.LastIndexOf(':');

            if (colon >= 0)
            {
                name = name[(colon + 1)..];
            }

            FieldDescription? field = described.Fields
                .Cast<FieldDescription?>()
                .FirstOrDefault(f => string.Equals(
                    f!.Value.Name, name, StringComparison.OrdinalIgnoreCase));

            if (field is null)
            {
                // The geometry is a property to a client and is not a field here,
                // so naming it is legitimate and selects nothing extra — the
                // geometry is always read.
                if (string.Equals(name, geometryProperty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // <b>Anything else is refused, and it used to be ignored.</b> A
                // PropertyName nobody recognises produced a collection with no
                // attributes at all and no error — the client asked for one column
                // and got a document that looks like an answer, which is the
                // silent degradation ADR-008 §2 forbids.
                fault = WfsFault.Invalid(
                    "propertyName",
                    $"'{name}' is not a property of this feature type. DescribeFeatureType lists "
                    + "the properties it has.");

                return false;
            }

            chosen.Add(field.Value.Name);
        }

        fields = chosen;
        return true;
    }

    /// <summary>
    /// Resolves a qualified type name to a layer.
    /// </summary>
    /// <remarks>
    /// <b>The prefix is resolved, not read.</b> WFS 2.0 §7.9.2 lets a request bind
    /// its own prefixes with the <c>NAMESPACES</c> parameter and then use them, so
    /// <c>ns98:look_parcels</c> with <c>xmlns(ns98,urn:graticula:ns:hosted)</c> is
    /// the same request as <c>hosted:look_parcels</c>. This read the prefix as a
    /// folder name until the OGC conformance suite — which picks prefixes on
    /// purpose that the server has never advertised — was pointed at it and could
    /// not fetch a single feature. Every other client had used the prefixes it read
    /// out of the capabilities, so nothing else could have found this.
    /// </remarks>
    private static bool TryFind(
        IReadOnlyList<PublishedLayer> visible,
        string typeName,
        IReadOnlyDictionary<string, string> namespaces,
        out PublishedLayer? layer,
        out WfsFault? fault)
    {
        layer = null;

        string name = typeName;
        string? prefix = null;

        int colon = typeName.LastIndexOf(':');

        if (colon >= 0)
        {
            prefix = typeName[..colon];
            name = typeName[(colon + 1)..];
        }

        // A bound prefix names a namespace and this server has one. An unbound
        // prefix is taken at face value, which is what a client that read the
        // capabilities sends.
        if (prefix is not null && namespaces.TryGetValue(prefix, out string? uri))
        {
            if (!string.Equals(uri, WfsNames.Namespace, StringComparison.Ordinal))
            {
                fault = WfsFault.Invalid(
                    "typeNames",
                    $"'{typeName}' binds '{prefix}' to a namespace this server does not serve. "
                    + $"Its feature types are in '{WfsNames.Namespace}'.");

                return false;
            }

            prefix = WfsNames.Prefix;
        }

        foreach (PublishedLayer candidate in visible)
        {
            if (!string.Equals(
                    candidate.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (prefix is not null
                && !string.Equals(prefix, WfsNames.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            layer = candidate;
            fault = null;
            return true;
        }

        // <b>The same answer whether it does not exist or is not visible.</b> A
        // caller who may not see a layer must not be able to tell the two apart,
        // which is the rule ServiceLookup applies to every other surface.
        fault = WfsFault.Invalid(
            "typeNames",
            $"'{typeName}' is not a feature type on this server. GetCapabilities lists the types "
            + "you may read.");

        return false;
    }

    private static IReadOnlyList<WfsFeatureType> Ordered(IEnumerable<WfsFeatureType> types) =>
    [
        .. types.OrderBy(t => t.Name, StringComparer.Ordinal),
    ];

    /// <summary>
    /// What a person reads in a layer list.
    /// </summary>
    /// <remarks>
    /// <b>The folder moved here when it stopped being a namespace.</b> A type name
    /// is now flat, so <c>turkiye</c> would be invisible to somebody browsing the
    /// capabilities unless the title carries it.
    /// </remarks>
    private static string TitleOf(PublishedLayer layer) =>
        string.IsNullOrWhiteSpace(layer.Folder)
            ? layer.Definition.Name
            : $"{layer.Folder} / {layer.Definition.Name}";

    private static string Endpoint(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{Path}";

    private static async IAsyncEnumerable<Feature> Nothing()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private static async Task RefuseAsync(
        HttpContext context, WfsFault fault, CancellationToken cancellation)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/xml; charset=utf-8";

        await fault.WriteAsync(context.Response.Body, cancellation).ConfigureAwait(false);
    }
}
