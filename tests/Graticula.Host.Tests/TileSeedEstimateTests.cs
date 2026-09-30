using System.Collections.Generic;
using Graticula.Geometries;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// How many bytes a seed adds to the cache, and whether they fit — ADR-093 §3.
/// </summary>
/// <remarks>
/// <b>Arithmetic, worked by hand beside each case.</b> The estimate decides whether a start is refused,
/// so each number here is written out rather than recomputed the way the code computes it — a test
/// that repeated the formula would agree with any mistake in it.
/// </remarks>
public sealed class TileSeedEstimateTests
{
    private static readonly Dictionary<int, TileSeedEstimate.Holding> Nothing = [];

    private static readonly Dictionary<int, long> Empty = [];

    private static TileSeedEstimate.Layer Everywhere(Dictionary<int, TileSeedEstimate.Holding>? held = null) =>
        new(_ => true, held ?? Nothing);

    [Theory]
    [InlineData(22, 16 * 1024)]
    [InlineData(16, 16 * 1024)]
    [InlineData(15, 32 * 1024)]
    [InlineData(14, 64 * 1024)]
    [InlineData(12, 256 * 1024)]
    [InlineData(11, 512 * 1024)]
    [InlineData(10, 1024 * 1024)]
    [InlineData(4, 1024 * 1024)]
    [InlineData(0, 1024 * 1024)]
    public void The_default_is_16_KB_at_level_16_doubling_below_up_to_a_megabyte(int zoom, long bytes) =>
        Assert.Equal(bytes, TileSeedEstimate.DefaultPartBytes(zoom));

    [Fact]
    public void Without_samples_each_tile_costs_the_default_for_its_level()
    {
        // Level 14: 10 tiles × 64 KB = 655,360. Level 15: 40 × 32 KB = 1,310,720.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(14, 0, 0, 4, 1), new TileRange(15, 0, 0, 9, 3)],
            [Everywhere()],
            budget: 10_000_000,
            used: 0,
            Empty);

        Assert.Equal(655_360, estimate.Levels[0].Bytes);
        Assert.Equal(1_310_720, estimate.Levels[1].Bytes);
        Assert.Equal(1_966_080, estimate.Bytes);
        Assert.False(estimate.Sampled);
        Assert.True(estimate.Fits);
    }

    [Fact]
    public void On_another_grid_a_level_costs_the_default_of_the_mercator_level_with_its_pixel()
    {
        // TM30's level 8 is 13.28 m a pixel — Mercator's level 13 — so 10 tiles × 128 KB, not 10 × 1 MB as a
        // Mercator level 8 would be. 2026-09-30: an export of levels 0-8 on TM30 was estimated at 85 GB.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(8, 0, 0, 4, 1)],
            [Everywhere()],
            budget: 10_000_000,
            used: 0,
            Empty,
            VectorTileSchemes.Find("turef-tm30")!.Scheme);

        Assert.Equal(1_310_720, estimate.Bytes);
    }

    [Fact]
    public void Enough_cached_tiles_replace_the_default_with_their_average_and_present_ones_add_nothing()
    {
        // 20 samples of 2,000 bytes on average; 5 of the 12 tiles are already there: 7 × 2,000.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(12, 0, 0, 3, 2)],
            [Everywhere(new() { [12] = new(Samples: 20, SampleBytes: 40_000, Present: 5) })],
            budget: 1_000_000,
            used: 0,
            Empty);

        Assert.Equal(14_000, estimate.Bytes);
        Assert.True(estimate.Sampled);
    }

    [Fact]
    public void Too_few_samples_are_not_trusted()
    {
        // 15 samples is one short: 4 tiles at level 16 take the 16 KB default, less the 1 present = 3 × 16,384.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(16, 0, 0, 1, 1)],
            [Everywhere(new() { [16] = new(Samples: TileSeedEstimate.SamplesNeeded - 1, SampleBytes: 15, Present: 1) })],
            budget: 1_000_000,
            used: 0,
            Empty);

        Assert.Equal(49_152, estimate.Bytes);
        Assert.False(estimate.Sampled);
    }

    [Fact]
    public void Every_layer_drawn_at_a_level_adds_its_part_and_one_left_out_adds_nothing()
    {
        // Two layers at level 16 (2 × 16 KB), one of them only above level 15; level 15 has one layer (32 KB).
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(15, 0, 0, 0, 0), new TileRange(16, 0, 0, 0, 0)],
            [Everywhere(), new TileSeedEstimate.Layer(z => z >= 16, Nothing)],
            budget: 1_000_000,
            used: 0,
            Empty);

        Assert.Equal(32_768, estimate.Levels[0].Bytes);
        Assert.Equal(32_768, estimate.Levels[1].Bytes);
    }

    [Fact]
    public void A_level_no_layer_draws_at_costs_nothing()
    {
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(3, 0, 0, 7, 7)],
            [new TileSeedEstimate.Layer(z => z >= 10, Nothing)],
            budget: 1,
            used: 0,
            Empty);

        Assert.Equal(0, estimate.Bytes);
        Assert.True(estimate.Fits);
    }

    /// <remarks>
    /// <b>A warm cache with little free space still takes a seed</b>, because eviction makes the room
    /// from tiles deeper than any of the seed's: the owner's worry is a seed evicting its own tiles, and
    /// this one evicts nobody's but other maps' level 18.
    /// </remarks>
    [Fact]
    public void A_seed_larger_than_the_free_space_fits_when_eviction_takes_deeper_tiles_first()
    {
        // Budget 100,000, eviction target 90,000; the cache holds 95,000, all at level 18: 5,000 free.
        // Levels 16 and 17, one tile each at 16,384: running 16,384 / 32,768, both past the free space.
        // Nothing is held below 16 or 17, so 16,384 and 32,768 are each ≤ 90,000: every tile is kept.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(16, 0, 0, 0, 0), new TileRange(17, 0, 0, 0, 0)],
            [Everywhere()],
            budget: 100_000,
            used: 95_000,
            new Dictionary<int, long> { [18] = 95_000 });

        Assert.Equal(32_768, estimate.Bytes);
        Assert.Equal(5_000, estimate.Free);
        Assert.Equal(90_000, estimate.Target);
        Assert.Equal(0, estimate.Protected);
        Assert.True(estimate.Fits);
        Assert.Equal(17, estimate.HighestLevelThatFits);
    }

    /// <remarks>
    /// <b>Tiles at a level inside the seed's range outrank its higher levels</b>, and they are the case a
    /// check of only what lies below the seed's first level would miss: other maps' level-15 tiles are
    /// kept ahead of the seed's own level 16.
    /// </remarks>
    [Fact]
    public void Tiles_held_below_a_seed_level_outrank_it_even_inside_the_seeds_range()
    {
        // Budget 100,000, target 90,000. Held: 70,000 at level 15 (other maps), 25,000 at level 18;
        // used 95,000, free 5,000.
        // Level 15: running 32,768 > 5,000 free; below 15 is 0, and 32,768 ≤ 90,000 — kept.
        // Level 16: running 49,152; below 16 is 70,000, and 119,152 > 90,000 — the seed's level 16 goes.
        // (Only what lies below level 15 — nothing — would have called 49,152 a fit.)
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(15, 0, 0, 0, 0), new TileRange(16, 0, 0, 0, 0)],
            [Everywhere()],
            budget: 100_000,
            used: 95_000,
            new Dictionary<int, long> { [15] = 70_000, [18] = 25_000 });

        Assert.Equal(49_152, estimate.Bytes);
        Assert.Equal(70_000, estimate.Protected);
        Assert.False(estimate.Fits);
        Assert.Equal(15, estimate.HighestLevelThatFits);
    }

    /// <remarks>
    /// <b>A seed that fits in the free space fits, whatever outranks it</b>: nothing is evicted at all.
    /// </remarks>
    [Fact]
    public void A_seed_inside_the_free_space_fits_even_past_the_eviction_target()
    {
        // Budget 100,000; held 80,000 at level 14; free 20,000. Level 16: 16,384 ≤ 20,000.
        // Below 16 is 80,000, and 96,384 > 90,000 — which would matter only if something were evicted.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(16, 0, 0, 0, 0)],
            [Everywhere()],
            budget: 100_000,
            used: 80_000,
            new Dictionary<int, long> { [14] = 80_000 });

        Assert.True(estimate.Fits);
        Assert.Equal(16, estimate.HighestLevelThatFits);
    }

    [Fact]
    public void When_not_even_the_first_level_fits_there_is_no_level_that_does()
    {
        // Budget 1,000, target 900; held 5,000 at level 3; free 0. Level 14: 65,536 > 0, and
        // 5,000 + 65,536 > 900.
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(14, 0, 0, 0, 0)],
            [Everywhere()],
            budget: 1_000,
            used: 5_000,
            new Dictionary<int, long> { [3] = 5_000 });

        Assert.Equal(0, estimate.Free);
        Assert.False(estimate.Fits);
        Assert.Null(estimate.HighestLevelThatFits);
    }

    [Fact]
    public void An_estimate_that_would_overflow_stays_at_the_largest_number_rather_than_going_negative()
    {
        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            [new TileRange(0, 0, 0, int.MaxValue - 1, int.MaxValue - 1)],
            [Everywhere()],
            budget: long.MaxValue,
            used: 0,
            Empty);

        Assert.Equal(long.MaxValue, estimate.Bytes);
    }

    [Theory]
    [InlineData(700, "1 KB")]
    [InlineData(65_536, "64 KB")]
    [InlineData(5_452_595, "5.2 MB")]
    [InlineData(2_147_483_648, "2 GB")]
    public void A_size_is_written_as_a_person_reads_it(long bytes, string said) =>
        Assert.Equal(said, TileSeedEstimate.Size(bytes));

    [Fact]
    public void The_refusal_names_the_estimate_the_budget_the_levels_that_fit_and_the_override()
    {
        const double Half = TileAddress.WebMercatorHalfExtent;
        TileSeedPlan plan = TileSeedPlan.For(new Envelope(-Half, -Half, Half, Half), 0, 2);

        TileSeedEstimate.Result estimate = TileSeedEstimate.Of(
            plan.Levels, [Everywhere()], budget: 3 * 1048576L, used: 0, Empty);

        // 1 + 4 + 16 tiles at a megabyte each against a 3 MB budget, target 2,831,155 (2.7 MB): level 0 is
        // 1 MB and inside the free space; levels 0 and 1 are 5 MB, past both — only level 0 is kept.
        string said = AdminEndpoints.TooLargeForTheCache(plan, estimate);

        Assert.Contains("21 MB", said, System.StringComparison.Ordinal);
        Assert.Contains("evicts down to 2.7 MB", said, System.StringComparison.Ordinal);
        Assert.Contains("90% of its 3 MB budget", said, System.StringComparison.Ordinal);
        Assert.Contains("Levels 0 to 0 keep every tile they build", said, System.StringComparison.Ordinal);
        Assert.Contains("3 MB is free now", said, System.StringComparison.Ordinal);
        Assert.Contains("\"force\": true", said, System.StringComparison.Ordinal);
        Assert.Contains("16 KB per layer at level 16", said, System.StringComparison.Ordinal);
    }
}
