using System;
using Graticula.Api.ArcGis.Offline;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// The offline copy's geometry blob is written as ArcGIS writes it — ADR-288.
/// </summary>
/// <remarks>
/// <b>Every expected value here is a blob ArcGIS Pro 3.x wrote</b> (<c>arcpy.da.InsertCursor</c> into a mobile
/// geodatabase it created, 2026-11-10) for the geometry beside it, which this project chose. No file Esri wrote is in
/// the repository; these bytes are the measurement, as research note §2 describes it.
/// </remarks>
public sealed class MobileGeodatabaseGeometryTests
{
    private static readonly CoordinateGrid Wgs84 = CoordinateGrid.Geographic;
    private static readonly CoordinateGrid Mercator = CoordinateGrid.WebMercator;

    private static XySequence Xy(params double[] xy) => XySequence.Wrap(xy);

    private static LinearRing Ring(params double[] xy) => new(Xy(xy));

    public static TheoryData<string, Geometry, int, bool, bool, string> Written => new()
    {
        { "point at the origin", new Point(0, 0), 4326, false, false,
            "64e610000100000004010c000000010000008080dd9da4178080dd9da417" },
        { "point at (1, 1)", new Point(1, 1), 4326, false, false,
            "64e610000100000004010c0000000100000080a8b3d7ab1780a8b3d7ab17" },
        { "point at (-1, -1)", new Point(-1, -1), 4326, false, false,
            "64e610000100000004010c0000000100000080d886e49c1780d886e49c17" },
        { "Istanbul", new Point(28.9784, 41.0082), 4326, false, false,
            "64e610000100000004010c0000000100000080acd791fc1880e598e2d519" },
        { "Web Mercator point", new Point(3150000, 4750000), 3857, false, false,
            "64110f000100000004010c0000000100000080ed91cfbf0d808f9185af14" },
        { "Web Mercator point, sub-metre", new Point(3150000.5, 4750000.25), 3857, false, false,
            "64110f000100000004010c0000000100000088bb92cfbf0d84b69185af14" },
        { "point Z 42", Point.Create(3150000, 4750000, 42, null), 3857, true, false,
            "64110f00010000000601110000000101000080ed91cfbf0d808f9185af14a0ca89ba07" },
        { "point Z -10.5", Point.Create(3150000, 4750000, -10.5, null), 3857, true, false,
            "64110f00010000000601110000000101000080ed91cfbf0d808f9185af1498bfc9b907" },
        { "point ZM", Point.Create(3150000, 4750000, 42, 7), 3857, true, true,
            "64110f00010000000701160000000103000080ed91cfbf0d808f9185af14a0ca89ba07b0eddeb907" },
        { "point M", Point.Create(1, 2, null, 5), 4326, false, true,
            "64e61000010000000501110000000102000080a8b3d7ab1780d08991b31790b5dcb907" },
        { "multipoint of two", new MultiPoint([new Point(1, 1), new Point(2, 3)]), 4326, false, false,
            "64e61000020000000841160000000100000080a8b3d7ab1780a8b3d7ab1780a8d6b90780d0acf30e" },
        { "multipoint of one", new MultiPoint([new Point(1, 1)]), 4326, false, false,
            "64e610000100000008010c0000000100000080a8b3d7ab1780a8b3d7ab17" },
        { "segment", new LineString(Xy(0, 0, 1, 1)), 4326, false, false,
            "64e6100002000000140416000000010000008080dd9da4178080dd9da41780a8d6b90780a8d6b907" },
        { "three-point line", new LineString(Xy(0, 0, 1, 1, 2, 0)), 4326, false, false,
            "64e6100003000000140420000000010000008080dd9da4178080dd9da41780a8d6b90780a8d6b90780a8d6b907c0a8d6b907" },
        { "two-part line", new MultiLineString([new LineString(Xy(0, 0, 1, 1)), new LineString(Xy(5, 5, 6, 7))]), 4326, false, false,
            "64e61000050000001444010338000000010000008080dd9da4178080dd9da41780a8d6b90780a8d6b907c1a8b3d7ab17c0a8b3d7ab1781c88cbec91780c88cbec91780a8d6b90780d0acf30e" },
        { "line with Z", new LineString(XySequence.Wrap([3150000, 4750000, 3150010, 4750020], [1, 2], null)), 3857, true, false,
            "64110f000200000016041a0000000101000080ed91cfbf0d808f9185af14a09a0c80b51890c4d7b907909c01" },
        { "square", new Polygon(Ring(0, 0, 0, 1, 1, 1, 1, 0, 0, 0)), 4326, false, false,
            "64e6100005000000180824000000011000048080dd9da4178080dd9da41780a8d6b907000080a8d6b907c0a8d6b9070000c0a8d6b907" },
        { "square with a hole", new Polygon(Ring(0, 0, 0, 10, 10, 10, 10, 0, 0, 0), [Ring(2, 2, 8, 2, 8, 8, 2, 8, 2, 2)]), 4326, false, false,
            "64e610000a000000180886010000011000048080dd9da4178080dd9da4178090dfc04a00008090dfc04ac090dfc04a0000c090dfc04a80d0acf30e80d0acf30e0080f085da2c80f085da2c0000c0f085da2cc0f085da2c00" },
        { "two squares, so a box", new MultiPolygon([new Polygon(Ring(0, 0, 0, 1, 1, 1, 1, 0, 0, 0)), new Polygon(Ring(5, 5, 5, 6, 6, 6, 6, 5, 5, 5))]), 4326, false, false,
            "64e610000b00000018488080dd9da4178080dd9da41780f0e2f7d01780f0e2f7d017010694010000011000048080dd9da4178080dd9da41780a8d6b907000080a8d6b907c0a8d6b9070000c0a8d6b907c180dd9da417c080dd9da41781c88cbec91780c88cbec91780a8d6b907000080a8d6b907c0a8d6b9070000c0a8d6b907" },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public void The_blob_is_the_one_ArcGIS_Pro_wrote(string name, Geometry geometry, int srid, bool z, bool m, string expected)
    {
        GeometryOrdinates ordinates = (z ? GeometryOrdinates.Z : 0) | (m ? GeometryOrdinates.M : 0);
        CoordinateGrid grid = srid == 4326 ? Wgs84 : Mercator;

        byte[]? blob = MobileGeodatabaseGeometry.Encode(geometry, srid, grid, ordinates);

        Assert.True(blob is not null, name);
        Assert.Equal(expected, Convert.ToHexString(blob).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(Written))]
    public void What_ArcGIS_Pro_wrote_reads_back_as_the_geometry(string name, Geometry geometry, int srid, bool z, bool m, string blob)
    {
        CoordinateGrid grid = srid == 4326 ? Wgs84 : Mercator;
        GeometryOrdinates ordinates = (z ? GeometryOrdinates.Z : 0) | (m ? GeometryOrdinates.M : 0);

        Geometry read = MobileGeodatabaseGeometry.Decode(Convert.FromHexString(blob), grid, out int readSrid);

        Assert.Equal(srid, readSrid);
        Assert.Equal(geometry.Kind, read.Kind);
        // Written again, the decoded geometry is the same bytes: the strongest equality a grid allows.
        byte[]? again = MobileGeodatabaseGeometry.Encode(read, srid, grid, ordinates);
        Assert.True(again is not null, name);
        Assert.Equal(blob, Convert.ToHexString(again).ToLowerInvariant());
    }

    [Fact]
    public void A_shell_written_counter_clockwise_is_turned_as_ArcGIS_turns_it()
    {
        var anticlockwise = new Polygon(Ring(0, 0, 1, 0, 1, 1, 0, 1, 0, 0));
        var clockwise = new Polygon(Ring(0, 0, 0, 1, 1, 1, 1, 0, 0, 0));

        Assert.Equal(
            MobileGeodatabaseGeometry.Encode(clockwise, 4326, Wgs84, GeometryOrdinates.None),
            MobileGeodatabaseGeometry.Encode(anticlockwise, 4326, Wgs84, GeometryOrdinates.None));
    }

    [Fact]
    public void An_empty_geometry_is_a_null_shape()
    {
        Assert.Null(MobileGeodatabaseGeometry.Encode(Polygon.Empty, 4326, Wgs84, GeometryOrdinates.None));
    }

    [Fact]
    public void A_coordinate_off_the_grid_is_refused_rather_than_wrapped()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MobileGeodatabaseGeometry.Encode(new Point(-500, 0), 4326, Wgs84, GeometryOrdinates.None));
    }

    [Fact]
    public void A_value_that_is_not_a_geometry_is_refused()
    {
        Assert.Throws<System.IO.InvalidDataException>(
            () => MobileGeodatabaseGeometry.Decode(new byte[20], Wgs84, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(-64)]
    [InlineData(70)]
    [InlineData(1_000_000_000)]
    [InlineData(-401_000_000_001)]
    [InlineData(long.MaxValue / 4)]
    public void A_number_reads_back_as_itself(long value)
    {
        using var stream = new System.IO.MemoryStream();
        MobileGeodatabaseGeometry.WriteVarint(stream, value);
        int at = 0;
        Assert.Equal(value, MobileGeodatabaseGeometry.ReadVarint(stream.ToArray(), ref at));
        Assert.Equal((int)stream.Length, at);
    }
}
