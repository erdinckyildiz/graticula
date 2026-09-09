using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// Enforces <c>docs/adr/ADR-061-metrics-are-aggregate-and-per-entity-numbers-live-in-the-admin-api.md</c>
/// condition 1 — the first metric this server emits is written after that decision
/// has been read, not before.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-061](../../docs/adr/ADR-061-metrics-are-aggregate-and-per-entity-numbers-live-in-the-admin-api.md)
/// decides that no metric may carry a label identifying a service or a layer, and in
/// particular none may carry a value taken from a request path segment before the
/// catalogue has resolved it.</b> The second half is a security rule rather than a cost
/// rule: <c>/rest/services/&lt;anything&gt;/FeatureServer</c> mints a new series per
/// request, which is an unbounded attacker-controlled key space and turns a monitoring
/// bill into a memory-exhaustion vector.
/// </para>
/// <para>
/// <b>And ADR-061 §6 admits the decision otherwise makes itself felt nowhere.</b>
/// Nothing in this product emits a metric today, so there is no code for the rule to
/// govern and nothing to notice when it is broken. A rule in that position is discovered
/// after somebody's dashboard is already built on top of its violation — which is the
/// one outcome ADR-061 §1 says the whole decision exists to get ahead of, because the
/// choice is free only while nobody is scraping.
/// </para>
/// <para>
/// <b>So this fails on the arrival of the capability rather than on the misuse of it.</b>
/// It cannot check a label that does not exist yet; what it can check is that the first
/// person to reach for a metrics API is handed the decision at the moment they reach.
/// That makes it a *deliberately* coarse test — introducing a counter with no labels at
/// all is correct under ADR-061 and still fails this — and the failure is answered in
/// one line: read the ADR, satisfy §5, and add the file to
/// <see cref="Acknowledged"/> with the review noted there.
/// </para>
/// <para>
/// <b>The same shape as
/// <see cref="TilePipelineVersionTests"/>.</b> A declaration maintained by whoever
/// changes the thing is unmaintained the first time somebody forgets, and then it is
/// wrong in exactly the situation it exists for. Failing loudly and being answered in one
/// line beats drifting silently.
/// </para>
/// </remarks>
public sealed class MetricsStayAggregateTests
{
    /// <summary>
    /// What reaching for a metrics API looks like in source text.
    /// </summary>
    /// <remarks>
    /// Text rather than types, because the point is to fire on the <em>first</em> use —
    /// before there is an assembly to reflect over, and before a package reference has
    /// been resolved. `ILogger` and the structured log are deliberately absent: ADR-061
    /// §5 makes the request log the answer to <em>which service is slow</em>, so logging
    /// is the sanctioned direction rather than the governed one.
    /// </remarks>
    private static readonly string[] Reaches =
    [
        "System.Diagnostics.Metrics",
        "new Meter(",
        "CreateCounter",
        "CreateHistogram",
        "CreateUpDownCounter",
        "CreateObservableGauge",
        "IMeterFactory",
        "OpenTelemetry",
        "Prometheus",
    ];

    /// <summary>
    /// Files that have been read against ADR-061 §5 and are allowed to say these words.
    /// </summary>
    /// <remarks>
    /// <b>Empty on purpose, and it should stay small.</b> A file belongs here when
    /// somebody has read ADR-061 §5 and satisfied it, and the entry is worth a sentence
    /// saying which metric was added and what its labels are — an allow-list that
    /// accumulates unexplained entries is the mechanism failing rather than working.
    /// This test itself is excluded by path rather than by name, since it necessarily
    /// contains every word it looks for.
    /// </remarks>
    private static readonly HashSet<string> Acknowledged =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Nothing emits a metric without ADR-061 having been read first.
    /// </summary>
    [Fact]
    public void A_metric_arrives_only_after_the_decision_about_labels_is_read()
    {
        DirectoryInfo root = FindRepositoryRoot();

        List<string> found = [];

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            // Build output is not source, and it carries whatever the packages carry.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string name = Path.GetFileName(file);

            if (Acknowledged.Contains(name))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            foreach (string reach in Reaches)
            {
                if (text.Contains(reach, StringComparison.Ordinal))
                {
                    found.Add($"{Path.GetRelativePath(root.FullName, file)} says `{reach}`");
                }
            }
        }

        // <b>And the package list, because a reference arrives before a using directive.</b>
        // Directory.Packages.props is where this repository pins every version, so an
        // exporter appears there first and this catches it a commit earlier.
        string packages = Path.Combine(root.FullName, "Directory.Packages.props");

        if (File.Exists(packages))
        {
            string text = File.ReadAllText(packages);

            foreach (string reach in new[] { "OpenTelemetry", "Prometheus", "prometheus-net" })
            {
                if (text.Contains(reach, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"Directory.Packages.props references `{reach}`");
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "This server is about to emit a metric, and ADR-061 decides what a metric may "
            + "be labelled with before the first one ships:\n\n  "
            + string.Join("\n  ", found)
            + "\n\nADR-061 §5: no metric may carry a label whose value identifies a service "
            + "or a layer, and in particular none may carry a value taken from a request "
            + "path segment before the catalogue has resolved it. The second half is a "
            + "security rule — /rest/services/<anything>/FeatureServer mints a new series "
            + "per request, which is unbounded and attacker-controlled. The first half is "
            + "arithmetic: three metrics aggregate is 24 series flat in service count, the "
            + "same three labelled by service at CLAUDE.md §7's target is about 24,000, and "
            + "with an operation dimension about 90,000.\n\n"
            + "If the new metric satisfies §5, add its file to "
            + "MetricsStayAggregateTests.Acknowledged with a sentence saying which metric "
            + "it is and what its labels are. If it does not, it is the label that changes "
            + "rather than this test. ADR-061 §9's first revisit trigger is 'the first "
            + "exporter, or the first deployment that asks for one' — if that is what this "
            + "is, the ADR is reopened rather than worked around.");
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!;
    }
}
