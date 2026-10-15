using System;
using System.Buffers.Binary;
using Graticula.Coverages;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The bytes of <c>exportImage?format=None&amp;bsq=true</c>, as Esri's public Terrain3D service answers it — Q-168.
/// </summary>
/// <remarks>
/// Each case is a measured answer: twelve valid floats end in <c>fff0</c>, ten in <c>ffc0</c>, and eight with nothing
/// under them are eight <c>noData</c> values and <c>00</c>. The multiband layout — band after band, one mask — was
/// checked against ArcGIS Pro's Copy Raster, which wrote a three-band service cell for cell as its source.
/// </remarks>
public sealed class BsqWriterTests
{
    private static bool[] All(int n, bool value = true)
    {
        bool[] valid = new bool[n];
        Array.Fill(valid, value);
        return valid;
    }

    [Fact]
    public void Twelve_floats_are_little_endian_row_by_row_then_a_mask_of_fff0()
    {
        double[] values = [3.25, 10, 16.5, 23, 3.5, 11.5, 0, -1, 7, 8, 9, 10];

        byte[] answer = BsqWriter.Write(values, 1, 4, 3, All(12), "F32", "0");

        Assert.Equal(50, answer.Length);
        Assert.Equal(3.25f, BinaryPrimitives.ReadSingleLittleEndian(answer.AsSpan(0, 4)));
        Assert.Equal(-1f, BinaryPrimitives.ReadSingleLittleEndian(answer.AsSpan(28, 4)));
        Assert.Equal("fff0", Convert.ToHexStringLower(answer.AsSpan(48)));
    }

    [Fact]
    public void Ten_pixels_pad_their_mask_to_the_byte()
    {
        byte[] answer = BsqWriter.Write(new double[10], 1, 5, 2, All(10), "F32", null);

        Assert.Equal(42, answer.Length);
        Assert.Equal("ffc0", Convert.ToHexStringLower(answer.AsSpan(40)));
    }

    [Fact]
    public void Pixels_with_nothing_under_them_are_the_no_data_asked_and_a_zero_bit()
    {
        double[] values = [5, 5, 5, 5, 5, 5, 5, 5];

        byte[] answer = BsqWriter.Write(values, 1, 4, 2, All(8, false), "F32", "0");

        Assert.Equal(33, answer.Length);
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(answer.AsSpan(4, 4)));
        Assert.Equal("00", Convert.ToHexStringLower(answer.AsSpan(32)));
    }

    [Fact]
    public void Several_bands_are_written_one_after_another_with_one_mask_and_each_band_its_own_no_data()
    {
        // Two pixels of three bands, pixel-interleaved as the reader gives them; the second pixel empty.
        double[] values = [10, 20, 30, 11, 21, 31];

        byte[] answer = BsqWriter.Write(values, 3, 2, 1, [true, false], "U8", "255, 254, 253");

        Assert.Equal([10, 255, 20, 254, 30, 253, 0x80], answer);
    }

    [Fact]
    public void The_type_is_the_requests_or_else_the_images_own()
    {
        Assert.True(BsqWriter.TryPixelType(null, SampleKind.Unsigned16, out string own));
        Assert.Equal("U16", own);
        Assert.True(BsqWriter.TryPixelType("f32", SampleKind.Unsigned8, out string asked));
        Assert.Equal("F32", asked);
        Assert.False(BsqWriter.TryPixelType("U4", SampleKind.Unsigned8, out _));
    }
}
