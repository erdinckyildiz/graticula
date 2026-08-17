using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Geometries;

/// <summary>
/// WKB built by hand, so the tests do not depend on the thing they verify.
/// </summary>
internal sealed class WkbBuilder
{
    private readonly List<byte> _bytes = [];
    private readonly bool _littleEndian;

    public WkbBuilder(bool littleEndian = true) => _littleEndian = littleEndian;

    public WkbBuilder Header(uint type)
    {
        _bytes.Add(_littleEndian ? (byte)1 : (byte)0);
        return UInt32(type);
    }

    public WkbBuilder UInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (_littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        }

        _bytes.AddRange(buffer);
        return this;
    }

    public WkbBuilder Double(double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (_littleEndian)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        }

        _bytes.AddRange(buffer);
        return this;
    }

    public WkbBuilder Coordinates(params double[] ordinates)
    {
        foreach (double ordinate in ordinates)
        {
            Double(ordinate);
        }

        return this;
    }

    public WkbBuilder Raw(WkbBuilder other)
    {
        _bytes.AddRange(other._bytes);
        return this;
    }

    public byte[] Build() => [.. _bytes];
}

public sealed class WkbReaderTests
{
    private static byte[] Point(double x, double y, bool littleEndian = true) =>
        new WkbBuilder(littleEndian).Header(1).Coordinates(x, y).Build();

    private static byte[] UnitSquareRing() =>
        new WkbBuilder().Header(3).UInt32(1).UInt32(5)
            .Coordinates(0, 0, 1, 0, 1, 1, 0, 1, 0, 0).Build();

    [Fact]
    public void A_little_endian_point_reads()
    {
        Point point = Assert.IsType<Point>(WkbReader.Read(Point(3, 4)));

        Assert.Equal(3, point.X);
        Assert.Equal(4, point.Y);
    }

    [Fact]
    public void A_big_endian_point_reads_the_same()
    {
        // Byte order is per geometry and both appear in the wild.
        Point point = Assert.IsType<Point>(WkbReader.Read(Point(3, 4, littleEndian: false)));

        Assert.Equal(3, point.X);
        Assert.Equal(4, point.Y);
    }

    [Fact]
    public void A_line_string_reads()
    {
        byte[] wkb = new WkbBuilder().Header(2).UInt32(3)
            .Coordinates(0, 0, 10, 5, 2, -3).Build();

        LineString line = Assert.IsType<LineString>(WkbReader.Read(wkb));

        Assert.Equal(3, line.CoordinateCount);
        Assert.Equal(new Envelope(0, -3, 10, 5), line.Envelope);
    }

    [Fact]
    public void A_polygon_with_a_hole_reads_shell_and_hole_separately()
    {
        byte[] wkb = new WkbBuilder().Header(3).UInt32(2)
            .UInt32(5).Coordinates(0, 0, 10, 0, 10, 10, 0, 10, 0, 0)
            .UInt32(5).Coordinates(1, 1, 2, 1, 2, 2, 1, 2, 1, 1)
            .Build();

        Polygon polygon = Assert.IsType<Polygon>(WkbReader.Read(wkb));

        Assert.Single(polygon.Holes);
        Assert.Equal(10, polygon.CoordinateCount);
        Assert.Equal(new Envelope(0, 0, 10, 10), polygon.Envelope);
    }

    [Fact]
    public void A_multi_polygon_reads_its_parts()
    {
        byte[] wkb = new WkbBuilder().Header(6).UInt32(2)
            .Raw(new WkbBuilder().Header(3).UInt32(1).UInt32(5)
                .Coordinates(0, 0, 1, 0, 1, 1, 0, 1, 0, 0))
            .Raw(new WkbBuilder().Header(3).UInt32(1).UInt32(5)
                .Coordinates(5, 5, 6, 5, 6, 6, 5, 6, 5, 5))
            .Build();

        MultiPolygon multi = Assert.IsType<MultiPolygon>(WkbReader.Read(wkb));

        Assert.Equal(2, multi.Parts.Count);
        Assert.Equal(new Envelope(0, 0, 6, 6), multi.Envelope);
    }

    [Fact]
    public void Parts_may_use_a_different_byte_order_from_their_parent()
    {
        // WKB permits it and some producers do it. A reader that assumes the
        // parent's order reads garbage coordinates rather than failing.
        byte[] wkb = new WkbBuilder().Header(4).UInt32(2)
            .Raw(new WkbBuilder(littleEndian: true).Header(1).Coordinates(1, 2))
            .Raw(new WkbBuilder(littleEndian: false).Header(1).Coordinates(3, 4))
            .Build();

        MultiPoint multi = Assert.IsType<MultiPoint>(WkbReader.Read(wkb));

        Assert.Equal(new Envelope(1, 2, 3, 4), multi.Envelope);
    }

    [Theory]
    [InlineData(1001u)]   // ISO Z
    [InlineData(0x8000_0001u)]   // EWKB Z
    public void Z_is_dropped_and_the_caller_is_told(uint type)
    {
        // geometry-crs-policy: lossy on read means not writable. Silently
        // flattening would break that rule without anyone noticing.
        byte[] wkb = new WkbBuilder().Header(type).Coordinates(3, 4, 99).Build();

        Point point = Assert.IsType<Point>(WkbReader.Read(wkb, out bool dropped));

        Assert.Equal(3, point.X);
        Assert.Equal(4, point.Y);
        Assert.True(dropped);
    }

    [Fact]
    public void Plain_two_dimensional_geometry_reports_no_loss()
    {
        WkbReader.Read(Point(1, 2), out bool dropped);

        Assert.False(dropped);
    }

    [Fact]
    public void An_embedded_srid_is_ignored_rather_than_stored()
    {
        // CRS belongs to the layer, not to every shape in it.
        byte[] wkb = new WkbBuilder().Header(0x2000_0001u).UInt32(3857)
            .Coordinates(3, 4).Build();

        Point point = Assert.IsType<Point>(WkbReader.Read(wkb));

        Assert.Equal(3, point.X);
    }

    [Fact]
    public void A_curve_is_refused_rather_than_linearised()
    {
        // ADR-005 §3.3c. An approximation put where an exact arc was is a loss
        // nothing downstream can detect.
        byte[] wkb = new WkbBuilder().Header(8).UInt32(3)
            .Coordinates(0, 0, 1, 1, 2, 0).Build();

        WkbFormatException error = Assert.Throws<WkbFormatException>(() => WkbReader.Read(wkb));

        Assert.Contains("refused rather than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_geometry_collection_is_refused_with_a_reason()
    {
        byte[] wkb = new WkbBuilder().Header(7).UInt32(0).Build();

        Assert.Throws<WkbFormatException>(() => WkbReader.Read(wkb));
    }

    [Fact]
    public void A_declared_count_larger_than_the_buffer_is_refused_before_allocating()
    {
        // A corrupt or hostile buffer can claim four billion coordinates. Since
        // A-037 established allocation as the binding constraint, believing it
        // would be a denial of service in one line.
        byte[] wkb = new WkbBuilder().Header(2).UInt32(uint.MaxValue)
            .Coordinates(0, 0).Build();

        WkbFormatException error = Assert.Throws<WkbFormatException>(() => WkbReader.Read(wkb));

        Assert.Contains("Truncated or hostile", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_buffer_is_refused()
    {
        byte[] full = UnitSquareRing();

        Assert.Throws<WkbFormatException>(() => WkbReader.Read(full.AsSpan(0, full.Length - 8)));
    }

    [Fact]
    public void Trailing_bytes_are_refused_rather_than_ignored()
    {
        // Usually a concatenated or misaligned buffer. Ignoring the tail would
        // hide the fact that we read the wrong thing.
        byte[] wkb = [.. Point(1, 2), 0, 0, 0];

        WkbFormatException error = Assert.Throws<WkbFormatException>(() => WkbReader.Read(wkb));

        Assert.Contains("trailing byte", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_byte_order_marker_is_refused()
    {
        byte[] wkb = [7, 1, 0, 0, 0];

        Assert.Throws<WkbFormatException>(() => WkbReader.Read(wkb));
    }

    [Fact]
    public void An_empty_buffer_is_refused()
    {
        Assert.Throws<WkbFormatException>(() => WkbReader.Read([]));
    }

    [Fact]
    public void An_unknown_geometry_type_is_refused()
    {
        byte[] wkb = new WkbBuilder().Header(42).Build();

        Assert.Throws<WkbFormatException>(() => WkbReader.Read(wkb));
    }

    [Fact]
    public void An_empty_polygon_reads_as_empty()
    {
        byte[] wkb = new WkbBuilder().Header(3).UInt32(0).Build();

        Assert.True(WkbReader.Read(wkb).IsEmpty);
    }

    [Fact]
    public void A_ring_that_is_not_closed_is_refused_by_the_model()
    {
        // The reader does not validate rings; LinearRing does, and this checks
        // that the two are actually connected.
        byte[] wkb = new WkbBuilder().Header(3).UInt32(1).UInt32(4)
            .Coordinates(0, 0, 1, 0, 1, 1, 0, 1).Build();

        Assert.Throws<ArgumentException>(() => WkbReader.Read(wkb));
    }
}
