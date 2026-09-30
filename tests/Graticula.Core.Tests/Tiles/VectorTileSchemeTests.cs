using System;
using System.Linq;
using Graticula.Cartography;
using Graticula.Geometries;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Core.Tests.Tiles;

/// <summary>
/// The grid a vector tile service is cut on — ADR-096.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two promises, and they are tested from opposite ends.</b> Web Mercator answers every question the
/// code before schemes answered, with the same numbers — so no Mercator tile moves. Any other grid answers
/// from its own origin and resolutions — so a TUREF tile is where a TUREF client expects it.
/// </para>
/// <para>
/// <b>The TUREF numbers are worked by hand, not read back.</b> TM30's area of use (EPSG register v12.013)
/// is 28.5–31.5°E, 36.06–41.46°N; projected with the Krüger series it spans x 364,852.207–635,147.793 and
/// y 3,992,200.244–4,592,746.422. Rounded outward to the kilometre the origin is (364,000, 4,593,000); the
/// longer side from it is max(635,147.793 − 364,000, 4,593,000 − 3,992,200.244) = 600,799.756 m, rounded up
/// to 601,000; level 0 is 601,000 / 512 = 1,173.828125 m a pixel; levels halve until a pixel is no bigger
/// than Web Mercator's at z22, 0.018661 m, which 1,173.828125 / 2^16 = 0.017911 is and 2^15 is not — so
/// seventeen levels, 0 to 16.
/// </para>
/// </remarks>
public sealed class VectorTileSchemeTests
{
    private const double Half = TileAddress.WebMercatorHalfExtent;

    [Theory]
    [InlineData(0, 5)]
    [InlineData(8, 13)]
    [InlineData(16, 21)]
    public void A_levels_mercator_equivalent_is_the_mercator_level_with_the_same_pixel(int level, int mercator)
    {
        // TM30's level 0 is 3,400.39 m a pixel; Mercator's level 5 is 2,445.98 m and level 4 4,891.97 m, and
        // 3,400.39 is nearer 5 on the doubling scale (2^0.47 from it). Both halve per level.
        Assert.Equal(mercator, Tm30.MercatorLevelOf(level));
        Assert.Equal(level, VectorTileScheme.WebMercator.MercatorLevelOf(level));
    }

    private static VectorTileScheme Tm30 => VectorTileSchemes.Find("turef-tm30")!.Scheme;

    // ---------- Web Mercator is what it was ----------

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(12, 2048, 2047)]
    [InlineData(22, 4_000_000, 1_000_000)]
    public void Web_Mercator_puts_a_tile_where_TileAddress_always_did(int z, int x, int y)
    {
        TileAddress address = new(z, x, y);

        Assert.Equal(address.WebMercatorEnvelope(), VectorTileScheme.WebMercator.Envelope(address));
        Assert.Equal(address.Rejection(), VectorTileScheme.WebMercator.Rejection(address));
    }

    [Fact]
    public void Web_Mercator_refuses_what_TileAddress_refused()
    {
        foreach (TileAddress wrong in new TileAddress[] { new(23, 0, 0), new(2, 4, 0), new(-1, 0, 0), new(3, 0, 8) })
        {
            Assert.Equal(wrong.Rejection(), VectorTileScheme.WebMercator.Rejection(wrong));
            Assert.NotNull(VectorTileScheme.WebMercator.Rejection(wrong));
        }
    }

    [Fact]
    public void Web_Mercator_has_no_fingerprint_so_no_cache_key_moves()
    {
        Assert.Null(VectorTileScheme.WebMercator.Fingerprint);
        Assert.Null(VectorTileScheme.WebMercator.ToJson());
        Assert.Equal(VectorTileScheme.WebMercatorId, VectorTileScheme.WebMercator.Key);

        string before = TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 4096, 64, null);
        string with = TileCacheKey.FingerprintOf(3857, "geom", ["a", "b"], 4096, 64, null, VectorTileScheme.WebMercator.Fingerprint);

        Assert.Equal(before, with);
    }

    [Fact]
    public void Web_Mercator_simplifies_through_z14_and_not_after()
    {
        // Q-157 as the Mercator statement says it: `@z <= 14`.
        Assert.True(VectorTileScheme.WebMercator.Simplifies(14));
        Assert.False(VectorTileScheme.WebMercator.Simplifies(15));

        // And the ground form of the same threshold agrees with it: z14's pixel is exactly the limit.
        Assert.Equal(VectorTileScheme.WebMercator.Resolution(14), VectorTileScheme.SimplifiedDownToResolution, 12);
        Assert.Equal(4.777314267823516, VectorTileScheme.SimplifiedDownToResolution, 12);
    }

    [Fact]
    public void Web_Mercator_draws_a_visible_range_exactly_as_CarriesVectorTile_does()
    {
        VisibleScaleRange[] ranges =
        [
            VisibleScaleRange.Unlimited,
            new(50_000, 0),
            new(0, 5_000),
            new(1_000_000, 10_000),
            new(VisibleScaleRange.VectorTileScale(10), 0),
        ];

        foreach (VisibleScaleRange range in ranges)
        {
            for (int z = 0; z <= TileAddress.MaxZoom; z++)
            {
                Assert.Equal(range.CarriesVectorTile(z), VectorTileScheme.WebMercator.Draws(range, z));
            }
        }
    }

    [Fact]
    public void Web_Mercator_counts_a_seed_as_TileSeedPlan_always_did()
    {
        Envelope area = new(-1_000_000, -500_000, 2_000_000, 750_000);

        TileSeedPlan old = TileSeedPlan.For(area, 3, 9);
        TileSeedPlan now = TileSeedPlan.For(VectorTileScheme.WebMercator, area, 3, 9);

        Assert.Equal(old.Levels, now.Levels);
        Assert.Equal(old.Total, now.Total);
        Assert.Equal(TileSeedPlan.RangeOf(area, 9), VectorTileScheme.WebMercator.RangeOf(area, 9));
    }

    [Fact]
    public void Web_Mercator_level_zero_is_the_whole_square_in_512_pixels()
    {
        Assert.Equal(23, VectorTileScheme.WebMercator.LevelCount);
        Assert.Equal(2 * Half / 512, VectorTileScheme.WebMercator.Resolution(0), 9);
        Assert.Equal(VisibleScaleRange.VectorTileLevel0Scale, VectorTileScheme.WebMercator.Scale(0), 6);
        Assert.Equal(1 << 12, VectorTileScheme.WebMercator.TilesAcross(12));
    }

    // ---------- TUREF, derived by hand ----------

    [Fact]
    public void TM30_is_derived_from_the_country_as_the_remarks_work_it_out()
    {
        VectorTileScheme tm30 = Tm30;

        // D-288: the country, 25.62-44.83°E and 35.81-42.15°N, projected into TM30 is x 104,015-1,844,996 and
        // y 3,964,461-4,776,137. The origin rounds outward to (104,000, 4,777,000); the longer side from it is
        // 1,741,000 m, so level 0 is 1,741,000 / 512 = 3,400.390625 m a pixel.
        Assert.Equal(5254, tm30.Srid);
        Assert.Equal(104_000, tm30.OriginX);
        Assert.Equal(4_777_000, tm30.OriginY);
        Assert.Equal(3400.390625, tm30.Resolution(0));
        Assert.Equal(19, tm30.LevelCount);
        Assert.Equal(3400.390625 / 262144, tm30.Resolution(18));
        Assert.True(tm30.Resolution(18) <= VectorTileScheme.WebMercator.Resolution(22));
        Assert.True(tm30.Resolution(17) > VectorTileScheme.WebMercator.Resolution(22));

        // Level zero is one 1,741 km tile, and it holds all of Turkey, Thrace included.
        Assert.Equal(new Envelope(104_000, 3_036_000, 1_845_000, 4_777_000), tm30.Frame);
        Assert.True(Holds(tm30.Frame, VectorTileSchemes.Find("turef-tm30")!.CoversProjected));
    }

    private static bool Holds(Envelope outer, Envelope inner) =>
        outer.MinX <= inner.MinX && outer.MinY <= inner.MinY && outer.MaxX >= inner.MaxX && outer.MaxY >= inner.MaxY;

    [Fact]
    public void A_TM30_tile_is_where_its_origin_and_resolution_put_it()
    {
        // Level 2: a tile is 1,741,000 / 4 = 435,250 m. Column 1, row 2 starts 435,250 m east and 870,500 m south.
        Envelope tile = Tm30.Envelope(new TileAddress(2, 1, 2));

        Assert.Equal(new Envelope(539_250, 3_471_250, 974_500, 3_906_500), tile);
        Assert.Equal(4, Tm30.TilesAcross(2));
        Assert.Equal(262_144, Tm30.TilesAcross(18));
    }

    [Fact]
    public void A_TM30_address_outside_its_grid_is_refused_with_its_own_numbers()
    {
        Assert.Null(Tm30.Rejection(new TileAddress(18, 262_143, 262_143)));
        Assert.Contains("0–18", Tm30.Rejection(new TileAddress(19, 0, 0)), StringComparison.Ordinal);
        Assert.Contains("4×4", Tm30.Rejection(new TileAddress(2, 4, 0)), StringComparison.Ordinal);
        Assert.NotNull(Tm30.Rejection(new TileAddress(2, 0, -1)));
    }

    [Fact]
    public void Every_TUREF_zone_derives_and_Gauss_Kruger_is_TM_with_the_zone_in_front()
    {
        Assert.Equal(14, VectorTileSchemes.BuiltIn.Count);

        foreach (int meridian in new[] { 27, 30, 33, 36, 39, 42, 45 })
        {
            VectorTileScheme tm = VectorTileSchemes.Find($"turef-tm{meridian}")!.Scheme;
            VectorTileScheme gk = VectorTileSchemes.Find($"turef-gk{meridian / 3}")!.Scheme;

            Assert.Equal(tm.Srid + 16, gk.Srid);
            Assert.Equal(tm.OriginX + (meridian / 3 * 1_000_000.0), gk.OriginX);
            Assert.Equal(tm.OriginY, gk.OriginY);
            Assert.Equal(tm.Resolutions, gk.Resolutions);

            // Every zone's frame holds its whole area of use, and the whole country (D-288).
            BuiltInTileScheme built = VectorTileSchemes.Find($"turef-tm{meridian}")!;
            Assert.True(Holds(tm.Frame, built.Projected));
            Assert.True(Holds(tm.Frame, built.CoversProjected));
            Assert.Equal(VectorTileSchemes.Country, built.Covers);
        }

        // TM45, the zone the country reaches furthest from: 1,748 km, so 3,414.0625 m at level 0.
        VectorTileScheme tm45 = VectorTileSchemes.Find("turef-tm45")!.Scheme;
        Assert.Equal(1_748_000 / 512.0, tm45.Resolution(0));
        Assert.Equal(19, tm45.LevelCount);
    }

    [Fact]
    public void A_built_in_is_found_without_case_and_an_unknown_one_is_not()
    {
        Assert.Same(VectorTileSchemes.Find("turef-tm30"), VectorTileSchemes.Find("TUREF-TM30"));
        Assert.Null(VectorTileSchemes.Find("turef-tm31"));
        Assert.Single(VectorTileSchemes.In(5254));
    }

    // ---------- generalisation and visible range, keyed by the pixel ----------

    [Fact]
    public void A_TM30_level_is_simplified_by_the_size_of_its_pixel_not_by_its_number()
    {
        // 3400.390625 / 2^9 = 6.64 m ≥ 4.777 m; / 2^10 = 3.32 m < 4.777 m.
        Assert.True(Tm30.Simplifies(9));
        Assert.False(Tm30.Simplifies(10));

        // Where a level-number rule would have said the opposite: level 14 is simplified in Web Mercator and
        // is a 7 cm pixel here.
        Assert.False(Tm30.Simplifies(14));
    }

    [Fact]
    public void A_TM30_level_draws_a_visible_range_by_its_own_scales()
    {
        // Scale is resolution × 96 × 39.37: level 7 is 100,405, level 8 50,203, level 9 25,101.
        Assert.Equal(3400.390625 / 128 * 96 * 39.37, Tm30.Scale(7), 6);

        VisibleScaleRange range = new(50_000, 0);   // hidden when zoomed out past 1:50,000

        // Level 7 is on screen from 1:100,405 to 1:50,203 — all past the limit.
        Assert.False(Tm30.Draws(range, 7));

        // Level 8 is on screen from 1:50,203 down to 1:25,101, which crosses it.
        Assert.True(Tm30.Draws(range, 8));
        Assert.True(Tm30.Draws(range, 16));
    }

    [Fact]
    public void A_style_zoom_counts_the_scheme_s_own_levels()
    {
        VisibleScaleRange range = new(Tm30.Scale(6), Tm30.Scale(12));

        Assert.Equal(6, range.StyleMinZoomOn(Tm30.Level0Scale)!.Value, 9);
        Assert.Equal(12, range.StyleMaxZoomOn(Tm30.Level0Scale)!.Value, 9);
    }

    // ---------- identity and storage ----------

    [Fact]
    public void A_grid_s_fingerprint_is_its_numbers_and_the_cache_key_carries_it()
    {
        string? tm30 = Tm30.Fingerprint;
        string? gk10 = VectorTileSchemes.Find("turef-gk10")!.Scheme.Fingerprint;

        Assert.NotNull(tm30);
        Assert.NotEqual(tm30, gk10);
        Assert.StartsWith("srid=5254;origin=104000,4777000;size=512;res=3400.390625,", tm30, StringComparison.Ordinal);

        string mercator = TileCacheKey.FingerprintOf(5254, "geom", ["a"], 4096, 64);
        string cut = TileCacheKey.FingerprintOf(5254, "geom", ["a"], 4096, 64, null, tm30);

        Assert.NotEqual(mercator, cut);
        Assert.Equal(12, Tm30.Key.Length);
    }

    [Fact]
    public void A_stored_scheme_reads_back_as_the_same_grid()
    {
        string json = Tm30.ToJson()!;

        Assert.Null(VectorTileScheme.Parse(json, out VectorTileScheme read));
        Assert.Equal(Tm30.Fingerprint, read.Fingerprint);
        Assert.Equal("turef-tm30", read.Id);

        Assert.Null(VectorTileScheme.Parse(null, out VectorTileScheme none));
        Assert.Same(VectorTileScheme.WebMercator, none);
    }

    [Fact]
    public void A_stored_scheme_this_build_cannot_serve_is_refused_not_guessed()
    {
        Assert.NotNull(VectorTileScheme.Parse("""{"wkid":5254}""", out _));
        Assert.NotNull(VectorTileScheme.Parse("""{"wkid":5254,"origin":{"x":0,"y":0},"tileSize":256,"resolutions":[1]}""", out _));
        Assert.NotNull(VectorTileScheme.Parse("""{"wkid":5254,"origin":{"x":0,"y":0},"resolutions":[10,null]}""", out _));
        Assert.NotNull(VectorTileScheme.Parse("""{"wkid":"5254","origin":{"x":0,"y":0},"resolutions":[10]}""", out _));
        Assert.NotNull(VectorTileScheme.Parse("not json", out VectorTileScheme fallback));
        Assert.Same(VectorTileScheme.WebMercator, fallback);
    }

    [Fact]
    public void A_custom_grid_is_refused_when_its_numbers_do_not_make_one()
    {
        Assert.Contains("geographic", VectorTileScheme.Create("custom", 4326, 0, 0, [1.0], out _), StringComparison.Ordinal);
        Assert.Contains("Web Mercator", VectorTileScheme.Create("custom", 3857, 0, 0, [1.0], out _), StringComparison.Ordinal);
        Assert.Contains("not finer", VectorTileScheme.Create("custom", 5254, 0, 0, [10.0, 10.0], out _), StringComparison.Ordinal);
        Assert.NotNull(VectorTileScheme.Create("custom", 5254, 0, 0, [], out _));
        Assert.NotNull(VectorTileScheme.Create("custom", 5254, double.NaN, 0, [1.0], out _));
        Assert.NotNull(VectorTileScheme.Create("custom", 5254, 0, 0, [-1.0], out _));
    }

    [Fact]
    public void A_custom_grid_with_an_explicit_list_counts_its_own_tiles()
    {
        // Level 0 is one 51,200 m tile; level 1 at 30 m a pixel is 15,360 m a tile, so 51,200 / 15,360 = 3.33,
        // four tiles a side — not two, because the list does not halve.
        Assert.Null(VectorTileScheme.Create("custom", 5254, 400_000, 4_600_000, [100.0, 30.0, 10.0], out VectorTileScheme? custom));

        Assert.Equal(1, custom!.TilesAcross(0));
        Assert.Equal(4, custom.TilesAcross(1));
        Assert.Equal(10, custom.TilesAcross(2));
        Assert.Equal(new Envelope(415_360, 4_569_280, 430_720, 4_584_640), custom.Envelope(new TileAddress(1, 1, 1)));
    }

    // ---------- seeding in a non-Mercator grid ----------

    [Fact]
    public void A_seed_of_TM30_counts_its_own_rectangles()
    {
        // Level 3 is 217,625 m a tile, level 4 108,812.5 m. The area is 296-346 km east of the origin and 227-277 km
        // south of it: column 1 and row 1 at level 3, columns 2-3 and row 2 at level 4.
        Envelope area = new(400_000, 4_500_000, 450_000, 4_550_000);

        TileSeedPlan plan = TileSeedPlan.For(Tm30, area, 3, 4);

        Assert.Equal(new TileRange(3, 1, 1, 1, 1), plan.Levels[0]);
        Assert.Equal(new TileRange(4, 2, 2, 3, 2), plan.Levels[1]);
        Assert.Equal(1 + 2, plan.Total);
    }

    [Fact]
    public void A_seed_of_the_whole_TM30_frame_is_four_to_the_level()
    {
        TileSeedPlan plan = TileSeedPlan.For(Tm30, Tm30.Frame, 0, 3);

        Assert.Equal([1L, 4L, 16L, 64L], plan.Levels.Select(level => level.Count));
    }

    [Fact]
    public void A_TM30_seed_refuses_levels_it_does_not_have_and_ground_it_does_not_cover()
    {
        Assert.Throws<ArgumentException>(() => TileSeedPlan.For(Tm30, Tm30.Frame, 0, 19));

        // Web Mercator metres of Istanbul are nowhere near TM30's frame.
        Assert.Throws<ArgumentException>(
            () => TileSeedPlan.For(Tm30, new Envelope(3_200_000, 5_000_000, 3_300_000, 5_100_000), 0, 2));
    }
}
