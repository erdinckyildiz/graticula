using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The arithmetic between a field's distribution and a class-breaks renderer's bounds.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.12.</b> CIM names seven classification methods and records which one produced a
/// document's bounds; this is the half that was missing. Until it existed the editor offered one
/// class with an upper bound of zero and an <i>Add a class</i> button that added the previous
/// bound plus one — numbers with no relationship to the data at all.
/// </para>
/// <para>
/// <b>Every method's hard case is here rather than its easy one.</b> A column of one value, a
/// range that reaches zero, more classes than distinct values, ties across a quantile boundary:
/// those are what a classifier meets on real data, and each of them divides by something.
/// </para>
/// </remarks>
public sealed class ClassificationTests
{
    private static Distribution Over(
        double min, double max, double mean = 0, double sd = 0, IReadOnlyList<double>? q = null)
        => new(min, max, mean, sd, q);

    // ------------------------------------------------------------------ equal interval

    [Fact]
    public void Equal_interval_divides_the_range_and_ends_exactly_at_the_maximum()
    {
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.EqualInterval, 4, Over(0, 100));

        Assert.Equal([25.0, 50.0, 75.0, 100.0], bounds);
    }

    [Fact]
    public void Equal_interval_over_a_range_that_crosses_zero_is_still_even()
    {
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.EqualInterval, 3, Over(-30, 60));

        Assert.Equal([0.0, 30.0, 60.0], bounds);
    }

    // --------------------------------------------------------------- defined interval

    [Fact]
    public void A_defined_interval_derives_its_own_class_count()
    {
        // 0..250 in steps of 100 is three classes, the last one short. The count is the data's
        // answer, not the caller's.
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.DefinedInterval, 99, Over(0, 250), interval: 100);

        Assert.Equal([100.0, 200.0, 250.0], bounds);
    }

    [Fact]
    public void An_interval_that_would_make_hundreds_of_classes_is_refused_with_the_arithmetic()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(
                ClassifyBy.DefinedInterval, 5, Over(0, 10_000), interval: 1));

        Assert.Contains("10000 classes", refused.Message, StringComparison.Ordinal);
        Assert.Contains("wider interval", refused.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- geometrical interval

    [Fact]
    public void A_geometrical_interval_multiplies_by_a_constant_ratio()
    {
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.GeometricalInterval, 3, Over(1, 1000));

        Assert.Equal(10.0, bounds[0], 9);
        Assert.Equal(100.0, bounds[1], 9);
        Assert.Equal(1000.0, bounds[2], 9);
    }

    [Fact]
    public void A_geometrical_interval_from_zero_is_refused_and_says_what_to_use_instead()
    {
        // <b>Refused rather than shifted.</b> ArcGIS shifts the data to make the progression
        // work; a bound that is not a number in the column is a legend that lies about the
        // column.
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(ClassifyBy.GeometricalInterval, 4, Over(0, 500)));

        Assert.Contains("positive minimum", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Equal interval or quantile", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ standard deviation

    [Fact]
    public void Standard_deviation_bands_step_out_from_the_mean_and_count_themselves()
    {
        // Mean 50, sd 10, data 20..80: three deviations below and three above.
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.StandardDeviation, 0, Over(20, 80, mean: 50, sd: 10));

        Assert.Equal([30.0, 40.0, 50.0, 60.0, 70.0, 80.0], bounds);
    }

    [Fact]
    public void A_half_deviation_interval_makes_twice_as_many_bands()
    {
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.StandardDeviation, 0, Over(40, 60, mean: 50, sd: 10), interval: 0.5);

        Assert.Equal([45.0, 50.0, 55.0, 60.0], bounds);
    }

    [Fact]
    public void A_column_with_no_spread_has_nothing_to_be_deviations_from()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(
                ClassifyBy.StandardDeviation, 0, Over(10, 20, mean: 15, sd: 0)));

        Assert.Contains("standard deviation above zero", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------ quantile

    [Fact]
    public void Quantile_asks_for_the_interior_cut_points_and_uses_exactly_those()
    {
        IReadOnlyList<double> wanted = Classification.Fractions(ClassifyBy.Quantile, 4);

        Assert.Equal([0.25, 0.5, 0.75], wanted);

        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.Quantile, 4, Over(0, 100, q: [10.0, 25.0, 70.0]));

        Assert.Equal([10.0, 25.0, 70.0, 100.0], bounds);
    }

    [Fact]
    public void Ties_across_a_quantile_boundary_collapse_the_class_rather_than_making_an_empty_one()
    {
        // <b>The data's answer, not an error.</b> A column where half the rows hold 5 has 5 as
        // several consecutive quantiles; keeping the duplicates would produce classes with
        // identical bounds and no features between them.
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.Quantile, 5, Over(0, 90, q: [5.0, 5.0, 5.0, 40.0]));

        Assert.Equal([5.0, 40.0, 90.0], bounds);
    }

    [Fact]
    public void A_quantile_classification_without_its_quantiles_says_where_to_get_them()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(ClassifyBy.Quantile, 4, Over(0, 100)));

        Assert.Contains("Classification.Fractions", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ natural breaks

    [Fact]
    public void Natural_breaks_finds_the_gaps_in_a_clustered_column()
    {
        // Three obvious clusters with wide empty gaps. Any classifier worth the name puts its
        // bounds in the gaps; equal interval would not.
        double[] values = [.. Enumerable.Repeat(1.0, 10).Concat(Enumerable.Repeat(50.0, 10))
            .Concat(Enumerable.Repeat(100.0, 10))];

        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.NaturalBreaks, 3, Over(1, 100, q: values));

        Assert.Equal([1.0, 50.0, 100.0], bounds);
    }

    [Fact]
    public void Natural_breaks_over_the_sampled_distribution_matches_the_exact_answer()
    {
        // <b>The sampling decision, measured rather than asserted.</b> Fisher is O(n²k), so the
        // classifier runs it over 254 evenly spaced quantiles instead of the column. Here the
        // column is small enough to do both: Fisher over all 600 values, and Fisher over the 254
        // quantiles of the same values. If the sample were a bad idea, these would differ.
        double[] column = [.. Enumerable.Range(0, 600)
            .Select(i => Math.Round(Math.Pow(i / 599.0, 2.5) * 1000, 4))];

        IReadOnlyList<double> exact = Classification.Bounds(
            ClassifyBy.NaturalBreaks, 5, Over(column[0], column[^1], q: column));

        IReadOnlyList<double> sampled = Classification.Bounds(
            ClassifyBy.NaturalBreaks, 5,
            Over(column[0], column[^1],
                q: [.. Classification.Fractions(ClassifyBy.NaturalBreaks, 5)
                    .Select(f => Quantile(column, f))]));

        Assert.Equal(exact.Count, sampled.Count);

        for (int i = 0; i < exact.Count; i++)
        {
            double apart = Math.Abs(exact[i] - sampled[i]);

            Assert.True(
                apart <= (column[^1] - column[0]) * 0.02,
                $"Bound {i} is {exact[i]:0.###} exactly and {sampled[i]:0.###} from the sample, "
                + $"{apart:0.###} apart on a range of {column[^1] - column[0]:0.###}. The sample "
                + "is supposed to stand in for the column.");
        }
    }

    [Fact]
    public void Natural_breaks_asks_for_the_sample_it_says_it_needs()
    {
        IReadOnlyList<double> wanted =
            Classification.Fractions(ClassifyBy.NaturalBreaks, 5);

        Assert.Equal(Classification.DistributionSample, wanted.Count);
        Assert.True(wanted[0] > 0 && wanted[^1] < 1, "The ends are the minimum and the maximum.");
        Assert.True(
            wanted.Zip(wanted.Skip(1)).All(p => p.Second > p.First), "Ascending, with no ties.");
    }

    [Fact]
    public void More_classes_than_distinct_values_gives_one_class_per_value()
    {
        IReadOnlyList<double> bounds = Classification.Bounds(
            ClassifyBy.NaturalBreaks, 8, Over(1, 3, q: [1.0, 1.0, 2.0, 3.0]));

        Assert.Equal([1.0, 2.0, 3.0], bounds);
    }

    // ------------------------------------------------------------------ shared refusals

    [Theory]
    [InlineData(ClassifyBy.EqualInterval)]
    [InlineData(ClassifyBy.GeometricalInterval)]
    [InlineData(ClassifyBy.Quantile)]
    [InlineData(ClassifyBy.NaturalBreaks)]
    public void A_column_holding_one_value_is_one_class_whatever_was_asked_for(ClassifyBy method)
    {
        // Every method below divides by the range. A column where every row holds 42 has none,
        // and one class is the honest legend.
        IReadOnlyList<double> bounds = Classification.Bounds(method, 5, Over(42, 42));

        Assert.Equal([42.0], bounds);
    }

    [Fact]
    public void Manual_is_refused_because_it_means_the_bounds_came_from_somebody()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(ClassifyBy.Manual, 4, Over(0, 100)));

        Assert.Contains("came from whoever wrote them", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void A_class_count_outside_what_a_map_can_show_is_refused(int classes)
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(ClassifyBy.EqualInterval, classes, Over(0, 100)));

        Assert.Contains("tellable apart", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_selection_is_refused_rather_than_classified()
    {
        SymbologyException refused = Assert.Throws<SymbologyException>(
            () => Classification.Bounds(
                ClassifyBy.EqualInterval, 4, Over(double.NaN, double.NaN)));

        Assert.Contains("column of nulls", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The value at a fraction of the way through a sorted column.</summary>
    /// <remarks>
    /// <b>PostgreSQL's `percentile_cont`, in the test.</b> The point of this class is that the
    /// server's numbers and these numbers are the same arithmetic; writing it out here is what
    /// lets the sampling comparison above run without a database.
    /// </remarks>
    private static double Quantile(double[] sorted, double fraction)
    {
        double at = fraction * (sorted.Length - 1);
        int low = (int)Math.Floor(at);
        int high = (int)Math.Ceiling(at);

        return low == high ? sorted[low] : sorted[low] + ((sorted[high] - sorted[low]) * (at - low));
    }
}
