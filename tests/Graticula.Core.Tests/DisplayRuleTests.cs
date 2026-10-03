using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ADR-138: the <c>renderingRule</c> chains the ArcGIS Maps SDK for JavaScript 4.31 was measured sending for a renderer
/// set on an <c>ImageryLayer</c> — the JSON below is what it sent, verbatim.
/// </summary>
public sealed class DisplayRuleTests
{
    private const string MinMaxRamp = """{"rasterFunctionArguments":{"colorRamp":{"type":"algorithmic","algorithm":"esriCIELabAlgorithm","fromColor":[0,0,255,255],"toColor":[255,0,0,255]},"Raster":{"rasterFunctionArguments":{"StretchType":5,"DRA":false,"UseGamma":false,"Gamma":[],"ComputeGamma":false},"rasterFunction":"Stretch","outputPixelType":"U8","variableName":"Raster"}},"rasterFunction":"Colormap","variableName":"Raster"}""";
    private const string Deviations = """{"rasterFunctionArguments":{"StretchType":3,"DRA":false,"UseGamma":false,"Gamma":[],"ComputeGamma":false,"NumberOfStandardDeviations":2},"rasterFunction":"Stretch","outputPixelType":"U8","variableName":"Raster"}""";
    private const string Clip = """{"rasterFunctionArguments":{"colorRamp":{"type":"multipart","colorRamps":[{"type":"algorithmic","algorithm":"esriCIELabAlgorithm","fromColor":[0,0,255,255],"toColor":[255,0,0,255]},{"type":"algorithmic","algorithm":"esriHSVAlgorithm","fromColor":[255,0,0,255],"toColor":[255,255,0,255]}]},"Raster":{"rasterFunctionArguments":{"StretchType":6,"DRA":false,"UseGamma":false,"Gamma":[],"ComputeGamma":false,"MinPercent":0.5,"MaxPercent":0.5},"rasterFunction":"Stretch","outputPixelType":"U8","variableName":"Raster"}},"rasterFunction":"Colormap","variableName":"Raster"}""";
    private const string NoStretch = """{"rasterFunctionArguments":{"StretchType":0,"DRA":false,"UseGamma":false,"Gamma":[],"ComputeGamma":false},"rasterFunction":"Stretch","variableName":"Raster"}""";
    private const string Dynamic = """{"rasterFunctionArguments":{"colorRamp":{"type":"algorithmic","algorithm":"esriCIELabAlgorithm","fromColor":[0,0,255,255],"toColor":[255,0,0,255]},"Raster":{"rasterFunctionArguments":{"StretchType":5,"DRA":true,"UseGamma":false,"Gamma":[],"ComputeGamma":false},"rasterFunction":"Stretch","outputPixelType":"U8","variableName":"Raster"}},"rasterFunction":"Colormap","variableName":"Raster"}""";
    private const string Gamma = """{"rasterFunctionArguments":{"StretchType":5,"DRA":false,"UseGamma":true,"Gamma":[1.5],"ComputeGamma":false},"rasterFunction":"Stretch","outputPixelType":"U8","variableName":"Raster"}""";
    private const string Equalized = """{"rasterFunctionArguments":{"StretchType":4,"DRA":false,"UseGamma":false,"Gamma":[],"ComputeGamma":false},"rasterFunction":"Stretch","variableName":"Raster"}""";
    private const string Custom = """{"rasterFunctionArguments":{"colorRamp":{"type":"algorithmic","algorithm":"esriCIELabAlgorithm","fromColor":[0,0,255,255],"toColor":[255,0,0,255]},"Raster":{"rasterFunctionArguments":{"StretchType":5,"Statistics":[[0,100,50,10]],"DRA":false,"UseGamma":false,"Gamma":[],"ComputeGamma":false},"rasterFunction":"Stretch","outputPixelType":"U8","variableName":"Raster"}},"rasterFunction":"Colormap","variableName":"Raster"}""";
    private const string Classes = """{"rasterFunctionArguments":{"Colormap":[[0,255,0,0],[1,0,0,255]],"Raster":{"rasterFunctionArguments":{"InputRanges":[0,100.0001,100.0001,255.0001],"OutputValues":[0,1],"NoDataRanges":[]},"rasterFunction":"Remap","variableName":"Raster"}},"rasterFunction":"Colormap"}""";

    private static readonly IReadOnlyList<BandInfo> Float = [new BandInfo(0, SampleKind.Real32, -9999, null, null)];

    private static DisplayRule Parse(string json, int bands = 1)
    {
        Assert.True(DisplayRule.TryParse(json, bands, out DisplayRule? rule, out string? error), "Not read as a display rule.");
        Assert.True(rule is not null, error);
        return rule!;
    }

    private static CoverageWindow Values(params double[] values) => new(values.Length, 1, 1, values);

    [Theory]
    [InlineData(MinMaxRamp)]
    [InlineData(Deviations)]
    [InlineData(Clip)]
    [InlineData(NoStretch)]
    [InlineData(Dynamic)]
    [InlineData(Gamma)]
    [InlineData(Equalized)]
    [InlineData(Custom)]
    [InlineData(Classes)]
    public void Every_chain_the_sdk_sends_is_read(string json) => Parse(json);

    [Theory]
    [InlineData("""{"rasterFunction":"Slope"}""")]
    // ADR-156: Remap by itself is a raster function now.
    [InlineData("""{"rasterFunction":"Remap","rasterFunctionArguments":{}}""")]
    [InlineData("""{"rasterFunction":"None"}""")]
    [InlineData("")]
    [InlineData("not json")]
    public void A_raster_function_is_left_to_the_functions(string json) =>
        Assert.False(DisplayRule.TryParse(json, 1, out _, out _));

    [Theory]
    [InlineData("""{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":7}}""", "StretchType 7")]
    [InlineData("""{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"ComputeGamma":true}}""", "ComputeGamma")]
    // A raster function under the rule is the function's to refuse (ADR-151); a display function under it is this rule's.
    [InlineData("""{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"Raster":{"rasterFunction":"Colormap"}}}""", "Colormap")]
    [InlineData("""{"rasterFunction":"Colormap","rasterFunctionArguments":{"colorRamp":{"type":"algorithmic","fromColor":[0,0,0],"toColor":[9,9,9]}}}""", "raw values")]
    [InlineData("""{"rasterFunction":"Colormap","rasterFunctionArguments":{"colorRamp":{"type":"random"},"Raster":{"rasterFunction":"Stretch"}}}""", "random")]
    public void What_it_does_not_apply_is_refused_by_name(string json, string named)
    {
        Assert.True(DisplayRule.TryParse(json, 1, out DisplayRule? rule, out string? error));
        Assert.Null(rule);
        Assert.Contains(named, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colour_map_on_a_colour_image_is_refused()
    {
        Assert.True(DisplayRule.TryParse(MinMaxRamp, 3, out DisplayRule? rule, out string? error));
        Assert.Null(rule);
        Assert.Contains("colour image", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Minimum_maximum_runs_the_ramp_from_the_services_lowest_to_its_highest()
    {
        DisplayRule rule = Parse(MinMaxRamp);
        Assert.True(rule.NeedsStatistics);
        BandSummary service = BandSummary.Of(Values(10, 20, 110), 0, Float[0]);

        Rgba[] drawn = rule.Paint(Values(10, 110, -9999, double.NaN, 60), Float, [service]);

        Assert.Equal(new Rgba(0, 0, 255, 255), drawn[0]);
        Assert.Equal(new Rgba(255, 0, 0, 255), drawn[1]);
        Assert.Equal(Rgba.Transparent, drawn[2]);
        Assert.Equal(Rgba.Transparent, drawn[3]);
        Assert.NotEqual(drawn[0], drawn[4]);
    }

    [Fact]
    public void The_rules_own_statistics_need_none_of_the_services()
    {
        DisplayRule rule = Parse(Custom);
        Assert.False(rule.NeedsStatistics);

        Rgba[] drawn = rule.Paint(Values(-5, 100, 200), Float, null);

        Assert.Equal(new Rgba(0, 0, 255, 255), drawn[0]);
        Assert.Equal(new Rgba(255, 0, 0, 255), drawn[1]);
        Assert.Equal(new Rgba(255, 0, 0, 255), drawn[2]);
    }

    [Fact]
    public void A_dynamic_range_stretches_over_the_window_not_the_service()
    {
        DisplayRule rule = Parse(Dynamic);
        Assert.False(rule.NeedsStatistics);

        Rgba[] drawn = rule.Paint(Values(500, 600), Float, null);

        Assert.Equal(new Rgba(0, 0, 255, 255), drawn[0]);
        Assert.Equal(new Rgba(255, 0, 0, 255), drawn[1]);
    }

    [Fact]
    public void Standard_deviations_spread_either_side_of_the_mean()
    {
        BandSummary service = new(0, 1000, 500, 100, 0, 1000, new long[256]);

        Rgba[] drawn = Parse(Deviations).Paint(Values(300, 500, 700, 900), Float, [service]);

        Assert.Equal([0, 128, 255, 255], drawn.Select(p => (int)p.R));
        Assert.All(drawn, p => Assert.True(p.R == p.G && p.G == p.B));
    }

    [Fact]
    public void A_gamma_above_one_lifts_the_middle_and_none_draws_the_values_as_they_are()
    {
        BandSummary service = new(0, 255, 0, 0, 0, 255, new long[256]);
        Assert.True(Parse(Gamma).Paint(Values(64), Float, [service])[0].R > 64 + 30);
        Assert.Equal([0, 64, 255, 255], Parse(NoStretch).Paint(Values(0, 64, 255, 900), Float, null).Select(p => (int)p.R));
    }

    [Fact]
    public void A_percent_clip_leaves_the_outliers_at_the_ends()
    {
        double[] values = [.. Enumerable.Range(0, 1000).Select(i => (double)i)];
        values[0] = -100000;
        values[999] = 100000;
        BandSummary service = BandSummary.Of(new CoverageWindow(1000, 1, 1, values), 0, Float[0]);
        BandSummary clipped = new(service.Minimum, service.Maximum, service.Mean, service.StandardDeviation, 0, 1000,
            [.. Enumerable.Range(0, 256).Select(b => b == 0 || b == 255 ? 3L : 4L)]);

        Rgba[] drawn = Parse(Clip).Paint(Values(0, 500, 1000), Float, [clipped]);

        Assert.Equal(new Rgba(0, 0, 255, 255), drawn[0]);
        Assert.Equal(new Rgba(255, 255, 0, 255), drawn[2]);
        Assert.Equal(new Rgba(255, 0, 0, 255), drawn[1]);
    }

    [Fact]
    public void Equalization_gives_each_level_as_many_pixels()
    {
        double[] skewed = [.. Enumerable.Range(0, 100).Select(i => i < 90 ? i / 90.0 : 1000 + i)];
        BandSummary service = BandSummary.Of(new CoverageWindow(100, 1, 1, skewed), 0, Float[0]);

        Rgba[] drawn = Parse(Equalized).Paint(Values(0.5, 1099), Float, [service]);

        // Nine pixels in ten are under 1, so 0.5 is near the middle rather than at the floor a linear stretch puts it.
        Assert.InRange(drawn[0].R, 200, 235);
        Assert.Equal(255, drawn[1].R);
    }

    [Fact]
    public void Classes_are_coloured_by_their_range_and_a_value_outside_them_is_clear()
    {
        IReadOnlyList<BandInfo> bytes = [new BandInfo(0, SampleKind.Unsigned8, null, null, null)];

        Rgba[] drawn = Parse(Classes).Paint(new CoverageWindow(3, 1, 1, [40, 180, 300]), bytes, null);

        Assert.Equal(new Rgba(255, 0, 0, 255), drawn[0]);
        Assert.Equal(new Rgba(0, 0, 255, 255), drawn[1]);
        Assert.Equal(Rgba.Transparent, drawn[2]);
    }

    [Fact]
    public void A_colour_image_is_stretched_band_by_band()
    {
        IReadOnlyList<BandInfo> rgb = [.. Enumerable.Range(0, 3).Select(b => new BandInfo(b, SampleKind.Unsigned16, null, null, null))];
        BandSummary[] service = [new(0, 100, 0, 0, 0, 100, new long[256]), new(0, 200, 0, 0, 0, 200, new long[256]), new(0, 400, 0, 0, 0, 400, new long[256])];
        DisplayRule rule = Parse("""{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""", 3);

        Rgba[] drawn = rule.Paint(new CoverageWindow(1, 1, 3, [100, 100, 100]), rgb, service);

        Assert.Equal(new Rgba(255, 128, 64, 255), drawn[0]);
    }
}
