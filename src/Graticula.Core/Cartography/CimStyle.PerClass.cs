using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Graticula.Cartography;

/// <summary>
/// The published form of the derived style: one style layer per class, with a legacy filter
/// and constant paint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists — [D-280](../../../docs/architecture-debt.md), measured by the owner on
/// 2026-09-29 in ArcGIS Pro 3.x.</b> Against the showcase, a layer whose generated style was one
/// `line` layer with a constant colour was drawn; a layer whose generated style was one `line`
/// layer with `line-color` as a `match` over its province field drew nothing, although Pro
/// fetched the style, the sprite and the tiles and the tiles held the features. Esri's own
/// vector basemap styles never classify inside the paint: they write one style layer per class
/// with a filter such as `["==", field, value]`, and constant paint. So that is what this
/// writes.
/// </para>
/// <para>
/// <b>Computed from the expression form by evaluating it, not written a second time.</b> Each
/// class's paint is what <see cref="StyleExpression"/> — the evaluator this server draws with —
/// answers for a feature of that class. A second writer would be a second reading of the
/// renderer, and the two would drift; this way the tile face and the raster faces can only
/// disagree where a filter and a paint expression mean different things, and those places are
/// written down below.
/// </para>
/// </remarks>
public static partial class CimStyle
{
    /// <summary>
    /// The most style layers one symbol layer is spread into before the expression form is
    /// published instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>256, and it is a bound on work rather than on maps.</b> A client evaluates every style
    /// layer's filter against every feature of its source layer, so spreading a renderer
    /// multiplies the client's work per feature by the number of classes. 256 admits the 81
    /// provinces of Turkey, a zoning scheme's land-use codes and a ten-band ramp across a
    /// twenty-class renderer; it refuses a class per district (973), which is a map nobody reads
    /// by its colours anyway. It is the same number ADR-054 withdrew as a bound on the
    /// <i>stored</i> document — withdrawn there because storing is cheap, and kept here because
    /// every client pays for every published layer on every feature it draws.
    /// </para>
    /// <para>
    /// <b>Past it, the style keeps its expressions and says so.</b> MapLibre draws a `match`;
    /// ArcGIS Pro draws nothing for one. Emitting thousands of style layers instead would trade
    /// a map one client cannot draw for a style every client draws slowly.
    /// </para>
    /// </remarks>
    public const int MostClassLayers = 256;

    /// <summary>How many steps a continuous variable is drawn in across its range.</summary>
    /// <remarks>
    /// <b>Eight across the range, and at least one per stop.</b> A two-stop colour ramp becomes
    /// eight bands, which reads as a graduated map; the twelve geometric stops of a proportional
    /// renderer and the nine a sampled colour ramp carries are one band each, because those
    /// stops were already placed where the curve bends. Every band is its own style layer, so
    /// this is also a multiplier on <see cref="MostClassLayers"/>.
    /// </remarks>
    private const int VariableSteps = 8;

    /// <summary>
    /// Spreads every expression over a feature's attribute into one style layer per class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A unique-value renderer</b> becomes one layer per class, filtered
    /// `["==", field, value]` (`["in", field, …]` for a class of several values, and
    /// `["all", ["==", f1, v1], ["==", f2, v2]]` for a renderer of several fields), and — when
    /// the renderer has a default symbol — one more beneath them filtered
    /// `["!in", field, every listed value]`, or `["none", …]` for several fields.
    /// </para>
    /// <para>
    /// <b>A value that reads as a number is matched as both.</b> CIM keeps every class value as
    /// a string and a vector tile keeps a number column as a number, and a legacy `==` is strict
    /// about the type — so `"7"` would match no road of class 7. The filter lists the text and
    /// the number, `["in", field, "7", 7]`. The `match` this replaces had the same strictness in
    /// every client and drew those roads in the fallback colour.
    /// </para>
    /// <para>
    /// <b>A class-breaks renderer</b> becomes one layer per class, filtered on the range the
    /// renderer means: `["&lt;=", field, b0]` for the first, `["all", ["&gt;", field, b0],
    /// ["&lt;=", field, b1]]` for the ones between, `["&gt;", field, bn]` for the last. Esri's breaks
    /// are upper-bound inclusive and the filters say so directly, which is exact where the
    /// `step` needed the next representable double. With a floor, the first class starts
    /// `["&gt;=", field, floor]` and a default layer takes `["&lt;", field, floor]`.
    /// </para>
    /// <para>
    /// <b>A continuous visual variable</b> — including every proportional renderer — becomes
    /// <see cref="VariableSteps"/> bands, each filtered `["all", ["&gt;=", f, lo], ["&lt;", f, hi]]`
    /// and painted with the value at its lower edge, plus a band below the first stop (which
    /// also takes a feature with no value, as this server's own renderer does) and one from the
    /// last stop up. <b>INFERRED, not measured:</b> Pro was measured failing a `match`, not an
    /// `interpolate` over a column, and Esri's styles use neither; bands are the choice that
    /// draws in Pro either way, and the stored document keeps the continuous variable. Lower
    /// edges rather than midpoints so that a style read back folds into an `interpolate` whose
    /// stops are exactly the band edges, and publishes the same bands again.
    /// </para>
    /// <para>
    /// <b>Drawing order: the first class on top, and a default at the bottom.</b> MapLibre
    /// draws a style's later layers over its earlier ones, so the classes are written last
    /// first. That is ArcGIS Pro's order when a renderer draws by class — the class at the top
    /// of its list is drawn on top — and the default is written first because it stands for
    /// everything nobody listed. The expression form had no class order at all: features drew
    /// in the order the tile held them. The stack of symbol layers is unchanged: every class of
    /// a casing is drawn before any class of the line above it, as before.
    /// </para>
    /// <para>
    /// <b>A symbol layer that does not classify is left exactly as it was</b>, so a simple
    /// renderer's style is byte for byte what it was before this existed. The one exception is a
    /// unique-value renderer with no default symbol: ArcGIS draws a value nobody listed with
    /// nothing at all, so every symbol layer of that renderer is filtered to the listed values,
    /// or an unlisted road would lose its fill and keep its casing.
    /// </para>
    /// </remarks>
    /// <param name="projection">What the renderer says.</param>
    /// <param name="expressions">The expression form of the same style.</param>
    /// <returns>The published style.</returns>
    private static DerivedStyle PerClass(CimProjection projection, DerivedStyle expressions)
    {
        if (projection.Heat is not null || projection.Dots is not null || projection.Pie is not null)
        {
            return expressions;
        }

        JsonArray layers = expressions.Style["layers"] as JsonArray ?? [];
        List<Cell>? classes = ClassCells(projection);
        List<(JsonObject Layer, bool Classed, SortedDictionary<string, List<double>> Slides)> reading = [];

        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            bool classed = false;
            SortedDictionary<string, List<double>> slides = new(StringComparer.Ordinal);

            foreach (KeyValuePair<string, JsonNode?> property in
                     layer["paint"] as JsonObject ?? [])
            {
                if (property.Value is not JsonArray expression
                    || (expression.ElementAtOrDefault(0) as JsonValue)?.ToString() is not { } head)
                {
                    continue;
                }

                if (head is "match" or "step")
                {
                    classed = true;
                }
                else if (head == "interpolate" && Continuous(expression) is { } over)
                {
                    if (!slides.TryGetValue(over.Field, out List<double>? stops))
                    {
                        slides[over.Field] = stops = [];
                    }

                    stops.AddRange(over.Stops);
                }
            }

            reading.Add((layer, classed, slides));
        }

        if (!reading.Any(r => r.Classed || r.Slides.Count > 0))
        {
            return expressions;
        }

        if (reading.Any(r => r.Classed) && classes is null)
        {
            return Unspread(
                expressions,
                "The style classifies in a way this server does not spread into filtered layers, "
                + "so the tile style keeps its expressions. MapLibre draws those; ArcGIS Pro draws "
                + "nothing for them (D-280).");
        }

        IReadOnlyList<string> classFields = projection.Fields.Count > 0
            ? projection.Fields
            : projection.Field is { } one ? [one] : [];

        if (reading.SelectMany(r => r.Slides.Keys).FirstOrDefault(f => classFields.Contains(f, StringComparer.Ordinal)) is { } shared)
        {
            // <b>Not attempted, and said so.</b> A variable over the field the classes already
            // cut would need its bands intersected with the classes' ranges, and a renderer
            // shaped like that is rare enough that getting the arithmetic wrong is the likelier
            // outcome than anybody needing it.
            return Unspread(
                expressions,
                $"A visual variable reads `{shared}`, which the renderer also classifies by. "
                + "This server does not spread the two together into filtered style layers, so the "
                + "tile style keeps its expressions: MapLibre draws them and ArcGIS Pro draws "
                + "nothing for them (D-280).");
        }

        bool everyLayer = projection.Kind == Cim.UniqueValue
            && projection.Default is null
            && reading.Any(r => r.Classed);

        List<string> losses = [.. expressions.Losses];
        JsonArray spread = [];

        foreach ((JsonObject layer, bool classed, SortedDictionary<string, List<double>> slides) in reading)
        {
            List<List<Cell>> axes = [];

            if (classed || everyLayer)
            {
                axes.Add(classes!);
            }

            foreach ((string field, List<double> stops) in slides)
            {
                axes.Add(Bands(field, stops));
            }

            if (axes.Count == 0)
            {
                spread.Add(layer.DeepClone());
                continue;
            }

            long count = axes.Aggregate(1L, (n, axis) => n * axis.Count);

            if (count > MostClassLayers)
            {
                return Unspread(
                    expressions,
                    $"Drawing this renderer without expressions takes {count:N0} style layers for "
                    + $"one symbol layer, more than the {MostClassLayers} this server writes, so the "
                    + "tile style classifies with expressions instead. MapLibre draws those; ArcGIS "
                    + "Pro draws nothing for them (measured 2026-09-29, D-280).");
            }

            foreach (List<Cell> combination in Product(axes))
            {
                spread.Add(Spread(layer, combination));
            }
        }

        foreach ((string field, List<double> stops) in reading
                     .SelectMany(r => r.Slides)
                     .GroupBy(s => s.Key, StringComparer.Ordinal)
                     .Select(g => (g.Key, g.SelectMany(s => s.Value).ToList())))
        {
            losses.Add(
                $"The tile face draws what varies with `{field}` in "
                + $"{Bands(field, stops).Count} bands, one style layer each, rather than "
                + "continuously, because a client such as ArcGIS Pro does not draw a style "
                + "expression over a feature's attribute (D-280). Each band takes the value at its "
                + "lower edge. The stored document and this server's own drawing keep the "
                + "continuous variable.");
        }

        if (everyLayer)
        {
            losses.Add(
                "The renderer has no default symbol, so on the tile face a feature whose value no "
                + "class lists is not drawn, as in ArcGIS. This server's own drawing paints it "
                + "with the first class's symbol.");
        }

        JsonObject style = new()
        {
            ["version"] = 8,
            ["layers"] = spread,
        };

        return new DerivedStyle(style, losses);
    }

    /// <summary>The expression form, published because the per-class form would not do.</summary>
    /// <param name="expressions">The expression form.</param>
    /// <param name="why">The sentence the read-back shows.</param>
    /// <returns>The same style with the reason added to its losses.</returns>
    private static DerivedStyle Unspread(DerivedStyle expressions, string why) =>
        expressions with { Losses = [.. expressions.Losses, why] };

    /// <summary>One style layer for one class, or one band, or one of each.</summary>
    /// <param name="layer">The expression form's layer.</param>
    /// <param name="combination">One cell from each axis.</param>
    /// <returns>The filtered layer with constant paint.</returns>
    private static JsonObject Spread(JsonObject layer, List<Cell> combination)
    {
        Dictionary<string, object?> attributes = new(StringComparer.Ordinal);
        List<JsonNode?> parts = [];

        foreach (Cell cell in combination)
        {
            foreach (KeyValuePair<string, object?> attribute in cell.Attributes)
            {
                attributes[attribute.Key] = attribute.Value;
            }

            if (cell.Filter is not null)
            {
                parts.Add(cell.Filter.DeepClone());
            }
        }

        string suffix = string.Join(
            "-", combination.Select(c => c.Suffix).Where(s => s.Length > 0));

        JsonObject one = new()
        {
            ["id"] = suffix.Length == 0
                ? (string?)layer["id"]
                : $"{(string?)layer["id"]}-{suffix}",
        };

        foreach (KeyValuePair<string, JsonNode?> property in layer)
        {
            if (property.Key is not ("id" or "paint"))
            {
                one[property.Key] = property.Value?.DeepClone();
            }
        }

        if (parts.Count > 0)
        {
            one["filter"] = parts.Count == 1 ? parts[0] : new JsonArray(["all", .. parts]);
        }

        StyleExpression.Context context = new(attributes, 0);
        JsonObject paint = [];

        foreach (KeyValuePair<string, JsonNode?> property in layer["paint"] as JsonObject ?? [])
        {
            if (Settled(property.Key, property.Value, context) is { } value)
            {
                paint[property.Key] = value;
            }
        }

        one["paint"] = paint;

        return one;
    }

    /// <summary>One paint value, as the constant it is for one class.</summary>
    /// <remarks>
    /// <b>Evaluated by the renderer's own evaluator</b>, so the constant a client is given for a
    /// class is exactly the value this server paints that class with.
    /// </remarks>
    /// <param name="property">The paint property, which says how to write the answer.</param>
    /// <param name="value">Its value in the expression form.</param>
    /// <param name="context">A feature of the class.</param>
    /// <returns>The constant.</returns>
    private static JsonNode? Settled(string property, JsonNode? value, in StyleExpression.Context context)
    {
        // A constant, or an array whose head is not an operator (a dash pattern), is data.
        if (value is not JsonArray expression
            || expression.Count == 0
            || expression[0] is not JsonValue head
            || !head.TryGetValue(out string? _))
        {
            return value?.DeepClone();
        }

        object? answer = StyleExpression.Compile(expression).Evaluate(context);

        if (StyleExpression.Text(answer) is { } text && answer is string)
        {
            return property.EndsWith("-color", StringComparison.Ordinal)
                && Rgba.TryParse(text, out Rgba colour)
                    ? Hex(colour)
                    : JsonValue.Create(text);
        }

        if (answer is bool flag)
        {
            return JsonValue.Create(flag);
        }

        // <b>Rounded as the expression form rounds</b>: sizes to three places, as `Pixels` does,
        // and everything else to four, as `Alpha` does. A band's value is computed rather than
        // copied, and a width of 2.6666666666666665 in a published style is noise.
        return StyleExpression.AsNumber(answer) is { } number
            ? Num(Math.Round(
                number,
                property.EndsWith("-width", StringComparison.Ordinal)
                    || property.EndsWith("-radius", StringComparison.Ordinal)
                    ? 3
                    : 4,
                MidpointRounding.AwayFromZero))
            : null;
    }

    /// <summary>Every combination of one cell from each axis, the first axis outermost.</summary>
    /// <param name="axes">The axes.</param>
    /// <returns>The combinations, in drawing order.</returns>
    private static IEnumerable<List<Cell>> Product(List<List<Cell>> axes)
    {
        IEnumerable<List<Cell>> combinations = [[]];

        foreach (List<Cell> axis in axes)
        {
            List<Cell> captured = axis;
            combinations = combinations.SelectMany(
                prefix => captured.Select(cell => new List<Cell>(prefix) { cell }));
        }

        return combinations;
    }

    /// <summary>The class axis of a classified renderer, bottom first.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <returns>The cells, or null when the renderer does not classify.</returns>
    private static List<Cell>? ClassCells(CimProjection projection) =>
        projection.Field is null
            ? null
            : projection.Kind switch
            {
                Cim.UniqueValue => UniqueCells(projection),
                Cim.ClassBreaks => RangeCells(projection),
                _ => null,
            };

    /// <summary>One cell per unique-value class, and one for the default symbol.</summary>
    /// <param name="projection">What the renderer says.</param>
    /// <returns>The cells, bottom first.</returns>
    private static List<Cell> UniqueCells(CimProjection projection)
    {
        IReadOnlyList<string> fields = projection.Fields.Count > 0
            ? projection.Fields
            : [projection.Field!];

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<IReadOnlyList<string>> everyTuple = [];
        List<Cell> listed = [];

        for (int i = 0; i < projection.Classes.Count; i++)
        {
            CimClass one = projection.Classes[i];
            List<IReadOnlyList<string>> tuples = [];

            for (int k = 0; k < one.Values.Count; k++)
            {
                // <b>The first class that claims a value keeps it</b>, as in the `match`, which
                // refuses a label twice; a second layer for the same value would draw it twice.
                if (!seen.Add(one.Values[k]))
                {
                    continue;
                }

                if (k < one.Tuples.Count)
                {
                    tuples.Add(one.Tuples[k]);
                }
                else if (fields.Count == 1)
                {
                    tuples.Add([one.Values[k]]);
                }
                else
                {
                    tuples.Add(one.Values[k].Split(projection.Delimiter, fields.Count));
                }
            }

            if (tuples.Count == 0)
            {
                continue;
            }

            everyTuple.AddRange(tuples);

            Dictionary<string, object?> feature = new(StringComparer.Ordinal);

            for (int f = 0; f < fields.Count; f++)
            {
                feature[fields[f]] = f < tuples[0].Count ? tuples[0][f] : string.Empty;
            }

            listed.Add(new Cell(
                i.ToString(CultureInfo.InvariantCulture), Listed(fields, tuples), feature));
        }

        List<Cell> cells = [];

        if (projection.Default is not null && everyTuple.Count > 0)
        {
            cells.Add(new Cell(
                "other",
                Unlisted(fields, everyTuple),
                new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        listed.Reverse();
        cells.AddRange(listed);

        return cells;
    }

    /// <summary>The filter a class of listed values draws with.</summary>
    /// <param name="fields">The fields classified by.</param>
    /// <param name="tuples">One value per field, for each value the class lists.</param>
    /// <returns>The filter.</returns>
    private static JsonArray Listed(IReadOnlyList<string> fields, List<IReadOnlyList<string>> tuples)
    {
        if (fields.Count == 1)
        {
            List<string> values = [.. tuples.Select(t => t[0])];

            return values.Count == 1 && Numeric(values[0]) is null
                ? new JsonArray("==", fields[0], values[0])
                : new JsonArray(["in", fields[0], .. Twinned(values)]);
        }

        return tuples.Count == 1
            ? Tuple(fields, tuples[0])
            : new JsonArray(["any", .. tuples.Select(t => (JsonNode?)Tuple(fields, t))]);
    }

    /// <summary>The filter the default symbol draws with: every value no class lists.</summary>
    /// <param name="fields">The fields classified by.</param>
    /// <param name="tuples">Every value every class lists.</param>
    /// <returns>The filter.</returns>
    private static JsonArray Unlisted(IReadOnlyList<string> fields, List<IReadOnlyList<string>> tuples) =>
        fields.Count == 1
            ? new JsonArray(["!in", fields[0], .. Twinned([.. tuples.Select(t => t[0])])])
            : new JsonArray(["none", .. tuples.Select(t => (JsonNode?)Tuple(fields, t))]);

    /// <summary>`["all", ["==", f1, v1], ["==", f2, v2]]`, for one value of several fields.</summary>
    /// <param name="fields">The fields.</param>
    /// <param name="tuple">One value each.</param>
    /// <returns>The filter.</returns>
    private static JsonArray Tuple(IReadOnlyList<string> fields, IReadOnlyList<string> tuple) =>
        new(["all", .. fields.Select((f, i) =>
        {
            string value = i < tuple.Count ? tuple[i] : string.Empty;

            return (JsonNode?)(Numeric(value) is null
                ? new JsonArray("==", f, value)
                : new JsonArray(["in", f, .. Twinned([value])]));
        })]);

    /// <summary>Each value, followed by the number it reads as when it reads as one.</summary>
    /// <param name="values">The values, as CIM keeps them.</param>
    /// <returns>The filter's list of values.</returns>
    private static IEnumerable<JsonNode?> Twinned(IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            yield return JsonValue.Create(value);

            if (Numeric(value) is { } number)
            {
                yield return Num(number);
            }
        }
    }

    /// <summary>The number a class value reads as, or null when it is not one.</summary>
    /// <remarks>
    /// <b>Strictly a number as written</b>: no surrounding space and nothing infinite, so that a
    /// code such as <c>" 7"</c> stays text and <c>"Infinity"</c> is not matched against a
    /// column of numbers.
    /// </remarks>
    /// <param name="value">The value.</param>
    /// <returns>The number, or null.</returns>
    private static double? Numeric(string value) =>
        value.Length > 0
        && value.Trim().Length == value.Length
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
        && double.IsFinite(n)
            ? n
            : null;

    /// <summary>One cell per range of a class-breaks renderer, and one below its floor.</summary>
    /// <remarks>
    /// <b>The ranges are walked the way <see cref="Step"/> walks them</b>, including its habit
    /// of skipping a class with no upper bound, so that each filter covers exactly the values the
    /// `step` gives that class.
    /// </remarks>
    /// <param name="projection">What the renderer says.</param>
    /// <returns>The cells, bottom first.</returns>
    private static List<Cell> RangeCells(CimProjection projection)
    {
        string field = projection.Field!;
        List<(double? Low, bool Closed, double? High, int Class)> ranges = [];
        double? low = projection.Floor;
        bool closed = projection.Floor is not null;
        int current = 0;

        for (int i = 0; i < projection.Classes.Count - 1; i++)
        {
            if (projection.Classes[i].UpperBound is not { } bound)
            {
                continue;
            }

            ranges.Add((low, closed, bound, current));
            low = bound;
            closed = false;
            current = i + 1;
        }

        ranges.Add((low, closed, null, current));

        List<Cell> listed = [];

        foreach ((double? lo, bool inclusive, double? hi, int index) in ranges)
        {
            // An empty range — two breaks at one value — has nothing to draw.
            if (lo is { } a && hi is { } b && (b < a || (b == a && !inclusive)))
            {
                continue;
            }

            List<JsonNode?> parts = [];

            if (lo is { } bottom)
            {
                parts.Add(new JsonArray(inclusive ? ">=" : ">", field, Num(bottom)));
            }

            if (hi is { } top)
            {
                parts.Add(new JsonArray("<=", field, Num(top)));
            }

            // <b>A value inside the range, for the evaluator to classify.</b> The upper bound
            // is inside because Esri's breaks include it; with no upper bound, the floor itself
            // or the next double above the last break.
            double inside = hi
                ?? (lo is { } l ? (inclusive ? l : Math.BitIncrement(l)) : 0);

            listed.Add(new Cell(
                index.ToString(CultureInfo.InvariantCulture),
                parts.Count switch
                {
                    0 => null,
                    1 => (JsonArray)parts[0]!,
                    _ => new JsonArray(["all", .. parts]),
                },
                new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = inside }));
        }

        List<Cell> cells = [];

        if (projection.Floor is { } floor)
        {
            cells.Add(new Cell(
                "other",
                new JsonArray("<", field, Num(floor)),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [field] = Math.BitDecrement(floor),
                }));
        }

        listed.Reverse();
        cells.AddRange(listed);

        return cells;
    }

    /// <summary>The bands a continuous variable is drawn in, lowest first.</summary>
    /// <param name="field">The column it reads.</param>
    /// <param name="stops">Every stop over that column in one style layer.</param>
    /// <returns>The cells.</returns>
    private static List<Cell> Bands(string field, List<double> stops)
    {
        List<double> at = [.. stops.Distinct().Order()];

        if (at.Count < 2)
        {
            return
            [
                new Cell(
                    string.Empty,
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [field] = at.Count == 0 ? 0 : at[0],
                    }),
            ];
        }

        // <b>Below the first stop, and a feature with no value.</b> The expression form clamps a
        // value under the first stop to it, and this server's evaluator reads a missing value as
        // nought and clamps that too; a vector tile has no nulls, only absent attributes, so
        // `!has` is where a null lands.
        List<Cell> cells =
        [
            new Cell(
                "b0",
                new JsonArray("any", new JsonArray("!has", field), new JsonArray("<", field, Num(at[0]))),
                new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = at[0] }),
        ];

        int per = Math.Max(1, (int)Math.Ceiling((double)VariableSteps / (at.Count - 1)));

        for (int k = 0; k < at.Count - 1; k++)
        {
            for (int j = 0; j < per; j++)
            {
                double lo = j == 0 ? at[k] : at[k] + ((at[k + 1] - at[k]) * j / per);
                double hi = j == per - 1 ? at[k + 1] : at[k] + ((at[k + 1] - at[k]) * (j + 1) / per);

                cells.Add(new Cell(
                    string.Create(CultureInfo.InvariantCulture, $"b{cells.Count}"),
                    new JsonArray(
                        "all",
                        new JsonArray(">=", field, Num(lo)),
                        new JsonArray("<", field, Num(hi))),
                    new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = lo }));
            }
        }

        cells.Add(new Cell(
            string.Create(CultureInfo.InvariantCulture, $"b{cells.Count}"),
            new JsonArray(">=", field, Num(at[^1])),
            new Dictionary<string, object?>(StringComparer.Ordinal) { [field] = at[^1] }));

        return cells;
    }

    /// <summary>One class, or one band: its id suffix, its filter and a feature inside it.</summary>
    /// <param name="Suffix">What is appended to the style layer's id.</param>
    /// <param name="Filter">The legacy filter, or null for a cell that is everything.</param>
    /// <param name="Attributes">A feature inside the cell, for the evaluator.</param>
    private sealed record Cell(
        string Suffix, JsonArray? Filter, IReadOnlyDictionary<string, object?> Attributes);
}
