using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Graticula.Host.Forms;
using Graticula.Host.Forms.Reports;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-301: a feature report template — a Word document written here, in the test, never one of Esri's — is read, checked
/// against a form and filled per record by the server's own engine: placeholders Word split over runs, choice labels,
/// number and date formats, repeats as paragraphs and as table rows, conditionals, pictures from attachments, headers,
/// several records in one document, and every unsupported placeholder refused with its place.
/// </summary>
public sealed class AFeatureReportFillsAWordTemplateTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // ---------------------------------------------------------------------------------------------- the form

    private static readonly ReportSchema Schema = MakeSchema();

    private static ReportSchema MakeSchema()
    {
        string[] header = ["type", "name", "label", "appearance"];
        string[][] survey =
        [
            ["text", "inspector", "Inspector", ""],
            ["text", "notes", "Notes", "multiline"],
            ["select_one colours", "colour", "Colour", ""],
            ["select_multiple colours", "colours", "Colours", ""],
            ["integer", "qty", "Quantity", ""],
            ["decimal", "amount", "Amount", ""],
            ["date", "visited", "Visited", ""],
            ["dateTime", "seen", "Seen", ""],
            ["geopoint", "location", "Location", ""],
            ["image", "photo", "Photo", ""],
            ["image", "sign", "Signature", "signature"],
            ["begin repeat", "items", "Items", ""],
            ["text", "item", "Item", ""],
            ["integer", "count", "Count", ""],
            ["end repeat", "", "", ""],
        ];
        string[][] choices = [["colours", "red", "Kırmızı"], ["colours", "blue", "Mavi"], ["colours", "green", "Yeşil"]];
        FormModel model = new([
            new ModelSheet("survey", header, survey.Select(r => (IReadOnlyList<string>)r).ToList()),
            new ModelSheet("choices", ["list_name", "name", "label"], choices.Select(r => (IReadOnlyList<string>)r).ToList()),
        ]);
        FormCheck check = FormDefinition.Check(model);
        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        return new ReportSchema(check.Definition!, "inspection");
    }

    private static readonly byte[] Png400x200 = Png(400, 200);
    private static readonly byte[] Png100x50 = Png(100, 50);

    /// <summary>The first bytes of a PNG of a size: its signature and its header chunk, which is all the engine reads.</summary>
    private static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private static ReportRecord Record(long id = 7, string colour = "red", IReadOnlyList<ReportFile>? files = null, params (string Item, int Count)[] items) => new()
    {
        ObjectId = id,
        Values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["inspector"] = "Ayşe",
            ["notes"] = "first line\nsecond line",
            ["colour"] = colour,
            ["colours"] = "red,green",
            ["qty"] = 3,
            ["amount"] = 1234.5,
            ["visited"] = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc),
            ["seen"] = new DateTime(2026, 11, 17, 21, 30, 0, DateTimeKind.Utc),
            ["objectid"] = id,
        },
        Point = (28.97, 41.01),
        Files = files ?? [],
        Repeats = new Dictionary<string, IReadOnlyList<ReportRecord>>(StringComparer.OrdinalIgnoreCase)
        {
            ["items"] = [.. items.Select((x, i) => new ReportRecord
            {
                Table = "items",
                ObjectId = 100 + i,
                Values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["item"] = x.Item, ["count"] = x.Count },
            })],
        },
    };

    private static ReportEvaluator Evaluator() =>
        new(Schema, new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(3));

    // ----------------------------------------------------------------------------------- writing a .docx here

    /// <summary>A paragraph of runs, each written as its own run — so a placeholder can be split as Word splits it.</summary>
    private static string P(params string[] runs) =>
        "<w:p>" + string.Concat(runs.Select((r, i) => $"<w:r>{(i % 2 == 1 ? "<w:rPr><w:b/></w:rPr>" : string.Empty)}<w:t xml:space=\"preserve\">{SecurityElement.Escape(r)}</w:t></w:r>")) + "</w:p>";

    private static string Table(params string[][] rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid/>" + string.Concat(rows.Select(r => "<w:tr>" + string.Concat(r.Select(c => $"<w:tc>{P(c)}</w:tc>")) + "</w:tr>")) + "</w:tbl>";

    private static byte[] Docx(string body, string? header = null)
    {
        const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
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
                + (header is null ? string.Empty : "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>")
                + "</Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Ns}><w:body>{body}"
                + (header is null ? string.Empty : "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH\"/></w:sectPr>")
                + (header is null ? "<w:sectPr/>" : string.Empty) + "</w:body></w:document>");

            if (header is not null)
            {
                Add("word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rIdH\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/></Relationships>");
                Add("word/header1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr {Ns}>{header}</w:hdr>");
            }
        }

        return output.ToArray();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        using ZipArchive zip = new(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using MemoryStream copy = new();
            e.Open().CopyTo(copy);
            return copy.ToArray();
        });
    }

    private static XDocument Part(byte[] docx, string name = "word/document.xml") => XDocument.Load(new MemoryStream(Unzip(docx)[name]));

    private static string Text(XElement paragraph) =>
        string.Concat(paragraph.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "br").Select(e => e.Name == W + "br" ? "\n" : e.Value));

    /// <summary>The text of each top-level paragraph of the body, tables' cells joined by '|' and rows by '/'.</summary>
    private static List<string> Body(byte[] docx) =>
        [.. Part(docx).Root!.Element(W + "body")!.Elements().Where(e => e.Name != W + "sectPr").Select(e => e.Name == W + "tbl"
            ? string.Join(" / ", e.Elements(W + "tr").Select(r => string.Join("|", r.Elements(W + "tc").Select(c => string.Join(" ", c.Elements(W + "p").Select(Text))))))
            : Text(e))];

    private static List<string> Filled(string body, ReportRecord? record = null)
    {
        ReportTemplate template = ReportTemplate.Open(Docx(body));
        Assert.Empty(template.Check(Schema));
        return Body(template.Fill([record ?? Record()], Evaluator()));
    }

    private static ReportIssue Refused(string body, string words)
    {
        ReportTemplate template = ReportTemplate.Open(Docx(body));
        IReadOnlyList<ReportIssue> issues = template.Check(Schema);
        Assert.NotEmpty(issues);
        ReportIssue found = Assert.Single(issues, i => i.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    // -------------------------------------------------------------------------------------------------- tests

    [Fact]
    public void A_placeholder_word_split_over_runs_is_read_whole_and_keeps_the_first_runs_formatting()
    {
        byte[] filled = ReportTemplate.Open(Docx(P("Inspector: $", "{insp", "ector}", " done"))).Fill([Record()], Evaluator());
        XElement paragraph = Part(filled).Root!.Element(W + "body")!.Element(W + "p")!;

        Assert.Equal("Inspector: Ayşe done", Text(paragraph));

        // The placeholder began in the plain first run, so the value is plain; the bold runs it swallowed are empty.
        XElement valueRun = paragraph.Elements(W + "r").First(r => r.Value == "Ayşe");
        Assert.Null(valueRun.Element(W + "rPr"));
    }

    [Fact]
    public void Word_typographic_quotes_in_a_placeholder_read_as_plain_quotes()
    {
        Assert.Equal(["1,234.50"], Filled(P("${amount | format:“#,##0.00”}")));
    }

    [Fact]
    public void A_choice_is_written_by_its_label_and_getValue_gives_its_name()
    {
        Assert.Equal(["Kırmızı / red"], Filled(P("${colour} / ${colour | getValue:\"\"}")));
        Assert.Equal(["Kırmızı, Yeşil"], Filled(P("${colours}")));
        Assert.Equal(["2 Yeşil"], Filled(P("${colours | countSelected} ${colours | selectedAt:1}")));
        Assert.Equal(["☒ red ☐ blue"], Filled(P("${colours | checked:\"red\"} red ${colours | checked:\"blue\"} blue")));
        Assert.Equal(["• Kırmızı\n• Yeşil"], Filled(P("${colours | appearance:\"bullets\"}")));
    }

    [Fact]
    public void Numbers_are_formatted_rounded_and_calculated()
    {
        Assert.Equal(["1,234.50|1234.5|1234.500|1235|6"],
            Filled(P("${amount | format:\"#,##0.00\"}|${amount}|${amount | toFixed:3}|${amount | round:0}|${qty * 2}")));
    }

    [Fact]
    public void Dates_are_written_in_the_askers_offset_and_a_date_question_keeps_its_day()
    {
        // 21:30 UTC is 00:30 the next day at +03:00; a date question is a calendar day and does not move.
        Assert.Equal(["18/11/2026 00:30|2026-11-17|17 November|2026-11-17 21:30|2026"],
            Filled(P("${seen | format:\"DD/MM/YYYY HH:mm\"}|${visited}|${visited | format:\"D MMMM\"}|${seen | utcOffset:\"+00:00\"}|${$date | format:\"YYYY\"}")));
    }

    [Fact]
    public void A_multiline_answer_keeps_its_breaks_only_when_asked()
    {
        Assert.Equal(["first line second line"], Filled(P("${notes}")));
        Assert.Equal(["first line\nsecond line"], Filled(P("${notes | appearance:\"multiline\"}")));
    }

    [Fact]
    public void A_repeat_over_paragraphs_writes_its_paragraphs_once_per_row()
    {
        List<string> body = Filled(P("Items:") + P("${#items}") + P("${$feature | getValue:\"position\"}. ${item} × ${count} (", "${inspector}", ")") + P("${/}") + P("End"),
            Record(items: [("bolt", 4), ("nut", 9)]));

        Assert.Equal(["Items:", "1. bolt × 4 (Ayşe)", "2. nut × 9 (Ayşe)", "End"], body);
    }

    [Fact]
    public void A_repeat_written_in_a_table_row_repeats_the_row()
    {
        List<string> body = Filled(Table(["Item", "Count"], ["${#items}${item}", "${count}${/}"]), Record(items: [("bolt", 4), ("nut", 9), ("washer", 1)]));

        Assert.Equal(["Item|Count / bolt|4 / nut|9 / washer|1"], body);
    }

    [Fact]
    public void A_repeat_with_no_rows_leaves_its_table_header_and_no_damaged_row()
    {
        List<string> body = Filled(Table(["Item", "Count"], ["${#items}${item}", "${count}${/}"]), Record());

        Assert.Equal(["Item|Count"], body);
    }

    [Fact]
    public void A_conditional_keeps_or_drops_its_text_inline_and_over_paragraphs()
    {
        string template = P("Colour ${if colour==\"red\"}is red${/}.") + P("${if qty > 2 && colour != \"blue\"}") + P("Many") + P("${/}");

        Assert.Equal(["Colour is red.", "Many"], Filled(template, Record(colour: "red")));
        Assert.Equal(["Colour ."], Filled(template, Record(colour: "blue")));
    }

    [Fact]
    public void A_photograph_and_a_signature_are_placed_as_pictures_at_their_size()
    {
        ReportFile photo = new(1, "photo.png", "image/png", Png400x200.Length, "photo", null, Png400x200);
        ReportFile sign = new(2, "sign.png", "image/png", Png100x50.Length, "sign", null, Png100x50);
        ReportTemplate template = ReportTemplate.Open(Docx(P("${photo | size:200:0}") + P("${sign}") + P("${photo | getValue:\"name\"}")));
        Assert.Empty(template.Check(Schema));

        byte[] filled = template.Fill([Record(files: [photo, sign])], Evaluator());
        Dictionary<string, byte[]> parts = Unzip(filled);
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        List<XElement> extents = [.. Part(filled).Descendants(wp + "extent")];

        Assert.Equal(2, extents.Count);
        Assert.Equal(200 * 9525L, (long)extents[0].Attribute("cx")!);
        Assert.Equal(100 * 9525L, (long)extents[0].Attribute("cy")!);
        Assert.Equal(100 * 9525L, (long)extents[1].Attribute("cx")!);
        Assert.Equal("photo.png", Body(filled)[2]);

        Assert.Equal(Png400x200, parts["word/media/graticula-report-1.png"]);
        Assert.Equal(Png100x50, parts["word/media/graticula-report-2.png"]);
        string rels = Encoding.UTF8.GetString(parts["word/_rels/document.xml.rels"]);
        Assert.Contains("media/graticula-report-1.png", rels, StringComparison.Ordinal);
        Assert.Contains("Extension=\"png\"", Encoding.UTF8.GetString(parts["[Content_Types].xml"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_picture_of_a_question_is_written_by_a_section_over_its_files()
    {
        ReportFile one = new(1, "a.png", "image/png", 1, "photo", null, Png100x50);
        ReportFile two = new(2, "b.png", "image/png", 1, "photo", null, Png400x200);
        ReportFile other = new(3, "c.png", "image/png", 1, "sign", null, Png100x50);
        ReportTemplate template = ReportTemplate.Open(Docx(P("${#photo}") + P("${$file | getValue:\"position\"}: ${$file | getValue:\"name\"} ${$file | size:50:0}") + P("${/}")
            + P("${#$attachment}${$file | getValue:\"name\"};${/}")));
        Assert.Empty(template.Check(Schema));
        Assert.True(template.ReadsAllFiles);

        byte[] filled = template.Fill([Record(files: [one, two, other])], Evaluator());

        Assert.Equal(["1: a.png ", "2: b.png ", "a.png;b.png;c.png;"], Body(filled));
        Assert.Equal(2, Part(filled).Descendants().Count(e => e.Name.LocalName == "drawing"));
    }

    [Fact]
    public void A_location_is_written_by_its_coordinates()
    {
        Assert.Equal(["41.01, 28.97"], Filled(P("${location | getValue:\"y\"}, ${location | getValue:\"x\"}")));
    }

    [Fact]
    public void A_header_is_filled_too()
    {
        ReportTemplate template = ReportTemplate.Open(Docx(P("Body"), header: P("Inspection ${objectid} by ${inspector}")));
        Assert.Empty(template.Check(Schema));

        byte[] filled = template.Fill([Record(id: 42)], Evaluator());

        Assert.Equal("Inspection 42 by Ayşe", Text(Part(filled, "word/header1.xml").Root!.Element(W + "p")!));
    }

    [Fact]
    public void Several_records_merged_are_one_document_with_a_page_between_them()
    {
        ReportTemplate template = ReportTemplate.Open(Docx(P("Record ${objectid}: ${colour}")));
        byte[] filled = template.Fill([Record(id: 1, colour: "red"), Record(id: 2, colour: "blue")], Evaluator());
        XElement body = Part(filled).Root!.Element(W + "body")!;

        Assert.Equal(["Record 1: Kırmızı", "\n", "Record 2: Mavi"], Body(filled));
        Assert.Single(body.Descendants(W + "br"), b => (string?)b.Attribute(W + "type") == "page");
        Assert.Single(body.Elements(W + "sectPr"));
        Assert.Equal(W + "sectPr", body.Elements().Last().Name);
    }

    [Fact]
    public void A_reports_name_takes_values_and_is_cleaned_for_a_file_system()
    {
        Assert.Equal("Inspection 7 - Ayşe - 20261118", ReportTemplate.Name("Inspection ${objectid} - ${inspector} - ${seen | format:\"YYYYMMDD\"}", Record(), Evaluator()));
        Assert.Equal("a_b_c", ReportTemplate.FileName("a/b:c"));
        Assert.Throws<ReportSyntaxException>(() => ReportTemplate.Name("${#items}", Record(), Evaluator()));
    }

    // ----------------------------------------------------------------------------- refused, with the place

    [Fact]
    public void A_map_is_refused_with_its_paragraph_and_reason()
    {
        ReportIssue issue = Refused(P("Title") + P("Where: ${location | map:\"\"}"), "map is not drawn");

        Assert.Equal("document", issue.Part);
        Assert.Equal(2, issue.Paragraph);
        Assert.Equal("${location | map:\"\"}", issue.Placeholder);
    }

    [Theory]
    [InlineData("${nosuch}", "not a question of this form")]
    [InlineData("${$shape}", "$shape is not supported")]
    [InlineData("${$layers[0]}", "index")]
    [InlineData("${inspector | bogus}", "not a method a report reads")]
    [InlineData("${location}", "does not draw maps yet")]
    [InlineData("${item}", "not a question of this form")]
    [InlineData("${colour | size:100}", "is for a picture")]
    [InlineData("${photo | getValue:\"direction\"}", "EXIF")]
    [InlineData("${#$attachment | attachmentsDefExp:\"x\"}${/}", "attachmentsDefExp")]
    [InlineData("${$image | src:\"https://example.org/a.png\"}", "$image is not supported")]
    [InlineData("${if qty = 2}x${/}", "single '='")]
    [InlineData("${amount | format:\"0.0%\"}", "'%'")]
    public void Unsupported_syntax_is_refused_with_its_reason(string placeholder, string words)
    {
        Refused(P(placeholder), words);
    }

    [Fact]
    public void A_section_that_never_closes_and_a_stray_close_are_refused()
    {
        Refused(P("${#items}") + P("${item}"), "never closed");
        Refused(P("text ${/}"), "closes no section");
    }

    [Fact]
    public void A_section_over_paragraphs_with_text_before_its_opening_is_refused()
    {
        Refused(P("Before ${#items}") + P("${item}") + P("${/}"), "starts at the start of its paragraph");
    }

    [Fact]
    public void A_section_over_something_that_is_not_a_repeat_or_files_is_refused()
    {
        Refused(P("${#nosuch}") + P("x") + P("${/}"), "not a question of this form");
        Refused(P("${#inspector}") + P("x") + P("${/}"), "none of them");
    }

    [Fact]
    public void A_file_that_is_not_a_docx_is_refused()
    {
        Assert.Throws<ReportTemplateException>(() => ReportTemplate.Open(Encoding.UTF8.GetBytes("not a zip")));
    }

    [Fact]
    public void A_number_format_is_checked_at_upload_and_a_good_template_has_no_issues()
    {
        ReportTemplate template = ReportTemplate.Open(Docx(P("${inspector} ${amount | format:\"#,##0.###\"} ${qty | round:0} ${visited | format:\"\"}")));
        Assert.Empty(template.Check(Schema));
    }
}
