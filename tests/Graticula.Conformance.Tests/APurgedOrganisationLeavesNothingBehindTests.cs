using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A purged organisation takes its hosted data with it and leaves no history trigger recording in a database it
/// registered — owner decision 2026-10-27, D-316.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's two answers:</b> a purge refuses while a registered layer keeps a history or attachments, as every
/// other removal does (ADR-207); and the organisation's hosted tables are dropped, as a service's delete drops its own
/// (ADR-034 §5k). Until then the purge removed the rows and said *"its hosted tables in the datastore were not dropped"*.
/// </para>
/// <para>
/// <b>The main host has a service of the same name</b>, and its table is asserted to outlive the purge: the purge
/// chooses what to drop, and a choice that reached past the organisation would drop another organisation's data.
/// </para>
/// <para>
/// <b>Returns early where the store's role is a superuser</b>, as every organisation test does: ADR-204 condition 9 refuses
/// a second organisation there. CI serves as a bound role, so it runs there.
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed class APurgedOrganisationLeavesNothingBehindTests : ArcGisClient
{
    private string _root = string.Empty;

    [Fact]
    public async Task A_purge_asks_about_a_history_first_and_then_drops_the_organisations_hosted_tables_only()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");
        Assert.False(
            string.IsNullOrWhiteSpace(ARegisteredLayerLeavesNothingBehindTests.Connection),
            "GRATICULA_TEST_PG is not set, so this FAILS rather than skips: it makes a database and reads the datastore.");

        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"pg{run}", host = $"pg{run}.localhost";
        string hosted = $"zz_pg_{run}", registered = $"zz_pgreg_{run}";
        string database = $"zz_pgdb_{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", token: admin, json: new
        {
            key, name = $"Purged {run}", hosts = new[] { host }, administrator = $"pgadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}: {tenant}");

        await ARegisteredLayerLeavesNothingBehindTests.CreateDatabaseAsync(database, withPostgis: true,
            "create table public.places (id integer primary key, shape geometry(Point, 4326))",
            "insert into public.places values (1, 'SRID=4326;POINT(32.85 39.93)')");

        try
        {
            string theirs = await SettleAsync($"pgadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            // The same name on both hosts: the main host's table must survive the purge.
            string mine = await DefineAsync(admin, null, hosted);
            string doomed = await DefineAsync(theirs, host, hosted);
            Assert.NotEqual(mine, doomed);

            // A registered layer of theirs, with its history on.
            (HttpStatusCode sourced, JsonElement source) = await SendAsync(HttpMethod.Post, "/admin/datasources", host, theirs, new
            {
                name = database, connectionString = ARegisteredLayerLeavesNothingBehindTests.On(database),
            });
            Assert.True(sourced is HttpStatusCode.OK or HttpStatusCode.Created, $"Registering at {host} answered {(int)sourced}: {source}");

            (HttpStatusCode published, JsonElement layer) = await SendAsync(HttpMethod.Post, "/admin/layers", host, theirs, new
            {
                name = registered, dataSourceId = source.GetProperty("id").GetString(), schemaName = "public", tableName = "places",
                geometryColumn = "shape", identityColumn = "id", objectIdColumn = "id", srid = 4326, geometryType = "Point",
                sharing = "private",
            });
            Assert.True(published is HttpStatusCode.OK or HttpStatusCode.Created, $"Publishing at {host} answered {(int)published}: {layer}");

            (HttpStatusCode on, JsonElement onBody) = await SendAsync(HttpMethod.Post, $"/admin/layers/{registered}/history", host, theirs,
                new { enabled = true });
            Assert.True(on == HttpStatusCode.OK, $"Switching history on answered {(int)on}: {onBody}");

            // Refused, and nothing gone.
            (HttpStatusCode asked, JsonElement why) = await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            Assert.True(asked == HttpStatusCode.Conflict,
                $"Purging an organisation whose registered layer keeps a history answered {(int)asked}: {why}. The trigger would "
                + "have been left recording to nobody (D-316).");
            Assert.Contains("a history", why.ToString(), StringComparison.Ordinal);
            Assert.True(await ExistsAsync(doomed), $"The refused purge dropped {doomed}.");
            Assert.True(await ARegisteredLayerLeavesNothingBehindTests.TriggersOnAsync(database) > 0, "The refused purge removed the trigger.");

            (HttpStatusCode off, JsonElement offBody) = await SendAsync(HttpMethod.Post, $"/admin/layers/{registered}/history", host, theirs,
                new { enabled = false });
            Assert.True(off == HttpStatusCode.OK, $"Switching history off answered {(int)off}: {offBody}");

            // Purged: their table dropped, the main host's kept.
            (HttpStatusCode purged, JsonElement gone) = await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", token: admin);
            Assert.True(purged == HttpStatusCode.OK, $"The purge answered {(int)purged}: {gone}");
            Assert.False(await ExistsAsync(doomed), $"The purged organisation's table {doomed} is still in the datastore: {gone}");
            Assert.True(await ExistsAsync(mine), $"The purge dropped {mine}, the main host's table of the same name: {gone}");
            Assert.Contains(doomed, gone.GetProperty("dropped").ToString(), StringComparison.Ordinal);
            Assert.Equal(0, gone.GetProperty("failed").GetArrayLength());
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true&unchecked=true", token: admin);
            await SendAsync(HttpMethod.Delete, $"/admin/featureservices/{hosted}?folder=hosted&drop=true", token: admin);
            await ARegisteredLayerLeavesNothingBehindTests.DropDatabaseAsync(database);
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host = null, string? token = null, object? json = null)
    {
        using HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Content = json is null ? null : JsonContent.Create(json);

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> SettleAsync(string name, string issued, string host)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, json: new { name, password = own });
        return again.GetProperty("token").GetString()!;
    }

    /// <summary>Defines an empty hosted layer and returns the datastore table it is kept in.</summary>
    private async Task<string> DefineAsync(string token, string? host, string name)
    {
        (HttpStatusCode made, JsonElement body) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, token, new
        {
            name, geometryType = "point", fields = new[] { new { name = "who", type = "Text" } }, sharing = "private",
        });
        Assert.True(made == HttpStatusCode.Created, $"Defining '{name}' at {host ?? "the main host"} answered {(int)made}: {body}");
        return body.GetProperty("table").GetString()!;
    }

    private static async Task<bool> ExistsAsync(string qualified)
    {
        await using NpgsqlConnection datastore = new(ARegisteredLayerLeavesNothingBehindTests.Connection);
        await datastore.OpenAsync();
        await using NpgsqlCommand exists = new("select to_regclass(@name) is not null", datastore);
        exists.Parameters.AddWithValue("name", qualified);
        return (bool)(await exists.ExecuteScalarAsync())!;
    }
}
