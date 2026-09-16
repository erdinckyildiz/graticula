using System;
using System.Buffers.Binary;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Geometry;

/// <summary>
/// The geometry model carries Z and M beside x and y, and a flat geometry is exactly what it was —
/// ADR-077, step 2 of ADR-074 §5.
/// </summary>
public sealed class ZAndMRideBesideXyTests
{
    [Fact]
    public void A_flat_sequence_carries_nothing_beside_x_and_y()
    {
        XySequence flat = XySequence.Wrap([0, 0, 1, 1]);

        Assert.Equal(GeometryOrdinates.None, flat.Ordinates);
        Assert.False(flat.HasZ);
        Assert.True(flat.ZSpan().IsEmpty);
        Assert.Throws<InvalidOperationException>(() => flat.Z(0));
    }

    [Fact]
    public void Z_and_M_are_indexed_like_the_coordinates_and_a_slice_keeps_them()
    {
        XySequence line = XySequence.Wrap([0, 0, 1, 1, 2, 2, 3, 3], z: [10, 11, 12, 13], m: [0, 5, 10, 15]);

        Assert.Equal(GeometryOrdinates.Z | GeometryOrdinates.M, line.Ordinates);
        Assert.Equal(12, line.Z(2));
        Assert.Equal(15, line.M(3));

        // <b>The x/y view is unchanged</b> — every hot loop reads AsSpan as x0, y0, x1, y1.
        Assert.Equal(8, line.AsSpan().Length);

        XySequence middle = line.Slice(1, 2);

        Assert.Equal(1, middle.X(0));
        Assert.Equal(11, middle.Z(0));
        Assert.Equal([11.0, 12.0], middle.ZSpan().ToArray());
        Assert.Equal(12, middle.Slice(1, 1).Z(0));
        Assert.Equal([5.0, 10.0], middle.MSpan().ToArray());
    }

    [Fact]
    public void A_length_that_does_not_match_the_coordinates_is_refused()
    {
        Assert.Throws<ArgumentException>(() => XySequence.Wrap([0, 0, 1, 1], z: [1], m: null));
        Assert.Throws<ArgumentException>(() => XySequence.Wrap([0, 0, 1, 1], z: null, m: [1, 2, 3]));
    }

    [Fact]
    public void Equality_counts_the_elevation()
    {
        Assert.NotEqual(XySequence.Wrap([0, 0], [1], null), XySequence.Wrap([0, 0], [2], null));
        Assert.NotEqual(XySequence.Wrap([0, 0], [1], null), XySequence.Wrap([0, 0]));
        Assert.Equal(XySequence.Wrap([0, 0], [1], null), XySequence.Wrap([0, 0], [1], null));
    }

    [Fact]
    public void A_flat_point_is_the_plain_object_it_always_was()
    {
        // <b>Measured, not asserted for tidiness</b>: a reference on every point cost eight bytes each,
        // nine per cent more allocation over a million points (ADR-077 §4).
        Assert.Equal(typeof(Point), Point.Create(1, 2, null, null).GetType());

        Point high = Point.Create(1, 2, 300, null);

        Assert.Equal(GeometryOrdinates.Z, high.Ordinates);
        Assert.Equal(300, high.Z);
        Assert.Null(high.M);
        Assert.Null(new Point(1, 2).Z);
    }

    // ---------- WKB ----------

    private static byte[] Wkb(uint type, params double[] ordinates)
    {
        bool single = type % 1000 == 1;
        byte[] b = new byte[1 + 4 + (single ? 0 : 4) + (ordinates.Length * 8)];
        b[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), type);
        int o = 5;

        if (!single)
        {
            int stride = (type / 1000) switch { 1 or 2 => 3, 3 => 4, _ => 2 };
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), (uint)(ordinates.Length / stride));
            o += 4;
        }

        foreach (double value in ordinates)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o), value);
            o += 8;
        }

        return b;
    }

    [Fact]
    public void By_default_the_reader_still_drops_Z_and_says_so()
    {
        Graticula.Geometries.Geometry read = WkbReader.Read(Wkb(1002, 0, 0, 5, 1, 1, 6), out bool dropped);

        Assert.True(dropped);
        Assert.Equal(GeometryOrdinates.None, ((LineString)read).Coordinates.Ordinates);
    }

    [Fact]
    public void Asked_to_keep_them_it_reads_XYZ_XYM_and_XYZM_in_WKB_s_order()
    {
        LineString z = (LineString)WkbReader.Read(Wkb(1002, 0, 0, 5, 1, 1, 6), keepOrdinates: true, out bool dropped);
        Assert.False(dropped);
        Assert.Equal([5.0, 6.0], z.Coordinates.ZSpan().ToArray());
        Assert.Equal([0.0, 0.0, 1.0, 1.0], z.Coordinates.AsSpan().ToArray());

        LineString m = (LineString)WkbReader.Read(Wkb(2002, 0, 0, 7, 1, 1, 8), keepOrdinates: true, out _);
        Assert.Equal(GeometryOrdinates.M, m.Coordinates.Ordinates);
        Assert.Equal([7.0, 8.0], m.Coordinates.MSpan().ToArray());

        LineString zm = (LineString)WkbReader.Read(Wkb(3002, 0, 0, 5, 7, 1, 1, 6, 8), keepOrdinates: true, out _);
        Assert.Equal([5.0, 6.0], zm.Coordinates.ZSpan().ToArray());
        Assert.Equal([7.0, 8.0], zm.Coordinates.MSpan().ToArray());

        Point p = (Point)WkbReader.Read(Wkb(3001, 1, 2, 300, 42), keepOrdinates: true, out _);
        Assert.Equal(300, p.Z);
        Assert.Equal(42, p.M);
    }

    [Fact]
    public void A_mask_keeps_what_was_asked_for_and_reports_the_rest()
    {
        // `returnZ=true` without `returnM` on an XYZM column: Z comes back, M is dropped and said to be.
        LineString line = (LineString)WkbReader.Read(Wkb(3002, 0, 0, 5, 7, 1, 1, 6, 8), GeometryOrdinates.Z, out bool dropped);
        Assert.True(dropped);
        Assert.Equal(GeometryOrdinates.Z, line.Coordinates.Ordinates);
        Assert.Equal([5.0, 6.0], line.Coordinates.ZSpan().ToArray());

        Point p = (Point)WkbReader.Read(Wkb(3001, 1, 2, 300, 42), GeometryOrdinates.M, out _);
        Assert.Null(p.Z);
        Assert.Equal(42, p.M);

        // Asking for more than the column has is not a loss.
        WkbReader.Read(Wkb(1002, 0, 0, 5, 1, 1, 6), GeometryOrdinates.Z | GeometryOrdinates.M, out bool none);
        Assert.False(none);
    }
}
