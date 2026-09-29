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
/// A layer published from a registered PostGIS database has a VectorTileServer, its tiles hold what its
/// FeatureServer holds, a browser is told the registered default lifetime, and pointing its source at
/// another database throws its tiles away — ADR-095.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own source over the datastore's own database</b>, which is the one database a test can be sure
/// of — <c>DataSourceLifecycleConformanceTests</c>' fixture and for the same reason. Registered, it is a
/// registered PostGIS source in every way the server can see: not the datastore row, not hosted, its own
/// pool, budget and breaker.
/// </para>
/// <para>
/// <b>Over the table the hosted tile service already draws</b>, found through <c>/admin/layers</c>, so a tile
/// with features is known to exist and the two layers can be compared: one table, one statement, two
/// sources — the registered tile must hold as many features as the hosted one at the same address. That
/// is the strongest check available without decoding geometry, and it is what the owner's decision
/// promised: the same tiles, from somebody else's database.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, because it publishes a service and three other classes walk
/// the directory (D-111).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ARegisteredPostGisLayerServesTilesTests : ArcGisClient
{
    private const string SourceName = "zz_vt_registered";

    private static readonly string[] QueryOnly = ["Query"];
    private const string LayerName = "zz_vt_registered_layer";

    private static string? Connection => Environment.GetEnvironmentVariable("GRATICULA_TEST_PG");

    private static string? TileService => Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

    [Fact]
    public async Task A_registered_PostGIS_layer_tiles_like_the_hosted_one_and_forgets_a_moved_source()
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

            // ---------- the service document ----------

            (HttpStatusCode described, string document) = await AnonymousAsync($"/rest/services/{service}/VectorTileServer");

            Assert.True(
                described == HttpStatusCode.OK,
                $"A registered PostGIS layer's VectorTileServer answered {(int)described}: {document}. "
                + "ADR-095 gives it one; Q-67 refused it until 2026-09-29.");

            Assert.True(JsonDocument.Parse(document).RootElement.TryGetProperty("tiles", out _));

            // ---------- a tile, against the hosted one and against the features ----------

            (int z, int x, int y, Tile hosted) = await FirstPopulatedHostedTileAsync(root);

            Tile first = await TileAsync(root, service, z, y, x);

            Assert.True(
                first.Status == HttpStatusCode.OK,
                $"The registered tile {z}/{y}/{x} answered {(int)first.Status} where the hosted tile over "
                + "the same table has features.");

            int drawn = Features(first.Bytes, LayerName);
            int expected = Features(hosted.Bytes, hostedLayer);

            Assert.True(drawn > 0, "The registered tile decoded with no features in its layer.");
            Assert.Equal(expected, drawn);

            // Every feature in the tile is one the FeatureServer has in the tile's box and its buffer. A
            // tile may hold fewer — a shape smaller than a pixel is left out (Q-157) — and never more.
            long counted = await CountInTileAsync(service, z, x, y);

            Assert.True(
                drawn <= counted,
                $"The tile holds {drawn} features and the FeatureServer finds {counted} in the same box.");

            // ---------- the lifetime a browser is told ----------

            Assert.Equal("MISS", first.Disposition);
            Assert.Equal("public, max-age=300", first.CacheControl);

            Tile again = await TileAsync(root, service, z, y, x);
            Assert.Equal("HIT", again.Disposition);

            await CoherenceIsReadBackAsync(root, token!);

            // ---------- re-pointing the source ----------

            // The same database under a different pool setting is not a move: the pyramid is kept.
            (HttpStatusCode same, string kept) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/datasources/{source}", token!,
                JsonSerializer.Serialize(new { connectionString = Connection!.TrimEnd(';') + ";Application Name=zz_vt" }));

            Assert.True(same == HttpStatusCode.OK, $"The same database under another name answered {(int)same}: {kept}");
            Assert.Equal(0, JsonDocument.Parse(kept).RootElement.GetProperty("tilesPurged").GetInt32());
            Assert.Equal("HIT", (await TileAsync(root, service, z, y, x)).Disposition);

            // Another database is: the tiles drawn from the first are gone.
            string elsewhere = Regex.Replace(
                Connection!, @"(?i)(Database=)[^;]*", "${1}postgres");

            (HttpStatusCode moved, string gone) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/datasources/{source}?force=true", token!,
                JsonSerializer.Serialize(new { connectionString = elsewhere }));

            Assert.True(moved == HttpStatusCode.OK, $"Pointing the source elsewhere answered {(int)moved}: {gone}");
            Assert.True(
                JsonDocument.Parse(gone).RootElement.GetProperty("tilesPurged").GetInt32() > 0,
                $"The source moved to another database and none of its layer's tiles were purged: {gone}");

            // And back: the tile is built again rather than served from before the move.
            (HttpStatusCode back, string restored) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/datasources/{source}?force=true", token!,
                JsonSerializer.Serialize(new { connectionString = Connection }));

            Assert.True(back == HttpStatusCode.OK, $"Pointing the source back answered {(int)back}: {restored}");
            Assert.Equal("MISS", (await TileAsync(root, service, z, y, x)).Disposition);
        }
        finally
        {
            await CleanUpAsync(root, token!, service);
        }
    }

    /// <summary>ADR-010 §6b: the listing says how long this layer's tiles are kept and that coherence is best-effort.</summary>
    private async Task CoherenceIsReadBackAsync(string root, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/layers", token, null);
        Assert.Equal(HttpStatusCode.OK, status);

        JsonElement row = JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == LayerName);

        Assert.True(row.GetProperty("tileable").GetBoolean());
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

    private async Task<(int Z, int X, int Y, Tile Tile)> FirstPopulatedHostedTileAsync(string root)
    {
        (HttpStatusCode status, string body) = await AnonymousAsync($"/rest/services/{TileService}/VectorTileServer");
        Assert.True(status == HttpStatusCode.OK, $"The hosted tile service answered {(int)status}.");

        JsonElement extent = JsonDocument.Parse(body).RootElement.GetProperty("fullExtent");

        const int Zoom = 12;
        int side = 1 << Zoom;
        double size = 2 * Half / side;

        int x0 = Math.Clamp((int)((extent.GetProperty("xmin").GetDouble() + Half) / size), 0, side - 1);
        int x1 = Math.Clamp((int)((extent.GetProperty("xmax").GetDouble() + Half) / size), 0, side - 1);
        int y0 = Math.Clamp((int)((Half - extent.GetProperty("ymax").GetDouble()) / size), 0, side - 1);
        int y1 = Math.Clamp((int)((Half - extent.GetProperty("ymin").GetDouble()) / size), 0, side - 1);

        // Bounded as VectorTileConformanceTests bounds it: a world-wide extent at z12 is millions of tiles.
        foreach (int x in Enumerable.Range(x0, Math.Min(12, x1 - x0 + 1)))
        {
            foreach (int y in Enumerable.Range(y0, Math.Min(12, y1 - y0 + 1)))
            {
                Tile tile = await TileAsync(root, TileService!, Zoom, y, x);

                if (tile.Bytes.Length > 0)
                {
                    return (Zoom, x, y, tile);
                }
            }
        }

        Assert.Fail("No z12 tile over the hosted service's extent has anything in it.");
        throw new InvalidOperationException();
    }

    /// <summary>Half the Web Mercator world's width, in metres.</summary>
    private const double Half = 20037508.342789244;

    /// <summary>How many features the FeatureServer finds in a tile's box widened by the tile's buffer.</summary>
    private async Task<long> CountInTileAsync(string service, int z, int x, int y)
    {
        double size = 2 * Half / (1 << z);
        double buffer = size * 64 / 4096;
        double xmin = -Half + (x * size) - buffer;
        double xmax = -Half + ((x + 1) * size) + buffer;
        double ymax = Half - (y * size) + buffer;
        double ymin = Half - ((y + 1) * size) - buffer;

        string box = string.Join(
            ",", new[] { xmin, ymin, xmax, ymax }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

        (HttpStatusCode status, string body) = await AnonymousAsync(
            $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&geometry={box}"
            + "&geometryType=esriGeometryEnvelope&inSR=3857&spatialRel=esriSpatialRelIntersects"
            + "&returnCountOnly=true");

        Assert.True(status == HttpStatusCode.OK, $"The count query answered {(int)status}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("count").GetInt64();
    }

    private sealed record Tile(HttpStatusCode Status, byte[] Bytes, string CacheControl, string Disposition);

    /// <summary>A tile fetched as nobody, with what the headers said about it.</summary>
    private static async Task<Tile> TileAsync(string root, string service, int z, int y, int x)
    {
        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient http = new(handler);

        using HttpResponseMessage response = await http.GetAsync(
            new Uri($"{root}/rest/services/{service}/VectorTileServer/tile/{z}/{y}/{x}.pbf"));

        return new Tile(
            response.StatusCode,
            await response.Content.ReadAsByteArrayAsync(),
            response.Headers.CacheControl?.ToString() ?? string.Empty,
            response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? said) ? said.First() : string.Empty);
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

    // ---------- the MVT walk, written from the published specification ----------

    /// <summary>How many features the tile's layer of this name holds.</summary>
    private static int Features(byte[] tile, string layerName)
    {
        int i = 0;

        while (i < tile.Length)
        {
            (int field, int wire) = Tag(tile, ref i);

            if (field != 3 || wire != 2)
            {
                Skip(tile, ref i, wire);
                continue;
            }

            // The length first, then where it ends: `i + Varint(ref i)` reads i before the varint moves it.
            int layerLength = (int)Varint(tile, ref i);
            int end = i + layerLength;
            string name = string.Empty;
            int features = 0;

            while (i < end)
            {
                (int lf, int lw) = Tag(tile, ref i);

                if (lf == 1 && lw == 2)
                {
                    int length = (int)Varint(tile, ref i);
                    name = System.Text.Encoding.UTF8.GetString(tile, i, length);
                    i += length;
                }
                else if (lf == 2 && lw == 2)
                {
                    features++;
                    int featureLength = (int)Varint(tile, ref i);
                    i += featureLength;
                }
                else
                {
                    Skip(tile, ref i, lw);
                }
            }

            if (name == layerName)
            {
                return features;
            }
        }

        return 0;
    }

    private static (int Field, int Wire) Tag(byte[] b, ref int i)
    {
        ulong key = Varint(b, ref i);
        return ((int)(key >> 3), (int)(key & 7));
    }

    private static ulong Varint(byte[] b, ref int i)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            byte next = b[i++];
            value |= (ulong)(next & 0x7F) << shift;

            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static void Skip(byte[] b, ref int i, int wire)
    {
        switch (wire)
        {
            case 0: Varint(b, ref i); break;
            case 1: i += 8; break;
            case 2: { int length = (int)Varint(b, ref i); i += length; break; }
            case 5: i += 4; break;
            default: throw new InvalidOperationException($"wire type {wire} is not in the MVT encoding");
        }
    }
}
