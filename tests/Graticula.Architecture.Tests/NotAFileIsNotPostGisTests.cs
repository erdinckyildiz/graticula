using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// ADR-205 condition 2: no new call site may read <em>not a GeoParquet locator</em> as <em>a PostgreSQL connection
/// string</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is counted.</b> Until ADR-205 every registered source was either a file DuckDB reads or a PostgreSQL
/// database, so <c>GeoParquetLocator.Is</c> doubled as the test for PostgreSQL, and an Oracle locator fell through to
/// Npgsql wherever it was asked — measured: a publish succeeded and left a service whose every query failed. The
/// sites that existed on 2026-10-20 were each read and either ask about a file for a file's own reason (closing its
/// DuckDB, reading its version) or put Oracle before it; they are listed here with their count.
/// </para>
/// <para>
/// <b>A new call fails this test</b>, and the answer is almost always <c>GeoParquetLocator.KindOf</c>, which names
/// every kind. If the new call really is about files only, raise the count here and say why in the commit.
/// </para>
/// </remarks>
public sealed class NotAFileIsNotPostGisTests
{
    /// <summary>The call sites read on 2026-10-20, per file.</summary>
    private static readonly Dictionary<string, int> Read = new(StringComparer.Ordinal)
    {
        // Closing a removed or moved folder's DuckDB; whether a change moves a file; the file-only echo, refusal of a
        // hand-written locator, and the two secret strippers, each with Oracle handled above it; the publish's file
        // checks; and the two validity and reference guards, which now hand Oracle to OracleTableChecks on the next
        // line (ADR-207).
        ["src/Graticula.Host/AdminEndpoints.cs"] = 12,

        // ADR-207: a file is refused attachments; Oracle and PostGIS are both given them.
        ["src/Graticula.Host/AdminEndpoints.Attachments.cs"] = 1,

        // ADR-207: a file is refused a history; Oracle and PostGIS both keep one.
        ["src/Graticula.Host/AdminEndpoints.History.cs"] = 1,

        // Closing a folder; the probe and the database listing, Oracle asked first.
        ["src/Graticula.Host/GeoParquetSources.cs"] = 3,

        // The read branch, Oracle's ahead of it; the edit refusal, which names Oracle next; and the attachment
        // store's file refusal, Oracle's store chosen below it (ADR-207). (The tile branch went with ADR-206.)
        ["src/Graticula.Host/LayerConnections.cs"] = 3,

        // The relationship refusal, which names Oracle beside it.
        ["src/Graticula.Host/RelationshipEndpoints.cs"] = 3,

        // A file's tile cache version; every other kind goes to TileSources.Registered.
        ["src/Graticula.Host/VectorTileEndpoints.cs"] = 1,
    };

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

    [Fact]
    public void No_new_call_reads_not_a_file_as_postgresql()
    {
        Regex call = new(@"GeoParquetLocator\.Is\(", RegexOptions.CultureInvariant);
        Dictionary<string, int> found = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Root, path).Replace('\\', '/');

            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative == "src/Graticula.Platform/Admin/DataSourceLocator.cs")
            {
                continue;
            }

            int count = File.ReadLines(path)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Sum(line => call.Count(line));

            if (count > 0)
            {
                found[relative] = count;
            }
        }

        List<string> wrong = [];

        foreach ((string file, int count) in found)
        {
            int allowed = Read.TryGetValue(file, out int listed) ? listed : 0;

            if (count > allowed)
            {
                wrong.Add($"{file}: {count} calls, {allowed} read");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "GeoParquetLocator.Is is called where nobody has read whether it means 'not PostgreSQL' — ADR-205 "
            + "condition 2. Ask GeoParquetLocator.KindOf instead, which names Oracle; if the call is about files only, "
            + "raise its count in NotAFileIsNotPostGisTests and say why. " + string.Join("; ", wrong));

        // A count that fell is a site that went away; lower it, so the next one is not let in under the old number.
        List<string> stale = [.. Read.Where(r => (found.TryGetValue(r.Key, out int n) ? n : 0) < r.Value)
            .Select(r => $"{r.Key}: {(found.TryGetValue(r.Key, out int n) ? n : 0)} calls, {r.Value} recorded")];

        Assert.True(stale.Count == 0, "Lower these counts in NotAFileIsNotPostGisTests: " + string.Join("; ", stale));
    }
}
