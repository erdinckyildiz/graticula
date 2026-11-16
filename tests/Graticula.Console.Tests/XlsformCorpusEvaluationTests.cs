using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-287 condition 4 over a corpus of public XLSForms: every expression the server accepts is parsed and run by the
/// client's evaluator, in the same browser tab and with the same harness as <see cref="XlsformEvaluatorTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two runs, in order.</b> <c>AFormCorpusTests</c> in <c>Graticula.Host.Tests</c> reads the corpus, checks it, and writes
/// every expression it finds with the server's verdict on it to the file named by <c>GRATICULA_FORMS_CORPUS_EXPRESSIONS</c>.
/// This test reads that file. The two projects do not reference each other, for the reason the project file gives, so the
/// file is the link. With the variable unset this test says so and passes.
/// </para>
/// <para>
/// <b>What a failure means.</b> An expression the server accepts is one the server believes the client can run. If the
/// evaluator cannot parse it, the two lists have drifted, or the grammar is wrong. If it parses and throws while running,
/// either the harness lacks something the form needs (a choice list, a pulldata file), or the evaluator is wrong. The
/// second kind is listed by name, and the harness context is the same one the specification-case tests use, so a
/// failure here is read against the expression, not against the context.
/// </para>
/// </remarks>
public sealed class XlsformCorpusEvaluationTests : IClassFixture<XlsformEvaluatorTests.Page>
{
    /// <summary>The expressions file the host test writes, when it has been run.</summary>
    private const string ExpressionsVariable = "GRATICULA_FORMS_CORPUS_EXPRESSIONS";

    private readonly XlsformEvaluatorTests.Page _page;

    /// <summary>Creates the test over the shared page.</summary>
    /// <param name="page">The tab the evaluator is loaded in.</param>
    public XlsformCorpusEvaluationTests(XlsformEvaluatorTests.Page page) => _page = page;

    /// <summary>Every expression the server accepts parses in the client, and runs in it without an error.</summary>
    [Fact]
    public async Task Every_expression_the_server_accepts_parses_and_runs_in_the_client()
    {
        string? path = Environment.GetEnvironmentVariable(ExpressionsVariable);

        if (path is not { Length: > 0 })
        {
            System.Console.WriteLine($"{ExpressionsVariable} is not set, so the corpus expressions are not run (ADR-287 condition 4).");
            return;
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        List<string> parseFailures = [];
        List<string> runFailures = [];
        int accepted = 0;
        int total = 0;

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            total++;

            string expression = item.GetProperty("expression").GetString() ?? string.Empty;
            bool serverAccepts = item.GetProperty("serverAccepts").GetBoolean();
            string where = $"{item.GetProperty("form").GetString()} {item.GetProperty("sheet").GetString()} row {item.GetProperty("row").GetInt32()} {item.GetProperty("column").GetString()}";

            if (!serverAccepts)
            {
                continue;
            }

            accepted++;
            string parse = await _page.Browser.EvaluateAsync<string>(
                $"__err(function () {{ GraticulaXlsform.parse({Js(expression)}); }})") ?? "(null)";

            if (parse != "no error")
            {
                parseFailures.Add($"{where}: {expression} → {parse}");
                continue;
            }

            string run = await _page.Browser.EvaluateAsync<string>(
                $"__err(function () {{ __run({Js(expression)}); }})") ?? "(null)";

            if (run != "no error")
            {
                runFailures.Add($"{where}: {expression} → {run}");
            }
        }

        System.Console.WriteLine($"Corpus expressions: {total} found, {accepted} accepted by the server, {parseFailures.Count} did not parse, {runFailures.Count} threw when run.");
        Assert.True(parseFailures.Count == 0, "Accepted by the server but not parsed by the client:\n" + string.Join("\n", parseFailures));
        Assert.True(runFailures.Count == 0, "Accepted by the server but thrown by the client when run:\n" + string.Join("\n", runFailures));
    }

    private static string Js(string text) => JsonSerializer.Serialize(text);
}
