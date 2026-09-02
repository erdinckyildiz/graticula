using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.Wms;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// The WMS surface: capabilities, maps, feature info and legends.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-041](../../docs/adr/ADR-041-the-map-renderer.md), built 2026-08-20.</b>
/// The first face on this server that returns a picture. Everything above the
/// drawing is this file; the drawing itself is <c>Graticula.Core.Cartography</c> and
/// the pixels are <c>Graticula.Render.Skia</c>, and neither of those knows WMS
/// exists.
/// </para>
/// <para>
/// <b>Sharing and capability limits are the catalogue's, not a second set.</b> The
/// same four checks every other surface applies — running, shared, privileged, and
/// the feature face switched on — decide which layers exist here, which is D-123's
/// lesson applied at the moment a third surface was built rather than after.
/// </para>
/// </remarks>
internal static class WmsEndpoints
{
    /// <summary>Where the surface lives.</summary>
    public const string Path = "/wms";

    /// <summary>
    /// How long a layer's time extent is trusted before it is measured again.
    /// </summary>
    /// <remarks>
    /// <b>A capabilities document would otherwise cost a min/max scan per layer.</b>
    /// At the stated scale of 100–1,000 services that is a thousand aggregate queries
    /// to answer one document a client asks for on every connection. Five minutes is
    /// long enough that browsing is free and short enough that a layer loaded this
    /// morning is animatable this morning.
    /// </remarks>
    public static readonly TimeSpan TimeExtentLifetime = TimeSpan.FromMinutes(5);

    // <b>The measurement lives on `ServiceContexts` since 2026-08-25 — D-160.</b> It was a
    // `static` dictionary here, read and written and never removed from: the lifetime
    // above decides whether to re-measure over the top, and nothing was asking whether an
    // entry should still exist. A republished layer gets a new id (D-34 makes
    // republishing the ordinary way to correct a name), so the count grew with every
    // publication a deployment had ever made. `ServiceContexts.Forget` is already called
    // by the unpublish and refresh paths and already cleared everything else the request
    // path remembers; this is now one of the things it clears.

    /// <summary>The link a REST directory page offers to this surface.</summary>
    /// <remarks>
    /// <b>Here rather than in the renderer, for the reason
    /// <see cref="WfsEndpoints.DirectoryLink"/> gives.</b> A directory page that
    /// builds a WMS request out of string fragments is a second description of this
    /// surface, and the two drift the first time a parameter is renamed.
    /// </remarks>
    /// <param name="layerName">The layer to preview, or null for the capabilities.</param>
    /// <param name="extent">Where to draw, or null when that is unknown.</param>
    /// <param name="srid">The layer's CRS.</param>
    /// <returns>The label and the address.</returns>
    public static (string Label, string Href) DirectoryLink(
        string? layerName, Envelope? extent, int srid)
    {
        if (layerName is null || extent is not { IsEmpty: false } box)
        {
            return ("WMS", $"{Path}?service=WMS&version=1.3.0&request=GetCapabilities");
        }

        // 1.3.0 with a geographic CRS is latitude first, which is the whole trap.
        // A link this server writes must obey the rule this server publishes, or
        // the one place a person clicks to check the surface is the one place that
        // proves it wrong.
        bool swap = WmsNames.IsLatitudeFirst(WmsVersion.V130, srid);

        string bbox = string.Join(
            ',',
            (swap ? new[] { box.MinY, box.MinX, box.MaxY, box.MaxX }
                  : [box.MinX, box.MinY, box.MaxX, box.MaxY])
                .Select(n => n.ToString("0.##########", CultureInfo.InvariantCulture)));

        return ("WMS",
            $"{Path}?service=WMS&version=1.3.0&request=GetMap"
            + $"&layers={Uri.EscapeDataString(layerName)}&styles="
            + $"&crs=EPSG:{srid.ToString(CultureInfo.InvariantCulture)}"
            + $"&bbox={bbox}&width=800&height=600&format=image/png&transparent=true");
    }

    /// <summary>Maps the surface.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Path, GetAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    private static async Task GetAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        WmsLimits limits = new(
            settings.MaximumImageWidth,
            settings.MaximumImageHeight,
            MaximumLayersPerMap,
            MaximumFeatureInfoCount);

        if (!WmsRequest.TryParse(Parameter(context), limits, out WmsRequest? request, out WmsFault? fault))
        {
            // The version a refusal is written in is the one that was asked for
            // where that parsed, and 1.3.0 otherwise. A 1.1.1 client handed a
            // namespaced 1.3.0 exception reports a parse error rather than the
            // message the exception carries.
            await RefuseAsync(context, VersionOf(context), fault!, cancellation)
                .ConfigureAwait(false);

            return;
        }

        try
        {
            switch (request!.Operation)
            {
                case WmsOperation.GetCapabilities:
                    await CapabilitiesAsync(
                        context, catalog, contexts, projector, request, limits, settings,
                        cancellation)
                        .ConfigureAwait(false);
                    return;

                case WmsOperation.GetMap:
                    await MapImageAsync(context, catalog, contexts, canvases, request, settings, cancellation)
                        .ConfigureAwait(false);
                    return;

                case WmsOperation.GetFeatureInfo:
                    await FeatureInfoAsync(context, catalog, contexts, request, settings, cancellation)
                        .ConfigureAwait(false);
                    return;

                case WmsOperation.GetLegendGraphic:
                    await LegendAsync(context, catalog, contexts, canvases, request, cancellation)
                        .ConfigureAwait(false);
                    return;

                default:
                    await RefuseAsync(
                        context,
                        request.Version,
                        new WmsFault(
                            WmsFault.OperationNotSupported,
                            $"{request.Operation} parsed but has no handler. That is a defect here "
                            + "rather than in the request.",
                            "REQUEST"),
                        cancellation).ConfigureAwait(false);

                    return;
            }
        }
        catch (SymbologyException e)
        {
            // A style this server stored and cannot draw from. The client did
            // nothing wrong and the message names what is wrong with the style,
            // because the person who can fix it is reading the client.
            await RefuseAsync(
                context,
                request!.Version,
                new WmsFault(WmsFault.StyleNotDefined, e.Message, "STYLES"),
                cancellation).ConfigureAwait(false);
        }
        catch (RenderException e)
        {
            await RefuseAsync(
                context, request!.Version, new WmsFault(null, e.Message), cancellation)
                .ConfigureAwait(false);
        }
    }

    /// <summary>How many layers one map may compose.</summary>
    private const int MaximumLayersPerMap = 32;

    /// <summary>How many features <c>GetFeatureInfo</c> may return.</summary>
    private const int MaximumFeatureInfoCount = 100;

    /// <summary>Reads a parameter case-insensitively, as the specification requires.</summary>
    private static Func<string, string?> Parameter(HttpContext context) =>
        name =>
        {
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair
                in context.Request.Query)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value.ToString();
                }
            }

            return null;
        };

    /// <summary>The version a refusal should be written in, before parsing succeeded.</summary>
    private static WmsVersion VersionOf(HttpContext context)
    {
        string? asked = Parameter(context)("VERSION") ?? Parameter(context)("WMTVER");

        return asked?.Trim() is "1.1.1" or "1.1.0" or "1.0.0"
            ? WmsVersion.V111
            : WmsVersion.V130;
    }

    private static async Task RefuseAsync(
        HttpContext context,
        WmsVersion version,
        WmsFault fault,
        CancellationToken cancellation,
        int status = 200)
    {
        // <b>200, and it is inherited rather than chosen.</b> A WMS service
        // exception is a successful response carrying an application refusal, and
        // several clients treat a 4xx as a transport failure and never read the body
        // — discarding the one sentence that says what was wrong.
        //
        // <b>The override exists for one case and it is the opposite one.</b> When the
        // catalogue is unreachable there is no sentence worth reading — the answer is
        // *retry* — and the status is the whole message: a proxy, a load balancer and a
        // monitor all act on 503 and none of them parse a ServiceExceptionReport.
        // [D-127](../../docs/architecture-debt.md).
        context.Response.StatusCode = status;
        context.Response.ContentType = WmsFault.MediaType(version);

        await context.Response.WriteAsync(fault.ToXml(version), cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Every layer this caller may see through this surface.
    /// </summary>
    /// <remarks>
    /// <b>Four checks, and the fourth is [D-123](../../docs/architecture-debt.md).</b>
    /// A stopped service is invisible unless the caller may manage the server;
    /// sharing is evaluated with <see cref="LayerAccess"/> rather than
    /// re-implemented; and a service whose feature face the operator switched off is
    /// switched off here too. WFS learned that last one a day late by being caught;
    /// this surface has it from the first commit.
    /// </remarks>
    private static async Task<IReadOnlyList<PublishedLayer>?> VisibleAsync(
        HttpContext context, CatalogFallback catalog, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        bool seesStopped = current.Authorization.Allows(Privilege.AdminManageServer);

        CatalogListing listing =
            await catalog.ListServicesAsync(cancellation).ConfigureAwait(false);

        // <b>An empty layer tree is a claim, and this one would be false.</b> A client
        // that reads a capabilities document with no layers stops asking; 503 says ask
        // later. [D-127](../../docs/architecture-debt.md).
        if (listing.Services is not { } services)
        {
            await RefuseAsync(
                    context,
                    VersionOf(context),
                    new WmsFault(
                        null,
                        "The catalogue is not reachable and this server has no remembered "
                        + "listing to answer from, so it cannot say which layers it publishes. "
                        + "Retry shortly; see /healthz/ready."),
                    cancellation,
                    StatusCodes.Status503ServiceUnavailable)
                .ConfigureAwait(false);

            return null;
        }

        // Built from a remembered listing, said in one place for every document rather
        // than in each document's own vocabulary. See ServiceLookup.SayAge.
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

            foreach (PublishedLayer layer in service.Layers)
            {
                // A group layer holds other layers and has no geometry of its own.
                // Publishing one as a WMS layer offers a client a map of nothing.
                if (layer.Definition.GeometryColumn is { Length: > 0 })
                {
                    layers.Add(layer);
                }
            }
        }

        return layers;
    }

    private static PublishedLayer? Find(IReadOnlyList<PublishedLayer> layers, string name)
    {
        foreach (PublishedLayer layer in layers)
        {
            if (string.Equals(layer.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return layer;
            }
        }

        return null;
    }

    private static string EndpointOf(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}{Path}";

    // ---------- GetCapabilities ----------

    private static async Task CapabilitiesAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        WmsRequest request,
        WmsLimits limits,
        HostSettings settings,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        // Null means the refusal is already written: no listing, so nothing to filter.
        if (visible is null)
        {
            return;
        }

        List<WmsLayer> published = [];

        foreach (PublishedLayer layer in visible)
        {
            published.Add(
                await DescribeAsync(contexts, layer, cancellation).ConfigureAwait(false));
        }

        await GeographicallyAsync(projector, published, cancellation).ConfigureAwait(false);

        string document = CapabilitiesDocument.Write(
            request.Version,
            EndpointOf(context),
            "Graticula",
            published,
            limits,
            settings.WmsContact);

        context.Response.ContentType = request.Version == WmsVersion.V130
            ? WmsNames.CapabilitiesMediaType130
            : WmsNames.CapabilitiesMediaType111;

        await context.Response.WriteAsync(document, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// A layer as WMS sees it, including its time dimension when it has one.
    /// </summary>
    /// <remarks>
    /// <b>The extent is the description's and costs nothing; the time extent costs
    /// a query and is cached.</b> See <see cref="TimeExtentLifetime"/>.
    /// </remarks>
    private static async Task<WmsLayer> DescribeAsync(
        ServiceContexts contexts, PublishedLayer layer, CancellationToken cancellation)
    {
        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        return new WmsLayer(
            layer.Definition.Name,
            TitleOf(layer),
            Abstract: null,
            layer.Definition.Srid,
            layer.GeometryType,
            Drawable(described.Extent),
            Geographic: null,
            Queryable: true,
            await TimeOfAsync(source, layer, described, contexts, cancellation)
                .ConfigureAwait(false));
    }

    /// <summary>
    /// Fills in each layer's extent in WGS 84.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Required, not decorative.</b> WMS 1.3.0 makes
    /// <c>EX_GeographicBoundingBox</c> mandatory on a named layer, and 1.1.1 makes
    /// <c>LatLonBoundingBox</c> mandatory. A document without it validates in
    /// nothing and several clients refuse to add the layer at all — which is how
    /// this was found, by writing the document and then reading the schema.
    /// </para>
    /// <para>
    /// <b>Batched by source CRS, because projection is a round trip.</b> A
    /// capabilities document listing a thousand layers must not make a thousand
    /// calls; <see cref="IProjector"/> takes a list precisely so it does not have to.
    /// </para>
    /// <para>
    /// <b>Four corners, and it is an approximation.</b> A rectangle is not a
    /// rectangle after projection, so the true geographic extent of a projected box
    /// can bulge past its corners. Every WMS in existence does this and the error is
    /// smaller than the extent it describes; projecting the boundary densely would be
    /// exact and would cost a hundred times as much for a hint in a listing.
    /// </para>
    /// </remarks>
    private static async Task GeographicallyAsync(
        IProjector projector, List<WmsLayer> layers, CancellationToken cancellation)
    {
        // <b>The batching moved to GeographicExtents on 2026-08-25 and the reason is
        // Q-125.</b> WFS had concluded the same work was one round trip per layer and
        // published no bounding box for any layer outside 4326 — while this method,
        // one assembly along, had been doing it in one call per reference since the
        // day the WMS document was written. Two surfaces, one question, and only the
        // one that could omit the element got the wrong answer.
        IReadOnlyList<Envelope?> geographic = await GeographicExtents
            .InWgs84Async(
                projector,
                [.. layers.Select(l => (l.Srid, l.Extent))],
                cancellation)
            .ConfigureAwait(false);

        for (int i = 0; i < layers.Count; i++)
        {
            if (geographic[i] is { } box)
            {
                layers[i] = layers[i] with { Geographic = Drawable(box) };
            }
        }
    }

    /// <summary>
    /// An extent a client can send straight back as a <c>BBOX</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A layer can genuinely have no width.</b> Every feature of a road at one
    /// latitude, or a single point, produces an extent with a zero dimension — and
    /// this deployment has two of them. Published verbatim, a client that does the
    /// obvious thing with the capabilities document sends <c>BBOX</c> with no area
    /// and is refused for it, having done nothing wrong.
    /// </para>
    /// <para>
    /// <b>Padded by a twentieth of the other dimension</b>, so a long thin layer
    /// stays recognisably long and thin; where both are zero, by a ten-thousandth of
    /// a degree, which is about eleven metres and is the smallest padding that
    /// survives being written with ten decimal places.
    /// </para>
    /// </remarks>
    private static Envelope? Drawable(Envelope? extent)
    {
        if (extent is not { IsEmpty: false } box)
        {
            return extent;
        }

        if (box.Width > 0 && box.Height > 0)
        {
            return box;
        }

        double x = box.Width > 0 ? 0 : Math.Max(box.Height, 0.0001) / 20;
        double y = box.Height > 0 ? 0 : Math.Max(box.Width, 0.0001) / 20;

        return new Envelope(box.MinX - x, box.MinY - y, box.MaxX + x, box.MaxY + y);
    }

    private static string TitleOf(PublishedLayer layer) =>
        layer.Folder is { Length: > 0 } folder
            ? $"{folder}/{layer.ServiceName} — {layer.Definition.Name}"
            : $"{layer.ServiceName} — {layer.Definition.Name}";

    /// <summary>
    /// A layer's time dimension, measured once and cached.
    /// </summary>
    /// <remarks>
    /// <b>Public because OGC API Features needs the same answer.</b> Its collection
    /// document reported no temporal interval while its <c>datetime</c> filter worked
    /// — so a client reading the document concluded the collection had no time and
    /// never sent the parameter. One measurement and one cache for both faces, rather
    /// than a second min/max query with its own answer.
    /// </remarks>
    /// <param name="source">The layer's feature source.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="described">Its columns and extent.</param>
    /// <param name="contexts">Where the measurement is remembered — D-160.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The dimension, or null when the layer has no single date column.</returns>
    public static async Task<TimeDimension?> TimeOfAsync(
        IFeatureSource source,
        PublishedLayer layer,
        LayerDescription described,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        if (TimeDimension.FieldOf(described.Fields, layer.TimeField) is not { } field)
        {
            return null;
        }

        if (contexts.RememberedTime(layer.Id, TimeExtentLifetime, DateTimeOffset.UtcNow)
            is { } held)
        {
            return held;
        }

        TimeDimension dimension = new(field, null, null);

        // <b>Statistics are not on `IFeatureSource`.</b> `ReadAsync` ignores the
        // `statistics` a query carries and returns rows, so asking it for a min and
        // a max produces a feature with no attributes at all — which is what this
        // did on its first run, and it failed as *no attribute named 'from'* rather
        // than as *statistics are somewhere else*. `StatisticsAsync` lives on the
        // provider, and Program.cs's own query path casts for it the same way.
        // <b>Unwrapped first.</b> `ServiceContexts` hands back a
        // `BudgetedFeatureSource` — the connection-budget wrapper — and a cast
        // straight to the provider fails silently against it. Program.cs's own
        // statistics path unwraps exactly here; this is the second caller and the
        // reason that step is worth naming rather than repeating from memory.
        IFeatureSource inner = source is BudgetedFeatureSource wrapper ? wrapper.Inner : source;

        if (inner is not Graticula.Providers.PostGis.PostGisFeatureSource provider)
        {
            return null;
        }

        try
        {
            FeatureQuery query = new(
                limit: 1,
                includeGeometry: false,
                statistics:
                [
                    new StatisticRequest(StatisticKind.Min, field, "from"),
                    new StatisticRequest(StatisticKind.Max, field, "until"),
                ]);

            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                await provider.StatisticsAsync(query, cancellation).ConfigureAwait(false);

            if (rows.Count == 0)
            {
                return null;
            }

            rows[0].TryGetValue("from", out object? from);
            rows[0].TryGetValue("until", out object? until);

            dimension = new TimeDimension(field, Moment(from), Moment(until));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // <b>A layer whose time extent cannot be measured publishes no time
            // dimension, rather than failing the whole capabilities document.</b>
            // One unreadable layer must not make the server look absent to every
            // client that asks what it has.
            return null;
        }

        contexts.RememberTime(layer.Id, dimension, DateTimeOffset.UtcNow);
        return dimension;
    }

    private static DateTimeOffset? Moment(object? value) => value switch
    {
        null => null,
        DateTimeOffset moment => moment,
        DateTime moment => new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc)),
        string text when DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            => parsed,
        _ => null,
    };

    // ---------- GetMap ----------

    private static async Task MapImageAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        WmsRequest request,
        HostSettings settings,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        // Null means the refusal is already written: no listing, so nothing to filter.
        if (visible is null)
        {
            return;
        }

        List<PublishedLayer> wanted = [];

        foreach (string name in request.Layers)
        {
            if (Find(visible, name) is not { } found)
            {
                await RefuseAsync(
                    context,
                    request.Version,
                    new WmsFault(
                        WmsFault.LayerNotDefined,
                        $"`{name}` is not a layer this server publishes to you.",
                        "LAYERS"),
                    cancellation).ConfigureAwait(false);

                return;
            }

            /*
              <b>The configured ceiling — [D-180](../../docs/architecture-debt.md) and
              [ADR-049](../../docs/adr/ADR-049-a-face-refuses-in-its-own-vocabulary.md).</b>
              Until this existed, a service configured to `Create` answered `GetMap` with a
              picture while the ArcGIS `query` on the same service answered 403.

              <b>`OperationNotSupported` rather than `LayerNotDefined`, and the difference
              matters.</b> The layer *is* defined and `GetCapabilities` still names it, because
              [ADR-031](../../docs/adr/ADR-031-service-capability-configuration.md) §2a's state
              is *running and refusing* rather than absent. `LayerNotQueryable` is not this
              either: WMS 1.3.0 scopes it to `GetFeatureInfo`, and that is where this code uses
              it.

              <b>200 rather than 403, which is this face's own rule rather than a new one.</b>
              `RefuseAsync` above records why: several WMS clients treat a 4xx as a transport
              failure and never read the body, discarding the one sentence that says what
              happened. The sentence is the point here, so it is carried the way this face
              carries sentences.
            */
            if (CapabilityCeilings.Refuses(found, "Query"))
            {
                await RefuseAsync(
                    context,
                    request.Version,
                    new WmsFault(
                        WmsFault.OperationNotSupported,
                        CapabilityCeilings.Explain(found, "Query"),
                        "LAYERS"),
                    cancellation).ConfigureAwait(false);

                return;
            }

            wanted.Add(found);
        }

        PixelTransform transform = new(request.Extent, request.Width, request.Height);
        bool geographic = IsGeographic(request.Srid);

        using IMapCanvas canvas = canvases.Create(request.Width, request.Height);

        MapRenderer renderer = new(canvas, transform, geographic);

        renderer.Clear(
            request.Transparent && request.Format == MapImageFormat.Png
                ? Rgba.Transparent
                : request.Background);

        foreach (PublishedLayer layer in wanted)
        {
            await DrawLayerAsync(
                contexts, renderer, transform, layer, request.Srid, request.Time,
                settings.MaximumRecordCount, cancellation,
                context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory made
                    ? made.CreateLogger("wms")
                    : null)
                .ConfigureAwait(false);
        }

        renderer.FinishLabels();

        byte[] image = canvas.Encode(request.Format, settings.JpegQuality);

        context.Response.ContentType = WmsNames.MediaTypeOf(request.Format);
        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches and draws one layer.
    /// </summary>
    /// <remarks>
    /// <b>The query is the ordinary one</b> — <see cref="FeatureQuery"/> with the
    /// buffered extent, the requested CRS, a one-pixel simplification tolerance and
    /// only the columns the style reads. Nothing about rendering needed a new way to
    /// read data, which is most of why ADR-041 turned out smaller than ADR-004
    /// assumed.
    /// </remarks>
    /// <summary>
    /// Fetches and draws one layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The query is the ordinary one</b> — <see cref="FeatureQuery"/> with the
    /// buffered extent, the requested CRS, a one-pixel simplification tolerance and
    /// only the columns the style reads. Nothing about rendering needed a new way to
    /// read data, which is most of why ADR-041 turned out smaller than ADR-004
    /// assumed.
    /// </para>
    /// <para>
    /// <b>Shared with the ArcGIS MapServer face on purpose.</b> Two rendered faces
    /// that each fetched and drew would eventually draw differently, and the
    /// difference would be found by a user comparing them rather than by a test. The
    /// vocabularies differ and the drawing does not.
    /// </para>
    /// </remarks>
    /// <param name="contexts">Where a layer's source comes from.</param>
    /// <param name="renderer">What to draw into.</param>
    /// <param name="transform">Map units to pixels.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="srid">The CRS the image is drawn in.</param>
    /// <param name="time">A time filter, or null for none.</param>
    /// <param name="limit">The most features to read.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="log">
    /// Where to say that an area was outside what this layer can be projected into, or null
    /// on a path that has no logger — D-163.
    /// </param>
    /// <returns>The work.</returns>
    public static async Task DrawLayerAsync(
        ServiceContexts contexts,
        MapRenderer renderer,
        PixelTransform transform,
        PublishedLayer layer,
        int srid,
        TimeWindow? time,
        int limit,
        CancellationToken cancellation,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(layer);

        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        SymbologyPlan plan = layer.Symbology is { Length: > 0 } stored
            ? SymbologyPlan.Compile(stored)
            : SymbologyPlan.Default(layer.Definition.Name, layer.GeometryType);

        MapRenderer.Pass pass = renderer.Begin(plan);

        if (pass.DrawsNothing)
        {
            // Every style layer is switched off at this zoom. Reading the features
            // to draw none of them is the whole query for nothing.
            return;
        }

        /*
          <b>The part of the map outside its own reference is blank, not an error —
          [D-163](../../docs/architecture-debt.md), 2026-08-26.</b> A `GetMap` in `CRS:84`
          with `BBOX=-10,90,10,110` asks for latitudes up to 110°, which do not exist. PROJ
          refuses to transform them, PostGIS raises, and until today the caller got a
          `ServiceException` for what WMS says should be a picture with empty space in it.
          A client dragging a world map past the pole is doing something ordinary.

          <b>The transform is not clipped and that is the whole design.</b> `transform` still
          carries the *requested* box, so every pixel keeps the georeferencing the caller
          asked for and a client compositing tiles gets back the extent it named. Only the
          envelope that is queried and drawn is narrowed, so the rows outside simply are not
          there and their pixels stay background.

          <b>Geographic references only, and the limit is stated rather than looked up.</b>
          ±90° and ±180° are what `CRS:84` and the EPSG geographic block mean, which is the
          case a panning client hits and the case the suite exercises. A projected
          reference's mathematical domain is [Q-123](../../docs/open-questions.md)'s unsolved
          lookup — `postgis_srs` offers an *area of use*, which is a different thing — so a
          projected box is passed through untouched and D-163 stays open for it. Narrowing
          the half that is known, and saying so, beats leaving both halves broken.
        */
        Envelope query = transform.Buffered(plan.Margin);

        if (AxisOrder.IsGeographic(srid))
        {
            Envelope inside = new(
                Math.Max(query.MinX, -180),
                Math.Max(query.MinY, -90),
                Math.Min(query.MaxX, 180),
                Math.Min(query.MaxY, 90));

            // <b>Nothing of this map is inside the reference at all.</b> Drawing nothing is
            // the answer: the caller gets their requested extent as an empty image, which is
            // what a map of somewhere that cannot exist looks like.
            if (inside.MaxX <= inside.MinX || inside.MaxY <= inside.MinY)
            {
                return;
            }

            query = inside;
        }

        List<string> fields = [];

        foreach (string field in plan.Fields)
        {
            if (described.Find(field) is not null)
            {
                fields.Add(field);
            }
        }

        AttributePredicate? predicate = TimePredicate(time, described, layer.TimeField);

        ParsedWhere? where = null;

        if (predicate is not null
            && PredicateSql.TryEmit(
                predicate,
                [.. described.Fields.Select(f => f.Name)],
                LayerDefinition.Quote,
                out ParsedWhere emitted,
                out _))
        {
            where = emitted;
        }

        FeatureQuery features = new(
            limit: limit,
            fields: fields.Count > 0 ? fields : [],
            includeGeometry: true,
            spatial: new SpatialFilter(Rectangle(query)),
            outSrid: srid == layer.Definition.Srid ? null : srid,
            filterSrid: srid == layer.Definition.Srid ? null : srid,
            maxAllowableOffset: transform.UnitsPerPixel,
            where: where);

        /*
          <b>What the clip above could not reach, drawn as nothing rather than refused.</b>
          Clipping to ±90° keeps the request inside `CRS:84`; it does not make every point
          inside it transformable into the *layer's* reference. Web Mercator is undefined at
          the pole, so a box clipped to exactly 90° still fails — measured: the error changed
          from *exceeded limits* to *tolerance condition error*, one request to the next.

          <b>Catching is more general than knowing.</b> The alternative is a table of what
          each projection can represent, which is [Q-123](../../docs/open-questions.md)'s
          unsolved lookup wearing a different hat. If PROJ cannot put this area into the
          layer's reference, there is nothing of that layer there to draw, and an empty
          margin is exactly what WMS asks for.

          <b>Narrow on purpose.</b> `ErrorResponse.IsOutsideItsReference` is the single test,
          shared with the branch that turns the same fault into a 400 for surfaces that owe
          an error. Anything else propagates untouched: a swallowed database fault is a map
          that silently loses a layer, which is the failure this whole area is about.
        */
        try
        {
            await foreach (Feature feature
                in source.ReadAsync(features, cancellation).ConfigureAwait(false))
            {
                pass.Draw(feature);
            }
        }
        catch (PostgresException outside) when (ErrorResponse.IsOutsideItsReference(outside))
        {
            // <b>Optional, because two surfaces call this and only one has a logger to
            // hand.</b> Silence here would be the thing this repository dislikes most —
            // a layer that vanishes from a map with nothing said — so the caller that can
            // say it does, and MapServer's path is a line of work rather than a gap.
            if (log is not null)
            {
                Log.MapAreaOutsideReference(log, layer.Definition.Name, srid);
            }
        }
    }

    /// <summary>
    /// The <c>TIME</c> filter, as a predicate over the layer's own date column.
    /// </summary>
    /// <remarks>
    /// <b>Half-open, matching <see cref="TimeWindow"/>'s own definition.</b> A
    /// closed interval makes a midnight observation appear in two adjacent frames of
    /// an animation, which reads as duplicated data.
    /// </remarks>
    private static AttributePredicate.Conjunction? TimePredicate(
        TimeWindow? asked, LayerDescription described, string? declared)
    {
        if (asked is not { } window
            || TimeDimension.FieldOf(described.Fields, declared) is not { } field)
        {
            return null;
        }

        return new AttributePredicate.Conjunction(
            new AttributePredicate.Comparison(
                field, ComparisonOperator.GreaterThanOrEqual, window.From),
            new AttributePredicate.Comparison(
                field, ComparisonOperator.LessThan, window.Until));
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

    /// <summary>
    /// Whether a CRS measures in degrees.
    /// </summary>
    /// <remarks>
    /// <b>The two codes that matter, and it is not a general answer.</b> Scale
    /// denominators and zoom levels are computed from this, and the general form is
    /// <c>spatial_ref_sys</c> — a round trip per map for a value that is 4326 in
    /// almost every request. The same gap as
    /// [Q-123](../../docs/open-questions.md), from the other direction.
    /// </remarks>
    private static bool IsGeographic(int srid) =>
        Graticula.Geometries.AxisOrder.IsGeographic(srid);

    // ---------- GetFeatureInfo ----------

    private static async Task FeatureInfoAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        WmsRequest request,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (FeatureInfoWriter.Resolve(request.InfoFormat) is not { } mediaType)
        {
            await RefuseAsync(
                context,
                request.Version,
                new WmsFault(
                    WmsFault.InvalidFormat,
                    $"This server answers GetFeatureInfo as "
                    + $"{string.Join(", ", CapabilitiesDocument.InfoFormats)}; it was asked for "
                    + $"`{request.InfoFormat}`.",
                    "INFO_FORMAT"),
                cancellation).ConfigureAwait(false);

            return;
        }

        IReadOnlyList<PublishedLayer>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        // Null means the refusal is already written: no listing, so nothing to filter.
        if (visible is null)
        {
            return;
        }

        PixelTransform transform = new(request.Extent, request.Width, request.Height);
        Envelope around = FeatureInfoWriter.Around(transform, request.PixelX, request.PixelY);

        List<FeatureInfoWriter.Hits> hits = [];

        foreach (string name in request.QueryLayers)
        {
            if (Find(visible, name) is not { } layer)
            {
                await RefuseAsync(
                    context,
                    request.Version,
                    new WmsFault(
                        WmsFault.LayerNotDefined,
                        $"`{name}` is not a layer this server publishes to you.",
                        "QUERY_LAYERS"),
                    cancellation).ConfigureAwait(false);

                return;
            }

            /*
              <b>`LayerNotQueryable`, and here it is the right code rather than the nearest one
              — [D-180](../../docs/architecture-debt.md),
              [ADR-049](../../docs/adr/ADR-049-a-face-refuses-in-its-own-vocabulary.md).</b> WMS
              1.3.0 defines it as *GetFeatureInfo applied to a Layer which is not declared
              queryable*, which is exactly a service whose ceiling excludes `Query`. `GetMap`
              gets `OperationNotSupported` because this code does not reach that far, and the
              two are deliberately different for that reason rather than by accident.
            */
            if (CapabilityCeilings.Refuses(layer, "Query"))
            {
                await RefuseAsync(
                    context,
                    request.Version,
                    new WmsFault(
                        WmsFault.LayerNotQueryable,
                        CapabilityCeilings.Explain(layer, "Query"),
                        "QUERY_LAYERS"),
                    cancellation).ConfigureAwait(false);

                return;
            }

            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            // <b>Every column, named.</b> `FeatureQuery.Fields` empty means identity
            // and geometry only, so a GetFeatureInfo that left it empty answers with
            // a feature id and no attributes — which is what this returned the first
            // time it was run, and it looks like data with no columns rather than a
            // query that asked for none.
            FeatureQuery query = new(
                limit: request.FeatureCount,
                fields: [.. described.Fields.Select(f => f.Name)],
                includeGeometry: false,
                spatial: new SpatialFilter(Rectangle(around)),
                filterSrid: request.Srid == layer.Definition.Srid ? null : request.Srid);

            List<Feature> found = [];

            await foreach (Feature feature in source.ReadAsync(query, cancellation).ConfigureAwait(false))
            {
                found.Add(feature);
            }

            hits.Add(new FeatureInfoWriter.Hits(layer.Definition.Name, found));
        }

        string document = FeatureInfoWriter.Write(
            mediaType,
            hits,
            (transform.MapX(request.PixelX), transform.MapY(request.PixelY)),
            request.Srid);

        context.Response.ContentType = mediaType + "; charset=utf-8";
        await context.Response.WriteAsync(document, cancellation).ConfigureAwait(false);
    }

    // ---------- GetLegendGraphic ----------

    private static async Task LegendAsync(
        HttpContext context,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        WmsRequest request,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer>? visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

        // Null means the refusal is already written: no listing, so nothing to filter.
        if (visible is null)
        {
            return;
        }

        if (Find(visible, request.Layers[0]) is not { } layer)
        {
            await RefuseAsync(
                context,
                request.Version,
                new WmsFault(
                    WmsFault.LayerNotDefined,
                    $"`{request.Layers[0]}` is not a layer this server publishes to you.",
                    "LAYER"),
                cancellation).ConfigureAwait(false);

            return;
        }

        SymbologyPlan plan = layer.Symbology is { Length: > 0 } stored
            ? SymbologyPlan.Compile(stored)
            : SymbologyPlan.Default(layer.Definition.Name, layer.GeometryType);

        // <b>WIDTH and HEIGHT size a swatch, not the image.</b> A classified style
        // draws a row per class and the image is as tall as it needs to be — see
        // LegendGraphic, and Q-131 for why that is answerable without touching the
        // data. A layer with no classification still gets exactly the image it always
        // got: one swatch, at the size the client asked for.
        using IMapCanvas canvas = LegendGraphic.Draw(
            canvases,
            plan,
            layer.GeometryType,
            (request.Width, request.Height),
            request.Transparent ? Rgba.Transparent : Rgba.White);

        byte[] image = canvas.Encode(request.Format, 90);

        context.Response.ContentType = WmsNames.MediaTypeOf(request.Format);
        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }
}
