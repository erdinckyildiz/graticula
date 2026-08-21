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
        Envelope extent, int width, int height, MapImageFormat format, int srid)
    {
        Extent = extent;
        Width = width;
        Height = height;
        Format = format;
        Srid = srid;
    }

    /// <summary>The ground to draw, in the coverage's own reference.</summary>
    public Envelope Extent { get; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; }

    /// <summary>What to encode as.</summary>
    public MapImageFormat Format { get; }

    /// <summary>
    /// The reference the extent is written in, and the image is drawn in.
    /// </summary>
    /// <remarks>
    /// <b>Read rather than refused, from 2026-08-21.</b> Until the warp existed this
    /// parser refused anything but the coverage's own system, because answering in the
    /// wrong reference while claiming the right one is worse than refusing. The warp
    /// exists now — <see cref="CoverageWarp"/>, with its error measured in
    /// `benchmarks/raster-warp` — so the honest answer changed.
    /// </remarks>
    public int Srid { get; }

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

        // <b>`bboxSR` says what the box is written in; `imageSR` says what to draw
        // in.</b> Esri allows them to differ and this server does not: a request that
        // gave two would have its extent read in one and its pixels laid out in the
        // other, which is a picture of the right place at the wrong shape. Both are
        // read, and disagreeing is refused rather than silently resolved.
        if (!TryReference(parameter("bboxSR"), info, out int boxSrid, out error)
            || !TryReference(parameter("imageSR"), info, out int imageSrid, out error))
        {
            return false;
        }

        if (parameter("bboxSR") is { Length: > 0 } && parameter("imageSR") is { Length: > 0 }
            && boxSrid != imageSrid)
        {
            error = $"`bboxSR` is EPSG:{boxSrid.ToString(CultureInfo.InvariantCulture)} and "
                + $"`imageSR` is EPSG:{imageSrid.ToString(CultureInfo.InvariantCulture)}. This "
                + "server draws in the reference the extent is written in; asking for two "
                + "would give a picture of the right ground at the wrong shape.";

            return false;
        }

        int srid = parameter("bboxSR") is { Length: > 0 } ? boxSrid : imageSrid;

        if (!TryExtent(parameter("bbox"), info, srid, out Envelope extent, out error))
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

        asked = new ImageServerExportParameters(extent, width, height, format, srid);
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

        // <b>`identify` still answers only in the coverage's own reference, and that is
        // a smaller limitation than it sounds.</b> Warping a whole image is a grid of
        // control points amortised over a million pixels; projecting one point is a
        // round trip for one answer, and the point of `identify` is that it is cheap.
        // The client already has the service document, which names the reference.
        if (!TryReference(parameter("sr"), info, out int srid, out error))
        {
            return false;
        }

        if (srid != info.Srid)
        {
            error = $"`identify` reads a point in this service's own reference, EPSG:"
                + info.Srid.ToString(CultureInfo.InvariantCulture)
                + $", and the request named EPSG:{srid.ToString(CultureInfo.InvariantCulture)}. "
                + "Exporting an image will reproject; asking about one pixel will not.";

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
    /// Reads a reference system, defaulting to the coverage's own.
    /// </summary>
    /// <remarks>
    /// <b>Anything the projection engine knows is accepted now.</b> This used to refuse
    /// every system but the coverage's, because until the warp existed the alternative
    /// was to read the numbers in the wrong units and draw somewhere else — a 200
    /// carrying a picture of the wrong place, which is correctness gate 2's whole
    /// subject. What the request cannot do is name a system the database has never
    /// heard of, and that is refused by the projection itself with the sentence
    /// `ErrorResponse` gives it.
    /// </remarks>
    private static bool TryReference(string? text, CoverageInfo info, out int srid, out string? error)
    {
        error = null;
        srid = info.Srid;

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

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid)
            || srid <= 0)
        {
            srid = info.Srid;

            error = $"'{text}' is not a coordinate reference this server recognises. Write an "
                + "EPSG code.";

            return false;
        }

        return true;
    }

    private static bool TryExtent(
        string? text, CoverageInfo info, int srid, out Envelope extent, out string? error)
    {
        error = null;

        // <b>The whole coverage when nothing is asked for.</b> Esri clients send a bbox
        // on every real request; the default exists so that a person pasting the URL
        // into a browser sees the image rather than a refusal.
        if (string.IsNullOrWhiteSpace(text))
        {
            extent = info.Extent;

            if (srid != info.Srid)
            {
                extent = info.Extent;

                error = "A request in a reference other than this service's own has to say "
                    + "which ground it wants: there is no default extent to give it, because "
                    + "the coverage's own extent is written in the coverage's own reference. "
                    + "Send a `bbox`.";

                return false;
            }

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

            // <b>`jpgpng` is what every ArcGIS client sends, and refusing it meant none of
            // them could draw an image service at all.</b> Esri's own name for *JPEG
            // where the picture is opaque, PNG where it is not* — a size optimisation
            // that leaves the choice to the server. Answering PNG satisfies it: the
            // format's whole point is that transparency survives, and a client that
            // asked for it has said it will take either.
            //
            // Found on 2026-08-21 by pointing the ArcGIS Maps SDK at this face for
            // ADR-043 condition 1. The SDK asked, the parser refused by name, the map
            // framed correctly on empty ground — a request refused with a reason,
            // rendered as a blank map, which is the failure mode a conformance suite of
            // our own tests could not have found.
            case "jpgpng":
            case "jpg":
            case "jpeg":
                format = text.Trim().Equals("jpgpng", StringComparison.OrdinalIgnoreCase)
                    ? MapImageFormat.Png
                    : MapImageFormat.Jpeg;

                return true;

            default:
                error = $"`format={text}` is not one this server writes. It writes png, jpg and "
                    + "jpgpng, which it answers as png.";

                return false;
        }
    }
}
