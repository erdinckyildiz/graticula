using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// The columns whose meaning moved are not read back by anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-33](../../docs/architecture-debt.md) and [D-24](../../docs/architecture-debt.md), and
/// this file exists because both rows say the same thing about their own defence.</b> Migration 11
/// moved sharing, status and ownership from the layer to the service and left the layer's columns
/// in place, because expand-and-contract requires it. What stops a stale value being believed is
/// that the catalogue's select list reads `s.sharing` and never `l.sharing` — which D-33 calls
/// *a defence made of one SQL string and a comment*.
/// </para>
/// <para>
/// <b>This repository already has the cautionary tale, twice.</b> `layer.is_hosted` stayed
/// writable after its meaning moved, drifted to false on every insert, and silently disabled every
/// vector tile service. And `PUT /admin/layers/{name}/sharing` wrote `layer.sharing` after the
/// serving path had moved to the service, so making a layer private answered 200, changed a column
/// nothing reads, and left the layer readable by anybody.
/// </para>
/// <para>
/// <b>So the comment becomes a check.</b> This does not drop the columns — that is a contract
/// migration and a release decision, and it is what both rows stay open for. It makes the defence
/// structural in the meantime: the next reader of `l.sharing` fails the build instead of shipping.
/// </para>
/// </remarks>
public sealed class DeadColumnsStayDeadTests
{
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

    /// <summary>
    /// The layer columns whose meaning lives on the service now.
    /// </summary>
    /// <remarks>
    /// <b>Qualified with `l.`, because that is how the catalogue's SQL spells them</b> and the
    /// service's own columns carry the same names. `s.sharing` is the right answer and
    /// `l.sharing` is the wrong one; an unqualified search cannot tell them apart.
    /// </remarks>
    private static readonly string[] Dead =
        ["l.sharing", "l.status", "l.owner_principal_id", "l.is_hosted"];

    /// <summary>
    /// Nothing outside the migrations reads a layer column whose meaning has moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The migrations are exempt because they are the history.</b> Migration 11 has to name the
    /// columns it copies out of, and a migration is read once in the order it was written; it is
    /// not a query somebody might reach for tomorrow.
    /// </para>
    /// <para>
    /// <b>Comments are exempt too, and deliberately so.</b> Three of these columns are explained
    /// at length in the catalogue's own SQL — *reading l.sharing here would be the is_hosted
    /// mistake a second time* — and a check that failed on the sentence warning against the
    /// mistake would delete the warning.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_query_outside_the_migrations_reads_a_column_whose_meaning_moved()
    {
        List<string> found = [];

        foreach (string file in Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || Path.GetFileName(file).Contains("Migrations", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                // A comment, in either spelling, including the `--` inside a SQL literal.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("--", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                foreach (string column in Dead)
                {
                    if (line.Contains(column, StringComparison.Ordinal))
                    {
                        found.Add(
                            $"{Path.GetRelativePath(Root, file)}:{i + 1} reads {column} — "
                            + trimmed[..Math.Min(90, trimmed.Length)]);
                    }
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "Migration 11 moved sharing, status and ownership onto the service and left the "
            + "layer's columns carrying whatever they held that day. Reading one is the "
            + "`is_hosted` mistake a third time — a value that is wrong and does not "
            + "error:\n  " + string.Join("\n  ", found));
    }

    /// <summary>
    /// The check is looking at the file it is about.
    /// </summary>
    /// <remarks>
    /// <b>A search that finds nothing because it is searching nowhere passes silently.</b> The
    /// catalogue's SQL is where the right spelling lives, so seeing `s.sharing` there is the
    /// cheapest proof that this test can see the code it is checking.
    /// </remarks>
    [Fact]
    public void The_check_is_reading_the_catalogue_it_is_about()
    {
        string catalogue = Path.Combine(
            Root, "Graticula.Platform.Postgres", "PostgresLayerCatalog.cs");

        Assert.True(File.Exists(catalogue), $"{catalogue} is not where this test expects it.");

        Assert.Contains(
            "s.sharing",
            File.ReadAllText(catalogue),
            StringComparison.Ordinal);
    }
}
