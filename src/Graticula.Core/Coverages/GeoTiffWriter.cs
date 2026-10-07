using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Graticula.Coverages;

/// <summary>
/// Writes pixel values as a GeoTIFF in their own type — ADR-127, the raw export ArcGIS Pro's Export Raster and the JS
/// SDK's client-side rendering ask an image service for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written here rather than through the reader's library</b>, because what is written is small and fixed: one
/// uncompressed strip, interleaved by pixel, with the three GeoTIFF tags a reader needs to place it and the GDAL no-data
/// tag. A writer of every TIFF arrangement is the library's job and none of them is wanted here.
/// </para>
/// <para>
/// <b>Uncompressed</b>, so the bytes are the values: a request is bounded by the export's size ceiling, and a client
/// asking for values is asking for exactness rather than for the smallest answer.
/// </para>
/// </remarks>
public static class GeoTiffWriter
{
    /// <summary>Writes an image of values as a GeoTIFF.</summary>
    /// <param name="samples">Width × height × bands values, row by row, pixel-interleaved.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="bands">Values a pixel.</param>
    /// <param name="kind">The type each value is written as.</param>
    /// <param name="minX">The ground X of the left edge.</param>
    /// <param name="maxY">The ground Y of the top edge.</param>
    /// <param name="pixelWidth">A pixel's width in ground units.</param>
    /// <param name="pixelHeight">A pixel's height in ground units, positive.</param>
    /// <param name="srid">The EPSG code of the ground.</param>
    /// <param name="geographic">Whether that code is a geographic system rather than a projected one.</param>
    /// <param name="noData">The value that means nothing was measured, or null.</param>
    /// <returns>The file.</returns>
    public static byte[] Write(
        double[] samples,
        int width,
        int height,
        int bands,
        SampleKind kind,
        double minX,
        double maxY,
        double pixelWidth,
        double pixelHeight,
        int srid,
        bool geographic,
        double? noData)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bands);

        if (samples.Length != width * height * bands)
        {
            throw new ArgumentException("There is not one value per band per pixel.", nameof(samples));
        }

        (int bytes, ushort format) = kind switch
        {
            SampleKind.Unsigned8 => (1, (ushort)1),
            SampleKind.Signed16 => (2, (ushort)2),
            SampleKind.Unsigned16 => (2, (ushort)1),
            SampleKind.Signed32 => (4, (ushort)2),
            SampleKind.Real32 => (4, (ushort)3),
            _ => (8, (ushort)3),
        };

        byte[] image = new byte[samples.Length * bytes];

        for (int i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            Span<byte> at = image.AsSpan(i * bytes, bytes);

            switch (kind)
            {
                case SampleKind.Unsigned8: at[0] = (byte)Math.Clamp(Math.Round(v), 0, 255); break;
                case SampleKind.Signed16: BitConverter.TryWriteBytes(at, (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)); break;
                case SampleKind.Unsigned16: BitConverter.TryWriteBytes(at, (ushort)Math.Clamp(Math.Round(v), 0, ushort.MaxValue)); break;
                case SampleKind.Signed32: BitConverter.TryWriteBytes(at, (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue)); break;
                case SampleKind.Real32: BitConverter.TryWriteBytes(at, (float)v); break;
                default: BitConverter.TryWriteBytes(at, v); break;
            }
        }

        // Extra data the directory points at, laid out after the image.
        double[] scale = [pixelWidth, pixelHeight, 0];
        double[] tie = [0, 0, 0, minX, maxY, 0];
        ushort[] keys =
        [
            1, 1, 0, 3,
            1024, 0, 1, geographic ? (ushort)2 : (ushort)1,
            1025, 0, 1, 1,
            geographic ? (ushort)2048 : (ushort)3072, 0, 1, (ushort)Math.Clamp(srid, 0, ushort.MaxValue),
        ];
        ushort[] bitsPerSample = [.. System.Linq.Enumerable.Repeat((ushort)(bytes * 8), bands)];
        ushort[] sampleFormat = [.. System.Linq.Enumerable.Repeat(format, bands)];
        byte[] nodataText = noData is { } nd
            ? Encoding.ASCII.GetBytes(nd.ToString("R", CultureInfo.InvariantCulture) + "\0")
            : [];

        // ExtraSamples: a grey picture has one colour channel, so each band after the first is an extra sample, and TIFF
        // 6.0 asks for one tag value apiece (0, unspecified). Without it a two-band answer — WCS 1.0's Band=2,3 — read
        // in GDAL as "Sum of Photometric type-related color channels and ExtraSamples doesn't match SamplesPerPixel".
        bool colour = bands == 3 && kind == SampleKind.Unsigned8;
        int extra = colour ? 0 : bands - 1;

        int imageAt = 8;
        int bitsAt = imageAt + image.Length;
        int formatAt = bitsAt + (bitsPerSample.Length * 2);
        int extraAt = formatAt + (sampleFormat.Length * 2);
        int scaleAt = Align(extraAt + (extra * 2));
        int tieAt = scaleAt + 24;
        int keysAt = tieAt + 48;
        int nodataAt = keysAt + (keys.Length * 2);
        int directoryAt = Align(nodataAt + nodataText.Length);

        using MemoryStream file = new();
        using BinaryWriter w = new(file);

        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(directoryAt);
        w.Write(image);
        foreach (ushort b in bitsPerSample) w.Write(b);
        foreach (ushort f in sampleFormat) w.Write(f);
        for (int i = 0; i < extra; i++) w.Write((ushort)0);
        Pad(w, scaleAt);
        foreach (double d in scale) w.Write(d);
        foreach (double d in tie) w.Write(d);
        foreach (ushort k in keys) w.Write(k);
        w.Write(nodataText);
        Pad(w, directoryAt);

        List<(ushort Tag, ushort Type, int Count, int Value)> tags =
        [
            (256, 4, 1, width),
            (257, 4, 1, height),
            // Two shorts fit in the entry itself, and TIFF says they must be written there.
            (258, 3, bands, bands == 1 ? bytes * 8 : bands == 2 ? (bytes * 8) | ((bytes * 8) << 16) : bitsAt),
            (259, 3, 1, 1),
            (262, 3, 1, colour ? 2 : 1),
            (273, 4, 1, imageAt),
            (277, 3, 1, bands),
            (278, 4, 1, height),
            (279, 4, 1, image.Length),
            (284, 3, 1, 1),
        ];

        if (extra > 0)
        {
            // Two shorts or fewer fit in the entry, and every value is zero.
            tags.Add((338, 3, extra, extra <= 2 ? 0 : extraAt));
        }

        tags.AddRange(
        [
            (339, 3, bands, bands == 1 ? format : bands == 2 ? format | (format << 16) : formatAt),
            (33550, 12, 3, scaleAt),
            (33922, 12, 6, tieAt),
            (34735, 3, keys.Length, keysAt),
        ]);

        if (nodataText.Length > 0)
        {
            tags.Add((42113, 2, nodataText.Length, nodataText.Length <= 4 ? Inline(nodataText) : nodataAt));
        }

        w.Write((ushort)tags.Count);

        foreach ((ushort tag, ushort type, int count, int value) in tags)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            if (type == 3 && count == 1)
            {
                w.Write((ushort)value);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(value);
            }
        }

        w.Write(0);
        w.Flush();
        return file.ToArray();
    }

    private static int Align(int at) => (at + 7) & ~7;

    private static void Pad(BinaryWriter w, int to)
    {
        while (w.BaseStream.Position < to)
        {
            w.Write((byte)0);
        }
    }

    private static int Inline(byte[] text)
    {
        byte[] four = new byte[4];
        text.CopyTo(four, 0);
        return BitConverter.ToInt32(four);
    }
}
