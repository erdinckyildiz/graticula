using System;
using System.Globalization;

namespace Graticula.Cartography;

/// <summary>
/// A colour with an alpha channel, as a style writes it and a canvas paints it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 1, so that no colour crosses the port as somebody else's type.</b>
/// [build-vs-adopt-policy.md](../../../docs/build-vs-adopt-policy.md) §4 keeps
/// library types out of Tier 1 signatures, and a colour is the type that would
/// otherwise appear in every one of them.
/// </para>
/// <para>
/// <b>Straight alpha, not premultiplied.</b> A style document writes
/// <c>rgba(0,0,0,0.5)</c> meaning half-transparent black, and premultiplying at
/// this boundary would make the value a caller reads back differ from the one they
/// wrote. Premultiplication is the rasteriser's business and stays behind the port.
/// </para>
/// </remarks>
/// <param name="R">Red, 0–255.</param>
/// <param name="G">Green, 0–255.</param>
/// <param name="B">Blue, 0–255.</param>
/// <param name="A">Alpha, 0–255, where 0 is invisible.</param>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>Fully transparent, which is a WMS background and not a colour.</summary>
    public static Rgba Transparent => new(0, 0, 0, 0);

    /// <summary>Opaque white, the WMS default background.</summary>
    public static Rgba White => new(255, 255, 255, 255);

    /// <summary>Opaque black.</summary>
    public static Rgba Black => new(0, 0, 0, 255);

    /// <summary>Whether this paints nothing at all.</summary>
    public bool IsInvisible => A == 0;

    /// <summary>The same colour at a different opacity.</summary>
    /// <remarks>
    /// <b>Multiplied into the existing alpha rather than replacing it.</b> A style
    /// may set both <c>fill-color: rgba(…,0.5)</c> and <c>fill-opacity: 0.5</c>,
    /// and MapLibre composes the two; replacing would make the second silently
    /// discard the first.
    /// </remarks>
    /// <param name="opacity">0 to 1.</param>
    /// <returns>The colour.</returns>
    public Rgba WithOpacity(double opacity)
    {
        double scaled = A * Math.Clamp(opacity, 0, 1);

        return this with { A = (byte)Math.Round(scaled, MidpointRounding.AwayFromZero) };
    }

    /// <summary>
    /// Reads a colour as a style document may write one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The four spellings a MapLibre style actually uses</b> — <c>#rgb</c>,
    /// <c>#rrggbb</c>, <c>#rrggbbaa</c> and <c>rgba(r,g,b,a)</c> — plus
    /// <c>rgb(r,g,b)</c>. CSS colour names are deliberately absent: a style that
    /// says <c>rebeccapurple</c> is refused with a message rather than drawn in
    /// black, because a colour silently wrong is the hardest kind of map defect
    /// to see.
    /// </para>
    /// <para>
    /// <b>In <c>rgba()</c> the alpha is 0–1 and the channels are 0–255</b>, which
    /// is CSS's own inconsistency and not ours. Reading alpha as 0–255 here would
    /// make every semi-transparent style fully opaque.
    /// </para>
    /// </remarks>
    /// <param name="text">The value.</param>
    /// <param name="colour">The colour.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out Rgba colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();

        if (value[0] == '#')
        {
            return TryParseHex(value.AsSpan(1), out colour);
        }

        bool hasAlpha = value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase);

        if (!hasAlpha && !value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int open = value.IndexOf('(', StringComparison.Ordinal);

        if (value[^1] != ')')
        {
            return false;
        }

        string[] parts = value[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != (hasAlpha ? 4 : 3))
        {
            return false;
        }

        byte[] channels = new byte[3];

        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                return false;
            }

            channels[i] = (byte)Math.Clamp(Math.Round(n, MidpointRounding.AwayFromZero), 0, 255);
        }

        byte alpha = 255;

        if (hasAlpha)
        {
            if (!double.TryParse(
                parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
            {
                return false;
            }

            alpha = (byte)Math.Clamp(
                Math.Round(a * 255, MidpointRounding.AwayFromZero), 0, 255);
        }

        colour = new Rgba(channels[0], channels[1], channels[2], alpha);
        return true;
    }

    /// <summary>Reads a hexadecimal colour with no leading hash.</summary>
    private static bool TryParseHex(ReadOnlySpan<char> digits, out Rgba colour)
    {
        colour = default;

        // #rgb is shorthand for #rrggbb, so each digit doubles. Not an
        // approximation: 0xF becoming 0xFF is what the shorthand means.
        if (digits.Length == 3 || digits.Length == 4)
        {
            Span<char> expanded = stackalloc char[digits.Length * 2];

            for (int i = 0; i < digits.Length; i++)
            {
                expanded[i * 2] = digits[i];
                expanded[(i * 2) + 1] = digits[i];
            }

            return TryParseHex(expanded, out colour);
        }

        if (digits.Length is not (6 or 8))
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[4] { 0, 0, 0, 255 };

        for (int i = 0; i * 2 < digits.Length; i++)
        {
            if (!byte.TryParse(
                digits.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out byte channel))
            {
                return false;
            }

            channels[i] = channel;
        }

        colour = new Rgba(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }

    /// <summary>The colour as <c>#rrggbbaa</c>, which round-trips through TryParse.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}{A:x2}");
}
