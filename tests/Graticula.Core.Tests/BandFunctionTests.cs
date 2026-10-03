using System;
using System.Linq;
using Graticula.Cartography;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ADR-151: NDVI, BandArithmetic and ExtractBand — pixel by pixel, with ArcGIS's argument names and its numbering.
/// </summary>
public sealed class BandFunctionTests
{
    // Two pixels of four bands — blue, green, red, near infrared — the second with its red band absent (0 is no-data).
    private static readonly CoverageWindow Pixels = new(2, 1, 4, [10, 20, 30, 90, 10, 20, 0, 90]);

    private static readonly BandInfo[] Bands =
        [.. Enumerable.Range(0, 4).Select(i => new BandInfo(i, SampleKind.Unsigned8, 0, null, null))];

    private static RasterFunction Rule(string json)
    {
        Assert.True(RasterFunction.TryParseRule(json, out RasterFunction? function, out string? error), error);
        return function!;
    }

    [Fact]
    public void Ndvi_is_ArcGIS_s_0_to_200_by_default_and_minus_1_to_1_when_scientific()
    {
        RasterFunction plain = Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3}}""");
        RasterFunction scientific = Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3,"Scientific":true}}""");

        // (90 − 30) / (90 + 30) = 0.5.
        Assert.Equal(150, plain.Apply(Pixels, 0, 0, Bands).Samples[0], 9);
        Assert.Equal(0.5, scientific.Apply(Pixels, 0, 0, Bands).Samples[0], 9);

        // An absent band is no answer, not a zero in the arithmetic.
        Assert.True(double.IsNaN(plain.Apply(Pixels, 0, 0, Bands).Samples[1]));
        Assert.Single(plain.ResultBandsFor(Bands));
    }

    [Fact]
    public void BandArithmetic_reads_ArcGIS_s_expression_with_bands_from_one()
    {
        RasterFunction own = Rule("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":0,"BandIndexes":"(B4 - B3) / (B4 + B3)"}}""");
        RasterFunction ndvi = Rule("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":1,"BandIndexes":"4 3"}}""");
        RasterFunction savi = Rule("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":2,"BandIndexes":"4 3 0.5"}}""");

        Assert.Equal(0.5, own.Apply(Pixels, 0, 0, Bands).Samples[0], 9);
        Assert.Equal(0.5, ndvi.Apply(Pixels, 0, 0, Bands).Samples[0], 9);
        Assert.Equal(1.5 * 60 / 120.5, savi.Apply(Pixels, 0, 0, Bands).Samples[0], 9);
    }

    [Theory]
    [InlineData("-B1 + 2 * (B2 - 5)", -10 + 30)]
    [InlineData("B1 / (B2 - 20)", double.NaN)]
    [InlineData("b4*0.5", 45)]
    public void An_expression_has_the_usual_precedence_and_no_value_where_it_divides_by_zero(string text, double expected)
    {
        Assert.True(BandExpression.TryParse(text, out BandExpression? expression, out string? error), error);
        double value = expression!.Evaluate(b => Pixels.Samples[b]);

        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(value));
        }
        else
        {
            Assert.Equal(expected, value, 9);
        }
    }

    [Theory]
    [InlineData("(B4 - B3", "not closed")]
    [InlineData("NIR - B3", "'NIR'")]
    [InlineData("2 + 3", "names no band")]
    [InlineData("B4 B3", "left over")]
    public void An_expression_that_cannot_be_read_says_why(string text, string said)
    {
        Assert.False(BandExpression.TryParse(text, out _, out string? error));
        Assert.Contains(said, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractBand_keeps_the_bands_asked_in_the_order_asked_with_their_own_type()
    {
        RasterFunction extract = Rule("""{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIDs":[3,2,1]}}""");
        CoverageWindow chosen = extract.Apply(Pixels, 0, 0, Bands);

        Assert.Equal(3, chosen.Bands);
        Assert.Equal([90, 30, 20, 90, 0, 20], chosen.Samples);
        Assert.False(extract.Derives);
        Assert.Equal([SampleKind.Unsigned8, SampleKind.Unsigned8, SampleKind.Unsigned8], extract.ResultBandsFor(Bands).Select(b => b.Kind));
        Assert.Equal([0, 1, 2], extract.ResultBandsFor(Bands).Select(b => b.Index));
    }

    [Theory]
    [InlineData("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2}}""", "InfraredBandID")]
    [InlineData("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":19,"BandIndexes":"4 3 1"}}""", "Method 0")]
    [InlineData("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":1,"BandIndexes":"4"}}""", "NIR Red")]
    [InlineData("""{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandNames":["Red"]}}""", "BandIDs")]
    public void What_cannot_be_applied_is_refused_saying_what_it_needs(string json, string said)
    {
        Assert.False(RasterFunction.TryParseRule(json, out _, out string? error));
        Assert.Contains(said, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_band_the_image_does_not_have_is_refused_counting_as_ArcGIS_counts()
    {
        Assert.False(Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":3,"InfraredBandID":4}}""")
            .FitsBands(4, out string? ndvi));
        Assert.Contains("0 to 3", ndvi, StringComparison.Ordinal);

        Assert.False(Rule("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"BandIndexes":"B5 - B1"}}""")
            .FitsBands(4, out string? expression));
        Assert.Contains("B1 to B4", expression, StringComparison.Ordinal);

        // A surface function on a colour image is refused as before; NDVI on one is what it is for.
        Assert.False(new RasterFunction(RasterFunctionKind.Slope).FitsBands(4, out _));
        Assert.True(Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3}}""").FitsBands(4, out _));
    }

    [Theory]
    [InlineData("function:ndvi:2:3")]
    [InlineData("function:ndvi:2:3:s")]
    [InlineData("function:extractband:3,2,1")]
    [InlineData("function:bandarithmetic:(B4-B3)/(B4+B3)")]
    [InlineData("function:hillshade")]
    public void A_stored_function_keeps_its_arguments(string text)
    {
        Assert.Equal(text, RasterFunction.FromStyleText("stretch:auto;" + text).ToStyleText());
    }

    [Fact]
    public void A_stretch_laid_over_NDVI_draws_NDVI_s_one_band()
    {
        const string Rule = """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"Raster":{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3}}}}""";

        Assert.Contains("NDVI", DisplayRule.UnderOf(Rule), StringComparison.Ordinal);
        Assert.True(DisplayRule.TryParse(Rule, 1, out DisplayRule? rule, out string? error), error);
        Assert.NotNull(rule);
        Assert.Contains("NDVI", rule!.Under, StringComparison.Ordinal);

        // A display function under a display function is still a chain this does not make.
        Assert.True(DisplayRule.TryParse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"Raster":{"rasterFunction":"Colormap"}}}""", 1, out DisplayRule? refused, out string? why));
        Assert.Null(refused);
        Assert.Contains("chain", why, StringComparison.Ordinal);
        Assert.Null(DisplayRule.UnderOf("""{"rasterFunction":"Stretch","rasterFunctionArguments":{}}"""));
    }
}
