using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// The ArcGIS MapServer face: a rendered map service in Esri's own vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>The face the owner preferred, built the same day as the one they asked
/// for.</b> [ADR-004](../../docs/adr/ADR-004-rendering-engine.md) §0 carries their
/// 2026-08-13 statement — *"Prefer ArcGIS MapServer capability"* — and it was
/// unbuildable for as long as nothing could draw.
/// [ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) §5.5: once the renderer
/// exists this is a different spelling of extent, size, format and layer list.
/// </para>
/// <para>
/// <b>Nothing here draws.</b> Every operation resolves the catalogue, applies
/// sharing, and hands the same <see cref="MapRenderer"/> the WMS face uses the same
/// <see cref="SymbologyPlan"/>. Two rendered faces that each drew would eventually
/// draw differently, and the difference would be found by a user comparing them.
/// </para>
/// </remarks>
internal static class MapServerEndpoints
{
    /// <summary>
    /// What this face offers, in ArcGIS's own vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Map" alone for a while, and it used to say "Map,Query,Data".</b> There was no
    /// <c>/MapServer/{id}/query</c> route — ADR-041 §5.5 scoped this face to export, identify
    /// and legend — so the document promised an operation that answered 404, which the
    /// correctness gate found by reading the claim and then trying it.
    /// </para>
    /// <para>
    /// <b>"Map,Query,Data" again since 2026-09-15, because the route now exists.</b> The Maps
    /// SDK's MapImageLayer queries a sublayer at that address for pop-ups, so the omission cost
    /// every click on a drawn map. <c>Program</c> maps it onto the FeatureServer query handler;
    /// <c>GateFindingsTests</c> still asserts that the claim and the route agree.
    /// </para>
    /// </remarks>
    private const string Capabilities = "Map,Query,Data";

    /// <summary>How many features one identify may return per layer.</summary>
    private const int MaximumIdentifyResults = 20;

    /// <summary>The methods every read operation on this face answers.</summary>
    /// <remarks>
    /// <b>`GET` and `POST`, because the REST specification documents both and this face
    /// answered a bare 405 to one of them.</b>
    /// [D-139](../../docs/architecture-debt.md). `export` is the operation that needs it
    /// most: a client sending a dynamic layer definition or a long list of layer visibilities
    /// runs out of URL long before it runs out of things to say.
    ///
    /// <b>Accepting a posted parameter does not make a cookie work for POST</b> —
    /// `Authentication.CookieToken` refuses anything but GET and HEAD, deliberately. See
    /// `ArcGisParameters`.
    /// </remarks>
    private static readonly string[] Read = ["GET", "POST"];

    /// <summary>Maps the surface under both service-path shapes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            // <b>export and identify are registered before {layerId:int}</b>, and the
            // route constraint is what keeps them apart: `export` is not an integer,
            // so it cannot be mistaken for a layer. Without the constraint a client
            // asking for `/MapServer/export` would be answered with a 404 about
            // layer "export", which reads as a missing layer rather than a missing
            // route.
            app.MapMethods($"{prefix}/{{serviceName}}/MapServer", Read, ServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/export", Read, ExportAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/identify", Read, IdentifyAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/find", Read, FindAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/legend", Read, LegendAsync)
                .Governed(SharingGovernedExtensions.ByService);

            // All Layers and Tables, which ArcGIS Pro asks for by POST before it draws the service.
            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/layers", Read, AllLayersAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapMethods($"{prefix}/{{serviceName}}/MapServer/{{layerId:int}}", Read, LayerAsync)
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    /// <summary>
    /// An ArcGIS error document, which is a 200 carrying a refusal.
    /// </summary>
    /// <remarks>
    /// <b>Inherited, not chosen</b>, the same way WMS service exceptions are: every
    /// Esri client reads <c>error.code</c> out of a successful response, and several
    /// treat a 4xx as a transport failure and never open the body.
    /// </remarks>
    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Json(new
        {
            error = new
            {
                code,
                message,
                details = Array.Empty<string>(),
            },
        }).ExecuteAsync(context);

    // ---------- documents ----------

    private static async Task ServiceAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null || !await DrawableAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        List<FeatureServerMetadataWriter.ServiceLayer> layers =
            await LayersOfAsync(contexts, projector, service, cancellation).ConfigureAwait(false);

        object document = MapServerMetadataWriter.Service(
            layers,
            CapabilitiesOf(service),
            settings.MaximumImageWidth,
            settings.MaximumImageHeight,

            // The page size, which this face's query shares with the FeatureServer's — V-70.
            service.Limits.Cost.PageSize(
                await ServerPageSize.OfAsync(context, cancellation).ConfigureAwait(false),
                settings.MaximumRecordCount),
            await TimeOfServiceAsync(service, contexts, cancellation).ConfigureAwait(false),
            service.Description,
            service.CopyrightText,
            GroupsOf(service),

            // ADR-213: its tables, as ArcGIS's map service lists them; queried at /MapServer/{id}/query, never drawn.
            [.. service.Tables.Select(t => new FeatureServerMetadataWriter.ServiceTable(t.LayerIndex, t.DisplayName)
            {
                CatalogName = t.Definition.Name,
            })]);

        // The portal item this face is, as ArcGIS's service documents name it (ServiceItem).
        string serviceItemId = PortalEndpoints.ItemIdOf(service, "MapServer");
        document = ServiceItem.Apply(context, document, serviceItemId);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            string path = context.Request.Path;

            await Results.Content(
                RestDirectory.Document(
                    path,
                    $"{service.QualifiedName} (MapServer)",
                    document,
                    // <b>A viewer, because until 2026-08-21 this face had none and it
                    // is the face whose whole purpose is to draw.</b> These two links
                    // read "Export" and "Legend": a single 800x600 PNG of the full
                    // extent, and a JSON document. Neither is a map — you cannot zoom
                    // a PNG — so the feature face had two viewers and the map face had
                    // none, which is backwards. The owner asked why on 2026-08-21 and
                    // the answer was that the viewers were written when FeatureServer
                    // was the only face and nobody went back.
                    //
                    // <b>`face=mapserver` rather than a second page.</b> The two
                    // viewers already resolve a service and frame it; what changes is
                    // the layer type and what the picker means. A copied page would be
                    // a second place for the same bug — which is D-119's shape and it
                    // has recurred twice.
                    links:
                    [
                        ("Map", "/studio/view.html?face=mapserver"
                            + $"&service={Uri.EscapeDataString(service.QualifiedName)}"),
                        // `face=mapserver`, 2026-10-10: without it the Map Viewer tried FeatureServer first and opened
                        // this service as features — the map face's link showing the other face.
                        ("Map Viewer", "/studio/webmap.html?face=mapserver"
                            + $"&service={Uri.EscapeDataString(service.QualifiedName)}"),
                        ("ArcGIS SDK", "/studio/map.html?face=mapserver"
                            + $"&service={Uri.EscapeDataString(service.QualifiedName)}"),
                        ("Export", $"{path}/export?{ExtentText(layers)}"
                            + $"&size=800,600&format=png&transparent=true&f=image"),
                        ("Legend", $"{path}/legend?f=json"),
                    ],
                    linksLabel: "View in",
                    formats: [WmsEndpoints.DirectoryLink(null, null, 0)],
                    serviceItemId: serviceItemId),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One layer's MapServer document, and the shape it was built from — shared by the layer route and
    /// <c>layers</c>, so the two cannot describe one layer two ways.</summary>
    private static async Task<(object Document, LayerDescription Described)> LayerDocumentAsync(
        HttpContext context,
        PublishedLayer layer,
        ServiceContexts contexts,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        (IFeatureSource layerSource, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        // <b>A table answers as a table — ADR-213</b>: its fields and its query, and nothing a drawing reads.
        if (layer.IsTable)
        {
            return (MapServerMetadataWriter.Table(
                layer.LayerIndex,
                layer.DisplayName,
                FieldsOf(layer, described),
                FeatureServerMetadataWriter.DisplayField(layer.Definition, described),
                layer.Cost.PageSize(
                    await ServerPageSize.OfAsync(context, cancellation).ConfigureAwait(false),
                    settings.MaximumRecordCount),
                CapabilityCeilings.Refuses(layer, "Query") ? string.Empty : "Query,Data",
                layer.Definition.IntegerIdentityColumn,
                layer.ServiceCopyrightText), described);
        }

        // V-73: measured the way WMS and the FeatureServer measure it, and cached in the same place.
        Graticula.Api.Wms.TimeDimension? time = await WmsEndpoints
            .TimeOfAsync(layerSource, layer, described, contexts, cancellation)
            .ConfigureAwait(false);

        (int servedSrid, Envelope? servedExtent) = await ServedAsync(
            layer, described.Extent, projector, cancellation).ConfigureAwait(false);

        // ADR-070: the export draws the layer only inside its range, so its own document says so too. It said 0 and 0
        // until 2026-10-10 while the service document and the legend carried the range — and an ArcGIS client reads a
        // layer's range from the layer.
        FeatureServerMetadataWriter.ServiceLayer entry = new(
            layer.LayerIndex,
            layer.DisplayName,
            layer.RequireGeometryType(),
            servedSrid,
            servedExtent)
        {
            ParentId = layer.ParentIndex,
            CatalogName = layer.Definition.Name,
            MinScale = layer.VisibleRange.MinScale,
            MaxScale = layer.VisibleRange.MaxScale,
        };

        // <b>The layer's own stored style, not a synthesised one.</b> This called
        // the two-argument `DrawingInfo`, which always invents an appearance from the
        // name and geometry — so this document reported a colour the server does not
        // draw, while the legend, the rendered map and the FeatureServer document all
        // agreed on the real one. Found by the correctness gate 2026-08-20 by asking
        // four faces about one layer.
        object drawingInfo = FeatureServerMetadataWriter.Drawing(
            layer.Definition.Name, layer.RequireGeometryType(), layer.Symbology, out _);

        object document = MapServerMetadataWriter.Layer(
            entry,
            FieldsOf(layer, described),
            drawingInfo,
            // The FeatureServer face's own rule, so the two documents of one layer name one field.
            FeatureServerMetadataWriter.DisplayField(layer.Definition, described),
            layer.Cost.PageSize(
                await ServerPageSize.OfAsync(context, cancellation).ConfigureAwait(false),
                settings.MaximumRecordCount),
            Labels(layer),
            CapabilityCeilings.Refuses(layer, "Query") ? string.Empty : Capabilities,
            layer.Definition.IntegerIdentityColumn,
            FeatureServerMetadataWriter.TimeInfo(time is null ? null : (time.Field, time.From, time.Until)),
            FeatureServerMetadataWriter.ArchivingInfo(described),
            layer.ServiceCopyrightText);

        return (document, described);
    }

    /// <summary>
    /// All Layers and Tables — every layer's document in one answer, as ArcGIS's MapServer gives it.
    /// </summary>
    /// <remarks>
    /// <b>ArcGIS Pro asks for this before it draws a map service, by POST</b>, and it was a 404 here: Pro added the
    /// layer and drew nothing (measured 2026-10-13 against the local fixture through a recording relay). The FeatureServer
    /// face has answered it since ADR-057 condition 5.
    /// </remarks>
    private static async Task AllLayersAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null || !await DrawableAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        List<(int Id, object Document)> layers = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            (object document, _) = await LayerDocumentAsync(context, layer, contexts, projector, settings, cancellation)
                .ConfigureAwait(false);
            layers.Add((layer.LayerIndex, document));
        }

        // D-296: the groups the service document lists, at their own indices, as the FeatureServer's `layers` has them.
        foreach (FeatureServerMetadataWriter.ServiceGroup group in GroupsOf(service))
        {
            layers.Add((group.Id, FeatureServerMetadataWriter.GroupLayerDocument(group, CapabilitiesOf(service))));
        }

        // ADR-213: each table's own document, in the array ArcGIS keeps them in.
        List<object> tables = [];

        foreach (PublishedLayer table in service.Tables)
        {
            (object document, _) = await LayerDocumentAsync(context, table, contexts, projector, settings, cancellation)
                .ConfigureAwait(false);
            tables.Add(document);
        }

        await Results.Ok(new
        {
            layers = layers.OrderBy(l => l.Id).Select(l => l.Document).ToArray(),
            tables = tables.ToArray(),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The layers beneath a group, at any depth, or none when the id is not a group's — D-296.</summary>
    private static List<int> LayersBeneath(PublishedService service, int id)
    {
        if (service.Group(id) is null)
        {
            return [];
        }

        List<int> beneath = [];
        Queue<int> open = new([id]);

        while (open.TryDequeue(out int at))
        {
            foreach (int child in service.ChildrenOf(at))
            {
                if (service.Group(child) is not null)
                {
                    open.Enqueue(child);
                }
                else
                {
                    beneath.Add(child);
                }
            }
        }

        return beneath;
    }

    /// <summary>The service's group layers, as the FeatureServer face lists them — D-296.</summary>
    private static List<FeatureServerMetadataWriter.ServiceGroup> GroupsOf(PublishedService service) =>
    [
        .. service.Groups
            .Select(g => new FeatureServerMetadataWriter.ServiceGroup(
                g.Index, g.Name, g.ParentIndex, service.ChildrenOf(g.Index)))
            .OrderBy(g => g.Id),
    ];

    private static async Task LayerAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        HostSettings settings,
        CancellationToken cancellation)
    {
        // <b>A group answers at its own index — D-296.</b> The service document lists it now, so a client following
        // `subLayerIds`, or ArcGIS Pro building its table of contents, asks for it here; a 404 for an id the service
        // advertised is the inconsistency that makes a client give up on the service.
        PublishedService? owning = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        // A service of tables only has no map service at all, at any of its addresses (ADR-213).
        if (owning is null || !await DrawableAsync(context, owning).ConfigureAwait(false))
        {
            return;
        }

        if (owning.Group(layerId) is not null)
        {

            await Results.Ok(FeatureServerMetadataWriter.GroupLayerDocument(
                    GroupsOf(owning).First(g => g.Id == layerId), CapabilitiesOf(owning)))
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        (object document, LayerDescription described) =
            await LayerDocumentAsync(context, layer, contexts, projector, settings, cancellation).ConfigureAwait(false);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            await Results.Content(
                RestDirectory.Document(
                    context.Request.Path,
                    $"{layer.ServiceName} - {layer.Definition.Name} ({layer.LayerIndex})",
                    document,

                    // A table is not drawn, so it has no WMS to open it in (ADR-213).
                    formats: layer.IsTable ? [] :
                    [
                        WmsEndpoints.DirectoryLink(
                            layer.Definition.Name,
                            InitialExtents.Known(layer, layer.Definition.Srid, described.Extent, contexts, projector)
                                ?? described.Extent,
                            layer.Definition.Srid,
                            layer.VisibleRange.MinScale),
                    ]),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A layer's or a table's fields, as this face's documents give them.</summary>
    private static object[] FieldsOf(PublishedLayer layer, LayerDescription described) =>
        [.. described.Fields.Select(f => (object)new
        {
            name = f.Name,
            // V-52: the object id and the GlobalID by their role, as the FeatureServer document names them.
            type = FeatureServerMetadataWriter.FieldTypeOf(layer.Definition, described, f),
            // The same label the FeatureServer face gives the same column (ADR-063); two
            // faces over one layer disagreeing about a label is D-179's shape.
            alias = f.Label,
            nullable = f.Nullable,
            length = FeatureServerMetadataWriter.LengthOf(f),
        })];

    /// <summary>
    /// Whether a layer's stored style draws labels.
    /// </summary>
    /// <remarks>
    /// <b>Asked of the style rather than assumed.</b> `hasLabels` was written as
    /// false for every layer, including ones whose stored document has a `symbol`
    /// layer and whose map does draw names. A client reads this to decide whether to
    /// offer a label toggle.
    /// </remarks>
    private static bool Labels(PublishedLayer layer)
    {
        if (layer.Symbology is not { Length: > 0 } stored)
        {
            return false;
        }

        try
        {
            return SymbologyPlan.Compile(stored).HasLabels;
        }
        catch (SymbologyException)
        {
            // A style this server stores and cannot compile answers the question with
            // "no labels" rather than failing the whole document. The style itself is
            // the defect and GetMap will say so.
            return false;
        }
    }

    // ---------- export ----------

    private static async Task ExportAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        HostSettings settings,
        IProjector projector,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null || !await DrawableAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        Func<string, string?> exportParameters =
            await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (!MapServerExportParameters.TryParse(
                exportParameters,
                service.Layers,
                new WidthHeight(settings.MaximumImageWidth, settings.MaximumImageHeight),
                out MapServerExportParameters? asked,
                out string? error,
                id => LayersBeneath(service, id)))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // A bbox in one reference and an image in another: the box is carried across along its edges, as ImageServer's
        // is, and drawn there.
        if (asked!.NeedsReframing)
        {
            if (!await projector.KnowsAsync(asked.BoxSrid, cancellation).ConfigureAwait(false)
                || !await projector.KnowsAsync(asked.ImageSrid, cancellation).ConfigureAwait(false)
                || await ImageServerEndpoints.ProjectEnvelopeAsync(asked.Extent, asked.BoxSrid, asked.ImageSrid, projector, cancellation)
                    .ConfigureAwait(false) is not { } carried)
            {
                await RefuseAsync(context, 400,
                    $"The bbox could not be carried from EPSG:{asked.BoxSrid.ToString(CultureInfo.InvariantCulture)} into "
                    + $"EPSG:{asked.ImageSrid.ToString(CultureInfo.InvariantCulture)}: one of them is not a reference this "
                    + "server's projection database has, or the box lies outside where the projection is defined.")
                    .ConfigureAwait(false);
                return;
            }

            asked = asked.Reframed(carried);
        }

        if (!TryTime(exportParameters("time"), out Graticula.Api.Wms.TimeWindow? window, out string? timeError))
        {
            await RefuseAsync(context, 400, timeError!).ConfigureAwait(false);
            return;
        }

        (Dictionary<Guid, AttributePredicate> definitions, string? definitionError) = await LayerDefinitions
            .ParseAsync(asked!.Definitions, service.Layers, contexts, cancellation)
            .ConfigureAwait(false);

        if (definitionError is not null)
        {
            await RefuseAsync(context, 400, definitionError).ConfigureAwait(false);
            return;
        }

        if (await ReadableAsync(context, asked.Layers).ConfigureAwait(false) is not { } drawn)
        {
            return;
        }

        PixelTransform transform = new(asked.Extent, asked.Width, asked.Height);

        using IMapCanvas canvas = canvases.Create(asked.Width, asked.Height);

        MapRenderer renderer = new(canvas, transform, IsGeographic(asked.ImageSrid));

        renderer.Clear(
            asked.Transparent && asked.Format == MapImageFormat.Png
                ? Rgba.Transparent
                : Rgba.White);

        foreach (PublishedLayer layer in drawn)
        {
            await WmsEndpoints
                .DrawLayerAsync(
                    contexts, renderer, transform, layer, asked.ImageSrid, window,
                    settings.MaximumRecordCount, cancellation, honourVisibleRange: true,
                    definition: definitions.GetValueOrDefault(layer.Id),
                    whole: true)
                .ConfigureAwait(false);
        }

        renderer.FinishLabels();

        byte[] image = canvas.Encode(asked.Format, settings.JpegQuality);

        // <b>`f=json` returns where the image is, not the image.</b> The JavaScript
        // API places an element from this document and then fetches the href, so the
        // href has to be an address the same client can ask for — which is this
        // request with `f=image`.
        // <b>The first value, and case-insensitively.</b> A query string carrying `f`
        // twice — which happens when a client appends its own format to a URL that
        // already has one — makes `Query["f"]` the string "json,image", and a plain
        // equality check then reads it as neither. The image came back for a request
        // that asked for JSON, which is a wrong answer with a 200 on it.
        if (ArcGisResponseFormat.WantsJson(context))
        {
            // D-299: an output address that needs no token, as ArcGIS's output directory, rather than this request
            // again with f=image, which replayed a token= from the query string into the document.
            string href = ExportOutputs.Keep(
                context, image, asked.Format == MapImageFormat.Png ? "image/png" : "image/jpeg");

            await Results.Ok(MapServerMetadataWriter.Export(
                href,
                asked.Width,
                asked.Height,
                asked.Extent,
                asked.ImageSrid,
                MapServerMetadataWriter.Scale(asked.Extent, asked.Width, asked.ImageSrid)))
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        context.Response.ContentType = asked.Format == MapImageFormat.Png
            ? "image/png"
            : "image/jpeg";

        await context.Response.Body.WriteAsync(image, cancellation).ConfigureAwait(false);
    }

    // <b>Reading `f` moved to ArcGisResponseFormat, because this face was the only one
    // doing it.</b> `ImageServer/exportImage` ignored the parameter entirely and answered
    // PNG bytes for `f=json` — two faces on one server disagreeing about a parameter both
    // document. The careful part, that a query string carrying `f` twice makes
    // `Query["f"]` the single string "json,image", is worth having in one place rather
    // than rediscovered in the second.

    // ---------- find ----------

    /// <summary>The most results one find answers.</summary>
    private const int MaximumFindResults = 500;

    /// <summary>
    /// ArcGIS's <c>MapServer/find</c>: a text searched for in the layers' fields — answered 404 until the ArcGIS gap
    /// review of 2026-10-17.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through the where grammar, with the text bound.</b> Each searched field becomes
    /// <c>UPPER(field) LIKE UPPER('%text%')</c> (or <c>=</c> when <c>contains=false</c>), parsed by
    /// <see cref="WhereClause"/> as a caller's where is, so the text is a parameter and never SQL. Case does not
    /// matter, as in ArcGIS.
    /// </para>
    /// <para>
    /// <b>Text fields by default</b>; <c>searchFields</c> names others, and a number is then matched for equality when
    /// the text is one. One result per feature and field that matched, ArcGIS's shape, at most
    /// <see cref="MaximumFindResults"/>. <c>layerDefs</c> is refused rather than ignored: a find that ignored a
    /// definition would return features the map does not draw.
    /// </para>
    /// </remarks>
    private static async Task FindAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null || !await DrawableAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        Func<string, string?> parameter = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);
        string text = parameter("searchText") ?? string.Empty;

        if (text.Length == 0)
        {
            await RefuseAsync(context, 400, "'searchText' is required: the text to find.").ConfigureAwait(false);
            return;
        }

        if (text.Length > 256)
        {
            await RefuseAsync(context, 400, "'searchText' is at most 256 characters.").ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(parameter("layerDefs")))
        {
            await RefuseAsync(context, 400,
                "'layerDefs' is not applied by find here, and a find that ignored it would return features the map does "
                + "not draw. Search with the layer's own query and a where clause instead.").ConfigureAwait(false);
            return;
        }

        bool contains = !string.Equals(parameter("contains"), "false", StringComparison.OrdinalIgnoreCase);
        bool returnGeometry = !string.Equals(parameter("returnGeometry"), "false", StringComparison.OrdinalIgnoreCase);
        HashSet<string>? searchFields = parameter("searchFields") is { Length: > 0 } named
            ? new HashSet<string>(named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            : null;

        int? srid = null;
        if (parameter("sr") is { Length: > 0 } sr)
        {
            if (!int.TryParse(sr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSrid))
            {
                await RefuseAsync(context, 400, "'sr' is a well-known id, such as 4326 or 3857.").ConfigureAwait(false);
                return;
            }

            srid = parsedSrid;
        }

        List<PublishedLayer> asked = [];
        string layersText = parameter("layers") ?? string.Empty;
        List<PublishedLayer> drawable = [.. service.Layers.Where(l => l.Definition.GeometryColumn is { Length: > 0 })];

        if (layersText.Trim().Length == 0)
        {
            asked.AddRange(drawable);
        }
        else
        {
            foreach (string part in layersText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    await RefuseAsync(context, 400, $"'layers' is a list of layer ids, and '{part}' is not one.").ConfigureAwait(false);
                    return;
                }

                foreach (int index in LayersBeneath(service, id) is { Count: > 0 } beneath ? beneath : [id])
                {
                    if (drawable.FirstOrDefault(l => l.LayerIndex == index) is { } layer && !asked.Contains(layer))
                    {
                        asked.Add(layer);
                    }
                }
            }

            if (asked.Count == 0)
            {
                await RefuseAsync(context, 400, "`layers` named no layer this service has.").ConfigureAwait(false);
                return;
            }
        }

        if (await ReadableAsync(context, asked).ConfigureAwait(false) is not { } searched)
        {
            return;
        }

        // Upper-cased here: the grammar reads UPPER(field) as a case-insensitive comparison and takes the literal as written.
        string literal = "'" + (contains ? "%" + text + "%" : text).ToUpperInvariant().Replace("'", "''", StringComparison.Ordinal) + "'";
        bool numeric = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        List<object> results = [];

        foreach (PublishedLayer layer in searched)
        {
            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            string display = FeatureServerMetadataWriter.DisplayField(layer.Definition, described);
            Dictionary<string, FieldType> types = described.Fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.OrdinalIgnoreCase);

            foreach (FieldDescription field in described.Fields)
            {
                if (results.Count >= MaximumFindResults)
                {
                    break;
                }

                bool isText = field.Type == FieldType.Text;
                bool isNumber = field.Type is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
                    or FieldType.Single or FieldType.Double;

                if (searchFields is null ? !isText : !searchFields.Contains(field.Name) || !(isText || (isNumber && numeric)))
                {
                    continue;
                }

                string clause = isText
                    ? $"UPPER({field.Name}) {(contains ? "LIKE" : "=")} {literal}"
                    : $"{field.Name} = {text}";

                if (!WhereClause.TryParse(clause, [.. types.Keys], LayerDefinition.Quote, out ParsedWhere where, out string? unread, types))
                {
                    // Not silently: a search the grammar cannot read is this server's fault, not an empty answer.
                    throw new InvalidOperationException($"find built a clause the where grammar refused: {unread}");
                }

                FeatureQuery query = new(
                    limit: MaximumFindResults - results.Count,
                    fields: [.. described.Fields.Select(f => f.Name)],
                    includeGeometry: returnGeometry,
                    outSrid: srid is { } s && s != layer.Definition.Srid ? s : null,
                    where: where);

                await foreach (Feature feature in source.ReadAsync(query, cancellation).ConfigureAwait(false))
                {
                    Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

                    foreach (string name in feature.Schema.Names)
                    {
                        attributes[name] = feature[name];
                    }

                    results.Add(new
                    {
                        layerId = layer.LayerIndex,
                        layerName = layer.DisplayName,
                        foundFieldName = field.Name,
                        value = attributes.TryGetValue(field.Name, out object? found) ? found?.ToString() ?? string.Empty : string.Empty,
                        displayFieldName = display,
                        attributes,
                        geometryType = returnGeometry && feature.Geometry is not null
                            ? MapServerMetadataWriter.GeometryName(feature.Geometry.Kind)
                            : null,
                        geometry = returnGeometry && feature.Geometry is not null
                            ? (object?)Shape(feature.Geometry, srid ?? layer.Definition.Srid)
                            : null,
                    });
                }
            }
        }

        await Results.Ok(new { results }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- identify ----------

    private static async Task IdentifyAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null || !await DrawableAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        if (!MapServerIdentifyParameters.TryParse(
                await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false),
                service.Layers,
                out MapServerIdentifyParameters? asked,
                out string? error,
                id => LayersBeneath(service, id)))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        (Dictionary<Guid, AttributePredicate> definitions, string? definitionError) = await LayerDefinitions
            .ParseAsync(asked!.Definitions, service.Layers, contexts, cancellation)
            .ConfigureAwait(false);

        if (definitionError is not null)
        {
            await RefuseAsync(context, 400, definitionError).ConfigureAwait(false);
            return;
        }

        if (await ReadableAsync(context, asked.Layers).ConfigureAwait(false) is not { } identified)
        {
            return;
        }

        List<object> results = [];

        foreach (PublishedLayer layer in identified)
        {
            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            FeatureQuery query = new(
                limit: Math.Min(MaximumIdentifyResults, settings.MaximumRecordCount),
                fields: [.. described.Fields.Select(f => f.Name)],
                includeGeometry: asked.ReturnGeometry,
                spatial: new SpatialFilter(Rectangle(asked.Around)),
                outSrid: asked.Srid == layer.Definition.Srid ? null : asked.Srid,
                filterSrid: asked.Srid == layer.Definition.Srid ? null : asked.Srid,
                where: Emitted(definitions.GetValueOrDefault(layer.Id), described));

            // <b>The field the layer document names, not the first column — 2026-09-15.</b> This
            // took `Fields[0]`, which is the object id on nearly every table, so an identify on
            // Turkey's provinces answered `displayFieldName: objectid, value: 108` while the layer
            // document said `displayField: il`. A pop-up titled with a row number is the result.
            string display = FeatureServerMetadataWriter.DisplayField(layer.Definition, described);

            await foreach (Feature feature in
                source.ReadAsync(query, cancellation).ConfigureAwait(false))
            {
                Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

                foreach (string name in feature.Schema.Names)
                {
                    attributes[name] = feature[name];
                }

                results.Add(MapServerMetadataWriter.IdentifyResult(
                    layer.LayerIndex,
                    layer.DisplayName,
                    display,
                    display.Length > 0 && attributes.TryGetValue(display, out object? value)
                        ? value?.ToString() ?? string.Empty
                        : feature.Id,
                    attributes,
                    asked.ReturnGeometry && feature.Geometry is not null
                        ? MapServerMetadataWriter.GeometryName(feature.Geometry.Kind)
                        : null,
                    asked.ReturnGeometry && feature.Geometry is not null
                        ? Shape(feature.Geometry, asked.Srid)
                        : null));
            }
        }

        await Results.Ok(new { results }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A parsed layer definition as the statement fragment a query carries, or null.</summary>
    private static ParsedWhere? Emitted(AttributePredicate? definition, LayerDescription described)
    {
        if (definition is null)
        {
            return null;
        }

        return PredicateSql.TryEmit(
                definition, [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote,
                out ParsedWhere emitted, out string? error, dialect: PredicateSql.PostgreSqlFor(described.Fields, arcGisText: true))
            ? emitted
            : throw new InvalidOperationException($"A layer definition parsed and did not emit: {error}");
    }

    /// <summary>
    /// One geometry as the Esri JSON object an identify result carries.
    /// </summary>
    /// <remarks>
    /// <b>Through the existing writer, and it costs a round trip through text.</b>
    /// <see cref="ArcGisGeometryWriter"/> writes into a <c>Utf8JsonWriter</c> because
    /// the query face streams thousands of features and must not build an object per
    /// shape; identify returns at most twenty, so paying a serialise-and-reparse here
    /// buys one writer for both faces. A second geometry writer that produced objects
    /// would be a second place for the ring-winding rules to be right.
    /// </remarks>
    private static System.Text.Json.JsonElement Shape(Geometry geometry, int srid)
    {
        using System.IO.MemoryStream stream = new();

        using (System.Text.Json.Utf8JsonWriter writer = new(stream))
        {
            ArcGisGeometryWriter.Write(writer, geometry, srid);
        }

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(stream.ToArray());

        return document.RootElement.Clone();
    }

    // ---------- legend ----------

    private static async Task LegendAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IMapCanvasFactory canvases,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null || !await DrawableAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        const int Swatch = 20;

        List<object> layers = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            if (layer.Definition.GeometryColumn is not { Length: > 0 })
            {
                continue;
            }

            (_, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            SymbologyPlan plan = layer.Symbology is { Length: > 0 } stored
                ? SymbologyPlan.Compile(stored)
                : SymbologyPlan.Default(layer.Definition.Name, layer.RequireGeometryType());

            // <b>One entry per class, which is what this response's shape always
            // wanted.</b> [Q-131](../../docs/open-questions.md) closed by finding that
            // the classes are in the style rather than in the data: a `match` writes
            // its labels and a `step` its breaks, so enumerating them costs no query
            // on an endpoint a client calls to draw a table of contents. A layer whose
            // style does not classify still gets exactly what it got before -- one
            // swatch, with an empty label, because there the swatch *is* the layer.
            List<MapServerMetadataWriter.LegendEntry> entries = [];

            if (plan.LegendClasses() is { } axis)
            {
                foreach (StyleExpression.ClassCase each in axis.Cases)
                {
                    if (entries.Count == Graticula.Api.Wms.LegendGraphic.MaximumRows)
                    {
                        break;
                    }

                    using IMapCanvas swatch = Graticula.Api.Wms.LegendGraphic.DrawClass(
                        canvases,
                        plan,
                        layer.RequireGeometryType(),
                        (Swatch, Swatch),
                        Rgba.Transparent,
                        axis.Field,
                        each.Value);

                    entries.Add(new MapServerMetadataWriter.LegendEntry(
                        each.Label, swatch.Encode(MapImageFormat.Png, 90)));
                }
            }
            else
            {
                using IMapCanvas swatch = Graticula.Api.Wms.LegendGraphic.DrawClass(
                    canvases,
                    plan,
                    layer.RequireGeometryType(),
                    (Swatch, Swatch),
                    Rgba.Transparent,
                    null,
                    null);

                entries.Add(new MapServerMetadataWriter.LegendEntry(
                    string.Empty, swatch.Encode(MapImageFormat.Png, 90)));
            }

            layers.Add(MapServerMetadataWriter.LegendLayer(
                new FeatureServerMetadataWriter.ServiceLayer(
                    layer.LayerIndex,
                    layer.DisplayName,
                    layer.RequireGeometryType(),
                    layer.Definition.Srid,
                    described.Extent)
                {
                    MinScale = layer.VisibleRange.MinScale,
                    MaxScale = layer.VisibleRange.MaxScale,
                },
                entries,
                Swatch,
                Swatch));
        }

        await Results.Ok(new { layers }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- shared ----------

    /// <summary>
    /// Whether this service's feature face is switched on, and a refusal when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[D-123](../../docs/architecture-debt.md), on the door built two commits
    /// after it closed.</b> That row is about a face an operator switched off staying
    /// open on a new surface, and it says a second protocol quietly reopening a closed
    /// door is the failure a new surface is most likely to introduce. WFS was
    /// repaired, WMS and OGC API Features copied the repair — **and this face was
    /// built the same day without it**, so a service with `servesFeatures` false
    /// answered a full document and rendered images of its data.
    /// </para>
    /// <para>
    /// <b>404, matching the FeatureServer door.</b> `ServiceLookup.LayerAsync` answers
    /// the same way for the same reason (ADR-031 condition 2): a face configured off
    /// is absent, not forbidden.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The asked layers this service answers <c>Query</c> on, or null with a 403 written when it answers it on
    /// none of them — V-72, the fourth ArcGIS review.
    /// </summary>
    /// <remarks>
    /// <b>Drawing and identifying are reads, and a ceiling without Query refuses reads.</b> WMS GetMap and
    /// GetFeatureInfo already refused such a layer, as did the thumbnail and FeatureServer; this face drew it
    /// and <c>identify</c> handed out its attributes, so a service an administrator had shut to reads during an
    /// incident (ADR-031 §2a) went on answering here. A request naming several layers draws the ones it may.
    /// </remarks>
    private static async Task<IReadOnlyList<PublishedLayer>?> ReadableAsync(
        HttpContext context, IReadOnlyList<PublishedLayer> asked)
    {
        List<PublishedLayer> readable = [.. asked.Where(layer => !CapabilityCeilings.Refuses(layer, "Query"))];

        if (asked.Count > 0 && readable.Count == 0)
        {
            await RefuseAsync(context, 403, CapabilityCeilings.Explain(asked[0], "Query")).ConfigureAwait(false);
            return null;
        }

        return readable;
    }

    /// <summary>
    /// ArcGIS's <c>time</c> — one instant or <c>start,end</c> in epoch milliseconds, either end <c>null</c> — as the
    /// window WMS draws with; null when there is none. V-73, the fourth ArcGIS review.
    /// </summary>
    /// <remarks>
    /// <b>It was ignored</b>: <c>export&amp;time=…</c> drew the same image as without it, byte for byte, while WMS
    /// <c>TIME</c> filtered the same layer. A layer without a time field is drawn whole, which is what ArcGIS
    /// does with a time the layer cannot apply.
    /// </remarks>
    internal static bool TryTime(string? raw, out Graticula.Api.Wms.TimeWindow? window, out string? error)
    {
        window = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        string[] ends = raw.Split(',', StringSplitOptions.TrimEntries);

        if (ends.Length > 2)
        {
            error = "'time' is one instant or 'start,end' in epoch milliseconds.";
            return false;
        }

        DateTimeOffset?[] moments = new DateTimeOffset?[ends.Length];

        for (int i = 0; i < ends.Length; i++)
        {
            if (ends[i].Length == 0 || ends[i].Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!long.TryParse(ends[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ms)
                || ms < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
                || ms > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                error = $"'time' has '{ends[i]}', which is not epoch milliseconds.";
                return false;
            }

            moments[i] = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        DateTimeOffset from = moments[0] ?? DateTimeOffset.MinValue;
        DateTimeOffset until = ends.Length == 1 ? from : moments[1] ?? DateTimeOffset.MaxValue;

        if (ends.Length == 1 && moments[0] is null)
        {
            return true;
        }

        window = new Graticula.Api.Wms.TimeWindow(from, until);
        return true;
    }

    /// <summary>The union of the time its layers carry, or null when none does.</summary>
    private static async Task<(DateTimeOffset? From, DateTimeOffset? Until)?> TimeOfServiceAsync(
        PublishedService service, ServiceContexts contexts, CancellationToken cancellation)
    {
        (DateTimeOffset? From, DateTimeOffset? Until)? span = null;

        foreach (PublishedLayer layer in service.Layers)
        {
            if (layer.Definition.GeometryColumn is not { Length: > 0 })
            {
                continue;
            }

            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            if (await WmsEndpoints.TimeOfAsync(source, layer, described, contexts, cancellation).ConfigureAwait(false)
                is not { } time)
            {
                continue;
            }

            span = span is not { } known
                ? (time.From, time.Until)
                : (Earlier(known.From, time.From), Later(known.Until, time.Until));
        }

        return span;

        static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;
        static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
    }

    /// <summary>What this face offers for a service: nothing to read when no layer answers Query.</summary>
    private static string CapabilitiesOf(PublishedService service) =>
        service.Layers.Count > 0 && service.Layers.All(layer => CapabilityCeilings.Refuses(layer, "Query"))
            ? string.Empty
            : Capabilities;

    private static async Task<bool> DrawableAsync(HttpContext context, PublishedService service)
    {
        // <b>A service of tables only has nothing to draw — ADR-213</b>, so it has no map service, as the services
        // directory already says by listing none; answering one with no layers sent a web map to add an empty map.
        if (service.Layers.Count == 0 && service.Tables.Count > 0)
        {
            await RefuseAsync(
                context,
                404,
                $"No map service '{service.QualifiedName}'. It holds only tables, which have no geometry to draw; read "
                + $"them at /rest/services/{service.QualifiedName}/FeatureServer.")
                .ConfigureAwait(false);

            return false;
        }

        if (service.Limits.AllowsFeatures(dataSupportsIt: true))
        {
            return true;
        }

        await RefuseAsync(
            context,
            404,
            $"No map service '{service.QualifiedName}'. This service's feature face is switched "
            + "off, so it draws nothing and describes nothing.")
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>The service's drawable layers, in the reference the service answers in.</summary>
    /// <remarks>
    /// <b>The reference the service names, and each extent moved into it —
    /// [D-229](../../docs/architecture-debt.md), owner decision 2026-09-11.</b> This face was the
    /// last to take a layer's table reference while every other face read the service's choice:
    /// FeatureServer, WMS, WFS and OGC API Features all answer in it, and a service set to
    /// EPSG:5253 over a 3857 table told a MapServer client *3857*. The same move the
    /// FeatureServer document makes, for the reason it gives: `ServedExtent` moves the box
    /// rather than relabelling it, and a layer whose extent will not go there keeps its own —
    /// the one pair the document can still prove.
    /// </remarks>
    private static async Task<List<FeatureServerMetadataWriter.ServiceLayer>> LayersOfAsync(
        ServiceContexts contexts,
        IProjector projector,
        PublishedService service,
        CancellationToken cancellation)
    {
        List<FeatureServerMetadataWriter.ServiceLayer> layers = [];

        foreach (PublishedLayer layer in service.Layers)
        {
            if (layer.Definition.GeometryColumn is not { Length: > 0 })
            {
                continue;
            }

            (_, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            (int srid, Envelope? extent) = await ServedAsync(
                layer, described.Extent, projector, cancellation).ConfigureAwait(false);

            layers.Add(new FeatureServerMetadataWriter.ServiceLayer(
                layer.LayerIndex,
                layer.DisplayName,
                layer.RequireGeometryType(),
                srid,
                extent)
            {
                // ADR-070: the export draws the layer only inside these, so the document says so.
                ParentId = layer.ParentIndex,
                CatalogName = layer.Definition.Name,
                MinScale = layer.VisibleRange.MinScale,
                MaxScale = layer.VisibleRange.MaxScale,

                // Where a map of it opens, when its whole extent is outside its range — InitialExtents.
                Initial = InitialExtents.Known(layer, srid, extent, contexts, projector),
            });
        }

        return layers;
    }

    /// <summary>A layer's reference and extent, as the service publishes them.</summary>
    /// <remarks>
    /// One method for the service document and the layer document, so the two cannot answer
    /// in different references for the same layer — which is the contradiction that held the
    /// FeatureServer face back for three days (D-229).
    /// </remarks>
    private static async Task<(int Srid, Envelope? Extent)> ServedAsync(
        PublishedLayer layer, Envelope? stored, IProjector projector, CancellationToken cancellation)
    {
        if (layer.ServedSrid is not { } wanted || wanted == layer.Definition.Srid)
        {
            return (layer.Definition.Srid, stored);
        }

        Envelope? moved = await ServedExtent
            .InAsync(stored, layer.Definition.Srid, wanted, projector, cancellation)
            .ConfigureAwait(false);

        return moved is null ? (layer.Definition.Srid, stored) : (wanted, moved);
    }

    /// <summary>The whole map's box as <c>bbox</c> and <c>bboxSR</c>, for the directory's link.</summary>
    /// <remarks>
    /// <b>The reference is stated rather than left to the default</b>, because this link was the
    /// one caller that relied on it: until 2026-09-11 an absent <c>bboxSR</c> meant 4326, so the
    /// link drew a 3857 service's metres as degrees, and after it the world fallback below would
    /// have been read in the map's units. The reference is the first layer's, as the document's is.
    /// </remarks>
    private static string ExtentText(List<FeatureServerMetadataWriter.ServiceLayer> layers)
    {
        Envelope whole = Envelope.Empty;

        foreach (FeatureServerMetadataWriter.ServiceLayer layer in layers)
        {
            if (layer.Extent is { IsEmpty: false } box)
            {
                whole = whole.IsEmpty ? box : whole.Union(box);
            }
        }

        // Where the layers are densest, once that is known (InitialExtents); until then inside their visible range,
        // which WmsEndpoints.InsideRange says why.
        if (FeatureServerMetadataWriter.InitialOf(layers) is { } initial)
        {
            whole = initial;
        }
        else if (!whole.IsEmpty)
        {
            whole = WmsEndpoints.InsideRange(
                whole,
                layers[0].Srid,
                layers.Where(l => l.MinScale > 0).Select(l => l.MinScale).DefaultIfEmpty(0).Min(),
                800,
                600);
        }

        return whole.IsEmpty
            ? "bbox=-180,-90,180,90&bboxSR=4326"
            : "bbox="
                + string.Join(
                    ',',
                    new[] { whole.MinX, whole.MinY, whole.MaxX, whole.MaxY }
                        .Select(MapServerMetadataWriter.Number))
                + "&bboxSR=" + layers[0].Srid.ToString(CultureInfo.InvariantCulture);
    }

    // <b>The case-insensitive parameter lookup this face used to carry is
    // `ArcGisParameters` now.</b> `ImageServerEndpoints` had its own copy of the same
    // loop, and two copies of a lookup is how the two faces came to disagree about
    // where a parameter may live: neither read a form, so neither answered a POST.


    private static Polygon Rectangle(Envelope extent) =>
        new(new LinearRing(XySequence.Wrap(
        [
            extent.MinX, extent.MinY,
            extent.MaxX, extent.MinY,
            extent.MaxX, extent.MaxY,
            extent.MinX, extent.MaxY,
            extent.MinX, extent.MinY,
        ])));

    private static bool IsGeographic(int srid) =>
        Graticula.Geometries.AxisOrder.IsGeographic(srid);
}

/// <summary>An image size, as a bound rather than a request.</summary>
/// <param name="Width">Widest.</param>
/// <param name="Height">Tallest.</param>
internal readonly record struct WidthHeight(int Width, int Height);
