using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Every parameter the server ignores is one the conformance suite probes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This closes the loop [D-130](../../docs/architecture-debt.md) is about, on the test
/// that was written to close it.</b> `IgnoredParameterTests` sends each ignored parameter
/// and asserts the answer does not change — and it cannot read
/// <c>FeatureServerQueryParameters.Ignored</c>, because the conformance suite may not
/// reference our assemblies. So its list of names is a copy, and a copy that nobody checks
/// is precisely the failure being repaired one level up: an entry added to the server and
/// forgotten in the test leaves a test that quietly covers less than it says.
/// </para>
/// <para>
/// <b>By reading the other test's source, which is unusual and is the cheapest thing that
/// works.</b> The alternative is a shared constant, and there is nowhere to put one: the
/// conformance suite's whole discipline is that it holds no reference to the code it
/// exercises. Comparing names against the file is a text check, it needs no build-order
/// arrangement, and it fails with the name that is missing.
/// </para>
/// <para>
/// <b>It asserts the file was found</b>, because a path that stops resolving would turn
/// this into a test that passes by examining nothing — which is the same class of silent
/// coverage loss it exists to prevent.
/// </para>
/// </remarks>
public sealed class IgnoredParameterCoverageTests
{
    /// <summary>Where the conformance test that probes these lives.</summary>
    /// <remarks>
    /// Found by walking up from the test binary to the repository root, so it works from
    /// a command line, an IDE and a fresh clone alike.
    /// </remarks>
    private static string ProbeFile()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "tests")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);

        string path = Path.Combine(
            at!.FullName,
            "tests",
            "Graticula.Conformance.Tests",
            "IgnoredParameterTests.cs");

        Assert.True(
            File.Exists(path),
            $"The conformance probe file was not found at '{path}'. This test compares the "
            + "server's list of ignored parameters against that file, so a path that no "
            + "longer resolves makes it pass while checking nothing.");

        return path;
    }

    [Fact]
    public void The_conformance_suite_names_every_ignored_parameter()
    {
        string probes = File.ReadAllText(ProbeFile());

        IReadOnlyCollection<string> ignored = FeatureServerQueryParameters.Ignored;

        Assert.NotEmpty(ignored);

        List<string> missing =
        [
            .. ignored
                .Where(name => !probes.Contains(
                    "\"" + name + "\"", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Count == 0,
            "These parameters are ignored by the server and not named in "
            + "IgnoredParameterTests, so nothing asserts that ignoring them is true:\n  "
            + string.Join("\n  ", missing)
            + "\n\nAdd a probe value for each, or — if it has stopped being ignored — "
            + "remove it from IgnoredParameters, which is the direction D-130 is about.");
    }
}
