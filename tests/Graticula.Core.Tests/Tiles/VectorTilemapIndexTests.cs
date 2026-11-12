using System;
using System.Text.Json;
using Graticula.Geometries;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Core.Tests.Tiles;

/// <summary>
/// The quadtree a vector tile service's <c>tilemap</c> answers with no block — Esri's public REST API page
/// <i>Vector Tilemap</i>. Measured 2026-11-12: ArcGIS Pro and Field Maps ask for it and nothing else.
/// </summary>
public sealed class VectorTilemapIndexTests
{
    private static string Json(object[] index) => JsonSerializer.Serialize(new { index });

    private static int Entries(object[] four)
    {
        int count = 0;

        foreach (object each in four)
        {
            count += 1 + (each is object[] below ? Entries(below) : 0);
        }

        return count;
    }

    private static int Depth(object[] four)
    {
        int deepest = 0;

        foreach (object each in four)
        {
            deepest = Math.Max(deepest, each is object[] below ? Depth(below) : 0);
        }

        return deepest + 1;
    }

    [Fact]
    public void The_documented_example_comes_out_as_documented()
    {
        // The page's example, {"index":[[1,0,1,1],0,0,0]}: the north-west tile of level 1 exists and has three of its
        // four children, and nothing else does.
        object[] index = VectorTilemapIndex.Build(2, (z, column, row) => (z, column, row) switch
        {
            (1, 0, 0) => true,
            (2, 0, 0) => true,
            (2, 0, 1) => true,
            (2, 1, 1) => true,
            _ => false,
        });

        Assert.Equal("{\"index\":[[1,0,1,1],0,0,0]}", Json(index));
    }

    [Fact]
    public void The_quadrants_are_north_west_north_east_south_west_south_east_on_a_known_extent()
    {
        // A box in the north-east of the world, x 1,000 km to 2,000 km and y the same. Level 2 tiles are 10,018.754 km
        // across: columns 0 to 3 from the west, rows 0 to 3 from the north, so the box is in column 2, row 1 — the
        // north-east quadrant of level 1, and inside it the south-west child.
        VectorTileScheme scheme = VectorTileScheme.WebMercator;
        Envelope box = new(1_000_000, 1_000_000, 2_000_000, 2_000_000);

        object[] index = VectorTilemapIndex.Build(2, (z, column, row) =>
        {
            Envelope tile = scheme.Envelope(new TileAddress(z, column, row));
            return box.MinX <= tile.MaxX && box.MaxX >= tile.MinX && box.MinY <= tile.MaxY && box.MaxY >= tile.MinY;
        });

        Assert.Equal("{\"index\":[0,[0,0,1,0],0,0]}", Json(index));
    }

    [Fact]
    public void A_quadrant_without_data_is_a_zero_and_not_an_array()
    {
        object[] index = VectorTilemapIndex.Build(3, (z, column, row) => column < (1 << z) / 2 && row < (1 << z) / 2);

        Assert.IsType<object[]>(index[0]);
        Assert.Equal(0, index[1]);
        Assert.Equal(0, index[2]);
        Assert.Equal(0, index[3]);
    }

    [Fact]
    public void A_tile_at_the_finest_level_that_has_data_is_a_leaf()
    {
        object[] index = VectorTilemapIndex.Build(1, (z, column, row) => true);

        Assert.Equal("{\"index\":[1,1,1,1]}", Json(index));
    }

    [Fact]
    public void A_pyramid_too_big_to_describe_stops_at_the_deepest_level_that_fits_and_says_one_there()
    {
        // Everything holds data to level 10. Describing down to level L writes 4 × (1 + 4 + … + 4^(L-1)) entries: 4, 20,
        // 84, 340 — so with 100 the third level is the last that fits, and its tiles are leaves.
        object[] index = VectorTilemapIndex.Build(10, (z, column, row) => true, nodeBudget: 100);

        Assert.Equal(84, Entries(index));
        Assert.Equal(3, Depth(index));
        Assert.True(Entries(index) <= 100);
    }

    [Fact]
    public void The_default_budget_bounds_a_whole_world_at_twenty_two_levels()
    {
        object[] index = VectorTilemapIndex.Build(TileAddress.MaxZoom, (z, column, row) => true);

        // Down to level 6 is 4 × (1 + 4 + … + 4^5) = 5,460 entries and fits; down to level 7 is 21,844 and does not.
        Assert.Equal(5460, Entries(index));
        Assert.True(Entries(index) <= VectorTilemapIndex.NodeBudget);
    }

    [Fact]
    public void A_pyramid_with_no_data_is_four_zeros()
    {
        Assert.Equal("{\"index\":[0,0,0,0]}", Json(VectorTilemapIndex.Build(5, (z, column, row) => false)));
    }

    [Fact]
    public void Only_a_grid_whose_levels_double_is_a_quadtree()
    {
        Assert.True(VectorTilemapIndex.IsQuadtree(VectorTileScheme.WebMercator));

        Assert.Null(VectorTileScheme.Create("custom", 5254, 364000, 4593000, VectorTileScheme.Halving(1173.828125, 5), out VectorTileScheme? halving));
        Assert.True(VectorTilemapIndex.IsQuadtree(halving!));

        Assert.Null(VectorTileScheme.Create("custom", 5254, 364000, 4593000, [1000.0, 300.0, 100.0], out VectorTileScheme? thirds));
        Assert.False(VectorTilemapIndex.IsQuadtree(thirds!));
    }
}
