using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The grid a client names tiles in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The published schemes are checked against the published numbers, and that is not
/// a tautology.</b> A tiling scheme is only useful if it is the same one everybody else
/// is using: a level whose resolution is a millionth off from a basemap's is a level
/// that does not line up with it, and the failure looks like a projection error rather
/// than an arithmetic one. So the Web Mercator and WGS 84 constants are asserted as
/// literals rather than recomputed from the code that produced them.
/// </para>
/// <para>
/// <b>Arithmetic with no file, no canvas and no HTTP</b>, for the same reason
/// <see cref="CoveragePlannerTests"/> is: deciding where a piece of ground is belongs on
/// the Tier 1 side where it can be reasoned about.
/// </para>
/// </remarks>
public sealed class TilingSchemeTests
{
    private static CoverageInfo Info(
        int srid, Envelope extent, int width = 1024, int height = 512)
    {
        List<OverviewInfo> levels = [];

        for (int i = 1; i <= 3; i++)
        {
            levels.Add(new OverviewInfo(i, width >> i, height >> i));
        }

        return new CoverageInfo(
            width,
            height,
            srid,
            extent,
            [new BandInfo(0, SampleKind.Unsigned8, null, null, null)],
            levels,
            256,
            256);
    }

    [Fact]
    public void Web_Mercator_gets_the_scheme_every_client_already_shares()
    {
        TilingScheme scheme = TilingScheme.For(
            Info(3857, new Envelope(3300000, 4974400, 3325600, 5000000)));

        Assert.Equal(3857, scheme.Srid);
        Assert.Equal(256, scheme.TileSize);
        Assert.Equal(-20037508.342787, scheme.OriginX, 6);
        Assert.Equal(20037508.342787, scheme.OriginY, 6);

        // The published table, not a recomputation of it.
        Assert.Equal(156543.03392800014, scheme.Levels[0].Resolution, 8);
        Assert.Equal(591657527.591555, scheme.Levels[0].Scale, 4);
        Assert.Equal(295828763.795777, scheme.Levels[1].Scale, 4);

        // Each level is half the one above it, all the way down.
        for (int i = 1; i < scheme.Levels.Count; i++)
        {
            Assert.Equal(
                scheme.Levels[i - 1].Resolution / 2, scheme.Levels[i].Resolution, 12);
        }
    }

    [Fact]
    public void Geographic_gets_the_scheme_ArcGIS_publishes_for_WGS_84()
    {
        TilingScheme scheme = TilingScheme.For(Info(4326, new Envelope(30, 36, 40, 41)));

        Assert.Equal(4326, scheme.Srid);
        Assert.Equal(-180.0, scheme.OriginX);
        Assert.Equal(90.0, scheme.OriginY);

        // Level 0 is two tiles across the world and one down, so 360 degrees over 512
        // pixels. The scale beside it is the published one.
        Assert.Equal(0.703125, scheme.Levels[0].Resolution, 12);

        // <b>Three decimals, not four, and the missing digit is not slack.</b> A degree
        // enters this figure through a metres-per-degree constant, so the last digits are
        // that constant's rather than the scheme's; asking for four here is asking for
        // fourteen significant figures out of a chain of double multiplications. The
        // resolution above is what places a tile and it is exact. No client resolves a
        // scale to a thousandth of a unit at 1:295,828,763.
        Assert.Equal(295828763.795777, scheme.Levels[0].Scale, 3);
    }

    [Fact]
    public void A_reference_with_no_shared_scheme_gets_one_derived_from_the_coverage()
    {
        // <b>The case that makes this general rather than a Web Mercator special.</b>
        // EPSG:27700 has no ArcGIS scheme this server could copy, so the grid starts at
        // the coverage's own top-left corner and level 0 is one tile across its longer
        // side.
        Envelope extent = new(100000, 200000, 110000, 205000);
        TilingScheme scheme = TilingScheme.For(Info(27700, extent));

        Assert.Equal(27700, scheme.Srid);
        Assert.Equal(100000, scheme.OriginX);
        Assert.Equal(205000, scheme.OriginY);

        // The longer side is 10000 units, over 256 pixels.
        Assert.Equal(10000.0 / 256.0, scheme.Levels[0].Resolution, 9);

        // Level 0's single tile covers the whole coverage, which is what *one tile across
        // the longer side* has to mean if it is to mean anything.
        Envelope first = scheme.Tile(0, 0, 0);

        Assert.True(first.MinX <= extent.MinX && first.MaxX >= extent.MaxX);
        Assert.True(first.MinY <= extent.MinY && first.MaxY >= extent.MaxY);
    }

    [Fact]
    public void A_derived_scheme_reaches_the_coverages_own_pixel_size()
    {
        // Stopping short would offer a client a coarsest-available view of data it has
        // paid to store at full resolution.
        Envelope extent = new(100000, 200000, 110000, 205000);
        CoverageInfo info = Info(27700, extent, 4096, 2048);
        TilingScheme scheme = TilingScheme.For(info);

        double native = Math.Min(info.PixelWidth, info.PixelHeight);

        Assert.True(
            scheme.Levels[^1].Resolution <= native,
            "The finest level is coarser than the coverage itself.");
    }

    [Fact]
    public void Rows_count_downward_and_columns_rightward_from_the_origin()
    {
        // <b>Getting this backwards draws a map that is right tile by tile and mirrored
        // as a whole</b>, which reads as a projection bug and is not one.
        TilingScheme scheme = TilingScheme.For(
            Info(3857, new Envelope(3300000, 4974400, 3325600, 5000000)));

        Envelope origin = scheme.Tile(3, 0, 0);
        Envelope right = scheme.Tile(3, 0, 1);
        Envelope below = scheme.Tile(3, 1, 0);

        Assert.Equal(scheme.OriginX, origin.MinX, 6);
        Assert.Equal(scheme.OriginY, origin.MaxY, 6);

        Assert.True(right.MinX > origin.MinX, "Column 1 is not east of column 0.");
        Assert.Equal(origin.MaxX, right.MinX, 6);

        Assert.True(below.MaxY < origin.MaxY, "Row 1 is not south of row 0.");
        Assert.Equal(origin.MinY, below.MaxY, 6);
    }

    [Fact]
    public void Tiles_at_one_level_meet_without_gap_or_overlap()
    {
        TilingScheme scheme = TilingScheme.For(Info(4326, new Envelope(30, 36, 40, 41)));

        for (int level = 0; level < 6; level++)
        {
            Envelope here = scheme.Tile(level, 4, 7);
            Envelope east = scheme.Tile(level, 4, 8);
            Envelope south = scheme.Tile(level, 5, 7);

            Assert.Equal(here.MaxX, east.MinX, 9);
            Assert.Equal(here.MinY, south.MaxY, 9);
            Assert.Equal(here.MaxX - here.MinX, here.MaxY - here.MinY, 9);
        }
    }

    [Fact]
    public void A_level_this_scheme_does_not_have_is_refused_by_name()
    {
        TilingScheme scheme = TilingScheme.For(Info(4326, new Envelope(30, 36, 40, 41)));

        ArgumentOutOfRangeException thrown =
            Assert.Throws<ArgumentOutOfRangeException>(() => scheme.Tile(scheme.Levels.Count, 0, 0));

        Assert.Contains(
            (scheme.Levels.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            thrown.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Covers_is_true_only_where_the_coverage_actually_is()
    {
        Envelope extent = new(30, 36, 40, 41);
        CoverageInfo info = Info(4326, extent);
        TilingScheme scheme = TilingScheme.For(info);

        // Level 4 is 22.5 degrees a tile, so the coverage sits inside a handful of them.
        const int level = 4;

        List<(int Row, int Column)> covered = [];

        for (int row = 0; row < 16; row++)
        {
            for (int column = 0; column < 32; column++)
            {
                if (scheme.Covers(info, level, row, column))
                {
                    covered.Add((row, column));
                }
            }
        }

        Assert.NotEmpty(covered);

        // Every tile it claims really does meet the coverage, and the check here is
        // independent of the one under test: overlap computed from the tile's own extent.
        foreach ((int row, int column) in covered)
        {
            Envelope tile = scheme.Tile(level, row, column);

            Assert.True(
                tile.MinX < extent.MaxX && tile.MaxX > extent.MinX
                    && tile.MinY < extent.MaxY && tile.MaxY > extent.MinY,
                $"Tile {level}/{row}/{column} was reported covered and does not overlap.");
        }
    }

    [Fact]
    public void A_tile_that_only_touches_the_edge_is_not_covered()
    {
        // <b>Touching is not overlapping</b>, and reporting it as covered would put a
        // blank request on a client's critical path along two sides of every coverage.
        TilingScheme scheme = TilingScheme.For(Info(4326, new Envelope(30, 36, 40, 41)));

        // A coverage whose extent is exactly one tile, so its neighbours only touch.
        Envelope tile = scheme.Tile(4, 3, 9);
        CoverageInfo exact = Info(4326, tile);

        Assert.True(scheme.Covers(exact, 4, 3, 9));
        Assert.False(scheme.Covers(exact, 4, 3, 10));
        Assert.False(scheme.Covers(exact, 4, 4, 9));
    }

    [Fact]
    public void The_finest_useful_level_is_no_finer_than_the_coverage()
    {
        CoverageInfo info = Info(3857, new Envelope(3300000, 4974400, 3325600, 5000000), 256, 256);
        TilingScheme scheme = TilingScheme.For(info);

        int finest = scheme.FinestUsefulLevel(info);
        double native = Math.Min(info.PixelWidth, info.PixelHeight);

        Assert.True(
            scheme.Levels[finest].Resolution >= native,
            "The finest useful level is finer than the data behind it.");

        Assert.True(
            finest + 1 >= scheme.Levels.Count
                || scheme.Levels[finest + 1].Resolution < native,
            "A coarser level than necessary was chosen; the next one down still qualifies.");
    }

    [Fact]
    public void Web_Mercator_level_zero_is_one_tile_and_not_two()
    {
        // <b>This is a floating-point test wearing a geometry test's clothes, and it is the
        // one that matters most in this file.</b> The published level-zero resolution times
        // 256 is 40075016.68556804; the published half-width times two is 40075016.685574.
        // The same number, written down twice by different people, and dividing them gives
        // 1.00000000000015. A plain `Math.Ceiling` turns that into two tiles at level zero,
        // which puts the entire world in the left half of a grid twice its size — every
        // tile then names ground one tile too far west, at every level.
        TilingScheme scheme = TilingScheme.For(
            Info(3857, new Envelope(3300000, 4974400, 3325600, 5000000)));

        Assert.Equal(1, scheme.TilesAcross(0));
        Assert.Equal(1, scheme.TilesDown(0));

        // And it doubles from there, which is what makes it the scheme everyone shares.
        for (int level = 1; level < 12; level++)
        {
            Assert.Equal(1 << level, scheme.TilesAcross(level));
            Assert.Equal(1 << level, scheme.TilesDown(level));
        }
    }

    [Fact]
    public void The_geographic_scheme_is_twice_as_wide_as_it_is_tall()
    {
        // <b>Not square, and treating it as square put its southern half outside
        // itself.</b> The world is 360 degrees across and 180 down, so ArcGIS's WGS 84
        // scheme is two tiles wide and one tall at level zero. A bounds check written
        // against one dimension refuses every tile in the bottom half of the grid, or
        // accepts a row that does not exist, depending which dimension it took.
        TilingScheme scheme = TilingScheme.For(Info(4326, new Envelope(30, 36, 40, 41)));

        Assert.Equal(2, scheme.TilesAcross(0));
        Assert.Equal(1, scheme.TilesDown(0));

        Assert.Equal(4, scheme.TilesAcross(1));
        Assert.Equal(2, scheme.TilesDown(1));

        Assert.Equal(8, scheme.TilesAcross(2));
        Assert.Equal(4, scheme.TilesDown(2));
    }

    [Fact]
    public void A_derived_scheme_is_square_because_level_zero_is_one_tile()
    {
        Envelope extent = new(100000, 200000, 110000, 205000);
        TilingScheme scheme = TilingScheme.For(Info(27700, extent));

        Assert.Equal(1, scheme.TilesAcross(0));
        Assert.Equal(1, scheme.TilesDown(0));

        // Square even though the coverage is not: level zero is one tile across the longer
        // side, so the shorter side has room to spare. A frame cut to the coverage's own
        // aspect would leave a fractional tile on one edge, and a fractional tile has no
        // name in this scheme.
        //
        // <b>Every level this scheme actually has, rather than a level number written
        // here.</b> A derived scheme stops when it reaches the coverage's own pixel size,
        // so how many levels it has depends on the fixture; asserting on level 3 threw
        // rather than failed, which is a test telling you about itself.
        for (int level = 0; level < scheme.Levels.Count; level++)
        {
            Assert.Equal(scheme.TilesAcross(level), scheme.TilesDown(level));
        }
    }

    [Fact]
    public void Every_tile_the_grid_counts_is_inside_the_ground_the_grid_covers()
    {
        // <b>The counts and the extents have to agree, and they are computed by different
        // code.</b> `TilesAcross` divides a frame by a span; `Tile` multiplies a span by an
        // index. If either is off by one the last tile of a level falls outside the grid it
        // was counted into, and a bounds check built on the count then admits a request the
        // geometry cannot answer.
        foreach (CoverageInfo info in new[]
        {
            Info(3857, new Envelope(3300000, 4974400, 3325600, 5000000)),
            Info(4326, new Envelope(30, 36, 40, 41)),
            Info(27700, new Envelope(100000, 200000, 110000, 205000)),
        })
        {
            TilingScheme scheme = TilingScheme.For(info);

            for (int level = 0; level < Math.Min(6, scheme.Levels.Count - 1); level++)
            {
                int wide = scheme.TilesAcross(level);
                int tall = scheme.TilesDown(level);

                Envelope last = scheme.Tile(level, tall - 1, wide - 1);

                double eastEdge = scheme.OriginX + scheme.FrameWidth;
                double southEdge = scheme.OriginY - scheme.FrameHeight;

                double slack = scheme.Levels[level].Resolution * scheme.TileSize * 1e-6;

                Assert.True(
                    last.MaxX <= eastEdge + slack,
                    $"EPSG:{info.Srid} level {level}: the last column ends past the frame.");

                Assert.True(
                    last.MinY >= southEdge - slack,
                    $"EPSG:{info.Srid} level {level}: the last row ends past the frame.");

                // And one more would not fit, which is what makes the count the count
                // rather than an underestimate.
                Envelope beyond = scheme.Tile(level, tall, wide);

                Assert.True(
                    beyond.MinX >= eastEdge - slack,
                    $"EPSG:{info.Srid} level {level}: column {wide} is still inside the frame.");
            }
        }
    }

    [Fact]
    public void A_level_the_scheme_does_not_have_is_refused_by_the_counts_too()
    {
        TilingScheme scheme = TilingScheme.For(Info(4326, new Envelope(30, 36, 40, 41)));

        Assert.Throws<ArgumentOutOfRangeException>(() => scheme.TilesAcross(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheme.TilesDown(scheme.Levels.Count));
    }

    [Fact]
    public void Every_scheme_has_levels_and_they_are_numbered_from_zero()
    {
        foreach (CoverageInfo info in new[]
        {
            Info(3857, new Envelope(3300000, 4974400, 3325600, 5000000)),
            Info(4326, new Envelope(30, 36, 40, 41)),
            Info(27700, new Envelope(100000, 200000, 110000, 205000)),
        })
        {
            TilingScheme scheme = TilingScheme.For(info);

            Assert.NotEmpty(scheme.Levels);
            Assert.Equal(
                Enumerable.Range(0, scheme.Levels.Count),
                scheme.Levels.Select(l => l.Level));
        }
    }
}
