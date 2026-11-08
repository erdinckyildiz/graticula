using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Graticula.Host.Forms;

/// <summary>
/// Reads and writes an XLSForm workbook as sheets of text cells — ADR-287 §5.1, §5.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the import reader, and why.</b> The import reader turns a workbook's first sheet into CSV through GDAL in a
/// separate process (ADR-112). An XLSForm has three sheets that matter (<c>survey</c>, <c>choices</c>,
/// <c>settings</c>), and condition 7 asks that workbook → model → workbook → model is identical, which a reader that
/// guesses header rows and cell types cannot promise. What an XLSForm holds is text in cells, so this reads the parts of
/// SpreadsheetML that carry text — shared strings, inline strings, numbers, booleans and a formula's cached value —
/// with the zip and XML readers of the base library, and nothing else. Styles, merged cells, comments and formulas
/// themselves are not read, because no XLSForm column means anything by them.
/// </para>
/// <para>
/// <b>Every cell is text.</b> A number is kept as the text the workbook stored (<c>3</c>, <c>0.5</c>), a boolean as
/// <c>TRUE</c> or <c>FALSE</c>, and the writer writes every cell back as an inline string. So a number typed into
/// Excel comes back as text after one round trip, which XLSForm cannot tell apart: its columns are read as text by
/// every tool that reads them.
/// </para>
/// </remarks>
public static class FormWorkbook
{
    /// <summary>The most a workbook may unpack to. An XLSForm of a thousand questions is well under a megabyte.</summary>
    public const long MaximumUnpackedBytes = 32L * 1024 * 1024;

    /// <summary>The most rows one sheet may hold.</summary>
    public const int MaximumRows = 50_000;

    /// <summary>The most columns one sheet may hold.</summary>
    public const int MaximumColumns = 512;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Reads every worksheet of a workbook, in the workbook's order.</summary>
    /// <exception cref="FormWorkbookException">The bytes are not a workbook this can read, or exceed the limits.</exception>
    public static IReadOnlyList<WorkbookSheet> Read(Stream workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        ZipArchive zip;

        try
        {
            zip = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException broken)
        {
            throw new FormWorkbookException($"The file is not a workbook: {broken.Message}");
        }

        using (zip)
        {
            long declared = zip.Entries.Sum(e => e.Length);

            if (declared > MaximumUnpackedBytes)
            {
                throw new FormWorkbookException(
                    $"The workbook unpacks to {declared / 1048576} MB, and a form may unpack to at most "
                    + $"{MaximumUnpackedBytes / 1048576} MB.");
            }

            XDocument book = Load(zip, "xl/workbook.xml")
                ?? throw new FormWorkbookException("The file is a zip but not a workbook: it has no xl/workbook.xml.");
            Dictionary<string, string> targets = Relationships(zip, "xl/_rels/workbook.xml.rels");
            IReadOnlyList<string> shared = SharedStrings(zip);
            List<WorkbookSheet> sheets = [];

            foreach (XElement sheet in book.Root?.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
            {
                string name = (string?)sheet.Attribute("name") ?? string.Empty;
                string? relationship = (string?)sheet.Attribute(OfficeRelationships + "id");

                if (relationship is null || !targets.TryGetValue(relationship, out string? target))
                {
                    continue;
                }

                XDocument part = Load(zip, PartPath(target))
                    ?? throw new FormWorkbookException($"The sheet '{name}' is named by the workbook but is not in it.");
                sheets.Add(new WorkbookSheet(name, Rows(part, shared, name)));
            }

            return sheets;
        }
    }

    /// <summary>Writes sheets as a workbook, every cell an inline string.</summary>
    public static void Write(IReadOnlyList<WorkbookSheet> sheets, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(destination);

        using ZipArchive zip = new(destination, ZipArchiveMode.Create, leaveOpen: true);

        Part(zip, "[Content_Types].xml", new XDocument(
            new XElement((XNamespace)"http://schemas.openxmlformats.org/package/2006/content-types" + "Types",
                new XElement((XNamespace)"http://schemas.openxmlformats.org/package/2006/content-types" + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement((XNamespace)"http://schemas.openxmlformats.org/package/2006/content-types" + "Default",
                    new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement((XNamespace)"http://schemas.openxmlformats.org/package/2006/content-types" + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                sheets.Select((_, i) => new XElement(
                    (XNamespace)"http://schemas.openxmlformats.org/package/2006/content-types" + "Override",
                    new XAttribute("PartName", $"/xl/worksheets/sheet{i + 1}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))))));

        Part(zip, "_rels/.rels", new XDocument(new XElement(PackageRelationships + "Relationships",
            new XElement(PackageRelationships + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "xl/workbook.xml")))));

        Part(zip, "xl/workbook.xml", new XDocument(new XElement(Main + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", OfficeRelationships.NamespaceName),
            new XElement(Main + "sheets", sheets.Select((sheet, i) => new XElement(Main + "sheet",
                new XAttribute("name", sheet.Name),
                new XAttribute("sheetId", i + 1),
                new XAttribute(OfficeRelationships + "id", $"rId{i + 1}")))))));

        Part(zip, "xl/_rels/workbook.xml.rels", new XDocument(new XElement(PackageRelationships + "Relationships",
            sheets.Select((_, i) => new XElement(PackageRelationships + "Relationship",
                new XAttribute("Id", $"rId{i + 1}"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", $"worksheets/sheet{i + 1}.xml"))))));

        for (int i = 0; i < sheets.Count; i++)
        {
            Part(zip, $"xl/worksheets/sheet{i + 1}.xml", new XDocument(new XElement(Main + "worksheet",
                new XElement(Main + "sheetData", sheets[i].Rows.Select((row, r) => new XElement(Main + "row",
                    new XAttribute("r", r + 1),
                    row.Select((cell, c) => (cell, c)).Where(x => x.cell.Length > 0).Select(x => new XElement(Main + "c",
                        new XAttribute("r", Reference(x.c, r)),
                        new XAttribute("t", "inlineStr"),
                        new XElement(Main + "is", new XElement(Main + "t",
                            Preserved(x.cell), x.cell))))))))));
        }
    }

    /// <summary>The A1-style reference of a zero-based column and row.</summary>
    public static string Reference(int column, int row) => ColumnLetters(column) + (row + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The letters of a zero-based column: 0 is A, 25 is Z, 26 is AA.</summary>
    public static string ColumnLetters(int column)
    {
        StringBuilder letters = new();

        for (int n = column + 1; n > 0; n = (n - 1) / 26)
        {
            letters.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return letters.ToString();
    }

    private static XAttribute? Preserved(string text) =>
        text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
            ? new XAttribute(XNamespace.Xml + "space", "preserve")
            : null;

    private static List<IReadOnlyList<string>> Rows(XDocument part, IReadOnlyList<string> shared, string sheet)
    {
        List<IReadOnlyList<string>> rows = [];
        int next = 0;

        foreach (XElement row in part.Root?.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
        {
            int index = int.TryParse((string?)row.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
                ? r - 1
                : next;

            if (index < next || index >= MaximumRows)
            {
                throw new FormWorkbookException(index >= MaximumRows
                    ? $"The sheet '{sheet}' has more than {MaximumRows} rows."
                    : $"The sheet '{sheet}' lists row {index + 1} after row {next}.");
            }

            while (rows.Count < index)
            {
                rows.Add([]);
            }

            List<string> cells = [];
            int column = 0;

            foreach (XElement cell in row.Elements(Main + "c"))
            {
                string? reference = (string?)cell.Attribute("r");
                int at = reference is null ? column : ColumnOf(reference);

                if (at < column || at >= MaximumColumns)
                {
                    throw new FormWorkbookException(at >= MaximumColumns
                        ? $"The sheet '{sheet}' has more than {MaximumColumns} columns."
                        : $"The sheet '{sheet}' lists cell {reference} out of order.");
                }

                while (cells.Count < at)
                {
                    cells.Add(string.Empty);
                }

                cells.Add(Value(cell, shared));
                column = at + 1;
            }

            rows.Add(Trimmed(cells));
            next = index + 1;
        }

        while (rows.Count > 0 && rows[^1].Count == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static List<string> Trimmed(List<string> cells)
    {
        while (cells.Count > 0 && cells[^1].Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    private static string Value(XElement cell, IReadOnlyList<string> shared)
    {
        string type = (string?)cell.Attribute("t") ?? "n";

        if (type == "inlineStr")
        {
            return Text(cell.Element(Main + "is"));
        }

        string raw = (string?)cell.Element(Main + "v") ?? string.Empty;

        return type switch
        {
            "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < shared.Count
                => shared[i],
            "s" => throw new FormWorkbookException($"Cell {(string?)cell.Attribute("r")} names a shared string that is not there."),
            "b" => raw == "1" ? "TRUE" : "FALSE",
            _ => raw,
        };
    }

    private static string Text(XElement? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        XElement? plain = item.Element(Main + "t");

        // A rich-text string is runs, each with its own text; phonetic runs (rPh) are readings, not content.
        return plain is not null
            ? plain.Value
            : string.Concat(item.Elements(Main + "r").Select(run => (string?)run.Element(Main + "t") ?? string.Empty));
    }

    private static int ColumnOf(string reference)
    {
        int column = 0;
        int i = 0;

        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
        {
            column = (column * 26) + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        }

        if (i == 0)
        {
            throw new FormWorkbookException($"'{reference}' is not a cell reference.");
        }

        return column - 1;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        XDocument? strings = Load(zip, "xl/sharedStrings.xml");

        return strings?.Root?.Elements(Main + "si").Select(Text).ToList() ?? [];
    }

    private static Dictionary<string, string> Relationships(ZipArchive zip, string path)
    {
        XDocument? rels = Load(zip, path);

        return rels?.Root?.Elements(PackageRelationships + "Relationship")
            .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!, StringComparer.Ordinal)
            ?? [];
    }

    private static string PartPath(string target) =>
        target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

    private static XDocument? Load(ZipArchive zip, string path)
    {
        ZipArchiveEntry? entry = zip.GetEntry(path);

        if (entry is null)
        {
            return null;
        }

        try
        {
            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });

            return XDocument.Load(reader);
        }
        catch (XmlException broken)
        {
            throw new FormWorkbookException($"The workbook part {path} is not well-formed XML: {broken.Message}");
        }
        catch (InvalidDataException broken)
        {
            throw new FormWorkbookException($"The workbook part {path} could not be unpacked: {broken.Message}");
        }
    }

    private static void Part(ZipArchive zip, string path, XDocument document)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
        });
        document.Declaration = new XDeclaration("1.0", "UTF-8", "yes");
        document.Save(writer);
    }
}

/// <summary>One worksheet: its name and its rows, each row its cells as text with trailing empty cells removed.</summary>
public sealed record WorkbookSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>A workbook that cannot be read as a form, with the reason in words a publisher can act on.</summary>
public sealed class FormWorkbookException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FormWorkbookException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public FormWorkbookException() { }

    /// <summary>Creates the exception.</summary>
    public FormWorkbookException(string message, Exception inner) : base(message, inner) { }
}
