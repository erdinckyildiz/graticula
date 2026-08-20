using System;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Cartography;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// The parameters <c>MapServer/export</c> takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same map as a WMS <c>GetMap</c>, in a different vocabulary.</b>
/// <c>bbox</c> for <c>BBOX</c>, <c>size</c> for <c>WIDTH</c> and <c>HEIGHT</c>,
/// <c>layers=show:0,1</c> for <c>LAYERS</c>. It is worth having two parsers and one
/// renderer rather than the reverse.
/// </para>
/// <para>
/// <b>ArcGIS bounding boxes are always minx,miny,maxx,maxy.</b> There is no axis
/// question on this face — Esri's API never adopted the 1.3.0 convention — which is
/// one fewer thing to get wrong here and the reason WMS carries the rule instead.
/// </para>
/// </remarks>
internal sealed class MapServerExportParameters
{
    private MapServerExportParameters()
    {
    }

    /// <summary>What the image covers, in <see cref="ImageSrid"/>.</summary>
    public Envelope Extent { get; private init; }

    /// <summary>The CRS the image is drawn in.</summary>
    public int ImageSrid { get; private init; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; private init; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; private init; }

    /// <summary>The format to encode.</summary>
    public MapImageFormat Format { get; private init; } = MapImageFormat.Png;

    /// <summary>Whether the background is transparent.</summary>
    public bool Transparent { get; private init; }

    /// <summary>The layers to draw, in drawing order.</summary>
    public IReadOnlyList<PublishedLayer> Layers { get; private init; } = [];

    /// <summary>Reads the parameters.</summary>
    /// <param name="parameter">Reads one parameter by name, case-insensitively.</param>
    /// <param name="available">The service's layers.</param>
    /// <param name="bounds">The largest image this server will draw.</param>
    /// <param name="parsed">The parameters.</param>
    /// <param name="error">Why not, when they did not parse.</param>
    /// <returns>Whether they parsed.</returns>
    public static bool TryParse(
        Func<string, string?> parameter,
        IReadOnlyList<PublishedLayer> available,
        WidthHeight bounds,
        out MapServerExportParameters? parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(available);

        parsed = null;
        error = null;

        if (!TryExtent(parameter("bbox"), out Envelope extent, out error))
        {
            return false;
        }

        // <b>Three references and they mean three things.</b> `bboxSR` is what the
        // bbox is written in, `imageSR` is what to draw in, and a request that gives
        // only `bboxSR` means draw in that one. Treating them as one parameter
        // silently reprojects the extent to itself.
        int bboxSrid = Srid(parameter("bboxSR")) ?? 4326;
        int imageSrid = Srid(parameter("imageSR")) ?? bboxSrid;

        if (bboxSrid != imageSrid)
        {
            error = $"bboxSR is EPSG:{bboxSrid.ToString(CultureInfo.InvariantCulture)} and imageSR "
                + $"is EPSG:{imageSrid.ToString(CultureInfo.InvariantCulture)}. This server draws "
                + "in the reference the extent is given in; ask with both the same, or give only "
                + "one. It is refused rather than reprojected because an extent transformed "
                + "without saying so produces an image of somewhere near where it was asked for.";

            return false;
        }

        if (!TrySize(parameter("size"), bounds, out int width, out int height, out error))
        {
            return false;
        }

        if (!TryFormat(parameter("format"), out MapImageFormat format, out error))
        {
            return false;
        }

        if (!TryLayers(parameter("layers"), available, out List<PublishedLayer> layers, out error))
        {
            return false;
        }

        parsed = new MapServerExportParameters
        {
            Extent = extent,
            ImageSrid = imageSrid,
            Width = width,
            Height = height,
            Format = format,
            Transparent = Flag(parameter("transparent")),
            Layers = layers,
        };

        return true;
    }

    private static bool TryExtent(string? value, out Envelope extent, out string? error)
    {
        extent = Envelope.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "`bbox` is required: minx,miny,maxx,maxy.";
            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 4)
        {
            error = "`bbox` is four comma-separated numbers: minx,miny,maxx,maxy.";
            return false;
        }

        double[] numbers = new double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                error = $"`{parts[i]}` in `bbox` is not a number.";
                return false;
            }
        }

        if (numbers[2] <= numbers[0] || numbers[3] <= numbers[1])
        {
            error = "`bbox` needs a positive width and height.";
            return false;
        }

        extent = new Envelope(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>
    /// Reads a spatial reference, in either shape a client sends.
    /// </summary>
    /// <remarks>
    /// <b>A bare code or a JSON object.</b> ArcGIS Pro sends
    /// <c>{"wkid":4326,"latestWkid":4326}</c> where a browser sends <c>4326</c>, and
    /// a parser that only takes the number refuses the desktop client — which is a
    /// lesson this repository already paid for once, on the FeatureServer face.
    /// </remarks>
    private static int? Srid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bare))
        {
            return bare;
        }

        try
        {
            System.Text.Json.JsonElement document =
                System.Text.Json.JsonDocument.Parse(text).RootElement;

            foreach (string name in (string[])["latestWkid", "wkid"])
            {
                if (document.TryGetProperty(name, out System.Text.Json.JsonElement code)
                    && code.TryGetInt32(out int wkid))
                {
                    return wkid;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool TrySize(
        string? value, WidthHeight bounds, out int width, out int height, out string? error)
    {
        // ArcGIS's default is 400x400 and clients rely on it.
        width = 400;
        height = 400;
        error = null;

        if (!string.IsNullOrWhiteSpace(value))
        {
            string[] parts = value.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            {
                error = "`size` is width,height in pixels.";
                return false;
            }
        }

        if (width <= 0 || height <= 0)
        {
            error = "`size` needs a positive width and height.";
            return false;
        }

        if (width > bounds.Width || height > bounds.Height)
        {
            error = $"{width}×{height} is beyond this server's limit of "
                + $"{bounds.Width}×{bounds.Height}. It is refused rather than reduced, because an "
                + "image of a size the client did not ask for is placed in the wrong place on "
                + "their map.";

            return false;
        }

        return true;
    }

    private static bool TryFormat(string? value, out MapImageFormat format, out string? error)
    {
        format = MapImageFormat.Png;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            // <b>The PNG depths are aliases here.</b> This server writes one PNG;
            // refusing PNG24 would refuse a client over a bit depth it cannot choose
            // differently. Answering a JPEG request with a PNG would be a different
            // thing entirely, and does not happen.
            case "PNG":
            case "PNG8":
            case "PNG24":
            case "PNG32":
                format = MapImageFormat.Png;
                return true;

            case "JPG":
            case "JPEG":
                format = MapImageFormat.Jpeg;
                return true;

            default:
                error = $"`{value}` is not a format this server writes. It offers "
                    + $"{Graticula.Api.ArcGis.MapServerMetadataWriter.SupportedImageFormats}.";

                return false;
        }
    }

    /// <summary>
    /// Reads <c>layers</c>, which ArcGIS writes as <c>show:0,2</c>.
    /// </summary>
    /// <remarks>
    /// <b>Only <c>show</c> is honoured, and the others are refused rather than
    /// ignored.</b> <c>hide:</c>, <c>include:</c> and <c>exclude:</c> each mean
    /// something different about which features are drawn, and a server that read the
    /// numbers and dropped the verb would draw the exact opposite of <c>hide</c>.
    /// </remarks>
    private static bool TryLayers(
        string? value,
        IReadOnlyList<PublishedLayer> available,
        out List<PublishedLayer> layers,
        out string? error)
    {
        layers = [];
        error = null;

        List<PublishedLayer> drawable = [];

        foreach (PublishedLayer layer in available)
        {
            if (layer.Definition.GeometryColumn is { Length: > 0 })
            {
                drawable.Add(layer);
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            layers = drawable;
            return true;
        }

        string text = value.Trim();
        int colon = text.IndexOf(':', StringComparison.Ordinal);

        if (colon < 0)
        {
            error = "`layers` is written as `show:0,1` — a verb, a colon, then layer ids.";
            return false;
        }

        string verb = text[..colon].Trim();

        if (!string.Equals(verb, "show", StringComparison.OrdinalIgnoreCase))
        {
            error = $"`layers={verb}:…` is not supported. This server draws the layers named by "
                + "`show:`; hide, include and exclude each mean something different about which "
                + "features are drawn, and reading the ids without the verb would draw the "
                + "opposite of what was asked.";

            return false;
        }

        foreach (string part in text[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                error = $"`{part}` in `layers` is not a layer id.";
                return false;
            }

            PublishedLayer? found = null;

            foreach (PublishedLayer layer in drawable)
            {
                if (layer.LayerIndex == id)
                {
                    found = layer;
                    break;
                }
            }

            if (found is null)
            {
                error = $"This service has no drawable layer {id.ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }

            layers.Add(found);
        }

        return true;
    }

    private static bool Flag(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
