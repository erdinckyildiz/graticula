using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>
/// A canonical symbology document, compiled into something a renderer can execute.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first thing in this repository that <em>executes</em>
/// [ADR-033](../../../docs/adr/ADR-033-symbology.md)'s document rather than
/// translating it.</b> Until now the canonical form has been derived into a MapLibre
/// style for one face and an Esri <c>drawingInfo</c> for another, and both
/// derivations were checked by reading them. This one draws, which is why
/// [A-077](../../../docs/architecture-assumptions.md) says the document's fidelity
/// has been asserted since 2026-08-17 and tested by nothing.
/// </para>
/// <para>
/// <b>Compiled once per request, not once per feature.</b> Every paint property
/// becomes a <see cref="StyleExpression"/> here; the render loop only evaluates.
/// </para>
/// <para>
/// <b>What it refuses, and why refusing is the honest answer.</b> A style layer with
/// a <c>filter</c> is refused -- at write time since 2026-08-25, which is Q-128's
/// answer, and here as well for a document stored before that. MapLibre's filter
/// language is a second expression dialect, this server's own converter never emits
/// one, and drawing every feature of a layer whose style says *draw some of them* is
/// a map that is wrong without looking wrong.
/// </para>
/// </remarks>
public sealed class SymbologyPlan
{
    /// <summary>
    /// How far outside the image a symbol can reach, when the style will not say.
    /// </summary>
    /// <remarks>
    /// <b>A floor, not a guess.</b> The margin is computed from the style's own
    /// widths and radii where those are constants; where they are expressions, no
    /// static answer exists, and this is the fallback. Thirty-two pixels covers a
    /// marker, a thick line and a label of ordinary size.
    /// </remarks>
    public const double DefaultMargin = 32;

    private SymbologyPlan(IReadOnlyList<PlanLayer> layers, IReadOnlyList<string> fields, double margin)
    {
        Layers = layers;
        Fields = fields;
        Margin = margin;
    }

    /// <summary>The layers, in the order they are drawn.</summary>
    public IReadOnlyList<PlanLayer> Layers { get; }

    /// <summary>Every attribute column the style reads.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>How far outside the image to query, in pixels.</summary>
    public double Margin { get; }

    /// <summary>Whether any layer of this plan places labels.</summary>
    public bool HasLabels
    {
        get
        {
            foreach (PlanLayer layer in Layers)
            {
                if (layer is PlanLayer.Text)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The one classified axis this plan's legend can show, or empty for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Answered from the style, which is [Q-131](../../../docs/open-questions.md)'s
    /// correction.</b> That row assumed enumerating a classified legend meant reading
    /// the data. Both expressions this server evaluates write their classes out — a
    /// <c>match</c> its labels, a <c>step</c> its breaks — so the answer is in the
    /// document the legend is already compiled from, and costs no query.
    /// </para>
    /// <para>
    /// <b>One axis, and no axis at all when the style classifies on more than one
    /// column.</b> A legend is a strip of rows; two independent classifications are a
    /// grid, and flattening them would draw a row per value of one column with the
    /// other silently at its fallback — a picture that is confidently wrong rather
    /// than absent. When several classifications share a column, the longest wins,
    /// because a legend that omits a class is worse than one that repeats a swatch.
    /// </para>
    /// </remarks>
    /// <returns>The axis, or null when there is not exactly one to draw.</returns>
    public StyleExpression.Classification? LegendClasses()
    {
        List<StyleExpression.Classification> found = [];

        foreach (PlanLayer layer in Layers)
        {
            layer.Classes(found);
        }

        if (found.Count == 0)
        {
            return null;
        }

        StyleExpression.Classification longest = found[0];

        foreach (StyleExpression.Classification each in found)
        {
            if (!string.Equals(each.Field, longest.Field, StringComparison.Ordinal))
            {
                return null;
            }

            if (each.Cases.Count > longest.Cases.Count)
            {
                longest = each;
            }
        }

        // <b>A classification of one class is not one.</b> A `match` with a single
        // label and a fallback draws two rows that mean *this value* and *everything
        // else*, which is worth showing; anything less is the single swatch.
        return longest.Cases.Count > 1 ? longest : null;
    }

    /// <summary>
    /// Compiles a stored canonical document.
    /// </summary>
    /// <param name="document">The canonical symbology document.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="SymbologyException">The document cannot be drawn from.</exception>
    public static SymbologyPlan Compile(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);

        JsonNode root;

        try
        {
            root = JsonNode.Parse(document)
                ?? throw new SymbologyException("The symbology document is null.");
        }
        catch (JsonException e)
        {
            throw new SymbologyException($"The symbology document is not JSON: {e.Message}", e);
        }

        if (root is not JsonObject body)
        {
            throw new SymbologyException("A symbology document is an object.");
        }

        // <b>CIM is compiled through the style derivation, not through a second reader
        // (ADR-052 §3.5).</b> Everything below understands MapLibre paint expressions and is
        // the most tested code in this area; a CIM front end here would be a second
        // implementation of the same reading, and the two would drift. It also means the
        // picture this renderer paints and the style the tile face publishes come from one
        // function, so *what is drawn* and *what is advertised* cannot disagree.
        if (Cim.IsRenderer(body))
        {
            // The name is not read: `CompileLayer` looks at `type` and `paint` only. It is
            // written here so the derived document is well formed rather than nearly so.
            body = CimStyle.ToMapLibre(body, "layer").Style;
        }

        if (body["layers"] is not JsonArray layers)
        {
            throw new SymbologyException(
                "A stored symbology document is either a CIM renderer or a MapLibre style with "
                + "a `layers` array, and this is neither.");
        }

        List<PlanLayer> plan = [];
        HashSet<string> fields = new(StringComparer.Ordinal);
        double margin = 0;

        foreach (JsonNode? node in layers)
        {
            if (node is not JsonObject layer)
            {
                continue;
            }

            // <b>The second line, not the first, since 2026-08-25.</b> Q-128 moved
            // the refusal to the write path, where the author is reading. This one
            // stays because a stored document can predate that change or be edited
            // in the database directly, and a renderer that quietly drew every
            // feature of a filtered layer would produce a map that is wrong without
            // looking wrong -- which is the whole reason either refusal exists.
            if (layer["filter"] is not null)
            {
                throw new SymbologyException(
                    "A stored style layer carries a `filter`, and this server does not evaluate "
                    + "filters. Drawing every feature of a layer whose style says to draw some of "
                    + "them is a map that is wrong without looking wrong, so it is refused "
                    + "instead. This document was stored before filters were refused at write "
                    + "time: PUT it again without the filter, expressing the distinction in the "
                    + "paint with `match` or `step`.");
            }

            PlanLayer? compiled = CompileLayer(layer, ref margin);

            if (compiled is null)
            {
                continue;
            }

            compiled.Fields(fields);
            plan.Add(compiled);
        }

        if (plan.Count == 0)
        {
            throw new SymbologyException(
                "None of the document's layers is one this server can draw with.");
        }

        return new SymbologyPlan(plan, [.. fields], Math.Max(margin, 4));
    }

    /// <summary>
    /// The plan for a layer nobody has styled.
    /// </summary>
    /// <remarks>
    /// <b>Built from <see cref="GeneratedSymbology"/> rather than invented here.</b>
    /// ADR-033 §5b made that the one place a default appearance is decided, after
    /// three places had each decided differently; a fourth default living in the
    /// renderer would be the same defect with a new name.
    /// </remarks>
    /// <param name="layerName">The layer's name, which decides its colour.</param>
    /// <param name="geometry">What shape its features are.</param>
    /// <returns>The plan.</returns>
    public static SymbologyPlan Default(string layerName, GeometryKind geometry)
    {
        Appearance appearance = GeneratedSymbology.For(layerName, geometry);

        // <b>Asserted rather than defaulted.</b> These colours come from
        // GeneratedSymbology's own palette, which is a compiled-in array of
        // `#rrggbb` literals — a failure here is that array being edited into
        // something unreadable, and a map silently drawn in transparent black is
        // the worst possible way to learn that.
        if (!Rgba.TryParse(appearance.Colour, out Rgba colour))
        {
            throw new SymbologyException(
                $"The generated appearance for '{layerName}' has an unreadable colour "
                + $"'{appearance.Colour}'. That palette is compiled in, so this is a defect in "
                + "GeneratedSymbology rather than in any stored style.");
        }

        if (!Rgba.TryParse(appearance.Outline, out Rgba outline))
        {
            outline = Rgba.Transparent;
        }

        colour = colour.WithOpacity(appearance.Opacity);

        PlanLayer layer = appearance.Kind switch
        {
            AppearanceKind.Marker => new PlanLayer.Point(
                Constant(colour),
                Constant(appearance.Size),
                Constant(outline),
                Constant(appearance.OutlineWidth)),

            AppearanceKind.Line => new PlanLayer.Line(
                Constant(colour), Constant(appearance.Size), null),

            _ => new PlanLayer.Fill(
                Constant(colour), Constant(outline), Constant(appearance.OutlineWidth)),
        };

        return new SymbologyPlan([layer], [], DefaultMargin);
    }

    private static StyleExpression Constant(Rgba colour) =>
        StyleExpression.Compile(JsonValue.Create(colour.ToString()));

    private static StyleExpression Constant(double value) =>
        StyleExpression.Compile(JsonValue.Create(value));

    private static PlanLayer? CompileLayer(JsonObject layer, ref double margin)
    {
        string type = layer["type"]?.GetValue<string>() ?? string.Empty;

        JsonObject paint = layer["paint"] as JsonObject ?? [];
        JsonObject layout = layer["layout"] as JsonObject ?? [];

        (double? minimum, double? maximum) = Zooms(layer);

        switch (type)
        {
            case "fill":
                Widen(ref margin, paint["fill-outline-color"] is null ? 0 : 2);

                return new PlanLayer.Fill(
                    Opacity(paint, "fill-color", "fill-opacity"),
                    StyleExpression.Compile(paint["fill-outline-color"]),
                    Constant(1))
                {
                    MinimumZoom = minimum,
                    MaximumZoom = maximum,
                };

            case "line":
                Widen(ref margin, Static(paint["line-width"]) ?? 2);

                return new PlanLayer.Line(
                    Opacity(paint, "line-color", "line-opacity"),
                    paint["line-width"] is { } width
                        ? StyleExpression.Compile(width)
                        : Constant(1),
                    paint["line-dasharray"] is { } dash ? StyleExpression.Compile(dash) : null)
                {
                    MinimumZoom = minimum,
                    MaximumZoom = maximum,
                };

            case "circle":
                Widen(
                    ref margin,
                    (Static(paint["circle-radius"]) ?? 5) + (Static(paint["circle-stroke-width"]) ?? 0));

                return new PlanLayer.Point(
                    Opacity(paint, "circle-color", "circle-opacity"),
                    paint["circle-radius"] is { } radius
                        ? StyleExpression.Compile(radius)
                        : Constant(5),
                    StyleExpression.Compile(paint["circle-stroke-color"]),
                    paint["circle-stroke-width"] is { } stroke
                        ? StyleExpression.Compile(stroke)
                        : Constant(0))
                {
                    MinimumZoom = minimum,
                    MaximumZoom = maximum,
                };

            case "symbol":
                {
                    JsonNode? field = layout["text-field"];

                    if (field is null)
                    {
                        // A symbol layer with no text is an icon layer, and this
                        // server stores no sprites. Skipped rather than refused:
                        // the rest of the style still draws.
                        return null;
                    }

                    Widen(ref margin, (Static(layout["text-size"]) ?? 12) * 4);

                    return new PlanLayer.Text(
                        StyleExpression.Compile(field),
                        StyleExpression.Compile(paint["text-color"] ?? JsonValue.Create("#333333")),
                        layout["text-size"] is { } size
                            ? StyleExpression.Compile(size)
                            : Constant(12),
                        StyleExpression.Compile(
                            paint["text-halo-color"] ?? JsonValue.Create("#ffffff")),
                        paint["text-halo-width"] is { } halo
                            ? StyleExpression.Compile(halo)
                            : Constant(1))
                    {
                        MinimumZoom = minimum,
                        MaximumZoom = maximum,
                    };
                }

            case "heatmap":
            {
                // <b>The radius widens the margin, and by more than it looks.</b> A point half a
                // radius outside the image still lights pixels inside it, so the reader has to
                // fetch features beyond the extent it draws or every tile boundary gets a seam.
                double spreads = Static(paint["heatmap-radius"]) ?? 30;

                Widen(ref margin, spreads);

                if (Stops(paint["heatmap-color"]) is not { Count: > 1 } ramp)
                {
                    // <b>Skipped rather than refused, like a missing sprite above.</b> A heat map
                    // with no readable ramp has nothing to paint with, and the rest of a style
                    // that carries one still draws.
                    return null;
                }

                return new PlanLayer.Heat(
                    paint["heatmap-weight"] is { } weight
                        ? StyleExpression.Compile(weight)
                        : null,
                    paint["heatmap-radius"] is { } spread
                        ? StyleExpression.Compile(spread)
                        : Constant(spreads),
                    ramp,
                    Static(paint["heatmap-intensity"]) is { } intensity && intensity > 0
                        ? 1 / intensity
                        : null,
                    Static(paint["heatmap-opacity"]) ?? 1)
                {
                    MinimumZoom = minimum,
                    MaximumZoom = maximum,
                };
            }

            default:
                return null;
        }
    }

    /// <summary>The colours of an interpolate over the heat map's own density.</summary>
    /// <remarks>
    /// <b>Read as stops rather than compiled as an expression, and it has to be.</b>
    /// `["interpolate", ["linear"], ["heatmap-density"], 0, c0, 1, c1]` interpolates over a value
    /// that is neither a field nor the zoom: it is the surface's own density at a pixel, which
    /// does not exist until every feature has been read. An expression compiled from it could
    /// never be evaluated, because there is nothing to evaluate it against.
    /// </remarks>
    /// <param name="node">The `heatmap-color` value.</param>
    /// <returns>Its colours in order, or null when it is not that shape.</returns>
    private static List<Rgba>? Stops(JsonNode? node)
    {
        if (node is not JsonArray expression
            || expression.Count < 5
            || expression[0]?.GetValue<string>() != "interpolate")
        {
            return null;
        }

        List<Rgba> ramp = [];

        // ["interpolate", ["linear"], ["heatmap-density"], stop, colour, stop, colour, ...]
        for (int i = 4; i < expression.Count; i += 2)
        {
            if (expression[i] is not JsonValue value
                || !value.TryGetValue(out string? text)
                || !Rgba.TryParse(text, out Rgba colour))
            {
                return null;
            }

            ramp.Add(colour);
        }

        return ramp.Count > 1 ? ramp : null;
    }

    /// <summary>A paint colour with its opacity folded in, when both are constants.</summary>
    /// <remarks>
    /// <b>Folded here when it can be, and at draw time when it cannot.</b> Most
    /// styles set both to constants, and multiplying once per request beats
    /// multiplying once per feature; a style that computes either keeps both
    /// expressions and pays the per-feature cost it asked for.
    /// </remarks>
    private static StyleExpression Opacity(JsonObject paint, string colour, string opacity)
    {
        StyleExpression expression = StyleExpression.Compile(paint[colour]);

        if (paint[opacity] is not { } node)
        {
            return expression;
        }

        double? fraction = Static(node);

        if (fraction is null)
        {
            return StyleExpression.Fade(expression, StyleExpression.Compile(node));
        }

        // <b>Folded only when the colour is a literal, and this guard is the whole fix.</b>
        // The line below evaluates the colour with no feature and no zoom. For a literal that
        // is the colour; for `["match", ["get", "kind"], …]` it is the *fallback*, and keeping
        // it as a constant draws every feature in the fallback colour and empties the legend.
        //
        // Measured 2026-09-03 on a layer classified by `kind` with `fill-opacity` beside it:
        // two classes and an *Other* became one grey swatch, and the map matched the legend --
        // consistently, quietly, and wrongly. It was unreachable while stored styles rarely
        // carried an opacity; ADR-052's derivation writes one every time, which is how it
        // surfaced.
        if (expression.Varies)
        {
            return StyleExpression.Fade(expression, StyleExpression.Compile(node));
        }

        StyleExpression.Context empty = new(
            new Dictionary<string, object?>(StringComparer.Ordinal), 0);

        return Rgba.TryParse(StyleExpression.Text(expression.Evaluate(empty)), out Rgba parsed)
            ? Constant(parsed.WithOpacity(fraction.Value))
            : StyleExpression.Fade(expression, StyleExpression.Compile(node));
    }

    /// <summary>A node's value when it is a plain number, or null when it is an expression.</summary>
    private static double? Static(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static void Widen(ref double margin, double candidate) =>
        margin = Math.Max(margin, candidate);

    private static (double? Minimum, double? Maximum) Zooms(JsonObject layer) =>
        (Static(layer["minzoom"]), Static(layer["maxzoom"]));
}
