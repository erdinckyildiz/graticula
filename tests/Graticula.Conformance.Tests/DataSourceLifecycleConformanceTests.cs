using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Linq;
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

    /// <summary>
    /// The fields are assembled here, and a wrong password is an answer rather than a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two things this endpoint exists for, and both are asserted over real HTTP.</b> It
    /// fills the console's database combo, and filling it is the test of everything above it —
    /// so a right credential has to come back with names in it and a wrong one has to come back
    /// with a sentence about the credential rather than about this server.
    /// </para>
    /// <para>
    /// <b>200 either way, and that is the part worth pinning.</b> *Your password is wrong* is the
    /// answer to the question the browser asked, not a failure to answer it; a 401 here would be
    /// indistinguishable in a console from its own session having expired, and would send an
    /// operator to sign in again over a typo in someone else's database password.
    /// </para>
    /// <para>
    /// <b>And the request carries fields rather than a connection string</b>, which is the shape
    /// the dialog sends: a password containing a semicolon has to be quoted into an Npgsql
    /// string, and a browser doing that quoting would be a second implementation of a rule this
    /// server already owns. The password used here has one in it for exactly that reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_databases_on_a_server_are_listed_from_the_fields_that_reach_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        Assert.False(
            string.IsNullOrWhiteSpace(Connection),
            "GRATICULA_TEST_PG is not set, so these tests FAIL rather than skip.");

        string asked = JsonSerializer.Serialize(new
        {
            host = Field("Host"),
            port = int.TryParse(Field("Port"), out int port) ? port : 5432,
            username = Field("Username"),
            password = Field("Password"),
        });

        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/datasources/databases", token!, asked);

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement said = JsonDocument.Parse(body).RootElement;

        Assert.Equal("Usable", said.GetProperty("outcome").GetString());

        string[] names = [.. said.GetProperty("databases").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)];

        Assert.True(
            names.Length > 0,
            $"The server answered with no databases at all, on a connection this suite is "
            + $"already using: {body}");

        Assert.Contains(
            Field("Database"),
            names,
            StringComparer.Ordinal);

        // <b>Sorted, because a combo that reorders itself between two looks is a combo nobody
        // can find anything in.</b>
        Assert.Equal([.. names.OrderBy(x => x, StringComparer.Ordinal)], names);

        // <b>The other end, and it answers 200 with a sentence about the credential.</b>
        (HttpStatusCode refused, string why) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/datasources/databases", token!,
            JsonSerializer.Serialize(new
            {
                host = Field("Host"),
                port = int.TryParse(Field("Port"), out int again) ? again : 5432,
                username = Field("Username"),
                password = "not;the;password",
            }));

        Assert.Equal(HttpStatusCode.OK, refused);

        JsonElement second = JsonDocument.Parse(why).RootElement;

        Assert.Equal("CannotConnect", second.GetProperty("outcome").GetString());
        Assert.Empty(second.GetProperty("databases").EnumerateArray());

        Assert.False(
            string.IsNullOrWhiteSpace(second.GetProperty("message").GetString()),
            "A refusal with no sentence in it leaves the operator with a combo that does nothing "
            + "and no reason for it.");
    }

    /// <summary>
    /// A request naming both a string and the fields is refused rather than half read.
    /// </summary>
    /// <remarks>
    /// <b>A caller that sends both believes two different things about what it is asking for.</b>
    /// Preferring one silently is how an operator corrects a source that was never the one on
    /// screen — so the server says which two it was given instead of choosing.
    /// </remarks>
    [Fact]
    public async Task A_request_carrying_both_a_string_and_the_fields_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Post, $"{root}/admin/datasources/test", token!,
            JsonSerializer.Serialize(new
            {
                connectionString = "Host=one.example;Database=gis;Username=gis",
                host = "two.example",
                username = "gis",
                database = "gis",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not both", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A service that names a reference answers in it, and clearing it goes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-057](../../docs/adr/ADR-057-composing-and-publishing-a-service.md) §5c.</b> Every
    /// face answered in whatever the layer's table happened to be stored in, so an operator
    /// composing a service out of tables in three systems had no way to say which one clients
    /// get. Migration 39 gave the service a reference of its own; this is the half that makes
    /// it mean something.
    /// </para>
    /// <para>
    /// <b>Three states, because two of them are easy to get right by accident.</b> Unset must
    /// behave exactly as it did before — that is what stops this being a change to every
    /// service that already exists. Set must reproject with no <c>outSR</c> in the request at
    /// all, which is the whole point. And an explicit <c>outSR</c> must still win, because a
    /// client that names a reference is not asking the service's opinion.
    /// </para>
    /// <para>
    /// <b>Restored in a finally.</b> This writes to a shared fixture, and a service left
    /// answering in 4326 would be a puzzle for whichever test runs next.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_service_that_names_a_reference_is_answered_in_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE")
            ?? string.Empty;

        Assert.False(
            string.IsNullOrWhiteSpace(qualified),
            "GRATICULA_TEST_QUERYABLE is not set, so there is no service to name a reference on.");
        string bare = qualified.Contains('/', StringComparison.Ordinal)
            ? qualified[(qualified.LastIndexOf('/') + 1)..]
            : qualified;

        // <b>What it answers with nothing set, which is the control.</b>
        int stored = await ServedWkidAsync(root, token!, qualified);

        Assert.True(
            stored > 0,
            $"'{qualified}' answered no spatial reference at all, so there is nothing to "
            + "compare against.");

        // Something it is definitely not. Both are references this server can reach.
        int other = stored == 4326 ? 3857 : 4326;

        try
        {
            (HttpStatusCode set, string said) = await SendAsync(
                HttpMethod.Put, $"{root}/admin/services/{bare}/srid", token!,
                JsonSerializer.Serialize(new { srid = other }));

            Assert.True(
                set == HttpStatusCode.OK,
                $"Naming a reference answered {(int)set}: {said}");

            Assert.Equal(
                other,
                await ServedWkidAsync(root, token!, qualified));

            // <b>And the document says the same thing, which is the invariant this whole
            // feature was held back over.</b> Measured 2026-09-05 with only the query moved:
            // document 3857, query 4326, same layer — a client reads the contract and is handed
            // something else. Asserting the query alone would let that come back silently.
            (int documentWkid, double west, double east) =
                await DocumentReferenceAsync(root, token!, $"{qualified}/FeatureServer/0");

            Assert.Equal(other, documentWkid);

            // <b>And the service document, which is the one a client reads first.</b> It was
            // the half still disagreeing after the layer document was moved: three documents,
            // one face, and two of them agreeing is not agreement.
            (int serviceWkid, _, double serviceEast) =
                await DocumentReferenceAsync(root, token!, $"{qualified}/FeatureServer");

            Assert.Equal(other, serviceWkid);

            // <b>Moved, not relabelled.</b> The numbers have to belong to the reference the
            // label names: degrees are single or double digits here and Web Mercator metres are
            // in the millions, so one comparison tells them apart without pinning the fixture's
            // own coordinates.
            bool degrees = other == 4326;

            Assert.True(
                degrees
                    ? System.Math.Abs(serviceEast) <= 180
                    : System.Math.Abs(serviceEast) > 1000,
                $"The service document reports EPSG:{other} and an extent reaching "
                + $"{serviceEast}, which belongs to the other reference.");

            Assert.True(
                degrees ? System.Math.Abs(east) <= 180 : System.Math.Abs(east) > 1000,
                $"The document reports EPSG:{other} and an extent running {west} to {east}. "
                + "Those numbers belong to the other reference, so the label was changed and the "
                + "box was not — which is the one failure worse than reporting nothing.");

            // <b>And a client that names one still wins.</b> The service's reference is the
            // answer when nobody asked, not an override of somebody who did.
            Assert.Equal(
                stored,
                await ServedWkidAsync(root, token!, qualified, $"&outSR={stored}"));
        }
        finally
        {
            await SendAsync(
                HttpMethod.Put, $"{root}/admin/services/{bare}/srid", token!,
                JsonSerializer.Serialize(new { srid = (int?)null }));
        }

        Assert.Equal(stored, await ServedWkidAsync(root, token!, qualified));
    }

    /// <summary>
    /// A reference that is not one is refused, with the value in the message.
    /// </summary>
    [Fact]
    public async Task A_reference_that_is_not_one_is_refused()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE")
            ?? string.Empty;

        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_QUERYABLE is not set.");

        string bare = qualified.Contains('/', StringComparison.Ordinal)
            ? qualified[(qualified.LastIndexOf('/') + 1)..]
            : qualified;

        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Put, $"{root}/admin/services/{bare}/srid", token!,
            JsonSerializer.Serialize(new { srid = 0 }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not a spatial reference", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the layer document says its extent is, and in which reference.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">The credential.</param>
    /// <param name="path">The document, below <c>/rest/services/</c>.</param>
    /// <returns>The wkid, and the extent's west and east edges.</returns>
    private async Task<(int Wkid, double West, double East)> DocumentReferenceAsync(
        string root, string token, string path)
    {
        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Get, $"{root}/rest/services/{path}?f=json", token, null);

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement root_ = JsonDocument.Parse(body).RootElement;

        // A layer document calls it `extent`; a service document calls it `fullExtent`.
        JsonElement extent = root_.TryGetProperty("extent", out JsonElement own)
            ? own
            : root_.GetProperty("fullExtent");
        JsonElement reference = extent.GetProperty("spatialReference");

        return (
            reference.TryGetProperty("latestWkid", out JsonElement latest)
                ? latest.GetInt32()
                : reference.GetProperty("wkid").GetInt32(),
            extent.GetProperty("xmin").GetDouble(),
            extent.GetProperty("xmax").GetDouble());
    }

    /// <summary>The wkid a query answers in, with no outSR unless one is given.</summary>
    /// <param name="root">The server.</param>
    /// <param name="token">The credential.</param>
    /// <param name="qualified">The service.</param>
    /// <param name="extra">Anything else to add to the query string.</param>
    /// <returns>The wkid the response reports.</returns>
    private async Task<int> ServedWkidAsync(
        string root, string token, string qualified, string extra = "")
    {
        (HttpStatusCode status, string body) = await SendAsync(
            HttpMethod.Get,
            $"{root}/rest/services/{qualified}/FeatureServer/0/query"
            + $"?where=1%3D1&returnGeometry=true&resultRecordCount=1&f=json{extra}",
            token,
            null);

        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement said = JsonDocument.Parse(body).RootElement;

        Assert.False(
            said.TryGetProperty("error", out JsonElement complaint),
            $"The query failed: {complaint}");

        JsonElement reference = said.GetProperty("spatialReference");

        return reference.TryGetProperty("latestWkid", out JsonElement latest)
            ? latest.GetInt32()
            : reference.GetProperty("wkid").GetInt32();
    }

    /// <summary>One keyword out of the suite's own connection string.</summary>
    /// <remarks>
    /// <b>Split rather than parsed, and the difference is deliberate.</b> Npgsql's builder is
    /// what the *server* uses, and this suite links nothing of the server's — it is the black-box
    /// half, and giving it the provider would let a test agree with the implementation about a
    /// bug in both. What it reads is one string this suite writes for itself,
    /// `GRATICULA_TEST_PG`, which carries no quoted values; anything more elaborate belongs on
    /// the other side of the wire, where the builder is.
    /// </remarks>
    /// <param name="keyword">The keyword to find, spelled as the environment writes it.</param>
    /// <returns>Its value, or an empty string.</returns>
    private static string Field(string keyword)
    {
        foreach (string part in (Connection ?? string.Empty).Split(';'))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);

            if (at > 0 && part[..at].Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return part[(at + 1)..].Trim();
            }
        }

        return string.Empty;
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
