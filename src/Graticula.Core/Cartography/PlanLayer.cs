using System;
using System.Collections.Generic;

namespace Graticula.Cartography;

/// <summary>
/// One style layer, compiled: what it draws and how it decides.
/// </summary>
/// <remarks>
/// <b>Four kinds, matching the four
/// [ADR-033](../../../docs/adr/ADR-033-symbology.md) stores.</b> Each resolves to a
/// <see cref="MapSymbol"/> for one feature, or to null when the feature should not
/// be painted by this layer at all — an invisible colour, a zero width, a zoom the
/// layer is switched off at. Null is a real answer here and the renderer skips it;
/// drawing a fully transparent symbol costs the same as drawing a visible one.
/// </remarks>
public abstract class PlanLayer
{
    private PlanLayer()
    {
    }

    /// <summary>Below this zoom the layer is not drawn, or null for always.</summary>
    public double? MinimumZoom { get; init; }

    /// <summary>At and above this zoom the layer is not drawn, or null for always.</summary>
    public double? MaximumZoom { get; init; }

    /// <summary>Whether this layer draws at all at a given zoom.</summary>
    /// <remarks>
    /// <b>Minimum is inclusive and maximum is exclusive</b>, which is what a
    /// MapLibre style means and is not symmetric. A layer with
    /// <c>minzoom: 5, maxzoom: 10</c> draws at 5 and does not draw at 10.
    /// </remarks>
    /// <param name="zoom">The zoom the map's resolution corresponds to.</param>
    /// <returns>Whether to draw it.</returns>
    public bool DrawsAt(double zoom) =>
        (MinimumZoom is not { } minimum || zoom >= minimum)
        && (MaximumZoom is not { } maximum || zoom < maximum);

    /// <summary>Resolves this layer for one feature.</summary>
    /// <param name="context">The feature and the map.</param>
    /// <returns>The symbol, or null to draw nothing.</returns>
    public abstract MapSymbol? Resolve(in StyleExpression.Context context);

    /// <summary>Every attribute column this layer reads.</summary>
    /// <param name="into">Where to add them.</param>
    public abstract void Fields(ISet<string> into);

    /// <summary>Every classification this layer's expressions make.</summary>
    /// <remarks>
    /// <b>The same walk as <see cref="Fields"/>, over the same expressions.</b> A
    /// legend that classifies needs the classes, and they are in the style — see
    /// <see cref="StyleExpression.Classification"/> for why that is worth saying.
    /// </remarks>
    /// <param name="into">Where to add them.</param>
    public abstract void Classes(ICollection<StyleExpression.Classification> into);

    /// <summary>Reads a colour, treating anything unreadable as invisible.</summary>
    /// <remarks>
    /// <b>Invisible rather than black.</b> A style whose colour expression produced
    /// nothing for this feature has said nothing about it, and painting it black
    /// would put a mark on the map that no data supports. Painting nothing is
    /// visibly absent, which is a thing somebody can notice and fix.
    /// </remarks>
    private protected static Rgba Colour(StyleExpression? expression, in StyleExpression.Context context)
    {
        if (expression is null)
        {
            return Rgba.Transparent;
        }

        return Rgba.TryParse(StyleExpression.Text(expression.Evaluate(context)), out Rgba colour)
            ? colour
            : Rgba.Transparent;
    }

    private protected static double Number(
        StyleExpression? expression, in StyleExpression.Context context, double fallback) =>
        expression is null
            ? fallback
            : StyleExpression.AsNumber(expression.Evaluate(context)) ?? fallback;

    /// <summary>A filled area.</summary>
    public sealed class Fill(
        StyleExpression colour, StyleExpression? outline, StyleExpression? outlineWidth) : PlanLayer
    {
        /// <inheritdoc/>
        public override MapSymbol? Resolve(in StyleExpression.Context context)
        {
            Rgba fill = Colour(colour, context);
            Rgba edge = Colour(outline, context);

            if (fill.IsInvisible && edge.IsInvisible)
            {
                return null;
            }

            return new MapSymbol.Area(fill, edge, Number(outlineWidth, context, 1));
        }

        /// <inheritdoc/>
        public override void Fields(ISet<string> into)
        {
            colour.Fields(into);
            outline?.Fields(into);
            outlineWidth?.Fields(into);
        }

        /// <inheritdoc/>
        public override void Classes(ICollection<StyleExpression.Classification> into)
        {
            colour.Classes(into);
            outline?.Classes(into);
            outlineWidth?.Classes(into);
        }
    }

    /// <summary>A stroked line.</summary>
    public sealed class Line(
        StyleExpression colour, StyleExpression width, StyleExpression? dash) : PlanLayer
    {
        /// <inheritdoc/>
        public override MapSymbol? Resolve(in StyleExpression.Context context)
        {
            Rgba stroke = Colour(colour, context);
            double thickness = Number(width, context, 1);

            if (stroke.IsInvisible || thickness <= 0)
            {
                return null;
            }

            return new MapSymbol.Stroke(stroke, thickness, Dashes(context));
        }

        /// <inheritdoc/>
        public override void Fields(ISet<string> into)
        {
            colour.Fields(into);
            width.Fields(into);
            dash?.Fields(into);
        }

        /// <inheritdoc/>
        public override void Classes(ICollection<StyleExpression.Classification> into)
        {
            colour.Classes(into);
            width.Classes(into);
            dash?.Classes(into);
        }

        /// <summary>
        /// The dash pattern, in pixels.
        /// </summary>
        /// <remarks>
        /// <b>MapLibre writes dash lengths in line widths, not pixels.</b>
        /// <c>line-dasharray: [2, 4]</c> on a 3-pixel line means six pixels on and
        /// twelve off. Reading them as pixels gives a pattern that is right only for
        /// a one-pixel line, which is exactly the case an author tests with.
        /// </remarks>
        private List<double>? Dashes(in StyleExpression.Context context)
        {
            if (dash?.Evaluate(context) is not object?[] values || values.Length == 0)
            {
                return null;
            }

            double thickness = Number(width, context, 1);
            List<double> lengths = new(values.Length);

            foreach (object? value in values)
            {
                double length = StyleExpression.AsNumber(value) ?? 0;

                if (length <= 0)
                {
                    return null;
                }

                lengths.Add(length * thickness);
            }

            return lengths;
        }
    }

    /// <summary>A circular marker.</summary>
    public sealed class Point(
        StyleExpression colour,
        StyleExpression radius,
        StyleExpression? outline,
        StyleExpression? outlineWidth) : PlanLayer
    {
        /// <inheritdoc/>
        public override MapSymbol? Resolve(in StyleExpression.Context context)
        {
            Rgba fill = Colour(colour, context);
            Rgba edge = Colour(outline, context);
            double size = Number(radius, context, 5);

            if (size <= 0 || (fill.IsInvisible && edge.IsInvisible))
            {
                return null;
            }

            return new MapSymbol.Marker(fill, size, edge, Number(outlineWidth, context, 0));
        }

        /// <inheritdoc/>
        public override void Fields(ISet<string> into)
        {
            colour.Fields(into);
            radius.Fields(into);
            outline?.Fields(into);
            outlineWidth?.Fields(into);
        }

        /// <inheritdoc/>
        public override void Classes(ICollection<StyleExpression.Classification> into)
        {
            colour.Classes(into);
            radius.Classes(into);
            outline?.Classes(into);
            outlineWidth?.Classes(into);
        }
    }

    /// <summary>A density surface over points.</summary>
    /// <remarks>
    /// <para>
    /// <b>The one layer that does not resolve to a symbol.</b> Every other kind answers *what
    /// does this feature look like*; a heat map has no answer to that, because a pixel's colour
    /// depends on every point near it. So <see cref="Resolve"/> returns null — meaning *paint
    /// nothing for this feature on its own* — and <see cref="MapRenderer"/> recognises the type
    /// and accumulates instead. ADR-052 §3.14.
    /// </para>
    /// <para>
    /// <b>The ramp is a list rather than an expression.</b> MapLibre writes `heatmap-color` as an
    /// interpolate over `["heatmap-density"]`, which is not a field and not a zoom: it is the
    /// surface's own value, which does not exist until every feature has been read. Compiling it
    /// to an expression would produce one that cannot be evaluated per feature, so it is read
    /// into its stops at compile time.
    /// </para>
    /// </remarks>
    /// <param name="weight">What one feature counts for, or null for one each.</param>
    /// <param name="radius">How far its heat spreads, in pixels.</param>
    /// <param name="ramp">Two or more colours, coolest first.</param>
    /// <param name="ceiling">The density that reaches the ramp's end, or null to use the peak.</param>
    /// <param name="opacity">How opaque the finished surface is.</param>
    public sealed class Heat(
        StyleExpression? weight,
        StyleExpression radius,
        IReadOnlyList<Rgba> ramp,
        double? ceiling,
        double opacity) : PlanLayer
    {
        /// <summary>Two or more colours, coolest first.</summary>
        public IReadOnlyList<Rgba> Ramp { get; } = ramp;

        /// <summary>The density that reaches the ramp's end, or null to use the peak.</summary>
        public double? Ceiling { get; } = ceiling;

        /// <summary>How opaque the finished surface is.</summary>
        public double Opacity { get; } = opacity;

        /// <summary>What one feature counts for, at this feature.</summary>
        /// <param name="context">The feature and the map.</param>
        /// <returns>Its weight, one when the style names none.</returns>
        public double WeightOf(in StyleExpression.Context context) =>
            Number(weight, context, 1);

        /// <summary>How far this feature's heat spreads, in pixels.</summary>
        /// <param name="context">The feature and the map.</param>
        /// <returns>The radius.</returns>
        public double RadiusOf(in StyleExpression.Context context) =>
            Number(radius, context, 30);

        /// <inheritdoc/>
        public override MapSymbol? Resolve(in StyleExpression.Context context) => null;

        /// <inheritdoc/>
        public override void Fields(ISet<string> into)
        {
            weight?.Fields(into);
            radius.Fields(into);
        }

        /// <inheritdoc/>
        public override void Classes(ICollection<StyleExpression.Classification> into)
        {
            // <b>Nothing.</b> A heat map has no classes: its legend is a continuous scale from
            // *few* to *many*, and offering the ramp's stops as classes would put numbers in a
            // legend that stand for nothing anybody can count.
        }
    }

    /// <summary>A label.</summary>
    public sealed class Text(
        StyleExpression field,
        StyleExpression colour,
        StyleExpression size,
        StyleExpression? haloColour,
        StyleExpression? haloWidth) : PlanLayer
    {
        /// <inheritdoc/>
        public override MapSymbol? Resolve(in StyleExpression.Context context)
        {
            Rgba ink = Colour(colour, context);
            double em = Number(size, context, 12);

            if (ink.IsInvisible || em <= 0)
            {
                return null;
            }

            return new MapSymbol.Label(
                ink, em, Colour(haloColour, context), Number(haloWidth, context, 0));
        }

        /// <summary>The text this layer would put on a feature, or null for none.</summary>
        /// <param name="context">The feature and the map.</param>
        /// <returns>The text.</returns>
        public string? TextOf(in StyleExpression.Context context)
        {
            string? text = StyleExpression.Text(field.Evaluate(context));

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        /// <inheritdoc/>
        public override void Fields(ISet<string> into)
        {
            field.Fields(into);
            colour.Fields(into);
            size.Fields(into);
            haloColour?.Fields(into);
            haloWidth?.Fields(into);
        }

        /// <summary>Nothing: a label layer draws no swatch, so it classifies none.</summary>
        /// <remarks>
        /// <b>Deliberately empty rather than inherited.</b> A label layer's colour can
        /// carry a `match` — halo one colour for one class and another for the rest —
        /// and a legend built from it would show rows for classes whose *swatches* are
        /// identical, because a legend swatch has no text on it to colour.
        /// </remarks>
        /// <param name="into">Unused.</param>
        public override void Classes(ICollection<StyleExpression.Classification> into)
        {
        }
    }

}
