using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Graticula.Cartography.Printing;

/// <summary>
/// A one-page PDF holding one picture — ADR-265 §5.6: how a print becomes a PDF.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written here, and it is forty lines of PDF 1.4.</b> A catalogue, a page tree of one page, the page, the picture as an
/// image XObject and a content stream that paints it across the page — ISO 32000-1 §7.5 (file structure), §8.9 (images)
/// and §7.4.8 (<c>DCTDecode</c>, which carries a JPEG's bytes unchanged). A library for that would be a dependency for the
/// one thing in a PDF that needs none.
/// </para>
/// <para>
/// <b>A raster PDF, and that is the limit ADR-133 recorded and this keeps.</b> The page is drawn by the same rasteriser as
/// every other picture and carried as one JPEG; text in it is pixels, not selectable text. A vector PDF needs a second
/// implementation of the canvas port, which ADR-265 §6 leaves for a revisit.
/// </para>
/// </remarks>
public static class PdfPage
{
    /// <summary>Wraps a JPEG as the whole of one page.</summary>
    /// <param name="jpeg">A baseline JPEG with three colour components, as the canvas encodes one.</param>
    /// <param name="pixelWidth">The picture's width in pixels.</param>
    /// <param name="pixelHeight">The picture's height in pixels.</param>
    /// <param name="widthInches">The page's width.</param>
    /// <param name="heightInches">The page's height.</param>
    /// <param name="title">The document's title, or null.</param>
    /// <returns>The PDF.</returns>
    public static byte[] Wrap(byte[] jpeg, int pixelWidth, int pixelHeight, double widthInches, double heightInches, string? title)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        if (pixelWidth <= 0 || pixelHeight <= 0 || !(widthInches > 0) || !(heightInches > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "A page needs a positive size.");
        }

        string w = Points(widthInches);
        string h = Points(heightInches);

        using MemoryStream pdf = new();
        List<long> offsets = [];

        Write(pdf, "%PDF-1.4\n");

        // Four bytes over 127, which tell a transfer program the file is binary (ISO 32000-1 §7.5.2).
        pdf.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

        void Begin(int number)
        {
            offsets.Add(pdf.Position);
            Write(pdf, $"{number} 0 obj\n");
        }

        Begin(1);
        Write(pdf, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        Begin(2);
        Write(pdf, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        Begin(3);
        Write(pdf, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] "
            + "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        Begin(4);
        Write(pdf, string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} /ColorSpace /DeviceRGB "
            + $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"));
        pdf.Write(jpeg);
        Write(pdf, "\nendstream\nendobj\n");

        string content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
        Begin(5);
        Write(pdf, string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        Begin(6);
        Write(pdf, $"<< /Producer (Graticula) /Title {Hex(title ?? string.Empty)} >>\nendobj\n");

        long xref = pdf.Position;
        StringBuilder table = new();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");

        foreach (long offset in offsets)
        {
            table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(pdf, table.ToString());

        return pdf.ToArray();
    }

    private static string Points(double inches) =>
        Math.Round(inches * 72, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// A text string as UTF-16 with its byte-order mark, written in hex — ISO 32000-1 §7.9.2.2 — so a title in any script,
    /// and one holding a parenthesis or a backslash, needs no escaping at all.
    /// </summary>
    private static string Hex(string text)
    {
        StringBuilder hex = new("<FEFF");

        foreach (byte b in Encoding.BigEndianUnicode.GetBytes(text.Length > 200 ? text[..200] : text))
        {
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return hex.Append('>').ToString();
    }

    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
}
