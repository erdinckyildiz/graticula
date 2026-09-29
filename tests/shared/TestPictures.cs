using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Graticula.Testing;

/// <summary>
/// Small, real PNG files written from nothing but the format's own rules — ADR-099's test pictures.
/// </summary>
/// <remarks>
/// <b>A second implementation, on purpose</b>, as <c>PbfReader</c> is: the pictures a test sends are
/// written by this, not by the rasteriser the server decodes them with, so a test that the server
/// draws a picture cannot pass because the two share a mistake. The file is the PNG specification's
/// minimum — signature, <c>IHDR</c>, one <c>IDAT</c> of zlib-deflated rows with filter 0, <c>IEND</c> —
/// with each chunk's CRC-32 computed from the specification's polynomial.
/// </remarks>
public static class TestPictures
{
    private static readonly uint[] Table = MakeTable();

    /// <summary>A PNG of one colour.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <param name="a">Alpha.</param>
    /// <returns>The file.</returns>
    public static byte[] Png(int width, int height, byte r, byte g, byte b, byte a = 255) =>
        Png(width, height, (_, _) => (r, g, b, a));

    /// <summary>A PNG whose every pixel is what a function says.</summary>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="pixel">The colour at a column and row, top-left first.</param>
    /// <returns>The file.</returns>
    public static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        ArgumentNullException.ThrowIfNull(pixel);

        using MemoryStream raw = new();

        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);

            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = pixel(x, y);

                raw.WriteByte(r);
                raw.WriteByte(g);
                raw.WriteByte(b);
                raw.WriteByte(a);
            }
        }

        using MemoryStream deflated = new();

        using (ZLibStream zlib = new(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace

        using MemoryStream file = new();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", deflated.ToArray());
        Chunk(file, "IEND", []);

        return file.ToArray();
    }

    /// <summary>
    /// The first bytes of a baseline JPEG of the given size: start of image, a JFIF segment and a start of
    /// frame. Enough for a header reader, and deliberately not a picture anything can decode.
    /// </summary>
    /// <param name="width">The width the frame declares.</param>
    /// <param name="height">The height the frame declares.</param>
    /// <returns>The bytes.</returns>
    public static byte[] JpegHeader(int width, int height)
    {
        using MemoryStream file = new();

        file.Write([0xFF, 0xD8]);

        // APP0 "JFIF", sixteen bytes long including its length.
        file.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        // SOF0: length 17, precision 8, height, width, three components.
        file.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        file.Write([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        file.Write([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        file.Write([0xFF, 0xD9]);

        return file.ToArray();
    }

    /// <summary>The picture as a CIM <c>url</c>.</summary>
    /// <param name="png">The PNG.</param>
    /// <returns>The data URI.</returns>
    public static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        file.Write(length);

        byte[] kind = Encoding.ASCII.GetBytes(type);
        file.Write(kind);
        file.Write(data);

        uint crc = 0xFFFFFFFF;

        foreach (byte one in kind)
        {
            crc = Table[(crc ^ one) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte one in data)
        {
            crc = Table[(crc ^ one) & 0xFF] ^ (crc >> 8);
        }

        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, crc ^ 0xFFFFFFFF);
        file.Write(check);
    }

    private static uint[] MakeTable()
    {
        uint[] table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
