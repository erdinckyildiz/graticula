using System;

namespace Graticula.Cartography;

/// <summary>
/// Resolution, scale denominator and zoom — the three names for one number.
/// </summary>
/// <remarks>
/// <para>
/// <b>WMS speaks in scale denominators and a MapLibre style speaks in zoom
/// levels</b>, and this server has to satisfy both from one request that mentions
/// neither: a client sends an extent and a pixel size, and everything else is
/// derived. Deriving it in one place is the difference between a style that switches
/// layers on at the same point as the capabilities document says it will, and one
/// that does not.
/// </para>
/// <para>
/// <b>Both constants are somebody else's, and both are cited.</b> The 0.28 mm pixel
/// and the 111,319.49 metres per degree are from the WMS 1.3.0 specification
/// (OGC 06-042) §7.2.4.6.9, which defines the scale denominator and says exactly how
/// a geographic CRS is converted for it. The web-mercator resolution at zoom 0 is
/// the circumference of the WGS 84 sphere divided by 256.
/// </para>
/// </remarks>
public static class MapScale
{
    /// <summary>
    /// The pixel size WMS 1.3.0 defines a scale denominator against, in metres.
    /// </summary>
    /// <remarks>
    /// <b>0.28 mm, which is not any real screen.</b> It is a convention, and its
    /// only job is to make two servers agree about what <c>ScaleDenominator</c>
    /// means. A server that used the client's actual DPI would publish numbers no
    /// other server's numbers could be compared with.
    /// </remarks>
    public const double StandardPixelMetres = 0.00028;

    /// <summary>Metres per degree, as WMS 1.3.0 defines the conversion.</summary>
    public const double MetresPerDegree = 111_319.49079327358;

    /// <summary>Web-mercator resolution at zoom 0, in metres per pixel.</summary>
    private const double ZoomZeroResolution = 156_543.03392804097;

    /// <summary>Resolution in metres per pixel, whatever the CRS measures in.</summary>
    /// <param name="unitsPerPixel">Map units per pixel.</param>
    /// <param name="geographic">Whether the CRS measures in degrees.</param>
    /// <returns>Metres per pixel.</returns>
    public static double MetresPerPixel(double unitsPerPixel, bool geographic) =>
        geographic ? unitsPerPixel * MetresPerDegree : unitsPerPixel;

    /// <summary>The WMS scale denominator for a resolution.</summary>
    /// <param name="unitsPerPixel">Map units per pixel.</param>
    /// <param name="geographic">Whether the CRS measures in degrees.</param>
    /// <returns>The denominator, as in 1:<em>n</em>.</returns>
    public static double Denominator(double unitsPerPixel, bool geographic) =>
        MetresPerPixel(unitsPerPixel, geographic) / StandardPixelMetres;

    /// <summary>
    /// The web-mercator zoom level whose resolution matches this one.
    /// </summary>
    /// <remarks>
    /// <b>Fractional, and deliberately not rounded.</b> A style interpolating a line
    /// width between zoom 8 and zoom 12 should get the width for where the map
    /// actually is; rounding to 9 would make every intermediate scale draw as one of
    /// twenty-three widths. Rounding is right for choosing a tile and wrong for
    /// evaluating a style.
    /// </remarks>
    /// <param name="unitsPerPixel">Map units per pixel.</param>
    /// <param name="geographic">Whether the CRS measures in degrees.</param>
    /// <returns>The zoom, clamped to 0–24.</returns>
    public static double Zoom(double unitsPerPixel, bool geographic)
    {
        double metres = MetresPerPixel(unitsPerPixel, geographic);

        if (metres <= 0)
        {
            return 24;
        }

        return Math.Clamp(Math.Log2(ZoomZeroResolution / metres), 0, 24);
    }
}
