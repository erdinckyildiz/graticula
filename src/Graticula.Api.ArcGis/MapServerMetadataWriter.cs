using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis;

/// <summary>
/// The documents a MapServer publishes: the service, a layer, and a legend.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) §5.5, and it is the face
/// the owner asked for first.</b> ADR-004 §0 recorded their preference on
/// 2026-08-13 — *"Prefer ArcGIS MapServer capability"* — and it stayed unbuildable
/// while nothing could draw. It costs almost nothing now: a different spelling of
/// extent, size, format and layer list over the renderer WMS already uses.
/// </para>
/// <para>
/// <b>Written from the published ArcGIS REST API</b>, which is documentation rather
/// than an implementation. [CLAUDE.md](../../CLAUDE.md) §5 forbids reproducing
/// proprietary source; a documented wire format is exactly what a compatibility
/// surface is allowed to implement, and §51 keeps the vocabulary out of the core.
/// </para>
/// <para>
/// <b>What it does not claim.</b> No tile cache
/// (<c>singleFusedMapCache: false</c>), no dynamic layers, no time-aware map service
/// — this face draws what the catalogue holds, with each layer's stored symbology,
/// and says so rather than advertising capabilities the export handler would then
/// have to refuse.
/// </para>
/// </remarks>
public static class MapServerMetadataWriter
{
    /// <summary>The API version these documents claim.</summary>
    /// <remarks>The same one the FeatureServer face claims; one server, one version.</remarks>
    public const double CurrentVersion = FeatureServerMetadataWriter.CurrentVersion;

    /// <summary>The image formats <c>export</c> will write.</summary>
    /// <remarks>
    /// <b>The PNG spellings are aliases, not different encoders.</b> ArcGIS
    /// distinguishes PNG8, PNG24 and PNG32 by bit depth; this server writes one PNG
    /// and accepts all three names, because a client that asks for PNG24 and is
    /// refused has been refused over a detail it cannot act on. What it must never do
    /// is answer a JPEG request with a PNG, which is a different thing entirely.
    /// </remarks>
    public const string SupportedImageFormats = "PNG32,PNG24,PNG,JPG,JPEG";

    /// <summary>The service document at <c>/rest/services/{name}/MapServer</c>.</summary>
    /// <param name="layers">Its layers, in index order.</param>
    /// <param name="capabilities">What this service offers, comma-separated.</param>
    /// <param name="maximumWidth">The widest image export will draw.</param>
    /// <param name="maximumHeight">The tallest image export will draw.</param>
    /// <param name="maxRecordCount">What a query may return.</param>
    /// <returns>The document.</returns>
    public static object Service(
        IReadOnlyList<FeatureServerMetadataWriter.ServiceLayer> layers,
        string capabilities,
        int maximumWidth,
        int maximumHeight,
        int maxRecordCount)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(capabilities);

        int srid = layers.Count > 0 ? layers[0].Srid : 4326;
        Envelope? extent = Union(layers);

        return new
        {
            currentVersion = CurrentVersion,
            serviceDescription = string.Empty,

            // <b>"Layers" is ArcGIS's own default map name</b> and several clients
            // print it in a table of contents. A server inventing something here puts
            // its own word in the client's user interface.
            mapName = "Layers",
            description = string.Empty,
            copyrightText = string.Empty,
            supportsDynamicLayers = false,

            layers = Entries(layers),
            tables = Array.Empty<object>(),

            spatialReference = Reference(srid),
            singleFusedMapCache = false,

            // <b>Initial and full are the same extent, deliberately.</b> ArcGIS lets
            // them differ so a service can open zoomed to somewhere interesting; this
            // server has no opinion about where that is, and inventing one would move
            // every client's first view for no reason it could see.
            initialExtent = Box(extent, srid),
            fullExtent = Box(extent, srid),

            minScale = 0,
            maxScale = 0,
            units = Units(srid),
            supportedImageFormatTypes = SupportedImageFormats,

            documentInfo = new
            {
                Title = string.Empty,
                Author = string.Empty,
                Comments = string.Empty,
                Subject = string.Empty,
                Category = string.Empty,
                Keywords = string.Empty,
            },

            capabilities,
            supportedQueryFormats = "JSON",
            exportTilesAllowed = false,
            supportsDatumTransformation = true,
            maxRecordCount,
            maxImageHeight = maximumHeight,
            maxImageWidth = maximumWidth,
            supportedExtensions = string.Empty,
        };
    }

    /// <summary>One layer's document at <c>/rest/services/{name}/MapServer/{id}</c>.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="fields">Its columns, as the FeatureServer face describes them.</param>
    /// <param name="drawingInfo">Its symbology in Esri form, or null for none.</param>
    /// <param name="displayField">The column a client shows as a feature's name.</param>
    /// <param name="maxRecordCount">What a query may return.</param>
    /// <param name="hasLabels">Whether its stored style labels features.</param>
    /// <param name="capabilities">What this layer offers, comma-separated.</param>
    /// <returns>The document.</returns>
    public static object Layer(
        FeatureServerMetadataWriter.ServiceLayer layer,
        IReadOnlyList<object> fields,
        object? drawingInfo,
        string? displayField,
        int maxRecordCount,
        bool hasLabels = false,
        string capabilities = "Map")
    {
        ArgumentNullException.ThrowIfNull(fields);

        return new
        {
            currentVersion = CurrentVersion,
            id = layer.Id,
            name = layer.Name,
            type = "Feature Layer",
            description = string.Empty,
            geometryType = GeometryName(layer.GeometryType),
            copyrightText = string.Empty,
            parentLayer = (object?)null,
            subLayers = Array.Empty<object>(),
            minScale = 0,
            maxScale = 0,
            drawingInfo,
            defaultVisibility = true,
            extent = Box(layer.Extent, layer.Srid),
            hasAttachments = false,
            htmlPopupType = "esriServerHTMLPopupTypeNone",
            displayField = displayField ?? string.Empty,
            typeIdField = (string?)null,
            fields,
            relationships = Array.Empty<object>(),
            canModifyLayer = false,
            canScaleSymbols = false,

            // <b>Reported, not assumed.</b> This was hardcoded false for every layer
            // until the correctness gate read it against a layer whose stored style
            // has a `symbol` layer and does draw labels.
            hasLabels,
            capabilities,
            maxRecordCount,
            supportsStatistics = true,
            supportsAdvancedQueries = true,
            supportedQueryFormats = "JSON",
            isDataVersioned = false,
        };
    }

    /// <summary>
    /// The <c>export</c> answer when a client asks for JSON rather than the image.
    /// </summary>
    /// <remarks>
    /// <b>A client asking <c>f=json</c> wants to know where the picture goes</b>, not
    /// to receive it — the JavaScript API places an image element from this and then
    /// fetches the href. So the href must be an address the same client can request,
    /// which means the export URL it just sent with <c>f=image</c>.
    /// </remarks>
    /// <param name="href">Where the image can be fetched.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="extent">What it covers.</param>
    /// <param name="srid">The CRS of that extent.</param>
    /// <param name="scale">The map scale denominator.</param>
    /// <returns>The document.</returns>
    public static object Export(
        string href, int width, int height, Envelope extent, int srid, double scale) => new
        {
            href,
            width,
            height,
            extent = Box(extent, srid),
            scale,
        };

    /// <summary>One layer's legend, for <c>/MapServer/legend</c>.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="entries">One per class, or one with an empty label.</param>
    /// <param name="width">A swatch's width.</param>
    /// <param name="height">A swatch's height.</param>
    /// <returns>The entry.</returns>
    public static object LegendLayer(
        FeatureServerMetadataWriter.ServiceLayer layer,
        IReadOnlyList<LegendEntry> entries,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return new
        {
            layerId = layer.Id,
            layerName = layer.Name,
            layerType = "Feature Layer",
            minScale = 0,
            maxScale = 0,

            // <b>One entry per class since 2026-08-25, which is
            // [Q-131](../../docs/open-questions.md)'s answer on this face.</b> The
            // label was empty here for as long as there was one swatch per layer,
            // because the swatch *was* the layer and a label would have repeated the
            // layer name beside it. A classified style has as many entries as classes
            // and each carries its own name -- which is the shape this response
            // already had and nothing was filling in.
            legend = entries.Select(entry => new
            {
                label = entry.Label,
                url = string.Empty,

                // <b>Inline, not a second request.</b> ArcGIS offers both; a client
                // that has to fetch a URL per entry makes one request per entry to
                // draw a table of contents, and the swatches are a few hundred bytes
                // each.
                imageData = Convert.ToBase64String(entry.Image),
                contentType = "image/png",
                height,
                width,
            }).ToArray(),
        };
    }

    /// <summary>One legend entry: a class's name and its picture.</summary>
    /// <param name="Label">What to call it, empty when the swatch is the whole layer.</param>
    /// <param name="Image">The PNG bytes.</param>
    public readonly record struct LegendEntry(string Label, byte[] Image);

    /// <summary>One <c>identify</c> hit.</summary>
    /// <param name="layerId">Which layer it came from.</param>
    /// <param name="layerName">That layer's name.</param>
    /// <param name="displayFieldName">The column a client shows as the name.</param>
    /// <param name="value">This feature's value in that column.</param>
    /// <param name="attributes">Its attributes.</param>
    /// <param name="geometryType">Its geometry's kind, or null when none was returned.</param>
    /// <param name="geometry">Its geometry, or null.</param>
    /// <returns>The result.</returns>
    public static object IdentifyResult(
        int layerId,
        string layerName,
        string displayFieldName,
        string value,
        IReadOnlyDictionary<string, object?> attributes,
        string? geometryType,
        object? geometry) => new
        {
            layerId,
            layerName,
            displayFieldName,
            value,
            attributes,
            geometryType,
            geometry,
        };

    private static object[] Entries(
        IReadOnlyList<FeatureServerMetadataWriter.ServiceLayer> layers)
    {
        object[] entries = new object[layers.Count];

        for (int i = 0; i < layers.Count; i++)
        {
            entries[i] = new
            {
                id = layers[i].Id,
                name = layers[i].Name,
                parentLayerId = layers[i].ParentId ?? -1,
                defaultVisibility = true,
                subLayerIds = (object?)null,
                minScale = 0,
                maxScale = 0,
                type = "Feature Layer",
                geometryType = GeometryName(layers[i].GeometryType),
            };
        }

        return entries;
    }

    private static Envelope? Union(IReadOnlyList<FeatureServerMetadataWriter.ServiceLayer> layers)
    {
        Envelope whole = Envelope.Empty;

        foreach (FeatureServerMetadataWriter.ServiceLayer layer in layers)
        {
            if (layer.Extent is { IsEmpty: false } box)
            {
                whole = whole.IsEmpty ? box : whole.Union(box);
            }
        }

        return whole.IsEmpty ? null : whole;
    }

    private static object Reference(int srid) => new { wkid = srid, latestWkid = srid };

    private static object? Box(Envelope? extent, int srid) => extent is { } box
        ? new
        {
            xmin = box.MinX,
            ymin = box.MinY,
            xmax = box.MaxX,
            ymax = box.MaxY,
            spatialReference = Reference(srid),
        }
        : null;

    private static string Units(int srid) =>
        AxisOrder.IsGeographic(srid) ? "esriDecimalDegrees" : "esriMeters";

    /// <summary>An Esri geometry type name.</summary>
    /// <param name="kind">The geometry.</param>
    /// <returns>The name.</returns>
    public static string GeometryName(GeometryKind kind) => kind switch
    {
        GeometryKind.Point => "esriGeometryPoint",
        GeometryKind.MultiPoint => "esriGeometryMultipoint",
        GeometryKind.LineString or GeometryKind.MultiLineString => "esriGeometryPolyline",
        GeometryKind.Polygon or GeometryKind.MultiPolygon => "esriGeometryPolygon",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Every geometry kind needs a name here. A kind without one would be written as null, "
            + "and a client reads a null geometryType as a table rather than as a map layer."),
    };

    /// <summary>
    /// The scale denominator of an exported image.
    /// </summary>
    /// <remarks>
    /// <b>The same convention WMS publishes</b>, so the two faces of one renderer do
    /// not disagree about what 1:50,000 means: 0.28 mm per pixel, and degrees
    /// converted at 111,319.49 metres. A client comparing the scale in an export
    /// response with the scale in a WMS capabilities document is comparing the same
    /// number.
    /// </remarks>
    /// <param name="extent">What the image covers.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="srid">The CRS of the extent.</param>
    /// <returns>The denominator.</returns>
    public static double Scale(Envelope extent, int width, int srid) =>
        width <= 0
            ? 0
            : Graticula.Cartography.MapScale.Denominator(
                extent.Width / width, AxisOrder.IsGeographic(srid));

    /// <summary>A number as ArcGIS writes one in a URL.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    public static string Number(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);
}
