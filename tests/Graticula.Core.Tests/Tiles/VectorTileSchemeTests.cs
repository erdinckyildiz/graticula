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
    public void TM30_is_derived_from_its_area_of_use_as_the_remarks_work_it_out()
    {
        VectorTileScheme tm30 = Tm30;

        Assert.Equal(5254, tm30.Srid);
        Assert.Equal(364_000, tm30.OriginX);
        Assert.Equal(4_593_000, tm30.OriginY);
        Assert.Equal(1173.828125, tm30.Resolution(0));
        Assert.Equal(17, tm30.LevelCount);
        Assert.Equal(1173.828125 / 65536, tm30.Resolution(16));
        Assert.True(tm30.Resolution(16) <= VectorTileScheme.WebMercator.Resolution(22));
        Assert.True(tm30.Resolution(15) > VectorTileScheme.WebMercator.Resolution(22));

        // Level zero is one 601 km tile.
        Assert.Equal(new Envelope(364_000, 3_992_000, 965_000, 4_593_000), tm30.Frame);
    }

    [Fact]
    public void A_TM30_tile_is_where_its_origin_and_resolution_put_it()
    {
        // Level 2: a tile is 601,000 / 4 = 150,250 m. Column 1, row 2 starts 150,250 m east and 300,500 m south.
        Envelope tile = Tm30.Envelope(new TileAddress(2, 1, 2));

        Assert.Equal(new Envelope(514_250, 4_142_250, 664_500, 4_292_500), tile);
        Assert.Equal(4, Tm30.TilesAcross(2));
        Assert.Equal(65_536, Tm30.TilesAcross(16));
    }

    [Fact]
    public void A_TM30_address_outside_its_grid_is_refused_with_its_own_numbers()
    {
        Assert.Null(Tm30.Rejection(new TileAddress(16, 65_535, 65_535)));
        Assert.Contains("0–16", Tm30.Rejection(new TileAddress(17, 0, 0)), StringComparison.Ordinal);
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

            // Every zone's frame holds its whole area of use.
            BuiltInTileScheme built = VectorTileSchemes.Find($"turef-tm{meridian}")!;
            Assert.True(tm.Frame.MinX <= built.Projected.MinX && tm.Frame.MaxX >= built.Projected.MaxX);
            Assert.True(tm.Frame.MinY <= built.Projected.MinY && tm.Frame.MaxY >= built.Projected.MaxY);
        }

        // TM36, the tallest zone: 706 km, so 1378.90625 m at level 0 and one level more than TM30.
        VectorTileScheme tm36 = VectorTileSchemes.Find("turef-tm36")!.Scheme;
        Assert.Equal(706_000 / 512.0, tm36.Resolution(0));
        Assert.Equal(18, tm36.LevelCount);
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
        // 1173.828125 / 2^7 = 9.17 m ≥ 4.777 m; / 2^8 = 4.585 m < 4.777 m.
        Assert.True(Tm30.Simplifies(7));
        Assert.False(Tm30.Simplifies(8));

        // Where a level-number rule would have said the opposite: level 14 is simplified in Web Mercator and
        // is a 7 cm pixel here.
        Assert.False(Tm30.Simplifies(14));
    }

    [Fact]
    public void A_TM30_level_draws_a_visible_range_by_its_own_scales()
    {
        // Scale is resolution × 96 × 39.37: level 5 is 138,642, level 6 69,321, level 7 34,661.
        Assert.Equal(1173.828125 / 32 * 96 * 39.37, Tm30.Scale(5), 6);

        VisibleScaleRange range = new(50_000, 0);   // hidden when zoomed out past 1:50,000

        // Level 5 is on screen from 1:138,642 to 1:69,321 — all past the limit.
        Assert.False(Tm30.Draws(range, 5));

        // Level 6 is on screen from 1:69,321 down to 1:34,661, which crosses it.
        Assert.True(Tm30.Draws(range, 6));
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
        Assert.StartsWith("srid=5254;origin=364000,4593000;size=512;res=1173.828125,", tm30, StringComparison.Ordinal);

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
        // Level 3 is 75,125 m a tile, level 4 37,562.5 m. The area is 36–86 km east of the origin and 43–93 km
        // south of it: columns 0–1 and rows 0–1 at level 3, columns 0–2 and rows 1–2 at level 4.
        Envelope area = new(400_000, 4_500_000, 450_000, 4_550_000);

        TileSeedPlan plan = TileSeedPlan.For(Tm30, area, 3, 4);

        Assert.Equal(new TileRange(3, 0, 0, 1, 1), plan.Levels[0]);
        Assert.Equal(new TileRange(4, 0, 1, 2, 2), plan.Levels[1]);
        Assert.Equal(4 + 6, plan.Total);
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
        Assert.Throws<ArgumentException>(() => TileSeedPlan.For(Tm30, Tm30.Frame, 0, 17));

        // Web Mercator metres of Istanbul are nowhere near TM30's frame.
        Assert.Throws<ArgumentException>(
            () => TileSeedPlan.For(Tm30, new Envelope(3_200_000, 5_000_000, 3_300_000, 5_100_000), 0, 2));
    }
}
