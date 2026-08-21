using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The rule that turns samples into pixels, which is the Tier 1 half of raster.
/// </summary>
/// <remarks>
/// <b>Every test here runs without a file, a database or a canvas</b>, which is the
/// point of the line
/// [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4 draws.
/// The reader is a separate suite against a GDAL-written corpus; this is the
/// arithmetic of contrast and colour, and it is testable in isolation precisely
/// because no library type reaches it.
/// </remarks>
public sealed class CoverageStyleTests
{
    private static CoverageWindow Window(int width, int height, int bands, params double[] samples)
        => new(width, height, bands, samples);

    private static IReadOnlyList<BandInfo> Bands(int count, double? noData = null) =>
        [.. Enumerable.Range(0, count)
            .Select(i => new BandInfo(i, SampleKind.Unsigned8, noData, null, null))];

    [Fact]
    public void A_full_range_stretch_does_not_depend_on_what_is_in_the_window()
    {
        // <b>This is the property that makes it the default.</b> Two adjacent tiles
        // are rendered from different windows; if the contrast came from the window
        // they would disagree, and a map assembled from tiles would have a seam at
        // every boundary. Same style, different data, same answer for the same value.
        CoverageStyle style = new(StretchKind.Full);

        Rgba[] dark = style.Paint(Window(2, 1, 1, 10, 20), Bands(1));
        Rgba[] bright = style.Paint(Window(2, 1, 1, 10, 250), Bands(1));

        Assert.Equal(dark[0], bright[0]);
    }

    [Fact]
    public void A_window_stretch_uses_the_range_that_is_actually_there()
    {
        CoverageStyle style = new(StretchKind.Window);

        Rgba[] pixels = style.Paint(Window(3, 1, 1, 100, 110, 120), Bands(1));

        // The smallest becomes black and the largest white, whatever they were.
        Assert.Equal(0, pixels[0].R);
        Assert.Equal(255, pixels[2].R);
        Assert.InRange(pixels[1].R, 120, 135);
    }

    [Fact]
    public void A_fixed_stretch_without_both_ends_is_refused()
    {
        // Without both ends it is a window stretch wearing another name, and the
        // difference is exactly the one the first test is about.
        Assert.Throws<ArgumentException>(() => new CoverageStyle(StretchKind.Fixed, 0, null));
        Assert.Throws<ArgumentException>(() => new CoverageStyle(StretchKind.Fixed, null, 100));
    }

    [Fact]
    public void A_no_data_pixel_is_transparent_and_stays_out_of_the_stretch()
    {
        // <b>Both halves matter and the second is the subtle one.</b> One sentinel of
        // -9999 in a window would flatten every real value into the top of the range
        // if it were allowed into the range calculation — so the picture would be
        // wrong everywhere, not only where the sentinel is.
        CoverageStyle style = new(StretchKind.Window);

        Rgba[] pixels = style.Paint(
            Window(3, 1, 1, -9999, 100, 120), Bands(1, noData: -9999));

        Assert.Equal(0, pixels[0].A);

        // 100 and 120 are the real range, so they are the two ends of it.
        Assert.Equal(0, pixels[1].R);
        Assert.Equal(255, pixels[2].R);
    }

    [Fact]
    public void Three_bands_are_taken_as_colours_rather_than_ramped()
    {
        // A colour ramp over a photograph has no meaning, and deciding this by band
        // count rather than by a setting is what keeps that refusal from having to be
        // written anywhere.
        CoverageStyle style = new(StretchKind.Full);

        Rgba[] pixels = style.Paint(Window(1, 1, 3, 255, 0, 0), Bands(3));

        Assert.Equal(255, pixels[0].R);
        Assert.Equal(0, pixels[0].G);
        Assert.Equal(0, pixels[0].B);
        Assert.Equal(255, pixels[0].A);
    }

    [Fact]
    public void One_band_with_no_ramp_is_grey()
    {
        CoverageStyle style = new(StretchKind.Fixed, 0, 100);

        Rgba[] pixels = style.Paint(Window(1, 1, 1, 50), Bands(1));

        Assert.Equal(pixels[0].R, pixels[0].G);
        Assert.Equal(pixels[0].G, pixels[0].B);
        Assert.InRange(pixels[0].R, 120, 135);
    }

    [Fact]
    public void A_ramp_reaches_its_own_ends_exactly()
    {
        // An interpolated midpoint is a judgement; an endpoint is not. A ramp that
        // does not reach the colour somebody chose is a ramp with a different colour
        // in it than the one they asked for.
        CoverageStyle style = new(
            StretchKind.Fixed,
            0,
            100,
            [new RampStop(0, new Rgba(0, 0, 255, 255)),
             new RampStop(1, new Rgba(255, 255, 0, 255))]);

        Rgba[] pixels = style.Paint(Window(2, 1, 1, 0, 100), Bands(1));

        Assert.Equal(new Rgba(0, 0, 255, 255), pixels[0]);
        Assert.Equal(new Rgba(255, 255, 0, 255), pixels[1]);
    }

    [Fact]
    public void A_ramp_midpoint_does_not_go_through_grey()
    {
        // <b>This is why the interpolation is CIELAB and not RGB.</b> Blue to yellow
        // straight down the channels passes through (128,128,128) at the midpoint,
        // which reads as a band of "no data" across the middle of a perfectly ordinary
        // gradient. The vector renderer already made this choice for interpolate-lab
        // and two parts of one server must not disagree about what a gradient is.
        CoverageStyle style = new(
            StretchKind.Fixed,
            0,
            100,
            [new RampStop(0, new Rgba(0, 0, 255, 255)),
             new RampStop(1, new Rgba(255, 255, 0, 255))]);

        Rgba middle = style.Along(0.5);

        bool grey = Math.Abs(middle.R - middle.G) < 12
            && Math.Abs(middle.G - middle.B) < 12;

        Assert.False(
            grey,
            $"The midpoint of blue-to-yellow is ({middle.R},{middle.G},{middle.B}), which is "
            + "grey. That is what RGB interpolation does and what CIELAB exists to avoid.");
    }

    [Fact]
    public void A_position_outside_the_ramp_is_clamped_to_its_ends()
    {
        CoverageStyle style = new(
            StretchKind.Fixed,
            0,
            100,
            [new RampStop(0.25, new Rgba(10, 20, 30, 255)),
             new RampStop(0.75, new Rgba(200, 210, 220, 255))]);

        Assert.Equal(new Rgba(10, 20, 30, 255), style.Along(0));
        Assert.Equal(new Rgba(200, 210, 220, 255), style.Along(1));
    }

    [Fact]
    public void A_window_of_nothing_but_no_data_draws_nothing_and_does_not_divide_by_zero()
    {
        // The range calculation has no values to work from. Any answer will do because
        // nothing is drawn, and the one thing it must not do is produce NaN.
        CoverageStyle style = new(StretchKind.Window);

        Rgba[] pixels = style.Paint(Window(2, 1, 1, -1, -1), Bands(1, noData: -1));

        Assert.All(pixels, p => Assert.Equal(0, p.A));
    }

    [Fact]
    public void A_flat_window_does_not_divide_by_zero_either()
    {
        CoverageStyle style = new(StretchKind.Window);

        Rgba[] pixels = style.Paint(Window(2, 1, 1, 42, 42), Bands(1));

        Assert.All(pixels, p => Assert.Equal(255, p.A));
        Assert.Equal(pixels[0], pixels[1]);
    }

    [Theory]
    [InlineData("stretch:full", StretchKind.Full)]
    [InlineData("stretch:window", StretchKind.Window)]
    [InlineData("STRETCH:Window", StretchKind.Window)]
    [InlineData("stretch:0,4000", StretchKind.Fixed)]
    [InlineData(null, StretchKind.Full)]
    [InlineData("nonsense", StretchKind.Full)]
    public void The_stored_form_reads_back(string? text, StretchKind expected)
    {
        Assert.Equal(expected, CoverageStyle.Parse(text).Stretch);
    }

    [Fact]
    public void A_fixed_stretch_reads_its_numbers_back()
    {
        CoverageStyle style = CoverageStyle.Parse("stretch:-100,2500.5");

        Assert.Equal(-100, style.Minimum);
        Assert.Equal(2500.5, style.Maximum);
    }

    [Fact]
    public void The_default_is_full_range_and_grey()
    {
        // Both halves are the conservative choice: full range is the only stretch two
        // adjacent tiles agree on, and greyscale is what a measurement looks like when
        // nobody has said what it measures. A generated ramp would be this server
        // inventing meaning for somebody's data.
        Assert.Equal(StretchKind.Full, CoverageStyle.Default.Stretch);
        Assert.Empty(CoverageStyle.Default.Ramp);
    }

    [Fact]
    public void A_float_band_falls_back_to_a_unit_range_and_says_so_in_its_own_docs()
    {
        // There is no format range for a float band, so a full-range stretch has to
        // pick something. One is right for a normalised index and wrong for elevation,
        // which is why a float band almost always wants a fixed stretch.
        CoverageStyle style = new(StretchKind.Full);

        IReadOnlyList<BandInfo> bands =
            [new BandInfo(0, SampleKind.Real32, null, null, null)];

        Rgba[] pixels = style.Paint(Window(2, 1, 1, 0, 1), bands);

        Assert.Equal(0, pixels[0].R);
        Assert.Equal(255, pixels[1].R);
    }
}
