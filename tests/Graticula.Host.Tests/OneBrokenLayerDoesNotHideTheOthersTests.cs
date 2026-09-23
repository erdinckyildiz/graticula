using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// One layer whose table is gone is left out of the three listing documents instead of taking them down — V-43.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found on the showcase 2026-09-23.</b> Two MotherDuck services failed on every request, and OGC API Features
/// <c>/collections</c>, WMS <c>GetCapabilities</c> and WFS <c>GetCapabilities</c> answered 500 with them —
/// the three documents QGIS reads first, so from QGIS every layer on the server was invisible.
/// </para>
/// <para>
/// <b>The mechanism, not a fixture.</b> A table is published, then dropped under the layer, and the layer's
/// remembered shape is forgotten so the next listing has to describe it again. That is the same failure a dead
/// source produces — the describe throws — reached with nothing but SQL. The layer's own address still has to
/// fail: a listing leaves a layer out, it does not make a broken one look healthy.
/// </para>
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class OneBrokenLayerDoesNotHideTheOthersTests
{
    private const string Table = "zz_vanishing";
    private const string Layer = "zz_vanishing_layer";
    private const string Service = "zz_vanishing_svc";

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is not set, so this test FAILS rather than skips. It needs a running server, an account on "
                + "it, and the database its datastore is in.");

    [Fact]
    public async Task A_dropped_table_is_left_out_of_every_listing_and_the_listings_still_answer()
    {
        string url = Required("GRATICULA_TEST_URL");
        await using NpgsqlDataSource database = NpgsqlDataSource.Create(Required("GRATICULA_TEST_PG"));

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient http = new(handler) { BaseAddress = new Uri(url) };
        string token = await TokenAsync(http);

        await ExecuteAsync(database,
            "create schema if not exists cifree; "
            + $"drop table if exists cifree.{Table}; "
            + $"create table cifree.{Table} (objectid integer generated always as identity primary key, name text, "
            + "shape geometry(Point, 3857)); "
            + $"insert into cifree.{Table} (name, shape) values ('here', st_setsrid(st_makepoint(3200000, 5000000), 3857))");

        try
        {
            string datastore = await DatastoreIdAsync(http, token);

            (HttpStatusCode published, string said) = await SendAsync(http, token, HttpMethod.Post, "/admin/layers", $$"""
                {"name":"{{Layer}}","dataSourceId":"{{datastore}}","schemaName":"cifree","tableName":"{{Table}}",
                 "geometryColumn":"shape","geometryType":"Point","identityColumn":"objectid","objectIdColumn":"objectid",
                 "srid":3857,"serviceName":"{{Service}}","folder":"hosted","sharing":"public"}
                """);
            Assert.True(published is HttpStatusCode.OK or HttpStatusCode.Created, $"Publishing answered {(int)published}: {said}");

            // Listed while it is healthy, so its absence below means something.
            Assert.Contains(Layer, (await GetAsync(http, token, "/ogc/features/v1/collections?f=json")).Body, StringComparison.Ordinal);

            await ExecuteAsync(database, $"drop table cifree.{Table}");
            await SendAsync(http, token, HttpMethod.Post, $"/admin/layers/{Layer}/refresh", null);

            foreach (string document in (string[])
            [
                "/ogc/features/v1/collections?f=json",
                "/wms?service=WMS&version=1.3.0&request=GetCapabilities",
                "/wfs?service=WFS&version=2.0.0&request=GetCapabilities",
            ])
            {
                (HttpStatusCode status, string body) = await GetAsync(http, token, document);

                Assert.True(
                    status == HttpStatusCode.OK,
                    $"{document} answered {(int)status} because one layer's table is gone. One broken layer must be "
                    + $"left out of a listing, not take it down (V-43): {body[..Math.Min(body.Length, 300)]}");

                Assert.DoesNotContain(Layer, body, StringComparison.Ordinal);
            }

            // And the layer's own address still fails: leaving it out is not pretending it works.
            (HttpStatusCode own, _) = await GetAsync(http, token, $"/ogc/features/v1/collections/{Layer}?f=json");
            Assert.NotEqual(HttpStatusCode.OK, own);
        }
        finally
        {
            await SendAsync(http, token, HttpMethod.Delete, $"/admin/layers/{Layer}", null);
            await SendAsync(http, token, HttpMethod.Delete, $"/admin/featureservices/{Service}?folder=hosted", null);
            await ExecuteAsync(database, $"drop table if exists cifree.{Table}");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource database, string sql)
    {
        await using NpgsqlCommand command = database.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> DatastoreIdAsync(HttpClient http, string token)
    {
        (_, string body) = await GetAsync(http, token, "/admin/datasources");

        return JsonDocument.Parse(body).RootElement.GetProperty("dataSources").EnumerateArray()
            .Where(s => s.GetProperty("name").GetString() == "datastore")
            .Select(s => s.GetProperty("id").GetString()!)
            .First();
    }

    private static Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient http, string token, string path) =>
        SendAsync(http, token, HttpMethod.Get, path, null);

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient http, string token, HttpMethod method, string path, string? json)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> TokenAsync(HttpClient http)
    {
        using StringContent body = new(
            JsonSerializer.Serialize(new { name = Required("GRATICULA_TEST_USER"), password = Required("GRATICULA_TEST_PASSWORD") }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await http.PostAsync("/rest/auth/login", body);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }
}
