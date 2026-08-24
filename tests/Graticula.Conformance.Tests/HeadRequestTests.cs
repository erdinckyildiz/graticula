using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A resource that answers GET answers HEAD, on every face this server has.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-121](../../docs/architecture-debt.md), and it stopped being tidiness on 2026-08-20.</b>
/// The row was opened when ArcGIS Pro sent <c>HEAD</c> before its <c>GET</c> and read 405; it was
/// filed as harmless because Pro falls back. Then Pro's portal connection failed on the same
/// thing: it reads 405 as a dead end on a discovery probe, so a 405 on
/// <c>/sharing/rest/portals/self</c> stopped a connection whose GET answered 200 immediately
/// after.
/// </para>
/// <para>
/// <b>The repair is one middleware in front of routing, not sixty-three route registrations.</b>
/// The row's own prescription was *one call shape across the route table*, and that is the
/// version that goes wrong: it is right sixty-three times and then somebody adds the sixty-fourth
/// <c>MapGet</c>. So this test is written to be face-by-face rather than route-by-route — it
/// asserts the property, and the property holds for routes nobody has written yet.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class HeadRequestTests : ArcGisClient
{
    /// <summary>
    /// HEAD answers what GET answers, with the same status and no body.
    /// </summary>
    /// <remarks>
    /// <b>One address per face, chosen for being the first thing a client asks.</b> A discovery
    /// probe is what HEAD is for, and every one of these is a document a client reads before it
    /// reads anything else. The status is compared against the GET beside it rather than against
    /// 200, so an unauthenticated 401 or an absent 404 is a pass when GET says the same — what is
    /// under test is that the two agree, not what they agree on.
    /// </remarks>
    [Theory]
    [InlineData("/rest/info?f=json")]
    [InlineData("/rest/services?f=json")]
    [InlineData("/sharing/rest/portals/self?f=json")]
    [InlineData("/arcgisuris.xml")]
    [InlineData("/wms?service=WMS&request=GetCapabilities")]
    [InlineData("/wfs?service=WFS&request=GetCapabilities")]
    [InlineData("/healthz/live")]
    public async Task Head_answers_what_get_answers(string path)
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage get = await Http.GetAsync(new Uri(root + path));

        using HttpRequestMessage ask = new(HttpMethod.Head, root + path);
        using HttpResponseMessage head = await Http.SendAsync(ask);

        Assert.True(
            head.StatusCode == get.StatusCode,
            $"HEAD {path} answered {(int)head.StatusCode} where GET answered {(int)get.StatusCode}. "
            + "HTTP says a resource that answers GET answers HEAD, and ArcGIS Pro reads a 405 on a "
            + "discovery probe as a dead end — which is how this stopped being cosmetic.");

        byte[] body = await head.Content.ReadAsByteArrayAsync();

        Assert.True(
            body.Length == 0,
            $"HEAD {path} returned {body.Length} bytes. A HEAD response carries the headers a GET "
            + "would and no body.");
    }

    /// <summary>
    /// A layer's own document answers HEAD, which is the request Pro actually sends.
    /// </summary>
    /// <remarks>
    /// <b>Named separately because it is the row's opening sentence.</b> Pro sends
    /// <c>HEAD /rest/services/{name}/FeatureServer/0?f=json&amp;returnPbfFeatureEncodings=true</c>
    /// before the GET. It is found from the catalogue rather than hard-coded, because a fixture
    /// name here would make the test about this deployment.
    /// </remarks>
    [Fact]
    public async Task A_layers_document_answers_head_the_way_arcgis_pro_asks_for_it()
    {
        string root = await RequireServerAsync();
        string? service = await AnyServiceNameAsync();

        Assert.False(service is null, "No FeatureServer to ask about.");

        string path =
            $"/rest/services/{service}/FeatureServer/0?f=json&returnPbfFeatureEncodings=true";

        using HttpResponseMessage get = await Http.GetAsync(new Uri(root + path));

        using HttpRequestMessage ask = new(HttpMethod.Head, root + path);
        using HttpResponseMessage head = await Http.SendAsync(ask);

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// A route with no GET still refuses HEAD, which is the correct answer.
    /// </summary>
    /// <remarks>
    /// <b>The other half of the repair, and the one a blanket fix gets wrong.</b> Making every
    /// route answer HEAD would mean answering it where there is nothing to answer with: sign-in
    /// is a POST, and a HEAD that returned 200 there would tell a client the credential was
    /// accepted. The rewrite offers the request to the route table as a GET, so a route with no
    /// GET answers 405 exactly as before.
    /// </remarks>
    [Fact]
    public async Task A_route_that_only_takes_post_still_refuses_head()
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage ask = new(HttpMethod.Head, root + "/rest/auth/login");
        using HttpResponseMessage head = await Http.SendAsync(ask);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, head.StatusCode);
    }

    /// <summary>
    /// The access log records the method the client sent.
    /// </summary>
    /// <remarks>
    /// <b>Because the repair rewrites it, and a log that answered *what did they ask for* with
    /// the rewritten method would answer a different question.</b> The rewrite happens in front of
    /// routing and the request log runs behind it, so the method it would naturally read is the
    /// GET this invented. The middleware remembers what arrived; this is the check that the
    /// remembering works, from outside.
    /// </remarks>
    [Fact]
    public async Task The_log_records_head_rather_than_the_get_it_was_rewritten_to()
    {
        string root = await RequireServerAsync();

        string? file = Environment.GetEnvironmentVariable("GRATICULA_TEST_LOG");

        Assert.False(
            string.IsNullOrWhiteSpace(file),
            "GRATICULA_TEST_LOG is not set, so this suite cannot read what the server wrote.");

        string mark = "zz_head_log_" + Guid.NewGuid().ToString("N")[..8];

        using HttpRequestMessage ask = new(HttpMethod.Head, $"{root}/rest/info?f=json&probe={mark}");
        using HttpResponseMessage head = await Http.SendAsync(ask);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        string? line = null;

        // The log is written from a queue and never awaited by a request — ADR-045 condition 1 —
        // so the line arrives shortly after the answer rather than with it.
        for (int attempt = 0; attempt < 30 && line is null; attempt++)
        {
            await Task.Delay(200);

            foreach (string written in await ReadLinesAsync(file!))
            {
                if (written.Contains(mark, StringComparison.Ordinal))
                {
                    line = written;
                    break;
                }
            }
        }

        Assert.False(line is null, $"Nothing in the log mentions {mark} after six seconds.");

        Assert.Contains("HEAD ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("GET /rest/info", line, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<string>> ReadLinesAsync(string file)
    {
        // Opened share-all, because the server holds it open and is writing to it.
        await using System.IO.FileStream stream = new(
            file,
            System.IO.FileMode.Open,
            System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);

        using System.IO.StreamReader reader = new(stream);

        List<string> lines = [];

        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
