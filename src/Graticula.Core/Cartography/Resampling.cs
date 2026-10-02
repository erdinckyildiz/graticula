using System;

namespace Graticula.Cartography;

/// <summary>How a raster's cells are read between their centres — ADR-142, an image service's <c>interpolation</c>.</summary>
public enum Resampling
{
    /// <summary>The cell the point falls in: a value the image holds, and the only right answer for classes.</summary>
    Nearest = 0,

    /// <summary>The four nearest cells, weighted by distance: smooth, for continuous surfaces and photographs.</summary>
    Bilinear = 1,

    /// <summary>The sixteen nearest, by Catmull-Rom's cubic: sharper than bilinear, and may overshoot a little.</summary>
    Cubic = 2,
}

/// <summary>
/// Reads a raster between its cells — ADR-142. A position is in source pixel units, the centre of cell (c, r) at
/// (c + ½, r + ½); a position outside the source answers nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Values next to no-data fall back to the nearest cell.</b> Interpolating with a no-data neighbour would blend a
/// height with −9999; dropping the neighbour would still shift the value. The nearest cell is a value the image holds.
/// </para>
/// <para>
/// <b>Colours are blended premultiplied</b>, so a transparent neighbour fades the edge rather than darkening it.
/// </para>
/// </remarks>
public static class Resampler
{
    /// <summary>Parses ArcGIS's <c>interpolation</c>: <c>RSP_NearestNeighbor</c>, <c>RSP_BilinearInterpolation</c>, <c>RSP_CubicConvolution</c>.</summary>
    /// <param name="text">The parameter, or null.</param>
    /// <param name="resampling">What it asks for; null when it asks for nothing.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it was understood.</returns>
    public static bool TryParse(string? text, out Resampling? resampling, out string? error)
    {
        resampling = null;
        error = null;

        switch (text?.Trim())
        {
            case null or "":
                return true;
            case "RSP_NearestNeighbor":
                resampling = Resampling.Nearest;
                return true;
            case "RSP_BilinearInterpolation":
                resampling = Resampling.Bilinear;
                return true;
            case "RSP_CubicConvolution":
                resampling = Resampling.Cubic;
                return true;
            case "RSP_Majority":
                error = "`interpolation=RSP_Majority` is not one this image service applies. It applies RSP_NearestNeighbor, "
                    + "RSP_BilinearInterpolation and RSP_CubicConvolution; for classes, RSP_NearestNeighbor keeps every value one "
                    + "the image holds.";
                return false;
            default:
                error = $"`interpolation={text}` is not one this image service knows. It applies RSP_NearestNeighbor, "
                    + "RSP_BilinearInterpolation and RSP_CubicConvolution.";
                return false;
        }
    }

    /// <summary>ArcGIS's name for a resampling, as a service document states its default.</summary>
    /// <param name="resampling">The resampling.</param>
    /// <returns><c>NearestNeighbor</c>, <c>Bilinear</c> or <c>Cubic</c>.</returns>
    public static string Name(Resampling resampling) => resampling switch
    {
        Resampling.Bilinear => "Bilinear",
        Resampling.Cubic => "Cubic",
        _ => "NearestNeighbor",
    };

    /// <summary>One band's value at a position, or false where there is none.</summary>
    /// <param name="samples">Width × height × bands values, pixel-interleaved.</param>
    /// <param name="width">The source's width.</param>
    /// <param name="height">The source's height.</param>
    /// <param name="bands">Values a pixel.</param>
    /// <param name="band">Which band.</param>
    /// <param name="u">Across, in source pixels.</param>
    /// <param name="v">Down, in source pixels.</param>
    /// <param name="how">The resampling.</param>
    /// <param name="absent">Whether a value is no-data.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether the position is inside the source.</returns>
    public static bool TryValue(
        ReadOnlySpan<double> samples, int width, int height, int bands, int band, double u, double v, Resampling how,
        Func<double, bool> absent, out double value)
    {
        ArgumentNullException.ThrowIfNull(absent);
        value = 0;

        if (!(u >= 0 && v >= 0 && u < width && v < height))
        {
            return false;
        }

        int nearestX = Math.Min((int)u, width - 1), nearestY = Math.Min((int)v, height - 1);
        value = samples[(((nearestY * width) + nearestX) * bands) + band];

        if (how == Resampling.Nearest || absent(value))
        {
            return true;
        }

        int reach = how == Resampling.Cubic ? 2 : 1;
        double x = u - 0.5, y = v - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double total = 0;

        for (int j = 1 - reach; j <= reach; j++)
        {
            double wy = Weight(how, j - fy);

            for (int i = 1 - reach; i <= reach; i++)
            {
                double wx = Weight(how, i - fx);

                if (wx == 0 || wy == 0)
                {
                    continue;
                }

                int cx = Math.Clamp(x0 + i, 0, width - 1), cy = Math.Clamp(y0 + j, 0, height - 1);
                double neighbour = samples[(((cy * width) + cx) * bands) + band];

                if (absent(neighbour))
                {
                    return true; // the nearest value, already set
                }

                total += neighbour * wx * wy;
            }
        }

        value = total;
        return true;
    }

    /// <summary>A colour at a position, or transparent outside the source.</summary>
    /// <param name="source">Width × height colours.</param>
    /// <param name="width">The source's width.</param>
    /// <param name="height">The source's height.</param>
    /// <param name="u">Across, in source pixels.</param>
    /// <param name="v">Down, in source pixels.</param>
    /// <param name="how">The resampling.</param>
    /// <returns>The colour.</returns>
    public static Rgba Colour(ReadOnlySpan<Rgba> source, int width, int height, double u, double v, Resampling how)
    {
        if (!(u >= 0 && v >= 0 && u < width && v < height))
        {
            return Rgba.Transparent;
        }

        if (how == Resampling.Nearest)
        {
            return source[(Math.Min((int)v, height - 1) * width) + Math.Min((int)u, width - 1)];
        }

        int reach = how == Resampling.Cubic ? 2 : 1;
        double x = u - 0.5, y = v - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double r = 0, g = 0, b = 0, a = 0;

        for (int j = 1 - reach; j <= reach; j++)
        {
            double wy = Weight(how, j - fy);

            for (int i = 1 - reach; i <= reach; i++)
            {
                double w = Weight(how, i - fx) * wy;

                if (w == 0)
                {
                    continue;
                }

                Rgba c = source[(Math.Clamp(y0 + j, 0, height - 1) * width) + Math.Clamp(x0 + i, 0, width - 1)];
                double alpha = c.A / 255.0;
                r += c.R * alpha * w;
                g += c.G * alpha * w;
                b += c.B * alpha * w;
                a += alpha * w;
            }
        }

        if (a <= 1e-6)
        {
            return Rgba.Transparent;
        }

        static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return new Rgba(Channel(r / a), Channel(g / a), Channel(b / a), Channel(a * 255));
    }

    /// <summary>A neighbour's weight at a distance, in pixels.</summary>
    private static double Weight(Resampling how, double distance)
    {
        double d = Math.Abs(distance);

        if (how == Resampling.Bilinear)
        {
            return d < 1 ? 1 - d : 0;
        }

        // Catmull-Rom (a = -0.5).
        return d < 1 ? (1.5 * d * d * d) - (2.5 * d * d) + 1
            : d < 2 ? (-0.5 * d * d * d) + (2.5 * d * d) - (4 * d) + 2
            : 0;
    }
}
