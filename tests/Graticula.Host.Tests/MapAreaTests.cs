using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Geometries;
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
/// ADR-288 §11a's preplanned map areas, the parts that decide without a server: how a map's layer addresses become
/// services, the request a package is made from, the two items' shapes, who sees them, how they are related, how a package
/// is streamed, and what putting a package away does.
/// </summary>
/// <remarks>
/// Written 2026-11-12. The route itself, which needs a datastore and a writer, is the conformance suite's; whether Field
/// Maps takes what these answer is ADR-288's condition 3, on a device.
/// </remarks>
public sealed class MapAreaTests : IDisposable
{
    private const string MapId = "0123456789abcdef0123456789abcdef";
    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1794286130989);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mapareas-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------ fakes

    private sealed class StubMaps(params WebMap[] maps) : IWebMapStore
    {
        public Task<WebMap?> FindAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(maps.FirstOrDefault(m => m.Id == id));

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FeatureServiceUrlsAsync(
            IReadOnlyCollection<string> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());

        public Task<IReadOnlyList<WebMap>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebMap>>(maps);

        public Task<OwnerChange> ChangeOwnerAsync(string id, string receiver, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WebMap> CreateAsync(string title, string? snippet, Guid owner, SharingScope sharing, string document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WebMap?> UpdateAsync(string id, string title, string? snippet, SharingScope sharing, string document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SetTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetDescriptionAsync(string id, string? description, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetThumbnailAsync(string id, byte[] png, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]?> ThumbnailAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetDeleteProtectedAsync(string id, bool protectedFromDeletion, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryAreas : IMapAreaStore
    {
        public List<MapArea> Areas { get; } = [];

        public List<MapAreaPackage> Packages { get; } = [];

        public Task<bool> CreateAsync(MapArea area, CancellationToken cancellationToken)
        {
            Areas.Add(area);
            return Task.FromResult(true);
        }

        public Task<bool> AddPackageAsync(MapAreaPackage package, string title, CancellationToken cancellationToken)
        {
            Packages.Add(package);
            return Task.FromResult(true);
        }

        public Task<bool> ReplacePackageAsync(MapAreaPackage package, DateTimeOffset refreshed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SetStatusAsync(Guid id, string status, DateTimeOffset refreshed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> ItemKindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(Areas.Any(a => a.Id == id) ? MapAreas.AreaKind : Packages.Any(p => p.Id == id) ? MapAreas.PackageKind : null);

        public Task<MapArea?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Areas.FirstOrDefault(a => a.Id == id));

        public Task<IReadOnlyList<MapArea>> ListAsync(IReadOnlyCollection<string> mapIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapArea>>([.. Areas.Where(a => mapIds.Contains(a.MapId))]);

        public Task<MapAreaPackage?> FindPackageAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Packages.FirstOrDefault(p => p.Id == id));

        public Task<IReadOnlyList<MapAreaPackage>> PackagesAsync(IReadOnlyCollection<Guid> areaIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapAreaPackage>>([.. Packages.Where(p => areaIds.Contains(p.AreaId))]);

        public Task<IReadOnlyList<MapAreaPackage>> PackagesOfMapAsync(string mapId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapAreaPackage>>([.. Packages.Where(p => p.MapId == mapId)]);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryReplicas(params Guid[] registered) : IOfflineSyncStore
    {
        public List<Guid> Registered { get; } = [.. registered];

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Registered.Remove(id));

        public Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateAsync(ReplicaRecord replica, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReplicaRecord?> FindAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReplicaRecord>> ListAsync(Guid serviceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AdvanceAsync(Guid id, IReadOnlyList<ReplicaLayer> layers, DateTimeOffset synced, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static WebMap Map(SharingScope sharing) => new(MapId, "Hydrants map", null, Owner, "owner", sharing, "{}", At, At);

    private static MapArea Area(string mapId = MapId) =>
        new(Guid.NewGuid(), mapId, "North", 3600000, 4800000, 3700000, 4900000, 3857, MapAreas.Complete, At, At.AddHours(1), Owner, "owner");

    private static MapAreaPackage Package(MapArea area, string file = "map-area-package-files/0123456789abcdef0123456789abcdef.geodatabase") => new(
        Guid.NewGuid(), area.Id, area.MapId, Guid.NewGuid(), Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        "SDS_FS_1794286130989.geodatabase", file, new string('a', 64), 12, PropertiesJson, At, At.AddHours(1));

    private const string PropertiesJson =
        """
        {"url":"https://h/rest/services/hosted/Hydrants/FeatureServer","layers":[0,1],
         "layerQueries":{"0":{"queryOption":"useFilter","where":"","useGeometry":true}},
         "replicaID":"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE","layerServerGens":[{"serverGen":1794286130989,"id":0}],
         "returnAttachments":true,"attachmentsSyncDirection":"bidirectional","syncModel":"perLayer",
         "useSyncForRefresh":true,"hasUtilityNetworkLayer":false}
        """;

    private static RequestPrincipal Member(Guid id, string name) => new(
        new Principal(id, PrincipalKind.User, name, name, isDisabled: false), Guid.NewGuid(),
        Authorization.Resolve(UserTypes.Unrestricted, [Roles.User]));

    private static RequestPrincipal Administrator() => new(
        new Principal(Guid.NewGuid(), PrincipalKind.User, "boss", "boss", isDisabled: false), Guid.NewGuid(),
        Authorization.Resolve(UserTypes.Unrestricted, [Roles.Administrator]));

    private static readonly RequestPrincipal Anonymous = new(Principal.Anonymous, null, Authorization.Nothing);

    private DefaultHttpContext Context(RequestPrincipal who, MemoryAreas areas, WebMap? map, MemoryReplicas? replicas = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IMapAreaStore>(areas);
        services.AddSingleton<IWebMapStore>(new StubMaps(map is null ? [] : [map]));
        services.AddSingleton<IOfflineSyncStore>(replicas ?? new MemoryReplicas());
        services.AddSingleton(new ItemAnnotations(null!, NullLogger<ItemAnnotations>.Instance));
        services.AddSingleton(new MapAreaPackageFiles(_directory));

        DefaultHttpContext context = new() { RequestServices = services.BuildServiceProvider() };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("portal.example");
        context.Features.Set(who);
        context.Response.Body = new MemoryStream();
        return context;
    }

    // ------------------------------------------------------------------------------------------------ the map's services

    [Fact]
    public void A_maps_layer_addresses_become_its_services_with_the_layers_it_uses_of_each()
    {
        IReadOnlyList<MapAreaPackaging.MapService> services = MapAreaPackaging.ServicesOf(
        [
            "https://h/rest/services/hosted/Hydrants/FeatureServer/1",
            "https://h/rest/services/hosted/Hydrants/FeatureServer/0?token=x",
            "https://h/studio/rest/services/Roads/FeatureServer",
            "https://h/rest/services/hosted/Hydrants/FeatureServer/1",
            "https://h/rest/services/hosted/Tiles/VectorTileServer",
            "not an address",
        ]);

        Assert.Equal(2, services.Count);
        Assert.Equal("hosted/Hydrants", services[0].Name);
        Assert.Equal("https://h/rest/services/hosted/Hydrants/FeatureServer", services[0].Address);
        Assert.Equal([0, 1], services[0].Layers);

        // The address is the map's own, /studio included, because that is the address the map is told.
        Assert.Equal("Roads", services[1].Name);
        Assert.Equal("https://h/studio/rest/services/Roads/FeatureServer", services[1].Address);
        Assert.Empty(services[1].Layers);
    }

    // ------------------------------------------------------------------------------------------------ the request

    [Fact]
    public void A_packages_request_is_read_as_createReplica_reads_one_and_takes_the_area_as_its_filter()
    {
        MapArea area = Area();
        Func<string, string?> fields = MapAreaPackaging.RequestFields("SDS_FS_1", [1, 0], area);

        PublishedService service = ServiceWithLayers(2);
        Assert.True(OfflineReplicaRequests.TryReadCreate(fields, service, out CreateReplicaRequest? request, out string? error), error);

        Assert.True(request!.Registered);
        Assert.Equal("SDS_FS_1", request.Name);
        Assert.Equal([0, 1], request.Layers);
        Assert.Equal(3857, request.Srid);
        Assert.True(request.ReturnAttachments);
        Assert.Equal("bidirectional", request.AttachmentsSyncDirection);
        Assert.False(request.Async);
        Assert.Equal(3600000, request.Extent!.Value.MinX);
        Assert.Equal(4900000, request.Extent.Value.MaxY);
        Assert.NotNull(request.Filter);
        Assert.All(request.Queries.Values, q => Assert.Equal(("useFilter", true), (q.QueryOption, q.UseGeometry)));
    }

    private static PublishedService ServiceWithLayers(int count)
    {
        PublishedLayer Layer(int index) => new(
            Guid.NewGuid(),
            new LayerDefinition("l" + index, "public", "l" + index, "geom", 3857, "objectid", "objectid", isHosted: true),
            "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Private, ServiceStatus.Started,
            layerIndex: index, serviceName: "Hydrants");

        return new PublishedService(
            Guid.NewGuid(), "Hydrants", "hosted", "FeatureServer", null, null, SharingScope.Private, ServiceStatus.Started,
            [.. Enumerable.Range(0, count).Select(Layer)])
        { SyncEnabled = true };
    }

    [Fact]
    public void A_replica_gets_the_name_Enterprise_gave_its_packages()
    {
        Assert.Equal("SDS_FS_1794286130989", MapAreaPackaging.ReplicaNameAt(At));
        Assert.True(OfflineReplicaRequests.IsReplicaName(MapAreaPackaging.ReplicaNameAt(At)));
    }

    [Theory]
    [InlineData("""{"xmin":1,"ymin":2,"xmax":3,"ymax":4,"spatialReference":{"wkid":4326}}""", true, 4326)]
    [InlineData("""{"xmin":1,"ymin":2,"xmax":3,"ymax":4,"spatialReference":{"latestWkid":3857}}""", true, 3857)]
    [InlineData("""{"xmin":3,"ymin":2,"xmax":1,"ymax":4,"spatialReference":{"wkid":4326}}""", false, 0)]
    [InlineData("""{"xmin":1,"ymin":2,"xmax":3,"ymax":4}""", false, 0)]
    [InlineData("""{"xmin":1,"ymin":2,"xmax":3,"spatialReference":{"wkid":4326}}""", false, 0)]
    [InlineData("""{"xmin":"1","ymin":2,"xmax":3,"ymax":4,"spatialReference":{"wkid":4326}}""", false, 0)]
    [InlineData("""[1,2,3,4]""", false, 0)]
    public void An_extent_is_an_envelope_with_a_reference_and_some_width_and_height(string json, bool valid, int wkid)
    {
        Assert.Equal(valid, AdminEndpoints.TryReadAreaExtent(JsonNode.Parse(json), out Envelope extent, out int read, out string? why));

        if (valid)
        {
            Assert.Equal((1, 2, 3, 4), (extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
            Assert.Equal(wkid, read);
            Assert.Null(why);
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(why));
        }
    }

    [Fact]
    public void No_extent_at_all_is_refused_by_name()
    {
        Assert.False(AdminEndpoints.TryReadAreaExtent(null, out _, out _, out string? why));
        Assert.Contains("extent is required", why, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ the items

    [Fact]
    public void A_map_area_is_an_item_of_type_Map_Area_with_its_extent_and_status_in_its_properties()
    {
        MapArea area = Area();
        MemoryAreas areas = new() { Areas = { area } };
        HttpContext context = Context(Member(Owner, "owner"), areas, Map(SharingScope.Organization));

        JsonElement item = Json(PortalEndpoints.MapAreaItem(context, Map(SharingScope.Organization), area));

        Assert.Equal(area.Id.ToString("N"), item.GetProperty("id").GetString());
        Assert.Equal("Map Area", item.GetProperty("type").GetString());
        Assert.Equal(["Map", "Map Area"], item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal("North", item.GetProperty("title").GetString());
        Assert.Equal("org", item.GetProperty("access").GetString());
        Assert.Equal("owner", item.GetProperty("owner").GetString());
        Assert.Equal("complete", item.GetProperty("properties").GetProperty("status").GetString());

        JsonElement extent = item.GetProperty("properties").GetProperty("extent");
        Assert.Equal(3600000, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(3857, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        // The item's own extent is in degrees: Web Mercator 3.6e6, 4.8e6 is near 32.3 E, 39.5 N.
        double[][] corners = [.. item.GetProperty("extent").EnumerateArray().Select(c => c.EnumerateArray().Select(n => n.GetDouble()).ToArray())];
        Assert.InRange(corners[0][0], 32.3, 32.4);
        Assert.InRange(corners[0][1], 39.5, 39.6);
    }

    [Fact]
    public void An_area_kept_in_degrees_states_its_extent_in_its_Web_Mercator_maps_reference()
    {
        // Measured 2026-11-12: Field Maps refused every location in a preplanned area of a 102100 map whose extent was in
        // degrees ("The location is outside the map area").
        MapArea area = new(Guid.NewGuid(), MapId, "Probe", 28.9, 40.95, 29.1, 41.1, 4326, MapAreas.Complete, At, At.AddHours(1), Owner, "owner");
        WebMap map = new(MapId, "Probe map", null, Owner, "owner", SharingScope.Organization, """{"spatialReference":{"wkid":102100}}""", At, At);
        HttpContext context = Context(Member(Owner, "owner"), new MemoryAreas { Areas = { area } }, map);

        JsonElement extent = Json(PortalEndpoints.MapAreaItem(context, map, area)).GetProperty("properties").GetProperty("extent");

        Assert.Equal(102100, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.InRange(extent.GetProperty("xmin").GetDouble(), 3_217_100, 3_217_200); // 28.9 E
        Assert.InRange(extent.GetProperty("ymin").GetDouble(), 5_004_900, 5_005_000); // 40.95 N

        // A map in degrees with an area in degrees is left as it was kept.
        WebMap geographic = map with { Document = """{"spatialReference":{"wkid":4326}}""" };
        Assert.Equal(4326, Json(PortalEndpoints.MapAreaItem(context, geographic, area)).GetProperty("properties").GetProperty("extent")
            .GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void A_package_is_an_item_of_type_SQLite_Geodatabase_with_the_keywords_and_properties_Enterprise_gives()
    {
        MapArea area = Area();
        MapAreaPackage package = Package(area);
        HttpContext context = Context(Member(Owner, "owner"), new MemoryAreas(), Map(SharingScope.Public));

        JsonElement item = Json(PortalEndpoints.MapAreaPackageItem(context, Map(SharingScope.Public), area, package, "North - Hydrants"));

        Assert.Equal("SQLite Geodatabase", item.GetProperty("type").GetString());
        Assert.Equal(
            ["Bidirectional", "Data", "MapAreaPackage", "Package",
                "ReferenceURL#https://h/rest/services/hosted/Hydrants/FeatureServer", "Replica", "SQLite Geodatabase", "Syncable"],
            item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal("SDS_FS_1794286130989.geodatabase", item.GetProperty("name").GetString());
        Assert.Equal(12, item.GetProperty("size").GetInt64());
        Assert.Equal("public", item.GetProperty("access").GetString());

        JsonElement properties = item.GetProperty("properties");
        foreach (string key in new[]
        {
            "url", "layers", "extent", "layerQueries", "replicaID", "layerServerGens", "returnAttachments",
            "attachmentsSyncDirection", "syncModel", "useSyncForRefresh", "hasUtilityNetworkLayer",
        })
        {
            Assert.True(properties.TryGetProperty(key, out _), $"properties has no '{key}'.");
        }

        Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", properties.GetProperty("replicaID").GetString());
        Assert.Equal(1794286130989, properties.GetProperty("layerServerGens")[0].GetProperty("serverGen").GetInt64());
        Assert.False(properties.GetProperty("hasUtilityNetworkLayer").GetBoolean());
        Assert.Equal(3857, properties.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void Another_reader_is_not_told_who_owns_the_item_and_an_anonymous_one_is_not_told_an_owner_either()
    {
        MapArea area = Area();
        WebMap map = Map(SharingScope.Public);

        JsonElement other = Json(PortalEndpoints.MapAreaItem(
            Context(Member(Guid.NewGuid(), "reader"), new MemoryAreas(), map), map, area));
        JsonElement nobody = Json(PortalEndpoints.MapAreaItem(Context(Anonymous, new MemoryAreas(), map), map, area));

        Assert.Equal("graticula", other.GetProperty("owner").GetString());
        Assert.Equal("graticula", nobody.GetProperty("owner").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("itemControl").ValueKind);
    }

    [Fact]
    public void A_package_is_titled_by_its_area_and_its_service()
    {
        Assert.Equal("North - Hydrants", MapAreaPackaging.TitleOf("North", "https://h/rest/services/hosted/Hydrants/FeatureServer"));
        Assert.Equal("North - package", MapAreaPackaging.TitleOf("North", "garbage"));
    }

    [Theory]
    [InlineData(4326, true)]
    [InlineData(3857, true)]
    [InlineData(102100, true)]
    [InlineData(2154, false)]
    public void An_area_in_a_reference_without_a_formula_has_no_degrees_extent(int wkid, bool known)
    {
        MapArea area = Area() with { Wkid = wkid, XMin = 1, YMin = 2, XMax = 3, YMax = 4 };
        Assert.Equal(known, PortalEndpoints.DegreesOf(area).Length == 2);
    }

    // ------------------------------------------------------------------------------------------------ who sees them

    [Fact]
    public async Task An_area_and_its_package_are_seen_exactly_when_their_map_is()
    {
        MapArea area = Area();
        MapAreaPackage package = Package(area);
        MemoryAreas areas = new() { Areas = { area }, Packages = { package } };

        foreach ((SharingScope sharing, bool owner, bool member, bool anonymous) in new[]
        {
            (SharingScope.Private, true, false, false),
            (SharingScope.Organization, true, true, false),
            (SharingScope.Public, true, true, true),
        })
        {
            WebMap map = Map(sharing);

            foreach ((RequestPrincipal who, bool sees, string label) in new[]
            {
                (Member(Owner, "owner"), owner, "the map's owner"),
                (Member(Guid.NewGuid(), "reader"), member, "another member"),
                (Anonymous, anonymous, "an anonymous caller"),
            })
            {
                HttpContext context = Context(who, areas, map);

                Assert.True(
                    sees == (await PortalEndpoints.ReadableMapAreaAsync(context, area.Id.ToString("N"), CancellationToken.None) is not null),
                    $"{label} and an area of a {sharing} map");
                Assert.True(
                    sees == (await PortalEndpoints.ReadableMapAreaAsync(context, package.Id.ToString("N"), CancellationToken.None) is not null),
                    $"{label} and a package of a {sharing} map");
            }
        }
    }

    [Fact]
    public async Task An_administrator_sees_the_areas_of_a_private_map_and_an_id_that_names_neither_is_nothing()
    {
        MapArea area = Area();
        MemoryAreas areas = new() { Areas = { area } };
        HttpContext context = Context(Administrator(), areas, Map(SharingScope.Private));

        Assert.NotNull(await PortalEndpoints.ReadableMapAreaAsync(context, area.Id.ToString("N"), CancellationToken.None));
        Assert.Null(await PortalEndpoints.ReadableMapAreaAsync(context, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Null(await PortalEndpoints.ReadableMapAreaAsync(context, "not-a-guid", CancellationToken.None));
    }

    [Fact]
    public async Task An_area_whose_map_is_gone_is_nothing()
    {
        MapArea area = Area();
        HttpContext context = Context(Member(Owner, "owner"), new MemoryAreas { Areas = { area } }, map: null);

        Assert.Null(await PortalEndpoints.ReadableMapAreaAsync(context, area.Id.ToString("N"), CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------------ relationships

    [Fact]
    public async Task A_web_map_an_area_and_a_package_can_each_be_an_end_of_a_relationship_and_are_answered_when_readable()
    {
        MapArea area = Area();
        MapAreaPackage package = Package(area);
        MemoryAreas areas = new() { Areas = { area }, Packages = { package } };
        WebMap map = Map(SharingScope.Organization);
        HttpContext context = Context(Member(Owner, "owner"), areas, map);

        // Origins: the map, the area and the package are each found.
        Assert.Equal(Guid.Parse(MapId), await PortalEndpoints.MapAreaRelatableAsync(context, MapId, CancellationToken.None));
        Assert.Equal(area.Id, await PortalEndpoints.MapAreaRelatableAsync(context, area.Id.ToString("N"), CancellationToken.None));
        Assert.Equal(package.Id, await PortalEndpoints.MapAreaRelatableAsync(context, package.Id.ToString("N"), CancellationToken.None));
        Assert.Null(await PortalEndpoints.MapAreaRelatableAsync(context, Guid.NewGuid().ToString("N"), CancellationToken.None));

        // Destinations, in either direction: the area from the map, the map from the area, the package from the area, the area
        // from the package.
        Assert.Equal("Map Area", TypeOf(await PortalEndpoints.MapAreaRelatedItemAsync(context, area.Id, CancellationToken.None)));
        Assert.Equal("SQLite Geodatabase", TypeOf(await PortalEndpoints.MapAreaRelatedItemAsync(context, package.Id, CancellationToken.None)));
        Assert.Equal("Web Map", TypeOf(await PortalEndpoints.MapAreaRelatedItemAsync(context, Guid.Parse(MapId), CancellationToken.None)));
    }

    [Fact]
    public async Task A_relationship_to_an_item_the_caller_may_not_open_is_not_answered()
    {
        MapArea area = Area();
        MemoryAreas areas = new() { Areas = { area } };
        HttpContext stranger = Context(Member(Guid.NewGuid(), "stranger"), areas, Map(SharingScope.Private));

        Assert.Null(await PortalEndpoints.MapAreaRelatedItemAsync(stranger, area.Id, CancellationToken.None));
        Assert.Null(await PortalEndpoints.MapAreaRelatedItemAsync(stranger, Guid.Parse(MapId), CancellationToken.None));
        Assert.Null(await PortalEndpoints.MapAreaRelatableAsync(stranger, MapId, CancellationToken.None));
    }

    [Theory]
    [InlineData("Map2Area", "Map2Area")]
    [InlineData("area2package", "Area2Package")]
    [InlineData("Service2Data", null)]
    [InlineData("Bogus", null)]
    public void The_two_relationship_names_are_matched_without_regard_to_case(string asked, string? kept) =>
        Assert.Equal(kept, PortalEndpoints.MapAreaRelationshipNamed(asked));

    private static string? TypeOf(object? item) => item is null ? null : Json(item).GetProperty("type").GetString();

    private static JsonElement Json(object item) => JsonSerializer.SerializeToElement(item);

    // ------------------------------------------------------------------------------------------------ the file

    [Fact]
    public async Task A_packages_data_is_its_file_streamed_with_ranges_and_an_entity_tag()
    {
        MapAreaPackageFiles files = new(_directory);
        (string path, string recorded) = files.Next();
        byte[] content = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 251))];
        await File.WriteAllBytesAsync(path, content);

        MapArea area = Area();
        MapAreaPackage package = Package(area, recorded) with { Size = content.Length };

        // The whole file.
        HttpContext whole = Context(Member(Owner, "owner"), new MemoryAreas(), Map(SharingScope.Private));
        await PortalEndpoints.MapAreaPackageFile(whole, package).ExecuteAsync(whole);

        Assert.Equal(200, whole.Response.StatusCode);
        Assert.Equal("bytes", whole.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("\"" + package.Sha256 + "\"", whole.Response.Headers.ETag.ToString());
        Assert.Equal(content, Body(whole));

        // A range of it.
        HttpContext part = Context(Member(Owner, "owner"), new MemoryAreas(), Map(SharingScope.Private));
        part.Request.Method = HttpMethods.Get;
        part.Request.Headers.Range = "bytes=100-199";
        await PortalEndpoints.MapAreaPackageFile(part, package).ExecuteAsync(part);

        Assert.Equal(206, part.Response.StatusCode);
        Assert.Equal($"bytes 100-199/{content.Length}", part.Response.Headers.ContentRange.ToString());
        Assert.Equal(content[100..200], Body(part));
    }

    [Fact]
    public async Task A_package_whose_file_is_gone_is_said_to_be_gone_and_a_name_outside_the_folder_is_never_opened()
    {
        MapArea area = Area();
        HttpContext gone = Context(Member(Owner, "owner"), new MemoryAreas(), Map(SharingScope.Private));
        await PortalEndpoints.MapAreaPackageFile(gone, Package(area)).ExecuteAsync(gone);
        Assert.Equal(404, gone.Response.StatusCode);
        Assert.Contains("no longer held", Encoding.UTF8.GetString(Body(gone)), StringComparison.Ordinal);

        MapAreaPackageFiles files = new(_directory);
        Assert.Null(files.PathOf("../secrets.geodatabase"));
        Assert.Null(files.PathOf("map-area-package-files/../../x.geodatabase"));
        Assert.Null(files.PathOf("map-area-package-files/ABCDEF0123456789abcdef0123456789.geodatabase"));
        Assert.Null(files.PathOf("scene-package-files/0123456789abcdef0123456789abcdef.slpk"));
        Assert.Null(files.PathOf(null));
        Assert.NotNull(files.PathOf("map-area-package-files/0123456789abcdef0123456789abcdef.geodatabase"));
    }

    private static byte[] Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using MemoryStream copy = new();
        context.Response.Body.CopyTo(copy);
        return copy.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ putting packages away

    [Fact]
    public async Task Putting_a_package_away_unregisters_its_replica_and_removes_its_file_and_one_that_is_gone_is_no_error()
    {
        MapAreaPackageFiles files = new(_directory);
        (string path, string recorded) = files.Next();
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        MapArea area = Area();
        MapAreaPackage held = Package(area, recorded);
        MapAreaPackage lost = Package(area, "map-area-package-files/fedcba9876543210fedcba9876543210.geodatabase");
        MemoryReplicas replicas = new(held.ReplicaId!.Value);
        HttpContext context = Context(Member(Owner, "owner"), new MemoryAreas(), Map(SharingScope.Private), replicas);

        await MapAreaPackaging.DiscardAsync(context.RequestServices, [held, lost], CancellationToken.None);

        Assert.Empty(replicas.Registered);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Migration_139_is_in_the_set_and_the_map_area_items_are_in_it()
    {
        Assert.True(PlatformMigrations.ComponentSchemaVersion >= new SchemaVersion(139));
        Assert.Contains(PlatformMigrations.All.All, m => m.Version == new SchemaVersion(139));
    }
}
