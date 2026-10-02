using System;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ADR-136: Horn's slope, aspect and hillshade, checked on planes whose answers are known in closed form.
/// </summary>
public sealed class RasterFunctionTests
{
    /// <summary>A 5×5 plane rising by <paramref name="east"/> a cell eastward and <paramref name="north"/> a cell northward.</summary>
    private static CoverageWindow Plane(double east, double north)
    {
        double[] z = new double[25];
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                // Row 0 is the top, the north edge.
                z[(y * 5) + x] = 1000 + (x * east) + ((4 - y) * north);
            }
        }

        return new CoverageWindow(5, 5, 1, z);
    }

    private static double Centre(RasterFunction function, CoverageWindow window, double cell = 10) =>
        function.Apply(window, cell, cell, null).Samples[12];

    [Fact]
    public void A_plane_rising_one_cell_size_a_cell_is_forty_five_degrees_steep()
    {
        Assert.Equal(45, Centre(new RasterFunction(RasterFunctionKind.Slope), Plane(10, 0)), 6);
        Assert.Equal(0, Centre(new RasterFunction(RasterFunctionKind.Slope), Plane(0, 0)), 6);
    }

    [Theory]
    [InlineData(10, 0, 270)]   // rising to the east faces west
    [InlineData(-10, 0, 90)]   // rising to the west faces east
    [InlineData(0, 10, 180)]   // rising to the north faces south
    [InlineData(0, -10, 0)]    // rising to the south faces north
    public void Aspect_is_the_compass_direction_the_slope_faces(double east, double north, double expected)
    {
        double aspect = Centre(new RasterFunction(RasterFunctionKind.Aspect), Plane(east, north));
        Assert.Equal(expected, aspect % 360, 6);
    }

    [Fact]
    public void A_flat_cell_faces_nowhere_and_is_lit_by_the_suns_altitude_alone()
    {
        Assert.True(double.IsNaN(Centre(new RasterFunction(RasterFunctionKind.Aspect), Plane(0, 0))));
        // 255 × cos(zenith), zenith 45° for the default altitude.
        Assert.Equal(255 * Math.Cos(Math.PI / 4), Centre(new RasterFunction(RasterFunctionKind.Hillshade), Plane(0, 0)), 6);
    }

    [Fact]
    public void A_slope_facing_the_sun_is_lit_and_one_facing_away_is_in_shade()
    {
        RasterFunction sun = new(RasterFunctionKind.Hillshade);   // from the north-west
        double facingSun = Centre(sun, Plane(5, -5));               // rising to the south-east, so facing north-west
        double facingAway = Centre(sun, Plane(-5, 5));              // rising to the north-west, so facing south-east
        Assert.True(facingSun > 200 && facingAway < 120, $"lit {facingSun}, shaded {facingAway}");
    }

    [Fact]
    public void A_cell_with_an_absent_neighbour_has_no_answer()
    {
        CoverageWindow window = Plane(10, 0);
        window.Samples[6] = -9999;
        CoverageWindow slope = new RasterFunction(RasterFunctionKind.Slope).Apply(window, 10, 10, -9999);
        Assert.True(double.IsNaN(slope.Samples[12]));
        Assert.False(double.IsNaN(slope.Samples[18]));
    }

    [Fact]
    public void Degrees_are_turned_into_metres_at_the_latitude()
    {
        (double w, double h) = RasterFunction.Metres(0.001, 0.001, true, 60);
        Assert.Equal(55.66, w, 1);
        Assert.Equal(110.574, h, 2);
    }

    [Theory]
    [InlineData(null, RasterFunctionKind.None)]
    [InlineData("{}", RasterFunctionKind.None)]
    [InlineData("{\"rasterFunction\":\"None\"}", RasterFunctionKind.None)]
    [InlineData("{\"rasterFunction\":\"Hillshade\",\"rasterFunctionArguments\":{\"Azimuth\":200,\"Altitude\":30}}", RasterFunctionKind.Hillshade)]
    [InlineData("{\"rasterFunction\":\"slope\"}", RasterFunctionKind.Slope)]
    public void A_rendering_rule_is_read_as_ArcGIS_writes_it(string? rule, RasterFunctionKind kind)
    {
        Assert.True(RasterFunction.TryParseRule(rule, out RasterFunction? function, out string? error), error);
        // Empty and {} ask for nothing (null: the service's own drawing); "None" asks for the values.
        Assert.Equal(kind, (function ?? RasterFunction.None).Kind);
        Assert.Equal(rule is null or "{}", function is null);
    }

    [Fact]
    public void A_function_this_server_does_not_apply_is_refused_by_name()
    {
        Assert.False(RasterFunction.TryParseRule("{\"rasterFunction\":\"NDVI\"}", out _, out string? error));
        Assert.Contains("NDVI", error, StringComparison.Ordinal);
        Assert.Contains("Hillshade", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stored_style_names_its_function_beside_its_ramp()
    {
        Assert.Equal(RasterFunctionKind.Slope, RasterFunction.FromStyleText("stretch:auto;ramp:terrain;function:slope").Kind);
        Assert.Equal("terrain", Graticula.Cartography.CoverageStyle.Parse("stretch:auto;function:slope;ramp:terrain").RampName);
        Assert.Equal(RasterFunctionKind.None, RasterFunction.FromStyleText("stretch:full").Kind);
        Assert.Equal("Hillshade,Slope,Aspect", string.Join(",", RasterFunction.Names));
    }
}
