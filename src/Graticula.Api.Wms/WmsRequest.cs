using System;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Cartography;
using Graticula.Geometries;

namespace Graticula.Api.Wms;

/// <summary>What a request is allowed to ask for.</summary>
/// <remarks>
/// <b>Bounds, passed in rather than compiled in.</b> A WMS image size is a memory
/// allocation a stranger chooses: 20,000 × 20,000 pixels is four gigabytes of
/// surface, and the request costs nothing to send. The capabilities document
/// publishes these as <c>MaxWidth</c> and <c>MaxHeight</c>, so a client learns the
/// limit rather than discovering it.
/// </remarks>
/// <param name="MaximumWidth">Widest image, in pixels.</param>
/// <param name="MaximumHeight">Tallest image, in pixels.</param>
/// <param name="MaximumLayers">How many layers one map may compose.</param>
/// <param name="MaximumFeatureCount">How many features <c>GetFeatureInfo</c> may return.</param>
public readonly record struct WmsLimits(
    int MaximumWidth, int MaximumHeight, int MaximumLayers, int MaximumFeatureCount)
{
    /// <summary>Bounds that suit a single-machine deployment.</summary>
    public static WmsLimits Default => new(4096, 4096, 32, 100);
}

/// <summary>Who to ask about this server, if anybody has said.</summary>
/// <remarks>
/// <para>
/// <b>Configuration rather than content, because it is a deployment's fact and not this
/// product's.</b> WMS 1.3.0 recommends <c>ContactInformation</c> in the capabilities
/// document, and the CITE suite's <c>service-contact-info</c> assertion checks for it.
/// **The obvious way to pass that assertion is to write something plausible, and that
/// would be worse than failing it** — a client that reads a contact address and finds
/// nobody there has been actively misled, whereas a client that finds no address knows
/// to look elsewhere.
/// </para>
/// <para>
/// <b>So it is empty unless an operator fills it in</b>, and every part is optional:
/// a deployment that wants to publish only an address publishes only an address. Set
/// <c>Graticula:WmsContactPerson</c>, <c>…Organization</c>, <c>…Position</c>,
/// <c>…Email</c> and <c>…Phone</c>.
/// </para>
/// </remarks>
/// <param name="Person">A person's name, or null.</param>
/// <param name="Organization">The organisation running this server, or null.</param>
/// <param name="Position">That person's role, or null.</param>
/// <param name="Email">An address to write to, or null.</param>
/// <param name="Phone">A number to ring, or null.</param>
public readonly record struct WmsContact(
    string? Person, string? Organization, string? Position, string? Email, string? Phone)
{
    /// <summary>Nobody has said, which is the honest default.</summary>
    public static WmsContact Unstated => default;

    /// <summary>Whether there is anything to write.</summary>
    public bool IsStated =>
        !string.IsNullOrWhiteSpace(Person)
        || !string.IsNullOrWhiteSpace(Organization)
        || !string.IsNullOrWhiteSpace(Position)
        || !string.IsNullOrWhiteSpace(Email)
        || !string.IsNullOrWhiteSpace(Phone);
}

/// <summary>
/// One WMS request, parsed out of its key-value pairs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsed once, into one shape, for both versions.</b> The versions differ in
/// parameter names (<c>SRS</c>/<c>CRS</c>, <c>X,Y</c>/<c>I,J</c>) and in axis order;
/// they do not differ in what a map is. Two parsers would be two places for the axis
/// rule to be applied differently, which is the defect that puts a map in the sea.
/// </para>
/// <para>
/// <b>Parameter names are case-insensitive and values are not</b>, which is the
/// specification's rule (OGC 06-042 §6.8.1) and is why the whole surface reads
/// through one accessor rather than a dictionary the caller built.
/// </para>
/// <para>
/// <b>Every refusal names a parameter.</b> A WMS client showing *the server said no*
/// with no locator sends its user to a log file they cannot read.
/// </para>
/// </remarks>
public sealed class WmsRequest
{
    private WmsRequest(WmsVersion version, WmsOperation operation)
    {
        Version = version;
        Operation = operation;
    }

    /// <summary>Which version the response must be written in.</summary>
    public WmsVersion Version { get; }

    /// <summary>What was asked for.</summary>
    public WmsOperation Operation { get; }

    /// <summary>The layers to draw, outermost last.</summary>
    public IReadOnlyList<string> Layers { get; private init; } = [];

    /// <summary>The styles named, one per layer, each empty for the layer's own.</summary>
    public IReadOnlyList<string> Styles { get; private init; } = [];

    /// <summary>The EPSG code of the requested CRS.</summary>
    public int Srid { get; private init; }

    /// <summary>The extent, already in longitude/latitude order whatever was sent.</summary>
    public Envelope Extent { get; private init; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; private init; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; private init; }

    /// <summary>The image format.</summary>
    public MapImageFormat Format { get; private init; } = MapImageFormat.Png;

    /// <summary>Whether the background is transparent.</summary>
    public bool Transparent { get; private init; }

    /// <summary>The background colour, used when not transparent.</summary>
    public Rgba Background { get; private init; } = Rgba.White;

    /// <summary>The time value, or null when none was asked for.</summary>
    public TimeWindow? Time { get; private init; }

    /// <summary>The layers <c>GetFeatureInfo</c> asks about.</summary>
    public IReadOnlyList<string> QueryLayers { get; private init; } = [];

    /// <summary>The pixel column asked about, from <c>I</c> or <c>X</c>.</summary>
    public int PixelX { get; private init; }

    /// <summary>The pixel row asked about, from <c>J</c> or <c>Y</c>.</summary>
    public int PixelY { get; private init; }

    /// <summary>How many features <c>GetFeatureInfo</c> may return.</summary>
    public int FeatureCount { get; private init; } = 1;

    /// <summary>The <c>INFO_FORMAT</c> asked for.</summary>
    public string InfoFormat { get; private init; } = "text/plain";

    /// <summary>
    /// Reads a request.
    /// </summary>
    /// <param name="parameter">Reads one parameter by name, case-insensitively.</param>
    /// <param name="limits">What the request is allowed to ask for.</param>
    /// <param name="request">The request.</param>
    /// <param name="fault">Why not, when it did not parse.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        Func<string, string?> parameter,
        WmsLimits limits,
        out WmsRequest? request,
        out WmsFault? fault)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        request = null;
        fault = null;

        string? service = parameter("SERVICE");
        string? operation = parameter("REQUEST");

        if (string.IsNullOrWhiteSpace(operation))
        {
            fault = WmsFault.Missing("REQUEST");
            return false;
        }

        WmsOperation? asked = OperationOf(operation);

        if (asked is null)
        {
            fault = new WmsFault(
                WmsFault.OperationNotSupported,
                $"This server implements GetCapabilities, GetMap, GetFeatureInfo and "
                + $"GetLegendGraphic; it was asked for `{operation}`.",
                "REQUEST");

            return false;
        }

        // <b>SERVICE is required everywhere except GetMap, and that is the
        // specification's own inconsistency.</b> 1.3.0 §6.9 requires it on
        // GetCapabilities; older clients omit it on GetMap because 1.1.1's examples
        // did. Demanding it on GetMap refuses requests that every other WMS answers.
        if (asked == WmsOperation.GetCapabilities
            && !string.IsNullOrWhiteSpace(service)
            && !string.Equals(service, WmsNames.Service, StringComparison.OrdinalIgnoreCase))
        {
            fault = WmsFault.Invalid(
                "SERVICE", $"This is a WMS; it was asked for `{service}`.");

            return false;
        }

        if (!TryVersion(parameter, asked.Value, out WmsVersion version, out fault))
        {
            return false;
        }

        return asked.Value switch
        {
            WmsOperation.GetCapabilities =>
                Yield(new WmsRequest(version, WmsOperation.GetCapabilities), out request),

            WmsOperation.GetLegendGraphic =>
                TryLegend(parameter, version, out request, out fault),

            _ => TryMap(parameter, limits, version, asked.Value, out request, out fault),
        };
    }

    private static bool Yield(WmsRequest built, out WmsRequest? request)
    {
        request = built;
        return true;
    }

    private static WmsOperation? OperationOf(string text) => text.Trim().ToUpperInvariant() switch
    {
        "GETCAPABILITIES" or "CAPABILITIES" => WmsOperation.GetCapabilities,
        "GETMAP" or "MAP" => WmsOperation.GetMap,
        "GETFEATUREINFO" or "FEATURE_INFO" => WmsOperation.GetFeatureInfo,
        "GETLEGENDGRAPHIC" => WmsOperation.GetLegendGraphic,
        _ => null,
    };

    /// <summary>
    /// Negotiates the version.
    /// </summary>
    /// <remarks>
    /// <b>GetCapabilities negotiates and everything else does not.</b> A client
    /// asking what this server speaks must be answered even when it guessed the
    /// version wrong — that is the question it is asking — so an unknown version
    /// there falls back to the highest. On GetMap the version decides the axis order,
    /// so an unknown one is refused: drawing 1.3.0 axes for a 1.1.1 request produces
    /// a map of somewhere else with no error anywhere.
    /// </remarks>
    private static bool TryVersion(
        Func<string, string?> parameter,
        WmsOperation operation,
        out WmsVersion version,
        out WmsFault? fault)
    {
        fault = null;
        version = WmsVersion.V130;

        // 1.1.1 clients send WMTVER; 1.3.0 clients send VERSION. Both appear in the
        // wild and a server reading only one refuses the other for no reason.
        string? asked = parameter("VERSION") ?? parameter("WMTVER");

        if (string.IsNullOrWhiteSpace(asked))
        {
            if (operation == WmsOperation.GetCapabilities)
            {
                return true;
            }

            fault = WmsFault.Missing("VERSION");
            return false;
        }

        switch (asked.Trim())
        {
            case "1.3.0":
                version = WmsVersion.V130;
                return true;

            case "1.1.1":
            case "1.1.0":
            case "1.0.0":
                // 1.1.0 and 1.0.0 clients are answered in 1.1.1, which is what the
                // specification's own negotiation says: the highest version the
                // server supports that is not higher than the one asked for.
                version = WmsVersion.V111;
                return true;

            default:
                if (operation == WmsOperation.GetCapabilities)
                {
                    version = WmsVersion.V130;
                    return true;
                }

                fault = new WmsFault(
                    WmsFault.VersionNegotiationFailed,
                    $"This server speaks WMS 1.3.0 and 1.1.1; it was asked for `{asked}`. It is "
                    + "refused rather than answered approximately, because the two versions "
                    + "disagree about the axis order of EPSG:4326 and a client cannot tell a "
                    + "transposed map from a wrong one.",
                    "VERSION");

                return false;
        }
    }

    private static bool TryLegend(
        Func<string, string?> parameter,
        WmsVersion version,
        out WmsRequest? request,
        out WmsFault? fault)
    {
        request = null;
        fault = null;

        string? layer = parameter("LAYER");

        if (string.IsNullOrWhiteSpace(layer))
        {
            fault = WmsFault.Missing("LAYER");
            return false;
        }

        MapImageFormat format = MapImageFormat.Png;

        if (parameter("FORMAT") is { Length: > 0 } wanted)
        {
            if (WmsNames.FormatOf(wanted) is not { } parsed)
            {
                fault = new WmsFault(
                    WmsFault.InvalidFormat,
                    $"This server writes image/png and image/jpeg; it was asked for `{wanted}`.",
                    "FORMAT");

                return false;
            }

            format = parsed;
        }

        // <b>The same refusal `GetMap` makes, and it was missing here.</b> ADR-041
        // §5.2 says a style named anything but the default is refused rather than
        // approximated, and `TryStyles` enforces that for `GetMap` — but `TryLegend`
        // never called it, so `GetLegendGraphic` answered any name with the default
        // swatch. **It is the operation the capabilities document points every
        // legend-drawing client at**, under a remark asserting the opposite. Found by
        // contradiction sweep 3.
        string? style = parameter("STYLE")?.Trim();

        if (style is { Length: > 0 }
            && !string.Equals(style, "default", StringComparison.OrdinalIgnoreCase))
        {
            fault = new WmsFault(
                WmsFault.StyleNotDefined,
                $"`{style}` is not a style this server defines. A layer here has one "
                + "symbology, its own; ask for it with `STYLE=` or `STYLE=default`.",
                "STYLE");

            return false;
        }

        request = new WmsRequest(version, WmsOperation.GetLegendGraphic)
        {
            Layers = [layer.Trim()],
            Styles = [style ?? string.Empty],
            Format = format,
            Width = Size(parameter("WIDTH"), 20, 1024),
            Height = Size(parameter("HEIGHT"), 20, 1024),
            Transparent = Flag(parameter("TRANSPARENT")),
        };

        return true;
    }

    private static bool TryMap(
        Func<string, string?> parameter,
        WmsLimits limits,
        WmsVersion version,
        WmsOperation operation,
        out WmsRequest? request,
        out WmsFault? fault)
    {
        request = null;
        fault = null;

        if (!TrySplit(parameter("LAYERS"), "LAYERS", limits.MaximumLayers, out List<string> layers, out fault))
        {
            return false;
        }

        string crsParameter = WmsNames.CrsParameter(version);
        string? crs = parameter(crsParameter);

        if (string.IsNullOrWhiteSpace(crs))
        {
            fault = WmsFault.Missing(crsParameter);
            return false;
        }

        if (!TrySrid(crs, out int srid))
        {
            fault = new WmsFault(
                version == WmsVersion.V130 ? WmsFault.InvalidCrs : WmsFault.InvalidSrs,
                $"`{crs}` is not a CRS this server understands. Ask for an `EPSG:code`, such as "
                + "EPSG:4326 or EPSG:3857.",
                crsParameter);

            return false;
        }

        if (!TryExtent(parameter("BBOX"), version, crs, srid, out Envelope extent, out fault))
        {
            return false;
        }

        if (!TryDimension(parameter("WIDTH"), "WIDTH", limits.MaximumWidth, out int width, out fault)
            || !TryDimension(parameter("HEIGHT"), "HEIGHT", limits.MaximumHeight, out int height, out fault))
        {
            return false;
        }

        string? formatText = parameter("FORMAT");

        if (string.IsNullOrWhiteSpace(formatText) && operation == WmsOperation.GetMap)
        {
            fault = WmsFault.Missing("FORMAT");
            return false;
        }

        MapImageFormat format = MapImageFormat.Png;

        if (operation == WmsOperation.GetMap)
        {
            if (WmsNames.FormatOf(formatText) is not { } parsed)
            {
                fault = new WmsFault(
                    WmsFault.InvalidFormat,
                    $"This server writes image/png and image/jpeg; it was asked for "
                    + $"`{formatText}`. It is refused rather than answered in another format, "
                    + "because a client that receives a format it did not ask for cannot tell "
                    + "that from a server that ignored the parameter.",
                    "FORMAT");

                return false;
            }

            format = parsed;
        }

        if (!TryStyles(parameter("STYLES"), layers.Count, out List<string> styles, out fault))
        {
            return false;
        }

        bool transparent = Flag(parameter("TRANSPARENT"));

        if (!TryBackground(parameter("BGCOLOR"), out Rgba background, out fault))
        {
            return false;
        }

        if (!TryTime(parameter("TIME"), out TimeWindow? time, out fault))
        {
            return false;
        }

        WmsRequest built = new(version, operation)
        {
            Layers = layers,
            Styles = styles,
            Srid = srid,
            Extent = extent,
            Width = width,
            Height = height,
            Format = format,
            Transparent = transparent,
            Background = background,
            Time = time,
        };

        if (operation == WmsOperation.GetMap)
        {
            request = built;
            return true;
        }

        return TryInfo(parameter, limits, built, layers, out request, out fault);
    }

    private static bool TryInfo(
        Func<string, string?> parameter,
        WmsLimits limits,
        WmsRequest map,
        List<string> layers,
        out WmsRequest? request,
        out WmsFault? fault)
    {
        request = null;

        if (!TrySplit(
            parameter("QUERY_LAYERS"), "QUERY_LAYERS", limits.MaximumLayers,
            out List<string> queried, out fault))
        {
            return false;
        }

        foreach (string layer in queried)
        {
            // A layer queried but not drawn is a request about a map that was never
            // made. The specification requires QUERY_LAYERS to be a subset of LAYERS
            // and clients do send stale ones after a layer is switched off.
            // Ordinal, which is what List<string>.Contains uses and what a layer
            // name is compared with everywhere else on this server.
            if (!layers.Contains(layer))
            {
                fault = new WmsFault(
                    WmsFault.LayerNotDefined,
                    $"`{layer}` is in QUERY_LAYERS but not in LAYERS. GetFeatureInfo asks what is "
                    + "at a pixel of a map, so it can only ask about layers that map has.",
                    "QUERY_LAYERS");

                return false;
            }
        }

        // 1.3.0 renamed X,Y to I,J. Both are read for both versions because clients
        // in the field send the pair they have always sent.
        string? column = parameter("I") ?? parameter("X");
        string? row = parameter("J") ?? parameter("Y");

        if (!TryPixel(column, map.Version == WmsVersion.V130 ? "I" : "X", map.Width, out int x, out fault)
            || !TryPixel(row, map.Version == WmsVersion.V130 ? "J" : "Y", map.Height, out int y, out fault))
        {
            return false;
        }

        int count = 1;

        if (parameter("FEATURE_COUNT") is { Length: > 0 } text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int asked))
        {
            count = Math.Clamp(asked, 1, limits.MaximumFeatureCount);
        }

        request = new WmsRequest(map.Version, WmsOperation.GetFeatureInfo)
        {
            Layers = map.Layers,
            Styles = map.Styles,
            Srid = map.Srid,
            Extent = map.Extent,
            Width = map.Width,
            Height = map.Height,
            Format = map.Format,
            Transparent = map.Transparent,
            Background = map.Background,
            Time = map.Time,
            QueryLayers = queried,
            PixelX = x,
            PixelY = y,
            FeatureCount = count,
            InfoFormat = parameter("INFO_FORMAT")?.Trim() ?? "text/plain",
        };

        return true;
    }

    private static bool TrySplit(
        string? value, string name, int maximum, out List<string> items, out WmsFault? fault)
    {
        items = [];
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            fault = WmsFault.Missing(name);
            return false;
        }

        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0)
            {
                items.Add(part);
            }
        }

        if (items.Count == 0)
        {
            fault = WmsFault.Missing(name);
            return false;
        }

        if (items.Count > maximum)
        {
            fault = WmsFault.Invalid(
                name,
                $"A map may compose at most {maximum} layers; {items.Count} were asked for.");

            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads <c>STYLES</c>, which is required and is usually empty.
    /// </summary>
    /// <remarks>
    /// <b><c>STYLES=</c> with nothing after it is the correct, required way to ask
    /// for every layer's default</b>, and it is the thing a naive parser reads as
    /// missing. Refusing it would refuse the request every WMS client sends.
    /// </remarks>
    private static bool TryStyles(
        string? value, int layers, out List<string> styles, out WmsFault? fault)
    {
        fault = null;
        styles = [];

        if (value is null)
        {
            fault = WmsFault.Missing("STYLES");
            return false;
        }

        if (value.Length == 0)
        {
            for (int i = 0; i < layers; i++)
            {
                styles.Add(string.Empty);
            }

            return true;
        }

        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            styles.Add(part);
        }

        if (styles.Count != layers)
        {
            fault = WmsFault.Invalid(
                "STYLES",
                $"{layers} layers were asked for and {styles.Count} styles were named. Send one "
                + "style per layer, or `STYLES=` for every layer's own.");

            return false;
        }

        foreach (string style in styles)
        {
            // ADR-041 §5.2. This server has one style per layer, the one stored for
            // it, and no way to serve a second by name. Answering a named style with
            // the default is indistinguishable from ignoring the parameter.
            if (style.Length > 0 && !string.Equals(style, "default", StringComparison.OrdinalIgnoreCase))
            {
                fault = new WmsFault(
                    WmsFault.StyleNotDefined,
                    $"`{style}` is not a style this server defines. A layer here has one "
                    + "symbology, its own; ask for it with `STYLES=` or `STYLES=default`.",
                    "STYLES");

                return false;
            }
        }

        return true;
    }

    private static bool TrySrid(string crs, out int srid)
    {
        srid = 0;

        string value = crs.Trim();

        // CRS:84 is WMS's own name for WGS 84 in longitude/latitude order, and it
        // exists precisely because 1.3.0 made EPSG:4326 latitude first. Clients use
        // it to sidestep the axis question, so answering it is answering the thing
        // they are trying to avoid asking.
        if (string.Equals(value, "CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            srid = AxisOrder.Wgs84;
            return true;
        }

        int colon = value.LastIndexOf(':');

        if (colon < 0
            || !value.StartsWith("EPSG", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(
            value[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out srid)
            && srid > 0;
    }

    /// <summary>
    /// Reads <c>BBOX</c>, transposing it when the version and CRS say to.
    /// </summary>
    /// <remarks>
    /// <b>The single most expensive line in this file.</b> WMS 1.3.0 with a
    /// geographic CRS sends minimum latitude first; 1.1.1 never does. Read the wrong
    /// way round, a request for Turkey draws the Indian Ocean and every layer comes
    /// back empty — which looks exactly like a data problem and is diagnosed as one.
    /// </remarks>
    private static bool TryExtent(
        string? value, WmsVersion version, string? crs, int srid,
        out Envelope extent, out WmsFault? fault)
    {
        extent = Envelope.Empty;
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            fault = WmsFault.Missing("BBOX");
            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 4)
        {
            fault = WmsFault.Invalid(
                "BBOX", "A bounding box is four comma-separated numbers.");

            return false;
        }

        double[] numbers = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                fault = WmsFault.Invalid("BBOX", $"`{parts[i]}` is not a number.");
                return false;
            }
        }

        (double minX, double minY, double maxX, double maxY) =
            WmsNames.IsLatitudeFirst(version, crs, srid)
                ? (numbers[1], numbers[0], numbers[3], numbers[2])
                : (numbers[0], numbers[1], numbers[2], numbers[3]);

        if (maxX <= minX || maxY <= minY)
        {
            fault = WmsFault.Invalid(
                "BBOX",
                "A bounding box needs a positive width and height. In WMS "
                + $"{WmsNames.Text(version)} with {(version == WmsVersion.V130 ? "CRS" : "SRS")}="
                + $"EPSG:{srid} the order is "
                + (WmsNames.IsLatitudeFirst(version, crs, srid)
                    ? "miny,minx,maxy,maxx — latitude first."
                    : "minx,miny,maxx,maxy — longitude first."));

            return false;
        }

        extent = new Envelope(minX, minY, maxX, maxY);
        return true;
    }

    private static bool TryDimension(
        string? value, string name, int maximum, out int size, out WmsFault? fault)
    {
        size = 0;
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            fault = WmsFault.Missing(name);
            return false;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
            || size <= 0)
        {
            fault = WmsFault.Invalid(name, $"`{value}` is not a positive number of pixels.");
            return false;
        }

        if (size > maximum)
        {
            // Named rather than clamped: an image silently smaller than the one asked
            // for is georeferenced wrongly by every client that composites it.
            fault = WmsFault.Invalid(
                name,
                $"{size} pixels is beyond this server's limit of {maximum}. It is refused rather "
                + "than reduced, because an image of a size the client did not ask for is placed "
                + "in the wrong place on their map.");

            return false;
        }

        return true;
    }

    private static bool TryPixel(
        string? value, string name, int extent, out int pixel, out WmsFault? fault)
    {
        pixel = 0;
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            fault = WmsFault.Missing(name);
            return false;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pixel))
        {
            fault = WmsFault.Invalid(name, $"`{value}` is not a pixel coordinate.");
            return false;
        }

        if (pixel < 0 || pixel >= extent)
        {
            fault = new WmsFault(
                WmsFault.InvalidPoint,
                $"`{name}={pixel}` is outside a map {extent} pixels across.",
                name);

            return false;
        }

        return true;
    }

    private static bool TryBackground(string? value, out Rgba colour, out WmsFault? fault)
    {
        colour = Rgba.White;
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // WMS writes it as 0xRRGGBB, which is not a spelling Rgba.TryParse knows
        // and is the only place in this server that uses it.
        string text = value.Trim();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = "#" + text[2..];
        }

        if (!Rgba.TryParse(text, out colour))
        {
            fault = WmsFault.Invalid(
                "BGCOLOR", $"`{value}` is not a colour. WMS writes it as 0xRRGGBB.");

            return false;
        }

        return true;
    }

    private static bool TryTime(string? value, out TimeWindow? window, out WmsFault? fault)
    {
        window = null;
        fault = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!TimeWindow.TryParse(value, out TimeWindow parsed, out string? why))
        {
            fault = new WmsFault(WmsFault.InvalidDimensionValue, why!, "TIME");
            return false;
        }

        window = parsed;
        return true;
    }

    private static bool Flag(string? value) =>
        string.Equals(value?.Trim(), "TRUE", StringComparison.OrdinalIgnoreCase);

    private static int Size(string? value, int fallback, int maximum) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
        && size > 0
            ? Math.Min(size, maximum)
            : fallback;
}
