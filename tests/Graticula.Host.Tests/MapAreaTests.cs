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

        public Task<IReadOnlyList<MapAreaPackage>> PackagesOfServiceAsync(Guid serviceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapAreaPackage>>([.. Packages.Where(p => p.ServiceId == serviceId)]);

        public Task<IReadOnlyList<MapAreaPackage>> PackagesOfOwnerAsync(string memberName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapAreaPackage>>([]);

        public List<(string? MapId, Guid? ServiceId)> Marked { get; } = [];

        public Task<int> MarkNeedsRefreshAsync(string? mapId, Guid? serviceId, CancellationToken cancellationToken)
        {
            Marked.Add((mapId, serviceId));
            return Task.FromResult(1);
        }

        public Task<MapAreaPackage?> FindPackageByJobAsync(Guid job, CancellationToken cancellationToken) =>
            Task.FromResult(Packages.FirstOrDefault(p => p.ExportJobId == job));

        public Task<bool> MarkPackageProcessingAsync(Guid id, Guid job, string properties, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MapAreaPackage?> CompletePackageAsync(
            Guid job, string filePath, string sha256, long size, DateTimeOffset when, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> FailPackageAsync(Guid job, string error, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> FailStrandedPackagesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<string?> SettleStatusAsync(Guid id, DateTimeOffset refreshed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SetSettingsAsync(Guid id, MapAreaSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetOriginAsync(Guid id, string origin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<(MapArea Area, Guid Tenant)>> ScheduledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(MapArea, Guid)>>([]);

        public Task<IReadOnlySet<string>> FilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(Packages.Select(p => p.FilePath).ToHashSet());

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryReplicas(params Guid[] registered) : IOfflineSyncStore
    {
        public List<Guid> Registered { get; } = [.. registered];

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Registered.Remove(id));

        public Task<int> SetEnabledAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetChangeTrackingAsync(Guid serviceId, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

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

        // Read on an Enterprise 12.1 portal on 2026-11-15 (research note 5a.1): the item's own extent is an empty array and its
        // spatialReference is null, on every Map Area; the area's extent is in properties.
        Assert.Equal(JsonValueKind.Array, item.GetProperty("extent").ValueKind);
        Assert.Equal(0, item.GetProperty("extent").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("spatialReference").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("properties").GetProperty("packageRefreshSchedule").ValueKind);

        // Its size is the size of its /data text.
        Assert.Equal(Encoding.UTF8.GetByteCount(PortalEndpoints.MapAreaDataText(area)), item.GetProperty("size").GetInt32());
    }

    [Fact]
    public void A_map_areas_data_is_the_mapAreas_object_an_enterprise_portal_keeps()
    {
        // A weekly schedule, as read: cron 41 2 15 ? * TUE goes with {type weekly, dayOfWeek 2, nthDay 1}.
        QuartzCron cron = QuartzCron.TryParse("41 2 15 ? * TUE", out QuartzCron parsed, out _) ? parsed : throw new InvalidOperationException();
        string weekly = JsonSerializer.Serialize(cron.WeeklyParams(DateTimeOffset.Parse("2024-05-07T15:02:41.043Z")));
        MapArea scheduled = Area() with
        {
            PackageRefreshSchedule = "41 2 15 ? * TUE", RefreshParams = weekly, ScheduledUpdates = true, TileScaleMin = 147914382, TileScaleMax = 20000,
        };

        JsonElement data = JsonDocument.Parse(PortalEndpoints.MapAreaDataText(scheduled)).RootElement.GetProperty("mapAreas");
        Assert.Equal(147914382, data.GetProperty("mapAreaTileScale").GetProperty("minScale").GetDouble());
        Assert.Equal(20000, data.GetProperty("mapAreaTileScale").GetProperty("maxScale").GetDouble());
        Assert.Equal("weekly", data.GetProperty("mapAreaRefreshParams").GetProperty("type").GetString());
        Assert.Equal(2, data.GetProperty("mapAreaRefreshParams").GetProperty("dayOfWeek").GetInt32());
        Assert.Equal(1, data.GetProperty("mapAreaRefreshParams").GetProperty("nthDay").GetInt32());
        Assert.True(data.GetProperty("mapAreaRefreshParams").TryGetProperty("startDate", out _));
        Assert.True(data.GetProperty("mapAreasScheduledUpdatesEnabled").GetBoolean());

        // No schedule: {"type": "never"}, updates off, and the same three keys.
        JsonElement never = JsonDocument.Parse(PortalEndpoints.MapAreaDataText(Area())).RootElement.GetProperty("mapAreas");
        Assert.Equal("""{"type":"never"}""", never.GetProperty("mapAreaRefreshParams").GetRawText());
        Assert.False(never.GetProperty("mapAreasScheduledUpdatesEnabled").GetBoolean());
        Assert.Equal(["mapAreaTileScale", "mapAreaRefreshParams", "mapAreasScheduledUpdatesEnabled"], never.EnumerateObject().Select(p => p.Name));

        // The cron is the item's own property.
        HttpContext context = Context(Member(Owner, "owner"), new MemoryAreas { Areas = { scheduled } }, Map(SharingScope.Private));
        Assert.Equal("41 2 15 ? * TUE",
            Json(PortalEndpoints.MapAreaItem(context, Map(SharingScope.Private), scheduled)).GetProperty("properties").GetProperty("packageRefreshSchedule").GetString());
    }

    [Fact]
    public void Settings_are_read_as_a_weekly_schedule_and_a_scale_and_anything_else_is_refused_by_name()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        JsonNode scale = JsonNode.Parse("""{"minScale": 750000, "maxScale": 20000}""")!;

        Assert.True(AdminEndpoints.TryReadAreaSettings("0 30 2 ? * MON", true, null, scale, true, null, now, out MapAreaSettings ok, out string? why), why);
        Assert.Equal(("0 30 2 ? * MON", true, 750000d, 20000d), (ok.PackageRefreshSchedule, ok.ScheduledUpdates, ok.TileScaleMin, ok.TileScaleMax));
        Assert.Contains("\"dayOfWeek\":1", ok.RefreshParams, StringComparison.Ordinal);

        // Sent as off, a schedule is kept and does not run; null clears it; what is not sent is not changed.
        Assert.True(AdminEndpoints.TryReadAreaSettings("0 30 2 ? * MON", true, false, null, false, null, now, out MapAreaSettings off, out _));
        Assert.False(off.ScheduledUpdates);
        MapArea held = Area() with { PackageRefreshSchedule = ok.PackageRefreshSchedule, RefreshParams = ok.RefreshParams, ScheduledUpdates = true, TileScaleMin = 750000, TileScaleMax = 20000 };
        Assert.True(AdminEndpoints.TryReadAreaSettings(null, true, null, null, false, held, now, out MapAreaSettings cleared, out _));
        Assert.Equal((null, null, false, 750000d), (cleared.PackageRefreshSchedule, cleared.RefreshParams, cleared.ScheduledUpdates, cleared.TileScaleMin));
        Assert.True(AdminEndpoints.TryReadAreaSettings(null, false, null, null, true, held, now, out MapAreaSettings unscaled, out _));
        Assert.Equal(("0 30 2 ? * MON", true, null), (unscaled.PackageRefreshSchedule, unscaled.ScheduledUpdates, unscaled.TileScaleMin));

        Assert.False(AdminEndpoints.TryReadAreaSettings("nonsense", true, null, null, false, null, now, out _, out why));
        Assert.Contains("packageRefreshSchedule", why, StringComparison.Ordinal);
        Assert.False(AdminEndpoints.TryReadAreaSettings("0 0 3 * * ?", true, null, null, false, null, now, out _, out why));
        Assert.Contains("weekly", why, StringComparison.Ordinal);
        Assert.False(AdminEndpoints.TryReadAreaSettings(null, false, true, null, false, null, now, out _, out why));
        Assert.Contains("scheduledUpdatesEnabled", why, StringComparison.Ordinal);
        Assert.False(AdminEndpoints.TryReadAreaSettings(null, false, null, JsonNode.Parse("""{"minScale": 100, "maxScale": 5000}"""), true, null, now, out _, out why));
        Assert.Contains("mapAreaTileScale", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schedule_is_due_once_a_run_has_passed_since_the_packages_were_written_and_not_while_it_is_off_or_just_failed()
    {
        // 2026-11-15 is a Sunday; the Monday run is 2026-11-16 02:30 UTC.
        MapArea area = Area() with { PackageRefreshSchedule = "0 30 2 ? * MON", ScheduledUpdates = true, Refreshed = DateTimeOffset.Parse("2026-11-15T10:00:00Z") };

        Assert.False(MapAreaRefreshScheduler.IsDue(area, DateTimeOffset.Parse("2026-11-16T02:29:59Z")));
        Assert.True(MapAreaRefreshScheduler.IsDue(area, DateTimeOffset.Parse("2026-11-16T02:30:00Z")));

        // Refreshed after the run: the next one is a week on, so a refresh by hand postpones it.
        Assert.False(MapAreaRefreshScheduler.IsDue(area with { Refreshed = DateTimeOffset.Parse("2026-11-16T03:00:00Z") }, DateTimeOffset.Parse("2026-11-17T00:00:00Z")));
        Assert.True(MapAreaRefreshScheduler.IsDue(area with { Refreshed = DateTimeOffset.Parse("2026-11-16T03:00:00Z") }, DateTimeOffset.Parse("2026-11-23T02:30:00Z")));

        // Off, no schedule, or a schedule this server cannot read: never.
        Assert.False(MapAreaRefreshScheduler.IsDue(area with { ScheduledUpdates = false }, DateTimeOffset.Parse("2026-12-01T00:00:00Z")));
        Assert.False(MapAreaRefreshScheduler.IsDue(area with { PackageRefreshSchedule = null }, DateTimeOffset.Parse("2026-12-01T00:00:00Z")));
        Assert.False(MapAreaRefreshScheduler.IsDue(area with { PackageRefreshSchedule = "garbage" }, DateTimeOffset.Parse("2026-12-01T00:00:00Z")));

        // A try that failed a few minutes ago is not repeated at once, and is after ten.
        DateTimeOffset due = DateTimeOffset.Parse("2026-11-16T02:30:00Z");
        Assert.False(MapAreaRefreshScheduler.IsDue(area, due.AddMinutes(5), due));
        Assert.True(MapAreaRefreshScheduler.IsDue(area, due.AddMinutes(11), due));
    }

    [Fact]
    public void The_tile_scale_bounds_the_levels_from_the_one_at_the_coarse_scale_to_the_one_at_the_fine_scale()
    {
        Graticula.Tiles.VectorTileScheme scheme = Graticula.Tiles.VectorTileScheme.WebMercator;

        Assert.Null(MapAreaPackaging.LevelsForScales(scheme, null, null));

        // This grid has 512-pixel tiles, so level 0 is 1:295828763: 1:750000 lies between levels 8 (1155581) and 9 (577790), and the level at
        // or coarser is 8; 1:20000 is first reached at level 14 (18056).
        IReadOnlyList<int> levels = MapAreaPackaging.LevelsForScales(scheme, 750000, 20000)!;
        Assert.Equal(8, levels[0]);
        Assert.Equal(14, levels[^1]);
        Assert.Equal(levels.Count, levels[^1] - levels[0] + 1);

        // A fine scale alone starts at the top of the grid; a coarse one alone runs to its last level.
        Assert.Equal(0, MapAreaPackaging.LevelsForScales(scheme, null, 20000)![0]);
        Assert.Equal(scheme.MaxLevel, MapAreaPackaging.LevelsForScales(scheme, 750000, null)![^1]);

        // Never an empty set: a range that runs backwards is the one level.
        Assert.Single(MapAreaPackaging.LevelsForScales(scheme, 1000, 5000)!);
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

    // ------------------------------------------------------------------------------------------------ D-407

    [Fact]
    public void The_orphan_sweep_removes_a_day_old_file_no_row_names_and_keeps_the_named_the_young_and_strangers()
    {
        MapAreaPackageFiles files = new(_directory);
        Directory.CreateDirectory(files.Directory);
        string named = "0123456789abcdef0123456789abcdef.geodatabase", orphan = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.vtpk";
        string young = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.geodatabase", stranger = "notes.txt";

        foreach (string f in new[] { named, orphan, young, stranger })
        {
            File.WriteAllText(Path.Combine(files.Directory, f), "x");
        }

        foreach (string f in new[] { named, orphan, stranger })
        {
            File.SetLastWriteTimeUtc(Path.Combine(files.Directory, f), DateTime.UtcNow.AddDays(-2));
        }

        int removed = files.SweepOrphans(new HashSet<string> { $"map-area-package-files/{named}" }, TimeSpan.FromDays(1));

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(files.Directory, named)), "A file a row names was removed.");
        Assert.False(File.Exists(Path.Combine(files.Directory, orphan)), "A day-old file nothing names was kept.");
        Assert.True(File.Exists(Path.Combine(files.Directory, young)), "A file written moments ago, its row not yet recorded, was removed.");
        Assert.True(File.Exists(Path.Combine(files.Directory, stranger)), "A file this class never writes was removed.");
    }

    [Fact]
    public async Task Turning_sync_off_puts_the_packages_replicas_and_files_away_and_marks_their_areas()
    {
        MapAreaPackageFiles files = new(_directory);
        (string path, string recorded) = files.Next();
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        MapArea area = Area();
        MapAreaPackage held = Package(area, recorded);
        MemoryAreas areas = new() { Areas = { area }, Packages = { held } };
        MemoryReplicas replicas = new(held.ReplicaId!.Value);
        HttpContext context = Context(Member(Owner, "owner"), areas, Map(SharingScope.Private), replicas);

        IReadOnlyList<MapAreaPackage> before = await MapAreaPackaging.PackagesOfServiceAsync(context.RequestServices, held.ServiceId, CancellationToken.None);
        await MapAreaPackaging.PutAwayForSyncOffAsync(context.RequestServices, before, held.ServiceId, CancellationToken.None);

        Assert.Equal(held.Id, Assert.Single(before).Id);
        Assert.Empty(replicas.Registered);
        Assert.False(File.Exists(path));
        Assert.Equal((null, held.ServiceId), Assert.Single(areas.Marked));

        // A service with no package does nothing at all.
        areas.Marked.Clear();
        await MapAreaPackaging.PutAwayForSyncOffAsync(context.RequestServices, [], Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(areas.Marked);
    }

    [Fact]
    public async Task A_map_or_service_restored_from_the_recycle_bin_marks_its_areas_and_any_other_kind_marks_none()
    {
        MemoryAreas areas = new();
        HttpContext context = Context(Member(Owner, "owner"), areas, Map(SharingScope.Private));
        Guid service = Guid.NewGuid();

        await MapAreaPackaging.MarkRestoredAsync(context.RequestServices, "web map", Guid.Parse(MapId), CancellationToken.None);
        await MapAreaPackaging.MarkRestoredAsync(context.RequestServices, "service", service, CancellationToken.None);
        await MapAreaPackaging.MarkRestoredAsync(context.RequestServices, "document", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([(MapId, (Guid?)null), (null, (Guid?)service)], areas.Marked);
    }

    // ------------------------------------------------------------------------------------------------ D-427

    [Fact]
    public async Task A_package_whose_job_has_not_finished_says_so_and_its_file_is_not_there_yet()
    {
        MapArea area = Area();
        Guid job = Guid.NewGuid();
        MapAreaPackage waiting = Package(area, string.Empty) with
        {
            Format = MapAreas.VtpkFormat, ReplicaId = null, Status = MapAreas.PackageProcessing, ExportJobId = job, Size = 0, Sha256 = string.Empty,
        };
        MemoryAreas areas = new() { Areas = { area }, Packages = { waiting } };
        HttpContext context = Context(Member(Owner, "owner"), areas, Map(SharingScope.Private));

        JsonElement item = Json(PortalEndpoints.MapAreaPackageItem(context, Map(SharingScope.Private), area, waiting, "North - Streets"));
        Assert.Equal(-1, item.GetProperty("size").GetInt64());

        IResult data = PortalEndpoints.MapAreaPackageFile(context, waiting);
        DefaultHttpContext response = new() { RequestServices = context.RequestServices };
        response.Response.Body = new MemoryStream();
        await data.ExecuteAsync(response);
        Assert.Equal(404, response.Response.StatusCode);
        Assert.Contains("still being written", Encoding.UTF8.GetString(Body(response)), StringComparison.Ordinal);

        // The status is the portal's: processing, with the job; then failed with the reason; a complete package completed.
        JsonElement processing = await StatusAsync(context, waiting.Id);
        Assert.Equal("processing", processing.GetProperty("status").GetString());
        Assert.Equal(job.ToString("N"), processing.GetProperty("jobId").GetString());

        areas.Packages[0] = waiting with { Status = MapAreas.PackageFailed, ExportJobId = null, Error = "no tiles" };
        JsonElement failed = await StatusAsync(context, waiting.Id);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("no tiles", failed.GetProperty("statusMessage").GetString());

        areas.Packages[0] = Package(area, "map-area-package-files/0123456789abcdef0123456789abcdef.geodatabase");
        Assert.Equal("completed", (await StatusAsync(context, areas.Packages[0].Id)).GetProperty("status").GetString());
        Assert.Equal("null", (await StatusAsync(context, area.Id)).GetProperty("status").GetString());
    }

    private static async Task<JsonElement> StatusAsync(HttpContext context, Guid id)
    {
        IResult? result = await PortalEndpoints.MapAreaStatusAsync(context, id.ToString("N"), CancellationToken.None);
        Assert.NotNull(result);
        DefaultHttpContext response = new() { RequestServices = context.RequestServices };
        response.Response.Body = new MemoryStream();
        await result!.ExecuteAsync(response);
        return JsonDocument.Parse(Body(response)).RootElement.Clone();
    }

    [Fact]
    public void An_areas_status_is_one_of_the_portals_three_words()
    {
        Assert.Equal("complete", PortalEndpoints.PortalStatusOf(MapAreas.Complete));
        Assert.Equal("processing", PortalEndpoints.PortalStatusOf(MapAreas.Processing));
        Assert.Equal("failed", PortalEndpoints.PortalStatusOf(MapAreas.Failed));
        Assert.Equal("failed", PortalEndpoints.PortalStatusOf(MapAreas.NeedsRefresh));
    }

    // ------------------------------------------------------------------------------------------------ D-404: the packaging service

    private static Func<string, string?> Asked(params (string Name, string Value)[] given) =>
        name => given.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static WebMap MapWith(string document) => new(MapId, "Hydrants map", null, Owner, "owner", SharingScope.Private, document, At, At);

    [Fact]
    public void An_area_is_given_by_extent_geometry_or_bookmark_and_areaType_is_read_from_what_is_sent_when_it_is_not_named()
    {
        WebMap map = MapWith("""{"bookmarks":[{"name":"North yard","extent":{"xmin":1,"ymin":2,"xmax":3,"ymax":4,"spatialReference":{"wkid":3857}}}]}""");
        const string Box = """{"xmin":10,"ymin":20,"xmax":30,"ymax":40,"spatialReference":{"wkid":4326}}""";

        (JsonObject? byExtent, string? why) = OfflinePackagingEndpoints.AreaEnvelope(Asked(("extent", Box)), map);
        Assert.Null(why);
        Assert.Equal(10, byExtent!["xmin"]!.GetValue<double>());

        (JsonObject? byTyped, _) = OfflinePackagingEndpoints.AreaEnvelope(Asked(("areaType", "ENVELOPE"), ("extent", Box)), map);
        Assert.Equal(30, byTyped!["xmax"]!.GetValue<double>());

        (JsonObject? byRings, _) = OfflinePackagingEndpoints.AreaEnvelope(
            Asked(("area", """{"rings":[[[5,6],[9,6],[9,12],[5,6]]],"spatialReference":{"wkid":3857}}""")), map);
        Assert.Equal((5d, 6d, 9d, 12d), (byRings!["xmin"]!.GetValue<double>(), byRings["ymin"]!.GetValue<double>(), byRings["xmax"]!.GetValue<double>(), byRings["ymax"]!.GetValue<double>()));
        Assert.Equal(3857, byRings["spatialReference"]!["wkid"]!.GetValue<int>());

        (JsonObject? byBookmark, _) = OfflinePackagingEndpoints.AreaEnvelope(Asked(("bookmark", "north YARD")), map);
        Assert.Equal(4, byBookmark!["ymax"]!.GetValue<double>());
        Assert.True(AdminEndpoints.TryReadAreaExtent(byBookmark, out Envelope e, out int wkid, out _));
        Assert.Equal((3857, 3d), (wkid, e.MaxX));
    }

    [Fact]
    public void The_packages_a_refresh_names_are_read_from_an_id_a_list_or_what_setup_answered()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        Assert.Equal([a], OfflinePackagingEndpoints.PackageIdsOf(a.ToString("N")));
        Assert.Equal([a, b], OfflinePackagingEndpoints.PackageIdsOf($$"""["{{a:N}}","{{b:D}}"]"""));
        Assert.Equal([a, b], OfflinePackagingEndpoints.PackageIdsOf($$"""{"packages":[{"id":"{{a:N}}"},{"itemId":"{{b:N}}"}]}"""));
        Assert.Empty(OfflinePackagingEndpoints.PackageIdsOf(null));
        Assert.Empty(OfflinePackagingEndpoints.PackageIdsOf("""["nope"]"""));
        Assert.Empty(OfflinePackagingEndpoints.PackageIdsOf("{"));
    }

    [Fact]
    public void The_tasks_list_the_parameters_an_enterprise_portal_lists()
    {
        // Read 2026-11-15 from an Enterprise 12.1 portal's task descriptions.
        static string[] Names(string task, bool output) => [.. OfflinePackagingEndpoints.ParameterNames(task, output)];

        Assert.Equal(["mapItemId", "bookmark", "extent", "areaType", "area", "outputName"], Names("CreateMapArea", false));
        Assert.Equal(["mapAreaItemId"], Names("CreateMapArea", true));
        Assert.Equal(["mapAreaItemId", "mapLayersToIgnore", "tileServices", "featureServices"], Names("SetupMapArea", false));
        Assert.Equal(["packages"], Names("SetupMapArea", true));
        Assert.Equal(["packages"], Names("RefreshMapAreaPackage", false));
        Assert.Equal(["result"], Names("RefreshMapAreaPackage", true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("areaType=ENVELOPE")]
    [InlineData("areaType=CIRCLE&extent=x")]
    [InlineData("bookmark=No such")]
    [InlineData("extent=not json")]
    [InlineData("area=%5B1%5D")]
    public void An_area_that_is_not_given_properly_is_refused_with_a_sentence(string query)
    {
        (string Name, string Value)[] given =
        [.. query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
            .Select(kv => (kv[0], Uri.UnescapeDataString(kv[1])))];

        (JsonObject? envelope, string? why) = OfflinePackagingEndpoints.AreaEnvelope(Asked(given), MapWith("{}"));

        Assert.Null(envelope);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void Migration_139_is_in_the_set_and_the_map_area_items_are_in_it()
    {
        Assert.True(PlatformMigrations.ComponentSchemaVersion >= new SchemaVersion(139));
        Assert.Contains(PlatformMigrations.All.All, m => m.Version == new SchemaVersion(139));
    }
}
