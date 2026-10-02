using System;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>ADR-146: GARS and GEOREF, the two cell notations ArcGIS's geometry service converts besides the six.</summary>
public sealed class GeoCoordinateCellTests
{
    private static string Write(double lon, double lat, GeoCoordinateNotation notation, int digits = 4)
    {
        Assert.True(GeoCoordinateString.TryWrite(lon, lat, notation, digits, false, out string text, out string? error), error);
        return text;
    }

    [Fact]
    public void Gars_names_the_30_minute_cell_its_quadrant_and_its_keypad()
    {
        // 177.4° W, 86.9° S: the sixth column from the antimeridian, row AG, south-west quadrant, keypad 5.
        Assert.Equal("006AG35", Write(-177.4, -86.9, GeoCoordinateNotation.Gars));
        // 29.01° E, 41.01° N: column 419; row 262, which is L (10) and Y (22) of the 24 letters; south-west quadrant and keypad.
        Assert.Equal("419LY37", Write(29.01, 41.01, GeoCoordinateNotation.Gars));
    }

    [Fact]
    public void Georef_names_the_quadrangle_the_degree_and_the_minutes()
    {
        Assert.Equal("PJQM0000", Write(29, 41, GeoCoordinateNotation.Georef, 2));
        Assert.Equal("PJQM30001500", Write(29.5, 41.25, GeoCoordinateNotation.Georef, 4));
    }

    [Theory]
    [InlineData(GeoCoordinateNotation.Gars, 2.5 / 60)]
    [InlineData(GeoCoordinateNotation.Georef, 0.5 / 60 / 100)]
    public void A_string_reads_back_as_its_cells_centre_near_the_point_it_came_from(GeoCoordinateNotation notation, double half)
    {
        foreach ((double lon, double lat) in new[] { (29.0123, 41.0456), (-73.98, 40.75), (151.2, -33.87), (179.99, 89.99) })
        {
            string text = Write(lon, lat, notation);
            Assert.True(GeoCoordinateString.TryRead(text, notation, out double x, out double y, out string? error), error);
            Assert.InRange(Math.Abs(x - lon), 0, half + 1e-9);
            Assert.InRange(Math.Abs(y - lat), 0, half + 1e-9);
        }
    }

    [Theory]
    [InlineData(GeoCoordinateNotation.Gars, "721AA")]
    [InlineData(GeoCoordinateNotation.Gars, "006AI")]
    [InlineData(GeoCoordinateNotation.Gars, "006AG5")]
    [InlineData(GeoCoordinateNotation.Georef, "PJQ")]
    [InlineData(GeoCoordinateNotation.Georef, "PJQM123")]
    [InlineData(GeoCoordinateNotation.Georef, "IJQM")]
    public void A_string_that_names_no_cell_is_refused_saying_the_shape(GeoCoordinateNotation notation, string text)
    {
        Assert.False(GeoCoordinateString.TryRead(text, notation, out _, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
