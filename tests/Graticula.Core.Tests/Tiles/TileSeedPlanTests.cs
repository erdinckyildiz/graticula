using System;
using System.Linq;
using Graticula.Geometries;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Core.Tests.Tiles;

/// <summary>
/// Which tiles a seed covers, how many, in what order, and where it goes on from — ADR-093.
/// </summary>
/// <remarks>
/// <b>The count is what the cap refuses on and what the operator narrows against</b>, so an off-by-one
/// here is either a seed refused that fits or a seam of cold tiles along an edge nobody asked to leave
/// out. Every figure below is worked by hand from the grid, not read back from the code.
/// </remarks>
public sealed class TileSeedPlanTests
{
    private const double Half = TileAddress.WebMercatorHalfExtent;

    private static readonly Envelope World = new(-Half, -Half, Half, Half);

    [Fact]
    public void The_whole_world_is_four_to_the_level_at_every_level()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 3);

        Assert.Equal([1L, 4L, 16L, 64L], plan.Levels.Select(level => level.Count));
        Assert.Equal(85, plan.Total);
    }

    [Fact]
    public void Levels_come_lowest_first()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 2, 5);

        Assert.Equal([2, 3, 4, 5], plan.Levels.Select(level => level.Z));
    }

    [Fact]
    public void An_area_inside_one_tile_is_one_tile_per_level()
    {
        // The north-east quarter's interior, well away from any boundary at levels 0 and 1.
        Envelope area = new(Half / 4, Half / 4, Half / 2, Half / 2);

        TileSeedPlan plan = TileSeedPlan.For(area, 0, 1);

        Assert.Equal(new TileRange(0, 0, 0, 0, 0), plan.Levels[0]);
        Assert.Equal(new TileRange(1, 1, 0, 1, 0), plan.Levels[1]);
    }

    /// <remarks>
    /// <b>Touching counts</b>, because a vector tile carries a buffer and draws a feature on its
    /// neighbour's edge: the box whose east edge is exactly the meridian includes the column east of it.
    /// </remarks>
    [Fact]
    public void An_area_that_touches_a_tile_edge_includes_the_tile_beyond_it()
    {
        Envelope westHalf = new(-Half / 2, -Half / 2, 0, Half / 2);

        TileRange level1 = TileSeedPlan.For(westHalf, 1, 1).Levels[0];

        Assert.Equal(0, level1.MinX);
        Assert.Equal(1, level1.MaxX);
    }

    [Fact]
    public void The_squares_own_edges_stay_inside_the_last_column_and_row()
    {
        TileRange level2 = TileSeedPlan.For(World, 2, 2).Levels[0];

        Assert.Equal(new TileRange(2, 0, 0, 3, 3), level2);
    }

    [Fact]
    public void An_area_larger_than_the_square_is_clipped_to_it()
    {
        Envelope beyond = new(-3 * Half, -3 * Half, 3 * Half, 3 * Half);

        TileSeedPlan plan = TileSeedPlan.For(beyond, 0, 2);

        Assert.Equal(21, plan.Total);
        Assert.Equal(World, plan.Area);
    }

    [Fact]
    public void Rows_count_down_from_the_north()
    {
        // A thin strip just south of the top edge is row 0; just north of the bottom edge is the last row.
        TileRange north = TileSeedPlan.RangeOf(new Envelope(-10, Half - 10, 10, Half - 1), 3);
        TileRange south = TileSeedPlan.RangeOf(new Envelope(-10, -Half + 1, 10, -Half + 10), 3);

        Assert.Equal(0, north.MinY);
        Assert.Equal(0, north.MaxY);
        Assert.Equal(7, south.MinY);
        Assert.Equal(7, south.MaxY);
    }

    [Fact]
    public void The_order_within_a_level_is_row_by_row_west_to_east()
    {
        TileRange range = new(5, 10, 20, 12, 21);

        Assert.Equal(6, range.Count);
        Assert.Equal(new TileAddress(5, 10, 20), range.At(0));
        Assert.Equal(new TileAddress(5, 12, 20), range.At(2));
        Assert.Equal(new TileAddress(5, 10, 21), range.At(3));
        Assert.Equal(new TileAddress(5, 12, 21), range.At(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => range.At(6));
    }

    [Fact]
    public void Every_address_in_a_range_is_reached_exactly_once()
    {
        TileRange range = TileSeedPlan.RangeOf(new Envelope(-1_000_000, -500_000, 2_000_000, 750_000), 9);

        TileAddress[] walked = [.. Enumerable.Range(0, (int)range.Count).Select(i => range.At(i))];

        Assert.Equal(walked.Length, walked.Distinct().Count());
        Assert.All(walked, address => Assert.True(range.Contains(address) && address.IsValid));
    }

    [Fact]
    public void A_backwards_or_out_of_pyramid_range_is_refused_by_name()
    {
        ArgumentException backwards = Assert.Throws<ArgumentException>(() => TileSeedPlan.For(World, 5, 3));
        Assert.Contains("5 to 3", backwards.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => TileSeedPlan.For(World, 0, 23));
        Assert.Throws<ArgumentException>(() => TileSeedPlan.For(World, -1, 2));
    }

    [Fact]
    public void Degrees_labelled_as_metres_far_outside_the_square_are_refused()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => TileSeedPlan.For(new Envelope(1e9, 1e9, 2e9, 2e9), 0, 1));

        Assert.Contains("outside the Web Mercator square", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Geographic_corners_become_web_mercator_by_the_closed_formula()
    {
        Envelope mercator = TileSeedPlan.FromGeographic(new Envelope(-180, -90, 180, 90));

        // Latitude is clipped at the square's own edge, so the whole globe is exactly the square.
        Assert.Equal(-Half, mercator.MinX, 6);
        Assert.Equal(Half, mercator.MaxX, 6);
        Assert.Equal(-Half, mercator.MinY, 3);
        Assert.Equal(Half, mercator.MaxY, 3);

        // Istanbul, 29°E 41°N — a figure any Web Mercator calculator gives.
        Envelope point = TileSeedPlan.FromGeographic(new Envelope(29, 41, 29, 41));
        Assert.Equal(3_228_265.2, point.MinX, 0);
        Assert.Equal(5_012_341.7, point.MinY, 0);
    }

    [Fact]
    public void A_resume_goes_on_from_the_first_unfinished_level()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 2);

        Assert.Equal((0, 0L), plan.ResumeFrom([0, 0, 0]));
        Assert.Equal((1, 3L), plan.ResumeFrom([1, 3, 0]));
        Assert.Equal((2, 0L), plan.ResumeFrom([1, 4, 0]));
        Assert.Null(plan.ResumeFrom([1, 4, 16]));
    }

    [Fact]
    public void A_cursor_that_does_not_fit_the_plan_is_refused()
    {
        TileSeedPlan plan = TileSeedPlan.For(World, 0, 2);

        Assert.Throws<ArgumentException>(() => plan.ResumeFrom([0, 0]));
        Assert.Throws<ArgumentException>(() => plan.ResumeFrom([2, 0, 0]));
        Assert.Throws<ArgumentException>(() => plan.ResumeFrom([-1, 0, 0]));
    }

    [Fact]
    public void The_estimate_is_the_straight_line_from_the_rate_so_far()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), TileSeedPlan.Remaining(100, 300, TimeSpan.FromSeconds(100)));
        Assert.Null(TileSeedPlan.Remaining(0, 300, TimeSpan.FromSeconds(100)));
        Assert.Null(TileSeedPlan.Remaining(100, 300, TimeSpan.Zero));
    }

    /// <remarks>
    /// <b>The count at the cap's scale is exact, not estimated.</b> Istanbul's municipality from
    /// level 0 to 16 is 95,258 tiles, worked independently of this code with the same floor-and-clamp
    /// on each level's rectangle — well inside the default cap of 250,000.
    /// </remarks>
    [Fact]
    public void A_city_to_level_sixteen_is_counted_exactly()
    {
        Envelope istanbul = TileSeedPlan.FromGeographic(new Envelope(27.9, 40.8, 29.9, 41.6));

        TileSeedPlan plan = TileSeedPlan.For(istanbul, 0, 16);

        long summed = 0;

        foreach (TileRange level in plan.Levels)
        {
            summed += level.Width * level.Height;
        }

        Assert.Equal(summed, plan.Total);
        Assert.Equal(95_258, plan.Total);
    }
}
