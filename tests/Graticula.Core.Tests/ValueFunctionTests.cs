using System;
using System.Linq;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>ADR-156: Clip, Remap, Mask, Statistics and Arithmetic, read by ArcGIS's argument names.</summary>
public sealed class ValueFunctionTests
{
    // A 3 × 3 window of one band: 1 to 9, row by row; cells of 1 unit from (0, 3) at the top-left.
    private static readonly CoverageWindow Nine = new(3, 3, 1, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
    private static readonly BandInfo[] OneBand = [new BandInfo(0, SampleKind.Real32, null, null, null)];

    private static RasterFunction Rule(string json)
    {
        Assert.True(RasterFunction.TryParseRule(json, out RasterFunction? function, out string? error), error);
        return function!;
    }

    private static double[] Apply(RasterFunction function) =>
        function.Apply(Nine, 1, 1, OneBand, (0, 3, 1, 1)).Samples;

    [Fact]
    public void Clip_keeps_what_is_inside_or_what_is_outside()
    {
        // The left column, 0 ≤ x ≤ 1.
        double[] inside = Apply(Rule("""{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"xmin":0,"ymin":0,"xmax":1,"ymax":3},"ClipType":1}}"""));
        Assert.Equal([1, 4, 7], new[] { inside[0], inside[3], inside[6] });
        Assert.True(double.IsNaN(inside[1]) && double.IsNaN(inside[8]));

        double[] outside = Apply(Rule("""{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingGeometry":{"rings":[[[0,0],[0,3],[1,3],[1,0],[0,0]]]},"ClipType":2}}"""));
        Assert.True(double.IsNaN(outside[0]));
        Assert.Equal(9, outside[8]);
    }

    [Fact]
    public void Remap_gives_ranges_new_values_and_the_unmatched_none_unless_allowed()
    {
        double[] remapped = Apply(Rule("""{"rasterFunction":"Remap","rasterFunctionArguments":{"InputRanges":[0,4,4,7],"OutputValues":[10,20],"NoDataRanges":[8,9]}}"""));
        Assert.Equal([10, 10, 10, 20, 20, 20], remapped.Take(6));
        Assert.True(double.IsNaN(remapped[6]), "7 is in no range");
        Assert.True(double.IsNaN(remapped[7]), "8 is a no-data range");

        double[] kept = Apply(Rule("""{"rasterFunction":"Remap","rasterFunctionArguments":{"InputRanges":[0,4],"OutputValues":[0],"AllowUnmatched":true}}"""));
        Assert.Equal(0, kept[0]);
        Assert.Equal(9, kept[8]);
    }

    [Fact]
    public void Mask_keeps_only_values_in_its_ranges()
    {
        double[] masked = Apply(Rule("""{"rasterFunction":"Mask","rasterFunctionArguments":{"IncludedRanges":[3,5]}}"""));
        Assert.Equal([3, 4, 5], masked.Skip(2).Take(3));
        Assert.True(double.IsNaN(masked[0]) && double.IsNaN(masked[5]));
        Assert.False(Rule("""{"rasterFunction":"Mask","rasterFunctionArguments":{"IncludedRanges":[3,5,0,1]}}""").FitsBands(1, out string? error));
        Assert.Contains("for each band", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Statistics_answers_a_neighbourhood_and_counts_only_cells_in_the_window()
    {
        RasterFunction mean = Rule("""{"rasterFunction":"Statistics","rasterFunctionArguments":{"Type":3,"KernelColumns":3,"KernelRows":3}}""");
        Assert.Equal(1, mean.Margin);
        double[] means = Apply(mean);
        Assert.Equal(5, means[4]);
        Assert.Equal((1 + 2 + 4 + 5) / 4.0, means[0]);

        Assert.Equal(5, Apply(Rule("""{"rasterFunction":"Statistics","rasterFunctionArguments":{"Type":5}}"""))[4]);
        Assert.Equal(9, Apply(Rule("""{"rasterFunction":"Statistics","rasterFunctionArguments":{"Type":2}}"""))[4]);
    }

    [Fact]
    public void Arithmetic_works_each_band_against_a_number()
    {
        Assert.Equal(18, Apply(Rule("""{"rasterFunction":"Arithmetic","rasterFunctionArguments":{"Raster2":2,"Operation":3}}"""))[8]);
        Assert.True(double.IsNaN(Apply(Rule("""{"rasterFunction":"Arithmetic","rasterFunctionArguments":{"Raster2":0,"Operation":4}}"""))[0]));
    }

    [Theory]
    [InlineData("""{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingRaster":{}}}""", "ClippingGeometry")]
    [InlineData("""{"rasterFunction":"Remap","rasterFunctionArguments":{"InputRanges":[0,1,2],"OutputValues":[1]}}""", "InputRanges")]
    [InlineData("""{"rasterFunction":"Statistics","rasterFunctionArguments":{"KernelColumns":4}}""", "odd")]
    [InlineData("""{"rasterFunction":"Arithmetic","rasterFunctionArguments":{"Raster2":{"url":"x"}}}""", "number")]
    [InlineData("""{"rasterFunction":"Arithmetic","rasterFunctionArguments":{"Raster2":1,"Operation":9}}""", "divided by")]
    public void What_cannot_be_applied_is_refused_saying_what_it_takes(string json, string said)
    {
        Assert.False(RasterFunction.TryParseRule(json, out _, out string? error));
        Assert.Contains(said, error, StringComparison.Ordinal);
    }
}
