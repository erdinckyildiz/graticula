using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-288 §11a, migrations 139 and 140: a preplanned map area and its packages are items of their own, related to their map and
/// area by <c>Map2Area</c> and <c>Area2Package</c>, and go when the map does.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MapAreaStoreTests : PostgresFixture
{
    private const string Properties = """{"url":"https://h/rest/services/hosted/Hydrants/FeatureServer","layers":[0],"replicaID":"X"}""";

    private sealed record Ready(PostgresMapAreaStore Areas, PostgresWebMapStore Maps, WebMap Map, Guid Owner, Guid Service);

    private async Task<Ready> ReadyAsync(SharingScope sharing = SharingScope.Organization)
    {
        await MigrateAsync();

        Guid owner = Guid.NewGuid();
        await using (NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')"))
        {
            command.Parameters.AddWithValue("id", owner);
            command.Parameters.AddWithValue("name", "area-owner-" + owner.ToString("N")[..8]);
            await command.ExecuteNonQueryAsync();
        }

        PostgresWebMapStore maps = new(DataSource);
        WebMap map = await maps.CreateAsync("Hydrants map", null, owner, sharing, """{"operationalLayers":[]}""", CancellationToken.None);
        Guid service = await SeedServiceAsync("Hydrants_" + Guid.NewGuid().ToString("N")[..8], "hosted", owner);

        return new Ready(new PostgresMapAreaStore(DataSource), maps, map, owner, service);
    }

    private static MapArea Area(Ready r, string title = "North") => new(
        Guid.NewGuid(), r.Map.Id, title, 10, 20, 30, 40, 4326, MapAreas.Complete,
        DateTimeOffset.FromUnixTimeMilliseconds(1794286130989), DateTimeOffset.FromUnixTimeMilliseconds(1794286130989),
        r.Owner, "creator");

    private static MapAreaPackage Package(Ready r, MapArea area, string file = "map-area-package-files/0123456789abcdef0123456789abcdef.geodatabase") => new(
        Guid.NewGuid(), area.Id, area.MapId, r.Service, Guid.NewGuid(), "SDS_FS_1794286130989.geodatabase", file,
        new string('a', 64), 4096, Properties, area.Created, area.Created);

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task An_area_and_its_package_are_items_with_the_maps_owner_and_sharing_and_the_two_relationships()
    {
        Ready r = await ReadyAsync(SharingScope.Public);
        MapArea area = Area(r);
        MapAreaPackage package = Package(r, area);

        Assert.True(await r.Areas.CreateAsync(area, CancellationToken.None));
        Assert.True(await r.Areas.AddPackageAsync(package, "North - Hydrants", CancellationToken.None));

        Assert.Equal(MapAreas.AreaKind, await r.Areas.ItemKindAsync(area.Id, CancellationToken.None));
        Assert.Equal(MapAreas.PackageKind, await r.Areas.ItemKindAsync(package.Id, CancellationToken.None));
        Assert.Null(await r.Areas.ItemKindAsync(Guid.Parse(r.Map.Id), CancellationToken.None));

        // Both items take the map's owner and sharing, and are titled.
        await using (NpgsqlCommand items = DataSource.CreateCommand(
            "select kind, title, owner_principal_id, sharing from item where id = any(@ids) order by kind"))
        {
            items.Parameters.AddWithValue("ids", new[] { area.Id, package.Id });
            await using NpgsqlDataReader reader = await items.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("map area", "North", r.Owner, "public"), (reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("map area package", "North - Hydrants", r.Owner, "public"), (reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3)));
        }

        PostgresItemAnnotationStore relationships = new(DataSource);
        Assert.Equal([area.Id], await relationships.RelatedAsync(Guid.Parse(r.Map.Id), MapAreas.Map2Area, forward: true, CancellationToken.None));
        Assert.Equal([Guid.Parse(r.Map.Id)], await relationships.RelatedAsync(area.Id, MapAreas.Map2Area, forward: false, CancellationToken.None));
        Assert.Equal([package.Id], await relationships.RelatedAsync(area.Id, MapAreas.Area2Package, forward: true, CancellationToken.None));
        Assert.Equal([area.Id], await relationships.RelatedAsync(package.Id, MapAreas.Area2Package, forward: false, CancellationToken.None));

        MapArea read = (await r.Areas.FindAsync(area.Id, CancellationToken.None))!;
        Assert.Equal(area with { }, read);

        // jsonb keeps the document, not its spacing.
        MapAreaPackage kept = Assert.Single(await r.Areas.PackagesAsync([area.Id], CancellationToken.None));
        Assert.Equal(package with { Properties = kept.Properties }, kept);
        Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(Properties)!.ToJsonString(), System.Text.Json.Nodes.JsonNode.Parse(kept.Properties)!.ToJsonString());
        Assert.Equal(kept, await r.Areas.FindPackageAsync(package.Id, CancellationToken.None));
        Assert.Equal(area, Assert.Single(await r.Areas.ListAsync([r.Map.Id], CancellationToken.None)));
    }

    [Fact]
    public async Task Deleting_a_map_deletes_its_areas_packages_items_and_relationships_and_names_the_packages_first()
    {
        Ready r = await ReadyAsync();
        MapArea area = Area(r);
        MapAreaPackage package = Package(r, area);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        await r.Areas.AddPackageAsync(package, "North - Hydrants", CancellationToken.None);

        // What the caller reads before it deletes, to put away the replicas and files after.
        MapAreaPackage named = Assert.Single(await r.Areas.PackagesOfMapAsync(r.Map.Id, CancellationToken.None));
        Assert.Equal(package.ReplicaId, named.ReplicaId);
        Assert.Equal(package.FilePath, named.FilePath);

        Assert.True(await r.Maps.DeleteAsync(r.Map.Id, CancellationToken.None));

        Assert.Null(await r.Areas.FindAsync(area.Id, CancellationToken.None));
        Assert.Null(await r.Areas.FindPackageAsync(package.Id, CancellationToken.None));
        Assert.Equal(0, await CountAsync("select count(*) from item where id = any(@ids)", ("ids", new[] { area.Id, package.Id })));
        Assert.Equal(0, await CountAsync(
            "select count(*) from item_relationship where origin_item_id = any(@ids) or destination_item_id = any(@ids)",
            ("ids", new[] { area.Id, package.Id })));
    }

    [Fact]
    public async Task Deleting_an_area_takes_its_packages_but_not_its_map()
    {
        Ready r = await ReadyAsync();
        MapArea area = Area(r);
        MapAreaPackage package = Package(r, area);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        await r.Areas.AddPackageAsync(package, "North - Hydrants", CancellationToken.None);

        Assert.True(await r.Areas.DeleteAsync(area.Id, CancellationToken.None));
        Assert.False(await r.Areas.DeleteAsync(area.Id, CancellationToken.None));

        Assert.Null(await r.Areas.FindPackageAsync(package.Id, CancellationToken.None));
        Assert.Equal(0, await CountAsync("select count(*) from item where id = any(@ids)", ("ids", new[] { area.Id, package.Id })));
        Assert.NotNull(await r.Maps.FindAsync(r.Map.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_refreshed_package_keeps_its_id_and_item_and_takes_the_new_replica_and_file()
    {
        Ready r = await ReadyAsync();
        MapArea area = Area(r);
        MapAreaPackage package = Package(r, area);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        await r.Areas.AddPackageAsync(package, "North - Hydrants", CancellationToken.None);

        DateTimeOffset later = area.Created.AddHours(1);
        MapAreaPackage replaced = package with
        {
            ReplicaId = Guid.NewGuid(),
            FileName = "SDS_FS_1794289730989.geodatabase",
            FilePath = "map-area-package-files/fedcba9876543210fedcba9876543210.geodatabase",
            Sha256 = new string('b', 64),
            Size = 9000,
            Refreshed = later,
        };

        Assert.True(await r.Areas.ReplacePackageAsync(replaced, later, CancellationToken.None));
        Assert.True(await r.Areas.SetStatusAsync(area.Id, MapAreas.Complete, later, CancellationToken.None));

        MapAreaPackage read = (await r.Areas.FindPackageAsync(package.Id, CancellationToken.None))!;
        Assert.Equal(replaced.ReplicaId, read.ReplicaId);
        Assert.Equal(replaced.FilePath, read.FilePath);
        Assert.Equal(9000, read.Size);
        Assert.Equal(later, read.Refreshed);
        Assert.Equal(package.Created, read.Created);
        Assert.Equal(later, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Refreshed);
        Assert.Equal(1, await CountAsync("select count(*) from item where id = @id", ("id", package.Id)));
    }

    [Fact]
    public async Task A_basemaps_tile_package_has_no_replica_is_kept_beside_the_geodatabase_and_is_refreshed_in_place()
    {
        // Migration 140, ADR-288 section 11a: a package of either kind, one per (area, service).
        Ready r = await ReadyAsync();
        Guid tiles = await SeedServiceAsync("Streets_" + Guid.NewGuid().ToString("N")[..8], "hosted", r.Owner);
        MapArea area = Area(r);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        MapAreaPackage geodatabase = Package(r, area);
        MapAreaPackage vtpk = new(
            Guid.NewGuid(), area.Id, area.MapId, tiles, null, "streets.vtpk", "map-area-package-files/0123456789abcdef0123456789abcdef.vtpk",
            new string('c', 64), 8192, """{"url":"https://h/rest/services/hosted/Streets/VectorTileServer","format":"vtpk"}""",
            area.Created, area.Created, MapAreas.VtpkFormat);

        Assert.True(await r.Areas.AddPackageAsync(geodatabase, "North - Hydrants", CancellationToken.None));
        Assert.True(await r.Areas.AddPackageAsync(vtpk, "North - Streets", CancellationToken.None));

        IReadOnlyList<MapAreaPackage> held = await r.Areas.PackagesAsync([area.Id], CancellationToken.None);
        Assert.Equal(2, held.Count);

        MapAreaPackage read = held.Single(p => p.Id == vtpk.Id);
        Assert.Null(read.ReplicaId);
        Assert.Equal(MapAreas.VtpkFormat, read.Format);
        Assert.Equal(MapAreas.SqliteFormat, held.Single(p => p.Id == geodatabase.Id).Format);
        Assert.Equal(MapAreas.PackageKind, await r.Areas.ItemKindAsync(vtpk.Id, CancellationToken.None));

        // Refreshed in place: the same id and item, a new file.
        MapAreaPackage again = vtpk with
        {
            FilePath = "map-area-package-files/fedcba9876543210fedcba9876543210.vtpk",
            Size = 9999,
            Refreshed = area.Created.AddHours(1),
        };
        Assert.True(await r.Areas.ReplacePackageAsync(again, again.Refreshed, CancellationToken.None));
        MapAreaPackage after = (await r.Areas.FindPackageAsync(vtpk.Id, CancellationToken.None))!;
        Assert.Null(after.ReplicaId);
        Assert.Equal(again.FilePath, after.FilePath);
        Assert.Equal(1, await CountAsync("select count(*) from item where id = @id", ("id", vtpk.Id)));

        // Deleting the area takes the tile package and its item as it takes the geodatabase.
        Assert.True(await r.Areas.DeleteAsync(area.Id, CancellationToken.None));
        Assert.Null(await r.Areas.FindPackageAsync(vtpk.Id, CancellationToken.None));
        Assert.Equal(0, await CountAsync("select count(*) from item where id = any(@ids)", ("ids", new[] { area.Id, vtpk.Id, geodatabase.Id })));
    }

    [Fact]
    public async Task A_row_has_a_replica_exactly_when_it_is_a_geodatabase()
    {
        Ready r = await ReadyAsync();
        MapArea area = Area(r);
        await r.Areas.CreateAsync(area, CancellationToken.None);

        // A geodatabase without a replica, and a tile package with one, are both refused by the table.
        await Assert.ThrowsAsync<PostgresException>(() =>
            r.Areas.AddPackageAsync(Package(r, area) with { ReplicaId = null }, "no replica", CancellationToken.None));
        await Assert.ThrowsAsync<PostgresException>(() =>
            r.Areas.AddPackageAsync(Package(r, area) with { Format = MapAreas.VtpkFormat }, "a replica for tiles", CancellationToken.None));
        await Assert.ThrowsAsync<PostgresException>(() =>
            r.Areas.AddPackageAsync(Package(r, area) with { ReplicaId = null, Format = "pmtiles" }, "unknown kind", CancellationToken.None));
    }

    [Fact]
    public async Task An_area_of_a_map_that_does_not_exist_is_not_recorded_and_one_service_has_one_package_per_area()
    {
        Ready r = await ReadyAsync();

        Assert.False(await r.Areas.CreateAsync(Area(r) with { MapId = new string('0', 32) }, CancellationToken.None));
        Assert.False(await r.Areas.CreateAsync(Area(r) with { MapId = "not-an-id" }, CancellationToken.None));

        MapArea area = Area(r);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        Assert.True(await r.Areas.AddPackageAsync(Package(r, area), "North - Hydrants", CancellationToken.None));
        await Assert.ThrowsAsync<PostgresException>(() =>
            r.Areas.AddPackageAsync(Package(r, area), "North - Hydrants again", CancellationToken.None));
        Assert.Single(await r.Areas.PackagesAsync([area.Id], CancellationToken.None));
    }

    [Fact]
    public async Task The_table_refuses_an_inside_out_extent_and_an_item_of_the_new_kinds_without_its_row()
    {
        Ready r = await ReadyAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            r.Areas.CreateAsync(Area(r) with { XMin = 50, XMax = 40 }, CancellationToken.None));

        await using NpgsqlCommand orphan = DataSource.CreateCommand(
            "insert into item (id, kind, title, sharing) values (@id, 'map area', 'No row', 'private')");
        orphan.Parameters.AddWithValue("id", Guid.NewGuid());
        await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task The_migration_is_floored_by_organisation_like_every_organisation_table()
    {
        await MigrateAsync();

        long policies = await CountAsync(
            "select count(*) from pg_policies where schemaname = current_schema() "
            + "and tablename in ('map_area', 'map_area_package') and policyname = 'tenant_floor'");
        Assert.Equal(2, policies);
    }

    [Fact]
    public async Task The_orphan_sweeps_names_are_every_rows_file_live_or_in_the_recycle_bin_and_a_service_names_its_packages()
    {
        Ready r = await ReadyAsync();
        MapArea area = Area(r);
        MapAreaPackage package = Package(r, area, "map-area-package-files/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.geodatabase");
        await r.Areas.CreateAsync(area, CancellationToken.None);
        await r.Areas.AddPackageAsync(package, "North - Hydrants", CancellationToken.None);

        Assert.Contains(package.FilePath, await r.Areas.FilesAsync(CancellationToken.None));
        Assert.Equal(package.Id, Assert.Single(await r.Areas.PackagesOfServiceAsync(r.Service, CancellationToken.None)).Id);
        Assert.Empty(await r.Areas.PackagesOfServiceAsync(Guid.NewGuid(), CancellationToken.None));

        // The map goes to the recycle bin: its package rows are kept whole there, and the file they name is still named.
        PostgresRecycleBin bin = new(DataSource);
        Guid? entry = await bin.RecycleAsync(
            "web_map", r.Map.Id, Guid.Parse(r.Map.Id), "web map", "Hydrants map", r.Owner, r.Owner,
            DateTimeOffset.UtcNow.AddDays(14), [], CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Null(await r.Areas.FindPackageAsync(package.Id, CancellationToken.None));
        Assert.Contains(package.FilePath, await r.Areas.FilesAsync(CancellationToken.None));

        // Brought back, its areas return as rows; marking them says they need a refresh, and does so once.
        Assert.Null(await bin.RestoreRowsAsync(entry!.Value, CancellationToken.None));
        Assert.Equal(MapAreas.Complete, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);
        Assert.Equal(1, await r.Areas.MarkNeedsRefreshAsync(r.Map.Id, null, CancellationToken.None));
        Assert.Equal(0, await r.Areas.MarkNeedsRefreshAsync(r.Map.Id, null, CancellationToken.None));
        Assert.Equal(MapAreas.NeedsRefresh, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);

        // A refresh writes the area again and says it is complete.
        Assert.True(await r.Areas.SetStatusAsync(area.Id, MapAreas.Complete, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(1, await r.Areas.MarkNeedsRefreshAsync(null, r.Service, CancellationToken.None));
        Assert.Equal(0, await r.Areas.MarkNeedsRefreshAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_removed_members_maps_name_their_packages()
    {
        Ready r = await ReadyAsync();
        MapArea area = Area(r);
        MapAreaPackage package = Package(r, area);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        await r.Areas.AddPackageAsync(package, "North - Hydrants", CancellationToken.None);

        string name = await ScalarNameAsync(r.Owner);

        Assert.Equal(package.Id, Assert.Single(await r.Areas.PackagesOfOwnerAsync(name, CancellationToken.None)).Id);
        Assert.Empty(await r.Areas.PackagesOfOwnerAsync("nobody-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));
    }

    private async Task<string> ScalarNameAsync(Guid principal)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand("select name from principal where id = @id");
        command.Parameters.AddWithValue("id", principal);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_basemap_package_is_processing_until_its_job_completes_and_the_area_says_so()
    {
        Ready r = await ReadyAsync();
        Guid tiles = await SeedServiceAsync("Streets_" + Guid.NewGuid().ToString("N")[..8], "hosted", r.Owner);
        MapArea area = Area(r);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        Guid job = Guid.NewGuid();
        MapAreaPackage waiting = new(
            Guid.NewGuid(), area.Id, area.MapId, tiles, null, "streets.vtpk", string.Empty, string.Empty, 0,
            """{"url":"https://h/rest/services/hosted/Streets/VectorTileServer","format":"vtpk"}""",
            area.Created, area.Created, MapAreas.VtpkFormat, MapAreas.PackageProcessing, job);

        Assert.True(await r.Areas.AddPackageAsync(waiting, "North - Streets", CancellationToken.None));
        Assert.Equal(MapAreas.Processing, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);
        Assert.Equal(waiting.Id, (await r.Areas.FindPackageByJobAsync(job, CancellationToken.None))!.Id);

        // Not counted as held until written; once written it counts against the export budget in the export's place.
        PostgresTileExportStore exports = new(DataSource);
        long before = await exports.HeldBytesAsync(CancellationToken.None);

        DateTimeOffset when = area.Created.AddMinutes(5);
        MapAreaPackage previous = (await r.Areas.CompletePackageAsync(
            job, "map-area-package-files/0123456789abcdef0123456789abcdef.vtpk", new string('d', 64), 7777, when, CancellationToken.None))!;

        Assert.Equal(string.Empty, previous.FilePath);
        MapAreaPackage done = (await r.Areas.FindPackageAsync(waiting.Id, CancellationToken.None))!;
        Assert.Equal((MapAreas.PackageComplete, null, 7777L), (done.Status, done.ExportJobId, done.Size));
        Assert.Equal(MapAreas.Complete, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);
        Assert.Equal(before + 7777, await exports.HeldBytesAsync(CancellationToken.None));
        Assert.Null(await r.Areas.FindPackageByJobAsync(job, CancellationToken.None));
        Assert.Null(await r.Areas.CompletePackageAsync(job, "x", "y", 1, when, CancellationToken.None));
    }

    [Fact]
    public async Task A_refresh_keeps_the_old_file_until_the_new_one_is_written_and_a_failed_new_package_fails_its_area()
    {
        Ready r = await ReadyAsync();
        Guid tiles = await SeedServiceAsync("Streets_" + Guid.NewGuid().ToString("N")[..8], "hosted", r.Owner);
        MapArea area = Area(r);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        const string Old = "map-area-package-files/0123456789abcdef0123456789abcdef.vtpk";
        MapAreaPackage held = new(
            Guid.NewGuid(), area.Id, area.MapId, tiles, null, "streets.vtpk", Old, new string('c', 64), 8192, """{"format":"vtpk"}""",
            area.Created, area.Created, MapAreas.VtpkFormat);
        await r.Areas.AddPackageAsync(held, "North - Streets", CancellationToken.None);

        // A refresh: the job is recorded, the old file stays named, the area is processing.
        Guid refreshJob = Guid.NewGuid();
        Assert.True(await r.Areas.MarkPackageProcessingAsync(held.Id, refreshJob, """{"format":"vtpk","tiles":5}""", CancellationToken.None));
        MapAreaPackage during = (await r.Areas.FindPackageAsync(held.Id, CancellationToken.None))!;
        Assert.Equal((MapAreas.PackageProcessing, Old, refreshJob), (during.Status, during.FilePath, during.ExportJobId));
        Assert.Equal(MapAreas.Processing, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);

        // It fails: the old file is still good, so the package is complete again, with the reason noted.
        Assert.True(await r.Areas.FailPackageAsync(refreshJob, "a tile could not be built", CancellationToken.None));
        MapAreaPackage after = (await r.Areas.FindPackageAsync(held.Id, CancellationToken.None))!;
        Assert.Equal((MapAreas.PackageComplete, Old, "a tile could not be built"), (after.Status, after.FilePath, after.Error));
        Assert.Equal(MapAreas.Complete, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);

        // A package that never had a file fails, and its area with it.
        Guid second = await SeedServiceAsync("Labels_" + Guid.NewGuid().ToString("N")[..8], "hosted", r.Owner);
        Guid newJob = Guid.NewGuid();
        await r.Areas.AddPackageAsync(held with
        {
            Id = Guid.NewGuid(), ServiceId = second, FilePath = string.Empty, Sha256 = string.Empty, Size = 0,
            Status = MapAreas.PackageProcessing, ExportJobId = newJob,
        }, "North - Labels", CancellationToken.None);
        Assert.True(await r.Areas.FailPackageAsync(newJob, "no tiles", CancellationToken.None));
        Assert.Equal(MapAreas.Failed, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);
        Assert.False(await r.Areas.FailPackageAsync(newJob, "again", CancellationToken.None));
    }

    [Fact]
    public async Task A_package_waiting_for_a_job_that_is_gone_is_failed_by_the_sweep_and_one_whose_job_runs_is_not()
    {
        Ready r = await ReadyAsync();
        Guid tiles = await SeedServiceAsync("Streets_" + Guid.NewGuid().ToString("N")[..8], "hosted", r.Owner);
        MapArea area = Area(r);
        await r.Areas.CreateAsync(area, CancellationToken.None);
        Guid gone = Guid.NewGuid(), running = Guid.NewGuid();

        await using (NpgsqlCommand job = DataSource.CreateCommand(
            "insert into job (id, kind, status, owner_principal_id, subject, detail, protocol) values (@id, 'tile.export', 'running', @owner, 's', '{}', 1)"))
        {
            job.Parameters.AddWithValue("id", running);
            job.Parameters.AddWithValue("owner", r.Owner);
            await job.ExecuteNonQueryAsync();
        }

        Guid otherService = await SeedServiceAsync("Labels_" + Guid.NewGuid().ToString("N")[..8], "hosted", r.Owner);
        MapAreaPackage Waiting(Guid service, Guid job) => new(
            Guid.NewGuid(), area.Id, area.MapId, service, null, "x.vtpk", string.Empty, string.Empty, 0, """{"format":"vtpk"}""",
            area.Created, area.Created, MapAreas.VtpkFormat, MapAreas.PackageProcessing, job);
        MapAreaPackage orphan = Waiting(tiles, gone), live = Waiting(otherService, running);
        await r.Areas.AddPackageAsync(orphan, "North - Streets", CancellationToken.None);
        await r.Areas.AddPackageAsync(live, "North - Labels", CancellationToken.None);

        Assert.Equal(1, await r.Areas.FailStrandedPackagesAsync(CancellationToken.None));
        Assert.Equal(MapAreas.PackageFailed, (await r.Areas.FindPackageAsync(orphan.Id, CancellationToken.None))!.Status);
        Assert.Equal(MapAreas.PackageProcessing, (await r.Areas.FindPackageAsync(live.Id, CancellationToken.None))!.Status);
        Assert.Equal(MapAreas.Processing, (await r.Areas.FindAsync(area.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task An_areas_schedule_scale_and_address_are_kept_and_the_scheduled_ones_are_listed()
    {
        Ready r = await ReadyAsync();
        MapArea plain = Area(r, "Plain");
        MapArea timed = Area(r, "Timed") with { Origin = "https://maps.example.test:8443" };
        await r.Areas.CreateAsync(plain, CancellationToken.None);
        await r.Areas.CreateAsync(timed, CancellationToken.None);

        Assert.Empty(await r.Areas.ScheduledAsync(CancellationToken.None));
        Assert.Equal("https://maps.example.test:8443", (await r.Areas.FindAsync(timed.Id, CancellationToken.None))!.Origin);

        MapAreaSettings settings = new(
            "41 2 15 ? * TUE", """{"startDate":"2026-11-15T10:00:00.000Z","type":"weekly","nthDay":1,"dayOfWeek":2}""", true, 147914382, 20000);
        Assert.True(await r.Areas.SetSettingsAsync(timed.Id, settings, CancellationToken.None));

        MapArea read = (await r.Areas.FindAsync(timed.Id, CancellationToken.None))!;
        Assert.Equal(("41 2 15 ? * TUE", true, 147914382d, 20000d), (read.PackageRefreshSchedule, read.ScheduledUpdates, read.TileScaleMin, read.TileScaleMax));
        // jsonb keeps the document, not its key order.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(settings.RefreshParams!), System.Text.Json.Nodes.JsonNode.Parse(read.RefreshParams!)));

        (MapArea scheduled, Guid tenant) = Assert.Single(await r.Areas.ScheduledAsync(CancellationToken.None));
        Assert.Equal(timed.Id, scheduled.Id);
        Assert.NotEqual(Guid.Empty, tenant);

        // Switched off, a schedule is kept and not listed; a scale that runs backwards is refused by the table.
        await r.Areas.SetSettingsAsync(timed.Id, settings with { ScheduledUpdates = false }, CancellationToken.None);
        Assert.Empty(await r.Areas.ScheduledAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PostgresException>(() =>
            r.Areas.SetSettingsAsync(plain.Id, settings with { TileScaleMin = 100, TileScaleMax = 5000 }, CancellationToken.None));

        Assert.True(await r.Areas.SetOriginAsync(plain.Id, "https://other.example.test", CancellationToken.None));
        Assert.Equal("https://other.example.test", (await r.Areas.FindAsync(plain.Id, CancellationToken.None))!.Origin);
        Assert.False(await r.Areas.SetSettingsAsync(Guid.NewGuid(), settings, CancellationToken.None));
    }
}
