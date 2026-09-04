using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Cartography;

/// <summary>
/// Turns a field's distribution into the bounds of a class-breaks renderer.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-052](../../../docs/adr/ADR-052-the-canonical-symbology-document-is-cim.md) §3.12.</b>
/// Reading a classified renderer and authoring one are different problems, and only the first
/// was solved here: the editor offered one empty class and a text box, so styling a field meant
/// already knowing its values. CIM names the arithmetic in
/// <c>ClassificationMethod</c> — seven methods — and
/// <c>CIMClassBreaksProperties.classificationMethod</c> records which one produced a document's
/// bounds. This is that arithmetic.
/// </para>
/// <para>
/// <b>Pure, and Tier 1.</b> Nothing here reads a database or a document: it takes numbers and
/// returns numbers, which is what lets the hard cases — a column of one value, a range that
/// crosses zero, more classes than distinct values — be tested without a server. What to ask the
/// data for is <see cref="Fractions"/>'s job, and the caller does the asking.
/// </para>
/// <para>
/// <b>Every method here is published cartography.</b> Equal interval, quantile, standard
/// deviation and geometric progression are arithmetic; natural breaks is Fisher's exact
/// dynamic programme, published as W. D. Fisher, <i>On grouping for maximum homogeneity</i>,
/// Journal of the American Statistical Association 53 (1958), and applied to cartography by
/// G. F. Jenks in 1967. None of it is read out of an implementation (§5 of `CLAUDE.md`).
/// </para>
/// </remarks>
public static class Classification
{
    /// <summary>
    /// How many points of the distribution a natural-breaks classification is computed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sampling decision, taken here rather than deferred.</b> Fisher's algorithm is
    /// O(n²k) in the number of values, so running it over a column of a million rows is not an
    /// option and every implementation samples. The usual sample is random rows, which makes the
    /// answer different on every run — a classification that changes when nobody changed
    /// anything.
    /// </para>
    /// <para>
    /// <b>This samples the distribution instead of the rows</b>, by asking for 254 evenly spaced
    /// quantiles and running Fisher over those. That is the empirical distribution function read
    /// at even intervals, which is the same thing Fisher weighs — each quantile stands for the
    /// same number of rows — so the approximation is principled rather than lucky. It is also
    /// **deterministic**: the same data gives the same breaks today and next year, on this
    /// deployment and on a copy of it.
    /// </para>
    /// <para>
    /// <b>254, and the cost is stated.</b> Fisher over 254 points with 7 classes is about
    /// 450,000 inner steps — a few milliseconds — and one SQL statement computes all 254
    /// quantiles in a single pass. Doubling it would quadruple the arithmetic to buy an accuracy
    /// the map cannot show.
    /// </para>
    /// </remarks>
    public const int DistributionSample = 254;

    /// <summary>The most classes a renderer may be asked for.</summary>
    /// <remarks>
    /// <b>Not a technical limit.</b> Nobody reads a thirty-class choropleth, and every class is
    /// a symbol somebody has to be able to tell apart from its neighbours. Refusing beyond this
    /// is kinder than producing it.
    /// </remarks>
    public const int MostClasses = 32;

    /// <summary>
    /// The quantiles a method needs before <see cref="Bounds"/> can be called.
    /// </summary>
    /// <remarks>
    /// <b>The classifier says what it needs and the caller fetches it.</b> Two of the seven
    /// methods want more than a summary — quantile wants the interior cut points and natural
    /// breaks wants the shape of the distribution — and putting the query behind this class
    /// would make every other method drag a database connection it never uses.
    /// </remarks>
    /// <param name="method">Which classification.</param>
    /// <param name="classes">How many classes are wanted.</param>
    /// <returns>Fractions from 0 to 1, ascending, possibly empty.</returns>
    public static IReadOnlyList<double> Fractions(ClassifyBy method, int classes)
    {
        int wanted = Math.Clamp(classes, 1, MostClasses);

        return method switch
        {
            ClassifyBy.Quantile =>
                [.. Enumerable.Range(1, Math.Max(wanted - 1, 0)).Select(i => (double)i / wanted)],

            // <b>The interior points only.</b> The ends are the minimum and the maximum, which
            // every caller already asks for, and asking for quantile 0 and 1 as well would
            // duplicate them at a different rounding.
            ClassifyBy.NaturalBreaks =>
                [.. Enumerable.Range(1, DistributionSample)
                    .Select(i => (double)i / (DistributionSample + 1))],

            _ => [],
        };
    }

    /// <summary>
    /// The upper bound of each class, ascending, ending at the maximum.
    /// </summary>
    /// <param name="method">Which classification.</param>
    /// <param name="classes">
    /// How many classes are wanted. Ignored by <see cref="ClassifyBy.DefinedInterval"/> and
    /// <see cref="ClassifyBy.StandardDeviation"/>, both of which derive their count from the
    /// data and the interval.
    /// </param>
    /// <param name="from">What the data says.</param>
    /// <param name="interval">
    /// The width of a class for <see cref="ClassifyBy.DefinedInterval"/>, and the multiple of a
    /// standard deviation for <see cref="ClassifyBy.StandardDeviation"/>. Ignored otherwise.
    /// </param>
    /// <returns>One bound per class.</returns>
    /// <exception cref="SymbologyException">The data cannot carry this classification.</exception>
    public static IReadOnlyList<double> Bounds(
        ClassifyBy method, int classes, Distribution from, double interval = 1)
    {
        ArgumentNullException.ThrowIfNull(from);

        // <b>Only for the methods that use it.</b> A defined interval and a standard-deviation
        // classification derive their count from the data and the interval, so a caller has
        // nothing sensible to put here and refusing whatever they put would be refusing them for
        // answering a question that was not asked. Both check their own count against the same
        // ceiling further down, where the number is known.
        bool counted = method is not
            (ClassifyBy.DefinedInterval or ClassifyBy.StandardDeviation);

        if (counted && (classes < 1 || classes > MostClasses))
        {
            throw new SymbologyException(
                $"A classification needs between 1 and {MostClasses} classes; {classes} were "
                + "asked for. Beyond that the classes stop being tellable apart on a map, which "
                + "is the only reason to have them.");
        }

        if (!double.IsFinite(from.Minimum) || !double.IsFinite(from.Maximum))
        {
            throw new SymbologyException(
                "The field has no finite minimum and maximum, so there is nothing to classify. "
                + "An empty selection, or a column of nulls, reaches here looking exactly like "
                + "this.");
        }

        // <b>One value is one class, whatever was asked for.</b> Every method below divides by
        // the range, and a column where every row holds 42 has a range of zero. Returning one
        // class is the honest answer and it is what the legend should say.
        if (from.Maximum <= from.Minimum)
        {
            return [from.Maximum];
        }

        return method switch
        {
            ClassifyBy.Manual => throw new SymbologyException(
                "`Manual` is not a classification this server computes — it means the bounds "
                + "came from whoever wrote them. Send them in the document instead of asking "
                + "for them."),

            ClassifyBy.EqualInterval => Equal(classes, from),
            ClassifyBy.DefinedInterval => Defined(interval, from),
            ClassifyBy.GeometricalInterval => Geometric(classes, from),
            ClassifyBy.StandardDeviation => Deviations(interval, from),
            ClassifyBy.Quantile => Quantiles(classes, from),
            ClassifyBy.NaturalBreaks => Fisher(classes, from),

            _ => throw new SymbologyException(
                $"'{method}' is not a classification this server knows."),
        };
    }

    /// <summary>Bands of equal width.</summary>
    private static IReadOnlyList<double> Equal(int classes, Distribution from)
    {
        double width = (from.Maximum - from.Minimum) / classes;

        return [.. Enumerable.Range(1, classes)
            .Select(i => i == classes ? from.Maximum : from.Minimum + (width * i))];
    }

    /// <summary>Bands of a width the caller chose.</summary>
    private static List<double> Defined(double interval, Distribution from)
    {
        if (!double.IsFinite(interval) || interval <= 0)
        {
            throw new SymbologyException(
                "A defined-interval classification needs a positive interval; it was given "
                + interval.ToString(CultureInfo.InvariantCulture) + ".");
        }

        double span = from.Maximum - from.Minimum;
        double count = Math.Ceiling(span / interval);

        if (count > MostClasses)
        {
            throw new SymbologyException(
                $"An interval of {interval.ToString(CultureInfo.InvariantCulture)} over a range "
                + $"of {span.ToString(CultureInfo.InvariantCulture)} makes {count} classes, and "
                + $"{MostClasses} is the most this server will draw. Use a wider interval.");
        }

        List<double> bounds = [];

        for (int i = 1; i <= (int)count; i++)
        {
            bounds.Add(i == (int)count ? from.Maximum : from.Minimum + (interval * i));
        }

        return bounds;
    }

    /// <summary>Bands growing by a constant ratio.</summary>
    /// <remarks>
    /// <b>The same curve as the proportional renderer's, and it needs the same positive
    /// minimum.</b> A geometric progression is a ratio to where it starts, so a minimum of zero
    /// divides by it and a negative one asks for a fractional power of a negative number. ArcGIS
    /// handles that by shifting the data; this server says so instead, because a classification
    /// whose bounds are not the numbers in the column is a legend that lies.
    /// </remarks>
    private static IReadOnlyList<double> Geometric(int classes, Distribution from)
    {
        if (from.Minimum <= 0)
        {
            throw new SymbologyException(
                "A geometrical-interval classification multiplies its way up from the smallest "
                + $"value, and this field's is {from.Minimum.ToString(CultureInfo.InvariantCulture)}. "
                + "It needs a positive minimum. Equal interval or quantile will classify this "
                + "field as it stands.");
        }

        double ratio = Math.Pow(from.Maximum / from.Minimum, 1.0 / classes);

        return [.. Enumerable.Range(1, classes)
            .Select(i => i == classes ? from.Maximum : from.Minimum * Math.Pow(ratio, i))];
    }

    /// <summary>Bands either side of the mean, measured in standard deviations.</summary>
    /// <remarks>
    /// <b>The class count comes from the data, not from the caller.</b> This classification says
    /// *where is the mean and how far is this from it*; how many bands that makes depends on how
    /// far the data spreads, and forcing it to a requested count would be answering a different
    /// question.
    /// </remarks>
    private static List<double> Deviations(double interval, Distribution from)
    {
        if (!double.IsFinite(from.StandardDeviation) || from.StandardDeviation <= 0)
        {
            throw new SymbologyException(
                "A standard-deviation classification needs a standard deviation above zero, and "
                + "this field's is "
                + from.StandardDeviation.ToString(CultureInfo.InvariantCulture)
                + ". A column whose values are all the same has nothing to be deviations from.");
        }

        double step = double.IsFinite(interval) && interval > 0 ? interval : 1;
        double width = step * from.StandardDeviation;

        List<double> bounds = [];

        // Downwards from the mean while there is still data below, then upwards.
        int below = (int)Math.Ceiling((from.Mean - from.Minimum) / width);
        int above = (int)Math.Ceiling((from.Maximum - from.Mean) / width);

        if (below + above > MostClasses)
        {
            throw new SymbologyException(
                $"An interval of {step.ToString(CultureInfo.InvariantCulture)} standard "
                + $"deviations spreads this field over {below + above} classes, and "
                + $"{MostClasses} is the most this server will draw. Use a wider interval.");
        }

        for (int i = below - 1; i >= 0; i--)
        {
            bounds.Add(from.Mean - (width * i));
        }

        for (int i = 1; i <= above; i++)
        {
            bounds.Add(from.Mean + (width * i));
        }

        // <b>The top bound is the maximum, exactly.</b> A band that ends a hair above the
        // largest value is arithmetically identical and reads as a mistake in the legend.
        if (bounds.Count > 0)
        {
            bounds[^1] = from.Maximum;
        }

        return bounds.Count > 0 ? bounds : [from.Maximum];
    }

    /// <summary>Bands holding equal numbers of features.</summary>
    private static IReadOnlyList<double> Quantiles(int classes, Distribution from)
    {
        if (from.Quantiles.Count < classes - 1)
        {
            throw new SymbologyException(
                $"A quantile classification into {classes} classes needs {classes - 1} interior "
                + $"quantiles and was given {from.Quantiles.Count}. `Classification.Fractions` "
                + "says which to ask for.");
        }

        List<double> bounds = [.. from.Quantiles.Take(classes - 1)];

        bounds.Add(from.Maximum);

        // <b>Ties collapse classes, and that is the data's answer rather than an error.</b> A
        // column where half the rows hold the same value has that value as several consecutive
        // quantiles; keeping the duplicates would make empty classes with identical bounds.
        return [.. bounds.Distinct().Order()];
    }

    /// <summary>
    /// Fisher's exact partition: the bounds that minimise the sum of within-class variance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The dynamic programme, not the iterative approximation.</b> Fisher (1958) proves the
    /// optimal partition of an ordered list into k contiguous groups can be built up from the
    /// optimal partition of every prefix into k-1, which makes it exact in O(n²k) rather than a
    /// hill climb that lands somewhere different depending on where it started.
    /// </para>
    /// <para>
    /// <b>Variance in one pass, from the prefix sums.</b> The cost of a group is its sum of
    /// squared deviations, which is <c>Σx² - (Σx)²/n</c> — so with running totals every
    /// candidate group costs a subtraction rather than a loop, and the whole thing stays inside
    /// the two loops it already has.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<double> Fisher(int classes, Distribution from)
    {
        double[] values = [.. from.Quantiles.Where(double.IsFinite).Order()];

        if (values.Length < 2)
        {
            throw new SymbologyException(
                "A natural-breaks classification is computed over a sample of the field's "
                + $"distribution and was given {values.Length} points. "
                + "`Classification.Fractions` says which quantiles to ask for.");
        }

        if (classes >= values.Length)
        {
            return [.. values.Distinct().Order().Append(from.Maximum).Distinct().Order()];
        }

        int n = values.Length;

        double[] sum = new double[n + 1];
        double[] squares = new double[n + 1];

        for (int i = 0; i < n; i++)
        {
            sum[i + 1] = sum[i] + values[i];
            squares[i + 1] = squares[i] + (values[i] * values[i]);
        }

        // Deviation of values[a..b), from the running totals.
        double Cost(int a, int b)
        {
            int count = b - a;

            if (count <= 1)
            {
                return 0;
            }

            double total = sum[b] - sum[a];

            return squares[b] - squares[a] - (total * total / count);
        }

        double[,] best = new double[classes + 1, n + 1];
        int[,] cut = new int[classes + 1, n + 1];

        for (int end = 0; end <= n; end++)
        {
            best[0, end] = end == 0 ? 0 : double.PositiveInfinity;
        }

        for (int k = 1; k <= classes; k++)
        {
            for (int end = 1; end <= n; end++)
            {
                best[k, end] = double.PositiveInfinity;

                for (int start = k - 1; start < end; start++)
                {
                    if (double.IsPositiveInfinity(best[k - 1, start]))
                    {
                        continue;
                    }

                    double candidate = best[k - 1, start] + Cost(start, end);

                    if (candidate < best[k, end])
                    {
                        best[k, end] = candidate;
                        cut[k, end] = start;
                    }
                }
            }
        }

        List<double> bounds = [];
        int at = n;

        for (int k = classes; k >= 1; k--)
        {
            int start = cut[k, at];

            bounds.Add(values[at - 1]);
            at = start;
        }

        bounds.Reverse();

        // The last class always ends at the field's own maximum, which the sample need not
        // contain — it is the quantiles' top point, and the true maximum is above it.
        bounds[^1] = from.Maximum;

        return [.. bounds.Distinct().Order()];
    }
}

/// <summary>The seven ways CIM classifies a numeric field.</summary>
/// <remarks>
/// Named as <c>ClassificationMethod</c> spells them, so a stored
/// <c>classificationMethod</c> maps across without a translation table.
/// </remarks>
public enum ClassifyBy
{
    /// <summary>Bounds somebody wrote. Nothing is computed.</summary>
    Manual,

    /// <summary>Bands of equal width.</summary>
    EqualInterval,

    /// <summary>Bands of a width the caller chose.</summary>
    DefinedInterval,

    /// <summary>Bands growing by a constant ratio.</summary>
    GeometricalInterval,

    /// <summary>Bands measured in standard deviations from the mean.</summary>
    StandardDeviation,

    /// <summary>Bands holding equal numbers of features.</summary>
    Quantile,

    /// <summary>Bands minimising the variance within each of them.</summary>
    NaturalBreaks,
}

/// <summary>What a classifier needs to know about a field.</summary>
/// <param name="Minimum">Its smallest value.</param>
/// <param name="Maximum">Its largest.</param>
/// <param name="Mean">Its mean, for the standard-deviation classification.</param>
/// <param name="StandardDeviation">Sample standard deviation, for the same.</param>
/// <param name="Quantiles">
/// The values at the fractions <see cref="Classification.Fractions"/> asked for, in the order it
/// asked for them. Empty for the methods that need none.
/// </param>
public sealed record Distribution(
    double Minimum,
    double Maximum,
    double Mean = 0,
    double StandardDeviation = 0,
    IReadOnlyList<double>? Quantiles = null)
{
    /// <summary>The values at the requested fractions.</summary>
    public IReadOnlyList<double> Quantiles { get; init; } = Quantiles ?? [];
}
