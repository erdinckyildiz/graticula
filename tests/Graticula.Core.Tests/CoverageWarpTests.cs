using System;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Cartography;
using Graticula.Geometries;
using Xunit;
using Xunit.Abstractions;

namespace Graticula.Core.Tests;

/// <summary>
/// The warp's interpolation error, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is
/// [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)'s condition
/// 2.</b> It asks for a number in <c>benchmarks/</c> saying how far a pixel can land
/// from where it belongs, at the grid density shipped — not for a claim that the
/// approximation is small.
/// </para>
/// <para>
/// <b>Web Mercator against WGS 84, because that pair has a closed form.</b> The
/// running server projects through PostGIS, and measuring against PostGIS would mix
/// the interpolation error this condition is about with whatever the projection
/// library itself does. The formula below is the definition of EPSG:3857 — a sphere,
/// no datum shift, exact to the last bit — so the difference it reveals is the
/// interpolation and nothing else.
/// </para>
/// <para>
/// <b>And it is the pair that matters.</b> Every web client asks for Web Mercator, and
/// imagery is stored in whatever it was captured in; this is the reprojection an
/// ImageServer actually performs.
/// </para>
/// </remarks>
public sealed class CoverageWarpTests
{
    private readonly ITestOutputHelper _output;

    public CoverageWarpTests(ITestOutputHelper output) => _output = output;

    /// <summary>Exactly EPSG:3857 to EPSG:4326, which is a sphere and a logarithm.</summary>
    private static (double Lon, double Lat) ToGeographic(double x, double y)
    {
        const double R = 20037508.342789244;

        double lon = x / R * 180;
        double lat = (Math.Atan(Math.Exp(y / R * Math.PI)) * 360 / Math.PI) - 90;

        return (lon, lat);
    }

    private static CoverageWarp Build(Envelope extent, int width, int height, int steps)
    {
        Point[] grid = CoverageWarp.ControlPoints(extent, width, height, steps);

        double[] x = new double[grid.Length];
        double[] y = new double[grid.Length];

        for (int i = 0; i < grid.Length; i++)
        {
            (x[i], y[i]) = ToGeographic(grid[i].X, grid[i].Y);
        }

        return new CoverageWarp(width, height, steps, x, y);
    }

    /// <summary>
    /// The measurement the condition asks for, printed as a table.
    /// </summary>
    /// <remarks>
    /// <b>It asserts as well as reports.</b> A benchmark nobody runs is a number that
    /// goes stale, and this repository has a debt row about exactly that. The assertion
    /// is the claim the shipped density makes: under a tenth of a pixel.
    /// </remarks>
    [Fact]
    public void The_shipped_grid_leaves_less_than_a_tenth_of_a_pixel()
    {
        // A 1024x768 request over most of Turkey, which is the shape a web client sends
        // and wide enough that Mercator's curvature is doing real work across it.
        Envelope request = new(2_800_000, 4_300_000, 5_000_000, 5_100_000);

        const int Width = 1024;
        const int Height = 768;

        // The coverage this lands on: degrees, at roughly a 1024-pixel resolution over
        // the same ground, so one coverage pixel is about what one canvas pixel wants.
        double perPixelDegrees = (45.0 - 25.0) / 2048;

        _output.WriteLine("steps | points | worst error (deg) | worst error (coverage px)");

        double shipped = double.NaN;

        foreach (int steps in new[] { 2, 4, 8, 16, 32, 64 })
        {
            CoverageWarp warp = Build(request, Width, Height, steps);

            double worst = 0;

            for (int y = 0; y < Height; y += 7)
            {
                for (int x = 0; x < Width; x += 7)
                {
                    (double gotX, double gotY) = warp.Ground(x + 0.5, y + 0.5);

                    double groundX = request.MinX
                        + ((x + 0.5) / Width * (request.MaxX - request.MinX));

                    double groundY = request.MaxY
                        - ((y + 0.5) / Height * (request.MaxY - request.MinY));

                    (double wantX, double wantY) = ToGeographic(groundX, groundY);

                    worst = Math.Max(
                        worst,
                        Math.Max(Math.Abs(gotX - wantX), Math.Abs(gotY - wantY)));
                }
            }

            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{steps,5} | {(steps + 1) * (steps + 1),6} | {worst,17:0.00000000} | "
                + $"{worst / perPixelDegrees,25:0.0000}"));

            if (steps == CoverageWarp.StepsFor(Width, Height))
            {
                shipped = worst / perPixelDegrees;
            }
        }

        Assert.False(
            double.IsNaN(shipped),
            "The shipped density was not among those measured, so this table says nothing "
            + "about what actually runs.");

        Assert.True(
            shipped < 0.1,
            $"At the shipped grid density the worst pixel lands {shipped:0.0000} coverage "
            + "pixels from where it belongs. ADR-043 condition 2 asks for this number and "
            + "the claim attached to it is a tenth of a pixel; a warp visibly out of "
            + "register with the vector layers over it is the failure that number exists "
            + "to prevent.");
    }

    [Fact]
    public void A_denser_grid_is_never_worse()
    {
        // <b>The property, not a number.</b> If refining the grid ever increased the
        // error, the interpolation would be wrong rather than approximate — and a
        // benchmark table alone cannot tell those apart.
        Envelope request = new(2_800_000, 4_300_000, 5_000_000, 5_100_000);

        double previous = double.MaxValue;

        foreach (int steps in new[] { 2, 4, 8, 16, 32 })
        {
            CoverageWarp warp = Build(request, 512, 512, steps);

            double worst = 0;

            for (int y = 0; y < 512; y += 11)
            {
                for (int x = 0; x < 512; x += 11)
                {
                    (double gotX, double gotY) = warp.Ground(x + 0.5, y + 0.5);

                    (double wantX, double wantY) = ToGeographic(
                        request.MinX + ((x + 0.5) / 512 * (request.MaxX - request.MinX)),
                        request.MaxY - ((y + 0.5) / 512 * (request.MaxY - request.MinY)));

                    worst = Math.Max(
                        worst, Math.Max(Math.Abs(gotX - wantX), Math.Abs(gotY - wantY)));
                }
            }

            Assert.True(
                worst <= previous,
                $"A {steps}-step grid was worse than the one before it. Refining an "
                + "interpolation must not make it less accurate; if it does, the "
                + "arithmetic is wrong rather than approximate.");

            previous = worst;
        }
    }

    [Fact]
    public void The_corners_are_exact_because_they_are_control_points()
    {
        // Interpolation is exact where it has a sample, so the corners are the one place
        // the warp cannot be approximate — and a warp whose corners are wrong has its
        // grid indexed wrongly, which no error table would show as anything but noise.
        Envelope request = new(2_800_000, 4_300_000, 5_000_000, 5_100_000);

        CoverageWarp warp = Build(request, 400, 300, 4);

        (double x, double y) = warp.Ground(0, 0);
        (double wantX, double wantY) = ToGeographic(request.MinX, request.MaxY);

        Assert.Equal(wantX, x, 9);
        Assert.Equal(wantY, y, 9);
    }

    [Fact]
    public void A_grid_that_does_not_match_its_step_count_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => new CoverageWarp(100, 100, 4, new double[9], new double[9]));
    }

    [Fact]
    public void The_shipped_density_stays_within_its_stated_bounds()
    {
        // The clamp is what stops a 4096-pixel request asking for a grid nobody wants to
        // project. Both ends are asserted because both are load-bearing: two is the
        // smallest grid that can interpolate at all.
        Assert.Equal(2, CoverageWarp.StepsFor(1, 1));
        Assert.Equal(2, CoverageWarp.StepsFor(128, 128));
        Assert.Equal(16, CoverageWarp.StepsFor(1024, 768));
        Assert.Equal(64, CoverageWarp.StepsFor(4096, 4096));
        Assert.Equal(64, CoverageWarp.StepsFor(100_000, 100_000));
    }
}
