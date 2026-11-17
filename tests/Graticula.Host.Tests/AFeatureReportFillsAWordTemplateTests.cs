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
            ["globalid"] = GlobalOf(id),
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

    private static Guid GlobalOf(long id) => new($"00000000-0000-0000-0000-{id:D12}");

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
    public void A_map_rotated_is_refused_with_its_paragraph_and_reason()
    {
        ReportIssue issue = Refused(P("Title") + P("Where: ${location | map:\"\" | rotate:90}"), "north up");

        Assert.Equal("document", issue.Part);
        Assert.Equal(2, issue.Paragraph);
        Assert.Equal("${location | map:\"\" | rotate:90}", issue.Placeholder);
    }

    [Theory]
    [InlineData("${nosuch}", "not a question of this form")]
    [InlineData("${$layers[0]}", "summarised with | stats")]
    [InlineData("${$layers[5] | stats:\"count,objectid\"}", "names no layer")]
    [InlineData("${location | drawingInfo:\"https://example.org/arcgis/rest/services/x/FeatureServer/0\"}", "currentLayer")]
    [InlineData("${location | mapFilters:\"where=1=1\"}", "mapFilters")]
    [InlineData("${location | map:\"not-an-id\"}", "item id")]
    [InlineData("${$map | drawingInfo:\"currentLayer\"}", "draws no records")]
    [InlineData("${inspection | stats:\"median,inspector\"}", "statistic of numbers")]
    [InlineData("${inspection | stats:\"total,qty\"}", "is not a statistic")]
    [InlineData("${inspection | stats:\"sum,nosuch\"}", "keeps a value")]
    [InlineData("${inspection | where:\"qty > 1\"}", "summarised with | stats")]
    [InlineData("${#inspection | orderByFields:\"nosuch DESC\"}x${/}", "orderByFields")]
    [InlineData("${#inspection | where:\"$inputParams\"}x${/}", "$inputParams")]
    [InlineData("${inspector | bogus}", "not a method a report reads")]
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
    public void A_section_over_paragraphs_with_text_before_its_opening_is_spliced_and_a_summarys_is_refused()
    {
        // Spliced since public templates write it (ADR-301 §5.12); a summary's tags still stand alone.
        Assert.Equal(["Before ", "bolt", ""], Filled(P("Before ${#items}") + P("${item}") + P("${/}"), Record(items: [("bolt", 4)])));
        Refused(P("Title $<$summary>") + P("x") + P("$</>"), "paragraphs of their own");
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

    // ------------------------------------------------------------------------- maps and summaries (ADR-301 §5.10, §5.11)

    /// <summary>Lookups that answer from what the test gives them, and keep what was asked.</summary>
    private sealed class Answering(Func<ReportQueryRequest, ReportQueryResult>? queries = null, byte[]? map = null) : IReportLookups
    {
        public List<ReportMapRequest> Maps { get; } = [];

        public List<ReportQueryRequest> Queries { get; } = [];

        public ReportMap? Map(ReportMapRequest request)
        {
            Maps.Add(request);
            return new ReportMap(map ?? Png(request.Width, request.Height));
        }

        public ReportQueryResult? Query(ReportQueryRequest request)
        {
            Queries.Add(request);
            return queries?.Invoke(request) ?? new ReportQueryResult([], []);
        }
    }

    private static byte[] FillWith(string body, IReportLookups lookups, params ReportRecord[] records)
    {
        ReportTemplate template = ReportTemplate.Open(Docx(body));
        Assert.Empty(template.Check(Schema));
        return template.Fill(records.Length == 0 ? [Record()] : records, Evaluator(), lookups);
    }

    [Fact]
    public void A_location_is_drawn_as_a_map_at_its_size_with_the_record_marked()
    {
        Answering lookups = new();
        byte[] filled = FillWith(P("${location | mapScale:5000 | size:300:200}") + P("${location}"), lookups);
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        List<XElement> extents = [.. Part(filled).Descendants(wp + "extent")];

        Assert.Equal(2, lookups.Maps.Count);
        ReportMapRequest sized = lookups.Maps[0];
        Assert.Equal((300, 200, 5000d, (string?)null), (sized.Width, sized.Height, sized.Scale, sized.WebMap));
        Graticula.Geometries.Point marked = Assert.IsType<Graticula.Geometries.Point>(Assert.Single(sized.Shapes));
        Assert.Equal((28.97, 41.01), (marked.X, marked.Y));
        Assert.Equal([7L], sized.ObjectIds);

        // With no size, Survey123's example size (INFERRED): 400 by 300.
        Assert.Equal((400, 300), (lookups.Maps[1].Width, lookups.Maps[1].Height));
        Assert.Equal(300 * 9525L, (long)extents[0].Attribute("cx")!);
        Assert.Equal(200 * 9525L, (long)extents[0].Attribute("cy")!);
        Assert.Contains(Unzip(filled).Keys, k => k.StartsWith("word/media/graticula-report-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_web_map_an_extent_a_shape_and_the_records_own_layer_are_asked_for_as_written()
    {
        Answering lookups = new();
        _ = FillWith(
            P("${location | map:\"7E2B9BE8A9C94E45B7F87857D8D168D6\" | drawingInfo:\"currentLayer\"}")
            + P("${$shape | size:100:100}")
            + P("${$map | map:\"\" | mapExtent:139.7:35.6:139.9:35.8:4326 | size:120:80}"), lookups);

        Assert.Equal("7e2b9be8a9c94e45b7f87857d8d168d6", lookups.Maps[0].WebMap);
        Assert.True(lookups.Maps[0].CurrentLayer);
        Assert.Single(lookups.Maps[1].Shapes);
        Assert.Empty(lookups.Maps[2].Shapes);
        Assert.Equal([139.7, 35.6, 139.9, 35.8, 4326], lookups.Maps[2].Extent);
    }

    [Fact]
    public void A_record_with_no_location_draws_no_map_and_a_map_not_drawn_writes_nothing()
    {
        ReportRecord nowhere = new() { ObjectId = 3, Values = Record().Values, Point = null };
        Answering lookups = new();

        Assert.Equal(["[]"], Body(FillWith(P("[${location}]"), lookups, nowhere)));
        Assert.Empty(lookups.Maps);

        // An empty picture is what a map that could not be drawn is (the report says why in its notes).
        Assert.Equal(["[]"], Body(FillWith(P("[${location}]"), new Answering(map: []))));
    }

    [Fact]
    public void A_map_marks_the_record_where_it_is_on_a_known_extent()
    {
        // The overlay a report's map draws over the print renderer's picture: a known extent of 100 by 100 units on a
        // 200 by 200 canvas puts the point (25, 75) at pixel (50, 50).
        using Graticula.Cartography.IMapCanvas canvas = new Graticula.Render.Skia.SkiaMapCanvasFactory().Create(200, 200);
        canvas.Clear(Graticula.Cartography.Rgba.White);
        Graticula.Cartography.PixelTransform transform = new(new Graticula.Geometries.Envelope(0, 0, 100, 100), 200, 200);

        FormEndpoints.Mark(canvas, transform, [new Graticula.Geometries.Point(25, 75)]);

        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(canvas.Encode(Graticula.Cartography.MapImageFormat.Png, 90));
        Assert.Equal((200, 200), (bitmap.Width, bitmap.Height));
        SkiaSharp.SKColor at = bitmap.GetPixel(50, 50);
        Assert.Equal((FormEndpoints.ReportMarkColour.R, FormEndpoints.ReportMarkColour.G, FormEndpoints.ReportMarkColour.B), (at.Red, at.Green, at.Blue));
        Assert.Equal(SkiaSharp.SKColors.White, bitmap.GetPixel(150, 150));
    }

    [Fact]
    public void A_summary_section_is_written_once_for_all_the_records_and_reads_their_statistics()
    {
        Answering lookups = new(q => q.Statistics.Count > 0
            ? new ReportQueryResult([], [new Dictionary<string, object?> { ["total"] = 12d, ["count_objectid"] = 2d }])
            : new ReportQueryResult([], []));
        string body = P("$<$summary>") + P("Records: ${inspection | stats:\"count,objectid\"}, quantity ${inspection | stats:\"sum,qty,total\"}") + P("${#inspection}${inspector} #${objectid};${/}") + P("$</>")
            + P("Record ${objectid}");

        byte[] filled = FillWith(body, lookups, Record(id: 1), Record(id: 2));

        Assert.Equal(["Records: 2, quantity 12", "Ayşe #1;Ayşe #2;", "Record 1", "\n", "Record 2"], Body(filled));
        ReportQueryRequest asked = lookups.Queries[0];
        Assert.Equal(string.Empty, asked.Table);
        Assert.Equal([1L, 2L], asked.WithinIds);
        Assert.Equal(new ReportStatistic("count", "objectid", "count_objectid"), Assert.Single(asked.Statistics));
        Assert.True(ReportTemplate.Open(Docx(body)).HasSummary);
    }

    [Fact]
    public void A_template_of_summaries_alone_writes_no_record_and_a_map_of_all_of_them()
    {
        Answering lookups = new();
        byte[] filled = FillWith(P("$<$summary>") + P("Map: ${location | size:200:100}") + P("$</>"), lookups,
            Record(id: 1), new ReportRecord { ObjectId = 2, Values = Record(id: 2).Values, Point = (29.0, 41.1) });

        Assert.Equal(["Map: "], Body(filled));
        ReportMapRequest map = Assert.Single(lookups.Maps);
        Assert.Equal([1L, 2L], map.ObjectIds);
        Assert.Equal(2, map.Shapes.Count);
    }

    [Fact]
    public void Statistics_grouped_repeat_a_section_per_group_with_the_groups_label()
    {
        Answering lookups = new(q => new ReportQueryResult([],
        [
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["colour"] = "red", ["Total"] = 5d },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["colour"] = "blue", ["Total"] = 2.5d },
        ]));

        byte[] filled = FillWith(P("$<$summary>") + P("${#inspection | stats:\"sum,amount,Total\":\"colour\" | orderByFields:\"Total DESC\" | resultRecordCount:5}${colour}: ${Total | round:1}; ${/}") + P("$</>"),
            lookups, Record(id: 1), Record(id: 2));

        Assert.Equal(["Kırmızı: 5; Mavi: 2.5; "], Body(filled));
        ReportQueryRequest asked = Assert.Single(lookups.Queries);
        Assert.Equal(("colour", "Total DESC", 5), (asked.GroupBy, asked.OrderBy, asked.Count));
    }

    [Fact]
    public void A_repeats_query_in_a_record_is_kept_to_that_records_rows_and_a_where_reads_its_answers()
    {
        ReportRecord bolt = new() { Table = "items", ObjectId = 100, Values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["item"] = "bolt", ["count"] = 4 } };
        Answering lookups = new(q => new ReportQueryResult([bolt], []));

        byte[] filled = FillWith(P("${#items | where:\"item <> '\"+inspector+\"'\" | orderByFields:\"count DESC\" | resultRecordCount:1}${item}${/}"), lookups, Record(id: 9));

        Assert.Equal(["bolt"], Body(filled));
        ReportQueryRequest asked = Assert.Single(lookups.Queries);
        Assert.Equal("items", asked.Table);
        Assert.Equal("item <> 'Ayşe'", asked.Where);
        Assert.Null(asked.WithinIds);
        Assert.Equal([GlobalOf(9)], asked.WithinParents);
    }

    [Fact]
    public void Important_reads_the_whole_layer_and_layers_are_named_by_position_or_name()
    {
        Answering lookups = new(q => new ReportQueryResult([], [new Dictionary<string, object?> { ["n"] = 40d }]));

        Assert.Equal(["40 40"], Body(FillWith(P("${$layers[1] | where:\"1=1 !important\" | stats:\"count,item,n\"} ${$layers[\"items\"] | where:\"1=1 !important\" | stats:\"count,item,n\"}"), lookups)));
        Assert.All(lookups.Queries, q => Assert.Equal(("items", true, "1=1"), (q.Table, q.Important, q.Where)));
        Assert.All(lookups.Queries, q => Assert.Null(q.WithinParents));
    }

    [Theory]
    [InlineData("$<$summary>¦Name: ${inspector}¦$</>", "summary section is written once")]
    [InlineData("Before¦$<$summary>¦x¦$</>¦After", "start or the end")]
    [InlineData("$<$summary>¦${$feature | getValue:\"position\"}¦$</>", "$feature")]
    [InlineData("$<$summary>¦${#$attachment}x${/}¦$</>", "$attachment")]
    [InlineData("${#items}¦$<$summary>¦x¦$</>¦${/}", "within the contents")]
    [InlineData("$<$whatever>¦x¦$</>", "not a tag a report reads")]
    [InlineData("$<$summary>¦${#inspection | stats:\"sum,qty,t\"}${inspector}${/}¦$</>", "not among what this section's rows hold")]
    public void A_summary_section_is_refused_where_Survey123_does_not_allow_it(string paragraphs, string words)
    {
        Refused(string.Concat(paragraphs.Split('¦').Select(p => P(p))), words);
    }

    // ------------------------------------------------------------- what public templates write (ADR-301 §5.12)

    [Fact]
    public void A_section_whose_ends_share_their_paragraphs_with_text_is_spliced_as_Word_keeps_it()
    {
        static string Conditional(int least) => P($"Before ${{if qty > {least}}}inside") + P("middle") + P("end${/} after");

        Assert.Equal(["Before inside", "middle", "end after"], Filled(Conditional(2)));
        Assert.Equal(["Before  after"], Filled(Conditional(5)));
        // One section closing where the next opens, as checklists are written: ${/} … ${if …} in one paragraph.
        Assert.Equal(["A x", "y  B  C"], Filled(P("A ${if qty > 2}x") + P("y ${/} B ${if qty > 5}z") + P("w${/} C")));
        Assert.Equal(["Rows: [bolt", "[nut", "]"], Filled(P("Rows: ${#items}[${item}") + P("${/}]"), Record(items: [("bolt", 4), ("nut", 9)])));
    }

    [Fact]
    public void A_choice_is_tested_with_selected_and_a_files_size_is_read_in_brackets()
    {
        ReportFile photo = new(1, "photo.png", "image/png", 3 * 1024 * 1024, "photo", null, Png400x200);

        Assert.Equal(["red yes", "3 MB"], Filled(P("red ${if colours | selected:\"red\"}yes${/}${if colours | selected:\"blue\"}no${/}")
            + P("${if (photo | getValue:\"size\") > 0}${(photo | getValue:\"size\")/1024/1024 | round:2} MB${/}"), Record(files: [photo])));
        Assert.Equal([""], Filled(P("${if (photo | getValue:\"size\") > 0}${photo | getValue:\"name\"}${/}")));
        Refused(P("${if inspector | selected:\"x\"}y${/}"), "selected");

        // A repeat in a condition asks whether it has rows.
        Assert.Equal(["rows: yes"], Filled(P("rows: ${if items}yes${/}"), Record(items: [("bolt", 1)])));
        Assert.Equal(["rows: "], Filled(P("rows: ${if items}yes${/}")));
    }

    [Fact]
    public void ArcGIS_editor_tracking_names_read_the_layers_own_fields()
    {
        ReportRecord record = Record();
        Dictionary<string, object?> values = new(record.Values, StringComparer.OrdinalIgnoreCase)
        {
            ["created_date"] = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
            ["created_user"] = "ayse",
        };

        Assert.Equal(["ayse 01.11.2026"], Filled(P("${Creator} ${CreationDate | format:\"DD.MM.YYYY\"}"), new ReportRecord { ObjectId = 7, Values = values }));
    }
}
