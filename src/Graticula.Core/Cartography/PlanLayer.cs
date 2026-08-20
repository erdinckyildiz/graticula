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
    }

}
