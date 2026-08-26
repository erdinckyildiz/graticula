using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// That this binary says what it can do, and says all of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-168](../../docs/architecture-debt.md).</b> `Program.Main` had four behaviours —
/// `keygen`, `migrate`, `tools admincreator` and serving — and advertised none of them:
/// no `--help`, no usage text, and no mention in `README.md`. The one that matters is the
/// recovery command, because a store whose last administrator is gone is recovered by
/// `tools admincreator` and by nothing else ([Q-137](../../docs/open-questions.md)). A
/// recovery path nobody can find is a recovery path that does not exist.
/// </para>
/// <para>
/// <b>The second test is the one that keeps this true.</b> Asserting that `--help` prints
/// something is worth little; a fifth command added next month would still go unannounced.
/// So the command names are read out of `Program.cs` itself — the same shape
/// `DeadColumnsStayDeadTests` uses — and every one of them has to appear in the text. It
/// fails on the day the command is added rather than on the night somebody needs it.
/// </para>
/// </remarks>
public sealed class UsageTests
{
    /// <summary>Where the entry point's argument matches are written.</summary>
    private static string ProgramSource
    {
        get
        {
            // <b>Found by walking up rather than by a relative path from the test binary.</b>
            // `bin/Debug/net9.0` is three deep today and is a build detail, not a fact this
            // test should encode.
            DirectoryInfo? here = new(AppContext.BaseDirectory);

            while (here is not null && !Directory.Exists(Path.Combine(here.FullName, "src")))
            {
                here = here.Parent;
            }

            Assert.True(here is not null, "The repository root is not above the test binary.");

            string path = Path.Combine(here!.FullName, "src", "Graticula.Host", "Program.cs");

            Assert.True(File.Exists(path), $"{path} does not exist, so this test reads nothing.");

            return File.ReadAllText(path);
        }
    }

    [Fact]
    public async Task Every_spelling_of_help_answers_without_configuration()
    {
        // <b>The exit code is the evidence, and the console is deliberately left alone.</b>
        // The first version of this test captured `Console.Out` to read the printed text —
        // and broke two unrelated classes, because `Console.SetOut` is process-global while
        // xunit runs classes in parallel: `AdminCreatorTests` and `PollerPoolTests` went red
        // in the full run and green in isolation, which is [D-111](../../docs/architecture-debt.md)'s
        // shape produced by the test that was supposed to be proving something else.
        //
        // <b>What the code proves instead.</b> This process has no `Graticula__PlatformStore`,
        // so anything that falls through to serving returns 78 —
        // [D-171](../../docs/architecture-debt.md)'s configuration refusal. A 0 can only come
        // from the help branch, which therefore fired. The *content* is asserted by the test
        // below, against the constant itself.
        foreach (string[] spelling in (string[][])[["--help"], ["-h"], ["help"]])
        {
            Assert.Equal(0, await Program.Main(spelling));
        }
    }

    [Fact]
    public void Every_command_the_entry_point_matches_is_in_the_usage_text()
    {
        string source = ProgramSource;

        // `args is ["keygen", ..]`, and the two-word form `["tools", "admincreator", ..]`.
        List<string> commands =
        [
            .. Regex.Matches(source, """args is \["(?<first>[a-z]+)"(?:, "(?<second>[a-z]+)")?""")
                .Select(m => m.Groups["second"].Success
                    ? $"{m.Groups["first"].Value} {m.Groups["second"].Value}"
                    : m.Groups["first"].Value)
                .Distinct(StringComparer.Ordinal),
        ];

        // <b>A search that finds nothing passes silently.</b> The pattern above is the only
        // thing tying this test to the code it polices, so it is asserted rather than
        // trusted — the same reason [D-33](../../docs/architecture-debt.md)'s check has a
        // second test that it is reading the catalogue at all.
        Assert.True(
            commands.Count >= 3,
            $"Only {commands.Count} command(s) were found in Program.cs, which means this test's "
            + "pattern no longer matches how the entry point reads its arguments — not that the "
            + "commands are gone.");

        string usage = UsageText(source);

        foreach (string command in commands)
        {
            Assert.True(
                usage.Contains(command, StringComparison.Ordinal),
                $"`{command}` is a command this binary answers and the usage text does not "
                + "mention it. Somebody holding only the container cannot discover it, which "
                + "is D-168.");
        }
    }

    /// <summary>The literal contents of the <c>Usage</c> constant.</summary>
    private static string UsageText(string source)
    {
        Match block = Regex.Match(
            source,
            "private const string Usage = \"\"\"(?<body>.*?)\"\"\";",
            RegexOptions.Singleline);

        Assert.True(block.Success, "Program.cs has no `Usage` raw string literal to read.");

        return block.Groups["body"].Value;
    }
}
