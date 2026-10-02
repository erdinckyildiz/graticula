using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ADR-137: LERC as Esri publishes it. Read back here by a decoder of the format written for this test; that the blobs
/// open in Esri's own decoders — the <c>lerc</c> package and the JS SDK — is recorded in the ADR, measured where those run.
/// </summary>
public sealed class LercWriterTests
{
    public static TheoryData<SampleKind, double> Kinds => new()
    {
        { SampleKind.Unsigned8, 0 },
        { SampleKind.Signed16, 0 },
        { SampleKind.Unsigned16, 0 },
        { SampleKind.Signed32, 0 },
        { SampleKind.Real32, 0 },
        { SampleKind.Real32, 0.01 },
        { SampleKind.Real64, 0 },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Values_come_back_within_the_tolerance_and_the_masked_pixels_stay_out(SampleKind kind, double tolerance)
    {
        const int width = 37, height = 21;
        Random random = new(5);
        double[] samples = new double[width * height];
        bool[] valid = new bool[width * height];

        for (int i = 0; i < samples.Length; i++)
        {
            int x = i % width;
            samples[i] = kind switch
            {
                SampleKind.Unsigned8 => random.Next(0, 256),
                SampleKind.Signed16 => random.Next(-3000, 3000),
                SampleKind.Unsigned16 => 60000 + x,
                SampleKind.Signed32 => (x * 100000) - (i / width),
                SampleKind.Real32 => (float)((Math.Sin(x / 5.0) * 100) + (i / width * 0.123)),
                _ => (x * 1e-9) + (i / width),
            };
            valid[i] = x > 3 && i / width != 5;
        }

        (Header header, double[]? values, bool[] mask) = Decode(LercWriter.Write(samples, valid, width, height, 1, kind, tolerance));

        Assert.Equal((width, height), (header.Width, header.Height));
        Assert.Equal(Array.FindAll(valid, v => v).Length, header.Valid);
        Assert.Equal(valid, mask);

        double allowed = kind is SampleKind.Real32 or SampleKind.Real64 ? tolerance : 0;

        for (int i = 0; i < samples.Length; i++)
        {
            if (valid[i])
            {
                Assert.InRange(values![i], samples[i] - allowed - 1e-9, samples[i] + allowed + 1e-9);
            }
        }
    }

    [Fact]
    public void A_tolerance_makes_a_smooth_surface_smaller_and_an_integer_is_never_quantized()
    {
        const int width = 64, height = 64;
        double[] surface = new double[width * height];

        for (int i = 0; i < surface.Length; i++)
        {
            surface[i] = (float)(1000 + (Math.Sin(i % width / 9.0) * 50) + (i / width * 0.37));
        }

        int exact = LercWriter.Write(surface, null, width, height, 1, SampleKind.Real32, 0).Length;
        int close = LercWriter.Write(surface, null, width, height, 1, SampleKind.Real32, 0.01).Length;
        Assert.True(close < exact * 0.75, $"{close} bytes at 0.01 against {exact} exact.");

        double[] counts = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        (_, double[]? back, _) = Decode(LercWriter.Write(counts, null, 9, 1, 1, SampleKind.Signed16, 3));
        Assert.Equal(counts, back);
    }

    [Fact]
    public void Each_band_is_a_blob_of_its_own_and_nan_is_no_value()
    {
        double[] samples = [1, 10, double.NaN, 20, 3, 30, 4, double.NaN];
        byte[] blobs = LercWriter.Write(samples, null, 2, 2, 2, SampleKind.Real32, 0);

        (Header first, double[]? one, bool[] firstMask) = Decode(blobs);
        Assert.Equal(3, first.Valid);
        Assert.Equal([true, false, true, true], firstMask);
        Assert.Equal(1, one![0]);
        Assert.Equal(4, one[3]);

        (Header second, double[]? two, bool[] secondMask) = Decode(blobs.AsSpan(first.BlobSize).ToArray());
        Assert.Equal(blobs.Length, first.BlobSize + second.BlobSize);
        Assert.Equal([true, true, true, false], secondMask);
        Assert.Equal(30, two![2]);
    }

    [Fact]
    public void Nothing_valid_and_one_value_everywhere_are_headers_alone()
    {
        (Header empty, _, bool[] none) = Decode(LercWriter.Write([1, 2, 3, 4], [false, false, false, false], 2, 2, 1, SampleKind.Real32, 0));
        Assert.Equal(0, empty.Valid);
        Assert.DoesNotContain(true, none);

        byte[] flat = LercWriter.Write([4.5, 4.5, 4.5, 4.5], null, 2, 2, 1, SampleKind.Real32, 0);
        (Header constant, double[]? values, _) = Decode(flat);
        Assert.Equal((4.5, 4.5), (constant.Low, constant.High));
        Assert.All(values!, v => Assert.Equal(4.5, v));
        Assert.Equal(62 + 4, flat.Length);
    }

    private sealed record Header(int Width, int Height, int Valid, int BlobSize, int Type, double MaxZError, double Low, double High);

    /// <summary>A decoder of what the writer writes: blob version 3, blocks, no Huffman coding, no lookup tables.</summary>
    private static (Header Header, double[]? Values, bool[] Mask) Decode(byte[] blob)
    {
        ReadOnlySpan<byte> b = blob;
        Assert.Equal("Lerc2 ", Encoding.ASCII.GetString(blob, 0, 6));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(b[6..]));
        Header h = new(
            BinaryPrimitives.ReadInt32LittleEndian(b[18..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[14..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[22..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[30..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[34..]),
            BinaryPrimitives.ReadDoubleLittleEndian(b[38..]),
            BinaryPrimitives.ReadDoubleLittleEndian(b[46..]),
            BinaryPrimitives.ReadDoubleLittleEndian(b[54..]));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(b[26..]));
        Assert.Equal(Checksum(b[14..h.BlobSize]), BinaryPrimitives.ReadUInt32LittleEndian(b[10..]));

        int count = h.Width * h.Height;
        int at = 62;
        int maskBytes = BinaryPrimitives.ReadInt32LittleEndian(b[at..]);
        at += 4;
        bool[] mask = new bool[count];

        if (maskBytes == 0)
        {
            Array.Fill(mask, h.Valid == count);
        }
        else
        {
            List<byte> bits = [];
            int m = at;

            while (true)
            {
                short n = BinaryPrimitives.ReadInt16LittleEndian(b[m..]);
                m += 2;

                if (n == short.MinValue)
                {
                    break;
                }

                if (n > 0)
                {
                    bits.AddRange(b.Slice(m, n).ToArray());
                    m += n;
                }
                else
                {
                    for (int i = 0; i < -n; i++)
                    {
                        bits.Add(b[m]);
                    }

                    m++;
                }
            }

            Assert.Equal(at + maskBytes, m);

            for (int i = 0; i < count; i++)
            {
                mask[i] = (bits[i >> 3] & (128 >> (i & 7))) != 0;
            }
        }

        at += maskBytes;

        if (h.Valid == 0)
        {
            return (h, null, mask);
        }

        double[] values = new double[count];

        if (h.Low == h.High)
        {
            Array.Fill(values, h.Low);
            return (h, values, mask);
        }

        Assert.Equal(0, b[at++]);

        if (h.Type == 1 && h.MaxZError == 0.5)
        {
            Assert.Equal(0, b[at++]);
        }

        int size = h.Type switch { 0 or 1 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, _ => 8 };

        double Read(ReadOnlySpan<byte> s) => h.Type switch
        {
            1 => s[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(s),
            3 => BinaryPrimitives.ReadUInt16LittleEndian(s),
            4 => BinaryPrimitives.ReadInt32LittleEndian(s),
            6 => BinaryPrimitives.ReadSingleLittleEndian(s),
            _ => BinaryPrimitives.ReadDoubleLittleEndian(s),
        };

        for (int top = 0; top < h.Height; top += 8)
        {
            for (int left = 0; left < h.Width; left += 8)
            {
                List<int> cells = [];

                for (int y = top; y < Math.Min(top + 8, h.Height); y++)
                {
                    for (int x = left; x < Math.Min(left + 8, h.Width); x++)
                    {
                        if (mask[(y * h.Width) + x])
                        {
                            cells.Add((y * h.Width) + x);
                        }
                    }
                }

                byte flag = b[at++];
                Assert.Equal((left >> 3) & 15, (flag >> 2) & 15);
                Assert.Equal(0, flag >> 6);

                switch (flag & 3)
                {
                    case 2:
                        cells.ForEach(c => values[c] = 0);
                        break;
                    case 0:
                        foreach (int c in cells)
                        {
                            values[c] = Read(b.Slice(at, size));
                            at += size;
                        }

                        break;
                    default:
                        double offset = Read(b.Slice(at, size));
                        at += size;

                        if ((flag & 3) == 3)
                        {
                            cells.ForEach(c => values[c] = offset);
                            break;
                        }

                        byte bitsByte = b[at++];
                        int bitCount = bitsByte & 31;
                        int n = (bitsByte >> 6) switch { 0 => 4, 1 => 2, _ => 1 };
                        int elements = 0;

                        for (int i = 0; i < n; i++)
                        {
                            elements |= b[at++] << (8 * i);
                        }

                        Assert.Equal(cells.Count, elements);
                        long position = 0;

                        foreach (int c in cells)
                        {
                            uint q = 0;

                            for (int bit = 0; bit < bitCount; bit++, position++)
                            {
                                if ((b[at + (int)(position >> 3)] & (1 << (int)(position & 7))) != 0)
                                {
                                    q |= 1u << bit;
                                }
                            }

                            values[c] = Math.Min(offset + (q * 2 * h.MaxZError), h.High);
                        }

                        at += (int)((position + 7) / 8);
                        break;
                }
            }
        }

        Assert.Equal(h.BlobSize, at);
        return (h, values, mask);
    }

    /// <summary>Fletcher-32 as the format reduces it, every 359 byte pairs.</summary>
    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint a = 0xffff, c = 0xffff;
        int words = bytes.Length / 2, i = 0;

        while (words > 0)
        {
            int chunk = Math.Min(words, 359);
            words -= chunk;

            for (int k = 0; k < chunk; k++, i += 2)
            {
                a += (uint)((bytes[i] << 8) + bytes[i + 1]);
                c += a;
            }

            a = (a & 0xffff) + (a >> 16);
            c = (c & 0xffff) + (c >> 16);
        }

        if ((bytes.Length & 1) != 0)
        {
            a += (uint)(bytes[i] << 8);
            c += a;
        }

        a = (a & 0xffff) + (a >> 16);
        c = (c & 0xffff) + (c >> 16);
        return (c << 16) | a;
    }
}
