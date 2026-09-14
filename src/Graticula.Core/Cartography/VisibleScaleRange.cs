using System;

namespace Graticula.Cartography;

/// <summary>
/// The scales a layer draws at — ArcGIS's <c>minScale</c> and <c>maxScale</c>, ADR-070.
/// </summary>
/// <remarks>
/// <para>
/// <b>ArcGIS's numbers and ArcGIS's meaning, because ArcGIS clients act on them.</b> A scale is the
/// denominator of 1:<em>n</em> at 96 dots per inch with 39.37 inches to the metre — the convention
/// every ArcGIS tiling scheme states, and the one <see cref="TilingScheme"/> already follows. Zero
/// means no limit on that side. <see cref="MinScale"/> is the <em>largest</em> denominator the layer
/// draws at, so zooming out past it hides the layer; <see cref="MaxScale"/> is the smallest, so
/// zooming in past it hides the layer. The names read backwards to anyone who has not met them, and
/// they are kept because a client reading <c>minScale</c> off a layer document expects exactly this.
/// </para>
/// <para>
/// <b>Not the WMS scale denominator.</b> WMS measures against a 0.28 mm pixel rather than a 96 dpi
/// one, so the same map is 1:50,000 to one and 1:47,247 to the other; <see cref="WmsDenominator"/>
/// converts.
/// </para>
/// </remarks>
/// <param name="MinScale">The largest scale denominator the layer draws at, or 0 for no limit.</param>
/// <param name="MaxScale">The smallest scale denominator the layer draws at, or 0 for no limit.</param>
public readonly record struct VisibleScaleRange(double MinScale, double MaxScale)
{
    /// <summary>A layer that draws at every scale.</summary>
    public static VisibleScaleRange Unlimited => new(0, 0);

    /// <summary>Dots per metre at 96 dpi and 39.37 inches to the metre — see <see cref="TilingScheme.Dpi"/>.</summary>
    public const double DotsPerMetre = TilingScheme.Dpi * 39.37;

    /// <summary>
    /// The scale of Web Mercator level 0 in a 512-pixel tile scheme, the one this server's vector tiles use.
    /// </summary>
    /// <remarks>Half of the 256-pixel figure every basemap states, 591,657,527.591555.</remarks>
    public const double VectorTileLevel0Scale = 295_828_763.795777;

    /// <summary>
    /// A relative tolerance for comparing scales, so a value stored from one of the tables above is
    /// not pushed across a level boundary by the last digit of a double.
    /// </summary>
    private const double Tolerance = 1e-9;

    /// <summary>Whether either side limits anything.</summary>
    public bool IsLimited => MinScale > 0 || MaxScale > 0;

    /// <summary>
    /// Why a pair cannot be stored, or null when it can.
    /// </summary>
    /// <param name="minScale">The candidate minimum scale.</param>
    /// <param name="maxScale">The candidate maximum scale.</param>
    /// <returns>A sentence for the caller, or null.</returns>
    public static string? Refusal(double minScale, double maxScale)
    {
        if (!double.IsFinite(minScale) || !double.IsFinite(maxScale) || minScale < 0 || maxScale < 0)
        {
            return "A scale is a positive denominator, as in 1:50000, or 0 for no limit.";
        }

        if (minScale > 1e10 || maxScale > 1e10)
        {
            return "A scale above 1:10,000,000,000 is larger than the whole world on any screen.";
        }

        if (minScale > 0 && maxScale > 0 && maxScale >= minScale)
        {
            return $"The layer would never draw: it hides when zoomed out past 1:{minScale:0} and "
                + $"when zoomed in past 1:{maxScale:0}. The zoomed-in limit must be the smaller number.";
        }

        return null;
    }

    /// <summary>The ArcGIS scale for a resolution in metres per pixel.</summary>
    /// <param name="metresPerPixel">Ground distance one pixel covers.</param>
    /// <returns>The denominator.</returns>
    public static double ScaleOf(double metresPerPixel) => metresPerPixel * DotsPerMetre;

    /// <summary>The scale a vector tile of level <paramref name="z"/> is drawn at when it fills its 512 pixels.</summary>
    /// <param name="z">The tile level.</param>
    /// <returns>The denominator.</returns>
    public static double VectorTileScale(int z) => VectorTileLevel0Scale / Math.Pow(2, z);

    /// <summary>Whether a map at <paramref name="scale"/> shows this layer.</summary>
    /// <param name="scale">The map's ArcGIS scale denominator.</param>
    /// <returns>True when the scale is inside the range.</returns>
    public bool DrawsAt(double scale)
    {
        if (MinScale > 0 && scale > MinScale * (1 + Tolerance))
        {
            return false;
        }

        return !(MaxScale > 0 && scale < MaxScale * (1 - Tolerance));
    }

    /// <summary>Whether a vector tile of level <paramref name="z"/> can be shown anywhere inside the range.</summary>
    /// <param name="z">The tile level.</param>
    /// <returns>False when every scale the tile is drawn at is outside the range.</returns>
    /// <remarks>
    /// <b>A tile is not drawn at one scale.</b> A client keeps a level-<em>z</em> tile on screen from the
    /// scale at which it fills its 512 pixels until the map is twice as zoomed in, and only then asks
    /// for level <em>z</em>+1. So the tile is needed when any part of that interval — from
    /// <see cref="VectorTileScale"/> down to half of it, the lower end open — is inside the range, and
    /// an empty tile is right only when none is. Testing the single scale would blank the last level
    /// before the limit for the half of its zoom where the layer should still be drawing.
    /// </remarks>
    public bool CarriesVectorTile(int z)
    {
        double coarsest = VectorTileScale(z);
        double finest = coarsest / 2;

        if (MinScale > 0 && finest >= MinScale * (1 - Tolerance))
        {
            return false;
        }

        return !(MaxScale > 0 && coarsest < MaxScale * (1 - Tolerance));
    }

    /// <summary>The WMS 1.3.0 scale denominator for an ArcGIS scale, which assumes a 0.28 mm pixel.</summary>
    /// <param name="scale">The ArcGIS denominator.</param>
    /// <returns>The WMS denominator for the same map.</returns>
    public static double WmsDenominator(double scale) =>
        scale / DotsPerMetre / MapScale.StandardPixelMetres;

    /// <summary>The style zoom this range starts drawing at, or null when zooming out is not limited.</summary>
    /// <remarks>A MapLibre style's <c>minzoom</c>, on the 512-pixel scheme a style's zoom is measured on.</remarks>
    public double? StyleMinZoom => MinScale > 0 ? Math.Log2(VectorTileLevel0Scale / MinScale) : null;

    /// <summary>The style zoom this range stops drawing at, or null when zooming in is not limited.</summary>
    public double? StyleMaxZoom => MaxScale > 0 ? Math.Log2(VectorTileLevel0Scale / MaxScale) : null;
}
