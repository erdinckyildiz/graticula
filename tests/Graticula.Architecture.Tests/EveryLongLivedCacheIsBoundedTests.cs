using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Graticula.Architecture.Tests;

/// <summary>
/// Every structure the serving process carries between requests, and what bounds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-64](../../docs/open-questions.md) asked how a leak is told from a legitimately
/// growing cache with no baseline, and the answer is decomposition rather than
/// history.</b> A fresh deployment has no history and never will on its first day. What
/// it can have is a complete list of what persists between requests, each with a bound
/// that is a property of the catalogue rather than of the request count — and then
/// <em>the heap grew and every count is flat</em> is a leak, and <em>the heap grew and a
/// count grew with it</em> is a cache doing its job. That is the signal the row wanted,
/// available on the first minute of the first day.
/// </para>
/// <para>
/// <b>It only works if the list is complete, which is what this test is.</b> Enumerating
/// them by hand found one that was neither bounded nor counted —
/// [D-160](../../docs/architecture-debt.md), a static dictionary keyed by layer id that
/// nothing ever removed from, growing with every publication a deployment had ever made.
/// One unlisted structure poisons the measurement before it is taken, so a new field
/// fails the build until somebody says what bounds it.
/// </para>
/// <para>
/// <b>Fixed lookup tables are listed too, and marked as fixed.</b> A table of operator
/// names built once at class load is not a cache and cannot grow; saying so costs a line
/// and stops the list reading as though somebody forgot them.
/// </para>
/// </remarks>
public sealed class EveryLongLivedCacheIsBoundedTests
{
    /// <summary>Where the serving process keeps things between requests.</summary>
    private static readonly string[] Assemblies =
    [
        "src/Graticula.Host",
        "src/Graticula.Platform.Postgres",
        "src/Graticula.Api.Wms",
        "src/Graticula.Api.Wfs",
        "src/Graticula.Api.ArcGis",
        "src/Graticula.Api.OgcFeatures",
    ];

    /// <summary>
    /// Each dictionary field that outlives a request, and what stops it growing.
    /// </summary>
    /// <remarks>
    /// <b>The bound must be a property of something other than how many requests have
    /// arrived.</b> A catalogue-shaped bound — layers, data sources, folders — is fine: a
    /// deployment with a thousand layers is allowed a thousand entries. An explicit
    /// capacity is fine. <em>Expires after five minutes</em> on its own is not, because
    /// staleness decides whether to trust an entry and not whether to keep it — which is
    /// precisely how D-160 happened.
    /// </remarks>
    private static readonly Dictionary<string, string> Bounds = new(StringComparer.Ordinal)
    {
        ["ConnectionBudget._sources"] = "one semaphore per data source",
        ["ConnectionBudget._waiting"] = "one counter per data source",
        ["FileSystemTileCache._index"] = "byte budget with least-recently-used eviction",
        ["JobSignal._waiting"] = "one per job kind, and the kinds are an enum",
        ["LayerConnections._pools"] = "one pool per connection string; cleared on reload",
        ["LayerConnections._attachmentPools"] = "one pool per connection string; cleared on reload",
        ["LogEndpoints._seen"] = "explicit capacity; cleared when full",
        ["ServiceContexts._entries"] = "one per table, and expires",
        ["ServiceContexts._known"] = "one per table; removed by Forget on unpublish and refresh",
        ["ServiceContexts._times"] = "one per layer; removed by Forget on unpublish and refresh (D-160)",
        ["SourceBreaker._tripped"] = "one per data source; removed on recovery",
        ["TileSingleFlight._building"] = "one per tile being built right now",
        ["CatalogFallback._last"] = "explicit capacity; cleared when full",
        ["DatumShiftNotices._seen"] =
            "explicit ceiling of 256 layer-and-reference pairs; stops recording rather "
            + "than evicting, because eviction would let the same notice be logged twice "
            + "(Q-141)",

        // Fixed at class load. Not caches.
        ["FilterReader.Comparisons"] = "fixed: the comparison operators WFS defines",
        ["FilterReader.SpatialRelations"] = "fixed: the spatial relations WFS defines",
        ["LegendGraphic.Nothing"] = "fixed: the empty attribute set, shared and never written",
        ["CommonPasswords.Unleet"] = "fixed: the substitution table",
        ["FeatureServerQueryParameters.IgnoredParameters"] = "fixed: the parameters ArcGIS sends and we ignore",
        ["GeometryServerEndpoints.Blocked"] = "fixed: the operations this surface refuses",
        ["GeometryServerEndpoints.Notations"] = "fixed: the coordinate notations",
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

    /// <summary>A field declaration of a dictionary type that lives beyond one request.</summary>
    /// <remarks>
    /// <b>Fields, not locals, and not return types either.</b> A dictionary built inside
    /// a method dies with the call and is nobody's problem, so the pattern requires an
    /// access modifier, which a local cannot have. Braces are excluded from the middle for
    /// the second case: <c>private static Dictionary&lt;…&gt; Parse(string s)</c> followed
    /// by a local of the same type read as one declaration until the body's opening brace
    /// was ruled out, and the check reported a local as an unbounded cache.
    /// </remarks>
    private static readonly Regex Declaration = new(
        @"(?:private|internal|public|protected)\s+(?:static\s+)?(?:readonly\s+)?"
        + @"(?:static\s+)?(?:Concurrent)?Dictionary<[^;{}]*?>\s*\r?\n?\s*(\w+)\s*(?:=|;)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void Every_dictionary_that_outlives_a_request_says_what_bounds_it()
    {
        List<string> unlisted = [];
        HashSet<string> found = [];

        foreach (string assembly in Assemblies)
        {
            string directory = Path.Combine(
                Root, assembly.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(Directory.Exists(directory), $"{assembly} is not on disk.");

            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string type = Path.GetFileNameWithoutExtension(file);

                foreach (Match match in Declaration.Matches(File.ReadAllText(file)))
                {
                    string name = $"{type}.{match.Groups[1].Value}";

                    found.Add(name);

                    if (!Bounds.ContainsKey(name))
                    {
                        unlisted.Add(name);
                    }
                }
            }
        }

        Assert.True(
            unlisted.Count == 0,
            "These dictionaries outlive a request and nothing says what bounds them:\n  "
            + string.Join("\n  ", unlisted.Distinct().Order(StringComparer.Ordinal))
            + "\n\nAdd each to `Bounds` with the thing that stops it growing — a count of "
            + "layers, of data sources, of anything but requests — or `fixed:` if it is a "
            + "lookup table built once. Q-64's whole answer is that the list is complete: one "
            + "unbounded structure nobody can enumerate makes *the heap grew and every count "
            + "is flat* stop meaning anything, and D-160 is what that looks like when it "
            + "happens.");

        // <b>And the reverse, so the list does not accumulate ghosts.</b> A name here that
        // no longer exists is a bound nobody is keeping, which reads as coverage.
        List<string> gone = [.. Bounds.Keys.Where(k => !found.Contains(k))];

        Assert.True(
            gone.Count == 0,
            "These are listed as bounded and no longer exist:\n  "
            + string.Join("\n  ", gone.Order(StringComparer.Ordinal)));
    }
}
