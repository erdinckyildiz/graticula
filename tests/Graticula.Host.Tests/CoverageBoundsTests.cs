using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The largest image a file may declare — owner decision 2026-10-13, ADR-009 condition 1.
/// </summary>
public sealed class CoverageBoundsTests
{
    private const int Side = CoverageBounds.DefaultMaximumSide;
    private const long Pixels = CoverageBounds.DefaultMaximumPixels;

    [Fact]
    public void The_largest_real_orthophotos_fit()
    {
        // 100,000 a side, ten billion pixels — the case the bound was chosen to leave room for.
        Assert.Null(CoverageBounds.Refusal("ortho.tif", 100_000, 100_000, Side, Pixels));
        Assert.Null(CoverageBounds.Refusal("edge.tif", Side, 65_536, Side, Pixels));
    }

    [Fact]
    public void A_side_or_a_total_past_the_bound_is_refused_with_the_settings_to_raise()
    {
        string? wide = CoverageBounds.Refusal("wide.tif", Side + 1, 10, Side, Pixels);
        Assert.NotNull(wide);
        Assert.Contains("wide.tif", wide, System.StringComparison.Ordinal);
        Assert.Contains("Graticula:RasterMaximumSide", wide, System.StringComparison.Ordinal);

        // Each side inside the bound, the two together past it.
        Assert.NotNull(CoverageBounds.Refusal("square.tif", Side, Side, Side, Pixels));
    }
}
