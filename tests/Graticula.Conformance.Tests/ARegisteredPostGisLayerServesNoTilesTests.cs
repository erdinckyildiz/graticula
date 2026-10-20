using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer published from a registered PostGIS database has no VectorTileServer — ADR-206, owner decision 2026-10-20,
/// which restored Q-67 after ADR-095 had tiled it — and its FeatureServer answers, with the five-minute default a
/// registered table's query answers keep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own source over the datastore's own database</b>, which is the one database a test can be sure
/// of — <c>DataSourceLifecycleConformanceTests</c>' fixture and for the same reason. Registered, it is a
/// registered PostGIS source in every way the server can see: not the datastore row, not hosted.
/// </para>
/// <para>
/// <b>Over the table the hosted tile service draws</b>, so the same rows that tile from the datastore are known to
/// exist: the difference this test sees is the source, and nothing else.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, because it publishes a service and three other classes walk
/// the directory (D-111).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ARegisteredPostGisLayerServesNoTilesTests : ArcGisClient
{
    private const string SourceName = "zz_vt_registered";

    private static readonly string[] QueryOnly = ["Query"];
    private const string LayerName = "zz_vt_registered_layer";

    private static string? Connection => Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");

    private static string? TileService => Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

    [Fact]
    public async Task A_registered_PostGIS_layer_has_no_tiles_and_its_features_are_kept_five_minutes()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        Assert.False(
            string.IsNullOrWhiteSpace(Connection),
            "GRATICULA_TEST_PG is not set, so this FAILS rather than skips: it registers that database "
            + "as a second PostGIS source.");
        Assert.False(
            string.IsNullOrWhiteSpace(TileService),
            "GRATICULA_TEST_TILE_SERVICE is not set, so this FAILS rather than skips: it borrows the "
            + "table that service draws, because a tile with features in it must be known to exist.");

        (string hostedLayer, string schema, string table) = await HostedTableAsync(root, token!);

        await CleanUpAsync(root, token!);

        (HttpStatusCode registered, string made) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/datasources", token!,
            JsonSerializer.Serialize(new { name = SourceName, connectionString = Connection }));

        Assert.True(
            registered is HttpStatusCode.OK or HttpStatusCode.Created,
            $"Registering the second source answered {(int)registered}: {made}");

        string source = JsonDocument.Parse(made).RootElement.GetProperty("id").GetString()!;
        string? service = null;

        try
        {
            JsonElement found = await TableOnAsync(root, token!, source, schema, table);

            (HttpStatusCode published, string answer) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers", token!,
                JsonSerializer.Serialize(new
                {
                    name = LayerName,
                    dataSourceId = source,
                    schemaName = schema,
                    tableName = table,
                    geometryColumn = found.GetProperty("geometryColumn").GetString(),
                    identityColumn = found.GetProperty("objectIdColumn").GetString(),
                    objectIdColumn = found.GetProperty("objectIdColumn").GetString(),
                    srid = found.GetProperty("srid").GetInt32(),
                    geometryType = found.TryGetProperty("geometryType", out JsonElement kind)
                        && kind.ValueKind == JsonValueKind.String ? kind.GetString() : "Polygon",
                    sharing = "public",
                }));

            Assert.True(
                published is HttpStatusCode.OK or HttpStatusCode.Created,
                $"Publishing the registered layer answered {(int)published}: {answer}");

            string url = JsonDocument.Parse(answer).RootElement.GetProperty("url").GetString()!;
            service = url["/rest/services/".Length..url.IndexOf("/FeatureServer", StringComparison.Ordinal)];

            // No scale limit, so the level the hosted service is known to draw at is drawn here too;
            // ADR-070 measures a range at publish, and this test is not about that.
            await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{LayerName}/visible-range", token!,
                "{\"minScale\":0,\"maxScale\":0}");

            // Query only, so nobody can edit it through this server and V-56's `no-cache` does not
            // stand in for the lifetime this test is about.
            string bare = service.Contains('/', StringComparison.Ordinal) ? service[(service.LastIndexOf('/') + 1)..] : service;
            string? folder = service.Contains('/', StringComparison.Ordinal) ? service[..service.LastIndexOf('/')] : null;

            (HttpStatusCode ceiling, string why) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/services/{Uri.EscapeDataString(bare)}/capabilities", token!,
                JsonSerializer.Serialize(new
                {
                    folder,
                    servesFeatures = (bool?)null,
                    servesTiles = (bool?)null,
                    capabilities = QueryOnly,
                }));

            Assert.True(ceiling == HttpStatusCode.OK, $"Could not narrow the service to Query: {(int)ceiling} {why}");

            // ---------- no tiles: the document and a tile are refused, and say where the features are ----------

            (HttpStatusCode described, string document) = await AnonymousAsync($"/rest/services/{service}/VectorTileServer");

            Assert.True(
                described == HttpStatusCode.BadRequest,
                $"A registered PostGIS layer's VectorTileServer answered {(int)described}: {document}. ADR-206 "
                + "draws tiles from the datastore only.");
            Assert.Contains("ADR-206", document, StringComparison.Ordinal);
            Assert.Contains("/FeatureServer", document, StringComparison.Ordinal);

            (HttpStatusCode tile, string refused) = await AnonymousAsync($"/rest/services/{service}/VectorTileServer/tile/0/0/0.pbf");
            Assert.True(tile != HttpStatusCode.OK, $"A registered layer's tile answered 200: {refused}");

            // ---------- its features: there, and kept the registered default ----------

            (HttpStatusCode counted, string count) = await AnonymousAsync(
                $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");

            Assert.True(counted == HttpStatusCode.OK, $"Its FeatureServer answered {(int)counted}: {count}");
            Assert.True(JsonDocument.Parse(count).RootElement.GetProperty("count").GetInt64() > 0, count);

            await ListingSaysAsync(root, token!);
        }
        finally
        {
            await CleanUpAsync(root, token!, service);
        }
    }

    /// <summary>
    /// The listing says it has no tiles, and still reads back the lifetime and coherence its query answers are sent
    /// with (ADR-069) — five minutes and best-effort, because other tools write to the table.
    /// </summary>
    private async Task ListingSaysAsync(string root, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/layers", token, null);
        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement row = JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == LayerName);

        Assert.False(row.GetProperty("tileable").GetBoolean());
        Assert.False(row.GetProperty("hosted").GetBoolean());
        Assert.Equal("postgis", row.GetProperty("kind").GetString());
        Assert.Equal("best-effort", row.GetProperty("coherence").GetString());
        Assert.True(
            row.GetProperty("tileLifetimeSeconds").GetInt64() is > 0 and <= 300,
            $"A registered layer nobody gave a lifetime says {row.GetProperty("tileLifetimeSeconds")} seconds.");
    }

    /// <summary>The hosted layer the tile service draws, and its table.</summary>
    private async Task<(string Layer, string Schema, string Table)> HostedTableAsync(string root, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/layers", token, null);
        Assert.Equal(HttpStatusCode.OK, status);

        string wanted = TileService!.Trim('/');
        string name = wanted.Contains('/', StringComparison.Ordinal) ? wanted[(wanted.LastIndexOf('/') + 1)..] : wanted;
        string? folder = wanted.Contains('/', StringComparison.Ordinal) ? wanted[..wanted.LastIndexOf('/')] : null;

        foreach (JsonElement layer in JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray())
        {
            string? itsFolder = layer.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()
                : null;

            if (string.Equals(layer.GetProperty("service").GetString(), name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(itsFolder ?? string.Empty, folder ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && layer.GetProperty("hosted").GetBoolean())
            {
                string qualified = layer.GetProperty("table").GetString()!;
                int dot = qualified.IndexOf('.', StringComparison.Ordinal);

                return (layer.GetProperty("name").GetString()!, qualified[..dot], qualified[(dot + 1)..]);
            }
        }

        Assert.Fail($"No hosted layer of '{TileService}' is in /admin/layers, so there is no table to borrow.");
        throw new InvalidOperationException();
    }

    /// <summary>The table as the new source's probe reports it: its geometry column, reference and identity.</summary>
    private async Task<JsonElement> TableOnAsync(string root, string token, string source, string schema, string table)
    {
        (HttpStatusCode status, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}/admin/datasources/{source}/capability", token, null);

        Assert.True(status == HttpStatusCode.OK, $"The new source's capability answered {(int)status}: {body}");

        foreach (JsonElement each in JsonDocument.Parse(body).RootElement.GetProperty("tables").EnumerateArray())
        {
            if (string.Equals(each.GetProperty("schemaName").GetString(), schema, StringComparison.Ordinal)
                && string.Equals(each.GetProperty("tableName").GetString(), table, StringComparison.Ordinal)
                && each.TryGetProperty("objectIdColumn", out JsonElement oid)
                && oid.ValueKind == JsonValueKind.String)
            {
                return each.Clone();
            }
        }

        Assert.Fail($"The registered source does not offer {schema}.{table} with an integer identity.");
        throw new InvalidOperationException();
    }

    private async Task CleanUpAsync(string root, string token, string? service = null)
    {
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/layers/{LayerName}", token, null);

        if (service is not null)
        {
            string bare = service.Contains('/', StringComparison.Ordinal) ? service[(service.LastIndexOf('/') + 1)..] : service;
            string folder = service.Contains('/', StringComparison.Ordinal) ? service[..service.LastIndexOf('/')] : string.Empty;

            await RequestAsync(
                HttpMethod.Delete,
                $"{root}/admin/featureservices/{Uri.EscapeDataString(bare)}"
                + (folder.Length > 0 ? $"?folder={Uri.EscapeDataString(folder)}" : string.Empty),
                token, null);
        }

        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/datasources", token, null);

        if (status == HttpStatusCode.OK)
        {
            foreach (JsonElement each in JsonDocument.Parse(body).RootElement.GetProperty("dataSources").EnumerateArray())
            {
                if (each.GetProperty("name").GetString() == SourceName)
                {
                    await RequestAsync(HttpMethod.Delete, $"{root}/admin/datasources/{each.GetProperty("id").GetString()}", token, null);
                }
            }
        }

        await RequestAsync(HttpMethod.Post, $"{root}/admin/featureservices/sweep", token, "{}");
    }
}
