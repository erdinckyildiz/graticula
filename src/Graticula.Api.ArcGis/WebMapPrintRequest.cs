using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Cartography.Printing;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis;

/// <summary>One layer a print was asked to draw, as <c>Web_Map_as_JSON</c> describes it.</summary>
/// <param name="Id">The layer's id in the web map, which <c>legendOptions</c> refers to.</param>
/// <param name="Title">Its title, for the legend.</param>
/// <param name="Url">Its service or layer address; null for a layer that names none (a feature collection, a graphics layer).</param>
/// <param name="Kind">What it is, when the client said: <c>layerType</c> or <c>type</c>.</param>
/// <param name="Visible">False when the client sent <c>visibility: false</c>.</param>
/// <param name="VisibleLayers">A map service's <c>visibleLayers</c>, or null for its default.</param>
/// <param name="Definitions">Each map-service sublayer's <c>layerDefinition.definitionExpression</c>, by sublayer id.</param>
/// <param name="DefinitionExpression">A feature layer's own <c>layerDefinition.definitionExpression</c>.</param>
/// <param name="DrawingInfo">A feature layer's <c>layerDefinition.drawingInfo</c>, when the client sent its renderer.</param>
/// <param name="IsBasemap">Whether it came from <c>baseMap.baseMapLayers</c>.</param>
public sealed record WebMapPrintLayer(
    string? Id,
    string? Title,
    string? Url,
    string? Kind,
    bool Visible,
    IReadOnlyList<int>? VisibleLayers,
    IReadOnlyDictionary<int, string> Definitions,
    string? DefinitionExpression,
    JsonObject? DrawingInfo,
    bool IsBasemap);

/// <summary>
/// A print as ArcGIS's <c>Export Web Map Task</c> takes it — ADR-265 §5.2: <c>Web_Map_as_JSON</c> read into what this
/// server draws.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the public specification</b> ("ExportWebMap specification", ArcGIS REST API reference): the
/// <c>mapOptions</c>, <c>operationalLayers</c>, <c>baseMap</c>, <c>exportOptions</c> and <c>layoutOptions</c> objects,
/// with the members this server acts on. A member it does not act on is ignored, as the specification's own clients
/// send many that a given server has no use for; the members it reads and refuses are said in the refusal.
/// </para>
/// <para>
/// <b>Pure.</b> It reads; it resolves no address and fetches nothing. Whether a layer's address is this server's own is
/// the endpoint's question (ADR-265 §5.3), asked of the URL this keeps exactly as it was sent.
/// </para>
/// </remarks>
public sealed record WebMapPrintRequest
{
    /// <summary>The most layers one print draws, basemap and operational together.</summary>
    public const int MaximumLayers = 64;

    /// <summary>The dpi a print is drawn at when the client says none, which is ArcGIS's default.</summary>
    public const double DefaultDpi = 96;

    /// <summary>The highest dpi this server draws a print at.</summary>
    public const double MaximumDpi = 300;

    /// <summary>The map's extent, in <see cref="ExtentSrid"/>.</summary>
    public required Envelope Extent { get; init; }

    /// <summary>The reference the extent was written in.</summary>
    public required int ExtentSrid { get; init; }

    /// <summary>The reference the map is drawn in: <c>mapOptions.spatialReference</c>, else the extent's.</summary>
    public required int Srid { get; init; }

    /// <summary><c>mapOptions.scale</c>, which centres the map on the extent at that scale; null for none.</summary>
    public double? Scale { get; init; }

    /// <summary><c>mapOptions.rotation</c> in degrees, which this server does not apply (ADR-265 §6).</summary>
    public double Rotation { get; init; }

    /// <summary>The layers to draw, bottom first: the basemap's, then the operational ones.</summary>
    public required IReadOnlyList<WebMapPrintLayer> Layers { get; init; }

    /// <summary><c>exportOptions.outputSize</c>'s width: the picture's for <c>MAP_ONLY</c>.</summary>
    public int OutputWidth { get; init; } = 800;

    /// <summary><c>exportOptions.outputSize</c>'s height.</summary>
    public int OutputHeight { get; init; } = 1100;

    /// <summary><c>exportOptions.dpi</c>.</summary>
    public double Dpi { get; init; } = DefaultDpi;

    /// <summary><c>layoutOptions.titleText</c>.</summary>
    public string? Title { get; init; }

    /// <summary><c>layoutOptions.authorText</c>.</summary>
    public string? Author { get; init; }

    /// <summary><c>layoutOptions.copyrightText</c>.</summary>
    public string? Copyright { get; init; }

    /// <summary><c>layoutOptions.scaleBarOptions</c>.</summary>
    public ScaleBarUnits Units { get; init; } = ScaleBarUnits.Default;

    /// <summary>
    /// <c>layoutOptions.legendOptions.operationalLayers</c>: the ids of the layers the legend shows, each with the
    /// sublayers it names or null for all of them; null when the client sent no legend options, which means every layer.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>?>? LegendLayers { get; init; }

    /// <summary>Reads <c>Web_Map_as_JSON</c>.</summary>
    /// <param name="json">The parameter's text.</param>
    /// <param name="request">The print, when it could be read.</param>
    /// <param name="error">Why it could not, in a sentence for the client.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryParse(string? json, out WebMapPrintRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Web_Map_as_JSON is required: the map to print, as the ExportWebMap specification describes it.";
            return false;
        }

        JsonObject? root;

        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject;
        }
        catch (JsonException e)
        {
            error = $"Web_Map_as_JSON is not JSON: {e.Message}";
            return false;
        }

        if (root is null)
        {
            error = "Web_Map_as_JSON must be a JSON object.";
            return false;
        }

        if (root["mapOptions"] is not JsonObject mapOptions || mapOptions["extent"] is not JsonObject extent)
        {
            error = "Web_Map_as_JSON needs mapOptions.extent: the area to print.";
            return false;
        }

        if (!TryNumber(extent["xmin"], out double xmin) || !TryNumber(extent["ymin"], out double ymin)
            || !TryNumber(extent["xmax"], out double xmax) || !TryNumber(extent["ymax"], out double ymax)
            || !(xmax > xmin) || !(ymax > ymin))
        {
            error = "mapOptions.extent needs numeric xmin, ymin, xmax and ymax, with each max above its min.";
            return false;
        }

        int? extentSrid = SridOf(extent["spatialReference"]);
        int? mapSrid = SridOf(mapOptions["spatialReference"]);

        if ((extentSrid ?? mapSrid) is not { } fromSrid)
        {
            error = "mapOptions.extent needs a spatialReference with a wkid. A spatial reference given only as wkt is "
                + "not one this print service reads.";
            return false;
        }

        double? scale = TryNumber(mapOptions["scale"], out double s) && s > 0 ? s : null;
        double rotation = TryNumber(mapOptions["rotation"], out double r) ? r : 0;

        List<WebMapPrintLayer> layers = [];

        if (root["baseMap"] is JsonObject baseMap && baseMap["baseMapLayers"] is JsonArray baseLayers)
        {
            foreach (JsonNode? one in baseLayers)
            {
                if (one is JsonObject layer)
                {
                    layers.Add(Layer(layer, isBasemap: true));
                }
            }
        }

        if (root["operationalLayers"] is JsonArray operational)
        {
            foreach (JsonNode? one in operational)
            {
                if (one is JsonObject layer)
                {
                    layers.Add(Layer(layer, isBasemap: false));
                }
            }
        }

        if (layers.Count > MaximumLayers)
        {
            error = $"This print names {layers.Count} layers; this server draws at most {MaximumLayers} in one print.";
            return false;
        }

        int width = 800, height = 1100;
        double dpi = DefaultDpi;

        if (root["exportOptions"] is JsonObject export)
        {
            if (export["outputSize"] is JsonArray size)
            {
                if (size.Count != 2 || !TryNumber(size[0], out double w) || !TryNumber(size[1], out double h)
                    || w < 1 || h < 1 || w > 100_000 || h > 100_000)
                {
                    error = "exportOptions.outputSize must be [width, height] in whole pixels.";
                    return false;
                }

                width = (int)Math.Round(w);
                height = (int)Math.Round(h);
            }

            if (export["dpi"] is { } dpiNode)
            {
                if (!TryNumber(dpiNode, out double d) || d < 1)
                {
                    error = "exportOptions.dpi must be a positive number.";
                    return false;
                }

                if (d > MaximumDpi)
                {
                    error = $"exportOptions.dpi is {d.ToString(CultureInfo.InvariantCulture)}; this server prints at "
                        + $"most {MaximumDpi.ToString(CultureInfo.InvariantCulture)} dpi.";
                    return false;
                }

                dpi = d;
            }
        }

        string? title = null, author = null, copyright = null;
        ScaleBarUnits units = ScaleBarUnits.Default;
        Dictionary<string, IReadOnlyList<int>?>? legend = null;

        if (root["layoutOptions"] is JsonObject layout)
        {
            title = Text(layout["titleText"]);
            author = Text(layout["authorText"]);
            copyright = Text(layout["copyrightText"]);

            if (layout["scaleBarOptions"] is JsonObject bar)
            {
                units = new ScaleBarUnits(
                    Text(bar["metricUnit"]) ?? units.MetricUnit,
                    Text(bar["metricLabel"]) ?? (Text(bar["metricUnit"]) is null ? units.MetricLabel : string.Empty),
                    Text(bar["nonMetricUnit"]) ?? units.NonMetricUnit,
                    Text(bar["nonMetricLabel"]) ?? (Text(bar["nonMetricUnit"]) is null ? units.NonMetricLabel : string.Empty));
            }

            if (layout["legendOptions"] is JsonObject legendOptions && legendOptions["operationalLayers"] is JsonArray shown)
            {
                legend = new(StringComparer.Ordinal);

                foreach (JsonNode? one in shown)
                {
                    if (one is JsonObject entry && Text(entry["id"]) is { } id)
                    {
                        legend[id] = entry["subLayerIds"] is JsonArray sub ? Integers(sub) : null;
                    }
                }
            }
        }

        request = new WebMapPrintRequest
        {
            Extent = new Envelope(xmin, ymin, xmax, ymax),
            ExtentSrid = fromSrid,
            Srid = mapSrid ?? fromSrid,
            Scale = scale,
            Rotation = rotation,
            Layers = layers,
            OutputWidth = width,
            OutputHeight = height,
            Dpi = dpi,
            Title = title,
            Author = author,
            Copyright = copyright,
            Units = units,
            LegendLayers = legend,
        };

        return true;
    }

    private static WebMapPrintLayer Layer(JsonObject layer, bool isBasemap)
    {
        string? url = Text(layer["url"]);

        // A vector tile layer may name only its style, under the service it belongs to.
        if (url is null && Text(layer["styleUrl"]) is { } style)
        {
            url = style;
        }

        Dictionary<int, string> definitions = [];

        if (layer["layers"] is JsonArray sublayers)
        {
            foreach (JsonNode? one in sublayers)
            {
                if (one is JsonObject sub && TryNumber(sub["id"], out double id) && id >= 0 && id == Math.Floor(id)
                    && sub["layerDefinition"] is JsonObject subDefinition
                    && Text(subDefinition["definitionExpression"]) is { } where)
                {
                    definitions[(int)id] = where;
                }
            }
        }

        JsonObject? definition = layer["layerDefinition"] as JsonObject;

        return new WebMapPrintLayer(
            Text(layer["id"]),
            Text(layer["title"]),
            url,
            Text(layer["layerType"]) ?? Text(layer["type"]),
            layer["visibility"] is not JsonValue visibility
                || visibility.GetValueKind() != JsonValueKind.False,
            layer["visibleLayers"] is JsonArray visible ? Integers(visible) : null,
            definitions,
            definition is null ? null : Text(definition["definitionExpression"]),
            definition?["drawingInfo"] is JsonObject drawing ? (JsonObject)drawing.DeepClone() : null,
            isBasemap);
    }

    /// <summary>A spatial reference's code: <c>latestWkid</c> where it is given, else <c>wkid</c>, 102100 read as 3857.</summary>
    private static int? SridOf(JsonNode? node)
    {
        if (node is not JsonObject sr)
        {
            return null;
        }

        if (TryNumber(sr["latestWkid"], out double latest) && latest > 0)
        {
            return ArcGisGeometryReader.Canonical((int)latest);
        }

        return TryNumber(sr["wkid"], out double wkid) && wkid > 0 ? ArcGisGeometryReader.Canonical((int)wkid) : null;
    }

    private static List<int> Integers(JsonArray array)
    {
        List<int> read = [];

        foreach (JsonNode? one in array)
        {
            if (TryNumber(one, out double value) && value >= 0 && value == Math.Floor(value) && value <= int.MaxValue)
            {
                read.Add((int)value);
            }
        }

        return read;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>() is { Length: > 0 } text ? text : null,
                JsonValueKind.Number => value.ToJsonString(),
                _ => null,
            }
            : null;

    private static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;

        if (node is not JsonValue value)
        {
            return false;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                number = value.GetValue<double>();
                return double.IsFinite(number);
            case JsonValueKind.String:
                return double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && double.IsFinite(number);
            default:
                return false;
        }
    }
}
