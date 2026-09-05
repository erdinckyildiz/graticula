using System;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Reads where the ink is in a PNG this server wrote.
/// </summary>
/// <remarks>
/// <b>One decoder, because two tests measure pictures and a second copy would drift.</b> It lived
/// in `ThumbnailFramingTests` and was lifted here the day a second test needed it — the symbology
/// preview's, which asks not *is the frame filled* but *which row is the layer on*, and answers a
/// question a byte-count cannot.
/// </remarks>
internal static class PngInk
{
    /// <summary>Where the non-transparent pixels of an RGBA PNG are.</summary>
    /// <remarks>
    /// <b>Only what this needs.</b> Eight-bit RGBA, no interlacing, no palette — which is what
    /// the thumbnail endpoint writes. Anything else throws rather than guessing, because a
    /// decoder that quietly mis-reads a format is worse than one that refuses it.
    /// </remarks>
    /// <param name="png">The image.</param>
    /// <returns>Its size, the ink's bounding box, and how many pixels carry ink.</returns>
    internal static (int Width, int Height, int MinX, int MinY, int MaxX, int MaxY, int Painted)
        Ink(byte[] png)
    {
        int width = 0;
        int height = 0;
        using MemoryStream data = new();

        int at = 8;

        while (at + 8 <= png.Length)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            string kind = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int body = at + 8;

            if (kind == "IHDR")
            {
                width = (png[body] << 24) | (png[body + 1] << 16)
                    | (png[body + 2] << 8) | png[body + 3];
                height = (png[body + 4] << 24) | (png[body + 5] << 16)
                    | (png[body + 6] << 8) | png[body + 7];

                Assert.True(png[body + 8] == 8, "This decoder reads 8 bits per channel.");
                Assert.True(png[body + 9] == 6, "This decoder reads RGBA.");
                Assert.True(png[body + 12] == 0, "This decoder does not read interlaced images.");
            }
            else if (kind == "IDAT")
            {
                data.Write(png, body, length);
            }

            at = body + length + 4;
        }

        data.Position = 0;

        using ZLibStream inflate = new(data, CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);

        byte[] bytes = raw.ToArray();

        const int Bpp = 4;
        int stride = width * Bpp;

        byte[] previous = new byte[stride];
        byte[] line = new byte[stride];

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, painted = 0;
        int read = 0;

        for (int y = 0; y < height; y++)
        {
            byte filter = bytes[read++];

            Array.Copy(bytes, read, line, 0, stride);
            read += stride;

            for (int x = 0; x < stride; x++)
            {
                int a = x >= Bpp ? line[x - Bpp] : 0;
                int b = previous[x];
                int c = x >= Bpp ? previous[x - Bpp] : 0;

                line[x] = filter switch
                {
                    1 => (byte)(line[x] + a),
                    2 => (byte)(line[x] + b),
                    3 => (byte)(line[x] + ((a + b) / 2)),
                    4 => (byte)(line[x] + Paeth(a, b, c)),
                    _ => line[x],
                };
            }

            for (int x = 0; x < width; x++)
            {
                if (line[(x * Bpp) + 3] == 0)
                {
                    continue;
                }

                painted++;
                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }

            (previous, line) = (line, previous);
        }

        return (width, height, minX, minY, maxX, maxY, painted);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
