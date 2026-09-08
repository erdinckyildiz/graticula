using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A quiesced data source refuses, says why, and answers again by itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md) condition 1.</b> The point of
/// quiesce is not the refusal — it is that this server's connections are gone, so a DBA's
/// <c>ALTER TABLE</c> completes instead of waiting behind a held read.
/// [D-08](../../docs/architecture-debt.md) measured the shape it exists to prevent: a 296 ms read
/// holding its pooled connection for 30.30 s behind a lock, with every request for that source
/// queued behind the waiting DDL.
/// </para>
/// <para>
/// <b>Asserted over HTTP, like everything in this suite.</b> What a DDL does while the source is
/// quiesced is measured separately, in the Postgres tests, where a second connection can hold a
/// lock; here the assertions are the ones a client can see — the refusal, its sentence, its
/// <c>Retry-After</c>, and the source answering again after a resume.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class QuiesceConformanceTests : ArcGisClient
{
    /// <summary>
    /// Quiescing refuses the layers over that source, and resuming brings them back.
    /// </summary>
    [Fact]
    public async Task A_quiesced_source_refuses_with_a_sentence_and_comes_back_on_resume()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        // <b>The datastore, because every fixture has one and it is a data source like any
        // other</b> — ADR-059 §5d. Quiescing it takes out the hosted layers this suite serves,
        // which is exactly the blast radius the test needs to observe.
        (HttpStatusCode listed, string sources) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/datasources", token!, null);

        Assert.Equal(HttpStatusCode.OK, listed);

        Guid datastore = JsonDocument.Parse(sources).RootElement
            .GetProperty("dataSources").EnumerateArray()
            .First(d => string.Equals(
                d.GetProperty("name").GetString(), "datastore", StringComparison.Ordinal))
            .GetProperty("id").GetGuid();

        string layer = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "";

        Assert.False(
            string.IsNullOrWhiteSpace(layer),
            "GRATICULA_TEST_QUERYABLE names no layer, so there is nothing to see refused.");

        string query =
            $"{root}/rest/services/{layer}/FeatureServer/0/query"
            + "?where=1%3D1&returnCountOnly=true&f=json";

        // Answering before, so the refusal afterwards is attributable.
        (HttpStatusCode before, _) = await RequestAsync(HttpMethod.Get, query, token!, null);

        Assert.Equal(HttpStatusCode.OK, before);

        (HttpStatusCode held, string what) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/datasources/{datastore}/quiesce",
            token!,
            JsonSerializer.Serialize(new { seconds = 120, why = "a conformance run" }));

        Assert.True(held == HttpStatusCode.OK, $"Quiescing answered {(int)held}: {what}");

        try
        {
            JsonElement said = JsonDocument.Parse(what).RootElement;

            Assert.True(said.GetProperty("quiesced").GetBoolean());
            Assert.NotEqual(default, said.GetProperty("until").GetDateTimeOffset());

            using HttpRequestMessage asking = new(HttpMethod.Get, query);
            asking.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!);

            using HttpResponseMessage refused = await Http.SendAsync(asking);

            Assert.True(
                refused.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"A quiesced source answered {(int)refused.StatusCode} rather than 503.");

            string body = await refused.Content.ReadAsStringAsync();

            // <b>ADR-059 §5e: who, why and until when.</b> A planned, self-ending unavailability
            // that reads like a network fault sends whoever is on call to the wrong place at the
            // one moment somebody already knows the answer.
            Assert.Contains("a conformance run", body, StringComparison.Ordinal);

            Assert.DoesNotContain(
                "healthz",
                body,
                StringComparison.OrdinalIgnoreCase);

            // <b>`Retry-After`, and it is a fact rather than an estimate.</b> This is one of the
            // two refusals whose end this server sets itself, which is the rule
            // `ErrorResponse.RetryAfterFor` states for when it answers at all.
            Assert.True(
                refused.Headers.RetryAfter?.Delta is { } wait && wait > TimeSpan.Zero,
                "A quiesced source did not say when it would answer again, though it is the one "
                + "refusal whose end this server is holding.");
        }
        finally
        {
            (HttpStatusCode back, string done) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/datasources/{datastore}/quiesce", token!, null);

            Assert.True(back == HttpStatusCode.OK, $"Resuming answered {(int)back}: {done}");
        }

        // <b>And the pool is rebuilt on the next request rather than by resuming.</b> Nothing is
        // reopened eagerly — that is the path a cold start takes — so this assertion is what says
        // the rebuild happens at all.
        (HttpStatusCode after, string answered) = await RequestAsync(
            HttpMethod.Get, query, token!, null);

        Assert.True(
            after == HttpStatusCode.OK,
            $"After resuming, the layer answered {(int)after} rather than 200: {answered}");
    }

    /// <summary>
    /// Resuming something that was not quiesced is an answer, not an error.
    /// </summary>
    /// <remarks>
    /// <b>A window that ended by itself leaves nothing to resume</b>, and an operator pressing
    /// Resume after lunch has done nothing wrong. Answering 404 would tell them the source is
    /// missing, which is a different and much more alarming fact.
    /// </remarks>
    [Fact]
    public async Task Resuming_a_source_that_was_answering_says_so_rather_than_failing()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (HttpStatusCode listed, string sources) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/datasources", token!, null);

        Guid any = JsonDocument.Parse(sources).RootElement
            .GetProperty("dataSources").EnumerateArray()
            .First().GetProperty("id").GetGuid();

        (HttpStatusCode status, string said) = await RequestAsync(
            HttpMethod.Delete, $"{root}/admin/datasources/{any}/quiesce", token!, null);

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement answer = JsonDocument.Parse(said).RootElement;

        Assert.False(answer.GetProperty("quiesced").GetBoolean());
        Assert.False(answer.GetProperty("wasQuiesced").GetBoolean());
    }

    /// <summary>
    /// A source that does not exist is refused by id.
    /// </summary>
    [Fact]
    public async Task Quiescing_a_source_this_server_does_not_have_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (HttpStatusCode status, _) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/datasources/{Guid.NewGuid()}/quiesce",
            token!,
            JsonSerializer.Serialize(new { seconds = 60 }));

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
