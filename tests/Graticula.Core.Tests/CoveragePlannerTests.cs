using System;
using System.Collections.Generic;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// Which pixels answer a request, and where they land on the canvas.
/// </summary>
/// <remarks>
/// <b>Arithmetic with no file and no canvas, which is why the line is where it is.</b>
/// [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) §3.4 puts
/// choosing an overview on the Tier 1 side precisely so that it can be reasoned about
/// here rather than inside a library.
/// </remarks>
public sealed class CoveragePlannerTests
{
    /// <summary>A 1024×512 coverage over ten degrees by five, with three overviews.</summary>
    private static CoverageInfo Info(int overviews = 3, int width = 1024, int height = 512)
    {
        List<OverviewInfo> levels = [];

        for (int i = 1; i <= overviews; i++)
        {
            levels.Add(new OverviewInfo(i, width >> i, height >> i));
        }

        return new CoverageInfo(
            width,
            height,
            4326,
            new Envelope(30, 36, 40, 41),
            [new BandInfo(0, SampleKind.Unsigned8, null, null, null)],
            levels,
            256,
            256);
    }

    [Fact]
    public void A_request_for_the_whole_thing_at_full_size_reads_full_resolution()
    {
        CoveragePlan? plan = CoveragePlanner.Plan(Info(), new Envelope(30, 36, 40, 41), 1024, 512);

        Assert.NotNull(plan);
        Assert.Equal(0, plan!.Value.Overview);
        Assert.Equal(0, plan.Value.X);
        Assert.Equal(0, plan.Value.Y);
        Assert.Equal(1024, plan.Value.Width);
        Assert.Equal(512, plan.Value.Height);
    }

    [Fact]
    public void A_request_drawn_small_reads_a_coarse_level_instead()
    {
        // <b>The whole reason a pyramid exists.</b> Reading 1024×512 to draw 128×64 is
        // eight thousand times the pixels for the same picture, and the picture is not
        // better for it because the extra detail is averaged away on the way down.
        CoveragePlan? plan = CoveragePlanner.Plan(Info(), new Envelope(30, 36, 40, 41), 128, 64);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.Value.Overview);
        Assert.Equal(128, plan.Value.Width);
    }

    [Fact]
    public void A_level_coarser_than_the_request_is_never_chosen()
    {
        // Magnified imagery reads as blur the viewer cannot tell from the data being
        // low-resolution, which is worse than costing more to draw.
        foreach (int size in new[] { 1024, 700, 512, 300, 256, 200, 128, 64, 33 })
        {
            CoverageInfo info = Info();

            CoveragePlan? plan = CoveragePlanner.Plan(
                info, new Envelope(30, 36, 40, 41), size, size / 2);

            Assert.NotNull(plan);

            int level = plan!.Value.Overview;
            int levelWidth = level == 0 ? info.Width : info.Overviews[level - 1].Width;

            double perPixel = (info.Extent.MaxX - info.Extent.MinX) / levelWidth;
            double wanted = (info.Extent.MaxX - info.Extent.MinX) / size;

            Assert.True(
                perPixel <= wanted + 1e-9,
                $"For a {size}px request the planner chose level {level}, whose pixels span "
                + $"{perPixel} degrees against the {wanted} the request wants. That is a "
                + "magnification.");
        }
    }

    [Fact]
    public void A_coverage_with_no_pyramid_always_reads_level_zero()
    {
        CoveragePlan? plan =
            CoveragePlanner.Plan(Info(overviews: 0), new Envelope(30, 36, 40, 41), 16, 8);

        Assert.NotNull(plan);
        Assert.Equal(0, plan!.Value.Overview);
    }

    [Fact]
    public void A_request_that_misses_the_coverage_entirely_plans_nothing()
    {
        // Null rather than an error: a client panning off the edge of its own data gets
        // a valid empty image, which is ADR-041 condition 5's rule for the vector faces.
        Assert.Null(CoveragePlanner.Plan(Info(), new Envelope(0, 0, 5, 5), 256, 256));
        Assert.Null(CoveragePlanner.Plan(Info(), new Envelope(41, 36, 50, 41), 256, 256));
    }

    [Fact]
    public void A_request_touching_only_an_edge_plans_nothing()
    {
        // Sharing an edge is not an overlap: the intersection has no area, so there is
        // no pixel to read and no box to draw it in.
        Assert.Null(CoveragePlanner.Plan(Info(), new Envelope(40, 36, 50, 41), 256, 256));
    }

    [Fact]
    public void A_request_for_the_left_half_reads_the_left_half()
    {
        CoveragePlan? plan = CoveragePlanner.Plan(Info(), new Envelope(30, 36, 35, 41), 512, 512);

        Assert.NotNull(plan);
        Assert.Equal(0, plan!.Value.X);
        Assert.Equal(0, plan.Value.Y);
        Assert.Equal(512, plan.Value.Width);
        Assert.Equal(512, plan.Value.Height);
    }

    [Fact]
    public void A_window_lands_where_its_ground_says_it_should()
    {
        // <b>The registration test, and the one that matters most.</b> A coverage drawn
        // a pixel out of place is a coverage that does not line up with the vector
        // layers over it, which is the failure a viewer notices and cannot diagnose.
        // Requesting twice the coverage's own extent puts it in the middle quarter.
        CoveragePlan? plan = CoveragePlanner.Plan(
            Info(), new Envelope(25, 33.5, 45, 43.5), 400, 200);

        Assert.NotNull(plan);

        PixelBox box = plan!.Value.Destination;

        // Ten degrees of twenty across 400 pixels: the coverage occupies 100..300.
        Assert.Equal(100, box.MinX, 6);
        Assert.Equal(300, box.MaxX, 6);

        // Five degrees of ten down 200 pixels: 50..150.
        Assert.Equal(50, box.MinY, 6);
        Assert.Equal(150, box.MaxY, 6);
    }

    [Fact]
    public void A_partly_overlapping_request_draws_only_the_part_that_overlaps()
    {
        // The left half of the request is off the west edge of the coverage, so the
        // destination starts halfway across the canvas rather than at zero.
        CoveragePlan? plan = CoveragePlanner.Plan(
            Info(), new Envelope(20, 36, 40, 41), 400, 200);

        Assert.NotNull(plan);

        PixelBox box = plan!.Value.Destination;

        Assert.Equal(200, box.MinX, 6);
        Assert.Equal(400, box.MaxX, 6);
    }

    [Fact]
    public void The_window_read_is_never_empty_and_never_past_the_edge()
    {
        // A degenerate window would ask the reader for zero pixels, and a window past
        // the edge would ask for pixels that are not there. Both are arithmetic that
        // has to be right here so that no caller repeats it.
        foreach (Envelope extent in new[]
        {
            new Envelope(29.999, 35.999, 30.001, 36.001),
            new Envelope(39.999, 40.999, 40.001, 41.001),
            new Envelope(30, 36, 30.0001, 36.0001),
        })
        {
            CoverageInfo info = Info();
            CoveragePlan? plan = CoveragePlanner.Plan(info, extent, 256, 256);

            if (plan is null)
            {
                continue;
            }

            (int levelWidth, int levelHeight) = plan.Value.Overview == 0
                ? (info.Width, info.Height)
                : (info.Overviews[plan.Value.Overview - 1].Width,
                   info.Overviews[plan.Value.Overview - 1].Height);

            Assert.True(plan.Value.Width > 0 && plan.Value.Height > 0);
            Assert.True(plan.Value.X >= 0 && plan.Value.Y >= 0);
            Assert.True(plan.Value.X + plan.Value.Width <= levelWidth);
            Assert.True(plan.Value.Y + plan.Value.Height <= levelHeight);
        }
    }

    [Fact]
    public void A_zero_sized_request_plans_nothing()
    {
        Assert.Null(CoveragePlanner.Plan(Info(), new Envelope(30, 36, 40, 41), 0, 100));
        Assert.Null(CoveragePlanner.Plan(Info(), new Envelope(30, 36, 40, 41), 100, 0));
    }
}
