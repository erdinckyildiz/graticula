using System;
using System.Collections.Generic;
using Graticula.Cartography;
using Graticula.Geometries;

namespace Graticula.Api.Wms;

/// <summary>
/// A legend: one small picture of what a layer looks like, or one per class.
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
/// <b>A classified style gets a row per class, and that closed
/// [Q-131](../../../docs/open-questions.md) by contradicting it.</b> That row said
/// enumerating the classes *means reading the data*, and it does not: a <c>match</c>
/// carries its labels and a <c>step</c> carries its breaks, both written out in the
/// style this legend is already compiled from. So a classified legend costs no query,
/// and the request stays as cheap as the row required it to be. Each row is the whole
/// plan resolved against a feature carrying that class's value, which keeps the same
/// guarantee the single swatch had: the legend is drawn by the code that draws the
/// map.
/// </para>
/// <para>
/// <b>What it still will not do.</b> A style classifying on two different columns
/// gets the single swatch, because a legend is a strip and two independent
/// classifications are a grid — see <see cref="SymbologyPlan.LegendClasses"/>. A
/// continuous <c>interpolate</c> ramp is not enumerable and gets the single swatch
/// too; a gradient bar is a different picture and nobody has asked for one.
/// </para>
/// </remarks>
public static class LegendGraphic
{
    /// <summary>The most rows drawn, however many classes the style has.</summary>
    /// <remarks>
    /// <b>A bound, because a <c>match</c> over a code list has hundreds of labels.</b>
    /// An 800-row legend is a large PNG nobody reads, on a request a client makes once
    /// per layer and often again on every pan. What is dropped is said in the last row
    /// rather than silently: a legend that stops without saying so is worse than one
    /// that is honest about being partial.
    /// </remarks>
    public const int MaximumRows = 24;

    /// <summary>Space between a swatch and its label, in pixels.</summary>
    private const int Gap = 6;

    /// <summary>Space around the whole strip, in pixels.</summary>
    private const int Pad = 4;

    /// <summary>The label type size, in pixels.</summary>
    private const double LabelSize = 12;

    /// <summary>Widest a classified legend gets, in pixels.</summary>
    private const int MaximumWidth = 1024;

    /// <summary>
    /// Draws a legend onto a canvas it sizes itself.
    /// </summary>
    /// <remarks>
    /// <b>The canvas is created here because its size depends on the labels</b>, and
    /// only the thing holding the font can measure those. WIDTH and HEIGHT stop being
    /// the image's size and become one swatch's size, which is what the SLD profile
    /// means by them and what a classified legend looks like everywhere else. A layer
    /// with no classification draws exactly the image it drew before: one swatch, at
    /// the requested size, with nothing beside it.
    /// </remarks>
    /// <param name="canvases">Where canvases come from.</param>
    /// <param name="plan">The layer's compiled style.</param>
    /// <param name="geometry">What shape the layer's features are.</param>
    /// <param name="swatch">The size of one swatch, from WIDTH and HEIGHT.</param>
    /// <param name="background">The image background, usually transparent.</param>
    /// <returns>The canvas, which the caller disposes.</returns>
    public static IMapCanvas Draw(
        IMapCanvasFactory canvases,
        SymbologyPlan plan,
        GeometryKind geometry,
        (int Width, int Height) swatch,
        Rgba background)
    {
        ArgumentNullException.ThrowIfNull(canvases);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.LegendClasses() is not { } axis)
        {
            IMapCanvas one = canvases.Create(swatch.Width, swatch.Height);

            one.Clear(background);
            Swatch(one, plan, geometry, new PixelBox(0, 0, one.Width, one.Height), Nothing);

            return one;
        }

        return Classified(canvases, plan, geometry, swatch, background, axis);
    }

    /// <summary>
    /// Draws one class's swatch, with no label on it.
    /// </summary>
    /// <remarks>
    /// <b>For a face whose own format carries the labels.</b> ArcGIS's <c>legend</c>
    /// response is a list of entries, each with its own <c>label</c> and image, so
    /// painting the text into the picture would put it there twice. WMS has no such
    /// list — one request, one image — which is why that face draws a strip and this
    /// one draws a swatch at a time.
    /// </remarks>
    /// <param name="canvases">Where canvases come from.</param>
    /// <param name="plan">The layer's compiled style.</param>
    /// <param name="geometry">What shape the layer's features are.</param>
    /// <param name="size">The swatch size, in pixels.</param>
    /// <param name="background">The background, usually transparent.</param>
    /// <param name="field">The classified column, or null for the fallback swatch.</param>
    /// <param name="value">The value selecting this class, or null for the fallback.</param>
    /// <returns>The canvas, which the caller disposes.</returns>
    public static IMapCanvas DrawClass(
        IMapCanvasFactory canvases,
        SymbologyPlan plan,
        GeometryKind geometry,
        (int Width, int Height) size,
        Rgba background,
        string? field,
        object? value)
    {
        ArgumentNullException.ThrowIfNull(canvases);
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

        if (field is { Length: > 0 } && value is not null)
        {
            attributes[field] = value;
        }

        IMapCanvas canvas = canvases.Create(size.Width, size.Height);

        canvas.Clear(background);
        Swatch(canvas, plan, geometry, new PixelBox(0, 0, canvas.Width, canvas.Height), attributes);

        return canvas;
    }

    private static IMapCanvas Classified(
        IMapCanvasFactory canvases,
        SymbologyPlan plan,
        GeometryKind geometry,
        (int Width, int Height) swatch,
        Rgba background,
        StyleExpression.Classification axis)
    {
        List<(string Label, object? Value)> rows = [];

        foreach (StyleExpression.ClassCase each in axis.Cases)
        {
            // The last row is spent saying what the rest were, when there is a rest.
            if (rows.Count == MaximumRows - 1 && axis.Cases.Count > MaximumRows)
            {
                rows.Add(($"+{axis.Cases.Count - rows.Count} more", null));
                break;
            }

            rows.Add((each.Label, each.Value));

            if (rows.Count == MaximumRows)
            {
                break;
            }
        }

        MapSymbol.Label ink = new(Rgba.Black, LabelSize, Rgba.Transparent, 0);

        // <b>Measured on a canvas that is thrown away.</b> Text width is a property of
        // the font rather than of the surface, so a one-pixel canvas answers it as
        // well as the real one — and the real one cannot be created until the answer
        // is known.
        double widest = 0;

        using (IMapCanvas ruler = canvases.Create(1, 1))
        {
            foreach ((string label, _) in rows)
            {
                PixelBox box = ruler.MeasureLabel(label, ink, 0, 0);
                widest = Math.Max(widest, box.MaxX - box.MinX);
            }
        }

        int rowHeight = Math.Max(swatch.Height, (int)Math.Ceiling(LabelSize * 1.4));
        int width = Math.Min(
            MaximumWidth, (Pad * 2) + swatch.Width + Gap + (int)Math.Ceiling(widest));
        int height = (Pad * 2) + (rowHeight * rows.Count);

        IMapCanvas canvas = canvases.Create(width, height);

        canvas.Clear(background);

        for (int i = 0; i < rows.Count; i++)
        {
            (string label, object? value) = rows[i];
            double top = Pad + (i * rowHeight);

            PixelBox box = new(
                Pad,
                top + ((rowHeight - swatch.Height) / 2.0),
                Pad + swatch.Width,
                top + ((rowHeight + swatch.Height) / 2.0));

            Dictionary<string, object?> attributes = new(StringComparer.Ordinal);

            // <b>Absent, not null, for the fallback row.</b> `Same(null, label)` is
            // false for every label a style can write, so an absent attribute lands in
            // the fallback branch — which is precisely what that row is a picture of.
            if (value is not null)
            {
                attributes[axis.Field] = value;
            }

            Swatch(canvas, plan, geometry, box, attributes);

            // <b>Centred text placed as if it were left-aligned.</b> The canvas port
            // draws labels centred on an anchor, because that is what a map label
            // needs; a legend wants a left edge, so the anchor is the measured
            // half-width to the right of it.
            PixelBox measured = canvas.MeasureLabel(label, ink, 0, 0);

            canvas.DrawLabel(
                label,
                ink,
                Pad + swatch.Width + Gap + ((measured.MaxX - measured.MinX) / 2),
                top + (rowHeight / 2.0) + (LabelSize / 3));
        }

        return canvas;
    }

    /// <summary>A feature with no attributes: the fallback branch of every expression.</summary>
    private static readonly Dictionary<string, object?> Nothing = new(StringComparer.Ordinal);

    /// <summary>Draws one swatch into a box.</summary>
    private static void Swatch(
        IMapCanvas canvas,
        SymbologyPlan plan,
        GeometryKind geometry,
        PixelBox box,
        IReadOnlyDictionary<string, object?> attributes)
    {
        // A zoom in the middle of the usual range, so a zoom-interpolated width draws
        // at something rather than at its first stop.
        StyleExpression.Context context = new(attributes, 12);

        double inset = Math.Max(1, Math.Min(box.MaxX - box.MinX, box.MaxY - box.MinY) * 0.15);

        foreach (PlanLayer layer in plan.Layers)
        {
            if (layer.Resolve(context) is not { } symbol)
            {
                continue;
            }

            switch (symbol)
            {
                case MapSymbol.Area area when IsAreal(geometry):
                    canvas.FillArea(Ring(box, inset), area);
                    break;

                case MapSymbol.Stroke stroke:
                    canvas.StrokeLine(
                        IsAreal(geometry) ? Ring(box, inset) : Diagonal(box, inset), stroke);
                    break;

                case MapSymbol.Marker marker:
                    canvas.DrawMarker(
                        (box.MinX + box.MaxX) / 2.0, (box.MinY + box.MaxY) / 2.0, Fit(marker, box));
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
    private static MapSymbol.Marker Fit(MapSymbol.Marker marker, PixelBox box)
    {
        double room =
            (Math.Min(box.MaxX - box.MinX, box.MaxY - box.MinY) / 2.0) - marker.OutlineWidth - 1;

        return marker.Radius <= room
            ? marker
            : marker with { Radius = Math.Max(1, room) };
    }

    private static PixelPath Ring(PixelBox box, double inset)
    {
        PixelPath path = new();

        path.Begin(closed: true);
        path.Add(box.MinX + inset, box.MinY + inset);
        path.Add(box.MaxX - inset, box.MinY + inset);
        path.Add(box.MaxX - inset, box.MaxY - inset);
        path.Add(box.MinX + inset, box.MaxY - inset);
        path.End();

        return path;
    }

    /// <summary>A line across the swatch, which is how a line layer reads at 20 pixels.</summary>
    private static PixelPath Diagonal(PixelBox box, double inset)
    {
        PixelPath path = new();

        path.Begin(closed: false);
        path.Add(box.MinX + inset, box.MaxY - inset);
        path.Add((box.MinX + box.MaxX) / 2.0, box.MinY + inset);
        path.Add(box.MaxX - inset, box.MaxY - inset);
        path.End();

        return path;
    }
}
