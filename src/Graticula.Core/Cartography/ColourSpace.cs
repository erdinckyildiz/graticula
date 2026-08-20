using System;

namespace Graticula.Cartography;

/// <summary>
/// Colour interpolation, in the three spaces a MapLibre style can name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three spaces because the style language has three</b> — <c>interpolate</c>,
/// <c>interpolate-hcl</c> and <c>interpolate-lab</c> — and a renderer that treated
/// all three as RGB would draw a ramp that is visibly wrong in the middle while
/// being exactly right at both ends. That is the failure nobody catches in review:
/// the two colours the author checked are the two that are correct.
/// </para>
/// <para>
/// <b>The maths is CIE's, published, and cited as such.</b> sRGB's transfer
/// function and the D65 white point are from the sRGB specification (IEC 61966-2-1);
/// CIELAB and CIELCh are from CIE 15. Nothing here is derived from any
/// implementation — [ADR-030](../../../docs/adr/ADR-030-reading-the-reference-implementation.md)
/// condition 3 makes a public specification the citation for anything a public
/// specification defines, and colour spaces are the clearest case there is.
/// </para>
/// <para>
/// <b>Alpha is always linear, in every space.</b> Transparency is not a colour and
/// interpolating it through Lab would be meaningless.
/// </para>
/// </remarks>
public static class ColourSpace
{
    /// <summary>D65, the white point sRGB is defined against.</summary>
    private const double WhiteX = 95.047;
    private const double WhiteY = 100.0;
    private const double WhiteZ = 108.883;

    /// <summary>How two colours are mixed.</summary>
    public enum Interpolation
    {
        /// <summary>Straight down each channel. Fast, and what a style means by default.</summary>
        Rgb = 0,

        /// <summary>Through CIELAB, which is perceptually even.</summary>
        Lab = 1,

        /// <summary>Through CIELCh, which keeps the hue path circular.</summary>
        Hcl = 2,
    }

    /// <summary>Mixes two colours.</summary>
    /// <param name="from">At <paramref name="t"/> = 0.</param>
    /// <param name="to">At <paramref name="t"/> = 1.</param>
    /// <param name="t">The position, clamped to 0–1.</param>
    /// <param name="space">Which space to travel through.</param>
    /// <returns>The mixture.</returns>
    public static Rgba Mix(Rgba from, Rgba to, double t, Interpolation space)
    {
        double position = Math.Clamp(t, 0, 1);

        byte alpha = Channel(from.A + ((to.A - from.A) * position));

        if (space == Interpolation.Rgb)
        {
            return new Rgba(
                Channel(from.R + ((to.R - from.R) * position)),
                Channel(from.G + ((to.G - from.G) * position)),
                Channel(from.B + ((to.B - from.B) * position)),
                alpha);
        }

        (double l1, double a1, double b1) = ToLab(from);
        (double l2, double a2, double b2) = ToLab(to);

        if (space == Interpolation.Lab)
        {
            return FromLab(
                l1 + ((l2 - l1) * position),
                a1 + ((a2 - a1) * position),
                b1 + ((b2 - b1) * position),
                alpha);
        }

        // LCh: chroma and lightness travel straight, hue travels the short way
        // round. Interpolating hue linearly would take 350° to 10° the long way
        // through the entire wheel, which is the classic rainbow-in-the-middle
        // artefact.
        double c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        double c2 = Math.Sqrt((a2 * a2) + (b2 * b2));

        double h1 = Math.Atan2(b1, a1);
        double h2 = Math.Atan2(b2, a2);

        double delta = h2 - h1;

        if (delta > Math.PI)
        {
            delta -= 2 * Math.PI;
        }
        else if (delta < -Math.PI)
        {
            delta += 2 * Math.PI;
        }

        double l = l1 + ((l2 - l1) * position);
        double c = c1 + ((c2 - c1) * position);
        double h = h1 + (delta * position);

        return FromLab(l, c * Math.Cos(h), c * Math.Sin(h), alpha);
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>sRGB to CIELAB, through XYZ.</summary>
    private static (double L, double A, double B) ToLab(Rgba colour)
    {
        double r = Linear(colour.R / 255.0);
        double g = Linear(colour.G / 255.0);
        double b = Linear(colour.B / 255.0);

        double x = ((r * 0.4124) + (g * 0.3576) + (b * 0.1805)) * 100;
        double y = ((r * 0.2126) + (g * 0.7152) + (b * 0.0722)) * 100;
        double z = ((r * 0.0193) + (g * 0.1192) + (b * 0.9505)) * 100;

        double fx = Pivot(x / WhiteX);
        double fy = Pivot(y / WhiteY);
        double fz = Pivot(z / WhiteZ);

        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIELAB back to sRGB.</summary>
    private static Rgba FromLab(double l, double a, double b, byte alpha)
    {
        double fy = (l + 16) / 116;
        double fx = fy + (a / 500);
        double fz = fy - (b / 200);

        double x = Unpivot(fx) * WhiteX / 100;
        double y = Unpivot(fy) * WhiteY / 100;
        double z = Unpivot(fz) * WhiteZ / 100;

        double r = (x * 3.2406) + (y * -1.5372) + (z * -0.4986);
        double g = (x * -0.9689) + (y * 1.8758) + (z * 0.0415);
        double bl = (x * 0.0557) + (y * -0.2040) + (z * 1.0570);

        return new Rgba(
            Channel(Companded(r) * 255),
            Channel(Companded(g) * 255),
            Channel(Companded(bl) * 255),
            alpha);
    }

    /// <summary>sRGB's transfer function, inverted — IEC 61966-2-1.</summary>
    private static double Linear(double channel) =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);

    /// <summary>sRGB's transfer function.</summary>
    private static double Companded(double channel)
    {
        double clamped = Math.Clamp(channel, 0, 1);

        return clamped <= 0.0031308
            ? clamped * 12.92
            : (1.055 * Math.Pow(clamped, 1 / 2.4)) - 0.055;
    }

    private static double Pivot(double ratio) =>
        ratio > 0.008856
            ? Math.Cbrt(ratio)
            : ((903.3 * ratio) + 16) / 116;

    private static double Unpivot(double f)
    {
        double cubed = f * f * f;

        return cubed > 0.008856 ? cubed : ((116 * f) - 16) / 903.3;
    }
}
