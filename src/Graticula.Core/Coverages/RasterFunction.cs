using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>
    /// The normalized difference vegetation index of a red and a near-infrared band — ADR-151: −1 to 1 when scientific,
    /// else ArcGIS's default 0 to 200, the index times 100 plus 100.
    /// </summary>
    Ndvi = 4,

    /// <summary>An expression over the bands — ADR-151, ArcGIS's <c>BandArithmetic</c>.</summary>
    BandArithmetic = 5,

    /// <summary>Some of the bands, in an order — ADR-151, ArcGIS's <c>ExtractBand</c>, and what <c>bandIds</c> asks.</summary>
    ExtractBand = 6,

    /// <summary>The image inside a polygon, or outside it — ADR-156, ArcGIS's <c>Clip</c>.</summary>
    Clip = 7,

    /// <summary>Ranges of values given new values — ADR-156, ArcGIS's <c>Remap</c>.</summary>
    Remap = 8,

    /// <summary>Only the pixels whose bands lie in ranges — ADR-156, ArcGIS's <c>Mask</c>.</summary>
    Mask = 9,

    /// <summary>A neighbourhood's minimum, maximum, mean, deviation, median, majority or minority — ADR-156.</summary>
    Statistics = 10,

    /// <summary>Each band and a number added, subtracted, multiplied or divided — ADR-156, ArcGIS's <c>Arithmetic</c>.</summary>
    Arithmetic = 11,
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
    public static IReadOnlyList<string> Names { get; } =
        ["Hillshade", "Slope", "Aspect", "NDVI", "BandArithmetic", "ExtractBand", "Clip", "Remap", "Mask", "Statistics", "Arithmetic"];

    /// <summary>The one band a function's result is: 32-bit values, NaN where nothing could be worked out.</summary>
    public static IReadOnlyList<BandInfo> ResultBands { get; } = [new BandInfo(0, SampleKind.Real32, double.NaN, null, null)];

    /// <summary>NDVI's red band, from zero — ArcGIS's <c>VisibleBandID</c>.</summary>
    public int RedBand { get; init; } = -1;

    /// <summary>NDVI's near-infrared band, from zero — ArcGIS's <c>InfraredBandID</c>.</summary>
    public int InfraredBand { get; init; } = -1;

    /// <summary>Whether NDVI answers −1 to 1 rather than ArcGIS's default 0 to 200.</summary>
    public bool Scientific { get; init; }

    /// <summary>BandArithmetic's expression.</summary>
    public BandExpression? Expression { get; init; }

    /// <summary>ExtractBand's bands, from zero, in the order asked.</summary>
    public IReadOnlyList<int>? BandIds { get; init; }

    /// <summary>Clip's polygon: its rings, each x, y, x, y… in the image's reference.</summary>
    public IReadOnlyList<double[]>? ClipRings { get; init; }

    /// <summary>Whether Clip keeps what is inside (ArcGIS's <c>ClipType</c> 1) rather than what is outside (2).</summary>
    public bool KeepInside { get; init; } = true;

    /// <summary>Remap's ranges, low inclusive, high exclusive, and the value each becomes.</summary>
    public IReadOnlyList<(double Low, double High, double Value)>? RemapRanges { get; init; }

    /// <summary>Remap's ranges that become no value.</summary>
    public IReadOnlyList<(double Low, double High)>? NoDataRanges { get; init; }

    /// <summary>Whether a value no range names keeps its value rather than becoming none.</summary>
    public bool AllowUnmatched { get; init; }

    /// <summary>Mask's range for each band, low and high, inclusive.</summary>
    public IReadOnlyList<(double Low, double High)>? IncludedRanges { get; init; }

    /// <summary>Statistics' kind: 1 minimum, 2 maximum, 3 mean, 4 standard deviation, 5 median, 6 majority, 7 minority.</summary>
    public int StatisticsType { get; init; } = 3;

    /// <summary>Statistics' neighbourhood, columns and rows, odd.</summary>
    public (int Columns, int Rows) Kernel { get; init; } = (3, 3);

    /// <summary>Arithmetic's operation: 1 plus, 2 minus, 3 times, 4 divided by.</summary>
    public int Operation { get; init; } = 1;

    /// <summary>Arithmetic's number.</summary>
    public double Constant { get; init; }

    /// <summary>How many cells beyond a window the function needs read: a surface's one, a neighbourhood's half.</summary>
    public int Margin => IsSurface ? 1 : Kind == RasterFunctionKind.Statistics ? Math.Max(Kernel.Columns, Kernel.Rows) / 2 : 0;

    /// <summary>Whether it is drawn as the image is — a clip or a mask leaves the values as they were.</summary>
    public bool KeepsImageStyle => Kind is RasterFunctionKind.Clip or RasterFunctionKind.Mask;

    /// <summary>The name ArcGIS gives the function.</summary>
    public string Name => Kind == RasterFunctionKind.Ndvi ? "NDVI" : Kind.ToString();

    /// <summary>Whether it works on a surface's slope — a cell and its eight neighbours — rather than pixel by pixel.</summary>
    public bool IsSurface => Kind is RasterFunctionKind.Hillshade or RasterFunctionKind.Slope or RasterFunctionKind.Aspect;

    /// <summary>
    /// Whether it makes new values — one 32-bit band, NaN where there are none — rather than the image's own: every
    /// function but <see cref="RasterFunctionKind.ExtractBand"/>, which only chooses among the bands.
    /// </summary>
    public bool Derives => Kind is not (RasterFunctionKind.None or RasterFunctionKind.ExtractBand or RasterFunctionKind.Clip or RasterFunctionKind.Mask);

    /// <summary>What the function's result is, band by band, for an image of these bands.</summary>
    /// <param name="input">The image's bands.</param>
    /// <returns>The result's bands.</returns>
    public IReadOnlyList<BandInfo> ResultBandsFor(IReadOnlyList<BandInfo> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return Kind switch
        {
            RasterFunctionKind.None => input,
            RasterFunctionKind.ExtractBand => [.. (BandIds ?? []).Select((b, i) => input[b] with { Index = i })],
            // ADR-156: a clip or a mask keeps the bands, with no value outside; arithmetic makes 32-bit values of each.
            RasterFunctionKind.Clip or RasterFunctionKind.Mask => [.. input.Select(b => b with { NoData = b.NoData ?? double.NaN })],
            RasterFunctionKind.Arithmetic => [.. input.Select(b => new BandInfo(b.Index, SampleKind.Real32, double.NaN, null, null))],
            _ => ResultBands,
        };
    }

    /// <summary>Whether the function can be applied to an image of this many bands, and why not.</summary>
    /// <param name="bands">The image's band count.</param>
    /// <param name="error">Why not.</param>
    /// <returns>Whether it can.</returns>
    public bool FitsBands(int bands, out string? error)
    {
        error = null;
        string count = bands.ToString(CultureInfo.InvariantCulture);

        switch (Kind)
        {
            case RasterFunctionKind.Hillshade or RasterFunctionKind.Slope or RasterFunctionKind.Aspect when bands >= 3:
                error = $"`renderingRule` asks for {Name}, which works on one band of measurements such as an elevation "
                    + "model; this image service is a colour image.";
                return false;

            case RasterFunctionKind.Ndvi when RedBand >= bands || InfraredBand >= bands:
                error = $"NDVI's VisibleBandID {RedBand} and InfraredBandID {InfraredBand} count from zero, and this image "
                    + $"has {count} band{(bands == 1 ? "" : "s")}, 0 to {bands - 1}.";
                return false;

            case RasterFunctionKind.BandArithmetic when Expression!.HighestBand > bands:
                error = $"The expression uses B{Expression.HighestBand}, but this image has {count} "
                    + $"band{(bands == 1 ? "" : "s")}, B1 to B{count}.";
                return false;

            case RasterFunctionKind.Remap or RasterFunctionKind.Statistics when bands >= 3:
                error = $"`renderingRule` asks for {Name}, which works on one band of values; this image service is a colour image.";
                return false;

            case RasterFunctionKind.Mask when IncludedRanges!.Count != bands:
                error = $"Mask's IncludedRanges are a low and a high for each band, and this image has {count} "
                    + $"band{(bands == 1 ? "" : "s")}: {(bands * 2).ToString(CultureInfo.InvariantCulture)} numbers.";
                return false;

            case RasterFunctionKind.ExtractBand when BandIds!.Any(b => b >= bands):
                error = $"ExtractBand's BandIDs [{string.Join(", ", BandIds!)}] count from zero, and this image has "
                    + $"{count} band{(bands == 1 ? "" : "s")}, 0 to {bands - 1}.";
                return false;

            default:
                return true;
        }
    }

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

            if (kind is RasterFunctionKind.Ndvi or RasterFunctionKind.BandArithmetic or RasterFunctionKind.ExtractBand)
            {
                return TryBandFunction(kind, arguments, out function, out error);
            }

            if (kind is RasterFunctionKind.Clip or RasterFunctionKind.Remap or RasterFunctionKind.Mask
                or RasterFunctionKind.Statistics or RasterFunctionKind.Arithmetic)
            {
                return TryValueFunction(kind, arguments, out function, out error);
            }

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

    /// <summary>
    /// Reads NDVI, BandArithmetic and ExtractBand's arguments — ADR-151, with ArcGIS's names and its numbering: band IDs
    /// from zero, an expression's B-numbers from one.
    /// </summary>
    private static bool TryBandFunction(
        RasterFunctionKind kind, JsonElement arguments, out RasterFunction? function, out string? error)
    {
        function = None;
        error = null;

        if (kind == RasterFunctionKind.Ndvi)
        {
            int red = (int)Argument(arguments, "VisibleBandID", -1);
            int infrared = (int)Argument(arguments, "InfraredBandID", -1);

            if (red < 0 || infrared < 0 || red == infrared)
            {
                error = "NDVI takes two different bands, from zero: `VisibleBandID`, the red, and `InfraredBandID`, the near "
                    + "infrared.";
                return false;
            }

            function = new RasterFunction(kind) { RedBand = red, InfraredBand = infrared, Scientific = Flag(arguments, "Scientific") };
            return true;
        }

        if (kind == RasterFunctionKind.ExtractBand)
        {
            List<int> ids = [];

            if (Find(arguments, "BandIDs") is { ValueKind: JsonValueKind.Array } list)
            {
                foreach (JsonElement id in list.EnumerateArray())
                {
                    if (!id.TryGetInt32(out int band) || band < 0)
                    {
                        error = "ExtractBand's `BandIDs` are band numbers from zero.";
                        return false;
                    }

                    ids.Add(band);
                }
            }

            if (ids.Count == 0)
            {
                error = Find(arguments, "BandNames") is { ValueKind: JsonValueKind.Array } names && names.GetArrayLength() > 0
                    ? "ExtractBand by `BandNames` is not read: this server's images carry no band names. Give `BandIDs`, from zero."
                    : "ExtractBand takes `BandIDs`, the bands to keep from zero, in the order to keep them.";
                return false;
            }

            function = new RasterFunction(kind) { BandIds = ids };
            return true;
        }

        // BandArithmetic: method 0 is an expression; 1 (NDVI) and 2 (SAVI) are bands named in ArcGIS's order.
        int method = (int)Argument(arguments, "Method", 0);
        string indexes = Find(arguments, "BandIndexes") is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? string.Empty : string.Empty;
        string? expression = method switch
        {
            0 => indexes,
            1 => Bands(indexes, 2) is { } b ? $"(B{b[0]} - B{b[1]}) / (B{b[0]} + B{b[1]})" : null,
            2 => Bands(indexes, 3) is { } s
                ? string.Create(CultureInfo.InvariantCulture, $"(1 + {s[2]}) * (B{s[0]} - B{s[1]}) / (B{s[0]} + B{s[1]} + {s[2]})")
                : null,
            _ => null,
        };

        if (method is not (0 or 1 or 2))
        {
            error = string.Create(CultureInfo.InvariantCulture,
                $"BandArithmetic's `Method` {method} is not one this server computes: it computes 0, an expression of your own, ")
                + "1, NDVI (\"NIR Red\") and 2, SAVI (\"NIR Red L\"). Any other index can be written as an expression with Method 0.";
            return false;
        }

        if (expression is null)
        {
            error = method == 1
                ? "BandArithmetic's NDVI takes `BandIndexes` \"NIR Red\": two band numbers from one."
                : "BandArithmetic's SAVI takes `BandIndexes` \"NIR Red L\": two band numbers from one and the soil factor.";
            return false;
        }

        if (!BandExpression.TryParse(expression, out BandExpression? parsed, out error))
        {
            return false;
        }

        function = new RasterFunction(kind) { Expression = parsed };
        return true;
    }

    /// <summary>ADR-156's functions' arguments, by ArcGIS's names.</summary>
    private static bool TryValueFunction(
        RasterFunctionKind kind, JsonElement arguments, out RasterFunction? function, out string? error)
    {
        function = None;
        error = null;

        double[]? Numbers(string name)
        {
            if (Find(arguments, name) is not { ValueKind: JsonValueKind.Array } list)
            {
                return null;
            }

            List<double> values = [];

            foreach (JsonElement item in list.EnumerateArray())
            {
                if (!item.TryGetDouble(out double v))
                {
                    return null;
                }

                values.Add(v);
            }

            return [.. values];
        }

        switch (kind)
        {
            case RasterFunctionKind.Clip:
            {
                if (Find(arguments, "ClippingRaster") is { ValueKind: JsonValueKind.Object or JsonValueKind.String })
                {
                    error = "Clip by `ClippingRaster` is not read: this image service clips by a `ClippingGeometry`.";
                    return false;
                }

                if (Find(arguments, "ClippingGeometry") is not { ValueKind: JsonValueKind.Object } geometry)
                {
                    error = "Clip takes a `ClippingGeometry`: a polygon or an envelope in the image's reference, as Esri JSON.";
                    return false;
                }

                List<double[]> rings = [];

                if (geometry.TryGetProperty("xmin", out JsonElement xmin))
                {
                    double x0 = xmin.GetDouble(), y0 = geometry.GetProperty("ymin").GetDouble();
                    double x1 = geometry.GetProperty("xmax").GetDouble(), y1 = geometry.GetProperty("ymax").GetDouble();
                    rings.Add([x0, y0, x0, y1, x1, y1, x1, y0, x0, y0]);
                }
                else if (geometry.TryGetProperty("rings", out JsonElement given) && given.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement ring in given.EnumerateArray())
                    {
                        rings.Add([.. ring.EnumerateArray().SelectMany(p => new[] { p[0].GetDouble(), p[1].GetDouble() })]);
                    }
                }

                if (rings.Count == 0 || rings.Any(r => r.Length < 8))
                {
                    error = "Clip's `ClippingGeometry` is a polygon of at least three corners, or an envelope.";
                    return false;
                }

                int type = (int)Argument(arguments, "ClipType", 1);

                if (type is not (1 or 2))
                {
                    error = "Clip's `ClipType` is 1, keeping what is inside the geometry, or 2, keeping what is outside.";
                    return false;
                }

                function = new RasterFunction(kind) { ClipRings = rings, KeepInside = type == 1 };
                return true;
            }

            case RasterFunctionKind.Remap:
            {
                double[] ranges = Numbers("InputRanges") ?? [];
                double[] outputs = Numbers("OutputValues") ?? [];

                if (ranges.Length == 0 || ranges.Length != outputs.Length * 2)
                {
                    error = "Remap's `InputRanges` are a low and a high for each of its `OutputValues`.";
                    return false;
                }

                double[] empty = Numbers("NoDataRanges") ?? [];
                function = new RasterFunction(kind)
                {
                    RemapRanges = [.. outputs.Select((v, i) => (ranges[2 * i], ranges[(2 * i) + 1], v))],
                    NoDataRanges = [.. Enumerable.Range(0, empty.Length / 2).Select(i => (empty[2 * i], empty[(2 * i) + 1]))],
                    AllowUnmatched = Flag(arguments, "AllowUnmatched"),
                };
                return true;
            }

            case RasterFunctionKind.Mask:
            {
                double[] ranges = Numbers("IncludedRanges") ?? [];

                if (ranges.Length == 0 || ranges.Length % 2 != 0)
                {
                    error = "Mask's `IncludedRanges` are a low and a high for each band.";
                    return false;
                }

                function = new RasterFunction(kind) { IncludedRanges = [.. Enumerable.Range(0, ranges.Length / 2).Select(i => (ranges[2 * i], ranges[(2 * i) + 1]))] };
                return true;
            }

            case RasterFunctionKind.Statistics:
            {
                int type = (int)Argument(arguments, "Type", 3);
                int columns = (int)Argument(arguments, "KernelColumns", 3), rows = (int)Argument(arguments, "KernelRows", 3);

                if (type is < 1 or > 7)
                {
                    error = "Statistics' `Type` is 1 minimum, 2 maximum, 3 mean, 4 standard deviation, 5 median, 6 majority or 7 minority.";
                    return false;
                }

                if (columns % 2 == 0 || rows % 2 == 0 || columns is < 1 or > 15 || rows is < 1 or > 15)
                {
                    error = "Statistics' `KernelColumns` and `KernelRows` are odd, from 1 to 15.";
                    return false;
                }

                function = new RasterFunction(kind) { StatisticsType = type, Kernel = (columns, rows) };
                return true;
            }

            default:
            {
                if (Find(arguments, "Raster2") is { } second && second.ValueKind != JsonValueKind.Number
                    && !(second.ValueKind == JsonValueKind.String && double.TryParse(second.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                {
                    error = "Arithmetic's `Raster2` is a number here: this image service is one image, and has no second raster to combine.";
                    return false;
                }

                int operation = (int)Argument(arguments, "Operation", 1);

                if (operation is < 1 or > 4)
                {
                    error = "Arithmetic's `Operation` is 1 plus, 2 minus, 3 times or 4 divided by.";
                    return false;
                }

                function = new RasterFunction(kind) { Operation = operation, Constant = Argument(arguments, "Raster2", 0) };
                return true;
            }
        }
    }

    /// <summary>"4 3" or "4 3 0.5": band numbers from one, then, for SAVI, a number.</summary>
    private static double[]? Bands(string text, int count)
    {
        string[] parts = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != count)
        {
            return null;
        }

        double[] values = new double[count];

        for (int i = 0; i < count; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || (i < 2 && (values[i] < 1 || values[i] != Math.Floor(values[i]))))
            {
                return null;
            }
        }

        return values;
    }

    private static JsonElement? Find(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static bool Flag(JsonElement arguments, string name) =>
        Find(arguments, name) is { } value
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool b) && b));

    private static double Argument(JsonElement arguments, string name, double fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double value))
                {
                    return value;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && double.TryParse(property.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double text))
                {
                    return text;
                }
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
                && FromStyleSegment(piece["function:".Length..].Trim()) is { } function)
            {
                return function;
            }
        }

        return None;
    }

    /// <summary>
    /// A stored function: <c>hillshade</c>, <c>ndvi:2:3</c> (scientific: <c>ndvi:2:3:s</c>), <c>extractband:3,2,1</c>,
    /// <c>bandarithmetic:(B4-B3)/(B4+B3)</c> — ADR-151. Null for anything it does not read.
    /// </summary>
    /// <param name="text">The segment after <c>function:</c>.</param>
    /// <returns>The function, or null.</returns>
    public static RasterFunction? FromStyleSegment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] parts = text.Split(':', 2);

        if (!Enum.TryParse(parts[0].Trim(), ignoreCase: true, out RasterFunctionKind kind) || kind == RasterFunctionKind.None)
        {
            return null;
        }

        string rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (kind)
        {
            case RasterFunctionKind.Ndvi:
                string[] bands = rest.Split(':');
                return bands.Length >= 2
                    && int.TryParse(bands[0], NumberStyles.None, CultureInfo.InvariantCulture, out int red)
                    && int.TryParse(bands[1], NumberStyles.None, CultureInfo.InvariantCulture, out int infrared) && red != infrared
                    ? new RasterFunction(kind) { RedBand = red, InfraredBand = infrared, Scientific = bands.Length > 2 && bands[2] == "s" }
                    : null;

            case RasterFunctionKind.ExtractBand:
                List<int> ids = [];

                foreach (string id in rest.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int band))
                    {
                        return null;
                    }

                    ids.Add(band);
                }

                return ids.Count == 0 ? null : new RasterFunction(kind) { BandIds = ids };

            case RasterFunctionKind.BandArithmetic:
                return BandExpression.TryParse(rest, out BandExpression? expression, out _)
                    ? new RasterFunction(kind) { Expression = expression }
                    : null;

            // ADR-156's functions carry geometry and tables a style does not store; they are asked for, not kept.
            case RasterFunctionKind.Clip or RasterFunctionKind.Remap or RasterFunctionKind.Mask
                or RasterFunctionKind.Statistics or RasterFunctionKind.Arithmetic:
                return null;

            default:
                return new RasterFunction(kind);
        }
    }

    /// <summary>The style segment that names this function, or null for none.</summary>
    /// <returns>The text.</returns>
    public string? ToStyleText() => Kind switch
    {
        RasterFunctionKind.None => null,
        RasterFunctionKind.Ndvi => string.Create(CultureInfo.InvariantCulture, $"function:ndvi:{RedBand}:{InfraredBand}{(Scientific ? ":s" : "")}"),
        RasterFunctionKind.ExtractBand => "function:extractband:" + string.Join(",", BandIds ?? []),
        RasterFunctionKind.BandArithmetic => "function:bandarithmetic:" + Expression!.Text.Replace(";", string.Empty, StringComparison.Ordinal),
        _ => "function:" + Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>How a function's result is drawn: grey for relief, a ramp for slope and for aspect.</summary>
    public CoverageStyle Style => Kind switch
    {
        RasterFunctionKind.Hillshade => new CoverageStyle(StretchKind.Fixed, 0, 255),
        // Over 0–45°: ordinary terrain is under 15°, and 0–90° drew it all one green (design review 2026-10-02); steeper
        // than 45° is the ramp's red.
        RasterFunctionKind.Slope => new CoverageStyle(StretchKind.Fixed, 0, 45).WithRamp("slope"),
        RasterFunctionKind.Aspect => new CoverageStyle(StretchKind.Fixed, 0, 360).WithRamp("aspect"),
        // ADR-151: bare ground and water brown, vegetation green, over the index's own range.
        RasterFunctionKind.Ndvi => (Scientific ? new CoverageStyle(StretchKind.Fixed, -1, 1) : new CoverageStyle(StretchKind.Fixed, 0, 200)).WithRamp("ndvi"),
        // An expression's range is its own and unknown, and the bands chosen keep their values: both are stretched over
        // what is in view, as ArcGIS's dynamic range adjustment draws them.
        RasterFunctionKind.BandArithmetic or RasterFunctionKind.ExtractBand => new CoverageStyle(StretchKind.Window),
        // ADR-156: new values are stretched over what is in view; a clip or a mask is drawn as the image is.
        RasterFunctionKind.Remap or RasterFunctionKind.Statistics or RasterFunctionKind.Arithmetic => new CoverageStyle(StretchKind.Window),
        _ => CoverageStyle.Default,
    };

    /// <summary>
    /// Applies the function to a window of the image whose bands these are — ADR-151's pixel functions need each band's
    /// no-data; ADR-136's surface functions take the first band's.
    /// </summary>
    /// <param name="window">The values, as read.</param>
    /// <param name="cellWidth">A cell's width on the ground, metres.</param>
    /// <param name="cellHeight">A cell's height on the ground, metres.</param>
    /// <param name="bands">The image's bands.</param>
    /// <returns>The function's values.</returns>
    /// <param name="place">Where the window's top-left corner is and how large its cells are, in the image's reference —
    /// what a clip needs to know which pixels its polygon covers.</param>
    public CoverageWindow Apply(
        CoverageWindow window, double cellWidth, double cellHeight, IReadOnlyList<BandInfo> bands,
        (double Left, double Top, double PixelWidth, double PixelHeight)? place = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(bands);

        if (IsSurface || Kind == RasterFunctionKind.None)
        {
            return Apply(window, cellWidth, cellHeight, bands.Count > 0 ? bands[0].NoData : null);
        }

        if (Kind is RasterFunctionKind.Clip or RasterFunctionKind.Remap or RasterFunctionKind.Mask
            or RasterFunctionKind.Statistics or RasterFunctionKind.Arithmetic)
        {
            return ApplyValues(window, bands, place);
        }

        int pixels = window.Width * window.Height;
        int count = window.Bands;

        double Value(int pixel, int band)
        {
            double v = window.Samples[(pixel * count) + band];
            return double.IsNaN(v) || (band < bands.Count && bands[band].NoData is { } absent && v == absent) ? double.NaN : v;
        }

        if (Kind == RasterFunctionKind.ExtractBand)
        {
            IReadOnlyList<int> ids = BandIds!;
            double[] chosen = new double[pixels * ids.Count];

            for (int p = 0; p < pixels; p++)
            {
                for (int b = 0; b < ids.Count; b++)
                {
                    chosen[(p * ids.Count) + b] = window.Samples[(p * count) + ids[b]];
                }
            }

            return new CoverageWindow(window.Width, window.Height, ids.Count, chosen);
        }

        double[] result = new double[pixels];

        for (int p = 0; p < pixels; p++)
        {
            if (Kind == RasterFunctionKind.Ndvi)
            {
                double red = Value(p, RedBand), infrared = Value(p, InfraredBand), sum = infrared + red;
                double index = double.IsNaN(sum) || sum == 0 ? double.NaN : (infrared - red) / sum;
                result[p] = Scientific || double.IsNaN(index) ? index : (index * 100) + 100;
            }
            else
            {
                int pixel = p;
                result[p] = Expression!.Evaluate(band => band < count ? Value(pixel, band) : double.NaN);
            }
        }

        return new CoverageWindow(window.Width, window.Height, 1, result);
    }

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

    /// <summary>ADR-156's functions over a window.</summary>
    private CoverageWindow ApplyValues(
        CoverageWindow window, IReadOnlyList<BandInfo> bands, (double Left, double Top, double PixelWidth, double PixelHeight)? place)
    {
        int w = window.Width, h = window.Height, count = window.Bands, pixels = w * h;

        double Value(int pixel, int band)
        {
            double v = window.Samples[(pixel * count) + band];
            return double.IsNaN(v) || (band < bands.Count && bands[band].NoData is { } absent && v == absent) ? double.NaN : v;
        }

        if (Kind is RasterFunctionKind.Clip or RasterFunctionKind.Mask)
        {
            double[] kept = (double[])window.Samples.Clone();

            for (int p = 0; p < pixels; p++)
            {
                bool keep;

                if (Kind == RasterFunctionKind.Clip)
                {
                    if (place is not { } at)
                    {
                        throw new InvalidOperationException("Clip needs to know where the window is.");
                    }

                    double x = at.Left + (((p % w) + 0.5) * at.PixelWidth), y = at.Top - (((p / w) + 0.5) * at.PixelHeight);
                    keep = Inside(ClipRings!, x, y) == KeepInside;
                }
                else
                {
                    keep = true;

                    for (int b = 0; b < count && b < IncludedRanges!.Count; b++)
                    {
                        double v = Value(p, b);
                        keep &= !double.IsNaN(v) && v >= IncludedRanges[b].Low && v <= IncludedRanges[b].High;
                    }
                }

                if (!keep)
                {
                    for (int b = 0; b < count; b++)
                    {
                        kept[(p * count) + b] = b < bands.Count && bands[b].NoData is { } none ? none : double.NaN;
                    }
                }
            }

            return new CoverageWindow(w, h, count, kept);
        }

        if (Kind == RasterFunctionKind.Arithmetic)
        {
            double[] result = new double[pixels * count];

            for (int i = 0; i < result.Length; i++)
            {
                double v = Value(i / count, i % count);
                double answer = Operation switch
                {
                    1 => v + Constant,
                    2 => v - Constant,
                    3 => v * Constant,
                    _ => Constant == 0 ? double.NaN : v / Constant,
                };
                result[i] = double.IsFinite(answer) ? answer : double.NaN;
            }

            return new CoverageWindow(w, h, count, result);
        }

        double[] single = new double[pixels];

        if (Kind == RasterFunctionKind.Remap)
        {
            for (int p = 0; p < pixels; p++)
            {
                double v = Value(p, 0);

                if (double.IsNaN(v) || NoDataRanges!.Any(r => v >= r.Low && v < r.High))
                {
                    single[p] = double.NaN;
                    continue;
                }

                int matched = -1;

                for (int i = 0; i < RemapRanges!.Count && matched < 0; i++)
                {
                    if (v >= RemapRanges[i].Low && v < RemapRanges[i].High)
                    {
                        matched = i;
                    }
                }

                single[p] = matched >= 0 ? RemapRanges[matched].Value : AllowUnmatched ? v : double.NaN;
            }

            return new CoverageWindow(w, h, 1, single);
        }

        // Statistics over a neighbourhood of the first band; a cell outside the window is not counted.
        int halfX = Kernel.Columns / 2, halfY = Kernel.Rows / 2;
        List<double> around = new(Kernel.Columns * Kernel.Rows);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                around.Clear();

                for (int dy = -halfY; dy <= halfY; dy++)
                {
                    for (int dx = -halfX; dx <= halfX; dx++)
                    {
                        int nx = x + dx, ny = y + dy;

                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && Value((ny * w) + nx, 0) is var v && !double.IsNaN(v))
                        {
                            around.Add(v);
                        }
                    }
                }

                single[(y * w) + x] = around.Count == 0 || double.IsNaN(Value((y * w) + x, 0)) ? double.NaN : StatisticsType switch
                {
                    1 => around.Min(),
                    2 => around.Max(),
                    3 => around.Average(),
                    4 => Math.Sqrt(around.Sum(v => Math.Pow(v - around.Average(), 2)) / around.Count),
                    5 => Median(around),
                    6 => around.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key,
                    _ => around.GroupBy(v => v).OrderBy(g => g.Count()).ThenBy(g => g.Key).First().Key,
                };
            }
        }

        return new CoverageWindow(w, h, 1, single);
    }

    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>Whether a point is inside a polygon's rings, even-odd, so a hole is outside.</summary>
    private static bool Inside(IReadOnlyList<double[]> rings, double x, double y)
    {
        bool inside = false;

        foreach (double[] ring in rings)
        {
            for (int i = 0, j = ring.Length - 2; i < ring.Length; j = i, i += 2)
            {
                double xi = ring[i], yi = ring[i + 1], xj = ring[j], yj = ring[j + 1];

                if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
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
