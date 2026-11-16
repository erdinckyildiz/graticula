using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Graticula.Host.Forms;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.27: <c>pulldata("@javascript", "file.js", "function", …)</c> is accepted when it names its file and its
/// function as Survey123 writes them, refused at its row when it does not, and the pieces that keep the script away from
/// the page hold — the sandbox page's policy, the form page's frame source, and a script resource never served as one.
/// What a form shared with everyone does (refused at publish and at sharing) is in the endpoints, which need a database;
/// the browser's side is <c>FormFillScriptTests</c> in the console suite.
/// </summary>
public sealed class AFormRunsItsScriptsInASandboxTests
{
    private static readonly string[] Header = ["type", "name", "label", "calculation"];

    private static readonly string[] RowA = ["integer", "a", "A", ""];

    private static readonly string[] RowB = ["integer", "b", "B", ""];

    private static FormCheck WithCalculation(string expression) =>
        FormDefinition.Check(new FormModel([
            new ModelSheet("survey", Header, new List<IReadOnlyList<string>>
            {
                RowA,
                RowB,
                new[] { "calculate", "c", "", expression },
            }),
            new ModelSheet("choices", ["list_name", "name", "label"], []),
        ]));

    private static void RefusedAt(FormCheck check, string words)
    {
        Assert.False(check.Accepted);
        Assert.Contains(check.Refusals, r => r.Sheet == "survey" && r.Row == 4 && string.Equals(r.Column, "calculation", StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(words, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pulldata(\"@javascript\", \"calc.js\", \"add\", ${a}, ${b})")]
    [InlineData("pulldata('@javascript', 'calc.js', 'add')")]
    [InlineData("pulldata('@javascript', 'calc.js', '$add_2', ${a}, 'x', 3, ${b}, concat(${a}, ${b}), 'six', 'seven')")]
    [InlineData("concat('n=', pulldata('@javascript', 'calc.js', 'add', ${a}, ${b}))")]
    public void A_call_that_names_its_file_and_function_is_accepted_and_its_file_is_listed(string expression)
    {
        FormCheck check = WithCalculation(expression);

        Assert.True(check.Accepted, string.Join("; ", check.Refusals.Select(r => r.Message)));
        Assert.Equal(("survey", 4, "calculation", "calc.js"), Assert.Single(check.Definition!.Scripts));
    }

    [Fact]
    public void A_form_with_no_script_lists_none()
    {
        FormCheck check = WithCalculation("${a} + ${b}");

        Assert.True(check.Accepted);
        Assert.Empty(check.Definition!.Scripts);
    }

    [Theory]
    [InlineData("pulldata('@javascript', 'calc.js')", "names the script file and the function in quotes")]
    [InlineData("pulldata('@javascript', ${a}, 'add')", "names its script file in quotes")]
    [InlineData("pulldata('@javascript', 'calc.js', ${a})", "names its function in quotes")]
    [InlineData("pulldata('@javascript', 'scripts/calc.js', 'add')", "is not a script file a form can carry")]
    [InlineData("pulldata('@javascript', '../calc.js', 'add')", "is not a script file a form can carry")]
    [InlineData("pulldata('@javascript', 'https://example.org/calc.js', 'add')", "is not a script file a form can carry")]
    [InlineData("pulldata('@javascript', 'calc.txt', 'add')", "a script is a .js file")]
    [InlineData("pulldata('@javascript', 'my calc.js', 'add')", "no spaces")]
    [InlineData("pulldata('@javascript', 'calc.js', '1add')", "is not the name of a JavaScript function")]
    [InlineData("pulldata('@javascript', 'calc.js', 'a.b')", "is not the name of a JavaScript function")]
    [InlineData("pulldata('@javascript', 'calc.js', 'alert(1)')", "is not the name of a JavaScript function")]
    public void A_call_that_does_not_is_refused_at_its_row(string expression, string words) => RefusedAt(WithCalculation(expression), words);

    [Fact]
    public void The_refusal_list_no_longer_names_javascript_and_the_locale_properties_say_why_they_stay_refused()
    {
        Assert.False(FormCatalog.RefusedPulldataSources.ContainsKey("@javascript"));
        Assert.Contains("§5.27", FormCatalog.RefusedProperties["locale"], StringComparison.Ordinal);
        Assert.Contains("§5.27", FormCatalog.RefusedProperties["localeInfo"], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ the sandbox page

    private static string SandboxPage()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Graticula.Host", "wwwroot", "forms", SecurityHeaders.FormScriptSandboxFile));
    }

    [Fact]
    public void The_sandbox_page_admits_its_own_script_by_hash_and_nothing_else_and_the_header_and_the_page_say_the_same()
    {
        string page = SandboxPage();
        string policy = SecurityHeaders.FormScriptSandboxPolicy(page);

        // The hash is of the block as a browser reads it: CR LF is LF before hashing, so a Windows checkout agrees.
        string text = page.Replace("\r\n", "\n", StringComparison.Ordinal);
        Match block = Regex.Match(text, "<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(block.Success);
        string hash = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(block.Groups[1].Value))) + "'";

        Assert.Equal($"default-src 'none'; script-src {hash}; worker-src blob:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'", policy);

        // The page carries the same policy in a meta element (less frame-ancestors, which a meta cannot say), so a copy
        // served without the header — the console suite's stand-in server — is held to it too.
        Match meta = Regex.Match(text, "<meta http-equiv=\"Content-Security-Policy\" content=\"([^\"]+)\">");
        Assert.True(meta.Success);
        Assert.Equal(policy.Replace("; frame-ancestors 'self'", string.Empty, StringComparison.Ordinal), meta.Groups[1].Value);

        // One script block, and no script loaded from anywhere.
        Assert.True(Regex.Count(text, "<script", RegexOptions.IgnoreCase) == 1, "The sandbox page holds more than one script element.");
        Assert.DoesNotContain("src=", block.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_with_no_script_block_gets_a_policy_that_admits_no_script()
    {
        Assert.StartsWith("default-src 'none'; script-src 'none';", SecurityHeaders.FormScriptSandboxPolicy("<p>nothing</p>"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_form_page_may_frame_this_origin_and_nothing_else()
    {
        Assert.Contains("frame-src 'self';", SecurityHeaders.FormsPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", SecurityHeaders.FormsPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", SecurityHeaders.FormsPolicy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/javascript", "text/plain; charset=utf-8")]
    [InlineData("application/javascript", "text/plain; charset=utf-8")]
    [InlineData("TEXT/JAVASCRIPT; charset=utf-8", "text/plain; charset=utf-8")]
    [InlineData("application/ecmascript", "text/plain; charset=utf-8")]
    [InlineData("text/csv", "text/csv")]
    [InlineData("image/png", "image/png")]
    public void A_resource_is_never_served_as_a_script(string stored, string served) => Assert.Equal(served, PortalEndpoints.Inert(stored));

    [Fact]
    public void A_script_is_named_alone_and_ends_in_js()
    {
        Assert.Null(FormDefinition.ScriptProblem("calc.js"));
        Assert.Null(FormDefinition.ScriptProblem("Lookup-2.JS"));
        Assert.NotNull(FormDefinition.ScriptProblem(".js"));
        Assert.NotNull(FormDefinition.ScriptProblem("a\\b.js"));
        Assert.NotNull(FormDefinition.ScriptProblem("c:calc.js"));
        Assert.True(FormCatalog.ScriptFolder == "scripts/" && FormCatalog.MaximumScriptBytes == 256 * 1024);
    }
}
