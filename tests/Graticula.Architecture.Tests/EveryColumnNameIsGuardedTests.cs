using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// Everything that names a column by name is on the list that refuses to drop it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-058](../../docs/adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
/// condition 2, and it is that ADR's own strongest counterargument turned into a check.</b> The
/// datastore's fields can be dropped from a screen, and the whole safety of that is a list in
/// <c>HostedDataEndpoints.HoldingOn</c> naming what reads a column by name. Nothing in the
/// language ties the two together: the next feature that stores a column name — a label
/// expression, a popup field, a join key — will not add itself, and the delete that breaks it
/// will be allowed.
/// </para>
/// <para>
/// <b>The ADR says this in §11 as dissent rather than as a solved problem</b>, and it was wrong
/// within the hour of being written: the first draft listed a fourth holder, a layer filter, that
/// does not exist in this server. A list maintained by remembering is wrong in both directions.
/// </para>
/// <para>
/// <b>So the catalogue's own shape is the check.</b> A property called <c>SomethingColumn</c> or
/// <c>SomethingField</c> on the two types that describe a published layer holds the name of a
/// column, which is the whole class of thing this is about. Every one of them has to be named in
/// the guard. Adding a fifth without touching the guard fails the build, which is what
/// <i>enforced by something that fails when it is incomplete</i> means.
/// </para>
/// <para>
/// <b>What this cannot catch by reading properties</b> is a column name that reaches the server
/// without being stored on the layer — inside the symbology document, which the guard handles by
/// compiling it rather than by reading a property. **That half is no longer a judgement, and it
/// is covered from the other end**: `SymbologyNamesItsColumnsTests` enumerates the renderer kinds
/// `Cim` declares — by reflection over its constants, so an eighth fails by name — and asserts
/// each one reports the column it draws with in `SymbologyPlan.Fields`, which is the list the
/// guard asks. What is left here is the *link* between the two: this file asserts the guard still
/// asks it at all, below.
/// </para>
/// </remarks>
public sealed class EveryColumnNameIsGuardedTests
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

    /// <summary>The two types that describe a published layer to everything else.</summary>
    /// <remarks>
    /// <b>Read as source rather than by reflection, deliberately.</b> The guard is source too —
    /// a method body — so this compares like with like and needs no assembly of the host loaded
    /// into a test that is about a rule rather than about behaviour.
    /// </remarks>
    private static readonly string[] Describing =
    [
        Path.Combine("Graticula.Core", "Catalog", "LayerDefinition.cs"),
        Path.Combine("Graticula.Platform", "Catalog", "PublishedLayer.cs"),
    ];

    /// <summary>
    /// Names one of these types holds are all named in the guard that refuses to drop them.
    /// </summary>
    [Fact]
    public void Every_stored_column_name_is_named_in_the_delete_guard()
    {
        string guard = Guard();

        List<string> missing = [];

        foreach (string property in StoredColumnNames())
        {
            if (!guard.Contains(property, StringComparison.Ordinal))
            {
                missing.Add(property);
            }
        }

        Assert.True(
            missing.Count == 0,
            "A published layer stores the name of a column in "
            + string.Join(", ", missing)
            + ", and HostedDataEndpoints.HoldingOn does not mention it. Dropping that column "
            + "from the Fields screen would then be allowed, and whatever reads it would fail on "
            + "the next request against a table that no longer has it — ADR-058 condition 2. "
            + "Either refuse the column there with a sentence naming what holds it, or say in "
            + "the guard why this one does not need refusing.");
    }

    /// <summary>
    /// The guard still asks the symbology what columns it draws with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The link between the two halves of ADR-058 condition 2, and it is one line of source
    /// that nothing else would miss.</b> The property half is above; the renderer half is
    /// `SymbologyNamesItsColumnsTests`, which proves every renderer kind reports its column in
    /// <c>SymbologyPlan.Fields</c>. Neither is worth anything if the guard stops consulting that
    /// list — and deleting the four lines that do would break no other test, because every one of
    /// them is about a column stored as a property.
    /// </para>
    /// <para>
    /// <b>Asserted on the source rather than on behaviour</b>, for the reason the class remarks
    /// give: the guard is a private method in the host, this project is about rules, and a
    /// behavioural version would need a database, a hosted layer and a stored style to check a
    /// fact that is visible in four lines.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_guard_asks_the_symbology_which_columns_it_draws_with()
    {
        Assert.Contains("layer.Symbology", Guard(), StringComparison.Ordinal);

        string all = File.ReadAllText(
            Path.Combine(Root, "Graticula.Host", "HostedDataEndpoints.cs"));

        // <b>And what it asks the document, which is the half that changed on 2026-09-09.</b>
        // Asking `SymbologyPlan.Fields` is asking what *drawing* reads, and a renderer whose
        // classes all carry one colour compiles to a constant and reports nothing — measured in
        // `SymbologyNamesItsColumnsTests`. `CimProjection.AllFields` reads the document instead,
        // which is what breaks when the column goes.
        Assert.True(
            all.Contains("AllFields()", StringComparison.Ordinal),
            "Nothing in HostedDataEndpoints asks CimProjection.AllFields any more. If the guard "
            + "has gone back to compiling the style and reading SymbologyPlan.Fields, a layer "
            + "whose classes all draw the same colour reports no fields at all — so its "
            + "classifying column can be dropped, the map does not change, and the stored "
            + "document is left naming a column that no longer exists. ADR-058 condition 2's "
            + "second half, and [D-218](../../docs/architecture-debt.md) is the shape of what "
            + "the reader sees afterwards.");
    }

    /// <summary>
    /// The guard is where this test thinks it is.
    /// </summary>
    /// <remarks>
    /// <b>A test that silently found nothing to check would pass forever.</b> The method could be
    /// renamed or moved in a refactor that has nothing to do with fields, and the check above
    /// would then compare a list against an empty string and report success. This is the
    /// assertion that makes the other one mean something.
    /// </remarks>
    [Fact]
    public void The_guard_this_rule_is_about_still_exists()
    {
        Assert.Contains("HoldingOn", Guard(), StringComparison.Ordinal);

        Assert.True(
            StoredColumnNames().Count >= 3,
            "Fewer than three stored column names were found, so either the types that describe "
            + "a layer have moved or the pattern this reads them with no longer matches. Either "
            + "way this file is checking nothing.");
    }

    /// <summary>The source of the method that decides whether a column may be dropped.</summary>
    private static string Guard()
    {
        string where = Path.Combine(Root, "Graticula.Host", "HostedDataEndpoints.cs");

        Assert.True(File.Exists(where), $"{where} is where the delete guard was.");

        string all = File.ReadAllText(where);
        int at = all.IndexOf("private static string? HoldingOn(", StringComparison.Ordinal);

        Assert.True(
            at > 0,
            "HostedDataEndpoints.HoldingOn is gone or renamed. It is the only thing standing "
            + "between the Fields screen and dropping a column something reads — ADR-058 §5c. If "
            + "it moved, move this test with it.");

        // To the end of the method: the next declaration at the same indentation.
        int end = all.IndexOf("\n    }", at, StringComparison.Ordinal);

        return all[at..(end > at ? end : all.Length)];
    }

    /// <summary>Every property of a published layer that holds the name of a column.</summary>
    private static List<string> StoredColumnNames()
    {
        List<string> found = [];

        foreach (string file in Describing)
        {
            string where = Path.Combine(Root, file);

            Assert.True(File.Exists(where), $"{where} describes a published layer, or did.");

            foreach (Match match in Regex.Matches(
                File.ReadAllText(where),
                @"public\s+string\??\s+(\w+(?:Column|Field))\s*\{\s*get"))
            {
                found.Add(match.Groups[1].Value);
            }
        }

        return found;
    }
}
