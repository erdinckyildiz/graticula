using System;
using System.Buffers.Binary;
using System.IO;

namespace Graticula.Coverages;

/// <summary>
/// Writes one band of heights as LERC version 1 — ADR-182, the elevation tile an ArcGIS 3D scene decodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version 1 because that is what the scene reads.</b> The JavaScript SDK's elevation layer decodes its tiles with a
/// LERC 1 decoder only, and Esri's own elevation services answer in it: a Lerc2 tile — what <see cref="LercWriter"/>
/// writes for <c>exportImage</c> — was fetched by a scene, refused by its decoder, and every height came back zero
/// (measured 2026-10-13 in a headless SceneView against this server and against Esri's Terrain3D).
/// </para>
/// <para>
/// <b>The format as Esri publishes it</b> (github.com/Esri/lerc, <c>src/LercLib/Lerc1Decode</c>, Apache-2.0):
/// <c>"CntZImage "</c>, version 11, type 8, height, width and the largest error; then a count part — which pixels
/// hold a value, a run-length coded bit mask — and a z part, a grid of blocks. A block is constant, raw floats, or an
/// offset and the values quantized to twice the error and bit-stuffed into big-endian-ordered words. Written here from that decoder's reading
/// of the format; no code was taken.
/// </para>
/// <para>
/// <b>The block size is chosen by trying</b> 8, 16 and 32 and keeping the smallest, as a tile of flat sea compresses
/// best in large blocks and a mountain in small ones.
/// </para>
/// </remarks>
public static class Lerc1Writer
{
    private static readonly int[] BlockSizes = [8, 16, 32];

    /// <summary>Writes heights as a LERC 1 blob.</summary>
    /// <param name="values">Width × height values, row by row.</param>
    /// <param name="valid">Whether each pixel holds a value, or null when all do; a NaN never does.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="maxZError">The largest error a value may carry; above zero.</param>
    /// <returns>The blob.</returns>
    public static byte[] Write(double[] values, bool[]? valid, int width, int height, double maxZError)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxZError);

        if (values.Length != width * height || (valid is not null && valid.Length != width * height))
        {
            throw new ArgumentException($"There is not one value and one flag for each of {width} × {height} pixels.", nameof(values));
        }

        bool[] holds = new bool[values.Length];
        bool all = true;
        float maxZ = float.MinValue;

        for (int i = 0; i < values.Length; i++)
        {
            holds[i] = (valid is null || valid[i]) && double.IsFinite(values[i]);
            all &= holds[i];

            if (holds[i])
            {
                maxZ = Math.Max(maxZ, (float)values[i]);
            }
        }

        byte[]? best = null;

        foreach (int block in BlockSizes)
        {
            byte[] written = Encode(values, holds, all, maxZ == float.MinValue ? 0 : maxZ, width, height, maxZError, block);

            if (best is null || written.Length < best.Length)
            {
                best = written;
            }
        }

        return best!;
    }

    private static byte[] Encode(
        double[] values, bool[] holds, bool all, float maxZ, int width, int height, double maxZError, int block)
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);

        w.Write("CntZImage "u8);
        w.Write(11);
        w.Write(8);
        w.Write(height);
        w.Write(width);
        w.Write(maxZError);

        int down = Math.Max(1, height / block);
        int across = Math.Max(1, width / block);

        // The count part: constant one when every pixel holds a value, which also tells the decoder to read a value for
        // every pixel; otherwise a bit mask, run-length coded. The format also allows the count part as a grid of blocks,
        // and Esri's C++ decoder reads that — but the JavaScript SDK's elevation decoder reads only the mask, and every
        // tile at a coverage's edge failed in a scene with "Offset is outside the bounds of the DataView" (ux review,
        // 2026-10-13).
        w.Write(0);
        w.Write(0);

        if (all)
        {
            w.Write(0);
            w.Write(1f);
        }
        else
        {
            byte[] mask = RunLength(Mask(holds));
            w.Write(mask.Length);
            w.Write(1f);
            w.Write(mask);
        }

        byte[] heights = Blocks(width, height, down, across,
            (i0, i1, j0, j1, into) => HeightBlock(values, holds, width, i0, i1, j0, j1, maxZError, into));
        w.Write(down);
        w.Write(across);
        w.Write(heights.Length);
        w.Write(maxZ);
        w.Write(heights);

        w.Flush();
        return file.ToArray();
    }

    /// <summary>Walks the grid as the decoder does: whole blocks, then a narrower last row and column of what is left.</summary>
    private static byte[] Blocks(int width, int height, int down, int across, Action<int, int, int, int, BinaryWriter> write)
    {
        using MemoryStream body = new();
        using BinaryWriter into = new(body);

        for (int row = 0; row <= down; row++)
        {
            int tall = height / down;
            int i0 = row * tall;
            tall = row == down ? height % down : tall;

            for (int column = 0; tall > 0 && column <= across; column++)
            {
                int wide = width / across;
                int j0 = column * wide;
                wide = column == across ? width % across : wide;

                if (wide > 0)
                {
                    write(i0, i0 + tall, j0, j0 + wide, into);
                }
            }
        }

        into.Flush();
        return body.ToArray();
    }

    /// <summary>One bit a pixel, row by row, the high bit of each byte first; set where the pixel holds a value.</summary>
    private static byte[] Mask(bool[] holds)
    {
        byte[] bits = new byte[(holds.Length + 7) / 8];

        for (int k = 0; k < holds.Length; k++)
        {
            if (holds[k])
            {
                bits[k >> 3] |= (byte)(0x80 >> (k & 7));
            }
        }

        return bits;
    }

    /// <summary>
    /// LERC's run-length code: little-endian 16-bit counts, a positive one followed by that many bytes as they are, a
    /// negative one by one byte that many times, and -32768 to end.
    /// </summary>
    private static byte[] RunLength(byte[] bytes)
    {
        using MemoryStream coded = new();
        using BinaryWriter w = new(coded);
        int at = 0;

        while (at < bytes.Length)
        {
            int run = 1;
            while (at + run < bytes.Length && bytes[at + run] == bytes[at] && run < short.MaxValue) run++;

            if (run >= 3)
            {
                w.Write((short)-run);
                w.Write(bytes[at]);
                at += run;
                continue;
            }

            // Bytes as they are, up to the next run of three.
            int start = at;
            while (at < bytes.Length && at - start < short.MaxValue
                && !(at + 2 < bytes.Length && bytes[at] == bytes[at + 1] && bytes[at] == bytes[at + 2]))
            {
                at++;
            }

            w.Write((short)(at - start));
            w.Write(bytes, start, at - start);
        }

        w.Write(short.MinValue);
        w.Flush();
        return coded.ToArray();
    }

    private static void HeightBlock(
        double[] values, bool[] holds, int width, int i0, int i1, int j0, int j1, double maxZError, BinaryWriter into)
    {
        int count = 0;
        float low = float.MaxValue, high = float.MinValue;

        for (int i = i0; i < i1; i++)
        {
            for (int j = j0; j < j1; j++)
            {
                if (holds[(i * width) + j])
                {
                    float z = (float)values[(i * width) + j];
                    low = Math.Min(low, z);
                    high = Math.Max(high, z);
                    count++;
                }
            }
        }

        if (count == 0)
        {
            into.Write((byte)2);
            return;
        }

        double step = 2 * maxZError;
        double levels = Math.Round((high - low) / step);

        if (levels == 0)
        {
            into.Write((byte)3);    // every value is the offset
            into.Write(low);
            return;
        }

        int bits = levels >= uint.MaxValue ? 32 : (int)Math.Ceiling(Math.Log2(levels + 1));

        // Raw floats when quantizing would not save anything, or the range is past what 31 bits can count.
        if (bits >= 32 || (bits * count) + 64 >= 32 * count)
        {
            into.Write((byte)0);

            for (int i = i0; i < i1; i++)
            {
                for (int j = j0; j < j1; j++)
                {
                    if (holds[(i * width) + j])
                    {
                        into.Write((float)values[(i * width) + j]);
                    }
                }
            }

            return;
        }

        uint[] quantized = new uint[count];
        uint top = (uint)levels;

        for (int i = i0, k = 0; i < i1; i++)
        {
            for (int j = j0; j < j1; j++)
            {
                if (holds[(i * width) + j])
                {
                    double q = Math.Round(((float)values[(i * width) + j] - low) / step);
                    quantized[k++] = (uint)Math.Clamp(q, 0, top);
                }
            }
        }

        into.Write((byte)1);    // bit-stuffed, a four-byte offset
        into.Write(low);
        Stuff(quantized, bits, into);
    }

    /// <summary>
    /// LERC 1's bit stuffing: a byte of the bit count, with the width of the count of values in its top two bits; the
    /// count; then the values packed from the high bit of each 32-bit word down, written little-endian, the last word
    /// shifted so that only the bytes it needs are written.
    /// </summary>
    private static void Stuff(uint[] values, int bits, BinaryWriter into)
    {
        int n = values.Length;
        int width = n < 256 ? 2 : n < 65536 ? 1 : 0;
        into.Write((byte)(bits | (width << 6)));

        switch (width)
        {
            case 2: into.Write((byte)n); break;
            case 1: into.Write((ushort)n); break;
            default: into.Write((uint)n); break;
        }

        long total = (long)n * bits;
        uint[] words = new uint[(total + 31) / 32];
        int at = 0, used = 0;

        foreach (uint v in values)
        {
            int room = 32 - used;

            if (room >= bits)
            {
                words[at] |= v << (room - bits);
                used += bits;

                if (used == 32)
                {
                    used = 0;
                    at++;
                }
            }
            else
            {
                int spill = bits - room;
                words[at] |= v >> spill;
                words[++at] |= v << (32 - spill);
                used = spill;
            }
        }

        if (words.Length == 0)
        {
            return;
        }

        int tailBytes = (int)(((total & 31) + 7) >> 3);
        int unneeded = tailBytes > 0 ? 4 - tailBytes : 0;
        words[^1] >>= 8 * unneeded;

        Span<byte> word = stackalloc byte[4];

        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, words[i]);
            into.Write(i == words.Length - 1 ? word[..(4 - unneeded)] : word);
        }
    }
}
