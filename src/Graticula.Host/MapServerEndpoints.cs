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
    /// <b>"Map" alone, and it used to say "Map,Query,Data".</b> There is no
    /// <c>/MapServer/{id}/query</c> route on this server — ADR-041 §5.5 scoped this
    /// face to export, identify and legend — so the document was promising an
    /// operation that answered 404, which the correctness gate found by reading the
    /// claim and then trying it. A claimed capability is a contract a client checks
    /// before it acts; the one thing it must never be is untrue. Querying the same
    /// data works at <c>/FeatureServer/{id}/query</c>, which is where the layer
    /// document's own links point.
    /// </remarks>
    private const string Capabilities = "Map";

    /// <summary>How many features one identify may return per layer.</summary>
    private const int MaximumIdentifyResults = 20;

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
            app.MapGet($"{prefix}/{{serviceName}}/MapServer", ServiceAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/MapServer/export", ExportAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/MapServer/identify", IdentifyAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/MapServer/legend", LegendAsync)
                .Governed(SharingGovernedExtensions.ByService);

            app.MapGet($"{prefix}/{{serviceName}}/MapServer/{{layerId:int}}", LayerAsync)
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
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        List<FeatureServerMetadataWriter.ServiceLayer> layers =
            await LayersOfAsync(contexts, service, cancellation).ConfigureAwait(false);

        object document = MapServerMetadataWriter.Service(
            layers,
            Capabilities,
            settings.MaximumImageWidth,
            settings.MaximumImageHeight,
            settings.MaximumRecordCount);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            string path = context.Request.Path;

            await Results.Content(
                RestDirectory.Document(
                    path,
                    $"{service.QualifiedName} (MapServer)",
                    document,
                    links:
                    [
                        ("Export", $"{path}/export?bbox={ExtentText(layers)}"
                            + $"&size=800,600&format=png&transparent=true&f=image"),
                        ("Legend", $"{path}/legend?f=json"),
                    ],
                    linksLabel: "View in",
                    formats: [WmsEndpoints.DirectoryLink(null, null, 0)]),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task LayerAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback catalog,
        ServiceContexts contexts,
        HostSettings settings,
        CancellationToken cancellation)
    {
        PublishedLayer? layer = await ServiceLookup
            .LayerAsync(context, catalog, serviceName, layerId, cancellation)
            .ConfigureAwait(false);

        if (layer is null)
        {
            return;
        }

        (_, LayerDescription described) =
            await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

        FeatureServerMetadataWriter.ServiceLayer entry = new(
            layer.LayerIndex,
            layer.Definition.Name,
            layer.GeometryType,
            layer.Definition.Srid,
            described.Extent);

        // <b>The layer's own stored style, not a synthesised one.</b> This called
        // the two-argument `DrawingInfo`, which always invents an appearance from the
        // name and geometry — so this document reported a colour the server does not
        // draw, while the legend, the rendered map and the FeatureServer document all
        // agreed on the real one. Found by the correctness gate 2026-08-20 by asking
        // four faces about one layer.
        object drawingInfo = FeatureServerMetadataWriter.Drawing(
            layer.Definition.Name, layer.GeometryType, layer.Symbology, out _);

        object document = MapServerMetadataWriter.Layer(
            entry,
            [.. described.Fields.Select(f => (object)new
            {
                name = f.Name,
                type = FeatureServerMetadataWriter.TypeName(f.Type),
                alias = f.Name,
                nullable = f.Nullable,
                length = f.MaxLength,
            })],
            drawingInfo,
            described.Fields.Count > 0 ? described.Fields[0].Name : null,
            settings.MaximumRecordCount,
            Labels(layer),
            Capabilities);

        if (RestDirectory.WantsHtml(context.Request.Query["f"], context.Request.Headers.Accept))
        {
            await Results.Content(
                RestDirectory.Document(
                    context.Request.Path,
                    $"{layer.ServiceName} - {layer.Definition.Name} ({layer.LayerIndex})",
                    document,
                    formats:
                    [
                        WmsEndpoints.DirectoryLink(
                            layer.Definition.Name, described.Extent, layer.Definition.Srid),
                    ]),
                "text/html; charset=utf-8")
                .ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        await Results.Ok(document).ExecuteAsync(context).ConfigureAwait(false);
    }

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
        CancellationToken cancellation)
    {
        PublishedService? service = await ServiceLookup
            .ServiceAsync(context, catalog, serviceName, cancellation)
            .ConfigureAwait(false);

        if (service is null)
        {
            return;
        }

        if (!MapServerExportParameters.TryParse(
                Parameter(context),
                service.Layers,
                new WidthHeight(settings.MaximumImageWidth, settings.MaximumImageHeight),
                out MapServerExportParameters? asked,
                out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        PixelTransform transform = new(asked!.Extent, asked.Width, asked.Height);

        using IMapCanvas canvas = canvases.Create(asked.Width, asked.Height);

        MapRenderer renderer = new(canvas, transform, IsGeographic(asked.ImageSrid));

        renderer.Clear(
            asked.Transparent && asked.Format == MapImageFormat.Png
                ? Rgba.Transparent
                : Rgba.White);

        foreach (PublishedLayer layer in asked.Layers)
        {
            await WmsEndpoints
                .DrawLayerAsync(
                    contexts, renderer, transform, layer, asked.ImageSrid, null,
                    settings.MaximumRecordCount, cancellation)
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
        if (string.Equals(Format(context), "json", StringComparison.OrdinalIgnoreCase))
        {
            string href = $"{context.Request.Scheme}://{context.Request.Host}"
                + $"{context.Request.Path}{Replaced(context.Request.QueryString.Value, "image")}";

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

    /// <summary>The response format asked for, taking the first if several were sent.</summary>
    private static string Format(HttpContext context)
    {
        Microsoft.Extensions.Primitives.StringValues values = default;

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair
            in context.Request.Query)
        {
            if (string.Equals(pair.Key, "f", StringComparison.OrdinalIgnoreCase))
            {
                values = pair.Value;
                break;
            }
        }

        return values.Count > 0 ? values[0] ?? string.Empty : string.Empty;
    }

    /// <summary>The query string with <c>f</c> replaced, so a href fetches the image.</summary>
    private static string Replaced(string? query, string format)
    {
        if (string.IsNullOrEmpty(query))
        {
            return $"?f={format}";
        }

        List<string> parts = [];

        foreach (string part in query.TrimStart('?').Split('&'))
        {
            if (!part.StartsWith("f=", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(part);
            }
        }

        parts.Add($"f={format}");

        return "?" + string.Join('&', parts);
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

        if (service is null)
        {
            return;
        }

        if (!MapServerIdentifyParameters.TryParse(
                Parameter(context),
                service.Layers,
                out MapServerIdentifyParameters? asked,
                out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        List<object> results = [];

        foreach (PublishedLayer layer in asked!.Layers)
        {
            (IFeatureSource source, LayerDescription described) =
                await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            FeatureQuery query = new(
                limit: Math.Min(MaximumIdentifyResults, settings.MaximumRecordCount),
                fields: [.. described.Fields.Select(f => f.Name)],
                includeGeometry: asked.ReturnGeometry,
                spatial: new SpatialFilter(Rectangle(asked.Around)),
                outSrid: asked.Srid == layer.Definition.Srid ? null : asked.Srid,
                filterSrid: asked.Srid == layer.Definition.Srid ? null : asked.Srid);

            string display = described.Fields.Count > 0 ? described.Fields[0].Name : string.Empty;

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
                    layer.Definition.Name,
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

        if (service is null)
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
                : SymbologyPlan.Default(layer.Definition.Name, layer.GeometryType);

            using IMapCanvas canvas = canvases.Create(Swatch, Swatch);

            Graticula.Api.Wms.LegendGraphic.Draw(
                canvas, plan, layer.GeometryType, Rgba.Transparent);

            layers.Add(MapServerMetadataWriter.LegendLayer(
                new FeatureServerMetadataWriter.ServiceLayer(
                    layer.LayerIndex,
                    layer.Definition.Name,
                    layer.GeometryType,
                    layer.Definition.Srid,
                    described.Extent),
                canvas.Encode(MapImageFormat.Png, 90),
                Swatch,
                Swatch));
        }

        await Results.Ok(new { layers }).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ---------- shared ----------

    private static async Task<List<FeatureServerMetadataWriter.ServiceLayer>> LayersOfAsync(
        ServiceContexts contexts, PublishedService service, CancellationToken cancellation)
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

            layers.Add(new FeatureServerMetadataWriter.ServiceLayer(
                layer.LayerIndex,
                layer.Definition.Name,
                layer.GeometryType,
                layer.Definition.Srid,
                described.Extent));
        }

        return layers;
    }

    private static string ExtentText(IReadOnlyList<FeatureServerMetadataWriter.ServiceLayer> layers)
    {
        Envelope whole = Envelope.Empty;

        foreach (FeatureServerMetadataWriter.ServiceLayer layer in layers)
        {
            if (layer.Extent is { IsEmpty: false } box)
            {
                whole = whole.IsEmpty ? box : whole.Union(box);
            }
        }

        return whole.IsEmpty
            ? "-180,-90,180,90"
            : string.Join(
                ',',
                new[] { whole.MinX, whole.MinY, whole.MaxX, whole.MaxY }
                    .Select(MapServerMetadataWriter.Number));
    }

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
