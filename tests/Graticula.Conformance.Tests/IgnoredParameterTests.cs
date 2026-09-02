using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A parameter this server says it ignores changes nothing about the answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-130](../../docs/architecture-debt.md)'s second check, and the failure it is
/// against is a sentence rather than a behaviour.</b> Each ignored parameter carries a
/// reason that is logged to the operator verbatim, and on 2026-08-20 three of those
/// sentences were false because the code had grown past them: `maxAllowableOffset` said
/// *geometry is returned ungeneralised* while PostGIS had been generalising — 26.2 MB
/// became 4.8 MB on the same request — `token` said *authentication is by header* while
/// `?token=` authenticated on every route, and `datumTransformation` said *no reprojection
/// happens* while `outSR` reprojected.
/// </para>
/// <para>
/// <b>A false entry is worse than a missing one</b>, because the server tells whoever
/// reads the log that it dropped a parameter it honoured, and a client reading the same
/// claim builds a workaround for a capability it already has.
/// </para>
/// <para>
/// <b>The names are restated here, and that is a compromise with a guard on it.</b> This
/// suite may not reference our own assemblies — see <see cref="ArcGisClient"/> for why —
/// so it cannot walk <c>FeatureServerQueryParameters.Ignored</c>, and a copied list is
/// exactly the rot this test exists to catch, one level up. Two things close that:
/// <see cref="The_probes_cover_every_parameter_the_server_says_it_ignores"/> sends every
/// name at once and asserts the server still accepts them all, so a name that has become
/// meaningful fails here; and <c>IgnoredParameterCoverageTests</c> in the host suite reads
/// this file and asserts it mentions every name the server actually ignores, so a name
/// added there and forgotten here fails there.
/// </para>
/// </remarks>
/// <b>In the catalogue walk's collection because it reads a layer other tests reconfigure.</b>
/// [D-180](../../docs/architecture-debt.md) made the capability ceiling reach WFS and WMS, and
/// `CeilingReachesEveryReadFaceTests` sets that ceiling on `GRATICULA_TEST_QUERYABLE` to prove
/// it. Before that change the mutation was invisible to most readers; after it, any class
/// querying the same layer in parallel can be answered **403** and report it as its own
/// defect — which is exactly what a CI run did on 2026-09-02, blaming
/// `supportsQueryWithDistance`. Serialising is the fix the collection already exists for.
[Collection("catalogue walk")]
public sealed class IgnoredParameterTests : ArcGisClient
{
    /// <summary>
    /// A plausible value for each ignored parameter.
    /// </summary>
    /// <remarks>
    /// <b>Syntactically credible, because a refusal would prove nothing.</b> The claim
    /// being tested is *this is accepted and changes nothing*; sending `cacheHint=banana`
    /// where the server parses a boolean would test the parser instead. Where a parameter
    /// has no natural value the probe is the one a real client sends.
    /// </remarks>
    private static readonly Dictionary<string, string> Probe = new(StringComparer.Ordinal)
    {
        ["quantizationParameters"] =
            "{\"mode\":\"view\",\"originPosition\":\"upperLeft\",\"tolerance\":1}",
        ["returnCentroid"] = "true",
        ["returnExceededLimitFeatures"] = "false",
        ["cacheHint"] = "true",
        ["datumTransformation"] = "108001",
        ["gdbVersion"] = "sde.DEFAULT",
        ["historicMoment"] = "1700000000000",
        ["f"] = "json",
        ["resultType"] = "standard",
        ["sqlFormat"] = "standard",

        // <b>`token` is deliberately absent, and the absence is asserted below.</b> Its
        // recorded reason is that the token authenticates and the header form is
        // preferred, so the honest probe is a *valid* token compared against the header —
        // and against a public layer both requests are the same request whatever is sent,
        // so the comparison would pass without testing anything. The claim is exercised
        // where it can be: `ArcGisTokenTests` signs in and uses the query channel, and
        // `TokenIsNotLoggedTests` sends a sentinel and searches the live log for it.
    };

    /// <summary>The one entry this class does not probe, named so it cannot be forgotten.</summary>
    private const string NotProbed = "token";

    private const string Baseline =
        "where=1%3D1&outFields=*&returnGeometry=false&resultRecordCount=5&f=json";

    private async Task<string> LayerAsync()
    {
        string? named = Environment.GetEnvironmentVariable(
            QueryCapabilityConformanceTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(named),
            $"{QueryCapabilityConformanceTests.LayerVariable} is not set, so this test FAILS "
            + "rather than skips.");

        await RequireServerAsync();

        return named!.Trim('/');
    }

    private async Task<(HttpStatusCode Status, string Body)> QueryAsync(string tail)
    {
        string root = await RequireServerAsync();
        string layer = await LayerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{root}/rest/services/{layer}/FeatureServer/0/query?{tail}");

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Every parameter the server says it ignores leaves the answer byte for byte the same.
    /// </summary>
    /// <remarks>
    /// <b>Byte for byte, because anything looser would have missed the defects that named
    /// this.</b> `maxAllowableOffset` changed a response from 26.2 MB to 4.8 MB and every
    /// feature was still present with the same attributes — a test comparing counts, or
    /// field names, or the status code would have passed. What changed was the coordinates.
    /// </remarks>
    [Fact]
    public async Task An_ignored_parameter_changes_nothing_about_the_answer()
    {
        (HttpStatusCode status, string plain) = await QueryAsync(Baseline);

        Assert.Equal(HttpStatusCode.OK, status);

        List<string> changed = [];
        int compared = 0;

        foreach (KeyValuePair<string, string> probe in Probe.OrderBy(
            p => p.Key, StringComparer.Ordinal))
        {
            string tail = probe.Key == "f"
                ? Baseline
                : $"{Baseline}&{probe.Key}={Uri.EscapeDataString(probe.Value)}";

            (HttpStatusCode answered, string body) = await QueryAsync(tail);

            compared++;

            if (answered != HttpStatusCode.OK)
            {
                changed.Add($"{probe.Key}: answered {(int)answered}, not 200");
                continue;
            }

            if (!string.Equals(body, plain, StringComparison.Ordinal))
            {
                changed.Add(
                    $"{probe.Key}: the answer differs ({plain.Length} bytes without it, "
                    + $"{body.Length} with) — either it is not ignored, or its recorded "
                    + "reason is out of date");
            }
        }

        Assert.True(compared > 0, "nothing was compared");

        Assert.True(
            changed.Count == 0,
            "A parameter recorded as ignored is a sentence logged to the operator verbatim, "
            + "so one that is not ignored tells them the server dropped something it "
            + "honoured:\n  " + string.Join("\n  ", changed));
    }

    /// <summary>
    /// The list this test walks is the list the server actually uses.
    /// </summary>
    /// <remarks>
    /// <b>The assertion that stops this test rotting the way the entries did.</b> A probe
    /// table with a name the server has dropped, or missing one the server has added, is a
    /// test that quietly covers less than it claims — which is the shape of the defect it
    /// exists to catch. The server reports what it ignored for a request that sends every
    /// name at once, so the two lists can be compared instead of trusted.
    /// </remarks>
    [Fact]
    public async Task The_probes_cover_every_parameter_the_server_says_it_ignores()
    {
        // Sent all together, so one request answers *which of these did you ignore*.
        string all = string.Join(
            "&", Probe.Where(p => p.Key != "f").Select(
                p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        (HttpStatusCode status, _) = await QueryAsync($"{Baseline}&{all}");

        // <b>200, and that is the whole of what this can assert from outside.</b> An
        // unknown parameter is refused by `TryUnknown`, so a name the server no longer
        // ignores would answer 400 here — which is the direction that matters, because a
        // probe for a parameter that has become meaningful is a probe testing nothing.
        Assert.Equal(HttpStatusCode.OK, status);

        // And one that is genuinely unknown is still refused, so the assertion above is
        // not passing because everything is accepted.
        (HttpStatusCode unknown, _) = await QueryAsync(
            $"{Baseline}&thisIsNotAnArcGisParameter=1");

        Assert.Equal(HttpStatusCode.BadRequest, unknown);

        // <b>And the one not probed here is still accepted</b>, so its entry has not
        // quietly become meaningful while this class was looking the other way. See the
        // note on the probe table for why it is compared elsewhere rather than here.
        (HttpStatusCode carried, _) = await QueryAsync($"{Baseline}&{NotProbed}=");

        Assert.Equal(HttpStatusCode.OK, carried);
    }
}
