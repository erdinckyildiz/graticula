using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Graticula.Cartography;

/// <summary>
/// Reads the per-class form back into the expression form it was spread from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The inverse of <see cref="PerClass"/>, and only of it.</b> A style this server published
/// has to store again as the renderer it came from: somebody downloads `root.json`, changes a
/// colour and PUTs it, and a server that refused its own output would be the least defensible
/// refusal it makes. So the filters <see cref="PerClass"/> writes are recognised here, and
/// nothing else is — a style with any other filter is left alone, and the write path refuses it
/// exactly as it did before (Q-128).
/// </para>
/// <para>
/// <b>Folded into expressions, not into CIM directly.</b> <see cref="FromMapLibre"/> already
/// reads a `match`, a `step` and an `interpolate` over a column into a renderer, a default
/// symbol and visual variables, and is tested for it; a second reader from filters would be a
/// second answer to the same question.
/// </para>
/// </remarks>
public static partial class CimStyle
{
    /// <summary>
    /// The expression form of a per-class style, with what the filters said that an expression
    /// cannot.
    /// </summary>
    /// <param name="Style">The style, one layer per symbol layer again.</param>
    /// <param name="HasDefault">
    /// Whether a unique-value renderer had a layer for the values no class lists. A `match`
    /// always ends in an otherwise, so this is the one thing about a renderer the folded form
    /// cannot carry.
    /// </param>
    /// <param name="Floor">The class-breaks floor, when there was a layer below it.</param>
    internal sealed record Collapsed(JsonObject Style, bool HasDefault, double? Floor);

    /// <summary>The delimiters a folded multi-field `match` may join with, in preference order.</summary>
    private static readonly string[] Delimiters = [", ", " | ", "\u001f"];

    /// <summary>
    /// Folds the per-class form back into expressions, or answers null when the style is not
    /// that form.
    /// </summary>
    /// <remarks>
    /// <b>All or nothing.</b> One filter this does not recognise, or one paint property whose
    /// values are not a function of the class or of a band, and the whole style is left as it
    /// came — half a style folded is a renderer nobody wrote.
    /// </remarks>
    /// <param name="style">The style.</param>
    /// <returns>The folded style, or null.</returns>
    internal static Collapsed? Collapse(JsonObject style)
    {
        if (style["layers"] is not JsonArray layers
            || !layers.OfType<JsonObject>().Any(l => l["filter"] is not null))
        {
            return null;
        }

        List<(JsonObject Layer, List<JsonArray>? Atoms)> read = [];
        HashSet<string> banded = new(StringComparer.Ordinal);

        foreach (JsonObject layer in layers.OfType<JsonObject>())
        {
            if (layer["filter"] is null)
            {
                read.Add((layer, null));
                continue;
            }

            List<JsonArray> atoms = [];

            if (!Flatten(layer["filter"], atoms))
            {
                return null;
            }

            foreach (JsonArray atom in atoms)
            {
                if (Below(atom) is { } below)
                {
                    banded.Add(below.Field);
                }
            }

            read.Add((layer, atoms));
        }

        // <b>Levels are runs of one layer type whose cells do not repeat.</b> A casing's classes
        // are followed by the road's classes, and the first class seen twice is where the second
        // symbol layer begins.
        List<List<(JsonObject Layer, Cut? Cut)>> levels = [];
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach ((JsonObject layer, List<JsonArray>? atoms) in read)
        {
            if (atoms is null)
            {
                levels.Add([(layer, null)]);
                keys.Clear();
                continue;
            }

            if (Cut.Of(atoms, banded) is not { } cut)
            {
                return null;
            }

            List<(JsonObject Layer, Cut? Cut)>? current = levels.Count > 0 ? levels[^1] : null;

            bool joins = current is not null
                && current[0].Cut is { } first
                && (string?)current[0].Layer["type"] == (string?)layer["type"]
                && (string?)current[0].Layer["source-layer"] == (string?)layer["source-layer"]
                && first.Shape == cut.Shape
                && !keys.Contains(cut.Key);

            if (!joins)
            {
                levels.Add([]);
                keys.Clear();
            }

            levels[^1].Add((layer, cut));
            keys.Add(cut.Key);
        }

        JsonArray folded = [];
        bool hasDefault = false;
        double? floor = null;

        foreach (List<(JsonObject Layer, Cut? Cut)> level in levels)
        {
            if (level[0].Cut is null)
            {
                folded.Add(level[0].Layer.DeepClone());
                continue;
            }

            List<(JsonObject Layer, Cut Cut)> cells = [.. level.Select(c => (c.Layer, c.Cut!))];

            if (Fold(cells) is not { } one)
            {
                return null;
            }

            folded.Add(one);
            hasDefault |= cells.Any(c => c.Cut.Kind == Cut.Other);

            if (cells.FirstOrDefault(c => c.Cut.Kind == Cut.Below).Cut is { } below)
            {
                floor = below.High;
            }
        }

        JsonObject collapsed = [];

        foreach (KeyValuePair<string, JsonNode?> property in style)
        {
            collapsed[property.Key] = property.Key == "layers" ? folded : property.Value?.DeepClone();
        }

        return new Collapsed(collapsed, hasDefault, floor);
    }

    /// <summary>One level's cells, folded into one style layer.</summary>
    /// <param name="cells">The level's layers and what each one's filter says.</param>
    /// <returns>The style layer, or null when a property is not a function of one axis.</returns>
    private static JsonObject? Fold(List<(JsonObject Layer, Cut Cut)> cells)
    {
        JsonObject first = cells[0].Layer;
        JsonObject one = new()
        {
            ["id"] = Common(cells.Select(c => (string?)c.Layer["id"] ?? string.Empty))
                is { Length: > 0 } stem
                    ? stem
                    : (string?)first["type"] ?? "layer",
        };

        foreach (KeyValuePair<string, JsonNode?> property in first)
        {
            if (property.Key is not ("id" or "filter" or "paint" or "layout"))
            {
                one[property.Key] = property.Value?.DeepClone();
            }
        }

        // <b>Layout is folded as paint is, since ADR-099.</b> A picture's class is its `icon-image`,
        // which is a layout property; copying the first cell's layout, as this did, folded every
        // class of an icon layer into the first class's picture.
        if (cells.Any(c => c.Layer["layout"] is JsonObject))
        {
            if (FoldBlock(cells, "layout") is not { } layout)
            {
                return null;
            }

            one["layout"] = layout;
        }

        if (FoldBlock(cells, "paint") is not { } paint)
        {
            return null;
        }

        one["paint"] = paint;

        return one;
    }

    /// <summary>One block of a level's cells — the paint or the layout — folded into one.</summary>
    /// <param name="cells">The level's layers and what each one's filter says.</param>
    /// <param name="name">`paint` or `layout`.</param>
    /// <returns>The block, or null when a property is not a function of one axis.</returns>
    private static JsonObject? FoldBlock(List<(JsonObject Layer, Cut Cut)> cells, string name)
    {
        List<string> properties = [.. cells
            .SelectMany(c => (c.Layer[name] as JsonObject ?? []).Select(p => p.Key))
            .Distinct(StringComparer.Ordinal)];

        JsonObject paint = [];

        foreach (string property in properties)
        {
            List<JsonNode?> values = [.. cells.Select(c => (c.Layer[name] as JsonObject)?[property])];

            if (values.Select(v => v?.ToJsonString()).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                paint[property] = values[0]?.DeepClone();
                continue;
            }

            // <b>An array value is wrapped before it becomes an output</b>: a `match` or a `step`
            // reads a bare array as an expression, and `icon-offset` is a pair of numbers.
            values = [.. values.Select(v => v is JsonArray { Count: > 0 } array
                && array[0] is JsonValue head && !head.TryGetValue(out string? _)
                    ? new JsonArray("literal", v.DeepClone())
                    : v)];

            JsonNode? expression = null;

            foreach (string field in cells[0].Cut.Bands.Keys)
            {
                if (Depends(cells, values, c => c.Bands[field].ToString()))
                {
                    expression = Interpolated(cells, values, field);
                    break;
                }
            }

            if (expression is null && cells[0].Cut.Kind != Cut.None
                && Depends(cells, values, c => c.ClassKey))
            {
                expression = cells[0].Cut.Kind is Cut.Listed or Cut.Other
                    ? Matched(cells, values)
                    : Stepped(cells, values);
            }

            if (expression is null)
            {
                return null;
            }

            paint[property] = expression;
        }

        return paint;
    }

    /// <summary>Whether a property's value is decided by one part of the cell alone.</summary>
    /// <param name="cells">The level.</param>
    /// <param name="values">The property's value in each, in the same order.</param>
    /// <param name="by">The part.</param>
    /// <returns>True when cells that agree on the part agree on the value.</returns>
    private static bool Depends(
        List<(JsonObject Layer, Cut Cut)> cells, List<JsonNode?> values, Func<Cut, string> by)
    {
        Dictionary<string, string?> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < cells.Count; i++)
        {
            string key = by(cells[i].Cut);
            string? value = values[i]?.ToJsonString();

            if (seen.TryGetValue(key, out string? before) && before != value)
            {
                return false;
            }

            seen[key] = value;
        }

        return true;
    }

    /// <summary>An `interpolate` whose stops are the bands' lower edges.</summary>
    /// <param name="cells">The level.</param>
    /// <param name="values">The property's value in each.</param>
    /// <param name="field">The column the bands cut.</param>
    /// <returns>The expression, or null when the bands give fewer than two stops.</returns>
    private static JsonArray? Interpolated(
        List<(JsonObject Layer, Cut Cut)> cells, List<JsonNode?> values, string field)
    {
        SortedDictionary<double, JsonNode?> stops = [];

        for (int i = 0; i < cells.Count; i++)
        {
            (double? low, double? high) = cells[i].Cut.Bands[field];

            // <b>The band below the first stop is painted with the first stop's value</b>, and
            // the band starting at that stop says the same thing; either names the stop.
            if ((low ?? high) is { } at)
            {
                stops[at] = values[i]?.DeepClone();
            }
        }

        if (stops.Count < 2)
        {
            return null;
        }

        JsonArray expression = ["interpolate", new JsonArray("linear"), new JsonArray("get", field)];

        foreach ((double at, JsonNode? value) in stops)
        {
            expression.Add(Num(at));
            expression.Add(value);
        }

        return expression;
    }

    /// <summary>A `match` over the fields the classes list.</summary>
    /// <param name="cells">The level.</param>
    /// <param name="values">The property's value in each.</param>
    /// <returns>The expression.</returns>
    private static JsonArray? Matched(List<(JsonObject Layer, Cut Cut)> cells, List<JsonNode?> values)
    {
        // <b>The renderer's order is the reverse of the drawing order</b>: <see cref="PerClass"/>
        // writes the first class last so that it is drawn on top.
        List<(Cut Cut, JsonNode? Value)> classes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        JsonNode? other = null;
        bool hasOther = false;

        for (int i = 0; i < cells.Count; i++)
        {
            Cut cut = cells[i].Cut;

            if (cut.Kind == Cut.Other)
            {
                other = values[i];
                hasOther = true;
            }
            else if (seen.Add(cut.ClassKey))
            {
                classes.Add((cut, values[i]));
            }
        }

        if (classes.Count == 0)
        {
            return null;
        }

        classes.Reverse();

        List<string> fields = classes[0].Cut.Fields;

        if (classes.Any(c => !c.Cut.Fields.SequenceEqual(fields, StringComparer.Ordinal)))
        {
            return null;
        }

        // <b>A delimiter no value contains</b>, so that the joined key splits back into the
        // values it was joined from. The renderer's own delimiter is not in the per-class form;
        // it only ever mattered for joining, and any unambiguous one joins the same classes.
        string delimiter = Delimiters.First(d =>
            d == Delimiters[^1] || !classes.Any(c => c.Cut.Tuples.Any(t => t.Any(v => v.Contains(d, StringComparison.Ordinal)))));

        JsonArray input = new("get", fields[0]);

        if (fields.Count > 1)
        {
            input = ["concat"];

            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    input.Add(delimiter);
                }

                input.Add(new JsonArray("to-string", new JsonArray("get", fields[i])));
            }
        }

        JsonArray expression = ["match", input];

        foreach ((Cut cut, JsonNode? value) in classes)
        {
            List<string> keys = [.. cut.Tuples.Select(t => string.Join(delimiter, t))];

            expression.Add(keys.Count == 1
                ? JsonValue.Create(keys[0])
                : new JsonArray([.. keys.Select(k => (JsonNode?)k)]));
            expression.Add(value?.DeepClone());
        }

        expression.Add((hasOther ? other : classes[0].Value)?.DeepClone());

        return expression;
    }

    /// <summary>A `step` over the classes' ranges, the inverse of <see cref="RangeCells"/>.</summary>
    /// <param name="cells">The level.</param>
    /// <param name="values">The property's value in each.</param>
    /// <returns>The expression, or null when the ranges do not start where a `step` starts.</returns>
    private static JsonArray? Stepped(List<(JsonObject Layer, Cut Cut)> cells, List<JsonNode?> values)
    {
        List<(Cut Cut, JsonNode? Value)> ranges = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < cells.Count; i++)
        {
            if (seen.Add(cells[i].Cut.ClassKey))
            {
                ranges.Add((cells[i].Cut, values[i]));
            }
        }

        // Below the floor first, then by where each range starts.
        ranges.Sort((a, b) =>
            (a.Cut.Kind == Cut.Below ? double.NegativeInfinity : a.Cut.Low ?? double.MinValue)
                .CompareTo(b.Cut.Kind == Cut.Below ? double.NegativeInfinity : b.Cut.Low ?? double.MinValue));

        string field = ranges[0].Cut.Fields[0];
        JsonArray expression = ["step", new JsonArray("get", field)];

        for (int i = 0; i < ranges.Count; i++)
        {
            (Cut cut, JsonNode? value) = ranges[i];

            if (i == 0)
            {
                // <b>The first output has no stop</b>, so the first range must reach down to
                // nothing: the layer below a floor, or a first class with no lower bound.
                if (cut.Kind != Cut.Below && cut.Low is not null)
                {
                    return null;
                }

                expression.Add(value?.DeepClone());
                continue;
            }

            if (cut.Low is not { } low)
            {
                return null;
            }

            // Exclusive lower bounds step at the next double, as `Step` writes them.
            expression.Add(Num(cut.LowClosed ? low : Math.BitIncrement(low)));
            expression.Add(value?.DeepClone());
        }

        return expression;
    }

    /// <summary>The longest prefix every id shares, without a trailing dash.</summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The prefix.</returns>
    private static string Common(IEnumerable<string> ids)
    {
        string? prefix = null;

        foreach (string id in ids)
        {
            if (prefix is null)
            {
                prefix = id;
                continue;
            }

            int n = 0;

            while (n < prefix.Length && n < id.Length && prefix[n] == id[n])
            {
                n++;
            }

            prefix = prefix[..n];
        }

        return (prefix ?? string.Empty).TrimEnd('-');
    }

    /// <summary>A filter's conditions, with every `all` opened.</summary>
    /// <param name="node">The filter.</param>
    /// <param name="into">Collects the conditions.</param>
    /// <returns>False when the filter is not an array of arrays with operators.</returns>
    private static bool Flatten(JsonNode? node, List<JsonArray> into)
    {
        if (node is not JsonArray array
            || array.Count == 0
            || array[0] is not JsonValue head
            || !head.TryGetValue(out string? op))
        {
            return false;
        }

        if (op != "all")
        {
            into.Add(array);
            return true;
        }

        for (int i = 1; i < array.Count; i++)
        {
            if (!Flatten(array[i], into))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads `["any", ["!has", f], ["&lt;", f, s]]`, the band below a variable's first stop.</summary>
    /// <param name="atom">A condition.</param>
    /// <returns>The field and the stop, or null.</returns>
    private static (string Field, double Stop)? Below(JsonArray atom)
    {
        if (atom.Count != 3
            || (atom[0] as JsonValue)?.ToString() != "any"
            || atom[1] is not JsonArray { Count: 2 } missing
            || (missing[0] as JsonValue)?.ToString() != "!has"
            || atom[2] is not JsonArray { Count: 3 } under
            || (under[0] as JsonValue)?.ToString() != "<"
            || Name(missing[1]) is not { } field
            || Name(under[1]) != field
            || Figure(under[2]) is not { } stop)
        {
            return null;
        }

        return (field, stop);
    }

    /// <summary>A field name out of a filter, which is a plain string in the legacy syntax.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The name, or null.</returns>
    private static string? Name(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? name) ? name : null;

    /// <summary>
    /// A filter's listed values as the strings CIM keeps, without the numeric twins
    /// <see cref="Twinned"/> adds.
    /// </summary>
    /// <param name="atom">The condition.</param>
    /// <param name="from">Where the values start.</param>
    /// <returns>The values, or null when one is neither text, a number nor a boolean.</returns>
    private static List<string>? Values(JsonArray atom, int from)
    {
        List<string> texts = [];
        List<double> numbers = [];

        for (int i = from; i < atom.Count; i++)
        {
            if (atom[i] is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue(out string? text))
            {
                texts.Add(text);
            }
            else if (value.TryGetValue(out bool flag))
            {
                texts.Add(flag ? "true" : "false");
            }
            else if (Figure(value) is { } number)
            {
                numbers.Add(number);
            }
            else
            {
                return null;
            }
        }

        foreach (double number in numbers)
        {
            // <b>A number that twins a listed text is dropped</b>; one that stands alone was
            // written by somebody and is kept, as the text CIM would store it as.
            if (!texts.Any(t => Numeric(t) == number))
            {
                texts.Add(number.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return texts;
    }

    /// <summary>What one layer's filter says, read into the axes <see cref="PerClass"/> writes.</summary>
    private sealed class Cut
    {
        /// <summary>No class: the layer is one band, or several, of a variable.</summary>
        public const string None = "none";

        /// <summary>A unique-value class.</summary>
        public const string Listed = "listed";

        /// <summary>A unique-value default: everything no class lists.</summary>
        public const string Other = "other";

        /// <summary>A class-breaks range.</summary>
        public const string Range = "range";

        /// <summary>Everything below a class-breaks floor.</summary>
        public const string Below = "below";

        /// <summary>Which of the five this is.</summary>
        public string Kind { get; private set; } = None;

        /// <summary>The fields a unique-value class tests, or the one a range cuts.</summary>
        public List<string> Fields { get; } = [];

        /// <summary>One value per field, for each value the class lists.</summary>
        public List<List<string>> Tuples { get; } = [];

        /// <summary>A range's lower bound.</summary>
        public double? Low { get; private set; }

        /// <summary>Whether the lower bound is in the range.</summary>
        public bool LowClosed { get; private set; }

        /// <summary>A range's upper bound, which is in it; or the floor, below it.</summary>
        public double? High { get; private set; }

        /// <summary>Each variable's band: its lower bound, in it, and its upper, not.</summary>
        public SortedDictionary<string, (double? Low, double? High)> Bands { get; } =
            new(StringComparer.Ordinal);

        /// <summary>What decides the class, as text to compare.</summary>
        public string ClassKey => string.Create(
            CultureInfo.InvariantCulture,
            $"{Kind}|{string.Join("\u001e", Fields)}|{string.Join("\u001e", Tuples.Select(t => string.Join("\u001f", t)))}|{Low:R}|{LowClosed}|{High:R}");

        /// <summary>What decides the cell: the class and every band.</summary>
        public string Key => ClassKey + "#" + string.Join(
            ";", Bands.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Key}:{b.Value.Low:R}:{b.Value.High:R}")));

        /// <summary>What every cell of one level shares: whether it classifies, and by what.</summary>
        /// <remarks>
        /// The fields are not part of it: a default's filter names every value and a class's only
        /// its own, and whether the classes of one level agree on their fields is checked when
        /// they are folded.
        /// </remarks>
        public string Shape => (Kind is Listed or Other ? "unique" : Kind is Range or Below ? "range" : "none")
            + "|" + string.Join(";", Bands.Keys);

        /// <summary>Reads one layer's conditions.</summary>
        /// <param name="atoms">The filter, with every `all` opened.</param>
        /// <param name="banded">The fields some layer bands a variable over.</param>
        /// <returns>The cut, or null when a condition is not one <see cref="PerClass"/> writes.</returns>
        public static Cut? Of(List<JsonArray> atoms, HashSet<string> banded)
        {
            Cut cut = new();
            List<(string Field, List<string> Values)> equal = [];
            List<List<(string Field, string Value)>>? any = null;
            List<List<(string Field, string Value)>>? none = null;
            (string Field, List<string> Values)? outside = null;
            string? ranged = null;
            double? below = null;

            foreach (JsonArray atom in atoms)
            {
                string op = (atom[0] as JsonValue)?.ToString() ?? string.Empty;

                switch (op)
                {
                    case "==":
                    case "in":
                    {
                        if (atom.Count < 3
                            || Name(atom[1]) is not { } field
                            || banded.Contains(field)
                            || Values(atom, 2) is not { Count: > 0 } values
                            || (op == "==" && atom.Count != 3))
                        {
                            return null;
                        }

                        equal.Add((field, values));
                        break;
                    }

                    case "!in":
                    {
                        if (atom.Count < 3 || Name(atom[1]) is not { } field || Values(atom, 2) is not { } values)
                        {
                            return null;
                        }

                        outside = (field, values);
                        break;
                    }

                    case "none":
                    case "any":
                    {
                        if (op == "any" && CimStyle.Below(atom) is { } band)
                        {
                            cut.Bands[band.Field] = (null, band.Stop);
                            break;
                        }

                        List<List<(string Field, string Value)>> tuples = [];

                        for (int i = 1; i < atom.Count; i++)
                        {
                            if (Tuple(atom[i], banded) is not { } tuple)
                            {
                                return null;
                            }

                            tuples.Add(tuple);
                        }

                        if (tuples.Count == 0)
                        {
                            return null;
                        }

                        if (op == "any")
                        {
                            any = tuples;
                        }
                        else
                        {
                            none = tuples;
                        }

                        break;
                    }

                    case "<":
                    case "<=":
                    case ">":
                    case ">=":
                    {
                        if (atom.Count != 3 || Name(atom[1]) is not { } field || Figure(atom[2]) is not { } n)
                        {
                            return null;
                        }

                        if (banded.Contains(field))
                        {
                            (double? low, double? high) = cut.Bands.TryGetValue(field, out var had) ? had : (null, null);

                            switch (op)
                            {
                                case ">=":
                                    low = n;
                                    break;
                                case "<":
                                    high = n;
                                    break;
                                default:
                                    return null;
                            }

                            cut.Bands[field] = (low, high);
                            break;
                        }

                        if (ranged is not null && ranged != field)
                        {
                            return null;
                        }

                        ranged = field;

                        switch (op)
                        {
                            case ">":
                                cut.Low = n;
                                cut.LowClosed = false;
                                break;
                            case ">=":
                                cut.Low = n;
                                cut.LowClosed = true;
                                break;
                            case "<=":
                                cut.High = n;
                                break;
                            default:
                                below = n;
                                break;
                        }

                        break;
                    }

                    default:
                        return null;
                }
            }

            int kinds = (equal.Count > 0 ? 1 : 0) + (any is null ? 0 : 1) + (none is null ? 0 : 1)
                + (outside is null ? 0 : 1) + (ranged is null ? 0 : 1);

            if (kinds > 1)
            {
                return null;
            }

            if (equal.Count == 1)
            {
                cut.Kind = Listed;
                cut.Fields.Add(equal[0].Field);
                cut.Tuples.AddRange(equal[0].Values.Select(v => new List<string> { v }));
            }
            else if (equal.Count > 1)
            {
                // Several fields, one value each: one tuple of a multi-field renderer.
                if (equal.Any(e => e.Values.Count != 1)
                    || equal.Select(e => e.Field).Distinct(StringComparer.Ordinal).Count() != equal.Count)
                {
                    return null;
                }

                cut.Kind = Listed;
                cut.Fields.AddRange(equal.Select(e => e.Field));
                cut.Tuples.Add([.. equal.Select(e => e.Values[0])]);
            }
            else if (any is not null || none is not null)
            {
                List<List<(string Field, string Value)>> tuples = any ?? none!;
                List<string> fields = [.. tuples[0].Select(t => t.Field)];

                if (tuples.Any(t => !t.Select(p => p.Field).SequenceEqual(fields, StringComparer.Ordinal)))
                {
                    return null;
                }

                cut.Kind = any is not null ? Listed : Other;
                cut.Fields.AddRange(fields);
                cut.Tuples.AddRange(tuples.Select(t => t.Select(p => p.Value).ToList()));
            }
            else if (outside is { } unlisted)
            {
                cut.Kind = Other;
                cut.Fields.Add(unlisted.Field);
                cut.Tuples.AddRange(unlisted.Values.Select(v => new List<string> { v }));
            }
            else if (ranged is not null)
            {
                cut.Fields.Add(ranged);

                if (below is { } floor)
                {
                    if (cut.Low is not null || cut.High is not null)
                    {
                        return null;
                    }

                    cut.Kind = Below;
                    cut.High = floor;
                }
                else
                {
                    cut.Kind = Range;
                }
            }

            // <b>A default's own values are not its identity</b>: every default of one level is
            // the same class, whichever values it happens to list.
            if (cut.Kind == Other)
            {
                cut.Tuples.Clear();
            }

            return cut;
        }

        /// <summary>
        /// One value of each field, from `["==", f, v]`, `["in", f, v, twin]` or an `all` of them.
        /// </summary>
        /// <param name="node">The condition.</param>
        /// <param name="banded">The fields some layer bands a variable over.</param>
        /// <returns>The pairs, or null.</returns>
        private static List<(string Field, string Value)>? Tuple(JsonNode? node, HashSet<string> banded)
        {
            List<JsonArray> atoms = [];

            if (!Flatten(node, atoms))
            {
                return null;
            }

            List<(string Field, string Value)> pairs = [];

            foreach (JsonArray atom in atoms)
            {
                string op = (atom[0] as JsonValue)?.ToString() ?? string.Empty;

                if (op is not ("==" or "in")
                    || atom.Count < 3
                    || Name(atom[1]) is not { } field
                    || banded.Contains(field)
                    || Values(atom, 2) is not { Count: 1 } values)
                {
                    return null;
                }

                pairs.Add((field, values[0]));
            }

            return pairs.Count == 0 ? null : pairs;
        }
    }
}
