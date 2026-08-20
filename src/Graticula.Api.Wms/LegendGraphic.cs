using System;
using System.Collections.Generic;
using Graticula.Cartography;
using Graticula.Geometries;

namespace Graticula.Api.Wms;

/// <summary>
/// A legend swatch: one small picture of what a layer looks like.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not in the WMS core, and built anyway.</b> <c>GetLegendGraphic</c> arrives from
/// the SLD profile rather than from WMS 1.3.0 itself, and every client that draws a
/// legend asks for it. A server without one does not look minimal to those clients;
/// it looks broken, because the legend box appears with a missing image in it.
/// </para>
/// <para>
/// <b>Drawn through the same port as the map</b>, so a swatch cannot drift from what
/// the map draws — it is the same <see cref="SymbologyPlan"/> resolved against a
/// synthetic feature. A legend built from a separate description of the style is a
/// legend that is eventually wrong, and wrong in the one place a reader trusts.
/// </para>
/// <para>
/// <b>One swatch, not a classified legend.</b> A style whose colour is a
/// <c>match</c> over a column has as many entries as the column has values, and
/// enumerating them means reading the data. This draws the style as it applies to a
/// feature with no attributes, which is the fallback branch of every expression —
/// honest, and less than a cartographer wants.
/// [Q-131](../../../docs/open-questions.md).
/// </para>
/// </remarks>
public static class LegendGraphic
{
    /// <summary>
    /// Draws a swatch.
    /// </summary>
    /// <param name="canvas">Where to draw, already the size of the swatch.</param>
    /// <param name="plan">The layer's compiled style.</param>
    /// <param name="geometry">What shape the layer's features are.</param>
    /// <param name="background">The swatch background, usually transparent.</param>
    public static void Draw(
        IMapCanvas canvas, SymbologyPlan plan, GeometryKind geometry, Rgba background)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(plan);

        canvas.Clear(background);

        // An empty context: no attributes, and a zoom in the middle of the usual
        // range so a zoom-interpolated width draws at something rather than at its
        // first stop.
        StyleExpression.Context context = new(
            new Dictionary<string, object?>(StringComparer.Ordinal), 12);

        double inset = Math.Max(1, Math.Min(canvas.Width, canvas.Height) * 0.15);

        foreach (PlanLayer layer in plan.Layers)
        {
            if (layer.Resolve(context) is not { } symbol)
            {
                continue;
            }

            switch (symbol)
            {
                case MapSymbol.Area area when IsAreal(geometry):
                    canvas.FillArea(Box(canvas, inset), area);
                    break;

                case MapSymbol.Stroke stroke:
                    canvas.StrokeLine(
                        IsAreal(geometry) ? Box(canvas, inset) : Diagonal(canvas, inset), stroke);
                    break;

                case MapSymbol.Marker marker:
                    canvas.DrawMarker(canvas.Width / 2.0, canvas.Height / 2.0, Fit(marker, canvas));
                    break;

                default:
                    // A label layer has nothing to show in a swatch: the text it
                    // would draw comes from a feature, and there is no feature.
                    break;
            }
        }
    }

    private static bool IsAreal(GeometryKind geometry) =>
        geometry is GeometryKind.Polygon or GeometryKind.MultiPolygon;

    /// <summary>
    /// A marker shrunk to fit, when the style's radius is larger than the swatch.
    /// </summary>
    /// <remarks>
    /// <b>Shrunk rather than clipped.</b> A 40-pixel marker in a 20-pixel swatch
    /// draws as a solid square of colour, which tells a reader the layer is a filled
    /// area. Scaling keeps the shape legible and the colour honest.
    /// </remarks>
    private static MapSymbol.Marker Fit(MapSymbol.Marker marker, IMapCanvas canvas)
    {
        double room = (Math.Min(canvas.Width, canvas.Height) / 2.0) - marker.OutlineWidth - 1;

        return marker.Radius <= room
            ? marker
            : marker with { Radius = Math.Max(1, room) };
    }

    private static PixelPath Box(IMapCanvas canvas, double inset)
    {
        PixelPath path = new();

        path.Begin(closed: true);
        path.Add(inset, inset);
        path.Add(canvas.Width - inset, inset);
        path.Add(canvas.Width - inset, canvas.Height - inset);
        path.Add(inset, canvas.Height - inset);
        path.End();

        return path;
    }

    /// <summary>A line across the swatch, which is how a line layer reads at 20 pixels.</summary>
    private static PixelPath Diagonal(IMapCanvas canvas, double inset)
    {
        PixelPath path = new();

        path.Begin(closed: false);
        path.Add(inset, canvas.Height - inset);
        path.Add(canvas.Width / 2.0, inset);
        path.Add(canvas.Width - inset, canvas.Height - inset);
        path.End();

        return path;
    }
}
