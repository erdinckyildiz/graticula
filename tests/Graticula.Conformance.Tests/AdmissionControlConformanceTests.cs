using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A flood is refused with a whole answer or served with a whole answer, never with half of one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is condition 4 of
/// <see href="../../docs/adr/ADR-046-admission-control-bounds-the-queue-not-the-wait.md">
/// ADR-046</see>, and it is the condition that chose the decision.</b> The alternative the debt
/// row asked for was to kill a request that had held a permit too long, and it was rejected
/// because a response already being written cannot be refused: there is no status left to send
/// once bytes are on the wire, so the client gets a document that stops mid-array and reports a
/// parse error pointing at its own parser. Bounding the queue instead means the refusal happens
/// before the first byte.
/// </para>
/// <para>
/// <b>So the assertion is not that anything is refused.</b> Whether this machine can drive the
/// queue to its bound is exactly what ADR-046 condition 1 says is unproven, and a test that
/// demanded a 503 would fail on a fast host for the right reason and be deleted for the wrong
/// one. What is asserted is the invariant that holds either way: <b>every answer that is not a
/// refusal parses.</b> Under the rejected alternative this test fails; under this one it cannot.
/// </para>
/// <para>
/// <b>Bodies are read to the last byte on purpose.</b> A truncated JSON response has a correct
/// status line and correct headers — the failure is only in the body, and only at its end, which
/// is why <c>StatusOfAsync</c> would have missed it and why every other suite here would have
/// stayed green.
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
public sealed class AdmissionControlConformanceTests : ArcGisClient
{
    /// <summary>
    /// How many callers flood at once.
    /// </summary>
    /// <remarks>
    /// <b>Well past the 96 the development budget will queue</b> — 24 permits at four waiters
    /// each — so the bound is reached if this machine can reach it. It is a number chosen to
    /// make a refusal likely rather than to make one certain; see the class remarks for why the
    /// difference does not weaken the assertion.
    /// </remarks>
    private const int Callers = 160;

    /// <summary>Which layer to flood.</summary>
    /// <remarks>
    /// <para>
    /// <b>A layer of its own, and not the one the rest of the suite queries.</b> This test was
    /// written against <c>GRATICULA_TEST_QUERYABLE</c> first and it passed in 946 milliseconds,
    /// which is the tell: that layer has eight rows and answers in 2.3 kilobytes. <b>A 2.3
    /// kilobyte body is one write.</b> It cannot be cut in half by anything, so the test would
    /// have passed identically under the alternative it exists to rule out — including on the
    /// day somebody implemented that alternative and broke every large response.
    /// </para>
    /// <para>
    /// Name a layer with tens of thousands of features and real geometry, e.g.
    /// <c>hosted/tr_il</c>, which answers 200 records in 342 kilobytes.
    /// </para>
    /// </remarks>
    public const string LargeLayerVariable = "GRATICULA_TEST_LARGE";

    /// <summary>
    /// The smallest body this test will accept as evidence.
    /// </summary>
    /// <remarks>
    /// <b>Asserted, because the alternative is a green test that proves nothing.</b> 64 kilobytes
    /// is past any single socket write and past Kestrel's response buffer, so a body this size
    /// reaches the client in pieces and a request abandoned midway leaves some of those pieces
    /// behind. Below it the assertion in this test is vacuous, and a vacuous assertion that
    /// reports success is worse than no test — so the size is checked and the failure says why.
    /// </remarks>
    private const int LeastMeaningfulBody = 64 * 1024;

    private static async Task<string> LayerAsync()
    {
        string? name = Environment.GetEnvironmentVariable(LargeLayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{LargeLayerVariable} is not set, so this test FAILS rather than skips. Name a "
            + "layer whose 200-record answer is larger than "
            + $"{LeastMeaningfulBody / 1024} kB, e.g. hosted/tr_il.");

        return name!.Trim('/');
    }

    /// <summary>
    /// Floods one data source and reads every body to its end.
    /// </summary>
    [Fact]
    public async Task Under_flood_every_answer_that_is_not_a_refusal_is_a_whole_document()
    {
        string root = await RequireServerAsync();
        string layer = await LayerAsync();

        // Enough rows and enough geometry that a response is worth truncating. A one-row
        // answer fits a single write and would pass this test under either alternative.
        Uri query = new(
            $"{root}/rest/services/{layer}/FeatureServer/0/query"
            + "?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=200&f=json");

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            MaxConnectionsPerServer = Callers,
        };

        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(60) };

        // One request before the gate opens, so the flood races for permits rather than for
        // sockets and a plan cache. The herd test learned this the hard way. It is also where
        // the body is measured, because a flood of small answers proves nothing.
        int warmBytes;
        int warmFeatures;

        using (HttpRequestMessage first = new(HttpMethod.Get, query))
        {
            await AuthenticateAsync(first, root);

            using HttpResponseMessage warm = await http.SendAsync(first);

            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);

            byte[] body = await warm.Content.ReadAsByteArrayAsync();
            warmBytes = body.Length;

            using JsonDocument document = JsonDocument.Parse(body);
            warmFeatures = document.RootElement.GetProperty("features").GetArrayLength();
        }

        Assert.True(
            warmBytes >= LeastMeaningfulBody,
            $"{LargeLayerVariable} names a layer answering in {warmBytes} bytes, which is one "
            + $"write. This test needs at least {LeastMeaningfulBody} so that a response cut "
            + "short would be a response missing bytes. Point it at a larger layer; a green "
            + "result from a body this size would mean nothing.");

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        List<Task<(HttpStatusCode Status, string Body)>> callers =
        [
            .. Enumerable.Range(0, Callers).Select(async _ =>
            {
                await gate.Task.ConfigureAwait(false);

                using HttpRequestMessage request = new(HttpMethod.Get, query);
                await AuthenticateAsync(request, root).ConfigureAwait(false);

                using HttpResponseMessage response = await http
                    .SendAsync(request)
                    .ConfigureAwait(false);

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                return (response.StatusCode, body);
            }),
        ];

        gate.SetResult();

        (HttpStatusCode Status, string Body)[] answers = await Task.WhenAll(callers);

        int refused = 0;
        int served = 0;

        foreach ((HttpStatusCode status, string body) in answers)
        {
            if (status == HttpStatusCode.ServiceUnavailable)
            {
                refused++;

                // A refusal is a document too. It is the one place ErrorResponse can answer
                // cleanly, and ADR-046 chose this design so that it always can.
                using JsonDocument document = JsonDocument.Parse(body);
                Assert.True(
                    document.RootElement.TryGetProperty("error", out _),
                    "A 503 under flood must carry an ArcGIS error object, because a client that "
                    + "cannot read it cannot tell shedding from breaking.");

                continue;
            }

            Assert.Equal(HttpStatusCode.OK, status);

            served++;

            // The assertion. Parsing is what a truncated body fails, and it fails at the end.
            using JsonDocument parsed = JsonDocument.Parse(body);

            Assert.True(
                parsed.RootElement.TryGetProperty("features", out JsonElement features),
                "An admitted query answered 200 without a features array, which is a response "
                + "that was cut short before its last property rather than one that was "
                + "refused.");

            Assert.Equal(JsonValueKind.Array, features.ValueKind);

            // <b>And the same number of features as the unloaded request.</b> Parsing catches a
            // body cut mid-token; it does not catch one cut at a comma, which is rarer and
            // still wrong. The count is the cheapest assertion that catches both.
            Assert.Equal(warmFeatures, features.GetArrayLength());
        }

        // Not an assertion about the ratio — see the class remarks — but the numbers belong in
        // the failure output of whatever assertion above breaks next.
        Assert.True(
            served + refused == Callers,
            $"{served} served, {refused} refused, of {Callers}.");
    }
}
