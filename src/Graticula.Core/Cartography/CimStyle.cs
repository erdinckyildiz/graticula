using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
/// <para>
/// <b>Two forms of one derivation since 2026-09-29, and only one of them is published.</b>
/// <see cref="ToExpressions"/> classifies inside the paint — a `match`, a `step`, an
/// `interpolate` over a column — and is what <see cref="SymbologyPlan"/> compiles.
/// <see cref="ToMapLibre"/> is what a client is handed: the same style with every expression
/// over a feature's attribute spread into one style layer per class, each with a legacy
/// `filter` and constant paint, because ArcGIS Pro draws nothing for a `match` in a style
/// (measured by the owner on 2026-09-29, D-280). The published form is computed <i>from</i> the
/// expression form by evaluating it, so the two cannot say different things about a class.
/// </para>
/// </remarks>
public static partial class CimStyle
{
    /// <summary>
    /// Derives the style a client is handed, and says what it could not carry.
    /// </summary>
    /// <remarks>
    /// <b>One style layer per class, not an expression per property.</b> See
    /// <see cref="PerClass"/> for the shapes and the cap past which the expression form is
    /// published instead.
    /// </remarks>
    /// <param name="renderer">The stored CIM renderer.</param>
    /// <param name="layerName">What the source layer is called.</param>
    /// <returns>The style and the losses, the projection's own included.</returns>
    public static DerivedStyle ToMapLibre(JsonObject renderer, string layerName)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        CimProjection projection = Cim.Project(renderer);

        return PerClass(projection, Expressions(projection, layerName));
    }

    /// <summary>
    /// Derives the style with its classes as paint expressions, which is what this server's own
    /// renderer compiles.
    /// </summary>
    /// <remarks>
    /// <b>Not what a client is handed.</b> ArcGIS Pro does not draw a `match` over a feature's
    /// attribute, so <see cref="ToMapLibre"/> publishes the per-class form derived from this one.
    /// <see cref="SymbologyPlan"/> keeps compiling this form because it evaluates expressions
    /// and not filters, and its legend reads the classes out of the `match` and the `step`.
    /// </remarks>
    /// <param name="renderer">The stored CIM renderer.</param>
    /// <param name="layerName">What the source layer is called.</param>
    /// <returns>The style and the losses, the projection's own included.</returns>
    public static DerivedStyle ToExpressions(JsonObject renderer, string layerName)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        return Expressions(Cim.Project(renderer), layerName);
    }

    /// <summary>The expression form, from a renderer already projected.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="layerName">What the source layer is called.</param>
    /// <returns>The style and the losses.</returns>
    private static DerivedStyle Expressions(CimProjection projection, string layerName)
    {
        // <b>A heat map is a layer type rather than a paint on one.</b> MapLibre has `heatmap`
        // with its own five properties and no `paint` this style builder would recognise, so it
        // is built here and returns before the per-geometry machinery below ever runs.
        // ADR-052 §3.14.
        if (projection.Heat is { } surface)
        {
            return Surface(projection, surface, layerName);
        }

        // <b>MapLibre has no dot-density layer, so this face cannot carry the renderer at
        // all.</b> What it publishes instead is a flat fill in the first field's colour and a
        // sentence saying so. Inventing a layer type would produce a style that this server
        // reads and every other client rejects; publishing nothing would make the layer
        // invisible on the tile face with no explanation anywhere.
        if (projection.Dots is { } scattered)
        {
            return Scattered(projection, scattered, layerName);
        }

        // Nor a chart layer. Same treatment, same reason.
        if (projection.Pie is { } chart)
        {
            return Charted(projection, chart, layerName);
        }
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
            if (Layer(projection, level, layerName, losses) is { } one)
            {
                Vary(one, projection, losses, shape[level] is CimPicture icon ? icon.Picture.SheetHeight : 1);
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

    /// <summary>
    /// Slides a style layer's paint with the renderer's visual variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The variable wins where it and the classes want the same property.</b> That is Esri's
    /// own precedence and it is the only one that makes sense: somebody who asked for colour to
    /// follow a number asked for it to stop following the class. It is reported, because a class
    /// list whose colours are not what the map draws is confusing precisely because both look
    /// deliberate.
    /// </para>
    /// <para>
    /// <b>Nothing new had to be built to draw this.</b> `SymbologyPlan` has compiled
    /// `interpolate` since ADR-041 and evaluates its input per feature, so a continuous colour
    /// is a style expression this renderer already executes. Measured by reading
    /// `Interpolate.Evaluate` rather than assumed: it takes the value from the feature's own
    /// context, not from the zoom.
    /// </para>
    /// </remarks>
    /// <param name="layer">The style layer, already painted from its classes.</param>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <param name="iconHeight">
    /// For a picture's `symbol` layer, the first class's icon height in the sprite sheet, which a
    /// size variable's `icon-size` is a ratio to; 1 for every other layer.
    /// </param>
    private static void Vary(
        JsonObject layer, CimProjection projection, List<string> losses, double iconHeight)
    {
        if (projection.Vary.Count == 0)
        {
            return;
        }

        string kind = (string?)layer["type"] ?? string.Empty;
        JsonObject paint = layer["paint"] as JsonObject ?? [];
        JsonObject layout = layer["layout"] as JsonObject ?? [];

        foreach (CimVary variable in projection.Vary)
        {
            string? property = (variable.What, kind) switch
            {
                (CimVaries.Colour, "fill") => "fill-color",
                (CimVaries.Colour, "line") => "line-color",
                (CimVaries.Colour, "circle") => "circle-color",

                (CimVaries.Opacity, "fill") => "fill-opacity",
                (CimVaries.Opacity, "line") => "line-opacity",
                (CimVaries.Opacity, "circle") => "circle-opacity",
                (CimVaries.Opacity, "symbol") => "icon-opacity",

                // <b>A fill has no size.</b> Saying so beats widening its outline instead,
                // which is what a renderer guessing here would do.
                (CimVaries.Size, "line") => "line-width",
                (CimVaries.Size, "circle") => "circle-radius",

                // <b>A picture grows by `icon-size`, which is a ratio rather than a length</b> —
                // ADR-099. `Sliding` divides by the icon's height in the sheet.
                (CimVaries.Size, "symbol") => "icon-size",

                _ => null,
            };

            if (property is null && variable.What == CimVaries.Colour && kind == "symbol")
            {
                losses.Add(
                    $"The renderer varies colour by `{variable.Field}`, and a picture marker is drawn "
                    + "in the picture's own colours. That variable is kept in the stored document and "
                    + "changes nothing on this layer.");

                continue;
            }

            if (property is null)
            {
                if (variable.What == CimVaries.Size && kind == "fill")
                {
                    losses.Add(
                        $"The renderer varies size by `{variable.Field}`, and a fill has no size. "
                        + "That variable is kept in the stored document and changes nothing on "
                        + "this layer.");
                }

                continue;
            }

            // <b>`icon-size` is a layout property in the style specification, not a paint one</b>, so
            // it is written where a client reads it.
            JsonObject block = property == "icon-size" ? layout : paint;

            if (block[property] is JsonArray existing
                && (existing.ElementAtOrDefault(0) as JsonValue)?.ToString() is "match" or "step")
            {
                losses.Add(
                    $"`{property}` is set both by the renderer's classes and by a visual variable "
                    + $"over `{variable.Field}`. The variable wins, which is what asking for a "
                    + "continuous value means; the class colours are still in the legend.");
            }

            block[property] = Sliding(variable, property, iconHeight);
        }

        layer["paint"] = paint;

        if (layout.Count > 0 && layer["layout"] is null)
        {
            layer["layout"] = layout;
        }
    }

    /// <summary>One paint value as an `interpolate` over the variable's field.</summary>
    /// <remarks>
    /// <b>Sorted, because `interpolate` needs ascending stops and CIM does not promise them.</b>
    /// Esri writes a size variable's `dataValues` descending often enough that taking the array
    /// as given produces a style no client will load.
    /// </remarks>
    /// <param name="variable">What varies.</param>
    /// <param name="property">The property being written, so radius can halve.</param>
    /// <param name="iconHeight">What an `icon-size` is a ratio to, in pixels.</param>
    /// <returns>The expression.</returns>
    private static JsonArray Sliding(CimVary variable, string property, double iconHeight = 1)
    {
        List<(double Stop, JsonNode? Output)> pairs = [];

        for (int i = 0; i < variable.Stops.Count; i++)
        {
            JsonNode? output = variable.What switch
            {
                CimVaries.Colour => i < variable.Colours.Count ? Hex(variable.Colours[i]) : null,

                // <b>Rounded once, after the halving.</b> Rounding to three places and then
                // dividing leaves a number rounded to half a place -- 2.6665 where 2.667 was
                // meant -- which is the kind of value that makes a document look wrong.
                CimVaries.Size => i < variable.Numbers.Count
                    ? Num(Math.Round(
                        property switch
                        {
                            "circle-radius" => variable.Numbers[i] / 0.75 / 2,
                            "icon-size" => variable.Numbers[i] / 0.75 / iconHeight,
                            _ => variable.Numbers[i] / 0.75,
                        },
                        property == "icon-size" ? 4 : 3,
                        MidpointRounding.AwayFromZero))
                    : null,

                _ => i < variable.Numbers.Count
                    ? Num(Math.Round(variable.Numbers[i], 4, MidpointRounding.AwayFromZero))
                    : null,
            };

            if (output is not null)
            {
                pairs.Add((variable.Stops[i], output));
            }
        }

        pairs.Sort((a, b) => a.Stop.CompareTo(b.Stop));

        JsonArray expression =
        [
            "interpolate",
            new JsonArray("linear"),
            new JsonArray("get", variable.Field),
        ];

        double last = double.NegativeInfinity;

        foreach ((double stop, JsonNode? output) in pairs)
        {
            // <b>Two stops at the same value is a style no client loads.</b> Nudging the second
            // one up by the smallest representable amount keeps both colours and keeps the
            // document valid, which is better than dropping one of them silently.
            double at = stop <= last ? Math.BitIncrement(last) : stop;

            expression.Add(Num(at));
            expression.Add(output);

            last = at;
        }

        return expression;
    }

    /// <summary>One style layer, for one level of the symbol stack.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="level">Which layer of the stack, counting from the bottom.</param>
    /// <param name="layerName">The source layer.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The layer, or null when this level draws nothing.</returns>
    private static JsonObject? Layer(
        CimProjection projection,
        int level,
        string layerName,
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
                        [.. dashes.Select(d => (JsonNode?)Num(d))]);

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

            case CimPicture:
                return Icon(projection, level, layer, losses);

            default:
                losses.Add(
                    $"A symbol layer at level {level} is of a kind this derivation does not "
                    + "write, so it is not in the style.");

                return null;
        }
    }

    /// <summary>A picture marker's level, as a `symbol` layer drawing an icon from the sprite sheet.</summary>
    /// <remarks>
    /// <para>
    /// <b>ADR-099: the shape Esri's own vector styles draw icons with.</b> A `symbol` layer with a
    /// literal `icon-image` per class, cut from the service's sprite sheet, is what ArcGIS Pro draws
    /// in its own basemaps; a picture drawn any other way — a `circle` with a pattern, an image
    /// source — is one it does not. The icon's name is the picture's content hash, so the sheet the
    /// sprite routes generate carries every name this style asks for (<see cref="MarkerPicture.Name"/>).
    /// </para>
    /// <para>
    /// <b>Sizes are ratios to the icon's height in the sheet.</b> `icon-size` scales the icon's own
    /// pixels; the sheet holds each picture at <see cref="MarkerPicture.SheetHeight"/>, so a marker of
    /// <i>s</i> points is <c>s / 0.75 / SheetHeight</c>. `icon-offset` is in the icon's pixels
    /// <i>before</i> that scaling, per the style specification, so the marker's offset in points is
    /// divided by the same ratio — and its <i>y</i> is turned over, because CIM's offset is upward and
    /// the style's is downward. `icon-rotate` is clockwise and CIM's rotation is not.
    /// </para>
    /// <para>
    /// <b>Every point is drawn</b>: `icon-allow-overlap` and `icon-ignore-placement`, because ArcGIS
    /// draws every feature of a point layer, and a style whose icons thin out as the reader zooms
    /// out would be a different map from the one the renderer describes.
    /// </para>
    /// </remarks>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="level">Which layer of the stack.</param>
    /// <param name="layer">The style layer, with its id and source already written.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The layer.</returns>
    private static JsonObject Icon(
        CimProjection projection, int level, JsonObject layer, List<string> losses)
    {
        layer["type"] = "symbol";

        JsonObject layout = new()
        {
            ["icon-image"] = Per(projection, level, IconName, losses),
            ["icon-size"] = Per(projection, level, IconSize, losses),
        };

        IEnumerable<CimPicture> pictures = projection.Classes
            .Select(c => c.Symbol.Paints.ElementAtOrDefault(level))
            .Append(projection.Default?.Paints.ElementAtOrDefault(level))
            .OfType<CimPicture>();

        // <b>Written only when some class needs it</b>, so a plain picture's style says nothing it
        // does not mean.
        if (pictures.Any(p => p.Rotation != 0))
        {
            layout["icon-rotate"] = Per(projection, level, IconRotate, losses);
        }

        if (pictures.Any(p => p.OffsetX != 0 || p.OffsetY != 0))
        {
            layout["icon-offset"] = Unwrapped(Per(projection, level, IconOffset, losses));
        }

        if (pictures.Any(p => Math.Abs(p.ScaleX - 1) > 1e-6))
        {
            losses.Add(
                "A picture marker is stretched sideways (`scaleX`). The tile face scales an icon by "
                + "one number, so it draws the picture in its own proportions at the marker's height; "
                + "this server's own drawing and the Esri face keep the stretch.");
        }

        layout["icon-allow-overlap"] = true;
        layout["icon-ignore-placement"] = true;

        layer["layout"] = layout;
        layer["paint"] = new JsonObject();

        return layer;
    }

    /// <summary>A constant `["literal", value]` as the value itself.</summary>
    /// <remarks>
    /// <b>An array output inside a `match` must be wrapped and a constant need not be</b>, and the
    /// constant is what ArcGIS Pro is given (D-280), so the wrapping is taken off where it can be.
    /// </remarks>
    /// <param name="node">The property's value.</param>
    /// <returns>The value, unwrapped when it was a wrapped constant.</returns>
    private static JsonNode? Unwrapped(JsonNode? node) =>
        node is JsonArray { Count: 2 } wrapped
            && (wrapped[0] as JsonValue)?.ToString() == "literal"
            ? wrapped[1]?.DeepClone()
            : node;

    /// <summary>The icon a class draws, by its name in the sprite sheet.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    /// <remarks>
    /// <b>A class whose layer at this level is not a picture draws no icon</b>, rather than falling back to
    /// another class's picture. That happens when a stored picture could not be used and was read as a grey
    /// marker (ADR-099 §5.1) while its neighbours' pictures could: a style layer has one shape for every class,
    /// the difference is already reported, and drawing the school's icon for the clinic would be a map that is
    /// wrong without looking wrong.
    /// </remarks>
    private static JsonNode? IconName(CimPaint? paint) =>
        paint switch
        {
            CimPicture picture => JsonValue.Create(picture.Picture.Name),
            null => null,
            _ => JsonValue.Create(string.Empty),
        };

    /// <summary>How large a class's icon is drawn, as a ratio to its height in the sheet.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? IconSize(CimPaint? paint) =>
        paint is CimPicture picture ? Num(IconScale(picture)) : null;

    /// <summary>`icon-size` for a picture: its height in style pixels over its height in the sheet.</summary>
    /// <param name="picture">The picture.</param>
    /// <returns>The ratio.</returns>
    internal static double IconScale(CimPicture picture) =>
        Math.Round(picture.Size / 0.75 / picture.Picture.SheetHeight, 4, MidpointRounding.AwayFromZero);

    /// <summary>How far a class's icon is turned, clockwise, as the style measures.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? IconRotate(CimPaint? paint) =>
        paint is CimPicture picture ? Num(picture.Rotation == 0 ? 0 : -picture.Rotation) : null;

    /// <summary>How far a class's icon is moved, in the icon's own unscaled pixels, downward.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, wrapped as a literal so that it can be a `match` output.</returns>
    private static JsonNode? IconOffset(CimPaint? paint)
    {
        if (paint is not CimPicture picture)
        {
            return null;
        }

        double scale = IconScale(picture);

        return new JsonArray(
            "literal",
            new JsonArray(
                Num(Math.Round(picture.OffsetX / 0.75 / scale, 3, MidpointRounding.AwayFromZero)),
                Num(Math.Round(-picture.OffsetY / 0.75 / scale, 3, MidpointRounding.AwayFromZero))));
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
        paint is CimFill fill ? Num(Alpha(fill.Colour)) : null;

    /// <summary>The stroke colour at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Stroke(CimPaint? paint) =>
        paint is CimStroke stroke ? Hex(stroke.Colour) : null;

    /// <summary>The stroke opacity at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? StrokeOpacity(CimPaint? paint) =>
        paint is CimStroke stroke ? Num(Alpha(stroke.Colour)) : null;

    /// <summary>The stroke width at a level, in pixels.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Width(CimPaint? paint) =>
        paint is CimStroke stroke ? Num(Pixels(stroke.Width)) : null;

    /// <summary>The marker colour at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Marker(CimPaint? paint) =>
        paint is CimMarker marker ? Hex(marker.Colour) : null;

    /// <summary>The marker opacity at a level.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? MarkerOpacity(CimPaint? paint) =>
        paint is CimMarker marker ? Num(Alpha(marker.Colour)) : null;

    /// <summary>The marker radius at a level, in pixels.</summary>
    /// <param name="paint">The symbol layer.</param>
    /// <returns>The value, or null.</returns>
    private static JsonNode? Radius(CimPaint? paint) =>
        paint is CimMarker marker ? Num(Pixels(marker.Size) / 2) : null;

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

        // <b>The default symbol's own value, where there is one.</b> A `uniqueValue` renderer's
        // default is what a feature no class matches is drawn with, and using the first class's
        // value for it instead would draw every unlisted feature as if it were the first
        // listed — a map that is wrong and looks deliberate.
        JsonNode? standIn = projection.Default is { } fell
            ? of(fell.Paints.ElementAtOrDefault(level))
            : null;

        JsonNode? fallback = standIn ?? values.FirstOrDefault(v => v is not null);

        // <b>Every class the same, or only one: a constant.</b> An expression over a single
        // value is a slower way to say the same thing and a harder one to read in a style
        // somebody is debugging.
        if (projection.Field is null
            || (values.All(v => Same(v, fallback)) && Same(standIn, fallback)))
        {
            return fallback?.DeepClone();
        }

        JsonArray expression = projection.Kind == Cim.UniqueValue
            ? Match(projection, values, fallback, losses)
            : Step(projection, values, fallback);

        return expression;
    }

    /// <summary>A MapLibre `heatmap` layer.</summary>
    /// <remarks>
    /// <b>`heatmap-color` interpolates over `["heatmap-density"]`, which is neither a field nor
    /// the zoom.</b> It is the surface's own value at a pixel, which does not exist until every
    /// feature has been read — so it cannot be evaluated per feature the way every other paint
    /// expression here is. It is written as stops and read back as stops.
    /// </remarks>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="surface">Its density surface.</param>
    /// <param name="layerName">The source layer.</param>
    /// <returns>The style.</returns>
    private static DerivedStyle Surface(
        CimProjection projection, CimHeat surface, string layerName)
    {
        JsonArray colour =
        [
            "interpolate",
            new JsonArray("linear"),
            new JsonArray("heatmap-density"),
        ];

        for (int i = 0; i < surface.Ramp.Count; i++)
        {
            colour.Add(Num((double)i / (surface.Ramp.Count - 1)));
            colour.Add(Hex(surface.Ramp[i]));
        }

        JsonObject paint = new()
        {
            ["heatmap-radius"] = Num(surface.Radius),
            ["heatmap-color"] = colour,
            ["heatmap-opacity"] = Num(1),
        };

        if (surface.Field is { Length: > 0 } weighted)
        {
            paint["heatmap-weight"] = new JsonArray("get", weighted);
        }

        // <b>`heatmap-intensity` carries the ceiling, and it is the closest MapLibre has.</b>
        // CIM's `maxPixelIntensity` fixes the density that reaches the ramp's end; MapLibre has
        // no such property, and multiplying the accumulated weight is the same lever from the
        // other side. A client reading this gets the same picture; a client reading the CIM gets
        // the number.
        if (surface.Ceiling is { } ceiling && ceiling > 0)
        {
            paint["heatmap-intensity"] = Num(1 / ceiling);
        }

        JsonObject style = new()
        {
            ["version"] = 8,
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = $"{layerName}-heat",
                ["type"] = "heatmap",
                ["source"] = "graticula",
                ["source-layer"] = layerName,
                ["paint"] = paint,
            }),
        };

        return new DerivedStyle(style, projection.NotDrawn);
    }

    /// <summary>The nearest a MapLibre style comes to a dot-density map, and it is not near.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="dots">The scatter.</param>
    /// <param name="layerName">The source layer.</param>
    /// <returns>The style, and the sentence about what it cannot do.</returns>
    private static DerivedStyle Scattered(
        CimProjection projection, CimDots dots, string layerName)
    {
        List<string> losses =
        [
            .. projection.NotDrawn,
            "The tile face cannot draw a dot-density map: MapLibre has no layer type for one. "
            + $"It publishes a flat fill in the first counted field's colour ({dots.Fields[0]}) "
            + "instead. The raster faces — WMS, the map service, the preview — draw the dots "
            + "from the stored document, so the same layer looks different on the two faces.",
        ];

        JsonObject style = new()
        {
            ["version"] = 8,
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = $"{layerName}-dots",
                ["type"] = "fill",
                ["source"] = "graticula",
                ["source-layer"] = layerName,
                ["paint"] = new JsonObject
                {
                    ["fill-color"] = Hex(dots.Colours[0]),
                    ["fill-opacity"] = Num(0.35),
                },
            }),
        };

        return new DerivedStyle(style, losses);
    }

    /// <summary>What a MapLibre style can say about a chart renderer, which is not much.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="pie">The chart.</param>
    /// <param name="layerName">The source layer.</param>
    /// <returns>The style, and the sentence about what it cannot do.</returns>
    private static DerivedStyle Charted(
        CimProjection projection, CimPie pie, string layerName)
    {
        List<string> losses =
        [
            .. projection.NotDrawn,
            "The tile face cannot draw a chart: MapLibre has no layer type for one. It publishes "
            + "a circle in the first slice's colour instead. The raster faces — WMS, the map "
            + "service, the preview — draw the chart from the stored document, so the same layer "
            + "looks different on the two faces.",
        ];

        JsonObject style = new()
        {
            ["version"] = 8,
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = $"{layerName}-chart",
                ["type"] = "circle",
                ["source"] = "graticula",
                ["source-layer"] = layerName,
                ["paint"] = new JsonObject
                {
                    ["circle-color"] = Hex(pie.Colours[0]),
                    ["circle-radius"] = Num(Math.Round(pie.Size / 0.75 / 2, 3)),
                },
            }),
        };

        return new DerivedStyle(style, losses);
    }

    /// <summary>What a classified renderer matches on: one field, or several joined.</summary>
    /// <remarks>
    /// <b>`concat` of `to-string`, which is what MapLibre needs and what a client expects.</b>
    /// One field is a plain `get`; two or three are joined by the renderer's own delimiter, in
    /// the order the document lists them, so the key a class matches is the same string Esri's
    /// `value` carries. `to-string` is explicit because `concat` in MapLibre takes strings and a
    /// numeric column would otherwise make the whole expression invalid in a client that checks.
    /// ADR-052 §3.17.
    /// </remarks>
    /// <param name="projection">What the renderer says.</param>
    /// <returns>The expression to match on.</returns>
    private static JsonArray MatchOn(CimProjection projection)
    {
        if (projection.Fields.Count <= 1)
        {
            return new JsonArray("get", projection.Field);
        }

        JsonArray joined = ["concat"];

        for (int i = 0; i < projection.Fields.Count; i++)
        {
            if (i > 0)
            {
                joined.Add(projection.Delimiter);
            }

            joined.Add(new JsonArray("to-string", new JsonArray("get", projection.Fields[i])));
        }

        return joined;
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
        JsonArray expression = ["match", MatchOn(projection)];
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

        // <b>`match` needs an otherwise, and it is the default symbol's value when there is
        // one.</b> `fallback` already is that value where the renderer carries a default, and
        // the first class's where it does not — so a style always says what an unlisted value
        // is drawn with rather than leaving MapLibre to decide.
        expression.Add(fallback?.DeepClone());

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
        ];

        // <b>The floor is a stop like any other, and it is the one that was missing.</b>
        // `step`'s first output covers everything below its first stop; without `minimumBreak`
        // that was the first class, so every value beneath the classification was drawn as if
        // it were inside it. With a floor the first output becomes the default symbol and the
        // floor itself becomes the first stop. D-205.
        if (projection.Floor is { } floor)
        {
            expression.Add(fallback?.DeepClone());
            expression.Add(Num(floor));
        }

        expression.Add((values[0] ?? fallback)?.DeepClone());

        for (int i = 0; i < projection.Classes.Count - 1; i++)
        {
            if (projection.Classes[i].UpperBound is not { } bound)
            {
                continue;
            }

            expression.Add(Num(Math.BitIncrement(bound)));
            expression.Add((values[i + 1] ?? fallback)?.DeepClone());
        }

        return expression;
    }

    /// <summary>
    /// Reads a MapLibre style into the CIM renderer that says the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The inverse of <see cref="ToMapLibre"/>, and written as one.</b> Going the other way
    /// through Esri's `drawingInfo` would have been less code and would have flattened every
    /// multi-layer style at the moment of storage — which is the exact loss
    /// [ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) moved the
    /// canonical document to stop. A style with a casing and a fill has to arrive as a symbol
    /// with a casing and a fill.
    /// </para>
    /// <para>
    /// <b>One classification for the whole style.</b> MapLibre lets every paint property carry
    /// its own expression over its own field; a CIM renderer classifies once. Where the layers
    /// disagree about the field, the style is refused rather than stored half-read, because a
    /// renderer built from the first field it found would silently draw by something nobody
    /// chose.
    /// </para>
    /// </remarks>
    /// <param name="style">The MapLibre style.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="pictures">
    /// The pictures an `icon-image` may name, by <see cref="MarkerPicture.Name"/>, or null for none —
    /// ADR-099. A style carries no pictures of its own, only names in a sprite sheet, so an icon this
    /// server can store has to be one it already holds: in practice the ones the layer's current
    /// symbology draws, which is what a style downloaded from the tile face names.
    /// </param>
    /// <returns>The renderer, and what could not be carried.</returns>
    public static CimWrite FromMapLibre(
        JsonObject style,
        GeometryKind geometry,
        IReadOnlyDictionary<string, MarkerPicture>? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(style);

        // <b>No dictionary is not an empty one.</b> A caller that passes none is reading a document
        // stored before ADR-099 (`SymbologyConversion.ToCim`), where an icon layer was never stored as a
        // picture and is reported as it always was; a caller that passes one is a write, where an icon
        // this server cannot turn back into a picture is refused with the way forward.
        bool readsIcons = pictures is not null;

        pictures ??= new Dictionary<string, MarkerPicture>(StringComparer.Ordinal);

        // <b>The per-class form this server publishes is read as the expressions it came
        // from.</b> Since 2026-09-29 <see cref="ToMapLibre"/> writes one filtered style layer per
        // class (D-280), so a style somebody downloaded from the tile face and stored again
        // arrives in that shape. Folding it back here means everything below reads one form, and
        // the two things the filters say that no paint expression can — that a renderer has no
        // default symbol, and where its floor is — travel beside it.
        Collapsed? collapsed = Collapse(style);

        if (collapsed is not null)
        {
            style = collapsed.Style;
        }

        List<string> losses = [];

        List<JsonObject> painting = [];

        foreach (JsonObject layer in (style["layers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string kind = (string?)layer["type"] ?? string.Empty;

            if (kind is "fill" or "line" or "circle")
            {
                painting.Add(layer);

                // <b>A scale range belongs to a CIM *layer*, not to a symbol</b>, and what is
                // stored here is a renderer. So a style layer that appears between two zooms
                // cannot be expressed and this says so. Found while converting the suite on
                // 2026-09-03 rather than reasoned about in advance: `SymbologyPlan` honours
                // `minzoom` and `maxzoom` today, so this is a capability the canonical move
                // costs, and ADR-052 §4 records it as one.
                if (layer["minzoom"] is not null || layer["maxzoom"] is not null)
                {
                    losses.Add(
                        $"The `{(string?)layer["id"] ?? kind}` layer appears only between "
                        + $"zoom {layer["minzoom"]?.ToString() ?? "0"} and "
                        + $"{layer["maxzoom"]?.ToString() ?? "24"}. A CIM renderer describes a "
                        + "symbol and a scale range belongs to a layer, so the range is not "
                        + "stored and the symbol is drawn at every scale.");
                }

                if (layer["layout"] is JsonObject layout && layout.Count > 0)
                {
                    losses.Add(
                        $"The `{(string?)layer["id"] ?? kind}` layer sets "
                        + string.Join(", ", layout.Select(p => $"`{p.Key}`"))
                        + ". A CIM symbol layer has its own vocabulary for caps, joins and "
                        + "placement, and this server does not map those across, so they are "
                        + "not stored.");
                }

                continue;
            }

            // <b>A `symbol` layer with an icon is a picture marker, since ADR-099</b> — the shape
            // this server's own tile style draws one with. Its text, when it has any, is still a
            // label, and labels are still not stored.
            if (kind == "symbol" && readsIcons && (layer["layout"] as JsonObject)?["icon-image"] is not null)
            {
                painting.Add(layer);

                JsonObject layout = (JsonObject)layer["layout"]!;
                string[] foreign = [.. layout
                    .Select(p => p.Key)
                    .Where(k => k is not ("icon-image" or "icon-size" or "icon-rotate" or "icon-offset"
                        or "icon-allow-overlap" or "icon-ignore-placement"))];

                if (foreign.Length > 0)
                {
                    losses.Add(
                        $"The `{(string?)layer["id"] ?? kind}` layer sets "
                        + string.Join(", ", foreign.Select(k => $"`{k}`"))
                        + ". A picture marker stores which picture, its size, its offset and its "
                        + "rotation; the rest is not stored"
                        + (layout["text-field"] is not null ? ", and the text is a label, which v1 does not store." : "."));
                }

                continue;
            }

            if (kind == "symbol")
            {
                losses.Add(
                    "The style has a `symbol` layer, so it labels features. Labelling is not in "
                    + "v1 (ADR-033 §5g), so the labels are not stored and not drawn.");

                continue;
            }

            losses.Add(
                $"The style has a `{kind}` layer, which this server does not paint with. It is "
                + "not stored.");
        }

        if (painting.Count == 0)
        {
            throw new SymbologyException(
                "The style has no `fill`, `line` or `circle` layer and no `symbol` layer with an "
                + "`icon-image`, so there is nothing to draw with.");
        }

        // <b>The keys come from whichever layer classifies by the most.</b> A style that paints
        // a casing in one colour and the road above it in four needs four classes, and the
        // casing repeats its single value into each of them.
        Classified? classified = null;

        foreach (JsonObject layer in painting)
        {
            foreach (JsonNode? value in Paints(layer))
            {
                if (Read(value) is not { } found)
                {
                    continue;
                }

                if (classified is { } already)
                {
                    if (!string.Equals(already.Field, found.Field, StringComparison.Ordinal)
                        || !already.Fields.SequenceEqual(found.Fields, StringComparer.Ordinal))
                    {
                        throw new SymbologyException(
                            $"The style classifies by `{already.Field}` in one place and by "
                            + $"`{found.Field}` in another. A renderer classifies by one field, "
                            + "so this style cannot be stored without choosing which — and this "
                            + "server will not choose.");
                    }

                    if (already.Kind != found.Kind)
                    {
                        throw new SymbologyException(
                            $"The style uses both `match` and `step` over `{found.Field}`. Those "
                            + "are two different renderers and a layer has one.");
                    }

                    if (found.Keys.Count <= already.Keys.Count)
                    {
                        continue;
                    }
                }

                classified = found;
            }
        }

        JsonArray variables = Continuous(painting, losses, pictures);

        int classes = classified?.Keys.Count ?? 1;
        JsonArray symbols = [];

        for (int i = 0; i < classes; i++)
        {
            symbols.Add(Symbol(painting, geometry, classified, i, losses, pictures));
        }

        if (classified is not { } over)
        {
            JsonObject one = new()
            {
                ["type"] = Cim.Simple,
                ["label"] = string.Empty,
                ["description"] = string.Empty,
                ["symbol"] = symbols[0]!.DeepClone(),
            };

            if (variables.Count > 0)
            {
                one["visualVariables"] = variables;
            }

            return new CimWrite(one, losses);
        }

        // <b>Index -1 asks `Choose` for the otherwise.</b> No class has that key, so every
        // expression falls through to its own fallback and a constant property answers itself —
        // which is exactly what a feature no class matches should be drawn with.
        //
        // <b>Unless the style was the per-class form and had no default layer.</b> A `match`
        // always ends in an otherwise, so reading one cannot tell a renderer with a default
        // symbol from one without; the filtered form can, because a renderer without one
        // publishes no layer for the values nobody listed.
        JsonNode? otherwise = over.Kind == "match"
            && over.Fallback is not null
            && collapsed?.HasDefault != false
                ? Symbol(painting, geometry, classified, -1, losses, pictures)
                : null;

        JsonObject built = over.Kind == "match"
            ? UniqueIn(over, symbols, otherwise)
            : BreaksIn(over, symbols, collapsed?.Floor);

        if (variables.Count > 0)
        {
            built["visualVariables"] = variables;
        }

        return new CimWrite(built, losses);
    }

    /// <summary>A `CIMUniqueValueRenderer` from a `match`.</summary>
    /// <param name="over">What the style classifies by.</param>
    /// <param name="symbols">One per class, already built.</param>
    /// <param name="fallback">The symbol for values no class lists, or null.</param>
    /// <returns>The renderer.</returns>
    private static JsonObject UniqueIn(Classified over, JsonArray symbols, JsonNode? fallback)
    {
        JsonArray classes = [];
        IReadOnlyList<string> fields = over.Fields.Count > 0 ? over.Fields : [over.Field];

        for (int i = 0; i < over.Keys.Count; i++)
        {
            // <b>A label that is an array is one class of several values</b>, which is what
            // MapLibre means by it and what the per-class form collapses to: a class listing two
            // values is one filtered layer, and reading it as two classes would publish two.
            List<string> listed = over.Keys[i] is JsonArray several
                ? [.. several.Select(v => v?.ToString() ?? string.Empty)]
                : [over.Keys[i]?.ToString() ?? string.Empty];

            JsonArray values = [];

            foreach (string value in listed)
            {
                // <b>Split on the delimiter the `concat` was read with</b>, into as many parts as
                // there are fields, so a two-field class stores a pair again.
                string[] parts = fields.Count > 1
                    ? value.Split(over.Delimiter, fields.Count)
                    : [value];

                values.Add(new JsonObject
                {
                    ["type"] = "CIMUniqueValue",
                    ["fieldValues"] = new JsonArray([.. Enumerable.Range(0, fields.Count)
                        .Select(p => (JsonNode?)(p < parts.Length ? parts[p] : string.Empty))]),
                });
            }

            classes.Add(new JsonObject
            {
                ["label"] = string.Join(", ", listed),
                ["visible"] = true,
                ["values"] = values,
                ["symbol"] = symbols[i]!.DeepClone(),
            });
        }

        JsonObject built = new()
        {
            ["type"] = Cim.UniqueValue,
            ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode?)f)]),
            ["groups"] = new JsonArray(new JsonObject { ["classes"] = classes }),
        };

        if (fields.Count > 1)
        {
            built["fieldDelimiter"] = over.Delimiter;
        }

        // <b>A `match`'s last element is its otherwise, and it is a class.</b> Dropping it
        // stores a renderer that draws nothing for every value nobody listed, and the legend
        // loses the row that says so. Measured on 2026-09-03: a three-row legend became a
        // one-row legend after a migration, and the store had two classes where the style had
        // two and an otherwise.
        if (fallback is not null)
        {
            built["useDefaultSymbol"] = true;
            built["defaultLabel"] = "Other";
            built["defaultSymbol"] = fallback.DeepClone();
        }

        return built;
    }

    /// <summary>A `CIMClassBreaksRenderer` from a `step`.</summary>
    /// <remarks>
    /// <b>The bound comes back down by one representable double.</b> `ToMapLibre` steps at
    /// `BitIncrement(bound)` so that MapLibre's lower-inclusive stop means Esri's
    /// upper-inclusive break; reading it back has to undo exactly that, or a document that went
    /// out and came back would drift by a bit every time.
    /// </remarks>
    /// <param name="over">What the style classifies by.</param>
    /// <param name="symbols">One per class, already built.</param>
    /// <param name="floor">
    /// The classification's floor, known only when the style was the per-class form: its
    /// layer below the floor is the default symbol, and the `step` it collapses to starts with
    /// that symbol rather than with a class.
    /// </param>
    /// <returns>The renderer.</returns>
    private static JsonObject BreaksIn(Classified over, JsonArray symbols, double? floor)
    {
        JsonArray breaks = [];

        // <b>With a floor, the `step`'s first output is the default symbol and not a class.</b>
        // That is how <see cref="Step"/> writes it (D-205), so reading it back as a class would
        // turn *below the classification* into a class of its own with a bound one bit under the
        // floor.
        int first = floor is not null && over.Keys.Count > 1 ? 1 : 0;

        for (int i = first; i < over.Keys.Count; i++)
        {
            double bound = i < over.Keys.Count - 1
                ? Math.BitDecrement(Figure(over.Keys[i + 1]) ?? 0)
                : double.MaxValue;

            breaks.Add(new JsonObject
            {
                ["upperBound"] = Num(bound),
                ["label"] = bound.ToString("G17", CultureInfo.InvariantCulture),
                ["symbol"] = symbols[i]!.DeepClone(),
            });
        }

        JsonObject built = new()
        {
            ["type"] = Cim.ClassBreaks,
            ["field"] = over.Field,
            ["breaks"] = breaks,
        };

        if (first == 1)
        {
            built["minimumBreak"] = Num(floor!.Value);
            built["useDefaultSymbol"] = true;
            built["defaultLabel"] = "Other";
            built["defaultSymbol"] = symbols[0]!.DeepClone();
        }

        return built;
    }

    /// <summary>One CIM symbol, taking each style layer's value for one class.</summary>
    /// <param name="painting">The style's painting layers, bottom first.</param>
    /// <param name="geometry">What the layer is made of.</param>
    /// <param name="classified">What the style classifies by, or null.</param>
    /// <param name="index">Which class.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <param name="pictures">The pictures an `icon-image` may name.</param>
    /// <returns>The symbol reference.</returns>
    private static JsonObject Symbol(
        List<JsonObject> painting,
        GeometryKind geometry,
        Classified? classified,
        int index,
        List<string> losses,
        IReadOnlyDictionary<string, MarkerPicture> pictures)
    {
        JsonArray layers = [];

        // <b>Reversed, because CIM draws index zero on top.</b> A style paints its first layer
        // at the bottom and this is the one place the two orders meet.
        foreach (JsonObject layer in Enumerable.Reverse(painting))
        {
            JsonObject paint = layer["paint"] as JsonObject ?? [];
            string kind = (string?)layer["type"] ?? string.Empty;

            switch (kind)
            {
                case "fill":
                    layers.Add(new JsonObject
                    {
                        ["type"] = "CIMSolidFill",
                        ["enable"] = true,
                        ["color"] = Cim.Colour(Paint(
                            paint["fill-color"], paint["fill-opacity"], "fill-color", classified, index, losses)),
                    });

                    break;

                case "line":
                    JsonObject stroke = new()
                    {
                        ["type"] = "CIMSolidStroke",
                        ["enable"] = true,
                        ["capStyle"] = "Butt",
                        ["joinStyle"] = "Miter",
                        ["width"] = Num(Points(
                            Choose(paint["line-width"], "line-width", classified, index, losses) is { } width
                                ? Figure(width) ?? 1
                                : 1)),
                        ["color"] = Cim.Colour(Paint(
                            paint["line-color"], paint["line-opacity"], "line-color", classified, index, losses)),
                    };

                    if (paint["line-dasharray"] is JsonArray dashes && dashes.Count > 0)
                    {
                        stroke["effects"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "CIMGeometricEffectDashes",
                                ["dashTemplate"] = dashes.DeepClone(),
                                ["lineDashEnding"] = "NoConstraint",
                            });
                    }

                    layers.Add(stroke);

                    break;

                case "circle":
                    Rgba colour = Paint(
                        paint["circle-color"], paint["circle-opacity"], "circle-color", classified, index,
                        losses);

                    double radius = Choose(paint["circle-radius"], "circle-radius", classified, index, losses) is { } r
                        ? Figure(r) ?? 5
                        : 5;

                    layers.Add(new JsonObject
                    {
                        ["type"] = "CIMVectorMarker",
                        ["enable"] = true,
                        ["size"] = Num(Points(radius * 2)),
                        ["rotation"] = Num(0),
                        ["markerGraphics"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "CIMMarkerGraphic",
                                ["geometry"] = new JsonObject { ["x"] = Num(0), ["y"] = Num(0) },
                                ["symbol"] = new JsonObject
                                {
                                    ["type"] = "CIMPolygonSymbol",
                                    ["symbolLayers"] = new JsonArray(
                                        new JsonObject
                                        {
                                            ["type"] = "CIMSolidFill",
                                            ["enable"] = true,
                                            ["color"] = Cim.Colour(colour),
                                        }),
                                },
                            }),
                    });

                    break;

                case "symbol":
                    layers.Add(PictureIn(
                        layer["layout"] as JsonObject ?? [], classified, index, losses, pictures));
                    break;

                default:
                    losses.Add($"A `{kind}` layer is not stored.");
                    break;
            }
        }

        return new JsonObject
        {
            ["type"] = "CIMSymbolReference",
            ["symbol"] = new JsonObject
            {
                ["type"] = geometry switch
                {
                    GeometryKind.Point or GeometryKind.MultiPoint => "CIMPointSymbol",
                    GeometryKind.LineString or GeometryKind.MultiLineString => "CIMLineSymbol",
                    _ => "CIMPolygonSymbol",
                },
                ["symbolLayers"] = layers,
            },
        };
    }

    /// <summary>One class's picture marker, read out of an icon layer's layout — the inverse of <see cref="Icon"/>.</summary>
    /// <remarks>
    /// <b>The picture is looked up, never fetched.</b> An `icon-image` is a name in a sprite sheet; the
    /// names this server can turn back into a picture are the ones it generated itself, and only for a
    /// picture it holds (ADR-099). Any other name is refused with the reason, because storing a picture
    /// marker with no picture would be storing a symbol that draws nothing.
    /// </remarks>
    /// <param name="layout">The icon layer's layout.</param>
    /// <param name="classified">What the style classifies by, or null.</param>
    /// <param name="index">Which class.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <param name="pictures">The pictures an `icon-image` may name.</param>
    /// <returns>The `CIMPictureMarker`.</returns>
    private static JsonObject PictureIn(
        JsonObject layout,
        Classified? classified,
        int index,
        List<string> losses,
        IReadOnlyDictionary<string, MarkerPicture> pictures)
    {
        string? name = (Choose(layout["icon-image"], "icon-image", classified, index, losses) as JsonValue)
            ?.ToString();

        if (name is null || !pictures.TryGetValue(name, out MarkerPicture? picture))
        {
            throw new SymbologyException(
                $"The style draws the icon '{name ?? "(an expression)"}', and this server can store an "
                + "icon only as a picture it already holds: the icons its own tile style names, "
                + $"`{MarkerPicture.NamePrefix}…`, for pictures this layer draws now. A sprite sheet's "
                + "other icons are not pictures this server can read back. Send the picture itself — a "
                + "`CIMPictureMarker` with a data URI, or an `esriPMS` with `imageData`.");
        }

        double scale = Figure(Choose(layout["icon-size"], "icon-size", classified, index, losses)) ?? 1;

        // <b>A constant pair is data, not an expression</b>, and `Choose` would read its first number
        // as an operator's name.
        JsonNode? given = layout["icon-offset"];
        JsonNode? chosen = given is JsonArray { Count: > 0 } constant
            && constant[0] is JsonValue first && !first.TryGetValue(out string? _)
                ? given
                : Choose(given, "icon-offset", classified, index, losses);

        double[] offset = chosen switch
        {
            JsonArray { Count: 2 } wrapped when (wrapped[0] as JsonValue)?.ToString() == "literal"
                && wrapped[1] is JsonArray pair => [Figure(pair.ElementAtOrDefault(0)) ?? 0, Figure(pair.ElementAtOrDefault(1)) ?? 0],
            JsonArray pair => [Figure(pair.ElementAtOrDefault(0)) ?? 0, Figure(pair.ElementAtOrDefault(1)) ?? 0],
            _ => [0, 0],
        };

        double rotate = Figure(Choose(layout["icon-rotate"], "icon-rotate", classified, index, losses)) ?? 0;

        return Cim.PictureLayer(new CimPicture(
            picture,
            Points(scale * picture.SheetHeight),
            1,
            Points(offset[0] * scale),
            Points(-offset[1] * scale) + 0.0,
            rotate == 0 ? 0 : -rotate));
    }

    /// <summary>A colour and its separate opacity, together.</summary>
    /// <param name="colour">The colour property.</param>
    /// <param name="opacity">The opacity property.</param>
    /// <param name="colourName">What the colour property is called.</param>
    /// <param name="classified">What the style classifies by, or null.</param>
    /// <param name="index">Which class.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The colour.</returns>
    private static Rgba Paint(
        JsonNode? colour,
        JsonNode? opacity,
        string colourName,
        Classified? classified,
        int index,
        List<string> losses)
    {
        Rgba found = Rgba.TryParse(
            (Choose(colour, colourName, classified, index, losses) as JsonValue)?.ToString(),
            out Rgba parsed)
            ? parsed
            : new Rgba(136, 136, 136, 255);

        if (Choose(opacity, colourName + "-opacity", classified, index, losses) is { } fraction
            && Figure(fraction) is { } alpha)
        {
            found = found with
            {
                A = (byte)Math.Clamp(
                    Math.Round(alpha * 255, MidpointRounding.AwayFromZero), 0, 255),
            };
        }

        return found;
    }

    /// <summary>One paint property's value for one class.</summary>
    /// <param name="value">The property.</param>
    /// <param name="property">What it is called, for a message that names it.</param>
    /// <param name="classified">What the style classifies by, or null.</param>
    /// <param name="index">Which class.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <returns>The value.</returns>
    private static JsonNode? Choose(
        JsonNode? value,
        string property,
        Classified? classified,
        int index,
        List<string> losses)
    {
        if (Read(value) is not { } own || classified is not { } over)
        {
            // <b>An expression that is neither a `match` nor a `step` is named here.</b>
            // `["interpolate", ["linear"], ["zoom"], …]` varies a width with the scale and a
            // symbol carries one value, so it cannot be stored — and saying nothing would leave
            // somebody with a line that quietly stopped changing with the scale.
            if (value is JsonArray other && Read(other) is null)
            {
                // <b>An interpolate over a column is not lost any more: it is stored.</b> It
                // becomes a visual variable (ADR-052 §3.6), so the symbol keeps the value at the
                // lowest stop and the variation travels beside it rather than being reported
                // gone. Only an interpolate over something else -- the zoom, an expression --
                // still has nowhere to go.
                if (Continuous(other) is not null)
                {
                    return other.Count > 4 ? other[4]?.DeepClone() : null;
                }

                string head = (other.ElementAtOrDefault(0) as JsonValue)?.ToString()
                    ?? "an expression";

                // <b>The value at the lowest stop, not a default.</b> An `interpolate` lists
                // its stops in ascending order, so the first output is what the property is at
                // the smallest input — which is a value the author chose, where the property's
                // own default is one nobody did.
                JsonNode? lowest = head == "interpolate" && other.Count > 4
                    ? other[4]?.DeepClone()
                    : null;

                string said =
                    $"`{property}` is an `{head}` expression over `"
                    + ((other.ElementAtOrDefault(2) as JsonArray)?.ElementAtOrDefault(0)
                        ?.ToString() ?? "an input")
                    + "`. A symbol carries one value per class, so this face keeps "
                    + (lowest is null
                        ? "the property's default"
                        : $"`{lowest}`, its value at the lowest stop")
                    + " and the variation is not stored.";

                if (!losses.Contains(said, StringComparer.Ordinal))
                {
                    losses.Add(said);
                }

                return lowest;
            }

            return value;
        }

        // <b>Matched by key, not by position.</b> Two expressions over the same field can list
        // their classes in different orders, and taking the *n*th of each would pair a road's
        // colour with a track's width.
        JsonNode? wanted = over.Keys.ElementAtOrDefault(index);

        for (int i = 0; i < own.Keys.Count; i++)
        {
            if (Same(own.Keys[i], wanted))
            {
                return own.Outputs[i];
            }
        }

        return own.Fallback;
    }

    /// <summary>A number out of a paint value.</summary>
    /// <param name="node">The value.</param>
    /// <returns>Its number, or null.</returns>
    private static double? Figure(JsonNode? node) =>
        node is JsonValue value
            && double.TryParse(
                value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            ? n
            : null;

    /// <summary>A style's pixels as CIM's points.</summary>
    /// <param name="pixels">The width or size.</param>
    /// <returns>Points.</returns>
    private static double Points(double pixels) =>
        Math.Round(pixels * 0.75, 4, MidpointRounding.AwayFromZero);

    /// <summary>The paint properties this server reads out of one style layer.</summary>
    /// <remarks>
    /// <b>An icon layer's layout too</b>, because a picture's class is its `icon-image` and that is a
    /// layout property (ADR-099).
    /// </remarks>
    /// <param name="layer">The style layer.</param>
    /// <returns>Its values.</returns>
    private static IEnumerable<JsonNode?> Paints(JsonObject layer)
    {
        foreach (KeyValuePair<string, JsonNode?> each in layer["paint"] as JsonObject ?? [])
        {
            yield return each.Value;
        }

        if ((string?)layer["type"] != "symbol")
        {
            yield break;
        }

        foreach (KeyValuePair<string, JsonNode?> each in layer["layout"] as JsonObject ?? [])
        {
            yield return each.Value;
        }
    }

    /// <summary>Reads a `match` or a `step`, or nothing when the value is a constant.</summary>
    /// <param name="node">The paint value.</param>
    /// <returns>What it classifies by, or null.</returns>
    private static Classified? Read(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count < 3)
        {
            return null;
        }

        string head = (array[0] as JsonValue)?.ToString() ?? string.Empty;

        if (head is not ("match" or "step") || array[1] is not JsonArray input)
        {
            return null;
        }

        string field;
        IReadOnlyList<string> fields = [];
        string delimiter = ", ";

        if (input.Count >= 2 && (input[0] as JsonValue)?.ToString() == "get")
        {
            field = input[1]?.ToString() ?? string.Empty;
        }
        else if (head == "match" && Joined(input) is { } joined)
        {
            // <b>A `match` over several fields joined, which is what this server writes for a
            // renderer of two or three fields</b> (ADR-052 §3.17). Read until 2026-09-29 as an
            // expression it could not store, so a style this server had derived could not be
            // stored again.
            fields = joined.Fields;
            delimiter = joined.Delimiter;
            field = string.Join(", ", fields);
        }
        else
        {
            return null;
        }

        List<JsonNode?> keys = [];
        List<JsonNode?> outputs = [];

        if (head == "match")
        {
            for (int i = 2; i + 1 < array.Count; i += 2)
            {
                keys.Add(array[i]?.DeepClone());
                outputs.Add(array[i + 1]?.DeepClone());
            }

            return new Classified(
                field, head, keys, outputs, array[^1]?.DeepClone())
            {
                Fields = fields,
                Delimiter = delimiter,
            };
        }

        // <b>A `step`'s first output has no stop.</b> It is what everything below the first
        // boundary gets, so it is class zero and its key is the boundary above it.
        outputs.Add(array[2]?.DeepClone());
        keys.Add(null);

        for (int i = 3; i + 1 < array.Count; i += 2)
        {
            keys.Add(array[i]?.DeepClone());
            outputs.Add(array[i + 1]?.DeepClone());
        }

        return new Classified(field, head, keys, outputs, null);
    }

    /// <summary>What one paint value classifies by.</summary>
    /// <param name="Field">The column.</param>
    /// <param name="Kind">`match` or `step`.</param>
    /// <param name="Keys">One per class; null for a `step`'s first, which has no stop.</param>
    /// <param name="Outputs">One per class, in the same order.</param>
    /// <param name="Fallback">What a `match` gives a value it does not list.</param>
    private sealed record Classified(
        string Field,
        string Kind,
        IReadOnlyList<JsonNode?> Keys,
        IReadOnlyList<JsonNode?> Outputs,
        JsonNode? Fallback)
    {
        /// <summary>The fields a `match` over a `concat` joins, or empty for one field.</summary>
        public IReadOnlyList<string> Fields { get; init; } = [];

        /// <summary>What the `concat` joins them with.</summary>
        public string Delimiter { get; init; } = ", ";
    }

    /// <summary>
    /// Reads `["concat", ["to-string", ["get", f1]], d, ["to-string", ["get", f2]], …]`, the
    /// input <see cref="MatchOn"/> writes for a renderer of several fields.
    /// </summary>
    /// <param name="input">The `match`'s input.</param>
    /// <returns>The fields and the one delimiter between them, or null for any other shape.</returns>
    private static (IReadOnlyList<string> Fields, string Delimiter)? Joined(JsonArray input)
    {
        if (input.Count < 4
            || input.Count % 2 != 0
            || (input[0] as JsonValue)?.ToString() != "concat")
        {
            return null;
        }

        List<string> fields = [];
        string? delimiter = null;

        for (int i = 1; i < input.Count; i++)
        {
            if (i % 2 == 0)
            {
                // Every separator has to be the same string, or the key is not one delimiter
                // joining fields and cannot be split back into them.
                string? between = input[i] is JsonValue text && text.TryGetValue(out string? s)
                    ? s
                    : null;

                if (between is null || (delimiter is not null && delimiter != between))
                {
                    return null;
                }

                delimiter = between;
                continue;
            }

            JsonArray? part = input[i] as JsonArray;

            if (part is { Count: 2 } && (part[0] as JsonValue)?.ToString() == "to-string")
            {
                part = part[1] as JsonArray;
            }

            if (part is not { Count: >= 2 } || (part[0] as JsonValue)?.ToString() != "get")
            {
                return null;
            }

            fields.Add(part[1]?.ToString() ?? string.Empty);
        }

        return delimiter is null ? null : (fields, delimiter);
    }

    /// <summary>
    /// A number that reads back as whatever type asks for it.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>JsonValue.Create</c>, and the difference is a whole class of defect.</b> A
    /// <c>JsonValue</c> created from an <c>int</c> is a <c>JsonValue&lt;int&gt;</c> and refuses
    /// <c>GetValue&lt;double&gt;</c>; one created from a <c>double</c> refuses
    /// <c>GetValue&lt;int&gt;</c>. Both serialise identically, so the document on the wire looks
    /// right and every consumer that reads it in memory throws. Going through a
    /// <c>JsonElement</c> gives a value backed by the parser, which converts the way a document
    /// read from text does. Measured twice on 2026-09-03, in the round trip and in the Esri face.
    /// </remarks>
    /// <param name="value">The number.</param>
    /// <returns>The node.</returns>
    private static JsonValue Num(double value) =>
        JsonValue.Create(JsonSerializer.SerializeToElement(value))!;

    /// <summary>
    /// Turns every `interpolate` over a column into a visual variable.
    /// </summary>
    /// <remarks>
    /// <b>The inverse of <see cref="Vary"/>, and the reason a continuous style stopped being a
    /// loss.</b> Before ADR-052 §3.6 a style that faded a colour with population was flattened to
    /// the colour at the lowest stop and reported gone. The canonical model has somewhere to keep
    /// it now, so it is kept.
    /// </remarks>
    /// <param name="painting">The style's painting layers.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <param name="pictures">The pictures an `icon-image` may name, for what an `icon-size` is a ratio to.</param>
    /// <returns>The `visualVariables` array, possibly empty.</returns>
    private static JsonArray Continuous(
        List<JsonObject> painting,
        List<string> losses,
        IReadOnlyDictionary<string, MarkerPicture> pictures)
    {
        JsonArray variables = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonObject layer in painting)
        {
            // <b>An icon's size slides in its layout</b>, as a ratio to the icon's height in the sheet;
            // the height is the picture the layer draws when no class says otherwise.
            double iconHeight = (layer["layout"] as JsonObject)?["icon-image"] switch
            {
                JsonValue literal when pictures.TryGetValue(literal.ToString(), out MarkerPicture? one) => one.SheetHeight,
                JsonArray { Count: > 2 } expression when expression[^1] is JsonValue last
                    && pictures.TryGetValue(last.ToString(), out MarkerPicture? fallback) => fallback.SheetHeight,
                _ => 1,
            };

            IEnumerable<KeyValuePair<string, JsonNode?>> properties =
                (layer["paint"] as JsonObject ?? []).Concat(
                    (string?)layer["type"] == "symbol"
                        ? (layer["layout"] as JsonObject ?? []).Where(p => p.Key == "icon-size")
                        : []);

            foreach (KeyValuePair<string, JsonNode?> property in properties)
            {
                if (property.Value is not JsonArray expression
                    || Continuous(expression) is not { } over)
                {
                    continue;
                }

                // <b>One variable per property kind, not one per style layer.</b> A renderer's
                // visual variables are the renderer's; a casing and a road that both fade with
                // the same column are one variable, and writing it twice would apply it twice.
                string what = property.Key.Split('-')[^1];

                if (!seen.Add(what + "|" + over.Field))
                {
                    continue;
                }

                if (Written(what, over, losses, iconHeight) is { } written)
                {
                    variables.Add(written);
                }
            }
        }

        return variables;
    }

    /// <summary>One visual variable, in the vocabulary CIM keeps it in.</summary>
    /// <param name="what">`color`, `width`, `radius`, `size` or `opacity`.</param>
    /// <param name="over">The field and the stops.</param>
    /// <param name="losses">Collects what could not be carried.</param>
    /// <param name="iconHeight">What an `icon-size` is a ratio to, in pixels.</param>
    /// <returns>The variable, or null when this property does not become one.</returns>
    private static JsonObject? Written(string what, Slide over, List<string> losses, double iconHeight)
    {
        string field = over.Field;

        switch (what)
        {
            case "color":
                List<Rgba> colours = [];

                foreach (JsonNode? output in over.Outputs)
                {
                    if (Rgba.TryParse((output as JsonValue)?.ToString(), out Rgba parsed))
                    {
                        colours.Add(parsed);
                    }
                }

                if (colours.Count < 2)
                {
                    return null;
                }

                JsonObject ramp = colours.Count == 2
                    ? new JsonObject
                    {
                        ["type"] = "CIMLinearContinuousColorRamp",
                        ["colorSpace"] = new JsonObject { ["type"] = "CIMICCColorSpace" },
                        ["fromColor"] = Cim.Colour(colours[0]),
                        ["toColor"] = Cim.Colour(colours[^1]),
                    }
                    : new JsonObject
                    {
                        ["type"] = "CIMFixedColorRamp",
                        ["colors"] = new JsonArray(
                            [.. colours.Select(c => (JsonNode?)Cim.Colour(c))]),
                    };

                if (colours.Count > 2)
                {
                    losses.Add(
                        $"The colour over `{field}` has {colours.Count} stops at uneven values. "
                        + "A CIM colour ramp spaces its colours evenly between the smallest and "
                        + "largest value, so the colour changes at slightly different numbers "
                        + "than the style asked for.");
                }

                return new JsonObject
                {
                    ["type"] = "CIMColorVisualVariable",
                    ["expression"] = "$feature." + field,
                    ["minValue"] = Num(over.Stops[0]),
                    ["maxValue"] = Num(over.Stops[^1]),
                    ["colorRamp"] = ramp,
                };

            case "width":
            case "radius":
            case "size":
                List<double> sizes = [];

                foreach (JsonNode? output in over.Outputs)
                {
                    sizes.Add(what == "size"
                        ? Points((Figure(output) ?? 0) * iconHeight)
                        : Points(Figure(output) ?? 0) * (what == "radius" ? 2 : 1));
                }

                return new JsonObject
                {
                    ["type"] = "CIMSizeVisualVariable",
                    ["expression"] = "$feature." + field,
                    ["dataValues"] = new JsonArray([.. over.Stops.Select(s => (JsonNode?)Num(s))]),
                    ["sizeValues"] = new JsonArray([.. sizes.Select(s => (JsonNode?)Num(s))]),
                    ["minValue"] = Num(over.Stops[0]),
                    ["maxValue"] = Num(over.Stops[^1]),
                    ["minSize"] = Num(sizes[0]),
                    ["maxSize"] = Num(sizes[^1]),
                };

            case "opacity":
                JsonArray transparency = [];

                foreach (JsonNode? output in over.Outputs)
                {
                    // CIM stores transparency, which is the other way up from opacity.
                    transparency.Add(Num(Math.Clamp(
                        Math.Round(100 - ((Figure(output) ?? 1) * 100), 2), 0, 100)));
                }

                return new JsonObject
                {
                    ["type"] = "CIMTransparencyVisualVariable",
                    ["field"] = field,
                    ["dataValues"] = new JsonArray([.. over.Stops.Select(s => (JsonNode?)Num(s))]),
                    ["transparencyValues"] = transparency,
                };

            default:
                return null;
        }
    }

    /// <summary>Reads `["interpolate", ["linear"], ["get", f], …]`, or nothing.</summary>
    /// <remarks>
    /// <b>Over a column only.</b> `["interpolate", …, ["zoom"], …]` is a scale rule, not a
    /// statement about the data, and storing it as a visual variable would claim the map says
    /// something about a field it never mentions.
    /// </remarks>
    /// <param name="expression">The paint value.</param>
    /// <returns>The field and its stops, or null.</returns>
    private static Slide? Continuous(JsonArray expression)
    {
        if (expression.Count < 5
            || (expression[0] as JsonValue)?.ToString() != "interpolate"
            || expression[2] is not JsonArray input
            || input.Count < 2
            || (input[0] as JsonValue)?.ToString() != "get")
        {
            return null;
        }

        List<double> stops = [];
        List<JsonNode?> outputs = [];

        for (int i = 3; i + 1 < expression.Count; i += 2)
        {
            if (Figure(expression[i]) is { } stop)
            {
                stops.Add(stop);
                outputs.Add(expression[i + 1]?.DeepClone());
            }
        }

        return stops.Count < 2
            ? null
            : new Slide(input[1]?.ToString() ?? string.Empty, stops, outputs);
    }

    /// <summary>A paint property that slides with a column.</summary>
    /// <param name="Field">The column.</param>
    /// <param name="Stops">Its values, in the order the style gave them.</param>
    /// <param name="Outputs">One per stop.</param>
    private sealed record Slide(
        string Field, IReadOnlyList<double> Stops, IReadOnlyList<JsonNode?> Outputs);

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
