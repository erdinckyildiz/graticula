using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host.Forms.Reports;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-302: a filled feature report is laid out as a PDF by the engine in its own process, <c>Graticula.Report.Pdf</c>,
/// started by the server with a deadline — and a document it cannot lay out, or cannot finish in time, comes back as a
/// sentence rather than as a broken file or a request that never ends.
/// </summary>
/// <remarks>
/// The PDF needs the image's fonts (ADR-302 §5.3); CI's Host.Tests job installs the same four packages. The documents here
/// are written in the test, never one of Esri's or a Survey123 user's.
/// </remarks>
public sealed class AFeatureReportIsLaidOutAsAPdfTests
{
    private static ReportPdfConverter Engine(TimeSpan? deadline = null) =>
        new(ReportPdfConverter.ExecutableBesideThisOne(), Path.Combine(Path.GetTempPath(), "graticula-report-pdf-tests"),
            deadline ?? ReportPdfConverter.DefaultDeadline);

    private static string P(string text) =>
        $"<w:p><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r></w:p>";

    private static string Table(params string[][] rows) =>
        "<w:tbl><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/>"
        + "<w:insideH w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"5000\"/></w:tblGrid>"
        + string.Concat(rows.Select(r => "<w:tr>" + string.Concat(r.Select(c => $"<w:tc>{P(c)}</w:tc>")) + "</w:tr>")) + "</w:tbl>";

    /// <summary>A minimal Word document: a body and nothing else.</summary>
    private static byte[] Docx(string body)
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
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + $"<w:body>{body}<w:sectPr/></w:body></w:document>");
        }

        return output.ToArray();
    }

    [Fact]
    public void The_engine_is_installed_beside_the_server_that_starts_it()
    {
        // Host.Tests opts into CopyReportPdf as the host does, so this is the arrangement an image has (deploy/server.Dockerfile).
        Assert.True(Engine().Available, $"No PDF engine at {ReportPdfConverter.ExecutableBesideThisOne()}.");
    }

    [Fact]
    public async Task A_filled_report_comes_back_as_a_PDF_with_its_Turkish_letters_and_tick_boxes()
    {
        byte[] docx = Docx(P("Vana denetimi: KDK-121") + Table(["Durum", "Sızıntı ☒ / Açık ☐"], ["Not", "Dükkânın içi su dolu — ğüşıöç"]));

        byte[] pdf = await Engine().ConvertAsync(docx, CancellationToken.None);

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5), StringComparison.Ordinal);
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf, Math.Max(0, pdf.Length - 64), Math.Min(64, pdf.Length)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_engine_cannot_read_is_refused_in_a_sentence()
    {
        ReportPdfException refused = await Assert.ThrowsAsync<ReportPdfException>(
            () => Engine().ConvertAsync(Encoding.UTF8.GetBytes("not a Word document"), CancellationToken.None));

        Assert.StartsWith("The PDF engine could not lay this template out:", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_layout_past_its_deadline_is_stopped_and_said_so()
    {
        ReportPdfException late = await Assert.ThrowsAsync<ReportPdfException>(
            () => Engine(TimeSpan.FromMilliseconds(1)).ConvertAsync(Docx(P("late")), CancellationToken.None));

        Assert.Contains("took longer than", late.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_engine_PDF_is_not_offered_and_a_conversion_says_why()
    {
        ReportPdfConverter missing = new(Path.Combine(Path.GetTempPath(), "no-such-engine", "Graticula.Report.Pdf"));

        Assert.False(missing.Available);
        ReportPdfException refused = await Assert.ThrowsAsync<ReportPdfException>(() => missing.ConvertAsync(Docx(P("x")), CancellationToken.None));
        Assert.Contains("not installed", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"bytes\":1}", null)]
    [InlineData("{\"ok\":false,\"error\":\"PDF bookmark names must be unique.\"}", "The PDF engine could not lay this template out: PDF bookmark names must be unique.")]
    [InlineData("{\"ok\":false}", "The PDF engine could not lay this template out: it gave no reason.")]
    [InlineData("Unhandled exception", "The PDF engine answered something that is not JSON, so this server and the engine were built apart.")]
    public void The_engines_answer_is_read_as_success_or_as_its_own_reason(string answer, string? refusal)
    {
        if (refusal is null)
        {
            ReportPdfConverter.Refusal(answer);
            return;
        }

        Assert.Equal(refusal, Assert.Throws<ReportPdfException>(() => ReportPdfConverter.Refusal(answer)).Message);
    }
}
