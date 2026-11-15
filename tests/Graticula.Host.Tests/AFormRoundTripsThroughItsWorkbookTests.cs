using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.1 and condition 7: a form is stored as a lossless JSON model of its XLSForm, and workbook → model →
/// workbook → model is identical. The workbook read here was written by openpyxl (Forms/make-fixtures.py), not by the
/// writer under test.
/// </summary>
public sealed class AFormRoundTripsThroughItsWorkbookTests
{
    private static FormModel Inspection()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Forms", "inspection.xlsx"));
        return FormModel.ReadWorkbook(file);
    }

    private static FormModel ThroughWorkbook(FormModel model)
    {
        using MemoryStream written = new();
        model.WriteWorkbook(written);
        written.Position = 0;
        return FormModel.ReadWorkbook(written);
    }

    [Fact]
    public void Every_sheet_is_kept_in_order_including_one_no_form_reads()
    {
        FormModel model = Inspection();

        Assert.Equal(["survey", "choices", "settings", "notlar"], model.Sheets.Select(s => s.Name));
        Assert.Equal("Bu sayfa formun parçası değil ama korunur.", model.Sheet("notlar")!.Columns[0]);
    }

    [Fact]
    public void Cells_are_read_as_the_text_a_form_means()
    {
        ModelSheet survey = Inspection().Sheet("survey")!;

        Assert.Equal("kendi_sütunumuz", survey.Columns[10]);
        Assert.Equal(["start", "start"], survey.Rows[0]);
        Assert.Equal("120", survey.Rows[2][9]);
        Assert.Equal("Kat sayısı", survey.Rows[3][2]);
        Assert.Equal("TRUE", survey.Rows[4][4]);
        Assert.Equal("0.5", survey.Rows[14][9]);
        Assert.Equal("  baştaki boşluk korunur", survey.Rows[14][2]);
        Assert.Equal("korunmalı", survey.Rows[10][10]);
        Assert.Equal("başlıksız", survey.Rows[2][13]);
        Assert.Equal(string.Empty, survey.Rows[2][12]);
    }

    [Fact]
    public void An_empty_row_between_rows_is_kept_and_trailing_empty_cells_are_not()
    {
        ModelSheet survey = Inspection().Sheet("survey")!;

        Assert.Empty(survey.Rows[6]);
        Assert.Equal("begin repeat", survey.Rows[7][0]);
        Assert.All(survey.Rows, row => Assert.True(row.Count == 0 || row[^1].Length > 0));
    }

    [Fact]
    public void Workbook_to_model_to_workbook_to_model_is_identical()
    {
        FormModel first = Inspection();
        FormModel second = ThroughWorkbook(first);

        Assert.True(first.SameAs(second));
        Assert.True(second.SameAs(ThroughWorkbook(second)));
    }

    /// <summary>
    /// Condition 7 over the corpus (ADR-287 §9): every workbook in <c>GRATICULA_FORMS_CORPUS</c> that reads goes workbook →
    /// model → workbook → model, and the two models are identical; the model also survives its own JSON. With the
    /// variable unset this says so and passes.
    /// </summary>
    [Fact]
    public void Every_corpus_form_round_trips_through_its_workbook_and_its_json()
    {
        string? folder = AFormCorpusTests.CorpusFolder();

        if (folder is null)
        {
            Console.WriteLine($"{AFormCorpusTests.CorpusVariable} is not set, so the corpus is not round-tripped (ADR-287 condition 7).");
            return;
        }

        List<string> differences = [];
        int read = 0;

        foreach (string path in AFormCorpusTests.CorpusFiles(folder))
        {
            FormModel first;

            try
            {
                using FileStream file = File.OpenRead(path);
                first = FormModel.ReadWorkbook(file);
            }
            catch (FormWorkbookException)
            {
                continue; // not a workbook this reads: counted by AFormCorpusTests, not a round trip
            }

            read++;
            FormModel second = ThroughWorkbook(first);
            FormModel third = ThroughWorkbook(second);
            FormModel fromJson = FormModel.FromJson(JsonNode.Parse(first.ToJson().ToJsonString()));

            if (!first.SameAs(second))
            {
                differences.Add($"{Path.GetFileName(path)}: workbook → model → workbook differs: {FirstDifference(first, second)}");
            }
            else if (!second.SameAs(third))
            {
                differences.Add($"{Path.GetFileName(path)}: a second trip differs: {FirstDifference(second, third)}");
            }

            if (!first.SameAs(fromJson))
            {
                differences.Add($"{Path.GetFileName(path)}: the model's JSON differs: {FirstDifference(first, fromJson)}");
            }
        }

        Console.WriteLine($"Round-tripped {read} corpus workbooks.");
        Assert.True(read > 0, "No workbook in the corpus could be read, so nothing was round-tripped.");
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    /// <summary>Where two models first differ, in words a reader of the defect can use.</summary>
    private static string FirstDifference(FormModel a, FormModel b)
    {
        if (a.Sheets.Count != b.Sheets.Count)
        {
            return $"{a.Sheets.Count} sheets against {b.Sheets.Count}";
        }

        for (int s = 0; s < a.Sheets.Count; s++)
        {
            ModelSheet x = a.Sheets[s];
            ModelSheet y = b.Sheets[s];

            if (x.Name != y.Name)
            {
                return $"sheet {s + 1} is '{x.Name}' against '{y.Name}'";
            }

            if (!x.Columns.SequenceEqual(y.Columns))
            {
                return $"sheet '{x.Name}' has different columns";
            }

            for (int r = 0; r < Math.Max(x.Rows.Count, y.Rows.Count); r++)
            {
                IReadOnlyList<string> p = r < x.Rows.Count ? x.Rows[r] : [];
                IReadOnlyList<string> q = r < y.Rows.Count ? y.Rows[r] : [];

                if (!p.SequenceEqual(q))
                {
                    int c = 0;
                    while (c < Math.Max(p.Count, q.Count) && ModelSheet.Cell(p, c) == ModelSheet.Cell(q, c))
                    {
                        c++;
                    }

                    return $"sheet '{x.Name}' row {r + 2}, column {FormWorkbook.ColumnLetters(c)}: {Quote(ModelSheet.Cell(p, c))} against {Quote(ModelSheet.Cell(q, c))}";
                }
            }
        }

        return "the models differ in a way this report does not locate";
    }

    private static string Quote(string text) => "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    [Fact]
    public void Model_to_json_to_model_is_identical()
    {
        FormModel first = Inspection();
        string json = first.ToJson().ToJsonString();
        FormModel second = FormModel.FromJson(JsonNode.Parse(json));

        Assert.True(first.SameAs(second));
        Assert.Equal(json, second.ToJson().ToJsonString());
    }

    [Fact]
    public void A_model_saved_with_trailing_empty_cells_compares_equal_to_one_without()
    {
        FormModel padded = FormModel.FromJson(JsonNode.Parse(
            """{"format":"xlsform/1","sheets":[{"name":"survey","columns":["type","name",""],"rows":[["text","a","",""]]}]}"""));
        FormModel plain = FormModel.FromJson(JsonNode.Parse(
            """{"format":"xlsform/1","sheets":[{"name":"survey","columns":["type","name"],"rows":[["text","a"]]}]}"""));

        Assert.True(padded.SameAs(plain));
    }

    [Theory]
    [InlineData("""{"sheets":[]}""", "format")]
    [InlineData("""{"format":"xlsform/1"}""", "'sheets'")]
    [InlineData("""{"format":"xlsform/1","sheets":[{"columns":[],"rows":[]}]}""", "'name'")]
    [InlineData("""{"format":"xlsform/1","sheets":[{"name":"survey","columns":[1],"rows":[]}]}""", "string")]
    public void A_model_that_is_not_one_is_refused_with_what_is_wrong(string json, string named)
    {
        FormWorkbookException refused = Assert.Throws<FormWorkbookException>(() => FormModel.FromJson(JsonNode.Parse(json)));

        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_workbook_is_refused_in_words()
    {
        using MemoryStream notZip = new(Encoding.UTF8.GetBytes("type,name\ntext,a\n"));
        using MemoryStream zipNoBook = new();

        using (ZipArchive zip = new(zipNoBook, ZipArchiveMode.Create, leaveOpen: true))
        {
            using StreamWriter w = new(zip.CreateEntry("hello.txt").Open());
            w.Write("hi");
        }

        zipNoBook.Position = 0;

        Assert.Contains("not a workbook", Assert.Throws<FormWorkbookException>(() => FormWorkbook.Read(notZip)).Message, StringComparison.Ordinal);
        Assert.Contains("xl/workbook.xml", Assert.Throws<FormWorkbookException>(() => FormWorkbook.Read(zipNoBook)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_with_a_document_type_is_refused_rather_than_expanded()
    {
        FormModel model = new([new ModelSheet("survey", ["type"], [])]);
        using MemoryStream book = new();
        model.WriteWorkbook(book);

        using MemoryStream tampered = new();
        book.Position = 0;

        using (ZipArchive source = new(book, ZipArchiveMode.Read, leaveOpen: true))
        using (ZipArchive target = new(tampered, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream from = entry.Open();
                using Stream to = target.CreateEntry(entry.FullName).Open();

                if (entry.FullName == "xl/worksheets/sheet1.xml")
                {
                    byte[] bomb = Encoding.UTF8.GetBytes(
                        """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaaaaaaaa">]><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>""");
                    to.Write(bomb);
                }
                else
                {
                    from.CopyTo(to);
                }
            }
        }

        tampered.Position = 0;

        Assert.Contains("not well-formed", Assert.Throws<FormWorkbookException>(() => FormWorkbook.Read(tampered)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Columns_are_lettered_as_spreadsheets_letter_them(int column, string letters)
    {
        Assert.Equal(letters, FormWorkbook.ColumnLetters(column));
    }

    [Fact]
    public void A_workbook_written_here_opens_as_a_spreadsheet_with_its_sheets_named()
    {
        FormModel model = new([
            new ModelSheet("survey", ["type", "name"], [["text", "ad"], [], ["note", " boşluk "]]),
            new ModelSheet("choices", [], []),
        ]);
        using MemoryStream book = new();
        model.WriteWorkbook(book);
        book.Position = 0;

        using ZipArchive zip = new(book, ZipArchiveMode.Read);
        string workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();
        string sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();

        Assert.Contains("name=\"survey\"", workbook, StringComparison.Ordinal);
        Assert.Contains("name=\"choices\"", workbook, StringComparison.Ordinal);
        Assert.Contains("xml:space=\"preserve\"", sheet, StringComparison.Ordinal);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
    }

    [Fact]
    public void Rows_out_of_order_are_refused()
    {
        FormModel model = new([new ModelSheet("survey", ["type"], [["text"]])]);
        using MemoryStream book = new();
        model.WriteWorkbook(book);
        using MemoryStream swapped = Rewrite(book, "xl/worksheets/sheet1.xml",
            """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="3"/><row r="2"/></sheetData></worksheet>""");

        Assert.Contains("row 2 after row 3", Assert.Throws<FormWorkbookException>(() => FormWorkbook.Read(swapped)).Message, StringComparison.Ordinal);
    }

    private static MemoryStream Rewrite(MemoryStream book, string part, string xml)
    {
        MemoryStream result = new();
        book.Position = 0;

        using (ZipArchive source = new(book, ZipArchiveMode.Read, leaveOpen: true))
        using (ZipArchive target = new(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream from = entry.Open();
                using Stream to = target.CreateEntry(entry.FullName).Open();

                if (entry.FullName == part)
                {
                    to.Write(Encoding.UTF8.GetBytes(xml));
                }
                else
                {
                    from.CopyTo(to);
                }
            }
        }

        result.Position = 0;
        return result;
    }
}
