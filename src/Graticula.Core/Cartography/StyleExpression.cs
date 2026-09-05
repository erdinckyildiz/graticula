using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Graticula.Cartography;

/// <summary>
/// Where a style value comes from: a constant, or an expression over one feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compiled once per request and evaluated once per feature.</b> The alternative
/// — walking the style's <c>JsonNode</c> tree for every feature — re-reads the same
/// document a hundred thousand times per map and allocates on each read. That is
/// exactly the cost [ADR-004](../../../docs/adr/ADR-004-rendering-engine.md) §0
/// measured and warned about, arrived at by accident rather than by design.
/// </para>
/// <para>
/// <b>Four operators, because
/// [SymbologyConversion](SymbologyConversion.cs) emits four</b> — <c>get</c>,
/// <c>match</c>, <c>step</c> and <c>interpolate</c> in its three colour spaces.
/// Anything else is refused **at compile time**, before a single feature is read,
/// so a client gets one clear service exception rather than a map that is silently
/// the wrong colour. That is [A-072](../../../docs/architecture-assumptions.md)'s
/// premise applied at the only point where it still can be.
/// </para>
/// <para>
/// <b><c>zoom</c> is answered from the request's resolution.</b> WMS has no zoom
/// level; a style written for a slippy map is full of them. The renderer derives the
/// web-mercator zoom whose resolution matches the requested one, which is what makes
/// a MapLibre style drawable through a protocol that never heard of tiles.
/// </para>
/// </remarks>
public abstract record StyleExpression
{
    private StyleExpression()
    {
    }

    /// <summary>Everything one evaluation may read.</summary>
    /// <param name="Attributes">The feature's attributes, by column name.</param>
    /// <param name="Zoom">The zoom this map's resolution corresponds to.</param>
    public readonly record struct Context(
        IReadOnlyDictionary<string, object?> Attributes, double Zoom);

    /// <summary>Evaluates against one feature.</summary>
    /// <param name="context">The feature and the map.</param>
    /// <returns>The value, which may be null.</returns>
    public abstract object? Evaluate(in Context context);

    /// <summary>
    /// One classified axis a style makes: a column, and the classes it sorts
    /// features into.
    /// </summary>
    /// <remarks>
    /// <b>Q-131, and it exists because that row's premise was wrong.</b> The row said
    /// enumerating a classified legend's entries *means reading the data*. It does
    /// not, for either expression this server evaluates: a <c>match</c> carries its
    /// labels and a <c>step</c> carries its breaks, both written out in the style
    /// document itself. The data would only be needed for a classification the style
    /// does not enumerate, and there is no such expression here.
    /// </remarks>
    /// <param name="Field">The column classified on.</param>
    /// <param name="Cases">The classes, in the order the style writes them.</param>
    public readonly record struct Classification(string Field, IReadOnlyList<ClassCase> Cases);

    /// <summary>One class of a classification.</summary>
    /// <remarks>
    /// <b><c>Value</c> is what to put in the column to land in this class</b>, not
    /// the class's appearance. A legend resolves the whole plan against a feature
    /// carrying that value, so a swatch is drawn by the same code that draws the map
    /// and cannot describe it differently.
    /// </remarks>
    /// <param name="Label">What to call it, ready to draw.</param>
    /// <param name="Value">A value that selects it, or null for the fallback class.</param>
    public readonly record struct ClassCase(string Label, object? Value);

    /// <summary>
    /// Whether this expression can answer differently for two features or two zooms.
    /// </summary>
    /// <remarks>
    /// <b>Asked so that a caller can tell folding from flattening.</b> Evaluating an
    /// expression once and keeping the answer is only sound when the expression has one
    /// answer. Everything except a literal is assumed to vary, because being wrong that way
    /// costs an evaluation per feature and being wrong the other way draws every feature the
    /// same — see <c>SymbologyPlan.Opacity</c>, where exactly that happened.
    /// </remarks>
    public virtual bool Varies => true;

    /// <summary>Every classification this expression makes over a feature attribute.</summary>
    /// <remarks>
    /// <b>A walk, like <see cref="Fields"/>, and for the same reason.</b> A style's
    /// classification can be nested — an opacity <c>step</c> inside a colour
    /// <c>match</c>'s branch — so the answer is a list rather than one axis, and what
    /// to do with several is the caller's decision rather than this type's.
    /// </remarks>
    /// <param name="into">Where to add them.</param>
    public virtual void Classes(ICollection<Classification> into)
    {
    }

    /// <summary>Every attribute name this expression reads.</summary>
    /// <remarks>
    /// <b>So the query fetches the columns the style needs and no others.</b> A map
    /// that selects <c>*</c> pays for every column of every feature in the extent,
    /// and on a wide table that is most of the request.
    /// </remarks>
    /// <param name="into">Where to add them.</param>
    public abstract void Fields(ISet<string> into);

    /// <summary>
    /// Compiles a style value.
    /// </summary>
    /// <param name="node">The value, as the canonical document holds it.</param>
    /// <returns>The expression.</returns>
    /// <exception cref="SymbologyException">It uses something this cannot evaluate.</exception>
    public static StyleExpression Compile(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return new Constant(Value(node));
        }

        // <b>An array whose head is not a string is data, not an expression.</b>
        // `line-dasharray` is `[6, 3]` -- two lengths, no operator -- and reading its first
        // element as an operator name threw *An element of type 'Number' cannot be converted to
        // a 'System.String'*, which arrived at the caller as a 500 with the reason only in the
        // log. Measured 2026-09-03 on a dashed line symbol; the symbol library ships two.
        if (array[0] is not JsonValue first || !first.TryGetValue(out string? head))
        {
            return new Constant(Value(node));
        }

        switch (head)
        {
            case "get":
                return new Attribute(Require(array, 1, "get").ToString());

            case "zoom":
                return Zoom.Instance;

            case "literal":
                // ["literal", x] wraps a value that would otherwise be read as an
                // expression — an array of dash lengths, most often.
                return new Constant(Value(array.Count > 1 ? array[1] : null));

            case "match":
                return CompileMatch(array);

            case "step":
                return CompileStep(array);

            case "interpolate":
                return CompileInterpolate(array, ColourSpace.Interpolation.Rgb);

            case "interpolate-lab":
                return CompileInterpolate(array, ColourSpace.Interpolation.Lab);

            case "interpolate-hcl":
                return CompileInterpolate(array, ColourSpace.Interpolation.Hcl);

            case "concat":
            case "to-string":
                // <b>Both, because a unique-value renderer over more than one field needs
                // them.</b> ArcGIS classifies by up to three fields at once and joins their
                // values with a delimiter — `field1`, `field2`, `field3` and `fieldDelimiter` in
                // its own vocabulary — so the derived style's `match` runs over
                // `["concat", ["to-string", ["get", a]], ",", ["to-string", ["get", b]]]`.
                // Without these the tile face could carry a two-field classification and this
                // renderer could not draw it. ADR-052 §3.17.
                return new Joined(
                    [.. Enumerable.Range(1, array.Count - 1).Select(i => Compile(array[i]))]);

            default:
                throw new SymbologyException(
                    $"This server draws with `get`, `zoom`, `literal`, `match`, `step`, "
                    + $"`interpolate`, `concat` and `to-string`; the style uses `{head}`. It is "
                    + "refused rather than approximated, because a map drawn from an expression "
                    + "the server did not understand is wrong in a way nobody can see.");
        }
    }

    private static JsonNode Require(JsonArray array, int index, string what) =>
        index < array.Count && array[index] is { } node
            ? node
            : throw new SymbologyException($"`{what}` is missing an argument.");

    /// <summary>["match", input, label, output, …, fallback].</summary>
    private static Match CompileMatch(JsonArray array)
    {
        if (array.Count < 4)
        {
            throw new SymbologyException(
                "`match` needs an input, at least one label and output, and a fallback.");
        }

        StyleExpression input = Compile(array[1]);

        List<(object?[] Labels, StyleExpression Output)> cases = [];

        int i = 2;

        // The last element is the fallback, so pairs run out two before the end.
        for (; i + 1 < array.Count - 1; i += 2)
        {
            JsonNode? label = array[i];

            // A label may be a single value or an array of values sharing one output.
            object?[] labels = label is JsonArray many
                ? [.. Many(many)]
                : [Value(label)];

            cases.Add((labels, Compile(array[i + 1])));
        }

        return new Match(input, cases, Compile(array[^1]), Lookup(cases));

        static IEnumerable<object?> Many(JsonArray labels)
        {
            foreach (JsonNode? each in labels)
            {
                yield return Value(each);
            }
        }
    }

    /// <summary>
    /// The cases as a table keyed the way <see cref="Same"/> compares, or null when they
    /// cannot be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A classification used to cost a scan of every class for every feature.</b>
    /// `Match.Evaluate` walked the list and asked `Same` about each label, which is fine at
    /// eight classes and is what a unique-value renderer is made of at a thousand. Measured
    /// 2026-09-05 on the owner's own layer, drawn at 1600×800 over the whole country:
    /// <b>25,280 polylines with 1,177 classes took 1.9 s</b>, while <b>46,041 polylines with a
    /// simple renderer took 0.65 s</b> — nearly twice the features and a third of the time. The
    /// difference is not the drawing and not the data; it is three paint properties each
    /// scanning up to 1,177 labels, 45 million comparisons for one picture.
    /// </para>
    /// <para>
    /// <b>Which nobody could meet until ADR-054.</b> A classification was capped at 256 classes
    /// because a stored document was capped at 262,144 characters, and at 256 the scan costs a
    /// fifth of this. Removing the cap is what made the shape of the loop matter.
    /// </para>
    /// <para>
    /// <b>The key is `Text`, and one kind of label is refused rather than keyed.</b> `Same`
    /// compares two doubles with `Equals` and everything else as invariant text, and those two
    /// rules disagree in exactly one place: `-0.0` equals `0.0` as a number and reads as `"-0"`
    /// against `"0"` as text. A table cannot reproduce a comparison that is not transitive, so
    /// a match whose labels include a zero keeps the scan — the answer stays the one the scan
    /// would have given rather than becoming *nearly* that.
    /// </para>
    /// <para>
    /// <b>First occurrence wins, because that is what the scan does.</b> Two cases carrying the
    /// same label are a document somebody wrote by hand or a merge of two classifications; the
    /// scan returns the first and so does this.
    /// </para>
    /// </remarks>
    /// <param name="cases">The compiled cases, in document order.</param>
    /// <returns>The table, or null when the scan has to be kept.</returns>
    private static Dictionary<string, StyleExpression>? Lookup(
        IReadOnlyList<(object?[] Labels, StyleExpression Output)> cases)
    {
        Dictionary<string, StyleExpression> table = new(StringComparer.Ordinal);

        foreach ((object?[] labels, StyleExpression output) in cases)
        {
            foreach (object? label in labels)
            {
                // <b>A null label is a case the table cannot hold and the scan can.</b>
                // `Same(null, null)` is true, so a null feature value would have to find it;
                // keying it would mean a second, parallel slot for one rare document.
                if (label is null || (label is double zero && zero == 0.0))
                {
                    return null;
                }

                if (Text(label) is { } key)
                {
                    table.TryAdd(key, output);
                }
                else
                {
                    return null;
                }
            }
        }

        return table;
    }

    /// <summary>["step", input, first, stop, output, …].</summary>
    private static Step CompileStep(JsonArray array)
    {
        if (array.Count < 3)
        {
            throw new SymbologyException("`step` needs an input and a first output.");
        }

        StyleExpression input = Compile(array[1]);
        StyleExpression first = Compile(array[2]);

        List<(double Stop, StyleExpression Output)> stops = [];

        for (int i = 3; i + 1 < array.Count; i += 2)
        {
            stops.Add((Number(array[i], "step"), Compile(array[i + 1])));
        }

        return new Step(input, first, stops);
    }

    /// <summary>["interpolate", ["linear"], input, stop, output, …].</summary>
    private static Interpolate CompileInterpolate(
        JsonArray array, ColourSpace.Interpolation space)
    {
        if (array.Count < 5)
        {
            throw new SymbologyException(
                "`interpolate` needs a curve, an input and at least one stop.");
        }

        double exponent = 1;

        if (array[1] is JsonArray curve && curve.Count > 0)
        {
            string kind = curve[0]?.GetValue<string>() ?? "linear";

            exponent = kind switch
            {
                "linear" => 1,
                "exponential" => curve.Count > 1 ? Number(curve[1], "exponential") : 1,
                _ => throw new SymbologyException(
                    $"This server interpolates with `linear` and `exponential`; the style asks "
                    + $"for `{kind}`."),
            };
        }

        StyleExpression input = Compile(array[2]);

        List<(double Stop, StyleExpression Output)> stops = [];

        for (int i = 3; i + 1 < array.Count; i += 2)
        {
            stops.Add((Number(array[i], "interpolate"), Compile(array[i + 1])));
        }

        if (stops.Count == 0)
        {
            throw new SymbologyException("`interpolate` needs at least one stop.");
        }

        return new Interpolate(input, stops, exponent, space);
    }

    private static double Number(JsonNode? node, string what) =>
        node is not null && double.TryParse(
            node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new SymbologyException($"`{what}` needs numeric stops.");

    /// <summary>A JSON value as the plain object an attribute would be.</summary>
    private static object? Value(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            object?[] values = new object?[array.Count];

            for (int i = 0; i < array.Count; i++)
            {
                values[i] = Value(array[i]);
            }

            return values;
        }

        if (node is not JsonValue value)
        {
            return node.ToString();
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        if (value.TryGetValue(out bool flag))
        {
            return flag;
        }

        return value.ToString();
    }

    /// <summary>
    /// Compares an expression's result with a style's literal, across types.
    /// </summary>
    /// <remarks>
    /// <b>Loose on purpose, and it is not laziness.</b> A <c>match</c> on a numeric
    /// column is written by every Esri renderer with string labels, because
    /// <c>uniqueValueInfos</c> carries <c>value</c> as text. Comparing strictly
    /// would make every converted unique-value renderer fall through to its
    /// fallback — a map in one colour, from a style that is correct.
    /// </remarks>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they match.</returns>
    public static bool Same(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is double a && right is double b)
        {
            return a.Equals(b);
        }

        return string.Equals(Text(left), Text(right), StringComparison.Ordinal);
    }

    /// <summary>A value as the invariant text a style would compare against.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, or null.</returns>
    public static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>A value as a number, or null when it is not one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The number.</returns>
    public static double? AsNumber(object? value) => value switch
    {
        null => null,
        double number => number,
        float number => number,
        int number => number,
        long number => number,
        short number => number,
        decimal number => (double)number,
        string text when double.TryParse(
            text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// A colour expression with a separate opacity expression multiplied into it.
    /// </summary>
    /// <remarks>
    /// <b>Only used when one of the two is not a constant.</b>
    /// <see cref="SymbologyPlan"/> folds the pair at compile time whenever it can,
    /// because multiplying once per request beats multiplying once per feature — and
    /// a style that computes either has asked for the per-feature cost.
    /// </remarks>
    /// <param name="colour">The colour.</param>
    /// <param name="opacity">The opacity, 0 to 1.</param>
    /// <returns>The expression.</returns>
    public static StyleExpression Fade(StyleExpression colour, StyleExpression opacity)
    {
        ArgumentNullException.ThrowIfNull(colour);
        ArgumentNullException.ThrowIfNull(opacity);

        return new Faded(colour, opacity);
    }

    // ---------- the operators ----------

    private sealed record Faded(StyleExpression Colour, StyleExpression Opacity) : StyleExpression
    {
        public override object? Evaluate(in Context context)
        {
            if (!Rgba.TryParse(Text(Colour.Evaluate(context)), out Rgba colour))
            {
                return null;
            }

            return colour.WithOpacity(AsNumber(Opacity.Evaluate(context)) ?? 1).ToString();
        }

        public override void Fields(ISet<string> into)
        {
            Colour.Fields(into);
            Opacity.Fields(into);
        }

        public override void Classes(ICollection<Classification> into)
        {
            Colour.Classes(into);
            Opacity.Classes(into);
        }
    }


    /// <summary>A value that does not depend on the feature.</summary>
    private sealed record Constant(object? Held) : StyleExpression
    {
        /// <summary>A literal is the one thing that does not vary.</summary>
        public override bool Varies => false;

        public override object? Evaluate(in Context context) => Held;

        public override void Fields(ISet<string> into)
        {
        }
    }

    /// <summary>["get", name].</summary>
    private sealed record Attribute(string Name) : StyleExpression
    {
        public override object? Evaluate(in Context context) =>
            context.Attributes.TryGetValue(Name, out object? value) ? value : null;

        public override void Fields(ISet<string> into) => into.Add(Name);
    }

    /// <summary>["concat", …] and ["to-string", x], which is a concat of one.</summary>
    /// <remarks>
    /// <b>Null joins as nothing rather than as "null".</b> A feature missing one of the fields a
    /// classification combines should fall to the default symbol, and it does — the joined key
    /// simply will not match any class. Writing the word null into the key would make a class
    /// somebody could match by accident.
    /// </remarks>
    private sealed record Joined(IReadOnlyList<StyleExpression> Parts) : StyleExpression
    {
        public override object? Evaluate(in Context context)
        {
            System.Text.StringBuilder built = new();

            foreach (StyleExpression part in Parts)
            {
                built.Append(Text(part.Evaluate(context)));
            }

            return built.ToString();
        }

        public override void Fields(ISet<string> into)
        {
            foreach (StyleExpression part in Parts)
            {
                part.Fields(into);
            }
        }
    }

    /// <summary>["zoom"].</summary>
    private sealed record Zoom : StyleExpression
    {
        public static Zoom Instance { get; } = new();

        public override object? Evaluate(in Context context) => context.Zoom;

        public override void Fields(ISet<string> into)
        {
        }
    }

    private sealed record Match(
        StyleExpression Input,
        IReadOnlyList<(object?[] Labels, StyleExpression Output)> Cases,
        StyleExpression Fallback,
        Dictionary<string, StyleExpression>? Table) : StyleExpression
    {
        public override object? Evaluate(in Context context)
        {
            object? value = Input.Evaluate(context);

            // <b>The table when there is one, and it is the same answer.</b> `Lookup` builds it
            // only from labels whose text is what `Same` would have compared, and refuses the
            // one shape where the two disagree — see the note there, and the measurement that
            // made it worth having.
            if (Table is { } table)
            {
                return value is not null
                    && Text(value) is { } key
                    && table.TryGetValue(key, out StyleExpression? found)
                        ? found.Evaluate(context)
                        : Fallback.Evaluate(context);
            }

            foreach ((object?[] labels, StyleExpression output) in Cases)
            {
                foreach (object? label in labels)
                {
                    if (Same(value, label))
                    {
                        return output.Evaluate(context);
                    }
                }
            }

            return Fallback.Evaluate(context);
        }

        public override void Fields(ISet<string> into)
        {
            Input.Fields(into);
            Fallback.Fields(into);

            foreach ((_, StyleExpression output) in Cases)
            {
                output.Fields(into);
            }
        }

        public override void Classes(ICollection<Classification> into)
        {
            // <b>Only a match on a column classifies.</b> `["match", ["zoom"], …]` is
            // a scale rule rather than a classification, and putting zoom levels in a
            // legend would describe the map's behaviour instead of its data.
            if (Input is Attribute column)
            {
                List<ClassCase> cases = new(Cases.Count + 1);

                foreach ((object?[] labels, _) in Cases)
                {
                    // Several labels may share one output — `["a", "b"], colour` —
                    // and they are one class with one swatch, so they read as one
                    // entry with both names on it.
                    string name = string.Join(", ", labels.Select(l => Text(l) ?? "null"));

                    cases.Add(new ClassCase(name, labels.Length > 0 ? labels[0] : null));
                }

                // <b>The fallback is a class and is drawn as one.</b> It is what every
                // feature the style did not name looks like, and leaving it out of the
                // legend is how a reader concludes those features are not there.
                cases.Add(new ClassCase("Other", null));

                into.Add(new Classification(column.Name, cases));
            }

            Fallback.Classes(into);

            foreach ((_, StyleExpression output) in Cases)
            {
                output.Classes(into);
            }
        }
    }

    private sealed record Step(
        StyleExpression Input,
        StyleExpression First,
        IReadOnlyList<(double Stop, StyleExpression Output)> Stops) : StyleExpression
    {
        public override object? Evaluate(in Context context)
        {
            double? value = AsNumber(Input.Evaluate(context));

            if (value is not { } number)
            {
                return First.Evaluate(context);
            }

            StyleExpression chosen = First;

            foreach ((double stop, StyleExpression output) in Stops)
            {
                if (number < stop)
                {
                    break;
                }

                chosen = output;
            }

            return chosen.Evaluate(context);
        }

        public override void Fields(ISet<string> into)
        {
            Input.Fields(into);
            First.Fields(into);

            foreach ((_, StyleExpression output) in Stops)
            {
                output.Fields(into);
            }
        }

        public override void Classes(ICollection<Classification> into)
        {
            if (Input is Attribute column && Stops.Count > 0)
            {
                List<ClassCase> cases = new(Stops.Count + 1)
                {
                    // <b>Negative infinity, not the first stop minus one.</b> A break
                    // at 0.5 makes *minus one* land in the right class by luck and a
                    // break at -1000 does not; the class below the first stop has no
                    // lower bound and the value that selects it should say so.
                    new ClassCase(
                        $"< {Round(Stops[0].Stop)}", double.NegativeInfinity),
                };

                for (int i = 0; i < Stops.Count; i++)
                {
                    double from = Stops[i].Stop;

                    // Half-open, because that is what `step` means: a feature exactly
                    // on a break belongs to the class the break opens.
                    string name = i + 1 < Stops.Count
                        ? $"{Round(from)} – {Round(Stops[i + 1].Stop)}"
                        : $"≥ {Round(from)}";

                    cases.Add(new ClassCase(name, from));
                }

                into.Add(new Classification(column.Name, cases));
            }

            First.Classes(into);

            foreach ((_, StyleExpression output) in Stops)
            {
                output.Classes(into);
            }
        }

        /// <summary>A break as a reader would write it.</summary>
        /// <remarks>
        /// <b>`R` is right for comparing and wrong for reading.</b> It writes a class
        /// break of 1000 as `1000` and one of 0.1 + 0.2 as `0.30000000000000004`,
        /// which in a legend is noise. Whole numbers lose the point; the rest keep
        /// three places.
        /// </remarks>
        private static string Round(double stop) =>
            stop == Math.Floor(stop) && Math.Abs(stop) < 1e15
                ? stop.ToString("F0", CultureInfo.InvariantCulture)
                : stop.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private sealed record Interpolate(
        StyleExpression Input,
        IReadOnlyList<(double Stop, StyleExpression Output)> Stops,
        double Exponent,
        ColourSpace.Interpolation Space) : StyleExpression
    {
        public override object? Evaluate(in Context context)
        {
            double number = AsNumber(Input.Evaluate(context)) ?? 0;

            if (number <= Stops[0].Stop || Stops.Count == 1)
            {
                return Stops[0].Output.Evaluate(context);
            }

            if (number >= Stops[^1].Stop)
            {
                return Stops[^1].Output.Evaluate(context);
            }

            int upper = 1;

            while (upper < Stops.Count - 1 && Stops[upper].Stop < number)
            {
                upper++;
            }

            (double lowStop, StyleExpression low) = Stops[upper - 1];
            (double highStop, StyleExpression high) = Stops[upper];

            double span = highStop - lowStop;
            double t = span <= 0 ? 0 : (number - lowStop) / span;

            if (Exponent is not 1 and > 0)
            {
                // MapLibre's exponential curve. Base 1 is linear and is handled
                // above; the formula divides by zero there.
                double baseValue = Exponent;

                t = (Math.Pow(baseValue, (number - lowStop)) - 1)
                    / (Math.Pow(baseValue, span) - 1);
            }

            object? from = low.Evaluate(context);
            object? to = high.Evaluate(context);

            if (Rgba.TryParse(Text(from), out Rgba a) && Rgba.TryParse(Text(to), out Rgba b))
            {
                return ColourSpace.Mix(a, b, t, Space).ToString();
            }

            double? na = AsNumber(from);
            double? nb = AsNumber(to);

            return na is { } x && nb is { } y ? x + ((y - x) * t) : from;
        }

        public override void Fields(ISet<string> into)
        {
            Input.Fields(into);

            foreach ((_, StyleExpression output) in Stops)
            {
                output.Fields(into);
            }
        }
    }
}
