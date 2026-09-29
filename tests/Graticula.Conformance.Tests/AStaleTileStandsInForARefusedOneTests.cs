using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// While a tile's data source cannot build it, the expired copy stands in — marked, and never a purged one — and a
/// service's tile cache quota is set and read back. ADR-010 §5.1a and §3, owner decisions of 2026-09-29.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against <c>GRATICULA_TEST_TILE_SERVICE</c>, and the outage is a quiesce</b> — ADR-059's own route, the one
/// refusal a test can start and end on demand. The tile is fetched while the source answers, its layers' lifetime
/// is set to one second and allowed to pass, the source is quiesced, and the same tile must come back 200 with the
/// same bytes and <c>X-Tile-Cache: STALE</c>. Then the layers are refreshed — which purges their tiles — and the
/// same request must be refused 503, because §5.1's <em>wrong</em> class stays purged through an outage.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b> with the other tests that quiesce the datastore or read this service's
/// cache, so none of them sees the others' state. Everything this changes is put back in <c>finally</c>.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AStaleTileStandsInForARefusedOneTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const double Half = 20037508.342789244;

    [Fact]
    public async Task An_expired_tile_stands_in_while_its_source_is_quiesced_and_a_purged_one_does_not()
    {
        (string root, string folder, string name, string qualified) = await ServiceAsync();
        string cache = $"/admin/services/{Uri.EscapeDataString(name)}/cache{FolderQuery(qualified)}";

        List<(string Layer, string Source)> layers = await LayersAsync(folder, name);
        Dictionary<string, string> sources = await SourceIdsAsync();

        Assert.All(layers, l => Assert.True(
            sources.ContainsKey(l.Source), $"Layer '{l.Layer}' names data source '{l.Source}', which /admin/datasources does not list."));

        Uri tile = await ATileAsync(root, qualified, cache);

        // <b>What each layer's lifetime and stale limit were, so finally puts those back and not the defaults.</b>
        // The fixture gives this service a 60-second lifetime; restoring null instead left it on the default,
        // whose editable layers are sent no-cache, and TileValidatorConformanceTests' 304 then had no max-age
        // (found 2026-09-29, the first time these ran in one pass).
        Dictionary<string, string> before = [];

        foreach (JsonElement layer in (await AdminJsonAsync(cache)).GetProperty("layers").EnumerateArray())
        {
            string? seconds = layer.GetProperty("lifetimeFrom").GetString() == "layer"
                ? layer.GetProperty("lifetimeSeconds").GetInt64().ToString(CultureInfo.InvariantCulture)
                : "null";
            string? stale = layer.GetProperty("staleFrom").GetString() == "layer"
                ? layer.GetProperty("staleSeconds").GetInt64().ToString(CultureInfo.InvariantCulture)
                : "null";
            before[layer.GetProperty("name").GetString()!] = $"{{\"seconds\":{seconds},\"staleSeconds\":{stale}}}";
        }

        List<string> quiesced = [];

        try
        {
            // A second's lifetime and an hour's stale limit, set on every layer so the tile as a whole expires.
            foreach ((string layer, _) in layers)
            {
                await ExpectAsync(
                    HttpMethod.Put, LayerCache(layer, qualified), """{"seconds":1,"staleSeconds":3600}""", 200);
            }

            await RefreshAsync(layers, qualified);

            (HttpStatusCode builtStatus, byte[] built, Headers builtSaid) = await FetchAsync(root, tile);

            Assert.True(
                builtStatus is HttpStatusCode.OK or HttpStatusCode.NoContent,
                $"{tile} answered {(int)builtStatus} while its source was up.");
            Assert.True(builtSaid.State is "MISS" or "COALESCED", $"{tile} was {builtSaid.State} straight after a refresh.");

            await Task.Delay(TimeSpan.FromSeconds(2.5));

            foreach (string source in layers.Select(l => l.Source).Distinct(StringComparer.Ordinal))
            {
                await ExpectAsync(
                    HttpMethod.Post, $"/admin/datasources/{sources[source]}/quiesce",
                    """{"seconds":120,"why":"a stale-while-error conformance run"}""", 200);
                quiesced.Add(sources[source]);
            }

            (HttpStatusCode staleStatus, byte[] stale, Headers staleSaid) = await FetchAsync(root, tile);

            Assert.True(
                staleStatus == builtStatus,
                $"With its source quiesced and an expired copy on disk, {tile} answered {(int)staleStatus} rather than "
                + $"{(int)builtStatus}. ADR-010 §5.1a: the expired copy stands in (D-278).");
            Assert.Equal("STALE", staleSaid.State);
            Assert.True(stale.AsSpan().SequenceEqual(built), $"The stale tile is {stale.Length} bytes; the one built was {built.Length}.");
            // <b>A minute past its own Age, not max-age=60</b> (RFC 9111 §4.2.3): a cache that honours Age would
            // otherwise treat a tile already older than 60 seconds as expired on arrival. Written against the
            // literal 60 first and corrected when the server was (2026-09-29).
            long.TryParse(staleSaid.Age, NumberStyles.None, CultureInfo.InvariantCulture, out long staleAge);
            Assert.True(
                staleSaid.CacheControl is { } control
                    && (control.Contains($"max-age={staleAge + 60}", StringComparison.Ordinal)
                        || control.Contains("no-cache", StringComparison.Ordinal)),
                $"A stale tile went out with Cache-Control: {staleSaid.CacheControl} and Age: {staleSaid.Age}. It is "
                + "kept a minute past its age downstream, or revalidated for an editable layer.");
            Assert.True(
                long.TryParse(staleSaid.Age, NumberStyles.None, CultureInfo.InvariantCulture, out long age) && age >= 1,
                $"A stale tile said Age: {staleSaid.Age}; it is past a one-second lifetime.");

            if (builtStatus == HttpStatusCode.OK)
            {
                Assert.Equal(builtSaid.ETag, staleSaid.ETag);
            }

            JsonElement readBack = await AdminJsonAsync(cache);
            Assert.True(
                Require(readBack, "stale", "The cache read-back does not count stale answers.").GetProperty("served").GetInt64() >= 1,
                $"The read-back counts no stale answers after one: {readBack.GetProperty("stale")}");

            // A refresh purges the layers' tiles; with the source still quiesced, the next request has nothing to
            // stand in and is the 503 it was before this existed.
            await RefreshAsync(layers, qualified);

            (HttpStatusCode purgedStatus, _, Headers purgedSaid) = await FetchAsync(root, tile);

            Assert.True(
                purgedStatus == HttpStatusCode.ServiceUnavailable,
                $"After a refresh, with the source quiesced, {tile} answered {(int)purgedStatus} "
                + $"(X-Tile-Cache: {purgedSaid.State}). A purged tile must never be served stale — ADR-010 §5.1.");
        }
        finally
        {
            foreach (string id in quiesced)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/datasources/{id}/quiesce");
            }

            foreach ((string layer, _) in layers)
            {
                await AdminAsync(
                    HttpMethod.Put, LayerCache(layer, qualified),
                    before.TryGetValue(layer, out string? was) ? was : """{"seconds":null,"staleSeconds":null}""");
            }
        }
    }

    [Fact]
    public async Task A_services_quota_is_set_read_back_and_cleared()
    {
        (_, _, string name, string qualified) = await ServiceAsync();
        string cache = $"/admin/services/{Uri.EscapeDataString(name)}/cache{FolderQuery(qualified)}";
        string quota = $"/admin/services/{Uri.EscapeDataString(name)}/cache/quota{FolderQuery(qualified)}";

        JsonElement before = Require(await AdminJsonAsync(cache), "quota", "The cache read-back has no quota.");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("megabytes").ValueKind);
        Assert.True(before.GetProperty("usedBytes").GetInt64() >= 0, before.ToString());

        try
        {
            await ExpectAsync(HttpMethod.Put, quota, """{"megabytes":0}""", 400);
            await ExpectAsync(HttpMethod.Put, quota, """{"megabytes":64}""", 200);

            JsonElement set = (await AdminJsonAsync(cache)).GetProperty("quota");

            Assert.Equal(64, set.GetProperty("megabytes").GetInt32());
            Assert.Equal(64L * 1024 * 1024, set.GetProperty("bytes").GetInt64());
            Assert.True(set.GetProperty("usedBytes").GetInt64() >= 0, set.ToString());
            Assert.True(set.GetProperty("evictedEntries").GetInt64() >= 0, set.ToString());
            Assert.True(set.GetProperty("evictedBytes").GetInt64() >= 0, set.ToString());
        }
        finally
        {
            await ExpectAsync(HttpMethod.Put, quota, """{"megabytes":null}""", 200);
        }

        Assert.Equal(
            JsonValueKind.Null,
            (await AdminJsonAsync(cache)).GetProperty("quota").GetProperty("megabytes").ValueKind);
    }

    private async Task<(string Root, string Folder, string Name, string Qualified)> ServiceAsync()
    {
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string qualified = configured!.Trim('/');
        int cut = qualified.LastIndexOf('/');

        return (root, cut < 0 ? string.Empty : qualified[..cut], cut < 0 ? qualified : qualified[(cut + 1)..], qualified);
    }

    private static string LayerCache(string layer, string qualified) =>
        $"/admin/layers/{Uri.EscapeDataString(layer)}/cache?service={Uri.EscapeDataString(qualified)}";

    /// <summary>The service's layers and the data source each is read from.</summary>
    private async Task<List<(string Layer, string Source)>> LayersAsync(string folder, string name)
    {
        JsonElement listed = await AdminJsonAsync("/admin/layers");

        List<(string, string)> ours = [.. listed.GetProperty("layers").EnumerateArray()
            .Where(layer => string.Equals(layer.GetProperty("service").GetString(), name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    layer.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : string.Empty,
                    folder,
                    StringComparison.OrdinalIgnoreCase))
            .Select(layer => (layer.GetProperty("name").GetString()!, layer.GetProperty("dataSource").GetString()!))];

        Assert.NotEmpty(ours);
        return ours;
    }

    private async Task<Dictionary<string, string>> SourceIdsAsync() =>
        (await AdminJsonAsync("/admin/datasources")).GetProperty("dataSources").EnumerateArray()
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("id").GetString()!, StringComparer.Ordinal);

    private async Task RefreshAsync(List<(string Layer, string Source)> layers, string qualified)
    {
        foreach ((string layer, _) in layers)
        {
            await ExpectAsync(
                HttpMethod.Post,
                $"/admin/layers/{Uri.EscapeDataString(layer)}/refresh?service={Uri.EscapeDataString(qualified)}",
                null,
                200);
        }
    }

    /// <summary>
    /// A tile at the lowest level the service draws at: for Web Mercator the one under the middle of the service's
    /// area, and for another grid the first of the level.
    /// </summary>
    private async Task<Uri> ATileAsync(string root, string qualified, string cache)
    {
        JsonElement about = await AdminJsonAsync(cache);
        int z = Require(about, "defaults", "The read-back names no default levels for a tileable service.")
            .GetProperty("minZoom").GetInt32();
        int wkid = about.GetProperty("tilingScheme").GetProperty("wkid").GetInt32();

        int x = 0, y = 0;

        if (wkid is 3857 or 102100)
        {
            string name = qualified.Contains('/', StringComparison.Ordinal) ? qualified[(qualified.LastIndexOf('/') + 1)..] : qualified;
            (int status, string body) = await AdminAsync(
                HttpMethod.Post,
                $"/admin/services/{Uri.EscapeDataString(name)}/cache/seeds{FolderQuery(qualified)}&dryRun=true",
                JsonSerializer.Serialize(new { minZoom = z, maxZoom = z }));

            Assert.True(status == 200, $"A dry run of level {z} answered {status}: {body}");

            JsonElement area = JsonDocument.Parse(body).RootElement.GetProperty("area");
            double midX = (area.GetProperty("xmin").GetDouble() + area.GetProperty("xmax").GetDouble()) / 2;
            double midY = (area.GetProperty("ymin").GetDouble() + area.GetProperty("ymax").GetDouble()) / 2;

            long side = 1L << z;
            double size = 2 * Half / side;

            x = (int)Math.Clamp(Math.Floor((midX + Half) / size), 0, side - 1);
            y = (int)Math.Clamp(Math.Floor((Half - midY) / size), 0, side - 1);
        }

        return new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"{root}/rest/services/{qualified}/VectorTileServer/tile/{z}/{y}/{x}.pbf"));
    }

    private async Task<JsonElement> AdminJsonAsync(string path)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, path);
        Assert.True(status == 200, $"GET {path} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task ExpectAsync(HttpMethod method, string path, string? body, int expected)
    {
        (int status, string said) = await AdminAsync(method, path, body);
        Assert.True(status == expected, $"{method} {path} answered {status} rather than {expected}: {said}");
    }

    private sealed record Headers(string? State, string? CacheControl, string? Age, string? ETag);

    private static async Task<(HttpStatusCode Status, byte[] Bytes, Headers Said)> FetchAsync(string root, Uri tile)
    {
        // Identity encoding, so the bytes compared are the tile's and not one compression's (ADR-068).
        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AutomaticDecompression = DecompressionMethods.None,
        });

        using HttpRequestMessage request = new(HttpMethod.Get, tile);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        static string? One(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out IEnumerable<string>? said) || r.Content.Headers.TryGetValues(name, out said)
                ? said.FirstOrDefault()
                : null;

        return (
            response.StatusCode,
            await response.Content.ReadAsByteArrayAsync(),
            new Headers(
                One(response, "X-Tile-Cache"),
                response.Headers.CacheControl?.ToString(),
                One(response, "Age"),
                response.Headers.ETag?.ToString()));
    }
}
