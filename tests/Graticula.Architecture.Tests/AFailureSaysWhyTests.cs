using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// That a suite which asks this server something records the answer when it is a refusal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three instances in one day — [D-177](../../docs/architecture-debt.md).</b> On
/// 2026-08-26 the console harness discarded what the page had recorded
/// ([D-172](../../docs/architecture-debt.md)), the ArcGIS client asserted on a status one
/// line before reading the body that explained it
/// ([D-174](../../docs/architecture-debt.md)), and a sign-in helper returned a bare null so
/// that the caller's assertion named the step after the one that failed (D-177). Each cost
/// a diagnosis, and each was repaired where somebody happened to look.
/// </para>
/// <para>
/// <b>So this is the enumerating remedy [D-46](../../docs/architecture-debt.md) asks for.</b>
/// That row is the record of what happens to a behaviour fixed in one of the several places
/// that carry it: the others keep the bug. Three fixes and a convention is exactly the shape
/// it describes; a check that reads every file is the shape it prescribes.
/// </para>
/// <para>
/// <b>Two rules, both narrow on purpose.</b> A general *did this failure say enough* cannot
/// be decided by a regular expression, and a check with a list of excuses stops being read.
/// These two are the shapes that actually cost something, are unambiguous in source, and
/// have no legitimate use in a suite: an outcome read and thrown away, and a refusal turned
/// into a bare null. Anything subtler is left to review.
/// </para>
/// </remarks>
public sealed class AFailureSaysWhyTests
{
    /// <summary>`_ = something.StatusCode;` — the answer read and dropped.</summary>
    private static readonly Regex Discarded = new(
        @"^\s*_\s*=\s*[A-Za-z_][A-Za-z0-9_]*\.StatusCode\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A refusal turned into a bare null, with nothing kept.</summary>
    private static readonly Regex SilentlyNull = new(
        @"if\s*\(\s*!\s*[A-Za-z_][A-Za-z0-9_]*\.IsSuccessStatusCode\s*\)\s*\{\s*return\s+(null|default)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static DirectoryInfo Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        string tests = Path.Combine(Root().FullName, "tests");

        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            // Build output holds copies of nothing and generated files of its own.
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(Root().FullName, file), File.ReadAllText(file));
        }
    }

    /// <summary>
    /// Nothing reads a response's status and drops it.
    /// </summary>
    [Fact]
    public void No_suite_reads_an_answer_and_throws_it_away()
    {
        List<string> found = [];

        foreach ((string path, string text) in Sources())
        {
            // The comment in D-177's own repair quotes the shape it removed, and a check
            // that cannot survive being described is not one. Only code counts.
            foreach (Match match in Discarded.Matches(text))
            {
                if (!InAComment(text, match.Index))
                {
                    found.Add($"{path}:{Line(text, match.Index)}");
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "A response's status is read and discarded here, so whatever the server refused "
            + "with is gone by the time anything asserts. That is D-177: the failure then "
            + "names the next step instead. Assert on it, with the body.\n  "
            + string.Join("\n  ", found));
    }

    /// <summary>
    /// Nothing turns a refusal into a bare null.
    /// </summary>
    [Fact]
    public void No_helper_turns_a_refusal_into_a_bare_null()
    {
        List<string> found = [];

        foreach ((string path, string text) in Sources())
        {
            foreach (Match match in SilentlyNull.Matches(text))
            {
                if (!InAComment(text, match.Index))
                {
                    found.Add($"{path}:{Line(text, match.Index)}");
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "A helper here answers every refusal with the same null, so its caller can say "
            + "only that something did not work. 401 and 429 are different problems and need "
            + "different repairs — the second is this server's per-address throttle, which a "
            + "suite signing in from one address can reach on its own. Return the reason "
            + "beside the value.\n  "
            + string.Join("\n  ", found));
    }

    /// <summary>
    /// That the two rules above are reading something.
    /// </summary>
    /// <remarks>
    /// <b>A search that finds nothing because it is searching nowhere passes silently</b> —
    /// [D-33](../../docs/architecture-debt.md)'s check has the same second test for the same
    /// reason. This asserts the corpus, not the verdict.
    /// </remarks>
    [Fact]
    public void The_rules_are_reading_the_suites()
    {
        (string Path, string Text)[] sources = [.. Sources()];

        Assert.True(
            sources.Length > 100,
            $"Only {sources.Length} test source file(s) were found, so the two checks above "
            + "are passing because they are looking at nothing.");

        Assert.Contains(
            sources,
            s => s.Text.Contains("IsSuccessStatusCode", StringComparison.Ordinal));
    }

    private static int Line(string text, int index) =>
        text[..index].Count(c => c == '\n') + 1;

    /// <summary>Whether an offset sits inside a `//` or `///` line.</summary>
    private static bool InAComment(string text, int index)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        string line = text[start..index];

        return line.TrimStart().StartsWith("//", StringComparison.Ordinal)
            || line.Contains("///", StringComparison.Ordinal);
    }
}
