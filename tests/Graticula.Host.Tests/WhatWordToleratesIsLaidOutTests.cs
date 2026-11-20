using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Forms.Reports;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-302 condition 1: the two things OfficeIMO 3.5.3 refused in public Survey123 templates and Word lays out — a footer
/// further from the page's edge than the bottom margin (<i>"PDF footer offset must not exceed the bottom margin when footer
/// content is enabled"</i>), and a bookmark name repeated, which every merged report of a template with a bookmark has
/// (<i>"PDF bookmark names must be unique"</i>). The engine's process makes them what Word makes them before it lays the
/// document out (<c>Graticula.Report.Pdf/WordTolerances.cs</c>). The documents are written here; neither is a template.
/// </summary>
public sealed class WhatWordToleratesIsLaidOutTests
{
    private static ReportPdfConverter Engine() =>
        new(ReportPdfConverter.ExecutableBesideThisOne(), Path.Combine(Path.GetTempPath(), "graticula-report-pdf-tests"),
            ReportPdfConverter.DefaultDeadline);

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    /// <summary>A Word document with a body, a section and a footer of its own.</summary>
    private static byte[] Docx(string body, string section, string footer)
    {
        using MemoryStream output = new();

        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
                + "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rIdF\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/></Relationships>");
            Add("word/footer1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:ftr xmlns:w=\"{W}\" xmlns:r=\"{R}\">{footer}</w:ftr>");
            Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\">"
                + $"<w:body>{body}{section}</w:body></w:document>");
        }

        return output.ToArray();
    }

    private static void AssertPdf(byte[] pdf) =>
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5), StringComparison.Ordinal);

    [Fact]
    public async Task A_footer_below_the_bottom_margin_is_given_room_as_Word_gives_it()
    {
        // The SWPPP template's numbers: a quarter-inch bottom margin and a half-inch footer, holding "Page n".
        byte[] docx = Docx(
            P("Inspection overview"),
            "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdF\"/>"
                + "<w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"360\" w:right=\"360\" w:bottom=\"360\" w:left=\"360\" w:header=\"864\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>",
            P("Page 1") + P(string.Empty));

        AssertPdf(await Engine().ConvertAsync(docx, CancellationToken.None));
    }

    [Fact]
    public async Task A_bookmark_repeated_by_a_merged_report_is_laid_out()
    {
        // Word's own _GoBack, once per record, as a merged report of a template carrying it has it.
        string record = "<w:p><w:bookmarkStart w:id=\"{0}\" w:name=\"_GoBack\"/><w:r><w:t>Record {0}</w:t></w:r><w:bookmarkEnd w:id=\"{0}\"/></w:p>";
        string body = string.Format(System.Globalization.CultureInfo.InvariantCulture, record, 1)
            + string.Format(System.Globalization.CultureInfo.InvariantCulture, record, 2)
            + string.Format(System.Globalization.CultureInfo.InvariantCulture, record, 3);
        byte[] docx = Docx(
            body,
            "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdF\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/>"
                + "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>",
            P("Spill report"));

        AssertPdf(await Engine().ConvertAsync(docx, CancellationToken.None));
    }
}
