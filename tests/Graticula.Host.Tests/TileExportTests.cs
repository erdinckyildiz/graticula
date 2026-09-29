using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Tests.Shared;
using Graticula.Tiles;
using Graticula.Tiles.Packages;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// Exporting tiles as a package — ADR-098: the staging, the two packages written from it and read back by the
/// specifications, the estimate, the capability gating, and the file names nothing in a request can reach past.
/// </summary>
public sealed class TileExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "graticula-export-" + Guid.NewGuid().ToString("N"));

    public TileExportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test's temporary directory; the operating system will have it.
        }
    }

    private static PublishedService Service(TileExportPolicy? export = null, bool? servesTiles = null) =>
        new(
            Guid.NewGuid(), "roads", "hosted", "FeatureServer", null, Guid.NewGuid(), SharingScope.Public,
            ServiceStatus.Started, [],
            limits: new ServiceCapabilityLimits(null, servesTiles, null, null).With(export ?? TileExportPolicy.Off));

    private static byte[] Tile(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A glyph directory with two ranges of the fallback stack, under the test's own directory.</summary>
    private GlyphStore Glyphs()
    {
        string root = Path.Combine(_directory, "glyphs");
        string stack = Path.Combine(root, GlyphStore.Fallback);

        Directory.CreateDirectory(stack);
        File.WriteAllBytes(Path.Combine(stack, "0-255.pbf"), [1]);
        File.WriteAllBytes(Path.Combine(stack, "256-511.pbf"), [2]);

        return new GlyphStore(root);
    }

    [Fact]
    public void The_generated_style_carries_the_service_s_name()
    {
        // D-285: the style's `name` is the service's; a stored style is served as its author wrote it.
        JsonElement style = JsonDocument.Parse(
            JsonSerializer.Serialize(VectorTileEndpoints.GeneratedStyle(Service(), Glyphs()))).RootElement;

        Assert.Equal("roads", style.GetProperty("name").GetString());
    }

    [Fact]
    public void The_resource_list_names_each_glyph_range_and_the_four_sprite_files_relative_to_itself()
    {
        // D-285, in the shape Esri's own World_Basemap_v2 answers resources/info: fonts, then sprites, each `../`.
        GlyphStore glyphs = Glyphs();
        string style = JsonSerializer.Serialize(VectorTileEndpoints.GeneratedStyle(Service(), glyphs));

        string[] listed = [.. JsonDocument.Parse(JsonSerializer.Serialize(VectorTileEndpoints.ResourceInfo(style, glyphs)))
            .RootElement.GetProperty("resourceInfo").EnumerateArray().Select(e => e.GetString()!)];

        Assert.Equal(
            [
                $"../fonts/{GlyphStore.Fallback}/0-255.pbf",
                $"../fonts/{GlyphStore.Fallback}/256-511.pbf",
                "../sprites/sprite.json",
                "../sprites/sprite.png",
                "../sprites/sprite@2x.json",
                "../sprites/sprite@2x.png",
            ],
            listed);

        // A style that fetches no glyphs lists none: the list is what the style can ask for.
        string[] bare = [.. JsonDocument.Parse(JsonSerializer.Serialize(
                VectorTileEndpoints.ResourceInfo("""{"version":8,"sources":{},"layers":[]}""", glyphs)))
            .RootElement.GetProperty("resourceInfo").EnumerateArray().Select(e => e.GetString()!)];

        Assert.All(bare, path => Assert.StartsWith("../sprites/", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Staged_tiles_are_kept_gzip_compressed_and_an_empty_one_is_not_kept()
    {
        using TileExportStaging staging = new(Path.Combine(_directory, "a.staging"));

        Assert.True(await staging.AddAsync(new TileAddress(3, 1, 2), Tile("one"), CancellationToken.None) > 0);
        Assert.Equal(0, await staging.AddAsync(new TileAddress(3, 1, 3), [], CancellationToken.None));

        StagedTile kept = Assert.Single(staging.Tiles);
        ReadOnlyMemory<byte> stored = await staging.ReadAsync(kept.Offset, kept.Length, CancellationToken.None);

        Assert.Equal(Tile("one"), TilePackageReaders.Gunzip(stored.ToArray()));
        Assert.Equal(new TileAddress(3, 1, 2), kept.Address);
    }

    /// <remarks>
    /// <b>A tile kept twice is kept once, the second time.</b> The walk retries a batch after a source outage, and a
    /// tile built after the one that met it arrives again; a package given the same tile twice is refused by both
    /// writers.
    /// </remarks>
    [Fact]
    public async Task A_tile_kept_again_after_a_retry_replaces_the_first_and_an_empty_one_removes_it()
    {
        using TileExportStaging staging = new(Path.Combine(_directory, "r.staging"));
        TileAddress address = new(5, 3, 4);

        await staging.AddAsync(address, Tile("first"), CancellationToken.None);
        await staging.AddAsync(address, Tile("second"), CancellationToken.None);

        StagedTile kept = Assert.Single(staging.Tiles);
        Assert.Equal(Tile("second"), TilePackageReaders.Gunzip((await staging.ReadAsync(kept.Offset, kept.Length, CancellationToken.None)).ToArray()));

        await staging.AddAsync(address, [], CancellationToken.None);
        Assert.Empty(staging.Tiles);
    }

    [Fact]
    public async Task A_vtpk_written_from_staging_gives_back_every_tile_as_the_route_served_it()
    {
        Dictionary<TileAddress, byte[]> tiles = [];

        using TileExportStaging staging = new(Path.Combine(_directory, "b.staging"));

        // Two levels, and at level 8 tiles in two different bundles.
        foreach (TileAddress address in new[]
                 {
                     new TileAddress(1, 0, 0), new TileAddress(1, 1, 1),
                     new TileAddress(8, 10, 20), new TileAddress(8, 200, 130), new TileAddress(8, 201, 130),
                 })
        {
            byte[] tile = Tile($"tile {address}");
            tiles[address] = tile;
            await staging.AddAsync(address, tile, CancellationToken.None);
        }

        TileExportPackage.VtpkDocuments documents = new(
            Tile("{\"name\":\"roads\"}"),
            Tile("{\"version\":8}"),
            new Dictionary<string, byte[]> { ["sprites/sprite.json"] = Tile("{}"), ["info/root.json"] = Tile("{}") },
            Tile("<ESRI_ItemInformation/>"),
            Tile("<pkinfo/>"));

        string path = Path.Combine(_directory, "b.vtpk");

        await using (FileStream output = File.Create(path))
        {
            await TileExportPackage.WriteVtpkAsync(output, staging, documents, CancellationToken.None);
        }

        using ZipArchive package = ZipFile.OpenRead(path);

        foreach ((TileAddress address, byte[] tile) in tiles)
        {
            Assert.Equal(tile, TilePackageReaders.VtpkTile(package, address.Z, address.X, address.Y));
        }

        Assert.Null(TilePackageReaders.VtpkTile(package, 8, 11, 20));

        string[] names = [.. package.Entries.Select(e => e.FullName)];
        Assert.Contains("p12/root.json", names);
        Assert.Contains("p12/resources/styles/root.json", names);
        Assert.Contains("p12/resources/sprites/sprite.json", names);
        Assert.Contains("p12/resources/info/root.json", names);
        Assert.Contains("esriinfo/iteminfo.xml", names);
        Assert.Contains("esriinfo/item.pkinfo", names);
        Assert.Contains("p12/tile/L01/R0000C0000.bundle", names);
        Assert.Contains("p12/tile/L08/R0000C0000.bundle", names);
        Assert.Contains("p12/tile/L08/R0080C0080.bundle", names);
        Assert.Equal(3, names.Count(n => n.EndsWith(".bundle", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_pmtiles_archive_written_from_staging_gives_back_every_tile_and_stores_twins_once()
    {
        using TileExportStaging staging = new(Path.Combine(_directory, "c.staging"));

        await staging.AddAsync(new TileAddress(0, 0, 0), Tile("world"), CancellationToken.None);
        await staging.AddAsync(new TileAddress(2, 1, 1), Tile("sea"), CancellationToken.None);
        await staging.AddAsync(new TileAddress(2, 3, 3), Tile("sea"), CancellationToken.None);
        await staging.AddAsync(new TileAddress(2, 2, 1), Tile("land"), CancellationToken.None);

        PmTilesDescription description = new(
            0, 2, -180, -85, 180, 85, 0, 0, 0,
            Encoding.UTF8.GetBytes("{\"vector_layers\":[{\"id\":\"roads\",\"fields\":{}}]}"));

        string path = Path.Combine(_directory, "c.pmtiles");

        await using (FileStream output = File.Create(path))
        {
            await TileExportPackage.WritePmTilesAsync(output, staging, description, CancellationToken.None);
        }

        byte[] archive = await File.ReadAllBytesAsync(path);
        TilePackageReaders.PmHeader header = TilePackageReaders.PmTilesHeader(archive);

        Assert.Equal(2, header.TileCompression);
        Assert.Equal(1, header.TileType);
        Assert.Equal(4ul, header.Addressed);
        Assert.Equal(3ul, header.Contents);

        Assert.Equal(Tile("world"), TilePackageReaders.PmTilesTile(archive, 0, 0, 0));
        Assert.Equal(Tile("sea"), TilePackageReaders.PmTilesTile(archive, 2, 1, 1));
        Assert.Equal(Tile("sea"), TilePackageReaders.PmTilesTile(archive, 2, 3, 3));
        Assert.Equal(Tile("land"), TilePackageReaders.PmTilesTile(archive, 2, 2, 1));
        Assert.Null(TilePackageReaders.PmTilesTile(archive, 2, 0, 0));
    }

    [Fact]
    public void The_estimate_adds_each_formats_overhead_to_the_tiles()
    {
        // Level 8, a rectangle of 200 × 10 tiles over three bundle columns (0, 128, 256): 2,000 tiles, 3 bundles.
        TileRange[] levels = [new TileRange(8, 100, 0, 299, 9)];

        long vtpk = TileExportPackage.EstimateBytes(TileExportFormat.Vtpk, levels, 1_000_000);
        long pmtiles = TileExportPackage.EstimateBytes(TileExportFormat.PmTiles, levels, 1_000_000);

        Assert.Equal(1_000_000 + (2_000 * 4) + (3 * CompactCacheBundle.DataStart) + TileExportPackage.DocumentAllowance, vtpk);
        Assert.Equal(1_000_000 + (2_000 * 16) + PmTiles.HeaderSize + (64 * 1024), pmtiles);
        Assert.Equal(long.MaxValue, TileExportPackage.EstimateBytes(TileExportFormat.Vtpk, levels, long.MaxValue - 10));
    }

    [Fact]
    public void Exports_are_offered_only_where_the_policy_and_the_tile_face_allow_and_anonymous_only_when_it_says()
    {
        Assert.False(VectorTileExportEndpoints.MayExport(Service(), anonymous: false));
        Assert.False(VectorTileExportEndpoints.MayExport(Service(), anonymous: true));

        PublishedService on = Service(new TileExportPolicy(true, false, null));
        Assert.True(VectorTileExportEndpoints.MayExport(on, anonymous: false));
        Assert.False(VectorTileExportEndpoints.MayExport(on, anonymous: true));

        PublishedService everyone = Service(new TileExportPolicy(true, true, null));
        Assert.True(VectorTileExportEndpoints.MayExport(everyone, anonymous: true));

        // The tile face turned off turns the export off with it: a ceiling cannot lift a floor.
        Assert.False(VectorTileExportEndpoints.MayExport(
            Service(new TileExportPolicy(true, true, null), servesTiles: false), anonymous: false));
    }

    [Fact]
    public void A_service_that_offers_exports_says_so_in_the_two_documented_properties_and_changes_nothing_else()
    {
        object plain = new { name = "roads", capabilities = "TilesOnly,Tilemap", exportTilesAllowed = false };

        JsonObject advertised = (JsonObject)VectorTileExportEndpoints.Advertised(plain, 5000);

        Assert.True(advertised["exportTilesAllowed"]!.GetValue<bool>());
        Assert.Equal(5000, advertised["maxExportTilesCount"]!.GetValue<long>());
        Assert.Equal("TilesOnly,Tilemap", advertised["capabilities"]!.GetValue<string>());
        Assert.Equal("roads", advertised["name"]!.GetValue<string>());
    }

    [Fact]
    public void The_policy_narrows_the_servers_ceiling_and_never_raises_it()
    {
        Assert.Equal(100_000, TileExportPolicy.Off.MaximumOf(100_000));
        Assert.Equal(5_000, new TileExportPolicy(true, false, 5_000).MaximumOf(100_000));
        Assert.Equal(100_000, new TileExportPolicy(true, false, 900_000).MaximumOf(100_000));
        Assert.NotNull(new TileExportPolicy(true, false, 0).Problem());
        Assert.NotNull(new TileExportPolicy(false, true, null).Problem());
        Assert.Null(new TileExportPolicy(true, true, 10).Problem());
    }

    [Theory]
    [InlineData("1,2,3", new[] { 1, 2, 3 })]
    [InlineData("1-4, 7-9", new[] { 1, 2, 3, 4, 7, 8, 9 })]
    [InlineData(" 5 ", new[] { 5 })]
    [InlineData("3,1-2,2", new[] { 1, 2, 3 })]
    public void Levels_are_read_as_arcgis_writes_them(string text, int[] expected)
    {
        Assert.Null(VectorTileExportEndpoints.ParseLevels(text, out IReadOnlyList<int>? levels));
        Assert.Equal(expected, levels);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("4-1")]
    [InlineData("-1")]
    [InlineData("1-2-3")]
    [InlineData(",")]
    public void A_level_list_that_is_not_one_is_refused(string text) =>
        Assert.NotNull(VectorTileExportEndpoints.ParseLevels(text, out _));

    [Fact]
    public void No_level_list_is_the_services_default()
    {
        Assert.Null(VectorTileExportEndpoints.ParseLevels(null, out IReadOnlyList<int>? levels));
        Assert.Null(levels);
    }

    [Fact]
    public void A_token_is_128_random_bits_and_nothing_else_becomes_a_file_name()
    {
        string token = TileExporter.NewToken();

        Assert.True(TileExporter.IsToken(token));
        Assert.NotEqual(token, TileExporter.NewToken());
        Assert.Equal(Path.Combine(Path.GetFullPath(_directory), token + ".vtpk"),
            TileExporter.FileOf(_directory, token, TileExportFormat.Vtpk));
        Assert.Equal(Path.Combine(Path.GetFullPath(_directory), token + ".pmtiles"),
            TileExporter.FileOf(_directory, token, TileExportFormat.PmTiles));

        foreach (string hostile in new[]
                 {
                     "../../etc/passwd", "..", "", "ABCDEF0123456789ABCDEF0123456789", token + "/x",
                     token[..31], token + "0", "0123456789abcdef0123456789abcde\\", "0123456789abcdef0123456789abc../",
                 })
        {
            Assert.False(TileExporter.IsToken(hostile));
            Assert.Null(TileExporter.FileOf(_directory, hostile, TileExportFormat.Vtpk));
            Assert.Null(TileExporter.PartOf(_directory, hostile));
            Assert.Null(TileExporter.StagingOf(_directory, hostile));
        }
    }

    [Fact]
    public void An_export_is_rerun_from_the_start_and_is_harmless_to_repeat() =>
        Assert.Equal(JobRerun.Harmless, JobKinds.RerunOf(JobKind.TileExport));

    [Fact]
    public void The_job_statuses_are_the_geoprocessing_words()
    {
        Assert.Equal("esriJobSubmitted", VectorTileExportEndpoints.StatusOf(JobStatus.Queued));
        Assert.Equal("esriJobExecuting", VectorTileExportEndpoints.StatusOf(JobStatus.Running));
        Assert.Equal("esriJobSucceeded", VectorTileExportEndpoints.StatusOf(JobStatus.Done));
        Assert.Equal("esriJobFailed", VectorTileExportEndpoints.StatusOf(JobStatus.Failed));
        Assert.Equal("esriJobCancelled", VectorTileExportEndpoints.StatusOf(JobStatus.Cancelled));
    }

    [Fact]
    public void Web_mercator_goes_to_degrees_and_back()
    {
        Envelope degrees = new(26.0, 36.0, 45.0, 42.0);
        Envelope there = TileExportPackage.ToGeographic(TileSeedPlan.FromGeographic(degrees));

        Assert.Equal(degrees.MinX, there.MinX, 9);
        Assert.Equal(degrees.MinY, there.MinY, 9);
        Assert.Equal(degrees.MaxX, there.MaxX, 9);
        Assert.Equal(degrees.MaxY, there.MaxY, 9);
    }

    [Fact]
    public void A_package_carries_every_glyph_range_of_every_stack_its_style_names_under_that_name()
    {
        string root = Path.Combine(_directory, "glyphs");
        Directory.CreateDirectory(Path.Combine(root, GlyphStore.Fallback));
        File.WriteAllBytes(Path.Combine(root, GlyphStore.Fallback, "0-255.pbf"), [1]);
        File.WriteAllBytes(Path.Combine(root, GlyphStore.Fallback, "256-511.pbf"), [2]);

        GlyphStore glyphs = new(root);
        Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);

        TileExportPackage.Fonts(
            """
            {"version":8,"glyphs":"../fonts/{fontstack}/{range}.pbf","layers":[
              {"id":"a","type":"symbol","layout":{"text-field":"{name}","text-font":["Noto Sans Bold","Arial"]}},
              {"id":"b","type":"symbol","layout":{"text-field":"{name}"}},
              {"id":"c","type":"symbol","layout":{"text-font":["literal",["../evil"]]}},
              {"id":"d","type":"line"}
            ]}
            """,
            glyphs,
            resources);

        Assert.Equal(
            [
                "fonts/Noto Sans Bold,Arial/0-255.pbf",
                "fonts/Noto Sans Bold,Arial/256-511.pbf",
                "fonts/Open Sans Regular,Arial Unicode MS Regular/0-255.pbf",
                "fonts/Open Sans Regular,Arial Unicode MS Regular/256-511.pbf",
            ],
            resources.Keys.Order(StringComparer.Ordinal));

        // A style that draws labels from an absolute URL carries nothing: the package cannot serve that host.
        Dictionary<string, byte[]> none = new(StringComparer.Ordinal);
        TileExportPackage.Fonts("""{"glyphs":"https://example.org/{fontstack}/{range}.pbf","layers":[]}""", glyphs, none);
        Assert.Empty(none);
    }

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Request(
        string method, bool signedIn, string? cookie = null, string? query = null, string? bearer = null,
        string? esri = null, string? fetchSite = null)
    {
        Microsoft.AspNetCore.Http.DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = "/rest/services/hosted/roads/VectorTileServer/exportTiles";
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query ?? "?levels=0&f=json");

        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{Authentication.SessionCookie}={cookie}";
        }

        if (bearer is not null)
        {
            context.Request.Headers.Authorization = "Bearer " + bearer;
        }

        if (esri is not null)
        {
            context.Request.Headers["X-Esri-Authorization"] = "Bearer " + esri;
        }

        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        context.Features.Set(new RequestPrincipal(
            signedIn
                ? new Principal(Guid.NewGuid(), PrincipalKind.User, "someone", null, isDisabled: false)
                : Principal.Anonymous,
            signedIn ? Guid.NewGuid() : null,
            Authorization.Nothing));

        return context;
    }

    [Fact]
    public void A_cookie_only_get_of_exportTiles_is_refused()
    {
        string? refused = VectorTileExportEndpoints.CrossSiteByCookie(Request("GET", signedIn: true, cookie: "c"));

        Assert.NotNull(refused);
        Assert.Contains("token", refused, StringComparison.Ordinal);
        Assert.Contains("/admin/services/{name}/exports", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cookie_request_from_another_site_is_refused_and_one_from_this_server_is_not()
    {
        Assert.NotNull(VectorTileExportEndpoints.CrossSiteByCookie(
            Request("GET", signedIn: true, cookie: "c", fetchSite: "cross-site")));
        Assert.NotNull(VectorTileExportEndpoints.CrossSiteByCookie(
            Request("GET", signedIn: true, cookie: "c", fetchSite: "same-site")));
        Assert.Null(VectorTileExportEndpoints.CrossSiteByCookie(
            Request("GET", signedIn: true, cookie: "c", fetchSite: "same-origin")));
    }

    [Theory]
    [InlineData("query")]
    [InlineData("bearer")]
    [InlineData("esri")]
    public void A_token_signed_request_is_not_asked_even_with_the_cookie_beside_it(string channel)
    {
        Microsoft.AspNetCore.Http.DefaultHttpContext context = channel switch
        {
            "query" => Request("GET", signedIn: true, cookie: "c", query: "?levels=0&f=json&token=t", fetchSite: "cross-site"),
            "bearer" => Request("POST", signedIn: true, cookie: "c", bearer: "t", fetchSite: "cross-site"),
            _ => Request("GET", signedIn: true, cookie: "c", esri: "t", fetchSite: "cross-site"),
        };

        Assert.Null(VectorTileExportEndpoints.CrossSiteByCookie(context));
    }

    [Fact]
    public void An_anonymous_request_is_not_a_forgery_because_it_acts_in_nobodys_name() =>
        Assert.Null(VectorTileExportEndpoints.CrossSiteByCookie(Request("GET", signedIn: false, fetchSite: "cross-site")));

    [Fact]
    public void A_pmtiles_export_of_a_service_on_another_grid_is_refused_with_the_reason()
    {
        Assert.Contains("Web Mercator", TileExportPackage.WhyNotPmTiles(Service()), StringComparison.Ordinal);
    }
}
