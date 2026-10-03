using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The baked transformation register still says what PROJ's register says — ADR-160, as
/// <see cref="TheAxisRegisterIsNotStaleTests"/> checks ADR-060's: the tool is run into a temporary file and the
/// bytes compared, and without a register the test fails rather than skips.
/// </summary>
public sealed class TheTransformationRegisterIsNotStaleTests
{
    /// <summary>Where the caller has put a copy of PROJ's register.</summary>
    private const string Variable = "GRATICULA_TEST_PROJ_DB";

    /// <summary>
    /// Regenerating from the register produces the file that is in the tree.
    /// </summary>
    [Fact]
    public void Regenerating_the_register_changes_nothing()
    {
        string register = Register();
        string root = Root();

        string generated = Path.Combine(Path.GetTempPath(), $"transformations-{Guid.NewGuid():N}.cs");

        try
        {
            (int code, string output) = Run(
                root, "python", ["tools/datum-transformations.py", register, generated]);

            Assert.True(
                code == 0 && File.Exists(generated),
                $"tools/datum-transformations.py exited {code} and wrote nothing. This test cannot tell a "
                + $"stale register from a broken tool, so it says so rather than passing:\n{output}");

            string expected = File.ReadAllText(generated).Replace("\r\n", "\n");
            string actual = File.ReadAllText(
                Path.Combine(root, "src", "Graticula.Core", "Geometry", "TransformationRegister.cs"))
                .Replace("\r\n", "\n");

            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                "`src/Graticula.Core/Geometry/TransformationRegister.cs` no longer matches what "
                + "`tools/datum-transformations.py` produces from PROJ's register. Either EPSG has revised "
                + "since it was generated — the file's own header says which register it came "
                + "from — or somebody has edited a generated file by hand. Regenerate it:\n"
                + "  python tools/datum-transformations.py <proj.db>\n"
                + "ADR-160 accepts this cost as ADR-060 §8 did; this is it arriving. "
                + $"First difference: {Difference(expected, actual)}");
        }
        finally
        {
            if (File.Exists(generated))
            {
                File.Delete(generated);
            }
        }
    }

    /// <summary>Where the two texts part company, in words rather than in offsets.</summary>
    /// <param name="expected">What the register produces.</param>
    /// <param name="actual">What is in the tree.</param>
    /// <returns>A sentence naming the first line that differs.</returns>
    private static string Difference(string expected, string actual)
    {
        string[] left = expected.Split('\n');
        string[] right = actual.Split('\n');

        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return $"line {i + 1} — the register says `{Short(left[i])}` and the file says "
                     + $"`{Short(right[i])}`.";
            }
        }

        return $"the files agree for {Math.Min(left.Length, right.Length)} lines and then one "
             + $"ends: the register produces {left.Length} lines, the file has {right.Length}.";
    }

    /// <summary>A line, trimmed to something a message can carry.</summary>
    /// <param name="line">The line.</param>
    /// <returns>Its first sixty characters.</returns>
    private static string Short(string line) =>
        line.Length <= 60 ? line.Trim() : line.Trim()[..60] + "…";

    /// <summary>The register, or a failure saying how to get one.</summary>
    /// <returns>Its path.</returns>
    private static string Register()
    {
        string? where = Environment.GetEnvironmentVariable(Variable);

        Assert.False(
            string.IsNullOrWhiteSpace(where),
            $"{Variable} is not set, so this test FAILS rather than skips. It needs PROJ's own "
            + "register, which PostGIS ships and which this deployment's transformations already "
            + "use: `docker cp gis-experiment-postgis:/usr/share/proj/proj.db /tmp/proj.db`. "
            + "Without it nothing checks whether the baked transformations have fallen behind "
            + "EPSG, and that is the whole cost ADR-160 accepted.");

        Assert.True(
            File.Exists(where),
            $"{Variable} points at {where} and there is nothing there.");

        return where!;
    }

    /// <summary>The repository root, from the test assembly's own location.</summary>
    /// <returns>The path.</returns>
    private static string Root()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "src")))
        {
            at = at.Parent;
        }

        Assert.True(at is not null, "Could not find the repository root from the test assembly.");

        return at!.FullName;
    }

    /// <summary>Runs one command and collects what it said.</summary>
    /// <param name="root">Where to run it.</param>
    /// <param name="file">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The exit code and everything it printed.</returns>
    private static (int Code, string Output) Run(string root, string file, string[] arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = file,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start);

        Assert.True(process is not null, $"{file} did not start.");

        string output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        process.WaitForExit(120_000);

        return (process.HasExited ? process.ExitCode : -1, output);
    }
}
