using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Graticula.Coverages;

/// <summary>
/// Writes pixel values as LERC — ADR-137, the compressed values the ArcGIS JS SDK asks an image service for when it
/// renders on the client (<c>format=lerc&amp;lercVersion=2</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lerc2, blob version 3, as Esri publishes the format</b> (github.com/Esri/lerc, Apache-2.0): a header, a
/// run-length coded bit mask of the pixels that hold a value, and the values in 8×8 blocks, each one either constant,
/// raw, or quantized to the error the client allows and bit-stuffed. Written here from the format's description and
/// checked against Esri's own decoders; no code was taken.
/// </para>
/// <para>
/// <b>One blob a band</b>, concatenated, which is how a multi-band image travels as LERC and how the decoders read it.
/// </para>
/// <para>
/// <b>Integer values are always exact</b> (an error of a half), whatever the client allows: a quantized integer would
/// be decoded as a value between two integers, which no integer band holds. Floating-point values are quantized to the
/// asked tolerance, and written exactly when it is zero.
/// </para>
/// </remarks>
public static class LercWriter
{
    private const int Version = 3;
    private const int Block = 8;
    private const int ChecksumStart = 14;

    /// <summary>Writes an image of values as LERC, one blob for each band.</summary>
    /// <param name="samples">Width × height × bands values, row by row, pixel-interleaved.</param>
    /// <param name="valid">Whether each pixel holds a value, or null when all of them do; a NaN never does.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="bands">Values a pixel.</param>
    /// <param name="kind">The type each value is written as.</param>
    /// <param name="tolerance">The largest error the client allows in a floating-point value; zero for exact.</param>
    /// <returns>The blobs.</returns>
    public static byte[] Write(
        double[] samples, bool[]? valid, int width, int height, int bands, SampleKind kind, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bands);

        if (samples.Length != width * height * bands)
        {
            throw new ArgumentException($"{samples.Length} values are not {width} × {height} × {bands}.", nameof(samples));
        }

        if (valid is not null && valid.Length != width * height)
        {
            throw new ArgumentException($"{valid.Length} flags are not {width} × {height}.", nameof(valid));
        }

        bool real = kind is SampleKind.Real32 or SampleKind.Real64;
        double maxZError = real ? Math.Max(0, double.IsFinite(tolerance) ? tolerance : 0) : 0.5;

        using MemoryStream all = new();

        for (int band = 0; band < bands; band++)
        {
            byte[] blob = Band(samples, valid, width, height, bands, band, kind, maxZError);
            all.Write(blob);
        }

        return all.ToArray();
    }

    private static byte[] Band(
        double[] samples, bool[]? valid, int width, int height, int bands, int band, SampleKind kind, double maxZError)
    {
        int count = width * height;
        bool[] has = new bool[count];
        int numValid = 0;
        double zMin = double.PositiveInfinity;
        double zMax = double.NegativeInfinity;

        for (int i = 0; i < count; i++)
        {
            double z = samples[(i * bands) + band];

            if ((valid is null || valid[i]) && !double.IsNaN(z))
            {
                has[i] = true;
                numValid++;
                zMin = Math.Min(zMin, z);
                zMax = Math.Max(zMax, z);
            }
        }

        if (numValid == 0)
        {
            zMin = zMax = 0;
        }

        using MemoryStream body = new();
        Span<byte> four = stackalloc byte[4];

        // The mask: none when every pixel or no pixel holds a value.
        if (numValid == 0 || numValid == count)
        {
            BinaryPrimitives.WriteInt32LittleEndian(four, 0);
            body.Write(four);
        }
        else
        {
            byte[] mask = RunLength(Bits(has));
            BinaryPrimitives.WriteInt32LittleEndian(four, mask.Length);
            body.Write(four);
            body.Write(mask);
        }

        if (numValid > 0 && zMin < zMax)
        {
            body.WriteByte(0); // not one raw sweep: blocks follow

            if (kind == SampleKind.Unsigned8 && maxZError == 0.5)
            {
                body.WriteByte(0); // blocks, not Huffman coding
            }

            for (int top = 0; top < height; top += Block)
            {
                for (int left = 0; left < width; left += Block)
                {
                    WriteBlock(body, samples, has, width, Math.Min(Block, height - top), Math.Min(Block, width - left),
                        top, left, bands, band, kind, maxZError);
                }
            }
        }

        const int headerLength = 6 + 4 + 4 + (6 * 4) + (3 * 8);
        byte[] blob = new byte[headerLength + body.Length];
        Span<byte> at = blob;
        "Lerc2 "u8.CopyTo(at);
        BinaryPrimitives.WriteInt32LittleEndian(at[6..], Version);
        // The checksum at 10 is written last, over everything after it.
        BinaryPrimitives.WriteInt32LittleEndian(at[14..], height);
        BinaryPrimitives.WriteInt32LittleEndian(at[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(at[22..], numValid);
        BinaryPrimitives.WriteInt32LittleEndian(at[26..], Block);
        BinaryPrimitives.WriteInt32LittleEndian(at[30..], blob.Length);
        BinaryPrimitives.WriteInt32LittleEndian(at[34..], TypeCode(kind));
        BinaryPrimitives.WriteDoubleLittleEndian(at[38..], maxZError);
        BinaryPrimitives.WriteDoubleLittleEndian(at[46..], zMin);
        BinaryPrimitives.WriteDoubleLittleEndian(at[54..], zMax);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(at[headerLength..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[10..], Fletcher32(blob.AsSpan(ChecksumStart)));

        return blob;
    }

    private static void WriteBlock(
        MemoryStream to, double[] samples, bool[] has, int width, int rows, int columns, int top, int left,
        int bands, int band, SampleKind kind, double maxZError)
    {
        List<double> values = new(Block * Block);

        for (int y = top; y < top + rows; y++)
        {
            for (int x = left; x < left + columns; x++)
            {
                int i = (y * width) + x;

                if (has[i])
                {
                    values.Add(samples[(i * bands) + band]);
                }
            }
        }

        // Bits 2 to 5 carry the block's column, which the decoder checks.
        byte check = (byte)(((left >> 3) & 15) << 2);

        if (values.Count == 0)
        {
            to.WriteByte((byte)(2 | check));
            return;
        }

        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;

        foreach (double z in values)
        {
            low = Math.Min(low, z);
            high = Math.Max(high, z);
        }

        if (low == high)
        {
            if (low == 0)
            {
                to.WriteByte((byte)(2 | check));
            }
            else
            {
                to.WriteByte((byte)(3 | check));
                WriteValue(to, low, kind);
            }

            return;
        }

        int rawLength = values.Count * Size(kind);

        if (maxZError > 0 && (high - low) / (2 * maxZError) < (1 << 30))
        {
            double step = 2 * maxZError;
            uint[] quantized = new uint[values.Count];
            uint most = 0;

            for (int i = 0; i < values.Count; i++)
            {
                quantized[i] = (uint)(((values[i] - low) / step) + 0.5);
                most = Math.Max(most, quantized[i]);
            }

            if (most == 0)
            {
                to.WriteByte((byte)(3 | check));
                WriteValue(to, low, kind);
                return;
            }

            byte[] stuffed = BitStuff(quantized, most);

            if (1 + Size(kind) + stuffed.Length < 1 + rawLength)
            {
                to.WriteByte((byte)(1 | check));
                WriteValue(to, low, kind);
                to.Write(stuffed);
                return;
            }
        }

        to.WriteByte((byte)(0 | check));

        foreach (double z in values)
        {
            WriteValue(to, z, kind);
        }
    }

    /// <summary>
    /// Unsigned integers in as few bits as the largest needs, least significant bit first, after a byte giving the bit
    /// count and how many bytes the count of values takes, and that count.
    /// </summary>
    private static byte[] BitStuff(uint[] values, uint most)
    {
        int bits = 0;

        while (bits < 32 && (most >> bits) != 0)
        {
            bits++;
        }

        int n = values.Length < 256 ? 1 : values.Length < 65536 ? 2 : 4;
        int bits67 = n == 4 ? 0 : 3 - n;
        int packed = (int)(((long)values.Length * bits + 7) / 8);
        byte[] result = new byte[1 + n + packed];
        result[0] = (byte)(bits | (bits67 << 6));

        for (int i = 0; i < n; i++)
        {
            result[1 + i] = (byte)(values.Length >> (8 * i));
        }

        long position = 0;

        foreach (uint value in values)
        {
            for (int bit = 0; bit < bits; bit++, position++)
            {
                if (((value >> bit) & 1) != 0)
                {
                    result[1 + n + (position >> 3)] |= (byte)(1 << (int)(position & 7));
                }
            }
        }

        return result;
    }

    /// <summary>One bit a pixel, the first pixel in a byte's highest bit.</summary>
    private static byte[] Bits(bool[] has)
    {
        byte[] bits = new byte[(has.Length + 7) / 8];

        for (int i = 0; i < has.Length; i++)
        {
            if (has[i])
            {
                bits[i >> 3] |= (byte)(128 >> (i & 7));
            }
        }

        return bits;
    }

    /// <summary>
    /// The mask's run-length coding: a 16-bit count, positive before that many literal bytes and negative before one
    /// byte repeated, ended by −32768.
    /// </summary>
    private static byte[] RunLength(byte[] bytes)
    {
        using MemoryStream to = new();
        Span<byte> two = stackalloc byte[2];
        int i = 0;

        while (i < bytes.Length)
        {
            int run = 1;

            while (i + run < bytes.Length && run < 32767 && bytes[i + run] == bytes[i])
            {
                run++;
            }

            if (run >= 5)
            {
                BinaryPrimitives.WriteInt16LittleEndian(two, (short)-run);
                to.Write(two);
                to.WriteByte(bytes[i]);
                i += run;
                continue;
            }

            // Literal bytes until the next run worth coding, or the most one count can say.
            int start = i;

            while (i < bytes.Length && i - start < 32767)
            {
                int ahead = 1;

                while (i + ahead < bytes.Length && ahead < 5 && bytes[i + ahead] == bytes[i])
                {
                    ahead++;
                }

                if (ahead >= 5)
                {
                    break;
                }

                i++;
            }

            BinaryPrimitives.WriteInt16LittleEndian(two, (short)(i - start));
            to.Write(two);
            to.Write(bytes, start, i - start);
        }

        BinaryPrimitives.WriteInt16LittleEndian(two, short.MinValue);
        to.Write(two);

        return to.ToArray();
    }

    /// <summary>Fletcher's 32-bit checksum, over byte pairs taken high byte first.</summary>
    private static uint Fletcher32(ReadOnlySpan<byte> bytes)
    {
        uint sum1 = 0xffff;
        uint sum2 = 0xffff;
        int words = bytes.Length / 2;
        int at = 0;

        while (words > 0)
        {
            int chunk = Math.Min(words, 359);
            words -= chunk;

            for (int i = 0; i < chunk; i++)
            {
                sum1 += (uint)(bytes[at++] << 8);
                sum1 += bytes[at++];
                sum2 += sum1;
            }

            sum1 = (sum1 & 0xffff) + (sum1 >> 16);
            sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        }

        if ((bytes.Length & 1) != 0)
        {
            sum1 += (uint)(bytes[at] << 8);
            sum2 += sum1;
        }

        sum1 = (sum1 & 0xffff) + (sum1 >> 16);
        sum2 = (sum2 & 0xffff) + (sum2 >> 16);

        return (sum2 << 16) | sum1;
    }

    private static int TypeCode(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => 1,
        SampleKind.Signed16 => 2,
        SampleKind.Unsigned16 => 3,
        SampleKind.Signed32 => 4,
        SampleKind.Real32 => 6,
        SampleKind.Real64 => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a type LERC writes."),
    };

    private static int Size(SampleKind kind) => kind switch
    {
        SampleKind.Unsigned8 => 1,
        SampleKind.Signed16 or SampleKind.Unsigned16 => 2,
        SampleKind.Signed32 or SampleKind.Real32 => 4,
        _ => 8,
    };

    private static void WriteValue(MemoryStream to, double z, SampleKind kind)
    {
        Span<byte> eight = stackalloc byte[8];

        switch (kind)
        {
            case SampleKind.Unsigned8:
                to.WriteByte((byte)z);
                return;
            case SampleKind.Signed16:
                BinaryPrimitives.WriteInt16LittleEndian(eight, (short)z);
                to.Write(eight[..2]);
                return;
            case SampleKind.Unsigned16:
                BinaryPrimitives.WriteUInt16LittleEndian(eight, (ushort)z);
                to.Write(eight[..2]);
                return;
            case SampleKind.Signed32:
                BinaryPrimitives.WriteInt32LittleEndian(eight, (int)z);
                to.Write(eight[..4]);
                return;
            case SampleKind.Real32:
                BinaryPrimitives.WriteSingleLittleEndian(eight, (float)z);
                to.Write(eight[..4]);
                return;
            default:
                BinaryPrimitives.WriteDoubleLittleEndian(eight, z);
                to.Write(eight);
                return;
        }
    }
}
