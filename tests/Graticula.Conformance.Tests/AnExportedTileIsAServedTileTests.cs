using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Tests.Shared;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A tile in an exported package is the tile the service serves — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>The package is opened by the specifications' readers, not the writer's</b> (<c>TilePackageReaders</c>): the
/// VTPK's bundle by Esri's compact cache V2 description, the PMTiles archive by its v3 specification and the Hilbert
/// id computed there. A tile found that way and gunzipped must equal the bytes <c>tile/{z}/{y}/{x}.pbf</c> answers.
/// </para>
/// <para>
/// <b>Against <c>GRATICULA_TEST_TILE_SERVICE</c>, small, and it puts the policy back.</b> The export policy is turned
/// on for the test and restored after it, so the fixture's service document says what it said before.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AnExportedTileIsAServedTileTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const long Small = 200;

    private const double Half = 20037508.342789244;

    [Fact]
    public async Task ExportTiles_makes_a_vtpk_whose_tiles_are_the_served_ones_and_a_caller_without_access_cannot_fetch_it()
    {
        (string root, string name, string qualified) = await ServiceAsync();
        string exports = $"/admin/services/{Uri.EscapeDataString(name)}/exports";
        string service = $"/rest/services/{qualified}/VectorTileServer";

        JsonElement before = (await AdminJsonAsync(HttpMethod.Get, $"{exports}{FolderQuery(qualified)}")).GetProperty("policy");

        try
        {
            await SetPolicyAsync(exports, qualified, allowed: true, anonymous: false);

            JsonElement document = await AdminJsonAsync(HttpMethod.Get, $"{service}?f=json");
            Assert.True(document.GetProperty("exportTilesAllowed").GetBoolean(), $"The service document: {document}");
            Assert.True(document.GetProperty("maxExportTilesCount").GetInt64() > 0, $"The service document: {document}");

            (int from, int to, long tiles, _) = await SmallLevelsAsync(exports, qualified, "vtpk");
            string levels = string.Create(CultureInfo.InvariantCulture, $"{from}-{to}");

            // <b>The estimate</b>: a job that has already succeeded, whose result counts the same tiles.
            JsonElement estimate = await AdminJsonAsync(
                HttpMethod.Get, $"{service}/estimateExportTilesSize?levels={levels}&exportExtent=DEFAULT&f=json");
            string estimateId = estimate.GetProperty("jobId").GetString()!;
            Assert.Equal("esriJobSucceeded", (await AdminJsonAsync(HttpMethod.Get, $"{service}/jobs/{estimateId}?f=json")).GetProperty("jobStatus").GetString());

            JsonElement counted = await AdminJsonAsync(
                HttpMethod.Get, $"{service}/jobs/{estimateId}/results/out_service_tile_estimates?f=json");
            Assert.Equal(tiles, counted.GetProperty("value").GetProperty("totalTilesToExport").GetInt64());
            Assert.True(counted.GetProperty("value").GetProperty("totalSize").GetInt64() > 0, counted.ToString());

            // <b>The export</b>: submitted, then watched until it succeeds.
            JsonElement submitted = await AdminJsonAsync(
                HttpMethod.Post, $"{service}/exportTiles?levels={levels}&exportExtent=DEFAULT&f=json");
            Assert.Equal("esriJobSubmitted", submitted.GetProperty("jobStatus").GetString());
            string jobId = submitted.GetProperty("jobId").GetString()!;

            JsonElement job = await WaitForJobAsync($"{service}/jobs/{jobId}?f=json", TimeSpan.FromMinutes(5));
            Assert.True(job.GetProperty("jobStatus").GetString() == "esriJobSucceeded", $"The export job: {job}");
            Assert.Equal(
                "results/out_service_url",
                job.GetProperty("results").GetProperty("out_service_url").GetProperty("paramUrl").GetString());

            JsonElement result = await AdminJsonAsync(HttpMethod.Get, $"{service}/jobs/{jobId}/results/out_service_url?f=json");
            string url = result.GetProperty("value").GetString()!;
            Assert.EndsWith(".vtpk", url, StringComparison.Ordinal);

            (HttpStatusCode status, byte[] package, HttpResponseMessage headers) = await DownloadAsync(url, authenticated: true);
            Assert.True(status == HttpStatusCode.OK, $"Downloading {url} answered {(int)status}.");
            Assert.Equal(package.Length, headers.Content.Headers.ContentLength);

            using (ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read))
            {
                using JsonDocument inside = JsonDocument.Parse(zip.GetEntry("p12/root.json")!.Open());
                Assert.Equal("gzip", inside.RootElement.GetProperty("resourceInfo").GetProperty("tileCompression").GetString());
                Assert.NotNull(zip.GetEntry("p12/resources/styles/root.json"));
                Assert.NotNull(zip.GetEntry("esriinfo/iteminfo.xml"));

                (int z, int row, int column, byte[] stored)? any = FirstStoredTile(zip);

                Assert.True(any is not null, "The package holds no tile at all; the fixture's levels drew nothing.");

                (int tz, int ty, int tx, byte[] gz) = any!.Value;
                (HttpStatusCode servedStatus, byte[] served) = await ServedTileAsync(root, qualified, tz, tx, ty);

                Assert.Equal(HttpStatusCode.OK, servedStatus);
                Assert.True(
                    TilePackageReaders.Gunzip(gz).AsSpan().SequenceEqual(served),
                    $"Tile {tz}/{ty}/{tx} in the package is not the tile the service serves.");
                Assert.Equal(served, TilePackageReaders.VtpkTile(zip, tz, tx, ty));
            }

            // <b>A range is a range</b>, with its length — what a device resuming a download asks for.
            (HttpStatusCode partial, byte[] first, _) = await DownloadAsync(url, authenticated: true, range: "bytes=0-99");
            Assert.Equal(HttpStatusCode.PartialContent, partial);
            Assert.Equal(package.AsSpan(0, 100).ToArray(), first);

            // <b>A caller without access cannot fetch it</b>: anonymous export is off, and a public service does not
            // change that; nor does a guessed file name.
            (HttpStatusCode anonymous, _, _) = await DownloadAsync(url, authenticated: false);
            Assert.True(anonymous is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
                $"An anonymous download of {url} answered {(int)anonymous}.");

            string guessed = url[..(url.LastIndexOf('/') + 1)] + new string('0', 32) + ".vtpk";
            (HttpStatusCode wrong, _, _) = await DownloadAsync(guessed, authenticated: true);
            Assert.Equal(HttpStatusCode.NotFound, wrong);

            // Turned off, the ArcGIS address stops handing the package out at once.
            await SetPolicyAsync(exports, qualified, allowed: false, anonymous: false);
            (HttpStatusCode off, _, _) = await DownloadAsync(url, authenticated: true);
            Assert.Equal(HttpStatusCode.Forbidden, off);
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Put,
                $"{exports}/policy{FolderQuery(qualified)}",
                JsonSerializer.Serialize(new
                {
                    allowed = before.GetProperty("allowed").GetBoolean(),
                    anonymous = before.GetProperty("anonymous").GetBoolean(),
                    maxExportTilesCount = before.GetProperty("maxExportTilesCount").ValueKind == JsonValueKind.Number
                        ? before.GetProperty("maxExportTilesCount").GetInt32()
                        : (int?)null,
                }));
        }
    }

    [Fact]
    public async Task An_administrator_exports_a_pmtiles_archive_whose_tiles_are_the_served_ones_and_can_delete_it()
    {
        (string root, string name, string qualified) = await ServiceAsync();
        string exports = $"/admin/services/{Uri.EscapeDataString(name)}/exports";

        JsonElement about = await AdminJsonAsync(HttpMethod.Get, $"{exports}{FolderQuery(qualified)}");

        if (!about.GetProperty("formats").EnumerateArray().Any(f => f.GetString() == "pmtiles"))
        {
            // A service on another grid is refused a PMTiles archive, and says so.
            (int refused, string refusal) = await AdminAsync(
                HttpMethod.Post, $"{exports}{FolderQuery(qualified)}", JsonSerializer.Serialize(new { format = "pmtiles" }));
            Assert.True(refused == 400 && refusal.Contains("Web Mercator", StringComparison.Ordinal), refusal);
            return;
        }

        (int from, int to, long tiles, JsonElement area) = await SmallLevelsAsync(exports, qualified, "pmtiles");

        (int started, string body) = await AdminAsync(
            HttpMethod.Post, $"{exports}{FolderQuery(qualified)}",
            JsonSerializer.Serialize(new { format = "pmtiles", minZoom = from, maxZoom = to }));

        Assert.True(started == 202, $"Starting an export answered {started}: {body}");
        string watch = JsonDocument.Parse(body).RootElement.GetProperty("watch").GetString()!;

        JsonElement done = await WaitForExportAsync(watch, TimeSpan.FromMinutes(5));
        Assert.True(done.GetProperty("status").GetString() == "done", $"The export: {done}");
        Assert.Equal(tiles, done.GetProperty("tiles").GetInt64());

        string download = done.GetProperty("download").GetString()!;
        (HttpStatusCode status, byte[] archive, HttpResponseMessage response) = await DownloadAsync($"{root}{download}", authenticated: true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/vnd.pmtiles", response.Content.Headers.ContentType?.MediaType);

        TilePackageReaders.PmHeader header = TilePackageReaders.PmTilesHeader(archive);
        Assert.Equal(1, header.TileType);
        Assert.Equal((from, to), (header.MinZoom, header.MaxZoom));

        // A served tile of the top level with something in it is in the archive, byte for byte.
        bool compared = false;

        foreach ((int x, int y) in Candidates(to, area))
        {
            (HttpStatusCode servedStatus, byte[] served) = await ServedTileAsync(root, qualified, to, x, y);

            if (servedStatus != HttpStatusCode.OK || served.Length == 0)
            {
                Assert.Null(TilePackageReaders.PmTilesTile(archive, to, x, y));
                continue;
            }

            Assert.Equal(served, TilePackageReaders.PmTilesTile(archive, to, x, y));
            compared = true;
            break;
        }

        Assert.True(compared || header.Addressed == 0, "No served tile near the middle of the area had anything in it.");

        (int deleted, string said) = await AdminAsync(HttpMethod.Delete, watch);
        Assert.True(deleted == 200, $"Deleting the export answered {deleted}: {said}");

        (HttpStatusCode gone, _, _) = await DownloadAsync($"{root}{download}", authenticated: true);
        Assert.Equal(HttpStatusCode.NotFound, gone);
    }

    /// <remarks>
    /// <b>ADR-098 §5.7: exportTiles is a GET that writes, so the session cookie alone may not start one</b> — from a page
    /// elsewhere, or with no word from the browser about where it came from. A token in the query, or a bearer header,
    /// is how an ArcGIS client signs it, and that keeps working.
    /// </remarks>
    [Fact]
    public async Task A_cookie_alone_cannot_start_an_export_and_a_token_can()
    {
        (string root, string name, string qualified) = await ServiceAsync();
        string exports = $"/admin/services/{Uri.EscapeDataString(name)}/exports";
        JsonElement before = (await AdminJsonAsync(HttpMethod.Get, $"{exports}{FolderQuery(qualified)}")).GetProperty("policy");
        JsonElement defaults = (await AdminJsonAsync(HttpMethod.Get, $"{exports}{FolderQuery(qualified)}")).GetProperty("defaults");
        int level = defaults.GetProperty("minZoom").GetInt32();
        string path = string.Create(
            CultureInfo.InvariantCulture, $"/rest/services/{qualified}/VectorTileServer/exportTiles?levels={level}&f=json");

        try
        {
            await SetPolicyAsync(exports, qualified, allowed: true, anonymous: false);

            CookieContainer jar = new();

            using HttpClient browser = new(new HttpClientHandler
            {
                CookieContainer = jar,
                UseCookies = true,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });

            using (FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
                   {
                       new("name", Environment.GetEnvironmentVariable(UserVariable) ?? "root"),
                       new("password", Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty),
                       new("return", "/rest/services"),
                   }))
            using (HttpResponseMessage _ = await browser.PostAsync(new Uri(root + "/rest/auth/login"), form))
            {
            }

            Assert.NotNull(jar.GetCookies(new Uri(root))["gis-session"]);

            foreach (string? site in new[] { null, "cross-site", "same-site" })
            {
                using HttpRequestMessage forged = new(HttpMethod.Get, new Uri(root + path));

                if (site is not null)
                {
                    forged.Headers.Add("Sec-Fetch-Site", site);
                }

                using HttpResponseMessage refused = await browser.SendAsync(forged);
                string said = await refused.Content.ReadAsStringAsync();

                Assert.True(
                    refused.StatusCode == HttpStatusCode.Forbidden && said.Contains("token", StringComparison.Ordinal),
                    $"A cookie-only exportTiles with Sec-Fetch-Site {site ?? "absent"} answered {(int)refused.StatusCode}: {said}");
                Assert.Equal(403, JsonDocument.Parse(said).RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            // A token signs it: the bearer header always, and token= where the deployment reads it (D-120).
            (int signed, string body) = await AdminAsync(HttpMethod.Get, path);
            Assert.True(signed == 200, $"A token-signed exportTiles answered {signed}: {body}");
            string job = JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;

            await AdminAsync(HttpMethod.Delete, $"{exports}/{job}{FolderQuery(qualified)}");

            if (await TokenAsync(root) is { } token)
            {
                using HttpRequestMessage query = new(HttpMethod.Get, new Uri($"{root}{path}&token={Uri.EscapeDataString(token)}"));
                using HttpResponseMessage answered = await browser.SendAsync(query);
                string queried = await answered.Content.ReadAsStringAsync();

                // 200 where the query channel is read; where a deployment turned it off the cookie is all that is left,
                // and the refusal above is the right answer.
                Assert.True(
                    answered.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden,
                    $"exportTiles with token= answered {(int)answered.StatusCode}: {queried}");

                if (answered.StatusCode == HttpStatusCode.OK)
                {
                    string again = JsonDocument.Parse(queried).RootElement.GetProperty("jobId").GetString()!;
                    await AdminAsync(HttpMethod.Delete, $"{exports}/{again}{FolderQuery(qualified)}");
                }
            }
        }
        finally
        {
            await AdminAsync(
                HttpMethod.Put,
                $"{exports}/policy{FolderQuery(qualified)}",
                JsonSerializer.Serialize(new
                {
                    allowed = before.GetProperty("allowed").GetBoolean(),
                    anonymous = before.GetProperty("anonymous").GetBoolean(),
                    maxExportTilesCount = before.GetProperty("maxExportTilesCount").ValueKind == JsonValueKind.Number
                        ? before.GetProperty("maxExportTilesCount").GetInt32()
                        : (int?)null,
                }));
        }
    }

    [Fact]
    public async Task ExportTiles_is_refused_where_the_service_does_not_offer_it()
    {
        (_, string name, string qualified) = await ServiceAsync();
        string exports = $"/admin/services/{Uri.EscapeDataString(name)}/exports";
        JsonElement policy = (await AdminJsonAsync(HttpMethod.Get, $"{exports}{FolderQuery(qualified)}")).GetProperty("policy");

        if (policy.GetProperty("allowed").GetBoolean())
        {
            return; // The fixture offers exports; the refusal is the other test's to see, after it turns them off.
        }

        (int status, string body) = await AdminAsync(
            HttpMethod.Get, $"/rest/services/{qualified}/VectorTileServer/exportTiles?levels=0&f=json");

        Assert.True(status == 403, $"exportTiles on a service that does not offer it answered {status}: {body}");

        JsonElement document = await AdminJsonAsync(HttpMethod.Get, $"/rest/services/{qualified}/VectorTileServer?f=json");
        Assert.False(document.GetProperty("exportTilesAllowed").GetBoolean());
        Assert.False(document.TryGetProperty("maxExportTilesCount", out _));
    }

    private async Task<(string Root, string Name, string Qualified)> ServiceAsync()
    {
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string qualified = configured!.Trim('/');
        int cut = qualified.LastIndexOf('/');

        return (root, cut < 0 ? qualified : qualified[(cut + 1)..], qualified);
    }

    private async Task SetPolicyAsync(string exports, string qualified, bool allowed, bool anonymous)
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Put, $"{exports}/policy{FolderQuery(qualified)}",
            JsonSerializer.Serialize(new { allowed, anonymous, maxExportTilesCount = (int?)null }));

        Assert.True(status == 200, $"Setting the export policy answered {status}: {body}");
    }

    /// <summary>The lowest default level and as many above it as keep an export under <see cref="Small"/> tiles.</summary>
    private async Task<(int From, int To, long Tiles, JsonElement Area)> SmallLevelsAsync(string exports, string qualified, string format)
    {
        JsonElement about = await AdminJsonAsync(HttpMethod.Get, $"{exports}{FolderQuery(qualified)}");
        JsonElement defaults = Require(about, "defaults", "The export read-back names no default levels.");

        int from = defaults.GetProperty("minZoom").GetInt32();
        int ceiling = defaults.GetProperty("maxZoom").GetInt32();
        int to = from;
        JsonElement counted = await DryRunAsync(exports, qualified, format, from, to);

        while (to < ceiling)
        {
            JsonElement wider = await DryRunAsync(exports, qualified, format, from, to + 1);

            if (wider.GetProperty("tiles").GetInt64() > Small)
            {
                break;
            }

            to++;
            counted = wider;
        }

        return (from, to, counted.GetProperty("tiles").GetInt64(), counted.GetProperty("area").Clone());
    }

    private async Task<JsonElement> DryRunAsync(string exports, string qualified, string format, int from, int to)
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, $"{exports}{FolderQuery(qualified)}&dryRun=true",
            JsonSerializer.Serialize(new { format, minZoom = from, maxZoom = to }));

        if (status == 400 && body.Contains("tooManyTiles", StringComparison.Ordinal))
        {
            return JsonDocument.Parse(body).RootElement.GetProperty("detail").Clone();
        }

        Assert.True(status == 200, $"A dry run of levels {from} to {to} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> AdminJsonAsync(HttpMethod method, string path)
    {
        (int status, string body) = await AdminAsync(method, path);
        Assert.True(status == 200, $"{method} {path} answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<JsonElement> WaitForJobAsync(string path, TimeSpan patience)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            JsonElement job = await AdminJsonAsync(HttpMethod.Get, path);

            if (job.GetProperty("jobStatus").GetString() is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled")
            {
                return job;
            }

            Assert.True(DateTimeOffset.UtcNow < until, $"The export was still running after {patience.TotalMinutes} minutes: {job}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<JsonElement> WaitForExportAsync(string watch, TimeSpan patience)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            JsonElement export = await AdminJsonAsync(HttpMethod.Get, watch);

            if (export.GetProperty("status").GetString() is "done" or "failed" or "cancelled")
            {
                return export;
            }

            Assert.True(DateTimeOffset.UtcNow < until, $"The export was still running after {patience.TotalMinutes} minutes: {export}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<(HttpStatusCode Status, byte[] Bytes, HttpResponseMessage Response)> DownloadAsync(
        string url, bool authenticated, string? range = null)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, url);

        if (authenticated)
        {
            await AuthenticateAsync(request, root);
        }

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        HttpResponseMessage response = await Http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response);
    }

    private static async Task<(HttpStatusCode Status, byte[] Bytes)> ServedTileAsync(
        string root, string qualified, int z, int x, int y)
    {
        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AutomaticDecompression = DecompressionMethods.None,
        });

        using HttpRequestMessage request = new(HttpMethod.Get, string.Create(
            CultureInfo.InvariantCulture, $"{root}/rest/services/{qualified}/VectorTileServer/tile/{z}/{y}/{x}.pbf"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The first tile stored in any bundle of the package, found by its record — grid-agnostic.</summary>
    private static (int Z, int Row, int Column, byte[] Stored)? FirstStoredTile(ZipArchive zip)
    {
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.EndsWith(".bundle", StringComparison.Ordinal)))
        {
            // p12/tile/L08/R0080C0100.bundle
            string[] parts = entry.FullName.Split('/');
            int z = int.Parse(parts[2][1..], CultureInfo.InvariantCulture);
            string file = parts[3];
            int c = file.IndexOf('C', StringComparison.Ordinal);
            int originRow = int.Parse(file[1..c], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int originColumn = int.Parse(file[(c + 1)..file.IndexOf('.', StringComparison.Ordinal)], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            using MemoryStream bytes = new();

            using (Stream read = entry.Open())
            {
                read.CopyTo(bytes);
            }

            byte[] bundle = bytes.ToArray();

            for (int i = 0; i < 16384; i++)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(bundle.AsSpan(64 + (8 * i))) >> 40 == 0)
                {
                    continue;
                }

                int row = originRow + (i / 128);
                int column = originColumn + (i % 128);

                return (z, row, column, TilePackageReaders.BundleTile(bundle, row, column)!);
            }
        }

        return null;
    }

    /// <summary>Tiles of a level near the middle of a Web Mercator area, the middle first.</summary>
    private static IEnumerable<(int X, int Y)> Candidates(int z, JsonElement area)
    {
        long side = 1L << z;
        double size = 2 * Half / side;

        int Column(double x) => (int)Math.Clamp(Math.Floor((x + Half) / size), 0, side - 1);
        int Row(double y) => (int)Math.Clamp(Math.Floor((Half - y) / size), 0, side - 1);

        (int x0, int x1, int y0, int y1) = (
            Column(area.GetProperty("xmin").GetDouble()), Column(area.GetProperty("xmax").GetDouble()),
            Row(area.GetProperty("ymax").GetDouble()), Row(area.GetProperty("ymin").GetDouble()));

        List<(int, int)> candidates = [((x0 + x1) / 2, (y0 + y1) / 2)];

        for (int step = 1; candidates.Count < 16 && step <= Math.Max(x1 - x0, y1 - y0); step++)
        {
            candidates.Add((Math.Min(x1, ((x0 + x1) / 2) + step), (y0 + y1) / 2));
            candidates.Add(((x0 + x1) / 2, Math.Min(y1, ((y0 + y1) / 2) + step)));
        }

        return candidates.Distinct();
    }
}
