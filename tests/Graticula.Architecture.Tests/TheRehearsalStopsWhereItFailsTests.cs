using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The quickstart rehearsal's top-level steps keep their own exit status —
/// [D-393](../../docs/architecture-debt.md).
/// </summary>
/// <remarks>
/// The build, migrate and up steps were <c>$COMPOSE … 2>&amp;1 | tail -n</c>; a pipeline's status is its last
/// command's, so on 2026-11-08 a build that failed on a network timeout was not a failed step, the released image
/// migrated the store, and the rehearsal failed later as a schema mismatch. The script is <c>sh</c>, where
/// <c>pipefail</c> is not portable, so the steps go through its <c>step</c> function, which keeps the command's
/// status. Textual: what can be checked cheaply is that no top-level line pipes a compose command into anything.
/// </remarks>
public sealed class TheRehearsalStopsWhereItFailsTests
{
    private static string Root
    {
        get
        {
            DirectoryInfo? at = new(AppContext.BaseDirectory);

            while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "tools")))
            {
                at = at.Parent;
            }

            Assert.True(at is not null, "Could not find the repository root from the test assembly.");

            return at!.FullName;
        }
    }

    [Fact]
    public void No_top_level_compose_step_is_piped()
    {
        string[] lines = File.ReadAllLines(Path.Combine(Root, "tools", "quickstart-rehearsal.sh"));

        string[] piped = lines
            .Select((line, number) => (line, number: number + 1))
            .Where(l => Regex.IsMatch(l.line, @"^\$COMPOSE\b[^#]*\|"))
            .Select(l => $"line {l.number}: {l.line}")
            .ToArray();

        Assert.True(piped.Length == 0,
            "A top-level compose step is piped, so its failure is the pipe's last command's (D-393); run it through `step`:\n"
            + string.Join("\n", piped));
    }

    [Fact]
    public void The_build_runs_through_step()
    {
        string script = File.ReadAllText(Path.Combine(Root, "tools", "quickstart-rehearsal.sh"));

        Assert.Matches(@"(?m)^step \d+ \$COMPOSE build\s*$", script);
        Assert.Matches(@"(?m)^step \d+ \$COMPOSE run --rm server migrate --apply\s*$", script);
    }
}
