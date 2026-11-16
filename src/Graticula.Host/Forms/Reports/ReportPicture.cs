using System;
using System.Buffers.Binary;

namespace Graticula.Host.Forms.Reports;

/// <summary>
/// A picture's kind and size in pixels, read from its first bytes — PNG, JPEG and GIF, the three a photograph, a signature
/// and an annotated picture are sent as and Word shows everywhere — and the size it is placed at (ADR-301 §5.4).
/// </summary>
internal static class ReportPicture
{
    /// <summary>The widest a picture is placed when the template does not size it: a page's text width at 96 dpi (INFERRED).</summary>
    public const double DefaultMaximumWidth = 600;

    /// <summary>English Metric Units in one pixel at 96 dpi: 914,400 per inch over 96.</summary>
    public const long EmuPerPixel = 9525;

    /// <summary>The picture's extension and size, or null for a file that is none of the three or cannot be read.</summary>
    public static (string Extension, string MediaType, int Width, int Height)? Kind(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 24)
        {
            return null;
        }

        ReadOnlySpan<byte> b = bytes;

        if (b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            int w = (int)BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
            int h = (int)BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
            return w > 0 && h > 0 ? ("png", "image/png", w, h) : null;
        }

        if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
        {
            int w = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
            return w > 0 && h > 0 ? ("gif", "image/gif", w, h) : null;
        }

        if (b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;

            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                byte marker = b[i + 1];

                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7) or 0xFF)
                {
                    i += marker == 0xFF ? 1 : 2;
                    continue;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);

                // Start of frame: baseline, progressive and the other coding processes, not the huffman or arithmetic tables.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    int h = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]);
                    int w = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]);
                    return w > 0 && h > 0 ? ("jpeg", "image/jpeg", w, h) : null;
                }

                if (length < 2)
                {
                    return null;
                }

                i += 2 + length;
            }
        }

        return null;
    }

    /// <summary>The picture's size in pixels, or null.</summary>
    public static (int Width, int Height)? Measure(byte[]? bytes) => Kind(bytes) is { } k ? (k.Width, k.Height) : null;

    /// <summary>
    /// The size a picture is placed at, from <c>size:width:height:maxWidth:maxHeight</c> as Survey123's page reads it: a set
    /// width or height (0 is no restriction on that side, the other side following the picture's proportions), then the
    /// largest width and height, and with nothing set, the picture's own size kept within <see cref="DefaultMaximumWidth"/>.
    /// </summary>
    public static (double Width, double Height) Placed(int pixelWidth, int pixelHeight, double width, double height, double maxWidth, double maxHeight)
    {
        double ratio = (double)pixelHeight / pixelWidth;
        double w;
        double h;

        if (width > 0 && height > 0)
        {
            (w, h) = (width, height);
        }
        else if (width > 0)
        {
            (w, h) = (width, width * ratio);
        }
        else if (height > 0)
        {
            (w, h) = (height / ratio, height);
        }
        else
        {
            (w, h) = (pixelWidth, pixelHeight);

            if (maxWidth <= 0 && maxHeight <= 0 && w > DefaultMaximumWidth)
            {
                (w, h) = (DefaultMaximumWidth, DefaultMaximumWidth * ratio);
            }
        }

        if (maxWidth > 0 && w > maxWidth)
        {
            (w, h) = (maxWidth, h * maxWidth / w);
        }

        if (maxHeight > 0 && h > maxHeight)
        {
            (w, h) = (w * maxHeight / h, maxHeight);
        }

        return (Math.Max(1, w), Math.Max(1, h));
    }
}
