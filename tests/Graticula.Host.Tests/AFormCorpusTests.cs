using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 conditions 4 and 5 over a corpus of public XLSForms: what the server reads, what it accepts unedited, what it
/// refuses and why, and the expressions it accepts, which the client's evaluator is then run over.
/// </summary>
/// <remarks>
/// <para>
/// <b>The corpus is not in the repository.</b> It is a folder of workbooks named by <c>GRATICULA_FORMS_CORPUS</c>. Many of
/// them are third-party files, and this project commits no file it does not own; their sources and licences are recorded
/// beside them in <c>SOURCES.md</c>, outside the repository too. With the variable unset this test says so and passes,
/// which is what every other environment-gated test here does.
/// </para>
/// <para>
/// <b>Three outputs, two runs.</b> This test reads and checks every workbook. It writes a JSON report to
/// <c>GRATICULA_FORMS_CORPUS_REPORT</c> when that is set, and the expressions it found, with the server's verdict on each,
/// to <c>GRATICULA_FORMS_CORPUS_EXPRESSIONS</c>. The client's evaluator is in another test project that has no reference to
/// this one, so it reads that file and runs the expressions in a browser (<c>XlsformCorpusEvaluationTests</c>). Run this
/// one first.
/// </para>
/// <para>
/// <b>Refusals name the row, the column and the reason</b> (condition 5). Every refusal this test sees must have a reason
/// and a row; a refusal with no column is one of the few that are about a whole sheet, and they are counted apart.
/// </para>
/// </remarks>
public sealed class AFormCorpusTests
{
    /// <summary>The folder of workbooks to read. Unset: the corpus is not checked.</summary>
    internal const string CorpusVariable = "GRATICULA_FORMS_CORPUS";

    /// <summary>Where the JSON report is written, when set.</summary>
    internal const string ReportVariable = "GRATICULA_FORMS_CORPUS_REPORT";

    /// <summary>Where the expressions found are written, for the browser run, when set.</summary>
    internal const string ExpressionsVariable = "GRATICULA_FORMS_CORPUS_EXPRESSIONS";

    /// <summary>The columns of an XLSForm that hold an expression, by base name.</summary>
    private static readonly string[] ExpressionColumns =
        ["relevant", "constraint", "calculation", "choice_filter", "default", "required", "read_only", "readonly", "repeat_count", "body::esri:visible"];

    /// <summary>The corpus folder, or null when the corpus is not set up.</summary>
    internal static string? CorpusFolder() =>
        Environment.GetEnvironmentVariable(CorpusVariable) is { Length: > 0 } folder ? folder : null;

    /// <summary>The workbooks in a folder, in name order.</summary>
    internal static IReadOnlyList<string> CorpusFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".xls")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_corpus_is_read_checked_and_its_refusals_are_counted()
    {
        string? folder = CorpusFolder();

        if (folder is null)
        {
            Console.WriteLine($"{CorpusVariable} is not set, so the forms corpus is not checked (ADR-287 conditions 4 and 5).");
            return;
        }

        List<Examined> forms = CorpusFiles(folder).Select(Examine).ToList();
        List<ExpressionFound> expressions = forms.Where(f => f.Model is not null).SelectMany(Expressions).ToList();

        Dictionary<string, int> functions = expressions
            .SelectMany(e => FormExpressions.Scan(e.Expression).Calls.Select(c => c.Name))
            .GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        JsonObject report = Report(forms, expressions, functions);
        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        string? reportPath = Environment.GetEnvironmentVariable(ReportVariable);
        if (reportPath is { Length: > 0 })
        {
            File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        string? expressionsPath = Environment.GetEnvironmentVariable(ExpressionsVariable);
        if (expressionsPath is { Length: > 0 })
        {
            File.WriteAllText(expressionsPath, new JsonArray(expressions.Select(e => (JsonNode)new JsonObject
            {
                ["form"] = e.Form,
                ["sheet"] = e.Sheet,
                ["row"] = e.Row,
                ["column"] = e.Column,
                ["expression"] = e.Expression,
                ["serverAccepts"] = e.ServerAccepts,
            }).ToArray()).ToJsonString());
        }

        // A crash is a defect in the reader or the checker, not a refusal: no workbook may take the checker down.
        Assert.Empty(forms.Where(f => f.Crash is not null).Select(f => $"{f.Form}: {f.Crash}"));

        // Every refusal names where it is and why (condition 5).
        Assert.All(forms.SelectMany(f => f.Refusals), r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
            Assert.True(r.Row >= 0 && r.Sheet.Length > 0);
        });
    }

    /// <summary>
    /// Condition 5's count, as a test: every workbook that reads, checks and comes in unedited is accepted, and every
    /// refusal of the rest has a row and, where it is about a column, the column. The count itself is in the report.
    /// </summary>
    [Fact]
    public void A_refusal_that_is_about_a_column_names_the_column()
    {
        string? folder = CorpusFolder();

        if (folder is null)
        {
            Console.WriteLine($"{CorpusVariable} is not set, so refusals are not checked against the corpus.");
            return;
        }

        List<FormIssue> refusals = CorpusFiles(folder).Select(Examine).SelectMany(f => f.Refusals).ToList();

        // A refusal with no column is about a whole sheet: the sheet is missing, or lacks its header columns, or holds
        // values where one row is allowed. Any other refusal must name its column.
        string[] wholeSheet = ["A form needs a sheet named", "needs a 'type' and a 'name' column", "needs a 'list_name' and a 'name' column",
            "holds one row of values"];

        Assert.All(refusals.Where(r => r.Column is null), r =>
            Assert.True(wholeSheet.Any(words => r.Message.Contains(words, StringComparison.Ordinal)),
                $"{r.Sheet} row {r.Row} refuses without a column: {r.Message}"));
    }

    private static Examined Examine(string path)
    {
        string form = Path.GetFileName(path);

        try
        {
            using FileStream file = File.OpenRead(path);
            FormModel model = FormModel.ReadWorkbook(file);
            FormCheck check = FormDefinition.Check(model);

            return new Examined(form, model, check.Accepted, check.Refusals, check.Notices, null, null);
        }
        catch (FormWorkbookException unreadable)
        {
            return new Examined(form, null, false, [], [], unreadable.Message, null);
        }
        catch (Exception crash)
        {
            return new Examined(form, null, false, [], [], null, $"{crash.GetType().Name}: {crash.Message}");
        }
    }

    private static IEnumerable<ExpressionFound> Expressions(Examined form)
    {
        if (form.Model?.Sheet("survey") is not { } survey)
        {
            yield break;
        }

        int[] columns = ExpressionColumnIndexes(survey);

        foreach (int column in columns)
        {
            for (int r = 0; r < survey.Rows.Count; r++)
            {
                string text = ModelSheet.Cell(survey.Rows[r], column).Trim();

                string header = survey.Columns[column].Trim();
                bool flag = header is "required" or "read_only" or "readonly";

                // A flag column holds yes or no, or an expression; only the expression is one. A default is an expression
                // only when it is a formula, as FormDefinition decides; a literal default is text and is not run.
                if (text.Length == 0 || (flag && text.ToLowerInvariant() is "yes" or "no" or "true" or "false" or "true()" or "false()" or "1" or "0")
                    || (header == "default" && !(text.Contains("${", StringComparison.Ordinal) || Regex.IsMatch(text, @"^\s*[A-Za-z_][A-Za-z0-9_:.\-]*\s*\("))))
                {
                    continue;
                }

                yield return new ExpressionFound(form.Form, "survey", r + 2, header, text, Accepts(form, "survey", r + 2, header));
            }
        }

        if (form.Model.Sheet("settings") is { } settings && settings.Rows.Count > 0 && settings.ColumnIndex("instance_name") is var named && named >= 0
            && ModelSheet.Cell(settings.Rows[0], named).Trim() is { Length: > 0 } instanceName)
        {
            yield return new ExpressionFound(form.Form, "settings", 2, "instance_name", instanceName,
                Accepts(form, "settings", 2, "instance_name"));
        }
    }

    /// <summary>Whether the checker refused nothing at a cell, which is how the server's verdict on one expression is read.</summary>
    private static bool Accepts(Examined form, string sheet, int row, string column) =>
        !form.Refusals.Any(i => i.Sheet == sheet && i.Row == row && string.Equals(i.Column, column, StringComparison.OrdinalIgnoreCase));

    private static int[] ExpressionColumnIndexes(ModelSheet survey)
    {
        List<int> found = [];

        for (int i = 0; i < survey.Columns.Count; i++)
        {
            string header = survey.Columns[i].Trim();

            if (ExpressionColumns.Contains(header, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(i);
            }
        }

        return found.ToArray();
    }

    private static JsonObject Report(IReadOnlyList<Examined> forms, IReadOnlyList<ExpressionFound> expressions,
        IReadOnlyDictionary<string, int> functions)
    {
        int readable = forms.Count(f => f.Model is not null);
        int accepted = forms.Count(f => f.Accepted);
        int withNotices = forms.Count(f => f.Accepted && f.Notices.Count > 0);

        List<FormIssue> refusals = forms.SelectMany(f => f.Refusals).ToList();

        JsonArray byReason = new(refusals
            .GroupBy(r => Reason(r.Message), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (JsonNode)new JsonObject
            {
                ["reason"] = g.Key,
                ["count"] = g.Count(),
                ["forms"] = g.Select(r => r.Sheet).Distinct().Count(),
                ["example"] = Describe(g.First()),
            }).ToArray());

        JsonArray byColumn = new(refusals.Where(r => r.Column is not null)
            .GroupBy(r => r.Column!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (JsonNode)new JsonObject { ["column"] = g.Key, ["count"] = g.Count() }).ToArray());

        JsonArray unreadable = new(forms.Where(f => f.Model is null).Select(f => (JsonNode)new JsonObject
        {
            ["form"] = f.Form,
            ["why"] = f.Unreadable ?? f.Crash,
        }).ToArray());

        JsonArray perForm = new(forms.Select(f => (JsonNode)new JsonObject
        {
            ["form"] = f.Form,
            ["readable"] = f.Model is not null,
            ["accepted"] = f.Accepted,
            ["refusals"] = f.Refusals.Count,
            ["notices"] = f.Notices.Count,

            // What stands between this form and coming in unedited, each once: the message with its numbers taken out
            // and its quoted names kept, so an appearance or a function can be counted by the forms it blocks.
            ["blockedBy"] = new JsonArray(f.Refusals
                .Select(r => Regex.Replace(r.Message, @"\d+", "N"))
                .Distinct(StringComparer.Ordinal)
                .Select(m => (JsonNode)m)
                .ToArray()),
        }).ToArray());

        return new JsonObject
        {
            ["workbooks"] = forms.Count,
            ["readable"] = readable,
            ["unreadable"] = unreadable,
            ["acceptedUnedited"] = accepted,
            ["acceptedWithNotices"] = withNotices,
            ["refused"] = readable - accepted,
            ["refusalsTotal"] = refusals.Count,
            ["refusalsByReason"] = byReason,
            ["refusalsByColumn"] = byColumn,
            ["expressions"] = expressions.Count,
            ["expressionsAcceptedByServer"] = expressions.Count(e => e.ServerAccepts),
            ["functionsUsed"] = new JsonObject(functions
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))),
            ["forms"] = perForm,
        };
    }

    /// <summary>A refusal's reason with its particulars replaced: <c>'renk' is refused</c> and <c>'mavi' is refused</c> are one reason.</summary>
    private static string Reason(string message) => Regex.Replace(Regex.Replace(message, "'[^']*'", "'…'"), @"\d+", "N");

    private static string Describe(FormIssue issue) =>
        $"{issue.Sheet} row {issue.Row}{(issue.Column is null ? string.Empty : $", column {issue.Column}")}: {issue.Message}";

    /// <summary>One workbook as it was read and checked.</summary>
    private sealed record Examined(string Form, FormModel? Model, bool Accepted, IReadOnlyList<FormIssue> Refusals,
        IReadOnlyList<FormIssue> Notices, string? Unreadable, string? Crash);

    /// <summary>An expression in a form, and whether the server accepts it.</summary>
    private sealed record ExpressionFound(string Form, string Sheet, int Row, string Column, string Expression, bool ServerAccepts);
}
