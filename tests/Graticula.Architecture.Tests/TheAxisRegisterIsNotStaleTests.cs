using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The baked axis register still says what the authority's register says.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-060](../../docs/adr/ADR-060-the-axis-order-comes-from-the-register.md) condition 2,
/// and it is the condition that makes that decision honest.</b> ADR-060 carries EPSG's axis
/// definitions in the product as generated data rather than looking them up, and its §8 says
/// plainly what that costs: <i>the register is a copy of somebody else's data, and copies rot.</i>
/// A cost nothing measures is a cost nobody pays until a customer does.
/// </para>
/// <para>
/// <b>It runs the generator rather than reimplementing it.</b> A test that queried
/// <c>proj.db</c> itself would be a second copy of <c>tools/axis-order.py</c>'s two SQL
/// statements — the thing that would drift is exactly the thing being checked, and it would need
/// a SQLite package this repository does not have. Running the tool into a temporary file and
/// comparing bytes checks the whole path, including the generator.
/// </para>
/// <para>
/// <b>Absent a register it fails rather than skips</b>, in this repository's style: a test that
/// goes green with its subject missing is worse than no test, and staleness is precisely the
/// thing nobody notices unaided. The recipe is one line and it is in the tool's own docstring —
/// <c>docker cp gis-experiment-postgis:/usr/share/proj/proj.db …</c> — because PostGIS ships the
/// register this deployment's transformations already use.
/// </para>
/// </remarks>
public sealed class TheAxisRegisterIsNotStaleTests
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

        string generated = Path.Combine(Path.GetTempPath(), $"axis-{Guid.NewGuid():N}.cs");

        try
        {
            (int code, string output) = Run(
                root, "python", ["tools/axis-order.py", register, generated]);

            Assert.True(
                code == 0 && File.Exists(generated),
                $"tools/axis-order.py exited {code} and wrote nothing. This test cannot tell a "
                + $"stale register from a broken tool, so it says so rather than passing:\n{output}");

            string expected = File.ReadAllText(generated).Replace("\r\n", "\n");
            string actual = File.ReadAllText(
                Path.Combine(root, "src", "Graticula.Core", "Geometry", "AxisOrderRegister.cs"))
                .Replace("\r\n", "\n");

            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                "`src/Graticula.Core/Geometry/AxisOrderRegister.cs` no longer matches what "
                + "`tools/axis-order.py` produces from PROJ's register. Either EPSG has revised "
                + "since it was generated — the file's own header says which register it came "
                + "from — or somebody has edited a generated file by hand. Regenerate it:\n"
                + "  python tools/axis-order.py <proj.db>\n"
                + "ADR-060 §8 says this is the cost that decision accepts; this is it arriving. "
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
            + "Without it nothing checks whether the baked axis order has fallen behind EPSG, "
            + "and that is the whole cost ADR-060 accepted.");

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
