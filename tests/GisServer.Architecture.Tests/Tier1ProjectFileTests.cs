using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace GisServer.Architecture.Tests;

/// <summary>
/// Checks the Tier 1 <em>project file</em> for declared dependencies, which the
/// assembly-reference test in <see cref="TierBoundaryTests"/> cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both tests exist.</b> The reference test was written first and looked
/// sufficient. Verifying it against a deliberate violation showed it was not:
/// the C# compiler omits a reference from assembly metadata when no type from it
/// is used, so a <c>PackageReference</c> added to Tier 1 stays invisible until
/// somebody writes the first line that consumes it.
/// </para>
/// <para>
/// That is the wrong moment to find out. By then the dependency is in the code
/// and removing it is a refactor rather than a deletion. This test fails at the
/// moment the dependency is <em>declared</em>.
/// </para>
/// <para>
/// The reference test is still the stronger of the two, because it also catches
/// a library arriving transitively through a project reference. Neither
/// subsumes the other.
/// </para>
/// </remarks>
public sealed class Tier1ProjectFileTests
{
    /// <summary>
    /// Projects governed by <c>docs/build-vs-adopt-policy.md</c> §4's
    /// <em>written by us, always</em> rule, relative to the repository root.
    /// New Tier 1 projects belong in this list; Tier 2 adapters do not.
    /// </summary>
    private static readonly string[] Tier1Projects =
    [
        Path.Combine("src", "GisServer.Core", "GisServer.Core.csproj"),
        Path.Combine("src", "GisServer.Platform", "GisServer.Platform.csproj"),
    ];

    [Fact]
    public void Tier1_projects_declare_no_package_reference()
    {
        DirectoryInfo root = FindRepositoryRoot();
        List<string> offenders = [];

        foreach (string relative in Tier1Projects)
        {
            string path = Path.Combine(root.FullName, relative);
            Assert.True(File.Exists(path), $"Tier 1 project not found at {path}. Has it moved?");

            IEnumerable<string> packages = XDocument.Load(path)
                .Descendants("PackageReference")
                .Select(static element => element.Attribute("Include")?.Value ?? "(unnamed)");

            offenders.AddRange(packages.Select(package => $"{relative} declares {package}"));
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             A Tier 1 project declares a package dependency:

                 {string.Join("\n    ", offenders)}

             build-vs-adopt-policy.md §4: Tier 1 is written by us, always. Tier 2 libraries
             are permitted behind our own port interface — the port belongs in Tier 1 and
             the PackageReference belongs in the adapter project that implements it.
             """);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "gis-server.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory;
    }
}
