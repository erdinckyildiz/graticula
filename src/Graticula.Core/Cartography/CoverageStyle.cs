using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Coverages;

namespace Graticula.Cartography;

/// <summary>
/// How a band's values are spread across the colours available to them.
/// </summary>
/// <remarks>
/// <b>The three every raster viewer offers, and no more.</b> A stretch is a decision
/// about contrast, and the useful ones are: use the range the data actually occupies,
/// use the range the format allows, or use a range somebody typed. Histogram
/// equalisation and standard-deviation clipping are the next two and neither is in
/// this cut — <see cref="StretchKind"/> gaining a member is a change with a test,
/// which is the point of it being a closed list.
/// </remarks>
public enum StretchKind
{
    /// <summary>
    /// The smallest and largest values actually present in what was read.
    /// </summary>
    /// <remarks>
    /// <b>Computed from the window, not from the file.</b> A COG rarely records its
    /// own statistics and reading the whole image to find them would cost more than
    /// drawing it. Stretching to what is on screen is what every viewer does and it
    /// has a consequence worth stating: <em>panning changes the contrast</em>, because
    /// the window changed. That is why it is not the default for a published layer.
    /// </remarks>
    Window = 1,

    /// <summary>
    /// The full range the sample kind can hold — 0–255 for bytes, 0–65535 for 16-bit.
    /// </summary>
    /// <remarks>
    /// <b>The default, because it is the only one that is stable across requests.</b>
    /// Two adjacent tiles rendered by two workers agree, and a map assembled from
    /// tiles has no seams — which <see cref="Window"/> cannot promise.
    /// </remarks>
    Full = 2,

    /// <summary>Between two numbers somebody chose.</summary>
    Fixed = 3,
}

/// <summary>
/// One stop of a colour ramp: a value, and the colour it becomes.
/// </summary>
/// <param name="Value">Where on the stretched 0–1 range this stop sits.</param>
/// <param name="Colour">The colour there.</param>
public readonly record struct RampStop(double Value, Rgba Colour);

/// <summary>
/// The rule that turns samples into pixels. Tier 1, and the reason the line is there.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half of raster rendering that is ours</b>
/// ([ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4).
/// <see cref="ICoverageReader"/> hands over numbers; everything about what those
/// numbers look like is decided here, in Tier 1, beside
/// <see cref="SymbologyPlan"/> which makes the equivalent decision for vectors.
/// </para>
/// <para>
/// <b>Two shapes, and which one applies is decided by the band count rather than by a
/// setting.</b> Three or more bands are already colours and want the first three used
/// as red, green and blue. One band is a measurement and wants a ramp. Making that a
/// choice would let somebody ask for a colour ramp over an RGB photograph, which has
/// no meaning, and the refusal would then have to be written somewhere.
/// </para>
/// <para>
/// <b>No-data is transparent and is checked before anything else.</b> A no-data pixel
/// is absent rather than dark: it must not enter the stretch, because one sentinel of
/// -9999 in a window would flatten every real value into the top of the range, and it
/// must not be drawn, because the map beneath it is the honest answer.
/// </para>
/// </remarks>
public sealed class CoverageStyle
{
    private readonly IReadOnlyList<RampStop> _ramp;

    /// <summary>Describes how a coverage is drawn.</summary>
    /// <param name="stretch">How values spread across the colours.</param>
    /// <param name="minimum">The low end, when <paramref name="stretch"/> is fixed.</param>
    /// <param name="maximum">The high end, when <paramref name="stretch"/> is fixed.</param>
    /// <param name="ramp">
    /// The colours a single band passes through, or empty for greyscale.
    /// </param>
    public CoverageStyle(
        StretchKind stretch = StretchKind.Full,
        double? minimum = null,
        double? maximum = null,
        IReadOnlyList<RampStop>? ramp = null)
    {
        if (stretch == StretchKind.Fixed && (minimum is null || maximum is null))
        {
            throw new ArgumentException(
                "A fixed stretch needs both ends. Without them it is a window stretch wearing "
                + "another name, and the difference matters: one is stable across requests and "
                + "the other is not.",
                nameof(stretch));
        }

        Stretch = stretch;
        Minimum = minimum;
        Maximum = maximum;
        _ramp = ramp is { Count: > 0 } ? [.. ramp.OrderBy(s => s.Value)] : [];
    }

    /// <summary>How values spread across the colours.</summary>
    public StretchKind Stretch { get; }

    /// <summary>The low end of a fixed stretch.</summary>
    public double? Minimum { get; }

    /// <summary>The high end of a fixed stretch.</summary>
    public double? Maximum { get; }

    /// <summary>The colours a single band passes through; empty means greyscale.</summary>
    public IReadOnlyList<RampStop> Ramp => _ramp;

    /// <summary>
    /// Turns a window of samples into the colours a canvas can draw.
    /// </summary>
    /// <param name="window">What the reader returned.</param>
    /// <param name="bands">The bands it came from, for their no-data and sample kind.</param>
    /// <returns>One colour per pixel, row-major from the top.</returns>
    public Rgba[] Paint(CoverageWindow window, IReadOnlyList<BandInfo> bands)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(bands);

        Rgba[] pixels = new Rgba[window.Width * window.Height];

        bool colour = window.Bands >= 3;

        // <b>The range is computed once for the window, not once per pixel.</b> A
        // window stretch that recomputed per pixel would be a different stretch in
        // every pixel, which is not a stretch.
        (double low, double high) = Range(window, bands, colour);
        double span = high - low;

        if (Math.Abs(span) < double.Epsilon)
        {
            span = 1;
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            int at = i * window.Bands;

            if (colour)
            {
                if (IsAbsent(window.Samples[at], bands, 0)
                    || IsAbsent(window.Samples[at + 1], bands, 1)
                    || IsAbsent(window.Samples[at + 2], bands, 2))
                {
                    pixels[i] = Rgba.Transparent;
                    continue;
                }

                pixels[i] = new Rgba(
                    Level(window.Samples[at], low, span),
                    Level(window.Samples[at + 1], low, span),
                    Level(window.Samples[at + 2], low, span),
                    255);

                continue;
            }

            double value = window.Samples[at];

            if (IsAbsent(value, bands, 0))
            {
                pixels[i] = Rgba.Transparent;
                continue;
            }

            double position = Math.Clamp((value - low) / span, 0, 1);

            pixels[i] = _ramp.Count > 0
                ? Along(position)
                : Grey(position);
        }

        return pixels;
    }

    /// <summary>Where a colour ramp sits at a position between zero and one.</summary>
    /// <remarks>
    /// <b>Interpolated in CIELAB through <see cref="ColourSpace"/>, not in RGB.</b>
    /// A ramp from blue to yellow interpolated in RGB passes through a muddy grey at
    /// the midpoint, which reads as a feature of the data that is not there. The
    /// vector renderer already makes this choice for `interpolate-lab`; making the
    /// opposite one here would give two parts of the same server different ideas of
    /// what a gradient is.
    /// </remarks>
    /// <param name="position">Between zero and one.</param>
    /// <returns>The colour.</returns>
    public Rgba Along(double position)
    {
        if (_ramp.Count == 0)
        {
            return Grey(position);
        }

        if (position <= _ramp[0].Value)
        {
            return _ramp[0].Colour;
        }

        if (position >= _ramp[^1].Value)
        {
            return _ramp[^1].Colour;
        }

        for (int i = 1; i < _ramp.Count; i++)
        {
            if (position > _ramp[i].Value)
            {
                continue;
            }

            RampStop before = _ramp[i - 1];
            RampStop after = _ramp[i];

            double width = after.Value - before.Value;
            double t = width <= double.Epsilon ? 0 : (position - before.Value) / width;

            return ColourSpace.Mix(before.Colour, after.Colour, t, ColourSpace.Interpolation.Lab);
        }

        return _ramp[^1].Colour;
    }

    /// <summary>The low and high ends this window is stretched between.</summary>
    private (double Low, double High) Range(
        CoverageWindow window, IReadOnlyList<BandInfo> bands, bool colour)
    {
        switch (Stretch)
        {
            case StretchKind.Fixed:
                return (Minimum!.Value, Maximum!.Value);

            case StretchKind.Full:
                return (0, Ceiling(bands.Count > 0 ? bands[0].Kind : SampleKind.Unsigned8));

            default:
                break;
        }

        double low = double.MaxValue;
        double high = double.MinValue;
        int used = colour ? Math.Min(3, window.Bands) : 1;

        for (int i = 0; i < window.Samples.Length; i += window.Bands)
        {
            for (int band = 0; band < used; band++)
            {
                double value = window.Samples[i + band];

                if (IsAbsent(value, bands, band))
                {
                    continue;
                }

                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }
        }

        // Every pixel was absent. Any range will do because nothing will be drawn, and
        // returning the format's range keeps the arithmetic below finite.
        return low > high
            ? (0, Ceiling(bands.Count > 0 ? bands[0].Kind : SampleKind.Unsigned8))
            : (low, high);
    }

    /// <summary>The largest value a sample kind holds, for a full-range stretch.</summary>
    /// <remarks>
    /// <b>Floating point has no ceiling, so it gets one and it is stated.</b> A float
    /// band is usually a measurement in its own units — metres, degrees, a ratio — and
    /// there is no format range to stretch to. One is the least surprising answer for
    /// a normalised index and the wrong one for elevation, which is why a float band
    /// almost always wants a fixed stretch and why this is a fallback rather than a
    /// recommendation.
    /// </remarks>
    private static double Ceiling(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => 255,
        SampleKind.Signed16 => 32767,
        SampleKind.Unsigned16 => 65535,
        SampleKind.Signed32 => int.MaxValue,
        _ => 1,
    };

    private static bool IsAbsent(double value, IReadOnlyList<BandInfo> bands, int band)
    {
        if (double.IsNaN(value))
        {
            return true;
        }

        if (band >= bands.Count)
        {
            return false;
        }

        double? absent = bands[band].NoData;

        return absent is not null && Math.Abs(value - absent.Value) < double.Epsilon;
    }

    private static byte Level(double value, double low, double span) =>
        (byte)Math.Clamp(Math.Round((value - low) / span * 255), 0, 255);

    private static Rgba Grey(double position)
    {
        byte level = (byte)Math.Clamp(Math.Round(position * 255), 0, 255);

        return new Rgba(level, level, level, 255);
    }

    /// <summary>The style a band gets when nobody has chosen one.</summary>
    /// <remarks>
    /// <b>A full-range stretch and no ramp, and both halves are the conservative
    /// choice.</b> Full range is the only stretch that gives two adjacent tiles the
    /// same contrast, and greyscale is what a measurement looks like when nobody has
    /// said what it measures. A generated ramp would be this server inventing meaning
    /// for somebody's data, which is a different thing from
    /// <see cref="GeneratedSymbology"/> giving a polygon layer a colour — a polygon
    /// has no natural colour and a value does not become one by being coloured.
    /// </remarks>
    public static CoverageStyle Default { get; } = new();

    /// <summary>Reads a style from the compact text a service definition stores.</summary>
    /// <remarks>
    /// <b>Deliberately small, because the canonical style document is
    /// [ADR-033](../../../docs/adr/ADR-033-symbology.md)'s and this is not it.</b> A
    /// coverage's appearance belongs in that document eventually; until it does, a
    /// request parameter and a stored string need somewhere to be read, and inventing
    /// a second full style language for the interim would be the thing §82 forbids.
    /// The grammar is <c>stretch:full</c>, <c>stretch:window</c>, or
    /// <c>stretch:0,4000</c>.
    /// </remarks>
    /// <param name="text">The text, or null.</param>
    /// <returns>The style it names, or <see cref="Default"/>.</returns>
    public static CoverageStyle Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        string value = text.Trim();

        if (value.StartsWith("stretch:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = value["stretch:".Length..].Trim();

            if (rest.Equals("window", StringComparison.OrdinalIgnoreCase))
            {
                return new CoverageStyle(StretchKind.Window);
            }

            if (rest.Equals("full", StringComparison.OrdinalIgnoreCase))
            {
                return new CoverageStyle(StretchKind.Full);
            }

            string[] parts = rest.Split(',');

            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double low)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double high))
            {
                return new CoverageStyle(StretchKind.Fixed, low, high);
            }
        }

        return Default;
    }
}
