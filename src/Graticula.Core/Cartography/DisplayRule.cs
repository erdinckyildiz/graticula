using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Graticula.Coverages;

namespace Graticula.Cartography;

/// <summary>The stretches a <c>Stretch</c> rule names, by ArcGIS's numbers for them.</summary>
public enum RuleStretch
{
    /// <summary>The values as they are, 0 to 255.</summary>
    None = 0,

    /// <summary>A number of standard deviations either side of the mean.</summary>
    StandardDeviation = 3,

    /// <summary>Spread so that every output level holds as many pixels.</summary>
    HistogramEqualization = 4,

    /// <summary>Between the smallest and the largest value.</summary>
    MinimumMaximum = 5,

    /// <summary>Between the values a percentage of pixels in from either end.</summary>
    PercentClip = 6,
}

/// <summary>What a band's values look like: its range, mean and spread, and a 256-bin histogram of them.</summary>
/// <param name="Minimum">The smallest value.</param>
/// <param name="Maximum">The largest value.</param>
/// <param name="Mean">The mean.</param>
/// <param name="StandardDeviation">The standard deviation.</param>
/// <param name="Low">The low edge of the first bin.</param>
/// <param name="High">The high edge of the last bin.</param>
/// <param name="Counts">How many values each bin holds.</param>
public sealed record BandSummary(
    double Minimum, double Maximum, double Mean, double StandardDeviation, double Low, double High, IReadOnlyList<long> Counts)
{
    /// <summary>Describes one band of a window, no-data and NaN left out — an 8-bit band's bins are its 256 values.</summary>
    /// <param name="window">The values.</param>
    /// <param name="band">Which band.</param>
    /// <param name="info">The band, for its type and its no-data.</param>
    /// <returns>The description; zeros when it holds nothing.</returns>
    public static BandSummary Of(CoverageWindow window, int band, BandInfo? info)
    {
        ArgumentNullException.ThrowIfNull(window);

        double? noData = info?.NoData;
        double min = double.MaxValue, max = double.MinValue, sum = 0, squares = 0;
        long counted = 0;

        for (int i = band; i < window.Samples.Length; i += window.Bands)
        {
            double value = window.Samples[i];

            if (double.IsNaN(value) || (noData is { } absent && value == absent))
            {
                continue;
            }

            min = Math.Min(min, value);
            max = Math.Max(max, value);
            sum += value;
            squares += value * value;
            counted++;
        }

        long[] counts = new long[256];

        if (counted == 0)
        {
            return new BandSummary(0, 0, 0, 0, 0, 0, counts);
        }

        double mean = sum / counted;
        bool bytes = info?.Kind == SampleKind.Unsigned8;
        double low = bytes ? -0.5 : min;
        double high = bytes ? 255.5 : max;

        if (high > low)
        {
            for (int i = band; i < window.Samples.Length; i += window.Bands)
            {
                double value = window.Samples[i];

                if (!double.IsNaN(value) && (noData is not { } absent || value != absent))
                {
                    counts[Math.Clamp((int)((value - low) / (high - low) * 256), 0, 255)]++;
                }
            }
        }

        return new BandSummary(min, max, mean, Math.Sqrt(Math.Max(0, (squares / counted) - (mean * mean))), low, high, counts);
    }
}

/// <summary>
/// How an ArcGIS client asks an image service to draw itself — ADR-138: the <c>renderingRule</c> chain the JS SDK sends
/// for a renderer set on an <c>ImageryLayer</c>, <c>Stretch</c>, <c>Colormap</c> over a stretch with a colour ramp, and
/// <c>Colormap</c> over <c>Remap</c> for classes. A display rule, not a raster function: it chooses colours, and the
/// values it draws from are the image's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from what the SDK was measured sending</b> (ADR-138 §4), and refused by name for anything else in the chain —
/// a function this server does not apply, a stretch it does not make, a ramp it cannot draw.
/// </para>
/// <para>
/// <b>Statistics are the service's</b>, from the sample it describes itself by, so adjacent tiles agree — unless the
/// rule asks for <c>DRA</c>, a dynamic range adjustment, when they are the drawn window's own, as in ArcGIS.
/// </para>
/// </remarks>
public sealed class DisplayRule
{
    private const string Shapes = "this image service draws Stretch, Colormap over Stretch with a colour ramp, and "
        + "Colormap over Remap or over the values with a colour map";

    private readonly IReadOnlyList<(Rgba From, Rgba To, ColourSpace.Interpolation Space)>? _ramp;
    private readonly Dictionary<long, Rgba>? _colours;
    private readonly IReadOnlyList<(double Low, double High, double Value)>? _remap;
    private readonly IReadOnlyList<(double Low, double High)> _noDataRanges = [];

    private DisplayRule(
        bool stretched,
        IReadOnlyList<(Rgba, Rgba, ColourSpace.Interpolation)>? ramp,
        Dictionary<long, Rgba>? colours,
        IReadOnlyList<(double, double, double)>? remap)
    {
        Stretched = stretched;
        _ramp = ramp;
        _colours = colours;
        _remap = remap;
    }

    /// <summary>Whether the chain stretches the values to 0–255.</summary>
    public bool Stretched { get; }

    /// <summary>Which stretch.</summary>
    public RuleStretch Stretch { get; private init; }

    /// <summary>The standard deviations a standard-deviation stretch spans either side of the mean.</summary>
    public double Deviations { get; private init; } = 2;

    /// <summary>The percentage of pixels a percent clip leaves below its low end.</summary>
    public double MinimumPercent { get; private init; } = 0.25;

    /// <summary>The percentage of pixels a percent clip leaves above its high end.</summary>
    public double MaximumPercent { get; private init; } = 0.25;

    /// <summary>Whether the statistics are the drawn window's own rather than the service's.</summary>
    public bool Dynamic { get; private init; }

    /// <summary>Each band's gamma, or null.</summary>
    public IReadOnlyList<double>? Gamma { get; private init; }

    /// <summary>Each band's minimum, maximum, mean and standard deviation, when the rule gives its own.</summary>
    public IReadOnlyList<double[]>? Statistics { get; private init; }

    /// <summary>Whether drawing needs the service's statistics.</summary>
    public bool NeedsStatistics => Stretched && !Dynamic && Stretch != RuleStretch.None
        && !(Stretch == RuleStretch.MinimumMaximum && Statistics is not null);

    /// <summary>
    /// Reads a <c>renderingRule</c> when its outermost function is one a display rule is made of.
    /// </summary>
    /// <param name="json">The rule.</param>
    /// <param name="bands">The image's band count.</param>
    /// <param name="rule">The rule, when it is one.</param>
    /// <param name="error">Why a display rule was refused.</param>
    /// <returns>Whether the rule is a display rule at all — false leaves it to the raster functions.</returns>
    public static bool TryParse(string? json, int bands, out DisplayRule? rule, out string? error)
    {
        rule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonElement root;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        string name = Name(root);

        if (!name.Equals("Stretch", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Colormap", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("Remap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            rule = Read(root, bands);
        }
        catch (FormatException refused)
        {
            error = $"`renderingRule`: {refused.Message} Refused rather than drawn as if it had been applied.";
        }

        return true;
    }

    private static DisplayRule Read(JsonElement root, int bands)
    {
        string name = Name(root);
        JsonElement arguments = Arguments(root);

        if (name.Equals("Remap", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Remap gives classes numbers, and has no colours without a Colormap over it; {Shapes}.");
        }

        if (name.Equals("Stretch", StringComparison.OrdinalIgnoreCase))
        {
            Inner(arguments, "Stretch");
            return Stretching(arguments, null);
        }

        // Colormap: over a stretch with a ramp, or over Remap or the values with a map of colours.
        JsonElement inner = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("Raster", out JsonElement r)
            && r.ValueKind == JsonValueKind.Object ? r : default;
        string under = inner.ValueKind == JsonValueKind.Object ? Name(inner) : string.Empty;

        if (bands >= 3)
        {
            throw new FormatException("a Colormap colours one band, and this image service is a colour image.");
        }

        if (arguments.TryGetProperty("colorRamp", out JsonElement ramp) && ramp.ValueKind == JsonValueKind.Object)
        {
            if (!under.Equals("Stretch", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"a colour ramp is laid over stretched values, and this one is over {(under.Length == 0 ? "the raw values" : under)}; {Shapes}.");
            }

            JsonElement stretchArguments = Arguments(inner);
            Inner(stretchArguments, "Stretch");
            return Stretching(stretchArguments, Ramp(ramp));
        }

        if (arguments.TryGetProperty("Colormap", out JsonElement map) && map.ValueKind == JsonValueKind.Array)
        {
            Dictionary<long, Rgba> colours = [];

            foreach (JsonElement entry in map.EnumerateArray())
            {
                double[] parts = Numbers(entry, "a Colormap entry");

                if (parts.Length < 4)
                {
                    throw new FormatException("a Colormap entry is [value, red, green, blue].");
                }

                colours[(long)Math.Round(parts[0])] = new Rgba(Byte(parts[1]), Byte(parts[2]), Byte(parts[3]), parts.Length > 4 ? Byte(parts[4]) : (byte)255);
            }

            if (under.Length == 0 || under.Equals("Raster", StringComparison.OrdinalIgnoreCase))
            {
                return new DisplayRule(false, null, colours, null);
            }

            if (!under.Equals("Remap", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"a Colormap of values is laid over Remap or over the values, and this one is over {under}; {Shapes}.");
            }

            JsonElement remap = Arguments(inner);
            Inner(remap, "Remap");
            double[] ranges = remap.TryGetProperty("InputRanges", out JsonElement ir) ? Numbers(ir, "InputRanges") : [];
            double[] outputs = remap.TryGetProperty("OutputValues", out JsonElement ov) ? Numbers(ov, "OutputValues") : [];

            if (ranges.Length != outputs.Length * 2)
            {
                throw new FormatException("Remap's InputRanges are a low and a high for each of its OutputValues.");
            }

            if (remap.TryGetProperty("AllowUnmatched", out JsonElement unmatched) && unmatched.ValueKind == JsonValueKind.True)
            {
                throw new FormatException("Remap with AllowUnmatched keeps values no range names, which this server does not colour.");
            }

            List<(double, double)> noData = [];

            if (remap.TryGetProperty(nameof(NoDataRanges), out JsonElement nd))
            {
                double[] edges = Numbers(nd, nameof(NoDataRanges));

                for (int i = 0; i + 1 < edges.Length; i += 2)
                {
                    noData.Add((edges[i], edges[i + 1]));
                }
            }

            return new DisplayRule(false, null, colours, [.. outputs.Select((v, i) => (ranges[2 * i], ranges[(2 * i) + 1], v))])
            {
                NoDataRanges = noData,
            };
        }

        throw new FormatException("a Colormap carries a colorRamp or a Colormap of values, and this one carries neither.");
    }

    private IReadOnlyList<(double Low, double High)> NoDataRanges
    {
        get => _noDataRanges;
        init => _noDataRanges = value;
    }

    private static DisplayRule Stretching(
        JsonElement arguments, IReadOnlyList<(Rgba, Rgba, ColourSpace.Interpolation)>? ramp)
    {
        int type = arguments.TryGetProperty("StretchType", out JsonElement t) && t.TryGetInt32(out int given) ? given : 0;

        if (!Enum.IsDefined(typeof(RuleStretch), type))
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                $"StretchType {type} is not one this server makes: it makes 0 (none), 3 (standard deviation), 4 (histogram equalization), 5 (minimum-maximum) and 6 (percent clip)."));
        }

        if (arguments.TryGetProperty("ComputeGamma", out JsonElement compute) && compute.ValueKind == JsonValueKind.True)
        {
            throw new FormatException("ComputeGamma asks the server to choose a gamma, which this server does not.");
        }

        if (arguments.TryGetProperty("Min", out JsonElement low) && low.ValueKind == JsonValueKind.Number
            && (low.GetDouble() != 0 || (arguments.TryGetProperty("Max", out JsonElement high) && high.GetDouble() != 255)))
        {
            throw new FormatException("Min and Max set the stretch's output range, which this server keeps at 0 to 255.");
        }

        IReadOnlyList<double>? gamma = null;

        if (arguments.TryGetProperty("UseGamma", out JsonElement use) && use.ValueKind == JsonValueKind.True)
        {
            double[] values = arguments.TryGetProperty(nameof(Gamma), out JsonElement g) ? Numbers(g, nameof(Gamma)) : [];

            if (values.Length == 0 || values.Any(v => !(v > 0)))
            {
                throw new FormatException("UseGamma needs a Gamma above zero for each band.");
            }

            gamma = values;
        }

        IReadOnlyList<double[]>? statistics = null;

        if (arguments.TryGetProperty(nameof(Statistics), out JsonElement s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
        {
            statistics = [.. s.EnumerateArray().Select(b => Numbers(b, nameof(Statistics)))];

            if (statistics.Any(b => b.Length < 2 || !(b[1] > b[0])))
            {
                throw new FormatException("Statistics gives each band a minimum and a maximum above it, then its mean and standard deviation.");
            }
        }

        double Argument(string name, double fallback) =>
            arguments.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : fallback;

        DisplayRule rule = new(true, ramp, null, null)
        {
            Stretch = (RuleStretch)type,
            Deviations = Argument("NumberOfStandardDeviations", 2),
            MinimumPercent = Argument("MinPercent", 0.25),
            MaximumPercent = Argument("MaxPercent", 0.25),
            Dynamic = arguments.TryGetProperty("DRA", out JsonElement dra) && dra.ValueKind == JsonValueKind.True,
            Gamma = gamma,
            Statistics = statistics,
        };

        if (rule.Deviations <= 0 || rule.MinimumPercent < 0 || rule.MaximumPercent < 0 || rule.MinimumPercent + rule.MaximumPercent >= 100)
        {
            throw new FormatException("a stretch spans more than zero standard deviations, and a percent clip leaves something between its ends.");
        }

        return rule;
    }

    private static List<(Rgba, Rgba, ColourSpace.Interpolation)> Ramp(JsonElement ramp)
    {
        string type = ramp.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;

        if (type.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            List<(Rgba, Rgba, ColourSpace.Interpolation)> parts = [];

            if (ramp.TryGetProperty("colorRamps", out JsonElement each) && each.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in each.EnumerateArray())
                {
                    parts.AddRange(Ramp(part));
                }
            }

            return parts.Count > 0 ? parts : throw new FormatException("a multipart colour ramp names its parts in colorRamps.");
        }

        if (!type.Equals("algorithmic", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"a colour ramp of type '{type}' is not one this server draws: it draws algorithmic and multipart ramps.");
        }

        string algorithm = ramp.TryGetProperty("algorithm", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
        ColourSpace.Interpolation space = algorithm switch
        {
            "esriHSVAlgorithm" => ColourSpace.Interpolation.Hsv,
            "esriLabLChAlgorithm" => ColourSpace.Interpolation.Hcl,
            "esriCIELabAlgorithm" or "" => ColourSpace.Interpolation.Lab,
            _ => throw new FormatException($"the colour ramp algorithm '{algorithm}' is not one this server draws: it draws esriCIELabAlgorithm, esriHSVAlgorithm and esriLabLChAlgorithm."),
        };

        return [(Colour(ramp, "fromColor"), Colour(ramp, "toColor"), space)];
    }

    private static Rgba Colour(JsonElement ramp, string name)
    {
        double[] parts = ramp.TryGetProperty(name, out JsonElement c) ? Numbers(c, name) : [];

        return parts.Length >= 3
            ? new Rgba(Byte(parts[0]), Byte(parts[1]), Byte(parts[2]), parts.Length > 3 ? Byte(parts[3]) : (byte)255)
            : throw new FormatException($"a colour ramp's {name} is [red, green, blue, alpha].");
    }

    /// <summary>A stretch or a remap reads the image's own values: its Raster is the image, not a further function.</summary>
    private static void Inner(JsonElement arguments, string outer)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("Raster", out JsonElement raster)
            && raster.ValueKind == JsonValueKind.Object && Name(raster) is { Length: > 0 } nested
            && !nested.Equals("Raster", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"{outer} over {nested} is a chain this server does not apply; {Shapes}.");
        }
    }

    private static string Name(JsonElement function) =>
        function.ValueKind == JsonValueKind.Object && function.TryGetProperty("rasterFunction", out JsonElement n)
            && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;

    private static JsonElement Arguments(JsonElement function) =>
        function.ValueKind == JsonValueKind.Object && function.TryGetProperty("rasterFunctionArguments", out JsonElement a)
            && a.ValueKind == JsonValueKind.Object ? a : default;

    private static double[] Numbers(JsonElement array, string what)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"{what} is a list of numbers.");
        }

        return [.. array.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.Number ? n.GetDouble()
            : throw new FormatException($"{what} is a list of numbers."))];
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>Draws a window under the rule.</summary>
    /// <param name="window">The values.</param>
    /// <param name="bands">Its bands, for their types and no-data.</param>
    /// <param name="service">The service's own statistics for each band, when <see cref="NeedsStatistics"/>.</param>
    /// <returns>One colour per pixel, row-major from the top.</returns>
    public Rgba[] Paint(CoverageWindow window, IReadOnlyList<BandInfo> bands, IReadOnlyList<BandSummary>? service)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(bands);

        Rgba[] pixels = new Rgba[window.Width * window.Height];
        bool colour = window.Bands >= 3 && Stretched;
        int drawn = colour ? 3 : 1;
        Func<double, double>[] levels = new Func<double, double>[drawn];

        if (Stretched)
        {
            for (int band = 0; band < drawn; band++)
            {
                BandInfo? info = band < bands.Count ? bands[band] : null;
                BandSummary? summary = Dynamic || service is null || band >= service.Count
                    ? (Stretch == RuleStretch.None || (Stretch == RuleStretch.MinimumMaximum && Statistics is not null) ? null : BandSummary.Of(window, band, info))
                    : service[band];
                levels[band] = Level(band, summary);
            }
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            int at = i * window.Bands;
            bool absent = false;

            for (int band = 0; band < drawn; band++)
            {
                double value = window.Samples[at + band];
                absent |= double.IsNaN(value) || (band < bands.Count && bands[band].NoData is { } none && value == none);
            }

            if (absent)
            {
                pixels[i] = Rgba.Transparent;
                continue;
            }

            if (colour)
            {
                pixels[i] = new Rgba(Out(levels[0](window.Samples[at])), Out(levels[1](window.Samples[at + 1])), Out(levels[2](window.Samples[at + 2])), 255);
                continue;
            }

            double v = window.Samples[at];

            if (!Stretched)
            {
                pixels[i] = Classify(v);
                continue;
            }

            double position = levels[0](v);
            pixels[i] = _ramp is { Count: > 0 } ? Along(position) : new Rgba(Out(position), Out(position), Out(position), 255);
        }

        return pixels;
    }

    private static byte Out(double position) => (byte)Math.Clamp(Math.Round(position * 255), 0, 255);

    private Rgba Classify(double value)
    {
        foreach ((double low, double high) in _noDataRanges)
        {
            if (value >= low && value <= high)
            {
                return Rgba.Transparent;
            }
        }

        double key = value;

        if (_remap is not null)
        {
            bool matched = false;

            foreach ((double low, double high, double output) in _remap)
            {
                if (value >= low && value < high)
                {
                    key = output;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return Rgba.Transparent;
            }
        }

        return _colours!.TryGetValue((long)Math.Round(key), out Rgba found) ? found : Rgba.Transparent;
    }

    private Rgba Along(double position)
    {
        int parts = _ramp!.Count;
        double scaled = Math.Clamp(position, 0, 1) * parts;
        int part = Math.Min((int)scaled, parts - 1);
        (Rgba from, Rgba to, ColourSpace.Interpolation space) = _ramp[part];
        return ColourSpace.Mix(from, to, scaled - part, space);
    }

    /// <summary>How a band's value becomes a position from 0 to 1, after its gamma.</summary>
    private Func<double, double> Level(int band, BandSummary? summary)
    {
        double gamma = Gamma is { Count: > 0 } g ? g[Math.Min(band, g.Count - 1)] : 1;
        Func<double, double> spread;

        switch (Stretch)
        {
            case RuleStretch.None:
                spread = v => v / 255;
                break;

            case RuleStretch.HistogramEqualization:
            {
                BandSummary s = summary!;
                long total = s.Counts.Sum();
                double[] cumulative = new double[s.Counts.Count];
                long running = 0;

                for (int i = 0; i < cumulative.Length; i++)
                {
                    running += s.Counts[i];
                    cumulative[i] = total == 0 ? 0 : (double)running / total;
                }

                spread = v => s.High > s.Low ? cumulative[Math.Clamp((int)((v - s.Low) / (s.High - s.Low) * cumulative.Length), 0, cumulative.Length - 1)] : 0;
                break;
            }

            default:
                (double low, double high) = Range(band, summary);
                double span = high > low ? high - low : 1;
                spread = v => (v - low) / span;
                break;
        }

        return gamma == 1
            ? v => Math.Clamp(spread(v), 0, 1)
            : v => Math.Pow(Math.Clamp(spread(v), 0, 1), 1 / gamma);
    }

    private (double Low, double High) Range(int band, BandSummary? summary)
    {
        if (Stretch == RuleStretch.MinimumMaximum && Statistics is { Count: > 0 } given)
        {
            double[] own = given[Math.Min(band, given.Count - 1)];
            return (own[0], own[1]);
        }

        BandSummary s = summary!;

        switch (Stretch)
        {
            case RuleStretch.StandardDeviation:
                return (s.Mean - (Deviations * s.StandardDeviation), s.Mean + (Deviations * s.StandardDeviation));

            case RuleStretch.PercentClip:
                long total = s.Counts.Sum();
                double width = (s.High - s.Low) / s.Counts.Count;
                return (Percentile(s, total, MinimumPercent / 100, width, fromTop: false),
                    Percentile(s, total, MaximumPercent / 100, width, fromTop: true));

            default:
                return (s.Minimum, s.Maximum);
        }
    }

    /// <summary>The edge of the bin where the given share of the pixels has been passed, from either end.</summary>
    private static double Percentile(BandSummary s, long total, double share, double width, bool fromTop)
    {
        long passed = 0;
        int count = s.Counts.Count;

        for (int k = 0; k < count; k++)
        {
            int bin = fromTop ? count - 1 - k : k;
            passed += s.Counts[bin];

            if (total > 0 && (double)passed / total > share)
            {
                return fromTop ? s.Low + ((bin + 1) * width) : s.Low + (bin * width);
            }
        }

        return fromTop ? s.High : s.Low;
    }
}
