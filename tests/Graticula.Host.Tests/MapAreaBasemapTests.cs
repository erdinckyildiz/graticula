using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-288 §11a (the owner's change of 2026-11-12): a preplanned map area also packages its basemap. The parts that decide
/// without a server — which basemap layers can be packaged and which are named as skipped, the tile package's item, its
/// data, and what putting it away does. Writing the VTPK itself is the exporter's, on a datastore.
/// </summary>
public sealed class MapAreaBasemapTests : IDisposable
{
    private const string MapId = "0123456789abcdef0123456789abcdef";
    private const string Origin = "https://portal.example";
    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1794286130989);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mapareas-tiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static MapArea Area() =>
        new(Guid.NewGuid(), MapId, "North", 3600000, 4800000, 3700000, 4900000, 3857, MapAreas.Complete, At, At.AddHours(1), Owner, "owner");

    private static MapAreaPackage TilePackage(MapArea area, string file) => new(
        Guid.NewGuid(), area.Id, area.MapId, Guid.NewGuid(), null, "streets.vtpk", file, new string('b', 64), 4096,
        """{"url":"https://portal.example/rest/services/hosted/streets/VectorTileServer","format":"vtpk","levels":[0,1,2],"tiles":21}""",
        At, At.AddHours(1), MapAreas.VtpkFormat);

    private DefaultHttpContext Context()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new ItemAnnotations(null!, NullLogger<ItemAnnotations>.Instance));
        services.AddSingleton(new MapAreaPackageFiles(_directory));
        services.AddSingleton<IOfflineSyncStore>(new Replicas());

        DefaultHttpContext context = new() { RequestServices = services.BuildServiceProvider() };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("portal.example");
        context.Features.Set(new RequestPrincipal(
            new Principal(Owner, PrincipalKind.User, "owner", "owner", isDisabled: false), Guid.NewGuid(),
            Authorization.Resolve(UserTypes.Unrestricted, [Roles.User])));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static WebMap Map() => new(MapId, "Hydrants map", null, Owner, "owner", SharingScope.Public, "{}", At, At);

    private static JsonElement Json(object item) => JsonSerializer.SerializeToElement(item);

    // ------------------------------------------------------------------------------------------------ the basemap's layers

    [Fact]
    public void A_maps_basemap_layers_are_read_from_its_document_with_what_each_is()
    {
        const string document = """
            {"baseMap":{"title":"Streets","baseMapLayers":[
               {"id":"streets","layerType":"VectorTileLayer","title":"Streets","url":"https://portal.example/rest/services/hosted/streets/VectorTileServer?token=x"},
               {"id":"osm","layerType":"OpenStreetMap","title":"OpenStreetMap"},
               {"id":"esri","layerType":"ArcGISTiledMapServiceLayer","url":"https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer"}],
             "referenceLayers":[{"id":"labels","layerType":"VectorTileLayer","title":"Labels","url":"https://elsewhere.example/arcgis/rest/services/labels/VectorTileServer"}]},
             "operationalLayers":[]}
            """;

        IReadOnlyList<MapAreaPackaging.BasemapLayer> layers = MapAreaPackaging.BasemapLayersOf(document);

        Assert.Equal(["Streets", "OpenStreetMap", "esri", "Labels"], layers.Select(l => l.Title));
        Assert.Equal([true, false, false, true], layers.Select(l => l.IsVectorTile));
        Assert.Null(layers[1].Url);
        Assert.Empty(MapAreaPackaging.BasemapLayersOf("{}"));
        Assert.Empty(MapAreaPackaging.BasemapLayersOf("not json"));
        Assert.Empty(MapAreaPackaging.BasemapLayersOf(null));
    }

    [Fact]
    public void A_vector_tile_basemap_that_names_only_its_style_is_found_by_its_style()
    {
        // Measured 2026-11-12 on dev: the probe map's basemap had no url, only a styleUrl, and the area skipped it.
        const string document = """
            {"baseMap":{"baseMapLayers":[
               {"id":"t","layerType":"VectorTileLayer","title":"probe tiles","styleUrl":" https://dev.example/rest/services/hosted/probe/VectorTileServer/resources/styles/root.json "}]}}
            """;

        MapAreaPackaging.BasemapLayer layer = Assert.Single(MapAreaPackaging.BasemapLayersOf(document));

        Assert.Equal("https://dev.example/rest/services/hosted/probe/VectorTileServer", layer.Url);
        Assert.True(layer.IsVectorTile);
    }

    [Theory]
    [InlineData("https://portal.example/rest/services/hosted/streets/VectorTileServer", "hosted/streets")]
    [InlineData("https://portal.example/rest/services/streets/VectorTileServer?f=json", "streets")]
    [InlineData("/rest/services/streets/VectorTileServer", "streets")]
    [InlineData("https://portal.example/rest/services/streets/FeatureServer", null)]
    [InlineData("https://tiles.example/styles/basic.json", null)]
    public void A_vector_tile_address_names_its_service(string url, string? name) =>
        Assert.Equal(name, MapAreaPackaging.TileServiceNameOf(url));

    [Theory]
    [InlineData("https://portal.example/rest/services/streets/VectorTileServer", true)]
    [InlineData("HTTP://PORTAL.EXAMPLE/rest/services/streets/VectorTileServer", true)]
    [InlineData("/rest/services/streets/VectorTileServer", true)]
    [InlineData("https://elsewhere.example/rest/services/streets/VectorTileServer", false)]
    [InlineData("https://portal.example.evil.test/rest/services/streets/VectorTileServer", false)]
    public void A_service_at_another_host_is_not_ours_even_when_a_service_of_that_name_is(string url, bool ours) =>
        Assert.Equal(ours, MapAreaPackaging.IsOnThisServer(url, Origin));

    [Fact]
    public void The_address_a_package_states_is_the_layers_without_a_query_or_a_closing_slash()
    {
        Assert.Equal("https://portal.example/rest/services/streets/VectorTileServer",
            MapAreaPackaging.TileAddressOf("https://portal.example/rest/services/streets/VectorTileServer/?token=x#y"));
    }

    // ------------------------------------------------------------------------------------------------ the item

    [Fact]
    public void A_tile_package_is_an_item_of_type_Vector_Tile_Package_related_to_its_source_by_ReferenceURL()
    {
        MapArea area = Area();
        MapAreaPackage package = TilePackage(area, "map-area-package-files/0123456789abcdef0123456789abcdef.vtpk");

        JsonElement item = Json(PortalEndpoints.MapAreaPackageItem(Context(), Map(), area, package, "North - streets"));

        Assert.Equal("Vector Tile Package", item.GetProperty("type").GetString());
        Assert.Equal(
            ["MapAreaPackage", "Package", "ReferenceURL#https://portal.example/rest/services/hosted/streets/VectorTileServer",
                "Vector Tile Package", "vtpk"],
            item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal("streets.vtpk", item.GetProperty("name").GetString());
        Assert.Equal(4096, item.GetProperty("size").GetInt64());
        Assert.Equal("public", item.GetProperty("access").GetString());
        Assert.Equal(21, item.GetProperty("properties").GetProperty("tiles").GetInt32());
        Assert.Equal(3857, item.GetProperty("properties").GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.False(item.GetProperty("properties").TryGetProperty("replicaID", out _), "A tile package states no replica.");
    }

    [Fact]
    public void A_tile_package_is_titled_by_its_area_and_its_service()
    {
        Assert.Equal("North - streets", MapAreaPackaging.TitleOf("North", "https://portal.example/rest/services/hosted/streets/VectorTileServer"));
    }

    [Fact]
    public async Task A_tile_packages_data_is_its_file_with_the_media_type_of_a_package()
    {
        MapAreaPackageFiles files = new(_directory);
        (string path, string recorded) = files.Next(".vtpk");
        byte[] content = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i % 199))];
        await File.WriteAllBytesAsync(path, content);

        MapAreaPackage package = TilePackage(Area(), recorded);
        HttpContext context = Context();

        await PortalEndpoints.MapAreaPackageFile(context, package).ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using MemoryStream copy = new();
        await context.Response.Body.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
        Assert.EndsWith(".vtpk", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_geodatabase_or_a_vtpk_is_a_package_file()
    {
        MapAreaPackageFiles files = new(_directory);

        Assert.NotNull(files.PathOf("map-area-package-files/0123456789abcdef0123456789abcdef.vtpk"));
        Assert.Null(files.PathOf("map-area-package-files/0123456789abcdef0123456789abcdef.exe"));
        Assert.Throws<ArgumentException>(() => files.Next(".exe"));
    }

    // ------------------------------------------------------------------------------------------------ putting it away

    [Fact]
    public async Task Putting_a_tile_package_away_removes_its_file_and_asks_no_replica_to_go()
    {
        MapAreaPackageFiles files = new(_directory);
        (string path, string recorded) = files.Next(".vtpk");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        HttpContext context = Context();

        await MapAreaPackaging.DiscardAsync(context.RequestServices, [TilePackage(Area(), recorded)], CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal(0, ((Replicas)context.RequestServices.GetRequiredService<IOfflineSyncStore>()).Deletes);
    }

    [Fact]
    public void Migration_140_lets_a_package_have_no_replica_and_is_the_level_this_build_was_written_against()
    {
        Assert.Equal(new SchemaVersion(140), PlatformMigrations.ComponentSchemaVersion);
        Assert.Contains(PlatformMigrations.All.All, m => m.Version == new SchemaVersion(139));
        Assert.Contains(PlatformMigrations.All.All, m => m.Version == new SchemaVersion(140));
    }

    private sealed class Replicas : IOfflineSyncStore
    {
        public int Deletes { get; private set; }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            Deletes++;
            return Task.FromResult(true);
        }

        public Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateAsync(ReplicaRecord replica, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplicaRecord?> FindAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReplicaRecord>> ListAsync(Guid serviceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AdvanceAsync(Guid id, IReadOnlyList<ReplicaLayer> layers, DateTimeOffset synced, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
