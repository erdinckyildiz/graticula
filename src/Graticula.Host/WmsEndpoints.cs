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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (TimeDimension Dimension, DateTimeOffset Measured)>
        TimeExtents = new();

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
        PostgresLayerCatalog catalog,
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
                        context, catalog, contexts, projector, request, limits, cancellation)
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
        HttpContext context, WmsVersion version, WmsFault fault, CancellationToken cancellation)
    {
        // <b>200, and it is inherited rather than chosen.</b> A WMS service
        // exception is a successful response carrying an application refusal, and
        // several clients treat a 4xx as a transport failure and never read the body
        // — discarding the one sentence that says what was wrong.
        context.Response.StatusCode = 200;
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
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IProjector projector,
        WmsRequest request,
        WmsLimits limits,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

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
            limits);

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
            await TimeOfAsync(source, layer, described, cancellation).ConfigureAwait(false));
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
        Dictionary<int, List<int>> bySrid = [];

        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Extent is not { IsEmpty: false })
            {
                continue;
            }

            if (layers[i].Srid == AxisOrder.Wgs84)
            {
                // Already there. Projecting 4326 to 4326 is a round trip to be told
                // what we sent.
                layers[i] = layers[i] with { Geographic = layers[i].Extent };
                continue;
            }

            if (!bySrid.TryGetValue(layers[i].Srid, out List<int>? group))
            {
                group = [];
                bySrid[layers[i].Srid] = group;
            }

            group.Add(i);
        }

        foreach ((int srid, List<int> indices) in bySrid)
        {
            List<Geometry> corners = [];

            foreach (int index in indices)
            {
                Envelope box = layers[index].Extent!.Value;

                corners.Add(new Graticula.Geometries.Point(box.MinX, box.MinY));
                corners.Add(new Graticula.Geometries.Point(box.MaxX, box.MinY));
                corners.Add(new Graticula.Geometries.Point(box.MaxX, box.MaxY));
                corners.Add(new Graticula.Geometries.Point(box.MinX, box.MaxY));
            }

            IReadOnlyList<Geometry> projected;

            try
            {
                (projected, _) = await projector
                    .ProjectAsync(corners, srid, AxisOrder.Wgs84, cancellation)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // <b>A CRS this deployment cannot transform leaves the layer without
                // a geographic extent rather than without a capabilities
                // document.</b> One unusual layer must not make the whole server
                // look absent to every client that asks what it has.
                continue;
            }

            for (int i = 0; i < indices.Count; i++)
            {
                Envelope whole = Envelope.Empty;

                for (int corner = 0; corner < 4; corner++)
                {
                    int at = (i * 4) + corner;

                    if (at >= projected.Count)
                    {
                        break;
                    }

                    Envelope point = projected[at].Envelope;
                    whole = whole.IsEmpty ? point : whole.Union(point);
                }

                if (!whole.IsEmpty)
                {
                    layers[indices[i]] = layers[indices[i]] with { Geographic = Drawable(whole) };
                }
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

    private static async Task<TimeDimension?> TimeOfAsync(
        IFeatureSource source,
        PublishedLayer layer,
        LayerDescription described,
        CancellationToken cancellation)
    {
        if (TimeDimension.FieldOf(described.Fields) is not { } field)
        {
            return null;
        }

        if (TimeExtents.TryGetValue(layer.Id, out (TimeDimension Dimension, DateTimeOffset Measured) held)
            && DateTimeOffset.UtcNow - held.Measured < TimeExtentLifetime)
        {
            return held.Dimension;
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

        TimeExtents[layer.Id] = (dimension, DateTimeOffset.UtcNow);
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
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        WmsRequest request,
        HostSettings settings,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

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
            await DrawAsync(contexts, renderer, transform, layer, request, settings, cancellation)
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
    private static async Task DrawAsync(
        ServiceContexts contexts,
        MapRenderer renderer,
        PixelTransform transform,
        PublishedLayer layer,
        WmsRequest request,
        HostSettings settings,
        CancellationToken cancellation)
    {
        (IFeatureSource source, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        SymbologyPlan plan = layer.Symbology is { Length: > 0 } stored
            ? SymbologyPlan.Compile(stored)
            : SymbologyPlan.Default(layer.Definition.Name, layer.GeometryType);

        Envelope query = transform.Buffered(plan.Margin);

        List<string> fields = [];

        foreach (string field in plan.Fields)
        {
            if (described.Find(field) is not null)
            {
                fields.Add(field);
            }
        }

        AttributePredicate? predicate = TimePredicate(request, described);

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
            limit: settings.MaximumRecordCount,
            fields: fields.Count > 0 ? fields : [],
            includeGeometry: true,
            spatial: new SpatialFilter(Rectangle(query)),
            outSrid: request.Srid == layer.Definition.Srid ? null : request.Srid,
            filterSrid: request.Srid == layer.Definition.Srid ? null : request.Srid,
            maxAllowableOffset: transform.UnitsPerPixel,
            where: where);

        MapRenderer.Pass pass = renderer.Begin(plan);

        if (pass.DrawsNothing)
        {
            // Every style layer is switched off at this zoom. Reading the features
            // to draw none of them is the whole query for nothing.
            return;
        }

        await foreach (Feature feature in source.ReadAsync(features, cancellation).ConfigureAwait(false))
        {
            pass.Draw(feature);
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
        WmsRequest request, LayerDescription described)
    {
        if (request.Time is not { } window
            || TimeDimension.FieldOf(described.Fields) is not { } field)
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
    private static bool IsGeographic(int srid) => srid is 4326 or 4258 or 4269;

    // ---------- GetFeatureInfo ----------

    private static async Task FeatureInfoAsync(
        HttpContext context,
        PostgresLayerCatalog catalog,
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

        IReadOnlyList<PublishedLayer> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

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
        PostgresLayerCatalog catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        WmsRequest request,
        CancellationToken cancellation)
    {
        IReadOnlyList<PublishedLayer> visible =
            await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false);

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

        using IMapCanvas canvas = canvases.Create(request.Width, request.Height);

        LegendGraphic.Draw(
            canvas,
            plan,
            layer.GeometryType,
            request.Transparent ? Rgba.Transparent : Rgba.White);

        byte[] image = canvas.Encode(request.Format, 90);

        context.Response.ContentType = WmsNames.MediaTypeOf(request.Format);
        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }
}
