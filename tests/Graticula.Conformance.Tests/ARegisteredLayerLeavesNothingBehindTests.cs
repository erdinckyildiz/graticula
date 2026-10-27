using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A registered layer is published only from a source that can serve it, and leaves nothing of this server's recording
/// in somebody else's database when it goes — D-311, D-309, D-312.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each test makes its own database on the suite's PostgreSQL and drops it</b>, because what is being asked about is
/// a database's state — no PostGIS, a trigger on a table, gone altogether — and the datastore's own database can be none
/// of those. <c>GRATICULA_TEST_PG</c> is the credential that makes them; the server registers them like any other source.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, because each publishes a service and three other classes walk the
/// directory (D-111).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ARegisteredLayerLeavesNothingBehindTests : ArcGisClient
{
    private const string Member = "zz_reglife_member";

    private static string? Connection => Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");

    /// <summary>
    /// D-311: the probe said a database without PostGIS could publish nothing, and <c>POST /admin/layers</c> published a
    /// layer from it anyway, whose every query answered 500. Both ways in now refuse, with the probe's own sentence.
    /// </summary>
    [Fact]
    public async Task A_database_without_PostGIS_publishes_nothing_by_either_route()
    {
        string root = await RequireServerAsync();
        string token = await AdministratorAsync(root);
        string database = $"zz_reglife_{Guid.NewGuid():N}"[..20];
        string? source = null;

        // template0, which no extension is ever installed into: the database is PostgreSQL and nothing more.
        await CreateDatabaseAsync(database, withPostgis: false, "create table public.places (id integer primary key, name text)");

        try
        {
            (HttpStatusCode registered, string made) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/datasources", token,
                JsonSerializer.Serialize(new { name = database, connectionString = On(database) }));

            Assert.True(registered is HttpStatusCode.OK or HttpStatusCode.Created, $"Registering it answered {(int)registered}: {made}");
            source = JsonDocument.Parse(made).RootElement.GetProperty("id").GetString()!;
            Assert.Contains("PostGIS is not installed", made, StringComparison.Ordinal);

            object layer = new
            {
                name = database,
                dataSourceId = source,
                schemaName = "public",
                tableName = "places",
                geometryColumn = "shape",
                identityColumn = "id",
                objectIdColumn = "id",
                srid = 4326,
                geometryType = "Point",
                sharing = "private",
            };

            (HttpStatusCode one, string oneBody) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers", token, JsonSerializer.Serialize(layer));

            Assert.True(one == HttpStatusCode.UnprocessableEntity,
                $"POST /admin/layers from a database without PostGIS answered {(int)one}: {oneBody}");
            Assert.Contains("PostGIS is not installed", oneBody, StringComparison.Ordinal);

            (HttpStatusCode composed, string composedBody) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/publish", token,
                JsonSerializer.Serialize(new { name = database, sharing = "private", nodes = new[] { new { layer } } }));

            Assert.True(composed == HttpStatusCode.UnprocessableEntity,
                $"POST /admin/publish from a database without PostGIS answered {(int)composed}: {composedBody}");
            Assert.Contains("PostGIS is not installed", composedBody, StringComparison.Ordinal);

            Assert.False(await LayerListedAsync(root, token, database), $"'{database}' was published although both answers refused it.");
        }
        finally
        {
            // A server that published it anyway leaves a service of the database's name behind.
            await RemoveServiceAsync(database, "&unchecked=true");
            await RemoveSourceAsync(root, token, source);
            await DropDatabaseAsync(database);
        }
    }

    /// <summary>
    /// D-309: a member removed with their content took their services out through the recycle bin, which asked nothing,
    /// and emptied it in the same request — so a history trigger on a registered table went on recording to nobody.
    /// </summary>
    [Fact]
    public async Task Removing_a_member_with_their_content_asks_about_a_history_first()
    {
        string root = await RequireServerAsync();
        string token = await AdministratorAsync(root);
        string database = $"zz_reglife_{Guid.NewGuid():N}"[..20];
        string? source = null;
        string? service = null;

        await CreateDatabaseAsync(database, withPostgis: true,
            "create table public.places (id integer primary key, shape geometry(Point, 4326))",
            "insert into public.places values (1, 'SRID=4326;POINT(32.85 39.93)')");

        try
        {
            source = await RegisterAsync(root, token, database);
            service = await PublishAsync(root, token, source, database, database);

            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/members",
                JsonSerializer.Serialize(new { name = Member, role = "publisher", userType = "creator" }));
            Assert.True(made is 200 or 201, $"making '{Member}': {made} {madeBody}");

            (int given, string givenBody) = await AdminAsync(HttpMethod.Put, $"/admin/services/{service}/owner",
                JsonSerializer.Serialize(new { to = Member }));
            Assert.True(given == 200, $"giving '{service}' to '{Member}': {given} {givenBody}");

            (HttpStatusCode on, string onBody) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers/{database}/history", token, "{\"enabled\":true}");
            Assert.True(on == HttpStatusCode.OK, $"Switching its history on answered {(int)on}: {onBody}");
            Assert.True(await TriggersOnAsync(database) > 0, "History is on and the table carries no trigger.");

            (int asked, string why) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");

            Assert.True(asked == 409,
                $"Removing a member whose registered layer keeps a history answered {asked}: {why}. Its services would have "
                + "been emptied from the recycle bin with the trigger still on the table (D-309).");
            Assert.Contains("a history", why, StringComparison.Ordinal);
            Assert.True(await LayerListedAsync(root, token, database), $"The refusal removed '{database}' anyway: {why}");
            Assert.True(await TriggersOnAsync(database) > 0, "The refusal removed the trigger, so something was half done.");

            (HttpStatusCode off, string offBody) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers/{database}/history", token, "{\"enabled\":false}");
            Assert.True(off == HttpStatusCode.OK, $"Switching its history off answered {(int)off}: {offBody}");

            (int removed, string removedBody) = await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
            Assert.True(removed == 200, $"Removing the member once history was off answered {removed}: {removedBody}");
            Assert.False(await LayerListedAsync(root, token, database), "The member's registered layer outlived them.");
            Assert.Equal(0, await TriggersOnAsync(database));
            service = null;
        }
        finally
        {
            await AdminAsync(HttpMethod.Post, $"/admin/layers/{database}/history", "{\"enabled\":false}");
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{Member}?deleteOwned=true");
            await RemoveServiceAsync(service, "&unchecked=true");
            await RemoveSourceAsync(root, token, source);
            await DropDatabaseAsync(database);
        }
    }

    /// <summary>
    /// D-312: a layer whose database was retired could never be removed, because ADR-207 asks that database whether a
    /// history is on before anything goes. It still asks, and says how to go without the answer; asked to, it goes.
    /// </summary>
    [Fact]
    public async Task A_layer_whose_database_was_retired_goes_when_told_the_check_cannot_be_made()
    {
        string root = await RequireServerAsync();
        string token = await AdministratorAsync(root);
        string database = $"zz_reglife_{Guid.NewGuid():N}"[..20];
        string second = database + "_b";
        string? source = null;
        string? service = null;

        await CreateDatabaseAsync(database, withPostgis: true,
            "create table public.places (id integer primary key, shape geometry(Point, 4326))",
            "insert into public.places values (1, 'SRID=4326;POINT(32.85 39.93)')",
            "create table public.places_b as table public.places",
            "alter table public.places_b add primary key (id)");

        try
        {
            source = await RegisterAsync(root, token, database);
            service = await PublishAsync(root, token, source, database, database);

            // A table is published once per source, so the service's second layer is a second table.
            await PublishAsync(root, token, source, second, database, "places_b");

            await DropDatabaseAsync(database);

            // One layer's unpublish.
            (HttpStatusCode layerAsked, string layerWhy) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/layers/{second}", token, null);

            Assert.True(layerAsked == HttpStatusCode.ServiceUnavailable,
                $"Removing a layer whose database is gone answered {(int)layerAsked}: {layerWhy}");
            Assert.Contains("unchecked=true", layerWhy, StringComparison.Ordinal);
            Assert.True(await LayerListedAsync(root, token, second), "The 503 removed the layer anyway.");

            (HttpStatusCode layerGone, string layerGoneBody) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/layers/{second}?unchecked=true", token, null);

            Assert.True(layerGone == HttpStatusCode.OK, $"Removing it unchecked answered {(int)layerGone}: {layerGoneBody}");
            Assert.False(await LayerListedAsync(root, token, second), "Removed unchecked, and still listed.");

            // The service's delete, the console's own button.
            (int serviceAsked, string serviceWhy) = await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}?drop=true");

            Assert.True(serviceAsked == 503, $"Deleting a service whose database is gone answered {serviceAsked}: {serviceWhy}");
            Assert.Contains("unchecked=true", serviceWhy, StringComparison.Ordinal);

            (int serviceGone, string serviceGoneBody) =
                await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}?drop=true&unchecked=true");

            Assert.True(serviceGone == 200, $"Deleting it unchecked answered {serviceGone}: {serviceGoneBody}");
            Assert.False(await LayerListedAsync(root, token, database), "Deleted unchecked, and its layer is still listed.");
            service = null;
        }
        finally
        {
            await RemoveServiceAsync(service, "&unchecked=true");
            await RemoveSourceAsync(root, token, source);
            await DropDatabaseAsync(database);
        }
    }

    private static async Task<string> AdministratorAsync(string root)
    {
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        Assert.False(
            string.IsNullOrWhiteSpace(Connection),
            "GRATICULA_TEST_PG is not set, so this FAILS rather than skips: it makes its own databases with that credential.");
        return token!;
    }

    /// <summary>The suite's connection, pointed at another database on the same server.</summary>
    private static string On(string database) => new NpgsqlConnectionStringBuilder(Connection) { Database = database }.ConnectionString;

    private static async Task CreateDatabaseAsync(string database, bool withPostgis, params string[] statements)
    {
        await using (NpgsqlConnection admin = new(Connection))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand create = new(
                $"create database \"{database}\"" + (withPostgis ? string.Empty : " template template0"), admin);
            await create.ExecuteNonQueryAsync();
        }

        await using NpgsqlConnection inside = new(On(database));
        await inside.OpenAsync();

        foreach (string statement in (withPostgis ? ["create extension if not exists postgis"] : Array.Empty<string>()).Concat(statements))
        {
            await using NpgsqlCommand command = new(statement, inside);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DropDatabaseAsync(string database)
    {
        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection admin = new(Connection);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"drop database if exists \"{database}\" with (force)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>The triggers a user made on the test table — the history's is the only one.</summary>
    private static async Task<long> TriggersOnAsync(string database)
    {
        await using NpgsqlConnection inside = new(On(database));
        await inside.OpenAsync();
        await using NpgsqlCommand count = new(
            "select count(*) from pg_trigger where tgrelid = 'public.places'::regclass and not tgisinternal", inside);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task<string> RegisterAsync(string root, string token, string database)
    {
        (HttpStatusCode registered, string made) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/datasources", token,
            JsonSerializer.Serialize(new { name = database, connectionString = On(database) }));

        Assert.True(registered is HttpStatusCode.OK or HttpStatusCode.Created, $"Registering '{database}' answered {(int)registered}: {made}");
        return JsonDocument.Parse(made).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Publishes the test table as a layer, into the service named after <paramref name="service"/>; the service's address.</summary>
    private async Task<string> PublishAsync(
        string root, string token, string source, string name, string service, string table = "places")
    {
        (HttpStatusCode published, string answer) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/layers", token,
            JsonSerializer.Serialize(new
            {
                name,
                dataSourceId = source,
                schemaName = "public",
                tableName = table,
                geometryColumn = "shape",
                identityColumn = "id",
                objectIdColumn = "id",
                srid = 4326,
                geometryType = "Point",
                sharing = "private",
                serviceName = service,
            }));

        Assert.True(published is HttpStatusCode.OK or HttpStatusCode.Created, $"Publishing '{name}' answered {(int)published}: {answer}");

        string url = JsonDocument.Parse(answer).RootElement.GetProperty("url").GetString()!;
        return url["/rest/services/".Length..url.IndexOf("/FeatureServer", StringComparison.Ordinal)];
    }

    private async Task<bool> LayerListedAsync(string root, string token, string name)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/layers", token, null);
        Assert.Equal(HttpStatusCode.OK, status);

        return JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray()
            .Any(l => l.GetProperty("name").GetString() == name);
    }

    private async Task RemoveServiceAsync(string? service, string also)
    {
        if (service is not null)
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}?drop=true{also}");
        }

        // What a delete put in the recycle bin goes too: a registered service sets no table aside, so nothing is lost.
        (int listed, string bin) = await AdminAsync(HttpMethod.Get, "/admin/recycle-bin");

        if (listed == 200)
        {
            foreach (JsonElement entry in JsonDocument.Parse(bin).RootElement.GetProperty("entries").EnumerateArray()
                .Where(e => (e.GetProperty("title").GetString() ?? "").StartsWith("zz_reglife_", StringComparison.Ordinal)))
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetProperty("id").GetString()}");
            }
        }
    }

    private async Task RemoveSourceAsync(string root, string token, string? source)
    {
        await RemoveServiceAsync(null, string.Empty);

        if (source is not null)
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/datasources/{source}", token, null);
        }
    }
}
