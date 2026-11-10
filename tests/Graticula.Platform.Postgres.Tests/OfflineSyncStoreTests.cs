using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// ADR-288, migration 137: a service's sync switch and its replica registry — a replica is kept with its layers'
/// generations, advanced by a sync, and unregistered with every other when its service's sync is turned off.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OfflineSyncStoreTests : PostgresFixture
{
    [Fact]
    public async Task A_replica_is_registered_advanced_and_unregistered_with_its_services_sync()
    {
        await MigrateAsync();
        Guid owner = await OwnerAsync();
        Guid service = await SeedServiceAsync("Hydrants_" + Guid.NewGuid().ToString("N")[..8], "hosted", owner);
        PostgresOfflineSyncStore store = new(DataSource);

        Assert.Equal(0, await store.SetEnabledAsync(service, true, CancellationToken.None));
        Assert.True(await EnabledAsync(service));

        DateTimeOffset created = DateTimeOffset.FromUnixTimeMilliseconds(1794286130989);
        ReplicaRecord replica = new(
            Guid.NewGuid(), "graticula_test_cap1", service, owner, "worker", "perLayer",
            [new ReplicaLayer(0, 1794286130989, "useFilter", "status = 1", true), new ReplicaLayer(1, 1794286130989, "none", "", false)],
            true, "bidirectional", """{"xmin":1,"ymin":2,"xmax":3,"ymax":4}""", 3857, created, created);

        await store.CreateAsync(replica, CancellationToken.None);

        ReplicaRecord found = (await store.FindAsync(replica.Id, CancellationToken.None))!;
        Assert.Equal("graticula_test_cap1", found.Name);
        Assert.Equal(2, found.Layers.Count);
        Assert.Equal("status = 1", found.Layers[0].Where);
        Assert.False(found.Layers[1].UseGeometry);
        Assert.Equal(3857, found.Wkid);
        Assert.Equal(created, found.Created);

        DateTimeOffset synced = created.AddMinutes(5);
        Assert.True(await store.AdvanceAsync(
            replica.Id, [found.Layers[0] with { ServerGeneration = 1794286430989 }, found.Layers[1]], synced, CancellationToken.None));

        ReplicaRecord advanced = Assert.Single(await store.ListAsync(service, CancellationToken.None));
        Assert.Equal(1794286430989, advanced.Layers[0].ServerGeneration);
        Assert.Equal(1794286130989, advanced.Layers[1].ServerGeneration);
        Assert.Equal(synced, advanced.LastSynced);

        // Turning sync off unregisters every replica of the service with it.
        Assert.Equal(1, await store.SetEnabledAsync(service, false, CancellationToken.None));
        Assert.False(await EnabledAsync(service));
        Assert.Null(await store.FindAsync(replica.Id, CancellationToken.None));
        Assert.False(await store.DeleteAsync(replica.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_name_ArcGIS_would_refuse_is_refused_by_the_table_too()
    {
        await MigrateAsync();
        Guid owner = await OwnerAsync();
        Guid service = await SeedServiceAsync("Valves_" + Guid.NewGuid().ToString("N")[..8], "hosted", owner);
        PostgresOfflineSyncStore store = new(DataSource);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<PostgresException>(() => store.CreateAsync(new ReplicaRecord(
            Guid.NewGuid(), "not a word", service, owner, "worker", "perLayer", [], false, "none", null, null, now, now),
            CancellationToken.None));
    }

    private async Task<Guid> OwnerAsync()
    {
        Guid owner = Guid.NewGuid();

        await using NpgsqlCommand command = DataSource.CreateCommand(
            "insert into principal (id, kind, name, user_type) values (@id, 'user', @name, 'creator')");
        command.Parameters.AddWithValue("id", owner);
        command.Parameters.AddWithValue("name", "replica-owner-" + owner.ToString("N")[..8]);
        await command.ExecuteNonQueryAsync();

        return owner;
    }

    private async Task<bool> EnabledAsync(Guid service)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand("select sync_enabled from service where id = @id");
        command.Parameters.AddWithValue("id", service);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
