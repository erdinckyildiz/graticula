using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.3 and condition 7: the text Excel writes for characters a text node cannot carry (<c>_x000a_</c> for a line
/// feed, <c>_x000D_</c> for a carriage return, ECMA-376 Part 1 §22.9.2.19) is read as the characters it names, and what
/// the writer writes reads back as what it was given. Found by the forms corpus, where 239 cells of 22 Survey123 forms
/// showed <c>_x000a_</c> in their labels. The workbooks here are built in the test, not copied from anywhere.
/// </summary>
public sealed class AFormWorkbookEscapesTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/></Types>
        """;

    private static readonly string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
        """;

    private static readonly string WorkbookRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>
        """;

    private static readonly string Workbook = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="survey" sheetId="1" r:id="rId1"/></sheets></workbook>
        """;

    /// <summary>A workbook of one sheet, with the given shared strings and sheet data.</summary>
    private static MemoryStream Book(string sharedStrings, string sheetData, string? styles = null)
    {
        string sheet = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="{Main}"><sheetData>{sheetData}</sheetData></worksheet>
            """;
        string shared = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <sst xmlns="{Main}">{sharedStrings}</sst>
            """;

        MemoryStream book = new();

        using (ZipArchive zip = new(book, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, "[Content_Types].xml", ContentTypes);
            Put(zip, "_rels/.rels", RootRels);
            Put(zip, "xl/workbook.xml", Workbook);
            Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
            Put(zip, "xl/worksheets/sheet1.xml", sheet);
            Put(zip, "xl/sharedStrings.xml", shared);

            if (styles is not null)
            {
                Put(zip, "xl/styles.xml", styles);
            }
        }

        book.Position = 0;
        return book;
    }

    private static void Put(ZipArchive zip, string path, string text)
    {
        using StreamWriter writer = new(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string FirstCell(FormModel model) => model.Sheet("survey")!.Columns[0];

    [Fact]
    public void A_shared_string_with_excels_line_feed_escape_is_read_as_a_line_feed()
    {
        using MemoryStream book = Book(
            "<si><t>Satır 1_x000a_Satır 2</t></si>",
            """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""");

        Assert.Equal("Satır 1\nSatır 2", FirstCell(FormModel.ReadWorkbook(book)));
    }

    [Fact]
    public void An_inline_string_with_a_carriage_return_escape_is_read_as_a_carriage_return()
    {
        using MemoryStream book = Book("""<si><t/></si>""",
            """<row r="1"><c r="A1" t="inlineStr"><is><t>a_x000D_b_x0009_c</t></is></c></row>""");

        Assert.Equal("a\rb\tc", FirstCell(FormModel.ReadWorkbook(book)));
    }

    [Fact]
    public void An_escape_written_as_its_underscore_form_is_read_as_the_text_it_spells()
    {
        // Excel writes an underscore that begins an escape as _x005F_, so _x005F_x0041_ is the text _x0041_, not A.
        using MemoryStream book = Book("""<si><t>_x005F_x0041_ and _x0041_</t></si>""",
            """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""");

        Assert.Equal("_x0041_ and A", FirstCell(FormModel.ReadWorkbook(book)));
    }

    [Fact]
    public void A_string_that_is_not_an_escape_is_left_alone()
    {
        using MemoryStream book = Book("""<si><t>_x00G1_ _x41_ _x0041</t></si>""",
            """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""");

        Assert.Equal("_x00G1_ _x41_ _x0041", FirstCell(FormModel.ReadWorkbook(book)));
    }

    [Theory]
    [InlineData("line one\r\nline two")]
    [InlineData("line one\nline two")]
    [InlineData("a\rb")]
    [InlineData("_x000a_ is literal in a label")]
    [InlineData("_x005F_ and _x0041_")]
    [InlineData("tab\there, and a \u0001 control")]
    [InlineData("ends with a carriage return\r")]
    [InlineData("  leading and trailing  ")]
    public void Text_with_what_a_cell_cannot_carry_comes_back_exactly_after_a_save_and_a_read(string text)
    {
        FormModel model = new([new ModelSheet("survey", ["type", "label"], [["note", text]])]);
        using MemoryStream saved = new();
        model.WriteWorkbook(saved);
        saved.Position = 0;

        FormModel read = FormModel.ReadWorkbook(saved);

        Assert.Equal(text, read.Sheet("survey")!.Rows[0][1]);
        Assert.True(model.SameAs(read));
    }

    /// <summary>A stylesheet whose cell formats are the given number formats, in order: index 0 is General.</summary>
    private static string Styles(string numFmts, params string[] xfNumFmtIds)
    {
        string formats = string.Concat(xfNumFmtIds.Select(id => "<xf numFmtId=\"" + id + "\" applyNumberFormat=\"1\"/>"));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<styleSheet xmlns=\"" + Main + "\"><numFmts>" + numFmts + "</numFmts><cellXfs><xf numFmtId=\"0\"/>"
            + formats + "</cellXfs></styleSheet>";
    }

    /// <summary>A workbook with one cell holding the serial 43831 (1 January 2020) in the given style, read back as text.</summary>
    private static string ReadSerial(string serial, int style, string numFmts, params string[] xfNumFmtIds)
    {
        using MemoryStream book = Book("""<si><t/></si>""",
            $"""<row r="1"><c r="A1" s="{style}"><v>{serial}</v></c></row>""",
            Styles(numFmts, xfNumFmtIds));

        return FirstCell(FormModel.ReadWorkbook(book));
    }

    [Fact]
    public void A_date_formatted_cell_is_read_as_the_date_it_shows_not_as_its_serial_number()
    {
        // Built-in format 14 (a short date): the form's default is the date, as Excel shows it.
        Assert.Equal("2020-01-01", ReadSerial("43831", 1, "", "14"));

        // A custom format with a date and a time: the date and the time, to the second.
        Assert.Equal("2020-01-01T12:00:00", ReadSerial("43831.5", 1, """<numFmt numFmtId="164" formatCode="yyyy\-mm\-dd\ hh:mm"/>""", "164"));

        // A time alone.
        Assert.Equal("12:00:00", ReadSerial("0.5", 1, """<numFmt numFmtId="165" formatCode="h:mm"/>""", "165"));
    }

    [Fact]
    public void A_number_is_still_a_number_when_its_format_is_not_a_date()
    {
        Assert.Equal("43831", ReadSerial("43831", 1, "", "0"));
        Assert.Equal("43831", ReadSerial("43831", 1, """<numFmt numFmtId="166" formatCode="&quot;day &quot;0"/>""", "166"));
        Assert.Equal("43831", ReadSerial("43831", 0, ""));
    }

    [Theory]
    [InlineData(1, "1900-01-01")]
    [InlineData(59, "1900-02-28")]
    [InlineData(60, "1900-02-29")]
    [InlineData(61, "1900-03-01")]
    [InlineData(43831, "2020-01-01")]
    [InlineData(0.25, "06:00:00")]
    [InlineData(43831.999999, "2020-01-02")]
    public void Excels_serial_days_are_the_dates_it_shows(double serial, string text)
    {
        Assert.Equal(text, FormWorkbook.DateText(serial));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(2958466)]
    public void A_serial_Excel_cannot_show_is_not_turned_into_a_date(double serial)
    {
        Assert.Null(FormWorkbook.DateText(serial));
    }

    [Fact]
    public void A_corrupt_zip_is_refused_in_words_and_never_throws_past_the_reader()
    {
        // The entry count in the end-of-central-directory record is changed, so the zip opens but its entries cannot be read.
        using MemoryStream book = Book("""<si><t/></si>""", """<row r="1"><c r="A1" t="inlineStr"><is><t>x</t></is></c></row>""");
        byte[] bytes = book.ToArray();
        int end = Enumerable.Range(0, bytes.Length - 3).Last(i => bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06);
        bytes[end + 10] = (byte)(bytes[end + 10] + 3);
        using MemoryStream corrupt = new(bytes);

        FormWorkbookException refused = Assert.Throws<FormWorkbookException>(() => FormWorkbook.Read(corrupt));
        Assert.Contains("not a workbook", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatting_beyond_the_column_limit_is_not_content_and_does_not_refuse_the_sheet()
    {
        // Column AMJ is column 1024, past the 512 a sheet may hold; an empty styled cell there is formatting only.
        using MemoryStream book = Book("""<si><t/></si>""",
            """<row r="1"><c r="A1" t="inlineStr"><is><t>x</t></is></c><c r="AMJ1" s="0"/></row>""");

        Assert.Equal(["x"], FormModel.ReadWorkbook(book).Sheet("survey")!.Columns);
    }

    [Fact]
    public void Excels_phantom_leap_day_is_29_february_1900_and_the_day_after_it_is_1_march()
    {
        Assert.Equal("1900-02-29", FormWorkbook.DateText(60));
        Assert.Equal("1900-02-29T12:00:00", FormWorkbook.DateText(60.5));
        Assert.Equal("1900-03-01", FormWorkbook.DateText(61));
    }
}
