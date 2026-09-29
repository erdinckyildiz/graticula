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
/// A seed fills the cache a request reads, with the bytes a request would have built — ADR-093.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is that a seeded tile is indistinguishable from a served one</b>, and it is tested
/// the only way that can fail: the tile is served cold first and its bytes kept, the cache is emptied,
/// the seed builds it, and the next request must be a <c>HIT</c> with the same bytes. A seed that
/// wrote under a key the route does not read would pass every check but the <c>HIT</c>; one that
/// built through different code would pass the <c>HIT</c> and fail the bytes.
/// </para>
/// <para>
/// <b>Against <c>GRATICULA_TEST_TILE_SERVICE</c>, and small.</b> The levels start at the lowest the
/// service draws at and grow while the dry run says the seed stays under five hundred tiles, so the
/// test seeds whatever the fixture holds in seconds.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection</b>, with the other test that reads this service's cache state
/// (<c>AStoredTileSaysHowOldItIsTests</c>): this one empties the service's cache twice, and a class
/// fetching a tile twice in parallel would see its second fetch miss.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ASeededTileIsAServedTileTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const long Small = 500;

    private const double Half = 20037508.342789244;

    /// <summary>The five things a seeded tile can come to, which add up to how many are done.</summary>
    private static readonly string[] Outcomes = ["built", "present", "empty", "failed", "skipped"];

    [Fact]
    public async Task A_seeded_tile_is_a_hit_with_the_bytes_a_request_built()
    {
        (string root, string folder, string name, string qualified) = await ServiceAsync();
        string cache = $"/admin/services/{Uri.EscapeDataString(name)}/cache{FolderQuery(qualified)}";
        string seeds = $"/admin/services/{Uri.EscapeDataString(name)}/cache/seeds";

        await CancelAnyRunningAsync(cache, seeds, qualified);

        JsonElement about = await AdminJsonAsync(HttpMethod.Get, cache);
        JsonElement defaults = Require(about, "defaults", "The read-back names no default levels for a tileable service.");

        int from = defaults.GetProperty("minZoom").GetInt32();
        int ceiling = defaults.GetProperty("maxZoom").GetInt32();
        int to = from;
        JsonElement counted = await DryRunAsync(seeds, qualified, from, to);

        while (to < ceiling)
        {
            JsonElement wider = await DryRunAsync(seeds, qualified, from, to + 1);

            if (wider.GetProperty("tiles").GetInt64() > Small)
            {
                break;
            }

            to++;
            counted = wider;
        }

        long tiles = counted.GetProperty("tiles").GetInt64();

        Assert.True(tiles is > 0 and <= Small * 4, $"The dry run counted {tiles} tiles for levels {from} to {to}.");

        // A tile of the top level, inside the area: the one whose bytes are compared.
        JsonElement area = counted.GetProperty("area");
        await EmptyTheCacheAsync(qualified, folder, name);

        (Uri tile, byte[] served, HttpStatusCode servedStatus) =
            await AServedTileAsync(root, qualified, to, area);

        await EmptyTheCacheAsync(qualified, folder, name);

        (int started, string startBody) = await AdminAsync(
            HttpMethod.Post, $"{seeds}{FolderQuery(qualified)}", Levels(from, to));

        Assert.True(started == 202, $"Starting a seed answered {started}: {startBody}");

        JsonElement job = JsonDocument.Parse(startBody).RootElement;
        string watch = Require(job, "watch", "A 202 must say where to watch the seed.").GetString()!;

        // <b>A second seed of the same service while this one is queued or running is a 409</b>,
        // naming the one that is — ADR-093 §5.2.
        (int second, string secondBody) = await AdminAsync(
            HttpMethod.Post, $"{seeds}{FolderQuery(qualified)}", Levels(from, to));

        Assert.True(
            second == 409 || (second == 202 && await FinishedAsync(watch)),
            $"A second seed while the first was running answered {second}: {secondBody}");

        JsonElement done = await WaitAsync(watch, TimeSpan.FromMinutes(5));

        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal(tiles, done.GetProperty("done").GetInt64());
        Assert.Equal(
            done.GetProperty("done").GetInt64(),
            Outcomes.Sum(k => done.GetProperty(k).GetInt64()));

        (HttpStatusCode status, byte[] bytes, string? state) = await FetchAsync(root, tile);

        Assert.True(
            state == "HIT",
            $"The seed finished and the next request for {tile} was X-Tile-Cache: {state}. The seed "
            + "filled a cache the tile route does not read, or under a key it does not ask for.");

        Assert.Equal(servedStatus, status);
        Assert.True(
            bytes.AsSpan().SequenceEqual(served),
            $"The seeded tile is {bytes.Length} bytes and the tile a request built before the seed was "
            + $"{served.Length}. A seed must build through the route's own code (ADR-093 §5.5).");

        // <b>ADR-010 §6b: the level reads back as seeded, and cached now.</b>
        JsonElement after = await AdminJsonAsync(HttpMethod.Get, cache);
        JsonElement top = after.GetProperty("levels").EnumerateArray()
            .Single(level => level.GetProperty("zoom").GetInt32() == to);

        Assert.True(top.GetProperty("lastSeeded").ValueKind == JsonValueKind.String, $"Level {to} reads back no seed time: {top}");
        Assert.True(
            top.GetProperty("cached").GetInt64() > 0,
            $"Level {to} was just seeded and reads back nothing cached: {top}");
    }

    [Fact]
    public async Task A_seed_over_the_cap_is_refused_with_its_count()
    {
        (_, _, string name, string qualified) = await ServiceAsync();

        string body = JsonSerializer.Serialize(new
        {
            minZoom = 0,
            maxZoom = 22,
            extent = new { xmin = -Half, ymin = -Half, xmax = Half, ymax = Half, spatialReference = new { wkid = 3857 } },
        });

        (int status, string said) = await AdminAsync(
            HttpMethod.Post, $"/admin/services/{Uri.EscapeDataString(name)}/cache/seeds{FolderQuery(qualified)}&dryRun=true", body);

        Assert.True(status == 400, $"A seed of the whole world to level 22 answered {status}: {said}");

        JsonElement refusal = JsonDocument.Parse(said).RootElement;

        Assert.True(refusal.GetProperty("tiles").GetInt64() > refusal.GetProperty("cap").GetInt64(), said);
        Assert.Contains("fit", refusal.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_seed_can_be_cancelled_and_says_so()
    {
        (_, _, string name, string qualified) = await ServiceAsync();
        string cache = $"/admin/services/{Uri.EscapeDataString(name)}/cache{FolderQuery(qualified)}";
        string seeds = $"/admin/services/{Uri.EscapeDataString(name)}/cache/seeds";

        await CancelAnyRunningAsync(cache, seeds, qualified);

        JsonElement defaults = (await AdminJsonAsync(HttpMethod.Get, cache)).GetProperty("defaults");
        int from = defaults.GetProperty("minZoom").GetInt32();
        int to = defaults.GetProperty("maxZoom").GetInt32();

        (int started, string startBody) = await AdminAsync(
            HttpMethod.Post, $"{seeds}{FolderQuery(qualified)}", Levels(from, to));

        if (started == 400)
        {
            // The fixture is large enough that its default levels exceed the cap; a smaller range
            // still exercises the cancel.
            (started, startBody) = await AdminAsync(
                HttpMethod.Post, $"{seeds}{FolderQuery(qualified)}", Levels(from, Math.Min(to, from + 3)));
        }

        Assert.True(started == 202, $"Starting a seed answered {started}: {startBody}");

        string watch = JsonDocument.Parse(startBody).RootElement.GetProperty("watch").GetString()!;
        string id = JsonDocument.Parse(startBody).RootElement.GetProperty("job").GetString()!;
        string address = $"{seeds}/{id}{FolderQuery(qualified)}";

        (int cancelled, string said) = await AdminAsync(HttpMethod.Delete, address);

        if (cancelled == 409 && await FinishedAsync(watch))
        {
            return; // It finished before it could be stopped, which the 409 says; nothing to cancel.
        }

        Assert.True(cancelled == 200, $"Cancelling a running seed answered {cancelled}: {said}");
        Assert.Equal("cancelled", (await AdminJsonAsync(HttpMethod.Get, address)).GetProperty("status").GetString());

        (int again, string againSaid) = await AdminAsync(HttpMethod.Delete, address);
        Assert.True(again == 409, $"Cancelling a cancelled seed answered {again}: {againSaid}");
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

    private static string Levels(int from, int to) => JsonSerializer.Serialize(new { minZoom = from, maxZoom = to });

    private async Task<JsonElement> DryRunAsync(string seeds, string qualified, int from, int to)
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, $"{seeds}{FolderQuery(qualified)}&dryRun=true", Levels(from, to));

        if (status == 400 && body.Contains("\"tiles\"", StringComparison.Ordinal))
        {
            // Over the cap: the refusal carries the count, which is all a dry run is for here.
            return JsonDocument.Parse(body).RootElement;
        }

        Assert.True(status == 200, $"A dry run of levels {from} to {to} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<JsonElement> AdminJsonAsync(HttpMethod method, string path)
    {
        (int status, string body) = await AdminAsync(method, path);
        Assert.True(status == 200, $"{method} {path} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task CancelAnyRunningAsync(string cache, string seeds, string qualified)
    {
        JsonElement about = await AdminJsonAsync(HttpMethod.Get, cache);

        if (about.GetProperty("running") is { ValueKind: JsonValueKind.Object } running)
        {
            await AdminAsync(HttpMethod.Delete, $"{seeds}/{running.GetProperty("id").GetString()}{FolderQuery(qualified)}");
        }
    }

    /// <summary>Empties this server's cache of every layer of the service, through the refresh route.</summary>
    private async Task EmptyTheCacheAsync(string qualified, string folder, string name)
    {
        (int listed, string layers) = await AdminAsync(HttpMethod.Get, "/admin/layers");
        Assert.True(listed == 200, $"/admin/layers answered {listed}: {layers}");

        IEnumerable<string> ours = JsonDocument.Parse(layers).RootElement.GetProperty("layers").EnumerateArray()
            .Where(layer => string.Equals(layer.GetProperty("service").GetString(), name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    layer.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : string.Empty,
                    folder,
                    StringComparison.OrdinalIgnoreCase))
            .Select(layer => layer.GetProperty("name").GetString()!)
            .ToList();

        Assert.NotEmpty(ours);

        foreach (string layer in ours)
        {
            (int status, string said) = await AdminAsync(
                HttpMethod.Post,
                $"/admin/layers/{Uri.EscapeDataString(layer)}/refresh?service={Uri.EscapeDataString(qualified)}");

            Assert.True(status == 200, $"Refreshing layer '{layer}' of '{qualified}' answered {status}: {said}");
        }
    }

    /// <summary>
    /// A tile of the level inside the area, fetched cold so this request builds it — preferring one
    /// with something in it, so the byte comparison compares something.
    /// </summary>
    private static async Task<(Uri Tile, byte[] Bytes, HttpStatusCode Status)> AServedTileAsync(
        string root, string qualified, int z, JsonElement area)
    {
        double xmin = area.GetProperty("xmin").GetDouble();
        double ymin = area.GetProperty("ymin").GetDouble();
        double xmax = area.GetProperty("xmax").GetDouble();
        double ymax = area.GetProperty("ymax").GetDouble();

        long side = 1L << z;
        double size = 2 * Half / side;

        int Column(double x) => (int)Math.Clamp(Math.Floor((x + Half) / size), 0, side - 1);
        int Row(double y) => (int)Math.Clamp(Math.Floor((Half - y) / size), 0, side - 1);

        (int x0, int x1, int y0, int y1) = (Column(xmin), Column(xmax), Row(ymax), Row(ymin));

        // The middle first, then outwards along the middle row and column.
        List<(int X, int Y)> candidates = [((x0 + x1) / 2, (y0 + y1) / 2)];

        for (int step = 1; candidates.Count < 12 && step <= Math.Max(x1 - x0, y1 - y0); step++)
        {
            candidates.Add((Math.Min(x1, ((x0 + x1) / 2) + step), (y0 + y1) / 2));
            candidates.Add(((x0 + x1) / 2, Math.Min(y1, ((y0 + y1) / 2) + step)));
        }

        (Uri Tile, byte[] Bytes, HttpStatusCode Status)? first = null;

        foreach ((int x, int y) in candidates.Distinct())
        {
            Uri tile = new(string.Create(
                CultureInfo.InvariantCulture,
                $"{root}/rest/services/{qualified}/VectorTileServer/tile/{z}/{y}/{x}.pbf"));

            (HttpStatusCode status, byte[] bytes, string? state) = await FetchAsync(root, tile);

            Assert.True(
                state is "MISS" or "COALESCED",
                $"{tile} answered X-Tile-Cache: {state} straight after the service's cache was emptied.");

            first ??= (tile, bytes, status);

            if (status == HttpStatusCode.OK && bytes.Length > 0)
            {
                return (tile, bytes, status);
            }
        }

        return first!.Value;
    }

    private static async Task<(HttpStatusCode Status, byte[] Bytes, string? State)> FetchAsync(string root, Uri tile)
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

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"{tile} answered {(int)response.StatusCode}.");

        string? state = response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? said)
            ? said.FirstOrDefault()
            : null;

        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), state);
    }

    private async Task<bool> FinishedAsync(string watch)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, watch);

        return status == 200
            && JsonDocument.Parse(body).RootElement.GetProperty("status").GetString() is "done" or "failed" or "cancelled";
    }

    private async Task<JsonElement> WaitAsync(string watch, TimeSpan patience)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            JsonElement seed = await AdminJsonAsync(HttpMethod.Get, watch);
            string? status = seed.GetProperty("status").GetString();

            if (status is "done" or "failed" or "cancelled")
            {
                return seed;
            }

            Assert.True(
                DateTimeOffset.UtcNow < until,
                $"The seed was still {status} after {patience.TotalMinutes} minutes: {seed}");

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
