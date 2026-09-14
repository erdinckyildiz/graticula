using Graticula.Cartography;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>ADR-070: the arithmetic every face of a layer's visible range agrees on.</summary>
public sealed class VisibleScaleRangeTests
{
    [Fact]
    public void Level_zero_matches_the_scale_every_512_pixel_scheme_states()
    {
        Assert.Equal(295_828_763.795777, VisibleScaleRange.VectorTileScale(0), 6);
        Assert.Equal(591_657_527.591555 / 2, VisibleScaleRange.ScaleOf(156_543.03392804097 / 2), 0);
    }

    [Fact]
    public void An_unlimited_range_carries_every_tile_and_draws_at_every_scale()
    {
        VisibleScaleRange range = VisibleScaleRange.Unlimited;

        Assert.False(range.IsLimited);
        Assert.True(range.CarriesVectorTile(0));
        Assert.True(range.CarriesVectorTile(22));
        Assert.True(range.DrawsAt(1e9));
        Assert.Null(range.StyleMinZoom);
    }

    [Fact]
    public void A_range_set_at_a_level_scale_starts_carrying_tiles_at_that_level()
    {
        // The suggestion stores the scale of a level exactly; the level above it is the dense one.
        VisibleScaleRange range = new(VisibleScaleRange.VectorTileScale(13), 0);

        Assert.False(range.CarriesVectorTile(12));
        Assert.True(range.CarriesVectorTile(13));
        Assert.True(range.CarriesVectorTile(20));
        Assert.Equal(13, range.StyleMinZoom!.Value, 9);
    }

    [Fact]
    public void A_range_between_levels_keeps_the_level_that_is_on_screen_across_it()
    {
        // 1:50,000 lies between level 12 (1:72,224) and level 13 (1:36,112). A level-12 tile is on
        // screen from 1:72,224 down to 1:36,112, and part of that is inside the range.
        VisibleScaleRange range = new(50_000, 0);

        Assert.False(range.CarriesVectorTile(11));
        Assert.True(range.CarriesVectorTile(12));
        Assert.True(range.DrawsAt(50_000));
        Assert.False(range.DrawsAt(50_001));
    }

    [Fact]
    public void The_zoomed_in_limit_stops_tiles_that_are_only_on_screen_closer_than_it()
    {
        VisibleScaleRange range = new(0, VisibleScaleRange.VectorTileScale(16));

        Assert.True(range.CarriesVectorTile(16));
        Assert.False(range.CarriesVectorTile(17));
        Assert.False(range.DrawsAt(VisibleScaleRange.VectorTileScale(16) / 1.5));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(10_000, 50_000)]
    [InlineData(50_000, 50_000)]
    [InlineData(2e10, 0)]
    public void A_range_that_cannot_draw_or_is_not_a_scale_is_refused(double min, double max) =>
        Assert.NotNull(VisibleScaleRange.Refusal(min, max));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50_000, 0)]
    [InlineData(0, 1_000)]
    [InlineData(50_000, 1_000)]
    public void A_range_that_draws_somewhere_is_accepted(double min, double max) =>
        Assert.Null(VisibleScaleRange.Refusal(min, max));

    [Fact]
    public void The_wms_denominator_is_the_same_map_on_a_028_mm_pixel()
    {
        // One metre per pixel: 3,779.52 at 96 dpi, 3,571.43 at 0.28 mm.
        Assert.Equal(1 / 0.00028, VisibleScaleRange.WmsDenominator(VisibleScaleRange.DotsPerMetre), 6);
    }
}
