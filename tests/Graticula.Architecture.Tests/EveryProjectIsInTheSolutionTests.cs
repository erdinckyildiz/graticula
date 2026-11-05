using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// <b>Every project under <c>src/</c> and <c>tests/</c> is in <c>graticula.sln</c> — found 2026-11-05.</b>
/// </summary>
/// <remarks>
/// CI builds the solution and then runs each test project with <c>--no-build</c>. A test project the solution does not
/// name is never built, and <c>dotnet test --no-build</c> on a project with no output runs nothing and exits 0 — so a
/// step named for a suite goes green without one test having run. <c>Graticula.Raster.Tiff.Tests</c> was in that state
/// from the day it was written: CI's <c>raster</c> step printed no test run at all on every build, and the suite's 75
/// tests passed the first time anything ran them. That is <c>D-63</c>'s shape — coverage the build claims and does not
/// have — and this test is what keeps it from coming back.
/// </remarks>
public sealed class EveryProjectIsInTheSolutionTests
{
    [Fact]
    public void Every_project_is_in_the_solution()
    {
        DirectoryInfo root = FindRepositoryRoot();
        string solution = File.ReadAllText(Path.Combine(root.FullName, "graticula.sln"));

        List<string> missing = [];

        foreach (string folder in new[] { "src", "tests" })
        {
            foreach (string project in Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*.csproj", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root.FullName, project).Replace('\\', '/');

                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                // The solution names a project by its path with backslashes, as Visual Studio writes it.
                if (!solution.Contains(relative.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
                    && !solution.Contains(relative, StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add(relative);
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "These projects are not in graticula.sln, so CI never builds them and a `dotnet test --no-build` of one runs "
            + "nothing and passes: " + string.Join(", ", missing.Order(StringComparer.Ordinal))
            + ". Add each with `dotnet sln graticula.sln add <path>`.");
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("The repository root (the folder with CLAUDE.md) was not found.");
    }
}
