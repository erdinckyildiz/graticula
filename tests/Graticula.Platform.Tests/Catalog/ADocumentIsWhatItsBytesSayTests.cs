using System;
using System.Text;
using Graticula.Platform.Catalog;
using Xunit;

namespace Graticula.Platform.Tests.Catalog;

/// <summary>
/// ADR-188 §5.3: a document is recognised by its first bytes and its extension together, and anything a browser
/// could run is refused whatever it is called — condition 2's first half.
/// </summary>
public sealed class ADocumentIsWhatItsBytesSayTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%\u00e2\u00e3\n1 0 obj\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 20, 0, 6, 0];

    [Theory]
    [InlineData("report.pdf", "pdf", "PDF")]
    [InlineData("REPORT.PDF", "pdf", "PDF")]
    public void A_pdf_is_a_pdf(string name, string kind, string arcGisType)
    {
        DocumentKind? found = Documents.Recognise(name, Pdf, out string? refusal);

        Assert.Null(refusal);
        Assert.Equal(kind, found!.Name);
        Assert.Equal(arcGisType, found.ArcGisType);
    }

    [Theory]
    [InlineData("parcels.xlsx", "Microsoft Excel")]
    [InlineData("spec.docx", "Microsoft Word")]
    [InlineData("talk.pptx", "Microsoft Powerpoint")]
    public void An_office_file_is_named_by_its_extension_once_its_bytes_say_zip(string name, string arcGisType)
    {
        Assert.Equal(arcGisType, Documents.Recognise(name, Zip, out _)!.ArcGisType);
    }

    [Fact]
    public void A_zip_that_is_not_an_office_file_is_refused()
    {
        Assert.Null(Documents.Recognise("data.zip", Zip, out string? refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_file_that_is_not_what_its_name_says_is_refused_and_told_its_real_kind()
    {
        Assert.Null(Documents.Recognise("parcels.xlsx", Pdf, out string? refusal));
        Assert.Contains(".pdf", refusal, StringComparison.Ordinal);

        Assert.Null(Documents.Recognise("photo.pdf", Png, out _));
    }

    /// <summary>
    /// A design review: an HTML page named <c>page.pdf</c> was refused with "'.pdf' is none of these" beside a list naming
    /// PDF. The name is one this server takes; what is wrong is the inside, and the sentence says so.
    /// </summary>
    [Fact]
    public void A_name_this_server_takes_on_bytes_it_does_not_is_told_the_inside_is_wrong()
    {
        Assert.Null(Documents.Recognise("plan.pdf", Encoding.UTF8.GetBytes("<html></html>"), out string? refusal));
        Assert.Contains("is named '.pdf' but is not a PDF inside", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("none of these", refusal, StringComparison.Ordinal);

        Assert.Null(Documents.Recognise("setup.exe", Encoding.UTF8.GetBytes("MZ"), out refusal));
        Assert.Contains("none of these", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("page.pdf", "<!doctype html><script>alert(1)</script>")]
    [InlineData("page.csv", "<html><script>alert(1)</script>")]
    [InlineData("page.csv", "  \r\n<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>")]
    [InlineData("drawing.svg", "<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("page.html", "<html></html>")]
    [InlineData("run.js", "alert(1)")]
    [InlineData("setup.exe", "MZ\u0090\u0000")]
    public void Anything_a_browser_could_run_is_refused_whatever_it_is_called(string name, string content)
    {
        Assert.Null(Documents.Recognise(name, Encoding.UTF8.GetBytes(content), out string? refusal));
        Assert.False(string.IsNullOrEmpty(refusal));
    }

    [Fact]
    public void A_csv_is_plain_utf8_text_and_may_start_with_a_byte_order_mark()
    {
        byte[] withMark = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("ad;nüfus\nAnkara;5.8\n")];

        Assert.Equal("csv", Documents.Recognise("iller.csv", withMark, out _)!.Name);
        Assert.Null(Documents.Recognise("binary.csv", [0x41, 0x00, 0x42], out _));
    }

    [Fact]
    public void A_head_cut_inside_a_character_is_still_text()
    {
        // "ş" is two bytes; a 512-byte head may end after the first of them.
        byte[] head = Encoding.UTF8.GetBytes("il,ad\n1,Kırşehir");
        Assert.NotNull(Documents.Recognise("iller.csv", head.AsSpan(0, head.Length - 1), out _));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\report.pdf", "report.pdf")]
    [InlineData("../../etc/report.pdf", "report.pdf")]
    [InlineData("re\"po\nrt.pdf", "report.pdf")]
    [InlineData("", "document.pdf")]
    public void A_file_name_keeps_no_path_and_nothing_that_could_break_a_header(string uploaded, string kept)
    {
        Assert.Equal(kept, Documents.CleanFileName(uploaded, Documents.Named("pdf")!));
    }
}
