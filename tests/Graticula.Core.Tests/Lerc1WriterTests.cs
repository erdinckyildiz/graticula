using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Graticula.Coverages;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ADR-182: LERC 1, the elevation tile a 3D scene decodes. Read back here by a decoder of the format as Esri publishes it
/// (github.com/Esri/lerc, <c>src/LercLib/Lerc1Decode</c>), written for this test; that the tiles open in Esri's own
/// decoders — the <c>lerc</c> package and the JS SDK's elevation layer — is recorded in the ADR, measured where they run.
/// </summary>
public sealed class Lerc1WriterTests
{
    [Fact]
    public void A_model_comes_back_within_the_error_and_the_pixels_without_a_value_stay_out()
    {
        const int side = 257;
        Random random = new(7);
        double[] heights = new double[side * side];
        bool[] valid = new bool[side * side];

        for (int i = 0; i < heights.Length; i++)
        {
            int x = i % side, y = i / side;
            heights[i] = 800 + (x * 3.7) + (y * 1.3) + (random.NextDouble() * 40);
            valid[i] = !(x < 40 && y > 200);    // a corner off the model, as at a coverage's edge
        }

        heights[5] = double.NaN;    // and a hole, which is never a value
        valid[5] = true;

        (float[] back, bool[] held) = Read(Lerc1Writer.Write(heights, valid, side, side, 0.1), out int width, out int height);

        Assert.Equal((side, side), (width, height));

        for (int i = 0; i < heights.Length; i++)
        {
            bool expected = valid[i] && !double.IsNaN(heights[i]);
            Assert.Equal(expected, held[i]);

            if (expected)
            {
                Assert.InRange(back[i] - heights[i], -0.1 - 1e-3, 0.1 + 1e-3);
            }
        }
    }

    [Fact]
    public void A_flat_sea_and_a_full_image_need_no_mask_and_little_room()
    {
        double[] sea = new double[257 * 257];
        byte[] written = Lerc1Writer.Write(sea, null, 257, 257, 0.1);
        (float[] back, bool[] held) = Read(written, out _, out _);

        Assert.All(held, Assert.True);
        Assert.All(back, v => Assert.Equal(0f, v));
        Assert.True(written.Length < 600, $"A flat tile took {written.Length} bytes.");
    }

    [Fact]
    public void Nothing_held_is_a_tile_of_nothing()
    {
        (_, bool[] held) = Read(Lerc1Writer.Write(new double[16 * 16], new bool[16 * 16], 16, 16, 0.1), out _, out _);
        Assert.All(held, Assert.False);
    }

    /// <summary>The format, read: header, count part, z part, each a grid of blocks.</summary>
    private static (float[] Values, bool[] Held) Read(byte[] blob, out int width, out int height)
    {
        Assert.Equal("CntZImage ", Encoding.ASCII.GetString(blob, 0, 10));
        ReadOnlySpan<byte> b = blob;
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(b[10..]));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(b[14..]));
        height = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
        width = BinaryPrimitives.ReadInt32LittleEndian(b[22..]);
        double maxZError = BinaryPrimitives.ReadDoubleLittleEndian(b[26..]);
        int at = 34;

        float[] cnt = new float[width * height];
        float[] z = new float[width * height];
        bool ignoreMask = false;

        for (int part = 0; part < 2; part++)
        {
            int down = BinaryPrimitives.ReadInt32LittleEndian(b[at..]);
            int across = BinaryPrimitives.ReadInt32LittleEndian(b[(at + 4)..]);
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(b[(at + 8)..]);
            float maxValue = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 12)..]);
            at += 16;
            int end = at + bytes;

            if (part == 0)
            {
                // The count part is a constant or a run-length coded bit mask — the form the JS SDK's decoder reads.
                Assert.Equal((0, 0), (down, across));

                if (bytes == 0)
                {
                    Array.Fill(cnt, maxValue);
                    ignoreMask = maxValue > 0;
                }
                else
                {
                    byte[] mask = Unrun(blob, at, end, (width * height + 7) / 8);
                    for (int k = 0; k < cnt.Length; k++) cnt[k] = (mask[k >> 3] & (0x80 >> (k & 7))) != 0 ? 1 : 0;
                }

                at = end;
                continue;
            }

            int p = at;

            for (int row = 0; row <= down; row++)
            {
                int tall = height / down, i0 = row * tall;
                tall = row == down ? height % down : tall;

                for (int column = 0; tall > 0 && column <= across; column++)
                {
                    int wide = width / across, j0 = column * wide;
                    wide = column == across ? width % across : wide;

                    if (wide == 0)
                    {
                        continue;
                    }

                    List<int> pixels = [];

                    for (int i = i0; i < i0 + tall; i++)
                    {
                        for (int j = j0; j < j0 + wide; j++)
                        {
                            pixels.Add((i * width) + j);
                        }
                    }

                    byte flag = blob[p++];
                    int sizeBits = flag >> 6;
                    int kind = flag & 63;

                    if (part == 0)
                    {
                        if (flag is 2 or 4)
                        {
                            pixels.ForEach(k => cnt[k] = flag == 4 ? 1 : 0);
                            continue;
                        }

                        float offset = Offset(blob, ref p, sizeBits);
                        uint[] values = Unstuff(blob, ref p);
                        for (int k = 0; k < pixels.Count; k++) cnt[pixels[k]] = offset + values[k];
                        continue;
                    }

                    List<int> held = ignoreMask ? pixels : pixels.FindAll(k => cnt[k] > 0);

                    if (kind == 2)
                    {
                        held.ForEach(k => z[k] = 0);
                    }
                    else if (kind == 0)
                    {
                        foreach (int k in held)
                        {
                            z[k] = BitConverter.ToSingle(blob, p);
                            p += 4;
                        }
                    }
                    else
                    {
                        float offset = Offset(blob, ref p, sizeBits);

                        if (kind == 3)
                        {
                            held.ForEach(k => z[k] = offset);
                        }
                        else
                        {
                            uint[] values = Unstuff(blob, ref p);
                            Assert.Equal(held.Count, values.Length);
                            for (int k = 0; k < held.Count; k++)
                            {
                                z[held[k]] = Math.Min((float)(offset + (values[k] * 2 * maxZError)), maxValue);
                            }
                        }
                    }
                }
            }

            Assert.Equal(end, p);
            at = end;
        }

        Assert.Equal(blob.Length, at);
        return (z, Array.ConvertAll(cnt, c => c > 0));
    }

    private static byte[] Unrun(byte[] blob, int p, int end, int size)
    {
        List<byte> bytes = [];

        while (true)
        {
            short count = BitConverter.ToInt16(blob, p);
            p += 2;

            if (count == short.MinValue)
            {
                break;
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++) bytes.Add(blob[p++]);
            }
            else
            {
                byte b = blob[p++];
                for (int i = 0; i < -count; i++) bytes.Add(b);
            }
        }

        Assert.Equal(end, p);
        Assert.Equal(size, bytes.Count);
        return [.. bytes];
    }

    private static float Offset(byte[] blob, ref int p, int sizeBits)
    {
        switch (sizeBits)
        {
            case 2: return (sbyte)blob[p++];
            case 1: p += 2; return BitConverter.ToInt16(blob, p - 2);
            default: p += 4; return BitConverter.ToSingle(blob, p - 4);
        }
    }

    private static uint[] Unstuff(byte[] blob, ref int p)
    {
        byte head = blob[p++];
        int bits = head & 63, size = head >> 6;
        uint n = size == 2 ? blob[p] : size == 1 ? BitConverter.ToUInt16(blob, p) : BitConverter.ToUInt32(blob, p);
        p += size == 2 ? 1 : size == 1 ? 2 : 4;

        int bytes = (int)(((n * bits) + 7) / 8);
        int words = (int)(((n * bits) + 31) / 32);
        byte[] padded = new byte[words * 4];
        Array.Copy(blob, p, padded, 0, bytes);
        p += bytes;

        uint[] w = new uint[words];
        for (int i = 0; i < words; i++) w[i] = BitConverter.ToUInt32(padded, i * 4);

        int tail = (int)(((n * bits) & 31) + 7) >> 3;
        int unneeded = tail > 0 ? 4 - tail : 0;
        if (words > 0) w[^1] <<= 8 * unneeded;

        // Read as one bit stream, high bit first in each word.
        uint[] values = new uint[n];
        long position = 0;

        for (int k = 0; k < n; k++)
        {
            uint v = 0;
            for (int bit = 0; bit < bits; bit++, position++)
            {
                v = (v << 1) | ((w[position >> 5] >> (31 - (int)(position & 31))) & 1);
            }

            values[k] = v;
        }

        return values;
    }
}
