using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Every capability flag the layer document publishes, checked against what the server does.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-101](../../docs/open-questions.md), and the answer is *generated is right and
/// checked is what we can have*.</b> That row asked whether the capability report is a
/// vocabulary somebody maintains or something derived from the code that implements the
/// keys, and named the failure precisely: <em>a hand-maintained vocabulary drifts from
/// the provider it describes and then lies in exactly the situation it exists to
/// prevent</em>.
/// </para>
/// <para>
/// <b>It had already drifted, and the repository says so in its own words.</b>
/// <c>FeatureServerMetadataWriter</c> carries the note that
/// <c>supportsPagination</c> and <c>supportsOrderBy</c> sat <c>false</c> for weeks while
/// both worked, and the rule adopted afterwards was that <em>changing a flag and
/// changing the parser are the same commit</em>. That is a rule enforced by memory.
/// <c>QueryCapabilityConformanceTests</c> exercises each parameter and reads none of the
/// flags, so a flag flipped in either direction passes every test in this suite.
/// </para>
/// <para>
/// <b>So this file reads the flag and drives the parameter, in both directions.</b> A
/// <c>true</c> flag whose parameter is refused is an over-claim — a button that returns
/// an error. A <c>false</c> flag whose parameter works is an under-claim, which is
/// quieter and not harmless: a client reading <c>supportsPagination=false</c> asks for
/// the whole layer at once or gives up on the large ones.
/// </para>
/// <para>
/// <b>What is not exercised is named rather than omitted.</b> Three keys have no request
/// this suite can drive against an arbitrary layer, and they are listed in
/// <see cref="NotExercised"/> with the reason. A test that silently covered the
/// convenient subset would read as a closed loop and be an open one, which is the defect
/// it exists to prevent.
/// </para>
/// </remarks>
public sealed class AdvertisedCapabilityTests : ArcGisClient
{
    /// <summary>Which layer to exercise — the same one the query suite uses.</summary>
    public const string LayerVariable = "GRATICULA_TEST_QUERYABLE";

    /// <summary>
    /// The keys this suite cannot drive, and why.
    /// </summary>
    /// <remarks>
    /// <b>Listed so the gap is visible.</b> Each needs a shape of request that depends on
    /// something an arbitrary layer may not have, or on a response this server does not
    /// emit at all. They are the ones to add when the fixture grows, not the ones to
    /// forget.
    /// </remarks>
    private static readonly Dictionary<string, string> NotExercised = new(StringComparer.Ordinal)
    {
        ["supportsReturningGeometryCentroid"] =
            "needs a polygon layer, and the fixture layer's geometry type is not guaranteed",
        ["supportsQueryWithResultType"] =
            "`resultType` changes how a server plans rather than what it returns, so a "
            + "successful response does not distinguish honoured from ignored",
        ["supportsCountDistinct"] =
            "the parameter is a modifier on outStatistics that this server does not parse "
            + "separately, so a refusal cannot be attributed to this key",
    };

    /// <summary>One key, the request that exercises it, and what the request means.</summary>
    private sealed record Probe(string Key, string Parameters, string Means);

    private async Task<(string Path, string Oid, JsonElement Advanced)> LayerAsync()
    {
        string? name = Environment.GetEnvironmentVariable(LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{LayerVariable} is not set, so these tests FAIL rather than skip. Name a layer "
            + "with several features.");

        string path = $"/rest/services/{name}/FeatureServer/0";
        JsonElement document = await GetJsonAsync(path);

        Assert.True(
            document.TryGetProperty("advancedQueryCapabilities", out JsonElement advanced),
            "The layer document carries no `advancedQueryCapabilities`, which is where the "
            + "ArcGIS REST specification puts these and where clients read them.");

        return (path, document.GetProperty("objectIdField").GetString()!, advanced.Clone());
    }

    private static IEnumerable<Probe> Probes(string oid) =>
    [
        new(
            "supportsPagination",
            "resultOffset=1&resultRecordCount=1&returnGeometry=false",
            "asking for one feature starting at the second"),

        new(
            "supportsOrderBy",
            $"where=1%3D1&orderByFields={oid}&resultRecordCount=2&returnGeometry=false",
            "asking for features in a stated order"),

        new(
            "supportsStatistics",
            "outStatistics=%5B%7B%22statisticType%22%3A%22count%22%2C%22onStatisticField%22%3A%22"
            + oid + "%22%2C%22outStatisticFieldName%22%3A%22n%22%7D%5D",
            "asking for a count as a statistic"),

        new(
            "supportsDistinct",
            $"returnDistinctValues=true&outFields={oid}&returnGeometry=false",
            "asking for distinct combinations"),

        new(
            "supportsReturningQueryExtent",
            "returnExtentOnly=true",
            "asking for the extent of what matched"),

        new(
            "supportsQueryWithDistance",
            "geometry=%7B%22x%22%3A0%2C%22y%22%3A0%7D&geometryType=esriGeometryPoint"
            + "&spatialRel=esriSpatialRelIntersects&distance=10&units=esriSRUnit_Meter"
            + "&returnGeometry=false&returnCountOnly=true",
            "asking for a buffered spatial search"),

        new(
            "supportsHavingClause",
            "outStatistics=%5B%7B%22statisticType%22%3A%22count%22%2C%22onStatisticField%22%3A%22"
            + oid + "%22%2C%22outStatisticFieldName%22%3A%22n%22%7D%5D&having=n%3E0",
            "asking to filter grouped rows"),

        new(
            "supportsSqlExpression",
            $"where={oid}%2B1%3E1&returnCountOnly=true",
            "asking for arithmetic in the where grammar"),

        new(
            "supportsPercentileStatistics",
            "outStatistics=%5B%7B%22statisticType%22%3A%22percentile_disc%22%2C"
            + "%22onStatisticField%22%3A%22" + oid
            + "%22%2C%22outStatisticFieldName%22%3A%22p%22%7D%5D",
            "asking for a percentile"),
    ];

    [Fact]
    public async Task Every_advertised_capability_agrees_with_what_the_server_does()
    {
        (string path, string oid, JsonElement advanced) = await LayerAsync();

        List<string> wrong = [];

        foreach (Probe probe in Probes(oid))
        {
            Assert.True(
                advanced.TryGetProperty(probe.Key, out JsonElement flag),
                $"The layer document does not publish `{probe.Key}`, so a client reading "
                + "advancedQueryCapabilities cannot find out whether it may send it.");

            bool claimed = flag.GetBoolean();
            int status = await StatusOfAsync($"{path}/query?{probe.Parameters}&f=json");

            if (claimed && status != 200)
            {
                wrong.Add(
                    $"`{probe.Key}` is advertised true and {probe.Means} answered {status}. "
                    + "An over-claim puts a button in front of somebody that returns an error.");
            }

            if (!claimed && status == 200)
            {
                wrong.Add(
                    $"`{probe.Key}` is advertised false and {probe.Means} answered 200. Either "
                    + "the flag is stale, or the parameter is being accepted and ignored — and "
                    + "a parameter that is ignored rather than refused is the worse of the two, "
                    + "because the answer looks like it honoured the request.");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task The_keys_this_suite_cannot_drive_are_the_ones_it_says_it_cannot()
    {
        // <b>No silent caps.</b> If a key is added to the document and to neither list,
        // this fails — which is the difference between a closed loop and a loop that
        // looks closed. The failure names the key, so whoever added it decides whether
        // it is exercisable rather than inheriting somebody else's silence.
        (_, string oid, JsonElement advanced) = await LayerAsync();

        HashSet<string> accounted = [.. Probes(oid).Select(p => p.Key), .. NotExercised.Keys];
        List<string> unaccounted = [];

        foreach (JsonProperty published in advanced.EnumerateObject())
        {
            if (!accounted.Contains(published.Name))
            {
                unaccounted.Add(published.Name);
            }
        }

        Assert.Empty(unaccounted);

        // And the reverse: a key this suite claims to cover that the document has
        // stopped publishing is a probe testing nothing.
        List<string> gone = [];

        foreach (string key in accounted)
        {
            if (!advanced.TryGetProperty(key, out _))
            {
                gone.Add(key);
            }
        }

        Assert.Empty(gone);
    }
}
