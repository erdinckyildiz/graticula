using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// Derives a MapLibre style from a stored CIM renderer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two jobs and one implementation, which is why this is worth writing down.</b> The
/// VectorTileServer face publishes a MapLibre style, and
/// [ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.5 has the
/// renderer read the stored CIM *through this same derivation* rather than through a second
/// compiler. So what a browser is told the layer looks like and what the server paints come from
/// one function, and cannot drift.
/// </para>
/// <para>
/// <b>A symbol's layers become style layers, which is the structural gain.</b> A road authored as
/// a wide casing under a narrow fill is two `CIMSolidStroke`s and becomes two MapLibre `line`
/// layers, which <see cref="SymbologyPlan"/> already compiles and <see cref="MapRenderer"/>
/// already draws in order. Nothing new had to be built for it.
/// </para>
/// <para>
/// <b>A polygon's outline is a `line` layer, not `fill-outline-color`.</b> MapLibre's fill
/// outline is one pixel wide and takes no width, so using it would silently discard every
/// outline width anybody ever set.
/// </para>
/// </remarks>
public static class CimStyle
{
    /// <summary>
    /// Derives the style, and says what it could not carry.
    /// </summary>
    /// <param name="renderer">The stored CIM renderer.</param>
    /// <param name="layerName">What the source layer is called.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <returns>The style and the losses, the projection's own included.</returns>
    public static DerivedStyle ToMapLibre(
        JsonObject renderer, string layerName, GeometryKind geometry)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        CimProjection projection = Cim.Project(renderer, geometry);
        List<string> losses = [.. projection.NotDrawn];

        // <b>The first class decides the shape.</b> Real classified renderers vary colour and
        // width between classes and keep the stack the same; where one does not, the difference
        // is reported rather than guessed at, because a class drawn with somebody else's
        // structure is a map that is wrong without looking wrong.
        IReadOnlyList<CimPaint> shape = projection.Classes[0].Symbol.Paints;

        foreach (CimClass other in projection.Classes.Skip(1))
        {
            if (other.Symbol.Paints.Count != shape.Count
                || other.Symbol.Paints.Zip(shape).Any(p => p.First.GetType() != p.Second.GetType()))
            {
                losses.Add(
                    $"The class '{other.Label}' is built from a different stack of symbol layers "
                    + $"than the first class ('{projection.Classes[0].Label}'). A style layer has "
                    + "one shape for every feature, so this class is drawn with the first class's "
                    + "structure and its own colours. The stored document keeps both.");
            }
        }

        JsonArray layers = [];

        for (int level = 0; level < shape.Count; level++)
        {
            if (Layer(projection, level, layerName, geometry, losses) is { } one)
            {
                layers.Add(one);
            }
        }

        if (layers.Count == 0)
        {
            throw new SymbologyException(
                "The renderer has no symbol layer that becomes a style layer, so there is "
                + "nothing to draw with.");
        }

        JsonObject style = new()
        {
            ["version"] = 8,
            ["layers"] = layers,
        };

        return new DerivedStyle(style, losses);
    }

    /// <summary>One style layer, for one level of the symbol stack.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="level">Which layer of the stack, counting from the bottom.</param>
    /// <param name="layerName">The source layer.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The layer, or null when this level draws nothing.</returns>
    private static JsonObject? Layer(
        CimProjection projection,
        int level,
        string layerName,
        GeometryKind geometry,
        List<string> losses)
    {
        CimPaint first = projection.Classes[0].Symbol.Paints[level];

        JsonObject layer = new()
        {
            ["id"] = string.Create(
                CultureInfo.InvariantCulture, $"{layerName}-{level}"),
            ["source-layer"] = layerName,
        };

        switch (first)
        {
            case CimFill:
                layer["type"] = "fill";
                layer["paint"] = new JsonObject
                {
                    ["fill-color"] = Per(projection, level, Fill, losses),
                    ["fill-opacity"] = Per(projection, level, FillOpacity, losses),
                };

                return layer;

            case CimStroke stroke:
                layer["type"] = "line";

                JsonObject paint = new()
                {
                    ["line-color"] = Per(projection, level, Stroke, losses),
                    ["line-opacity"] = Per(projection, level, StrokeOpacity, losses),
                    ["line-width"] = Per(projection, level, Width, losses),
                };

                // <b>The dash pattern is taken from the first class only.</b> A dash array
                // varying by class is a different line layer, not a different value, and
                // MapLibre has no expression for it.
                if (stroke.Dashes is { Length: > 0 } dashes)
                {
                    paint["line-dasharray"] = new JsonArray(
                        [.. dashes.Select(d => (JsonNode?)JsonValue.Create(d))]);

                    if (projection.Classes.Skip(1).Any(c =>
                            c.Symbol.Paints.ElementAtOrDefault(level) is CimStroke other
                            && !Same(other.Dashes, dashes)))
                    {
                        losses.Add(
                            "The renderer's classes use different dash patterns at the same "
                            + "level. `line-dasharray` takes one value for the whole layer, so "
                            + "the first class's pattern is used for all of them.");
                    }
                }

                layer["paint"] = paint;

                return layer;

            case CimMarker:
                layer["type"] = "circle";
                layer["paint"] = new JsonObject
                {
                    ["circle-color"] = Per(projection, level, Marker, losses),
                    ["circle-opacity"] = Per(projection, level, MarkerOpacity, losses),

                    // <b>Radius, so half the size.</b> CIM's marker size is the width across;
                    // MapLibre's `circle-radius` is from the centre, and getting this wrong
                    // draws every point at twice the size somebody asked for.
                    ["circle-radius"] = Per(projection, level, Radius, losses),
                };

                return layer;

            default:
                losses.Add(
                    $"A symbol layer at level {level} of a {geometry} layer is of a kind this "
                    + "derivation does not write, so it is not in the style.");

                return null;
        }
    }

    /// <summary>The fill colour at a level, as a hex string.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null when this class has nothing there.</returns>
    private static JsonNode? Fill(CimPaint? paint) =>
        paint is CimFill fill ? Hex(fill.Colour) : null;

    /// <summary>The fill opacity at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? FillOpacity(CimPaint? paint) =>
        paint is CimFill fill ? JsonValue.Create(Alpha(fill.Colour)) : null;

    /// <summary>The stroke colour at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Stroke(CimPaint? paint) =>
        paint is CimStroke stroke ? Hex(stroke.Colour) : null;

    /// <summary>The stroke opacity at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? StrokeOpacity(CimPaint? paint) =>
        paint is CimStroke stroke ? JsonValue.Create(Alpha(stroke.Colour)) : null;

    /// <summary>The stroke width at a level, in pixels.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Width(CimPaint? paint) =>
        paint is CimStroke stroke ? JsonValue.Create(Pixels(stroke.Width)) : null;

    /// <summary>The marker colour at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Marker(CimPaint? paint) =>
        paint is CimMarker marker ? Hex(marker.Colour) : null;

    /// <summary>The marker opacity at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? MarkerOpacity(CimPaint? paint) =>
        paint is CimMarker marker ? JsonValue.Create(Alpha(marker.Colour)) : null;

    /// <summary>The marker radius at a level, in pixels.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Radius(CimPaint? paint) =>
        paint is CimMarker marker ? JsonValue.Create(Pixels(marker.Size) / 2) : null;

    /// <summary>
    /// One paint value, constant when every class agrees and an expression when they do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A `match` for values and a `step` for ranges</b>, which is what those two renderers
    /// are. `SymbologyPlan` compiles both already, so this is a translation rather than a new
    /// capability.
    /// </para>
    /// <para>
    /// <b>`step`'s boundary is moved to the next representable double, and that is exact.</b>
    /// Esri's class breaks are upper-bound *inclusive* — a value equal to the break belongs to
    /// the class below — and MapLibre's `step` is lower-bound inclusive. Stepping at
    /// <c>BitIncrement(bound)</c> makes `v &lt; nextAfter(b)` mean exactly `v &lt;= b` for every
    /// double, so no value changes class. Nudging by an arbitrary epsilon would have been a
    /// guess; this is not one.
    /// </para>
    /// </remarks>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="level">Which layer of the stack.</param>
    /// <param name="of">What to read out of one symbol layer.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The paint value.</returns>
    private static JsonNode? Per(
        CimProjection projection,
        int level,
        Func<CimPaint?, JsonNode?> of,
        List<string> losses)
    {
        List<JsonNode?> values = [.. projection.Classes.Select(
            c => of(c.Symbol.Paints.ElementAtOrDefault(level)))];

        JsonNode? fallback = values.FirstOrDefault(v => v is not null);

        // <b>Every class the same, or only one: a constant.</b> An expression over a single
        // value is a slower way to say the same thing and a harder one to read in a style
        // somebody is debugging.
        if (projection.Field is null
            || values.All(v => Same(v, fallback)))
        {
            return fallback?.DeepClone();
        }

        JsonArray expression = projection.Kind == Cim.UniqueValue
            ? Match(projection, values, fallback, losses)
            : Step(projection, values, fallback);

        return expression;
    }

    /// <summary>A `match` over the classified field.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="values">One per class, in order.</param>
    /// <param name="fallback">What a feature no class matches gets.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The expression.</returns>
    private static JsonArray Match(
        CimProjection projection,
        List<JsonNode?> values,
        JsonNode? fallback,
        List<string> losses)
    {
        JsonArray expression = ["match", new JsonArray("get", projection.Field)];
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < projection.Classes.Count; i++)
        {
            foreach (string value in projection.Classes[i].Values)
            {
                // <b>A repeated value is dropped rather than emitted twice.</b> MapLibre
                // refuses a `match` with a duplicate label, so a renderer that lists one value
                // in two classes would produce a style no client would load.
                if (!seen.Add(value))
                {
                    losses.Add(
                        $"The value '{value}' appears in more than one class. A style matches "
                        + "each value once, so the first class that claims it wins.");

                    continue;
                }

                expression.Add(value);
                expression.Add((values[i] ?? fallback)?.DeepClone());
            }
        }

        JsonNode? otherwise = projection.Default is null
            ? fallback
            : null;

        expression.Add(otherwise?.DeepClone() ?? fallback?.DeepClone());

        return expression;
    }

    /// <summary>A `step` over the classified field.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="values">One per class, in order.</param>
    /// <param name="fallback">What a feature above the last break gets.</param>
    /// <returns>The expression.</returns>
    private static JsonArray Step(
        CimProjection projection, List<JsonNode?> values, JsonNode? fallback)
    {
        JsonArray expression =
        [
            "step",
            new JsonArray("get", projection.Field),
            (values[0] ?? fallback)?.DeepClone(),
        ];

        for (int i = 0; i < projection.Classes.Count - 1; i++)
        {
            if (projection.Classes[i].UpperBound is not { } bound)
            {
                continue;
            }

            expression.Add(Math.BitIncrement(bound));
            expression.Add((values[i + 1] ?? fallback)?.DeepClone());
        }

        return expression;
    }

    /// <summary>Whether two nodes say the same thing.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when both are null or their JSON is identical.</returns>
    private static bool Same(JsonNode? left, JsonNode? right) =>
        (left is null && right is null)
        || (left is not null && right is not null
            && string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal));

    /// <summary>Whether two dash templates are the same.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when both are absent or equal element by element.</returns>
    private static bool Same(double[]? left, double[]? right) =>
        (left is null && right is null)
        || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

    /// <summary><c>#rrggbb</c>, with the alpha carried separately.</summary>
    /// <remarks>
    /// <b>Separately because MapLibre keeps them apart.</b> `fill-color` takes a colour and
    /// `fill-opacity` an opacity, and writing `rgba()` into the colour would leave the opacity
    /// property saying something different from the colour beside it.
    /// </remarks>
    /// <param name="colour">The colour.</param>
    /// <returns>The hex string.</returns>
    private static JsonValue Hex(Rgba colour) =>
        JsonValue.Create(string.Create(
            CultureInfo.InvariantCulture, $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}"))!;

    /// <summary>Alpha as MapLibre's 0–1 opacity.</summary>
    /// <param name="colour">The colour.</param>
    /// <returns>The opacity.</returns>
    private static double Alpha(Rgba colour) =>
        Math.Round(colour.A / 255.0, 4, MidpointRounding.AwayFromZero);

    /// <summary>CIM measures in points; a style measures in pixels.</summary>
    /// <param name="points">The width or size.</param>
    /// <returns>Pixels, at the 96-per-inch a style assumes.</returns>
    private static double Pixels(double points) =>
        Math.Round(points / 0.75, 3, MidpointRounding.AwayFromZero);
}

/// <summary>A MapLibre style derived from CIM, and what it could not carry.</summary>
/// <param name="Style">The style document.</param>
/// <param name="Losses">One sentence per thing this face cannot express.</param>
public sealed record DerivedStyle(JsonObject Style, IReadOnlyList<string> Losses);
