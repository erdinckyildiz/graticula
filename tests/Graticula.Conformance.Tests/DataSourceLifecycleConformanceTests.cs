using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A registered database can be corrected and removed, and both refuse what would break something.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written the day the owner found the gap:</b> *"registered db path'ini güncelleyemiyorum
/// sanırım… path derken connection string."* `POST` and `GET` existed and nothing else, so a moved
/// host or a rotated password meant registering a second source and republishing every layer on the
/// first. [ADR-017](../../docs/adr/ADR-017-admin-api.md) §3.3 records the two operations that closed
/// it.
/// </para>
/// <para>
/// <b>The refusals are the subject, not the happy path.</b> A `PUT` that writes whatever it is given
/// is easy and wrong: pointing a source at a database that does not hold its layers' tables breaks
/// every service on it, and the symptom arrives later as 503s on services that were working. So what
/// is asserted here is that the server looked — connected, listed, compared — before it wrote.
/// </para>
/// <para>
/// <b>Its own source, never a fixture of somebody else's.</b> Each test registers `zz_ds_*`, does its
/// work and removes it. An update seals a new secret and the old one cannot be read back, so
/// exercising this against a real registration would risk breaking a layer with no undo.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, because publishing a layer puts a service in the
/// directory and three other classes walk it — D-111, found by five failures that named a fixture
/// which had already been removed.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class DataSourceLifecycleConformanceTests : ArcGisClient
{
    /// <summary>The datastore's own database, which is the one connection a test can be sure of.</summary>
    /// <remarks>
    /// <b>Read from the suite's own configuration rather than hard-coded.</b> `GRATICULA_TEST_PG` is
    /// what every other database-touching suite uses; a literal here would pass on this machine and
    /// fail on any other, which is worse than skipping.
    /// </remarks>
    private static string? Connection =>
        Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");

    private async Task<(string Token, string Id)> RegisterAsync(string name)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        Assert.False(
            string.IsNullOrWhiteSpace(Connection),
            "GRATICULA_TEST_PG is not set, so these tests FAIL rather than skip. They need one "
            + "connection string they can point a source at.");

        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/datasources", token!,
            $"{{\"name\":\"{name}\",\"connectionString\":{JsonSerializer.Serialize(Connection)}}}");

        Assert.True(
            status == HttpStatusCode.OK || status == HttpStatusCode.Created,
            $"Registering the fixture source answered {(int)status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        return (token!, id);
    }

    private async Task RemoveAsync(string token, string id)
    {
        string root = await RequireServerAsync();

        await SendAsync(HttpMethod.Delete, $"{root}/admin/datasources/{id}", token, null);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_reached_is_refused_before_anything_is_written()
    {
        string root = await RequireServerAsync();
        (string token, string id) = await RegisterAsync("zz_ds_unreachable");

        try
        {
            (HttpStatusCode status, string body) = await SendAsync(
                HttpMethod.Put, $"{root}/admin/datasources/{id}", token,
                "{\"connectionString\":\"Host=doesnotexist.invalid;Port=5432;Database=gis;"
                + "Username=gis;Password=gis\"}");

            Assert.Equal(HttpStatusCode.BadRequest, status);

            // <b>D-102: this answered *the reason is in the server log* until 2026-08-19</b>, because
            // an unresolvable host throws `SocketException` bare and the probe caught only
            // `NpgsqlException`. The assertion is that the operator is told what to check.
            Assert.Contains("host", Message(body), StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                "is in the server log", Message(body), StringComparison.OrdinalIgnoreCase);

            // And the stored connection is untouched: the summary still names the database it had.
            Assert.Contains("gis", await SummaryAsync(root, token, id), StringComparison.Ordinal);
        }
        finally
        {
            await RemoveAsync(token, id);
        }
    }

    /// <summary>
    /// Pointing a source somewhere its layers' tables are not is refused, and they are named.
    /// </summary>
    /// <remarks>
    /// <b>The case this endpoint exists to protect against.</b> The common reason to change a
    /// connection string is the same database at a new address, where nothing is missing — so a
    /// mismatch means the operator has pointed it at the wrong database, and finding that out one 503
    /// at a time is the outcome worth spending a probe to avoid.
    /// </remarks>
    [Fact]
    public async Task Pointing_a_source_where_its_tables_are_not_is_refused_and_names_them()
    {
        string root = await RequireServerAsync();
        (string token, string id) = await RegisterAsync("zz_ds_elsewhere");

        try
        {
            (HttpStatusCode published, string layer) = await SendAsync(
                HttpMethod.Post, $"{root}/admin/layers", token,
                $$"""
                  {"name":"zz_ds_elsewhere_layer","dataSourceId":"{{id}}","schemaName":"public",
                   "tableName":"spatial_ref_sys","geometryColumn":"srtext","identityColumn":"srid",
                   "objectIdColumn":"srid","srid":4326,"geometryType":"Point","sharing":"private"}
                  """);

            // <b>Any table will do, and this one is in every PostGIS database.</b> What is under test is
            // the *comparison*, not the layer — it is never queried.
            Assert.True(
                published is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Publishing the fixture layer answered {(int)published}: {layer}");

            try
            {
                // `postgres` is in every PostgreSQL cluster and holds none of our tables.
                string elsewhere = Connection!.Replace(
                    "Database=gis", "Database=postgres", StringComparison.OrdinalIgnoreCase);

                (HttpStatusCode status, string body) = await SendAsync(
                    HttpMethod.Put, $"{root}/admin/datasources/{id}", token,
                    $"{{\"connectionString\":{JsonSerializer.Serialize(elsewhere)}}}");

                Assert.Equal(HttpStatusCode.Conflict, status);

                // The layer is named, because *some layers would break* is not actionable.
                Assert.Contains("zz_ds_elsewhere_layer", Message(body), StringComparison.Ordinal);

                // And it says how to proceed deliberately.
                Assert.Contains("force=true", Message(body), StringComparison.Ordinal);

                (HttpStatusCode forced, _) = await SendAsync(
                    HttpMethod.Put, $"{root}/admin/datasources/{id}?force=true", token,
                    $"{{\"connectionString\":{JsonSerializer.Serialize(elsewhere)}}}");

                Assert.Equal(HttpStatusCode.OK, forced);
            }
            finally
            {
                await SendAsync(
                    HttpMethod.Delete, $"{root}/admin/layers/zz_ds_elsewhere_layer", token, null);
            }
        }
        finally
        {
            await RemoveAsync(token, id);
            await SendAsync(HttpMethod.Post, $"{root}/admin/featureservices/sweep", token, "{}");
        }
    }

    [Fact]
    public async Task A_source_with_layers_on_it_is_not_removed()
    {
        string root = await RequireServerAsync();
        (string token, string id) = await RegisterAsync("zz_ds_held");

        try
        {
            await SendAsync(
                HttpMethod.Post, $"{root}/admin/layers", token,
                $$"""
                  {"name":"zz_ds_held_layer","dataSourceId":"{{id}}","schemaName":"public",
                   "tableName":"spatial_ref_sys","geometryColumn":"srtext","identityColumn":"srid",
                   "objectIdColumn":"srid","srid":4326,"geometryType":"Point","sharing":"private"}
                  """);

            (HttpStatusCode status, string body) = await SendAsync(
                HttpMethod.Delete, $"{root}/admin/datasources/{id}", token, null);

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Contains("layer", Message(body), StringComparison.OrdinalIgnoreCase);

            await SendAsync(HttpMethod.Delete, $"{root}/admin/layers/zz_ds_held_layer", token, null);

            // And once nothing is on it, it goes.
            (HttpStatusCode removed, _) = await SendAsync(
                HttpMethod.Delete, $"{root}/admin/datasources/{id}", token, null);

            Assert.Equal(HttpStatusCode.OK, removed);
        }
        finally
        {
            await RemoveAsync(token, id);
            await SendAsync(HttpMethod.Post, $"{root}/admin/featureservices/sweep", token, "{}");
        }
    }

    /// <summary>
    /// The datastore is not removable, whatever its layer count says.
    /// </summary>
    /// <remarks>
    /// <b>Asserted rather than trusted to the layer check</b>, because the two reasons are different: a
    /// registered source with no layers is a tidy-up, and the datastore with no layers is still where
    /// every future import will go. A server whose datastore row is gone cannot publish anything hosted
    /// and has no route back through the API.
    /// </remarks>
    [Fact]
    public async Task The_datastore_cannot_be_removed()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (_, string listed) = await SendAsync(
            HttpMethod.Get, $"{root}/admin/datasources", token!, null);

        string? datastore = null;

        foreach (JsonElement source in
                 JsonDocument.Parse(listed).RootElement.GetProperty("dataSources").EnumerateArray())
        {
            if (source.GetProperty("name").GetString() == "datastore")
            {
                datastore = source.GetProperty("id").GetString();
            }
        }

        Assert.False(datastore is null, "The datastore is not registered, which is a different fault.");

        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Delete, $"{root}/admin/datasources/{datastore}", token!, null);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("datastore", Message(body), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A source's connection comes back to be corrected, with the password taken out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because *edit* opened an empty box.</b> The form asked for the whole connection string
    /// and gave nothing to start from, on the reasoning that the stored one is sealed and cannot
    /// be merged into — true of the password and of nothing else. The host, the port, the
    /// database and the user are the thing being corrected, and retyping them from memory is how
    /// one correction becomes two mistakes. Found 2026-09-05 by the owner pressing Edit.
    /// </para>
    /// <para>
    /// <b>The keyword, not the value, is what is asserted absent.</b> This suite's fixture
    /// password is short and also appears in the host and the database name, so *the answer does
    /// not contain the password* is unprovable here — the same trap the summary test below
    /// records falling into. What the builder guarantees is that no password keyword is emitted,
    /// and that holds against any credential.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_sources_connection_is_read_back_without_its_password()
    {
        string root = await RequireServerAsync();
        (string token, string id) = await RegisterAsync("zz_ds_readback");

        try
        {
            (HttpStatusCode status, string body) = await SendAsync(
                HttpMethod.Get, $"{root}/admin/datasources/{id}/connection", token, null);

            Assert.Equal(HttpStatusCode.OK, status);

            string connection = JsonDocument.Parse(body).RootElement
                .GetProperty("connection").GetString() ?? string.Empty;

            // <b>Enough to correct without retyping.</b> An answer with only the host would be an
            // empty box with extra steps.
            Assert.Contains("Host=", connection, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Database=", connection, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Username=", connection, StringComparison.OrdinalIgnoreCase);

            // <b>And the half that must never come back.</b> Returning it would let anybody with
            // `content:registerDataStore` read a credential they were never shown; keeping it
            // server-side and merging would let them repoint the source at a listener of their
            // own and have this server deliver it. Neither: it is typed again.
            Assert.DoesNotContain("Password", connection, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RemoveAsync(token, id);
        }
    }

    /// <summary>
    /// The datastore's connection is configuration, so it is neither read back nor changed here.
    /// </summary>
    /// <remarks>
    /// <b>Because changing it worked, said so, and was undone by the next restart.</b>
    /// `EnsureDatastoreAsync` runs on every start and upserts that row from
    /// `Graticula:PlatformStore`, so the update path accepted a change it could not keep. Removal
    /// has refused the datastore since ADR-019 gave it a reserved name and the update path did
    /// not — one rule, two doors, one guard, which is D-46's subject. A control that reports
    /// success and reverts is worse than either a working one or an absent one.
    /// </remarks>
    [Fact]
    public async Task The_datastores_connection_is_neither_read_back_nor_changed()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (_, string listed) = await SendAsync(
            HttpMethod.Get, $"{root}/admin/datasources", token!, null);

        string? datastore = null;

        foreach (JsonElement source in
                 JsonDocument.Parse(listed).RootElement.GetProperty("dataSources").EnumerateArray())
        {
            if (source.GetProperty("name").GetString() == "datastore")
            {
                datastore = source.GetProperty("id").GetString();
            }
        }

        Assert.False(datastore is null, "The datastore is not registered, which is a different fault.");

        (HttpStatusCode read, string why) = await SendAsync(
            HttpMethod.Get, $"{root}/admin/datasources/{datastore}/connection", token!, null);

        Assert.Equal(HttpStatusCode.Conflict, read);
        Assert.Contains("PlatformStore", Message(why), StringComparison.Ordinal);

        // <b>The same string it is already on, so a missing guard would succeed rather than
        // break the fixture.</b> A test that proves a refusal by asking for something harmful is
        // a test that does the harm on the day the refusal is gone.
        (HttpStatusCode changed, string said) = await SendAsync(
            HttpMethod.Put,
            $"{root}/admin/datasources/{datastore}",
            token!,
            JsonSerializer.Serialize(new { connectionString = Connection }));

        Assert.Equal(HttpStatusCode.Conflict, changed);
        Assert.Contains("PlatformStore", Message(said), StringComparison.Ordinal);
    }

    /// <summary>The listing says which database each source is on, and never the credential.</summary>
    /// <remarks>
    /// <b>The privacy half is the assertion that matters.</b> `summary` exists so a screen can show
    /// *which* database without showing how to reach it; a listing that leaked the password would be a
    /// new place to steal from, which is what `IAdminCatalog`'s own remarks say.
    /// </remarks>
    [Fact]
    public async Task The_listing_names_the_database_and_not_the_password()
    {
        string root = await RequireServerAsync();
        (string token, string id) = await RegisterAsync("zz_ds_summary");

        try
        {
            string summary = await SummaryAsync(root, token, id);

            Assert.False(string.IsNullOrWhiteSpace(summary), "The listing carried no summary.");

            (_, string listed) = await SendAsync(
                HttpMethod.Get, $"{root}/admin/datasources", token, null);

            // <b>The shape, not a substring search for the password — which is what the first version
            // did, and it failed.</b> This suite's fixture password is `gis`, three characters that
            // also appear in the database name and the host, so *the answer does not contain the
            // password* is unprovable here and would have been a test that only passed against a
            // distinctive credential. What `Summarise` actually guarantees is a shape: host, port and
            // database, and nothing else. That is checkable against any credential.
            Assert.Matches(@"^[^;\s]+:\d+/[^;\s]+$", summary);

            Assert.DoesNotContain("Password", listed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Username", listed, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RemoveAsync(token, id);
        }
    }

    private async Task<string> SummaryAsync(string root, string token, string id)
    {
        (_, string listed) = await SendAsync(HttpMethod.Get, $"{root}/admin/datasources", token, null);

        foreach (JsonElement source in
                 JsonDocument.Parse(listed).RootElement.GetProperty("dataSources").EnumerateArray())
        {
            if (source.GetProperty("id").GetString() == id)
            {
                return source.TryGetProperty("summary", out JsonElement summary)
                    ? summary.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string Message(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;

            return root.TryGetProperty("error", out JsonElement error)
                   && error.TryGetProperty("message", out JsonElement said)
                ? said.GetString() ?? body
                : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string url, string token, string? json)
    {
        using HttpRequestMessage request = new(method, url);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
