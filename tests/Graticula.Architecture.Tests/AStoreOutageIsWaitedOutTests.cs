using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// A loop in the host that waits out a store outage recognises every form the outage takes.
/// </summary>
/// <remarks>
/// <b>Seven catches said "the store is away" as <c>NpgsqlException</c>, and one of them cost the showcase an
/// hour.</b> On 2026-11-07 a name lookup failed under memory pressure and surfaced as a bare <c>SocketException</c>
/// through <c>NpgsqlConnection.Open</c>; the site heartbeat did not recognise it, the host stopped, and nothing
/// restarted it. The forms are now listed once, in <c>Graticula.Host.StoreOutage</c>, and this test keeps a new catch
/// from listing them again by hand. Endpoint files are left out: an exception there answers one request with an error,
/// it does not stop the host.
/// </remarks>
public sealed class AStoreOutageIsWaitedOutTests
{
    private static readonly Regex HandWritten = new(
        @"catch\s*\(\s*Exception\s+(\w+)\s*\)\s*when\s*\(\s*\1\s+is\s+NpgsqlException\b", RegexOptions.Compiled);

    [Fact]
    public void Host_loops_ask_StoreOutage_rather_than_naming_NpgsqlException()
    {
        string host = Path.Combine(Root, "Graticula.Host");
        List<string> found = [];

        foreach (string file in Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.Contains("Endpoints", StringComparison.Ordinal) || name == "StoreOutage.cs"
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (HandWritten.IsMatch(lines[i]))
                {
                    found.Add($"{Path.GetRelativePath(Root, file)}:{i + 1}");
                }
            }
        }

        Assert.True(found.Count == 0,
            "These catches treat only NpgsqlException as the store being away; a DNS or socket failure passes them and can "
            + "stop the host. Use StoreOutage.Is(e):\n  " + string.Join("\n  ", found));
    }

    /// <summary>Where the source lives, from the test assembly's own location.</summary>
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

            return Path.Combine(at!.FullName, "src");
        }
    }
}
