using System;
using System.Globalization;
using System.Linq;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;

namespace Graticula.Host;

/// <summary>
/// What an <c>exportImage</c> request asked for, once it has been checked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own parser rather than MapServer's, because the two faces differ in what
/// they can refuse.</b> A MapServer draws whatever layers a request names and its
/// reference system is negotiable; an ImageServer draws one coverage that exists in
/// exactly one reference system, and in this cut it cannot warp
/// ([ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)). Sharing a
/// parser would mean one of them carrying a rule the other must ignore, which is how a
/// parser comes to accept a parameter it does not honour — the shape of D-125.
/// </para>
/// <para>
/// <b>The image ceiling is the server's, not this face's.</b> ADR-043 condition 3 asks
/// for the same bounds <c>GetMap</c> has, and the reason it is a condition rather than
/// an obvious step is that a raster request can be arbitrarily more expensive than a
/// vector one at identical pixel dimensions.
/// </para>
/// </remarks>
internal sealed class ImageServerExportParameters
{
    private ImageServerExportParameters(
        Envelope extent, int width, int height, MapImageFormat format)
    {
        Extent = extent;
        Width = width;
        Height = height;
        Format = format;
    }

    /// <summary>The ground to draw, in the coverage's own reference.</summary>
    public Envelope Extent { get; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; }

    /// <summary>What to encode as.</summary>
    public MapImageFormat Format { get; }

    /// <summary>Reads and checks an export request.</summary>
    /// <param name="parameter">Reads one query parameter, case-insensitively.</param>
    /// <param name="info">The coverage being drawn.</param>
    /// <param name="ceiling">The largest image this server will make.</param>
    /// <param name="asked">What was asked for.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        Func<string, string?> parameter,
        CoverageInfo info,
        WidthHeight ceiling,
        out ImageServerExportParameters? asked,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(info);

        asked = null;

        if (!TryReference(parameter("bboxSR"), info, out error)
            || !TryReference(parameter("imageSR"), info, out error))
        {
            return false;
        }

        if (!TryExtent(parameter("bbox"), info, out Envelope extent, out error))
        {
            return false;
        }

        if (!TrySize(parameter("size"), ceiling, out int width, out int height, out error))
        {
            return false;
        }

        if (!TryFormat(parameter("format"), out MapImageFormat format, out error))
        {
            return false;
        }

        asked = new ImageServerExportParameters(extent, width, height, format);
        return true;
    }

    /// <summary>Reads the point an <c>identify</c> asks about.</summary>
    /// <param name="parameter">Reads one query parameter.</param>
    /// <param name="info">The coverage.</param>
    /// <param name="x">Its easting or longitude.</param>
    /// <param name="y">Its northing or latitude.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it parsed.</returns>
    /// <remarks>
    /// <b>Esri's <c>geometry</c> parameter, in its comma form only.</b> The JSON form
    /// <c>{"x":1,"y":2}</c> is the other spelling and is not read here; a request using
    /// it is refused with a sentence naming the form that works, rather than parsed
    /// halfway and answered about the wrong pixel.
    /// </remarks>
    public static bool TryPoint(
        Func<string, string?> parameter,
        CoverageInfo info,
        out double x,
        out double y,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(info);

        x = 0;
        y = 0;

        if (!TryReference(parameter("sr"), info, out error))
        {
            return false;
        }

        string? geometry = parameter("geometry");

        if (string.IsNullOrWhiteSpace(geometry))
        {
            error = "`geometry` names the point to identify, written as `x,y` in this image "
                + "service's own reference system.";

            return false;
        }

        string[] parts = geometry.Split(',');

        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
        {
            error = $"`geometry={geometry}` is not a point. Write it as `x,y`. The JSON form "
                + "this server does not read, so that a request using it is refused rather than "
                + "answered about the wrong pixel.";

            return false;
        }

        return true;
    }

    /// <summary>
    /// Refuses a reference system this cut cannot warp into.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than ignored, which is the whole point.</b> Accepting
    /// <c>bboxSR=3857</c> and reading the numbers as degrees would draw a picture of
    /// somewhere else and return it with a 200 — the defect class correctness gate 2
    /// was built to find, five times in one day. The message names the system that
    /// works, and the service document advertises it, so a client that reads either
    /// never sends the wrong one.
    /// </remarks>
    private static bool TryReference(string? text, CoverageInfo info, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string value = text.Trim();

        // Esri sends either a bare code or a JSON object with a wkid in it.
        int at = value.IndexOf("wkid", StringComparison.OrdinalIgnoreCase);

        if (at >= 0)
        {
            value = new string([.. value[at..].Where(char.IsDigit)]);
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int srid))
        {
            error = $"'{text}' is not a coordinate reference this server recognises. Write an "
                + "EPSG code.";

            return false;
        }

        if (srid == info.Srid)
        {
            return true;
        }

        error = $"This image service is stored in EPSG:"
            + info.Srid.ToString(CultureInfo.InvariantCulture)
            + $" and the request asked for EPSG:{srid.ToString(CultureInfo.InvariantCulture)}. "
            + "Reprojecting imagery needs a per-pixel warp, which this server does not do yet "
            + "(ADR-043 condition 2), and answering in the wrong reference while claiming the "
            + "right one would be worse than refusing. Ask in the service's own reference — the "
            + "service document names it.";

        return false;
    }

    private static bool TryExtent(
        string? text, CoverageInfo info, out Envelope extent, out string? error)
    {
        error = null;

        // <b>The whole coverage when nothing is asked for.</b> Esri clients send a bbox
        // on every real request; the default exists so that a person pasting the URL
        // into a browser sees the image rather than a refusal.
        if (string.IsNullOrWhiteSpace(text))
        {
            extent = info.Extent;
            return true;
        }

        string[] parts = text.Split(',');
        double[] ordinates = new double[4];

        if (parts.Length != 4)
        {
            extent = info.Extent;
            error = $"`bbox` is four numbers — minx,miny,maxx,maxy — and '{text}' has "
                + parts.Length.ToString(CultureInfo.InvariantCulture) + ".";

            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                    parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out ordinates[i]))
            {
                extent = info.Extent;
                error = $"`bbox` holds four numbers and '{parts[i]}' is not one.";

                return false;
            }
        }

        if (ordinates[2] <= ordinates[0] || ordinates[3] <= ordinates[1])
        {
            extent = info.Extent;
            error = "`bbox` needs maxx greater than minx and maxy greater than miny. A box with "
                + "no area names no pixels.";

            return false;
        }

        extent = new Envelope(ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
        return true;
    }

    private static bool TrySize(
        string? text, WidthHeight ceiling, out int width, out int height, out string? error)
    {
        error = null;
        width = 400;
        height = 400;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string[] parts = text.Split(',');

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
        {
            error = $"`size` is two whole numbers — width,height — and '{text}' is not.";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "`size` needs a positive width and height.";
            return false;
        }

        // <b>Named rather than clamped.</b> A silently smaller image is a picture the
        // client will scale and misread; saying the limit lets them ask again.
        if (width > ceiling.Width || height > ceiling.Height)
        {
            error = $"This server draws at most "
                + ceiling.Width.ToString(CultureInfo.InvariantCulture) + " by "
                + ceiling.Height.ToString(CultureInfo.InvariantCulture)
                + " pixels and the request asked for "
                + width.ToString(CultureInfo.InvariantCulture) + " by "
                + height.ToString(CultureInfo.InvariantCulture) + ".";

            return false;
        }

        return true;
    }

    private static bool TryFormat(string? text, out MapImageFormat format, out string? error)
    {
        error = null;
        format = MapImageFormat.Png;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "png":
            case "png8":
            case "png24":
            case "png32":
                format = MapImageFormat.Png;
                return true;

            case "jpg":
            case "jpeg":
                format = MapImageFormat.Jpeg;
                return true;

            default:
                error = $"`format={text}` is not one this server writes. It writes png and jpg.";
                return false;
        }
    }
}
