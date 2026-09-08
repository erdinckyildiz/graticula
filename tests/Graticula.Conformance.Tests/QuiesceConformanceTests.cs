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
    /// A quiesced datastore refuses the endpoints that build and drop its own tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-059](../../docs/adr/ADR-059-quiescing-a-data-source.md) §5g, and this test exists
    /// because the ADR asserted the behaviour before anybody measured it.</b> §5d said <i>the
    /// datastore can be quiesced too... ADR-058's field endpoints go through the same pool</i>.
    /// They do not. <c>LayerConnections</c> holds a pool per registered source and gates every
    /// hand-out; the datastore reaches PostGIS through a <b>second</b> pool — the keyed
    /// <c>datastore</c> data source — which that gate never sees.
    /// </para>
    /// <para>
    /// <b>Measured 2026-09-08 against a running server with the datastore quiesced</b>, before
    /// the gate was written: <c>/admin/hosted/define</c> answered <b>201</b> and created
    /// <c>hosted.zzzquiescedefine_b34d8e52</c>; <c>/admin/hosted/import</c> answered <b>201</b>,
    /// created a table and inserted a row; and <c>?drop=true</c> answered <b>200</b> and
    /// <b>dropped a table</b>. A read of the same database was refused with 503 throughout.
    /// </para>
    /// <para>
    /// <b>Why <c>define</c> and <c>drop</c> rather than the field endpoints.</b> Adding a field
    /// was refused even before the gate — but incidentally, because it reads the column list
    /// through the gated pool first and never reaches the DDL. An accident that holds today is
    /// not a guarantee, and it is invisible to a test that only checks the outcome. These two
    /// have no such read in front of them: they are the paths that were genuinely open.
    /// </para>
    /// <para>
    /// <b>And the key is what this really pins.</b> A quiesce is keyed by the source's
    /// connection string; the datastore's row is registered with one expression and the gate is
    /// wired with the same one. Quiescing here by <i>id</i>, through the admin route, is what
    /// makes a future divergence between those two expressions fail a test rather than silently
    /// reopen the hole.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_quiesced_datastore_refuses_to_create_or_drop_its_own_tables()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (HttpStatusCode listed, string sources) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/datasources", token!, null);

        Assert.Equal(HttpStatusCode.OK, listed);

        Guid datastore = JsonDocument.Parse(sources).RootElement
            .GetProperty("dataSources").EnumerateArray()
            .First(d => string.Equals(
                d.GetProperty("name").GetString(), "datastore", StringComparison.Ordinal))
            .GetProperty("id").GetGuid();

        string name = $"ZZZQuiesceDdl{Guid.NewGuid():N}"[..24];

        string define = JsonSerializer.Serialize(new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "text" } },
        });

        (HttpStatusCode held, string what) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/datasources/{datastore}/quiesce",
            token!,
            JsonSerializer.Serialize(new { seconds = 120, why = "a DDL conformance run" }));

        Assert.True(held == HttpStatusCode.OK, $"Quiescing answered {(int)held}: {what}");

        HttpStatusCode created;
        string said;

        try
        {
            (created, said) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/hosted/define", token!, define);

            Assert.True(
                created == HttpStatusCode.ServiceUnavailable,
                $"With the datastore quiesced, /admin/hosted/define answered {(int)created} "
                + $"rather than 503: {said}. An operator has told this server to stay off that "
                + "database while a DBA works on it, and a 201 here means it created a table in "
                + "the middle of that window.");

            Assert.Contains("a DDL conformance run", said, StringComparison.Ordinal);
        }
        finally
        {
            (HttpStatusCode back, string done) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/datasources/{datastore}/quiesce", token!, null);

            Assert.True(back == HttpStatusCode.OK, $"Resuming answered {(int)back}: {done}");
        }

        // <b>The other half, and it is the destructive one.</b> `?drop=true` reaches
        // `PostGisImporter.DropAsync`, so a quiesce that did not cover this endpoint was a
        // quiesce during which this server would issue `drop table` against the database an
        // operator had just taken out of service. Created while answering, dropped while
        // answering, so what the middle assertion measures is the refusal and nothing else.
        (HttpStatusCode made, string made_said) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/hosted/define", token!, define);

        Assert.True(
            made == HttpStatusCode.Created,
            $"After resuming, /admin/hosted/define answered {(int)made}: {made_said}");

        string drop = $"{root}/admin/featureservices/{name}?folder=hosted&drop=true";

        (held, what) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/datasources/{datastore}/quiesce",
            token!,
            JsonSerializer.Serialize(new { seconds = 120, why = "a DDL conformance run" }));

        Assert.True(held == HttpStatusCode.OK, $"Quiescing answered {(int)held}: {what}");

        try
        {
            (HttpStatusCode dropped, string refused) = await RequestAsync(
                HttpMethod.Delete, drop, token!, null);

            Assert.True(
                dropped == HttpStatusCode.ServiceUnavailable,
                $"With the datastore quiesced, dropping a hosted table answered {(int)dropped} "
                + $"rather than 503: {refused}. This is the destructive one, and it has two ways "
                + "to be wrong. Either `drop table` ran inside the window an operator opened for "
                + "a DBA — or, worse and quieter, the layer was unpublished and the drop failed "
                + "per-layer, leaving an orphaned table, an emptied catalogue, and a 200 saying "
                + "it went well.");
        }
        finally
        {
            await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/datasources/{datastore}/quiesce", token!, null);

            // Whatever the assertions did, the fixture does not keep the table.
            await RequestAsync(HttpMethod.Delete, drop, token!, null);
        }
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
