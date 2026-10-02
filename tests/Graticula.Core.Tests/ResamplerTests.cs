using System;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>ADR-142: an image read between its cells, as ArcGIS's <c>interpolation</c> asks.</summary>
public sealed class ResamplerTests
{
    // A 3 × 3 ramp: each cell its column × 10 plus its row × 100.
    private static readonly double[] Ramp = [0, 10, 20, 100, 110, 120, 200, 210, 220];

    private static double At(double u, double v, Resampling how, Func<double, bool>? absent = null)
    {
        Assert.True(Resampler.TryValue(Ramp, 3, 3, 1, 0, u, v, how, absent ?? (_ => false), out double value));
        return value;
    }

    [Fact]
    public void Nearest_takes_the_cell_and_bilinear_weighs_the_four_around_the_point()
    {
        Assert.Equal(110, At(1.9, 1.1, Resampling.Nearest));
        Assert.Equal(110, At(1.5, 1.5, Resampling.Bilinear), 9);
        Assert.Equal(115, At(2.0, 1.5, Resampling.Bilinear), 9);
        Assert.Equal(165, At(2.0, 2.0, Resampling.Bilinear), 9);
    }

    [Fact]
    public void Cubic_passes_through_cell_centres_and_is_exact_on_a_ramp_away_from_its_edge()
    {
        // A 5 × 5 ramp, so the sixteen cells around an inner point are all inside it.
        double[] wide = new double[25];
        for (int i = 0; i < 25; i++) wide[i] = (i % 5 * 10) + (i / 5 * 100);
        double Cubic(double u, double v) =>
            Resampler.TryValue(wide, 5, 5, 1, 0, u, v, Resampling.Cubic, _ => false, out double value) ? value : double.NaN;

        Assert.Equal(220, Cubic(2.5, 2.5), 9);
        Assert.Equal(222.5, Cubic(2.75, 2.5), 9);
    }

    [Fact]
    public void A_no_data_neighbour_falls_back_to_the_nearest_value_and_outside_is_nothing()
    {
        Assert.Equal(110, At(1.9, 1.5, Resampling.Bilinear, v => v == 120));
        Assert.False(Resampler.TryValue(Ramp, 3, 3, 1, 0, 3.2, 1, Resampling.Bilinear, _ => false, out _));
    }

    [Fact]
    public void Colours_blend_and_a_transparent_neighbour_fades_rather_than_darkens()
    {
        Rgba[] two = [new Rgba(255, 0, 0, 255), Rgba.Transparent];
        Rgba between = Resampler.Colour(two, 2, 1, 1.0, 0.5, Resampling.Bilinear);
        Assert.Equal((255, 0, 0), (between.R, between.G, between.B));
        Assert.InRange(between.A, 120, 135);
    }

    [Theory]
    [InlineData("RSP_NearestNeighbor", Resampling.Nearest)]
    [InlineData("RSP_BilinearInterpolation", Resampling.Bilinear)]
    [InlineData("RSP_CubicConvolution", Resampling.Cubic)]
    public void Arcgis_names_are_read(string name, Resampling expected)
    {
        Assert.True(Resampler.TryParse(name, out Resampling? read, out _));
        Assert.Equal(expected, read);
    }

    [Fact]
    public void Majority_and_unknown_names_are_refused_by_name()
    {
        Assert.False(Resampler.TryParse("RSP_Majority", out _, out string? why));
        Assert.Contains("RSP_Majority", why, StringComparison.Ordinal);
        Assert.False(Resampler.TryParse("smooth", out _, out why));
        Assert.True(Resampler.TryParse(null, out Resampling? none, out _));
        Assert.Null(none);
    }
}
