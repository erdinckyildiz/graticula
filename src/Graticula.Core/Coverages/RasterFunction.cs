using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Cartography;

namespace Graticula.Coverages;

/// <summary>What a raster function makes of an elevation model — ADR-136.</summary>
public enum RasterFunctionKind
{
    /// <summary>The values as they are.</summary>
    None = 0,

    /// <summary>Shaded relief: how lit each cell is by a sun at an azimuth and an altitude, 0 to 255.</summary>
    Hillshade = 1,

    /// <summary>Steepness in degrees, 0 to 90.</summary>
    Slope = 2,

    /// <summary>The compass direction a slope faces, 0 to 360 clockwise from north; a flat cell has none.</summary>
    Aspect = 3,
}

/// <summary>
/// A raster function applied to a coverage's first band before it is drawn, identified or exported — ADR-136, the
/// functions ArcGIS clients ask an image service for by <c>renderingRule</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 1, and written here</b>: surface derivatives are cartographic logic, and the arithmetic is Horn's
/// third-order finite difference over a cell's eight neighbours — the method Esri documents for its Slope, Aspect and
/// Hillshade tools, so the numbers a client gets here are the numbers it would get there.
/// </para>
/// <para>
/// <b>The cell size is in metres</b>: a geographic coverage's degrees are converted at the window's latitude, so a
/// slope in degrees means degrees whichever reference the model is stored in.
/// </para>
/// </remarks>
/// <param name="Kind">Which function.</param>
/// <param name="Azimuth">The sun's compass direction for a hillshade, degrees.</param>
/// <param name="Altitude">The sun's height above the horizon for a hillshade, degrees.</param>
/// <param name="ZFactor">What multiplies the values to put them in the cell size's units.</param>
public sealed record RasterFunction(RasterFunctionKind Kind, double Azimuth = 315, double Altitude = 45, double ZFactor = 1)
{
    /// <summary>The values as they are.</summary>
    public static RasterFunction None { get; } = new(RasterFunctionKind.None);

    /// <summary>The functions served, by the names ArcGIS gives them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["Hillshade", "Slope", "Aspect"];

    /// <summary>The one band a function's result is: 32-bit values, NaN where nothing could be worked out.</summary>
    public static IReadOnlyList<BandInfo> ResultBands { get; } = [new BandInfo(0, SampleKind.Real32, double.NaN, null, null)];

    /// <summary>
    /// Reads an ArcGIS <c>renderingRule</c>: <c>{"rasterFunction":"Hillshade","rasterFunctionArguments":{…}}</c>.
    /// Empty and <c>{}</c> ask for nothing — the service's own drawing, its default function if it has one — and the
    /// function is null; <c>None</c> asks for the values themselves.
    /// </summary>
    /// <param name="json">The parameter's text.</param>
    /// <param name="function">The function, when it parsed.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it is one this server applies.</returns>
    public static bool TryParseRule(string? json, out RasterFunction? function, out string? error)
    {
        function = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json) || json.Replace(" ", string.Empty, StringComparison.Ordinal) == "{}")
        {
            return true;
        }

        function = None;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "`renderingRule` is a JSON object naming a `rasterFunction`.";
                return false;
            }

            string name = root.TryGetProperty("rasterFunction", out JsonElement named) && named.ValueKind == JsonValueKind.String
                ? named.GetString() ?? string.Empty
                : string.Empty;

            if (name.Length == 0 || name.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!Enum.TryParse(name, ignoreCase: true, out RasterFunctionKind kind) || kind == RasterFunctionKind.None)
            {
                error = $"`renderingRule` asks for the raster function '{name}', and this image service applies "
                    + $"{string.Join(", ", Names)} and None. Refused rather than drawn as if it had been applied.";
                return false;
            }

            JsonElement arguments = root.TryGetProperty("rasterFunctionArguments", out JsonElement given)
                && given.ValueKind == JsonValueKind.Object ? given : default;

            function = new RasterFunction(
                kind,
                Argument(arguments, "Azimuth", 315),
                Argument(arguments, "Altitude", 45),
                Argument(arguments, "ZFactor", 1));

            if (function.ZFactor <= 0 || function.Altitude is < 0 or > 90)
            {
                error = "A raster function's `ZFactor` is above zero and its `Altitude` between 0 and 90 degrees.";
                function = None;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "`renderingRule` is not JSON.";
            return false;
        }
    }

    private static double Argument(JsonElement arguments, string name, double fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.TryGetDouble(out double value))
            {
                return value;
            }
        }

        return fallback;
    }

    /// <summary>The function a stored style names — <c>;function:hillshade</c> — or none.</summary>
    /// <param name="styleText">The stored style.</param>
    /// <returns>The function.</returns>
    public static RasterFunction FromStyleText(string? styleText)
    {
        if (string.IsNullOrWhiteSpace(styleText))
        {
            return None;
        }

        foreach (string part in styleText.Split(';'))
        {
            string piece = part.Trim();

            if (piece.StartsWith("function:", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(piece["function:".Length..].Trim(), ignoreCase: true, out RasterFunctionKind kind))
            {
                return new RasterFunction(kind);
            }
        }

        return None;
    }

    /// <summary>The style segment that names this function, or null for none.</summary>
    /// <returns>The text.</returns>
    public string? ToStyleText() => Kind == RasterFunctionKind.None
        ? null
        : "function:" + Kind.ToString().ToLowerInvariant();

    /// <summary>How a function's result is drawn: grey for relief, a ramp for slope and for aspect.</summary>
    public CoverageStyle Style => Kind switch
    {
        RasterFunctionKind.Hillshade => new CoverageStyle(StretchKind.Fixed, 0, 255),
        // Over 0–45°: ordinary terrain is under 15°, and 0–90° drew it all one green (design review 2026-10-02); steeper
        // than 45° is the ramp's red.
        RasterFunctionKind.Slope => new CoverageStyle(StretchKind.Fixed, 0, 45).WithRamp("slope"),
        RasterFunctionKind.Aspect => new CoverageStyle(StretchKind.Fixed, 0, 360).WithRamp("aspect"),
        _ => CoverageStyle.Default,
    };

    /// <summary>
    /// Works the function out over a window's first band. Each cell takes its eight neighbours; a neighbour outside the
    /// window repeats the edge, and a cell with an absent neighbour has no answer.
    /// </summary>
    /// <param name="window">The values, as read.</param>
    /// <param name="cellWidth">A cell's width on the ground, metres.</param>
    /// <param name="cellHeight">A cell's height on the ground, metres.</param>
    /// <param name="noData">The band's no-data value, or null.</param>
    /// <returns>One band of the function's values, NaN where there is none.</returns>
    public CoverageWindow Apply(CoverageWindow window, double cellWidth, double cellHeight, double? noData)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (Kind == RasterFunctionKind.None)
        {
            return window;
        }

        int w = window.Width;
        int h = window.Height;
        int bands = window.Bands;
        double[] result = new double[w * h];

        double Z(int x, int y)
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
            double v = window.Samples[(((y * w) + x) * bands)];
            return double.IsNaN(v) || (noData is { } absent && v == absent) ? double.NaN : v * ZFactor;
        }

        double zenith = (90 - Altitude) * Math.PI / 180;
        double azimuth = ((360 - Azimuth + 90) % 360) * Math.PI / 180;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double a = Z(x - 1, y - 1), b = Z(x, y - 1), c = Z(x + 1, y - 1);
                double d = Z(x - 1, y), e = Z(x, y), f = Z(x + 1, y);
                double g = Z(x - 1, y + 1), hh = Z(x, y + 1), i = Z(x + 1, y + 1);

                if (double.IsNaN(a + b + c + d + e + f + g + hh + i))
                {
                    result[(y * w) + x] = double.NaN;
                    continue;
                }

                double dzdx = ((c + (2 * f) + i) - (a + (2 * d) + g)) / (8 * cellWidth);
                double dzdy = ((g + (2 * hh) + i) - (a + (2 * b) + c)) / (8 * cellHeight);
                double slope = Math.Atan(Math.Sqrt((dzdx * dzdx) + (dzdy * dzdy)));

                result[(y * w) + x] = Kind switch
                {
                    RasterFunctionKind.Slope => slope * 180 / Math.PI,
                    RasterFunctionKind.Aspect => CompassAspect(dzdx, dzdy),
                    _ => Shade(slope, MathAspect(dzdx, dzdy), zenith, azimuth),
                };
            }
        }

        return new CoverageWindow(w, h, 1, result);
    }

    /// <summary>The direction a slope faces in radians, counter-clockwise from east, as the hillshade formula takes it.</summary>
    private static double MathAspect(double dzdx, double dzdy)
    {
        if (dzdx != 0)
        {
            double aspect = Math.Atan2(dzdy, -dzdx);
            return aspect < 0 ? aspect + (2 * Math.PI) : aspect;
        }

        return dzdy > 0 ? Math.PI / 2 : dzdy < 0 ? 3 * Math.PI / 2 : 0;
    }

    /// <summary>The direction a slope faces in compass degrees, NaN for a flat cell, which faces nowhere.</summary>
    private static double CompassAspect(double dzdx, double dzdy)
    {
        if (dzdx == 0 && dzdy == 0)
        {
            return double.NaN;
        }

        double aspect = Math.Atan2(dzdy, -dzdx) * 180 / Math.PI;
        return aspect < 0 ? 90 - aspect : aspect > 90 ? 360 - aspect + 90 : 90 - aspect;
    }

    private static double Shade(double slope, double aspect, double zenith, double azimuth) =>
        Math.Max(0, 255 * ((Math.Cos(zenith) * Math.Cos(slope)) + (Math.Sin(zenith) * Math.Sin(slope) * Math.Cos(azimuth - aspect))));

    /// <summary>A cell's ground size in metres, from its size in the coverage's units at a latitude.</summary>
    /// <param name="width">The cell's width in the coverage's units.</param>
    /// <param name="height">The cell's height in the coverage's units.</param>
    /// <param name="geographic">Whether those units are degrees.</param>
    /// <param name="latitude">The latitude the cells are at, degrees, when geographic.</param>
    /// <returns>Width and height in metres.</returns>
    public static (double Width, double Height) Metres(double width, double height, bool geographic, double latitude) =>
        geographic
            ? (width * 111_320 * Math.Cos(latitude * Math.PI / 180), height * 110_574)
            : (width, height);

    /// <summary>The value at a function's result with its unit, for a pixel's answer.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string Say(double value) => double.IsNaN(value)
        ? "NoData"
        : value.ToString("0.###", CultureInfo.InvariantCulture);
}
