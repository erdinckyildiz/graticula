using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The two conventions that keep the integration suites from failing on each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-111](../../docs/architecture-debt.md) was fixed in three passes and left two things
/// open, in its own words: <i>nothing detects a new class which publishes and forgets the
/// collection, and nothing stops the next reused class name from becoming somebody's oracle.
/// Both are conventions written down, which is weaker than a check.</i></b> These are the
/// checks.
/// </para>
/// <para>
/// <b>Source walks rather than browser runs</b>, because both faults are visible in the text and
/// neither needs a server. A failure here is a sentence about a file, before a suite has run at
/// all — which is the point: the defect they replace cost a morning of failures attributed twice
/// to the wrong cause.
/// </para>
/// </remarks>
public sealed class SuiteStabilityTests
{
    /// <summary>The prefixes `ArcGisClient.Fixture` treats as a test's own, not a deployment's.</summary>
    private static readonly string[] FixturePrefixes = ["zz_", "corpus_"];

    /// <summary>The collection that serialises the classes walking the catalogue against the ones publishing into it.</summary>
    private const string WalkCollection = "catalogue walk";

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

    /// <summary>
    /// A conformance class that publishes names its fixtures so the walkers can tell them apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two ways to be safe, and the check accepts either</b> — because the repair D-89 and
    /// D-111 arrived at together is two mechanisms rather than one. A class in the
    /// <c>catalogue walk</c> collection cannot run beside a walker at all. A class outside it can
    /// still publish safely if what it publishes is named `zz_` or `corpus_`, because
    /// <c>ArcGisClient.Fixture</c> is what the walkers use to tell a test's own service from a
    /// deployment's.
    /// </para>
    /// <para>
    /// <b>What it checks is the literal, which is what makes it mechanical.</b> A publish whose
    /// name comes from a variable is not resolved — but the class still has to introduce that
    /// name somewhere, and a class with no fixture-prefixed literal at all is the case this is
    /// for: somebody adding a test that publishes `probe` and watching four unrelated suites go
    /// red an hour later.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_conformance_class_that_publishes_names_its_fixtures_or_joins_the_collection()
    {
        string folder = Path.Combine(Root().FullName, "tests", "Graticula.Conformance.Tests");

        Assert.True(Directory.Exists(folder), $"The conformance suite is not at '{folder}'.");

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            string source = File.ReadAllText(file);

            if (!source.Contains("serviceName", StringComparison.Ordinal))
            {
                continue;
            }

            if (source.Contains($"[Collection(\"{WalkCollection}\")]", StringComparison.Ordinal))
            {
                continue;
            }

            // A plain literal handed straight to the publish body, without a prefix: the
            // unambiguous form of the fault, and worth naming on its own.
            foreach (Match named in Regex.Matches(source, "serviceName\\s*=\\s*\"([^\"]+)\""))
            {
                if (!FixturePrefixes.Any(p => named.Groups[1].Value.StartsWith(p, StringComparison.Ordinal)))
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)} publishes '{named.Groups[1].Value}', which is "
                        + "neither a fixture name nor inside the collection");
                }
            }

            bool anyFixtureName = FixturePrefixes.Any(p =>
                Regex.IsMatch(source, "\"" + Regex.Escape(p)));

            if (!anyFixtureName)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)} publishes and holds no zz_ or corpus_ name at all");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A conformance class publishes into the catalogue that three other classes walk, "
            + "without either of the two things that make that safe: joining the "
            + $"'{WalkCollection}' collection, or naming what it publishes with a fixture prefix "
            + "so ArcGisClient.Fixture skips it. This is D-111's first open half — the convention "
            + "was written down and nothing checked it.\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A console test's class selector is qualified when that class is used on more than one tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-111's third pass, and the one that was not a race.</b> `.empty` names two things in
    /// this console — the empty-list <c>&lt;td&gt;</c> and the placeholder
    /// <c>&lt;div class="thumb empty"&gt;</c> a row shows when its service has no cover — and
    /// three tests used the loose selector as their oracle for *is this list empty*. One
    /// assertion became unsatisfiable; two would have read a full page as an empty one.
    /// </para>
    /// <para>
    /// <b>The rule is not *no bare class selectors*, because most classes are unambiguous.</b>
    /// Twenty-five of the console's classes are on more than one kind of element and nearly all
    /// of them should be — <c>mono</c>, <c>tiny</c>, <c>primary</c> are styling. What the check
    /// forbids is asking for one of *those* by class alone: `td.empty` says which one, `.empty`
    /// does not.
    /// </para>
    /// <para>
    /// <b>Read from the console's own source, so it moves when the console does.</b> A
    /// hard-coded list of ambiguous classes would be a second place to keep in step, which is the
    /// shape of the defect one row along.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_console_test_qualifies_a_class_selector_that_names_more_than_one_kind_of_element()
    {
        DirectoryInfo root = Root();
        string web = Path.Combine(root.FullName, "src", "Graticula.Host", "wwwroot");

        Dictionary<string, SortedSet<string>> tagsOfClass = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(web, "*.js")
                     .Concat(Directory.EnumerateFiles(web, "*.html")))
        {
            foreach (Match element in Regex.Matches(
                         File.ReadAllText(file),
                         "<([a-zA-Z][a-zA-Z0-9]*)\\b[^>]*?class=\\\\?\"([^\"\\\\]+)"))
            {
                foreach (string name in element.Groups[2].Value.Split(
                             ' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Interpolated pieces are not class names; they are whatever the value is.
                    if (name.Contains('$', StringComparison.Ordinal)
                        || name.Contains('{', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!tagsOfClass.TryGetValue(name, out SortedSet<string>? tags))
                    {
                        tags = new SortedSet<string>(StringComparer.Ordinal);
                        tagsOfClass[name] = tags;
                    }

                    tags.Add(element.Groups[1].Value.ToLowerInvariant());
                }
            }
        }

        Assert.True(
            tagsOfClass.Count > 20,
            $"Only {tagsOfClass.Count} classes were found in the console's source, which means the "
            + "markup moved and this check is reading nothing. A check that cannot fail is worse "
            + "than no check.");

        string tests = Path.Combine(root.FullName, "tests", "Graticula.Console.Tests");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(tests, "*.cs"))
        {
            foreach (Match call in Regex.Matches(
                         File.ReadAllText(file), "querySelector(?:All)?\\('(\\.[^']+)'\\)"))
            {
                string selector = call.Groups[1].Value;

                // The first component only: `.empty` is the fault, `td.empty` is the repair, and
                // `.gdbpick td.tick span.val` is already saying which element it means.
                string first = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                string name = first.TrimStart('.').Split('.', ':', '[')[0];

                if (tagsOfClass.TryGetValue(name, out SortedSet<string>? tags) && tags.Count > 1)
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)} asks for '{selector}', and '{name}' is on "
                        + string.Join(", ", tags));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A console test selects by a class name that the console puts on more than one kind "
            + "of element. Qualify it with the tag — 'td.empty' rather than '.empty'. This is "
            + "D-111's second open half: three tests used '.empty' as their oracle for an empty "
            + "list and two of them would have read a full page as an empty one.\n  "
            + string.Join("\n  ", offenders));
    }
}
