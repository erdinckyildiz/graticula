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
        CatalogFallback catalog,
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
        CatalogFallback catalog,
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
        CatalogFallback catalog,
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

        IReadOnlyList<PublishedLayer>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        // Null means the refusal is already written: the catalogue is unreachable and nothing
        // is remembered, so there is no list to filter and no honest empty one to send.
        if (visible is null)
        {
            return;
        }

        switch (request!.Operation)
        {
            case WfsOperation.GetCapabilities:
                await CapabilitiesAsync(
                        context,
                        contexts,
                        context.RequestServices.GetRequiredService<IProjector>(),
                        visible,
                        cancellation)
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
    private static async Task<IReadOnlyList<PublishedLayer>?> VisibleAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        CatalogListing listing =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        // <b>Null and empty are different answers, and answering an empty capabilities
        // document here would be the worse of the two.</b> A client that reads
        // `<FeatureTypeList/>` learns this server publishes nothing and has no reason to ask
        // again; 503 says ask later. [D-127](../../docs/architecture-debt.md).
        if (listing.Services is not { } services)
        {
            await RefuseAsync(
                    context,
                    new WfsFault(
                        WfsFaultCode.OperationProcessingFailed,
                        null,
                        "The catalogue is not reachable and this server has no remembered "
                        + "listing to answer from, so it cannot say which feature types it "
                        + "publishes. Retry shortly; see /healthz/ready."),
                    cancellation,
                    StatusCodes.Status503ServiceUnavailable)
                .ConfigureAwait(false);

            return null;
        }

        // Built from a remembered listing, and every response says so in one place rather
        // than each document finding room for it. See ServiceLookup.SayAge.
        if (listing.Blind)
        {
            ServiceLookup.SayAge(context, listing.Age);
        }

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
        IProjector projector,
        IReadOnlyList<PublishedLayer> visible,
        CancellationToken cancellation)
    {
        List<WfsFeatureType> types = [];

        // <b>Every layer described, not only the ones already in 4326 — Q-125.</b> The
        // describe is what produces an extent at all, and it was skipped for anything
        // this document could not have published a box for. That reasoning held only
        // while projecting was believed to cost a round trip per layer.
        foreach (PublishedLayer layer in visible)
        {
            types.Add(await TypeOfAsync(contexts, layer, describe: true, cancellation)
                .ConfigureAwait(false));
        }

        // <b>One call per distinct reference, not per layer.</b> The same routine WMS
        // has used since its own capabilities document was written; see
        // GeographicExtents for why there is now one of it rather than two.
        IReadOnlyList<Graticula.Geometries.Envelope?> geographic =
            await Graticula.Geometries.GeographicExtents
                .InWgs84Async(
                    projector,
                    [.. types.Select(t => (t.Srid, t.Extent))],
                    cancellation)
                .ConfigureAwait(false);

        for (int i = 0; i < types.Count; i++)
        {
            types[i] = types[i] with { Geographic = geographic[i] };
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
        // <b>Whether this is the addressed-resource operation, which changes two things:
        // the status code for *not there*, and the shape of the answer.</b>
        bool byId = request.StoredQueryId is { Length: > 0 };

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
                // <b>404, because an identifier that does not name a feature of this
                // server names no resource</b> — and an unparseable one names no
                // resource just as certainly as a well-formed one that is absent.
                // Splitting the two into a 400 and a 404 would make a client's
                // retry logic depend on how wrong its identifier was.
                await RefuseAsync(
                        context,
                        new WfsFault(
                            WfsFaultCode.InvalidParameterValue,
                            "id",
                            "There is no feature with that identifier. GetFeatureById takes one "
                            + "'id' of the form <typeName>.<identity> — the same string this "
                            + "server writes as gml:id."),
                        cancellation,
                        StatusCodes.Status404NotFound)
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
            // Under GetFeatureById the type came out of the identifier, so an unknown
            // type is an unknown resource rather than a bad parameter.
            await RefuseAsync(
                    context,
                    notFound!,
                    cancellation,
                    byId ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest)
                .ConfigureAwait(false);

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

        /*
          <b>[D-118](../../docs/architecture-debt.md): every `GetFeature` counted the whole result
          before writing a page of it.</b> WFS 2.0 makes `numberReturned` a required attribute on
          the collection element, so it has to be known before the first feature is written — and
          the two ways to know it were to buffer the page, which A-037 rules out, or to ask how
          many rows match. Asking cost `O(table)` beside a page costing `O(page)`.

          <b>Measured before it was changed, because the row said to</b>
          (`benchmarks/wfs-count`): on the 6.5-million-row corpus an unfiltered `count(*)` is
          **577 ms** where the page it accompanies is **7.6 ms**. On a 46,041-row layer it is 3.9 ms
          of a 44 ms request — immaterial. The count is `O(table)` and the page is `O(page)`, so
          the share is not a constant: it is whatever the deployment's largest table makes it.

          <b>`resultType=hits` keeps the whole count, deliberately.</b> That is what hits is for —
          the client asked for the number and nothing else, and answering *unknown* would answer a
          different question. The row said the change was only ever to the `results` path.

          <b>Counting to `startIndex + limit + 1` answers both questions this path has.</b> How
          many rows will this page hold, exactly; and is there another page, exactly. What it
          gives up is the total, and only when the total exceeds the bound — which is the case the
          specification's `numberMatched="unknown"` exists for.

          <b>And that bound alone would have been the wrong trade, which the measurement showed
          too.</b> A page of ten from a 1,421-row layer would answer *unknown* — the row's own
          warning, that removing the count removes the paging metadata every client uses to draw a
          scrollbar, and it would have applied to every layer this deployment has. `ExactUpTo`
          raises the floor: the cost stops growing with the table and the total stays exact for
          any layer somebody is likely to page through by hand.
        */
        long ceiling = Math.Max((long)request.StartIndex + query!.Limit + 1, ExactUpTo);

        long counted = request.HitsOnly
            ? await source.CountAsync(query!, cancellation).ConfigureAwait(false)
            : await source.CountUpToAsync(query!, ceiling, cancellation).ConfigureAwait(false);

        // <b>At the ceiling the number means *at least this many*.</b> Everywhere below that
        // treats it as a total, so the ambiguity is resolved once, here.
        bool whole = request.HitsOnly || counted < ceiling;

        long? matched = whole ? counted : null;

        // <b>An identifier that matches nothing is a 404 and not an empty collection.</b>
        // Every other operation here answers *no features* with a collection of none,
        // which is the right answer to a question about a set. GetFeatureById is not a
        // question about a set: it addresses one resource, and returning 200 with an
        // empty document tells a client its identifier was fine and the feature is
        // simply absent — which is exactly the case a 404 exists to name.
        if (byId && counted == 0)
        {
            await RefuseAsync(
                    context,
                    new WfsFault(
                        WfsFaultCode.InvalidParameterValue,
                        "id",
                        "There is no feature with that identifier on this server."),
                    cancellation,
                    StatusCodes.Status404NotFound)
                .ConfigureAwait(false);

            return;
        }

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
            : Math.Clamp(counted - request.StartIndex, 0, query!.Limit);

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

        GmlFeatureCollectionWriter writer = new(type, outputSrid, Endpoint(context));

        if (byId)
        {
            // <b>The feature itself, because that is what the operation returns.</b>
            // §7.9.3.6, and the reason is in WriteFeatureAsync's own remark: a client
            // that asked for one identifier reads the answer's root element, and this
            // surface was handing it a collection. The 404 above guarantees there is
            // one to write.
            await StreamAsync(context, "wfs", async () =>
                {
                    await foreach (Feature only in features.WithCancellation(cancellation)
                        .ConfigureAwait(false))
                    {
                        await writer
                            .WriteFeatureAsync(context.Response.Body, only, cancellation)
                            .ConfigureAwait(false);

                        return;
                    }
                })
                .ConfigureAwait(false);

            return;
        }

        (string? next, string? previous) = Pages(context, request, query!, counted, returned);

        await StreamAsync(context, "wfs", () =>
                writer.WriteAsync(
                    context.Response.Body, features, matched, returned, now, cancellation,
                    next, previous))
            .ConfigureAwait(false);
    }

    /// <summary>How far the results path will count before it says the total is unknown.</summary>
    /// <remarks>
    /// <para>
    /// <b>A hundred thousand, and the number is measured rather than chosen</b> —
    /// [benchmarks/wfs-count](../../benchmarks/wfs-count/RESULTS.md), on the 6.5-million-row
    /// corpus: counting to this ceiling costs <b>17.9 ms</b> where the unbounded count of the same
    /// table costs <b>577 ms</b>, and where the page it accompanies costs 7.6 ms. Thirty-two times
    /// cheaper, and flat: a table ten times larger costs the same, which is the property
    /// [D-118](../../docs/architecture-debt.md) is about.
    /// </para>
    /// <para>
    /// <b>Generous on purpose.</b> Every layer this deployment publishes is under it — the largest
    /// is 46,041 — so `numberMatched` stays exact for all of them, and a client drawing a
    /// scrollbar keeps the number it draws it from. What is given up is the total on a table
    /// nobody pages through by hand.
    /// </para>
    /// <para>
    /// <b>Not configuration, for now.</b> A setting invites a deployment to raise it back to the
    /// cost this removed, and nothing has asked for it. Recorded here rather than in
    /// `HostSettings` so that whoever needs it can see what the number is worth first.
    /// </para>
    /// </remarks>
    private const long ExactUpTo = 100_000;

    /// <summary>
    /// Where the next and previous pages are, as absolute URLs, or null for neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>§7.7.4.4.1 requires these on any response that is part of a larger result
    /// set, and this surface had neither.</b> Two CITE assertions fail without them and
    /// the reason they matter is not conformance: <c>numberMatched</c> tells a client how
    /// much there is, and nothing told it how to ask for the rest. A client left to build
    /// <c>startIndex</c> itself is guessing at a page size the server chose, which is the
    /// same class of problem as an extent a client cannot send back.
    /// </para>
    /// <para>
    /// <b>Built from this request's own query string, with <c>startIndex</c> and
    /// <c>count</c> replaced.</b> That keeps every other parameter — the filter, the
    /// sort, the output format — exactly as the caller wrote it, which is what makes the
    /// next page the same query rather than a similar one. <c>count</c> is written
    /// explicitly even when the caller omitted it, because the page size that produced
    /// this response is the server's and the following page has to match it or the pages
    /// overlap.
    /// </para>
    /// <para>
    /// <b>Omitted for an XML request, and that is a limitation rather than a
    /// decision.</b> A POST body has no query string to amend, and re-encoding a
    /// Filter Encoding document as KVP is not generally possible — a filter that needed
    /// POST is a filter that does not fit a URL. A client that posts is a client
    /// constructing requests programmatically, which is the one that can page for itself.
    /// </para>
    /// </remarks>
    private static (string? Next, string? Previous) Pages(
        HttpContext context, WfsRequest request, FeatureQuery query, long counted, long returned)
    {
        if (context.Request.Query.Count == 0)
        {
            return (null, null);
        }

        int size = query.Limit;
        int start = request.StartIndex;

        string At(int index, bool forceResults = false)
        {
            List<string> parts = [];

            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair
                in context.Request.Query)
            {
                if (string.Equals(pair.Key, "startindex", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "count", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "maxfeatures", StringComparison.OrdinalIgnoreCase)
                    || (forceResults
                        && string.Equals(
                            pair.Key, "resulttype", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (string? value in pair.Value)
                {
                    parts.Add(
                        Uri.EscapeDataString(pair.Key)
                        + "=" + Uri.EscapeDataString(value ?? string.Empty));
                }
            }

            parts.Add("startIndex=" + index.ToString(CultureInfo.InvariantCulture));
            parts.Add("count=" + size.ToString(CultureInfo.InvariantCulture));

            if (forceResults)
            {
                parts.Add("resultType=results");
            }

            return Endpoint(context) + "?" + string.Join('&', parts);
        }

        /*
          <b>A hits response is page zero, and that is the suite's reading rather than
          mine.</b> `resultType=hits` returns a count and no features, so *the next page*
          is arguable — but CITE's `getFeatureWithHitsOnly` asserts `next` present and
          `previous` absent, which only makes sense if a client is expected to go from
          the count straight to the first page of features.

          <b>And it must not carry `resultType=hits` forward.</b> The first version of
          this preserved every parameter, so `next` reproduced the request that had just
          been answered — same document, same `next`, for ever. Measured against tr_il
          before it shipped. So this one link drops `resultType` and states `results`,
          which is the only place in this method that rewrites rather than preserves.
        */
        if (request.HitsOnly)
        {
            return (counted > 0 ? At(0, forceResults: true) : null, null);
        }

        // <b>`next` on the strength of `numberMatched`, not on a full page.</b> A page
        // that happens to end exactly on the last feature is still the last page, and
        // saying otherwise sends a client to an empty document.
        //
        // <b>And not at all when this page carried nothing, which is a loop rather than
        // a page.</b> `resultType=hits` and `count=0` both answer with metadata and no
        // features; `start + 0 < matched` is true for both, so the first version of this
        // wrote a `next` pointing at the request that had just been answered — and
        // because the URL preserves every other parameter, including `resultType`, a
        // client following it would have received the same document with the same `next`
        // for ever. Measured on `resultType=hits` against tr_il before it shipped.
        // <b>D-118: `counted` is bounded at `startIndex + limit + 1`</b>, so *is there another
        // page* is *did the bounded count reach past this page* — which is the same question the
        // full count used to answer and is the reason the bound is `+ 1` rather than `+ 0`.
        string? next = returned > 0 && start + returned < counted
            ? At((int)(start + returned))
            : null;

        string? previous = start > 0 ? At(Math.Max(0, start - size)) : null;

        return (next, previous);
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

        // <b>Absent and blank are different refusals.</b> `MissingParameterValue` tells
        // a caller to add the parameter; a caller who wrote `valueReference=""` has
        // added it and needs to be told the value is unusable instead. CITE's
        // `getProperty_emptyValueRef` asserts the second, and the request binder keeps
        // the empty string rather than folding it to null so that this can tell them
        // apart at all.
        if (request.PropertyValueReference is null)
        {
            fault = WfsFault.Missing("valueReference");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.PropertyValueReference))
        {
            fault = WfsFault.Invalid(
                "valueReference",
                "The 'valueReference' parameter was supplied with no value. It names the "
                + "property to return — DescribeFeatureType lists the ones this feature type "
                + "has.");

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

    /// <summary>Writes a refusal.</summary>
    /// <remarks>
    /// <b>400 for everything except a resource that is not there.</b>
    /// <see cref="WfsFault"/>'s own remark explains why every refusal on this surface is
    /// a 4xx carrying an <c>ows:ExceptionReport</c> rather than a bare status code, and
    /// why 400 is the right one for a request the server will not carry out. **The
    /// exception is <c>GetFeatureById</c>**, which is addressed like a resource and is
    /// required to answer 404 when the identifier names nothing — CITE's
    /// <c>invokeGetFeatureByIdWithUnknownID</c> accepts 404 or 403 and this surface sent
    /// 400. The distinction is worth keeping: 400 says *fix your request* and a client
    /// looking for a typo in an identifier it was given by this server will not find one.
    /// </remarks>
    private static async Task RefuseAsync(
        HttpContext context,
        WfsFault fault,
        CancellationToken cancellation,
        int status = StatusCodes.Status400BadRequest)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/xml; charset=utf-8";

        await fault.WriteAsync(context.Response.Body, cancellation).ConfigureAwait(false);
    }
}
