using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-288 §11a, migration 139: a preplanned map area and its packages are items of their own, related to their map and
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
}
