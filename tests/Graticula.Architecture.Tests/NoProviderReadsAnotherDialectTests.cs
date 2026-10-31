using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// D-162: a provider that is not PostGIS is never handed PostGIS SQL. It reads the where and having clauses' parsed
/// predicate and emits them again in its own dialect, and it refuses a clause that arrives without one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is counted.</b> <c>ParsedWhere</c> carries two things: the predicate tree, and PostgreSQL text emitted
/// from it. The text is the PostGIS provider's own and only it may read it. On 2026-10-31 the four others — DuckDB
/// (ADR-066), SQL Server, Oracle and MySQL — each emitted from the tree and threw when the tree was missing; none read
/// the text. Nothing kept it so. A fifth provider, or a hurried change to one of these four, could paste
/// <c>query.Where.Value.Sql</c> into its statement and every simple comparison would still work: the dialects agree on
/// <c>a = $1</c> and disagree on quoting, casts, case folding and dates, so the fault would show as a wrong answer
/// on the clause nobody tested.
/// </para>
/// <para>
/// <b>The check is by name, and deliberately blunt.</b> A provider reads its own emitted text as <c>emitted.Sql</c>
/// or <c>written.Sql</c>; what it may not read is the <c>.Sql</c> of something called <c>where</c> or
/// <c>having</c>, which is how the clause arrives in every provider. A provider that renames its parameter can slip
/// past this, and the second fact — that each provider says what it does when the tree is missing — is the other half.
/// </para>
/// </remarks>
public sealed class NoProviderReadsAnotherDialectTests
{
    private static readonly Regex ReadsTheText = new(
        @"\b(where|Where|having|Having)\??(\.Value)?\??\.Sql\b", RegexOptions.CultureInvariant);

    private static string Root
    {
        get
        {
            DirectoryInfo? at = new(AppContext.BaseDirectory);

            while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "src")))
            {
                at = at.Parent;
            }

            Assert.True(at is not null, "Could not find the repository root from the test assembly.");

            return at!.FullName;
        }
    }

    /// <summary>Every provider project under src except PostGIS's, whose dialect the text is.</summary>
    private static IEnumerable<string> OtherProviders() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "src"), "Graticula.Providers.*")
            .Where(d => !Path.GetFileName(d).Equals("Graticula.Providers.PostGis", StringComparison.Ordinal));

    private static IEnumerable<(string Relative, string[] Lines)> Sources(string project) =>
        Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Relative: Path.GetRelativePath(Root, p).Replace('\\', '/'), Path: p))
            .Where(f => !f.Relative.Contains("/bin/", StringComparison.Ordinal)
                && !f.Relative.Contains("/obj/", StringComparison.Ordinal))
            .Select(f => (f.Relative, File.ReadAllLines(f.Path)));

    [Fact]
    public void There_are_providers_other_than_postgis_to_check()
    {
        // A vacuous pass is the failure this guards: four were read on 2026-10-31.
        Assert.True(OtherProviders().Count() >= 4, "Expected DuckDB, MySQL, Oracle and SQL Server providers under src.");
    }

    [Fact]
    public void No_provider_but_postgis_reads_the_postgresql_text_of_a_where_or_having_clause()
    {
        List<string> found = [];

        foreach (string project in OtherProviders())
        {
            foreach ((string relative, string[] lines) in Sources(project))
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();

                    if (line.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ReadsTheText.IsMatch(line))
                    {
                        found.Add($"{relative}:{i + 1}");
                    }
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "A provider that is not PostGIS reads the PostgreSQL text of a where or having clause (D-162). Emit the "
            + "clause from its Predicate with PredicateSql.TryEmit and this provider's dialect instead: "
            + string.Join("; ", found));
    }

    [Fact]
    public void Every_provider_that_takes_a_where_clause_refuses_one_without_its_predicate()
    {
        List<string> silent = [];

        foreach (string project in OtherProviders())
        {
            List<(string Relative, string[] Lines)> files = [.. Sources(project)];
            bool takesOne = files.Any(f => f.Lines.Any(l => l.Contains("ParsedWhere", StringComparison.Ordinal)));
            bool refuses = files.Any(f => f.Lines.Any(l =>
                Regex.IsMatch(l, @"\b(where|Where)\??(\.Value)?\.Predicate is null\b", RegexOptions.CultureInvariant)));

            if (takesOne && !refuses)
            {
                silent.Add(Path.GetFileName(project));
            }
        }

        Assert.True(
            silent.Count == 0,
            "A provider takes a ParsedWhere and never says what it does when the clause arrives without its predicate "
            + "(D-162) — it can only be falling back to the PostgreSQL text: " + string.Join(", ", silent));
    }
}
