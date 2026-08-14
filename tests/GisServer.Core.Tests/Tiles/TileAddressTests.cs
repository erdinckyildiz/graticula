using GisServer.Tiles;
using Xunit;

namespace GisServer.Core.Tests.Tiles;

/// <summary>
/// Which tile addresses exist, and what a refusal says.
/// </summary>
/// <remarks>
/// The arithmetic is trivial and the consequence of getting it wrong is not: an
/// unchecked address reaches <c>ST_TileEnvelope</c>, which raises a database
/// error, which surfaces as a 500 — the server reporting a fault of its own for
/// a caller's typo.
/// </remarks>
public sealed class TileAddressTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(16, 38031, 24571)]
    [InlineData(22, 0, 0)]
    [InlineData(22, 4194303, 4194303)]
    public void An_address_inside_the_pyramid_is_valid(int z, int x, int y) =>
        Assert.True(new TileAddress(z, x, y).IsValid);

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(23, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 0, -1)]
    [InlineData(22, 4194304, 0)]
    public void An_address_outside_the_pyramid_is_not(int z, int x, int y) =>
        Assert.False(new TileAddress(z, x, y).IsValid);

    [Fact]
    public void The_grid_is_square_and_grows_by_four_per_level()
    {
        // z2 is 4x4, so 3 is the last valid index and 4 is the first invalid
        // one. Off by one here means the east and south edges of every map are
        // missing, which looks like missing data rather than a bug.
        Assert.True(new TileAddress(2, 3, 3).IsValid);
        Assert.False(new TileAddress(2, 4, 3).IsValid);
        Assert.False(new TileAddress(2, 3, 4).IsValid);
    }

    [Fact]
    public void A_zoom_refusal_names_the_range()
    {
        string reason = new TileAddress(99, 0, 0).Rejection()!;

        Assert.Contains("99", reason, System.StringComparison.Ordinal);
        Assert.Contains("22", reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_grid_refusal_names_the_grid_so_the_caller_can_see_the_mistake()
    {
        // "Tile 99,1 is outside the 4x4 grid at zoom 2" tells a caller they used
        // a z16 index at z2. "Bad request" tells them nothing they can act on.
        string reason = new TileAddress(2, 99, 1).Rejection()!;

        Assert.Contains("4×4", reason, System.StringComparison.Ordinal);
        Assert.Contains("zoom 2", reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_address_has_nothing_to_say()
    {
        Assert.Null(new TileAddress(16, 38031, 24571).Rejection());
    }

    [Fact]
    public void An_address_prints_as_z_slash_x_slash_y()
    {
        // The conventional order, which is deliberately NOT the ArcGIS URL order
        // — that one is z/y/x and the endpoint does the swap. A log line in the
        // wrong order sends somebody looking at a tile on the other side of the
        // world.
        Assert.Equal("16/38031/24571", new TileAddress(16, 38031, 24571).ToString());
    }
}
