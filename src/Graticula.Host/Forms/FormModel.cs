using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Graticula.Host.Forms;

/// <summary>
/// A form as it is stored: an XLSForm's sheets, row by row and column by column, as JSON — ADR-287 §5.1, owner
/// decision 2026-11-09.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lossless, and only that.</b> The model holds every sheet of the workbook — not only <c>survey</c>,
/// <c>choices</c> and <c>settings</c> — with its first row as <see cref="ModelSheet.Columns"/> and the rest as
/// <see cref="ModelSheet.Rows"/>, cell for cell, unknown columns and empty rows between rows included. It does not
/// interpret anything: what a column means is <c>FormDefinition</c>'s business. That separation is what makes
/// condition 7 testable: workbook → model → workbook → model must be identical, and the model has no opinion that could
/// make it otherwise.
/// </para>
/// <para>
/// <b>The JSON.</b> <c>{"format":"xlsform/1","sheets":[{"name":"survey","columns":["type","name","label"],
/// "rows":[["text","name","Your name"]]}]}</c>. A row holds its cells up to its last non-empty one, so a row may be
/// shorter or longer than <c>columns</c>.
/// </para>
/// </remarks>
public sealed record FormModel(IReadOnlyList<ModelSheet> Sheets)
{
    /// <summary>The value of <c>format</c> this model writes and reads.</summary>
    public const string Format = "xlsform/1";

    /// <summary>The sheet of this name, compared as XLSForm tools compare it: ignoring case.</summary>
    public ModelSheet? Sheet(string name) =>
        Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The model of a workbook's sheets.</summary>
    public static FormModel FromWorkbook(IReadOnlyList<WorkbookSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        return new FormModel(sheets.Select(s => new ModelSheet(
            s.Name,
            s.Rows.Count > 0 ? s.Rows[0] : [],
            s.Rows.Skip(1).ToList())).ToList());
    }

    /// <summary>Reads a workbook into a model.</summary>
    /// <exception cref="FormWorkbookException">The workbook cannot be read.</exception>
    public static FormModel ReadWorkbook(Stream workbook) => FromWorkbook(FormWorkbook.Read(workbook));

    /// <summary>The model's sheets as a workbook's: the columns as the first row, then the rows.</summary>
    public IReadOnlyList<WorkbookSheet> ToWorkbook() =>
        Sheets.Select(s => new WorkbookSheet(
            s.Name,
            s.Columns.Count == 0 && s.Rows.Count == 0 ? [] : new[] { s.Columns }.Concat(s.Rows).ToList())).ToList();

    /// <summary>Writes the model as a workbook.</summary>
    public void WriteWorkbook(Stream destination) => FormWorkbook.Write(ToWorkbook(), destination);

    /// <summary>The model as its JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["format"] = Format,
        ["sheets"] = new JsonArray(Sheets.Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.Name,
            ["columns"] = Strings(s.Columns),
            ["rows"] = new JsonArray(s.Rows.Select(r => (JsonNode)Strings(r)).ToArray()),
        }).ToArray()),
    };

    /// <summary>Reads a model from its JSON.</summary>
    /// <exception cref="FormWorkbookException">The JSON is not a form model, with what is wrong.</exception>
    public static FormModel FromJson(JsonNode? json)
    {
        if (json is not JsonObject root)
        {
            throw new FormWorkbookException("A form model is a JSON object.");
        }

        string? format = Text(root["format"]);

        if (format != Format)
        {
            throw new FormWorkbookException($"A form model's format is '{Format}', not '{format ?? "(missing)"}'.");
        }

        if (root["sheets"] is not JsonArray sheets)
        {
            throw new FormWorkbookException("A form model has 'sheets', an array.");
        }

        List<ModelSheet> read = [];

        foreach (JsonNode? node in sheets)
        {
            if (node is not JsonObject sheet || Text(sheet["name"]) is not { } name)
            {
                throw new FormWorkbookException("Each sheet of a form model is an object with a 'name'.");
            }

            IReadOnlyList<string> columns = Row(sheet["columns"], $"sheet '{name}' columns");

            if (sheet["rows"] is not JsonArray rows)
            {
                throw new FormWorkbookException($"The sheet '{name}' has 'rows', an array of arrays.");
            }

            if (rows.Count > FormWorkbook.MaximumRows)
            {
                throw new FormWorkbookException($"The sheet '{name}' has more than {FormWorkbook.MaximumRows} rows.");
            }

            read.Add(new ModelSheet(name, columns, rows.Select((r, i) => Row(r, $"sheet '{name}' row {i + 2}")).ToList()));
        }

        return new FormModel(read);
    }

    /// <summary>Whether two models hold the same sheets, columns and cells, in the same order.</summary>
    public bool SameAs(FormModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Sheets.Count == other.Sheets.Count
            && Sheets.Zip(other.Sheets).All(p =>
                p.First.Name == p.Second.Name
                && p.First.Columns.SequenceEqual(p.Second.Columns)
                && p.First.Rows.Count == p.Second.Rows.Count
                && p.First.Rows.Zip(p.Second.Rows).All(r => r.First.SequenceEqual(r.Second)));
    }

    private static JsonArray Strings(IReadOnlyList<string> cells) =>
        new(cells.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());

    private static List<string> Row(JsonNode? node, string where)
    {
        if (node is not JsonArray cells)
        {
            throw new FormWorkbookException($"The {where} is an array of strings.");
        }

        if (cells.Count > FormWorkbook.MaximumColumns)
        {
            throw new FormWorkbookException($"The {where} has more than {FormWorkbook.MaximumColumns} cells.");
        }

        List<string> row = cells.Select(c => Text(c) ?? throw new FormWorkbookException($"Every cell of the {where} is a string.")).ToList();

        // Trailing empty cells are not kept, so a model saved by any client compares equal to one read from a workbook.
        while (row.Count > 0 && row[^1].Length == 0)
        {
            row.RemoveAt(row.Count - 1);
        }

        return row;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

/// <summary>One sheet of a <see cref="FormModel"/>: its name, its header row, and the rows under it.</summary>
public sealed record ModelSheet(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>The index of a column, compared ignoring case as XLSForm tools do, or -1.</summary>
    public int ColumnIndex(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Trim(), column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A row's cell in a column, or empty.</summary>
    public static string Cell(IReadOnlyList<string> row, int column) =>
        column >= 0 && column < row.Count ? row[column] : string.Empty;
}
