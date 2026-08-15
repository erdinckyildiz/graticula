using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GisServer.Geometries;
using GisServer.Platform.Admin;
using GisServer.Platform.Catalog;
using GisServer.Platform.Identity;
using GisServer.Platform.Postgres;
using GisServer.Platform.Secrets;
using Npgsql;
using Xunit;

namespace GisServer.Platform.Postgres.Tests;

/// <summary>
/// Publishing, and mostly the index allocation underneath it.
/// </summary>
/// <remarks>
/// <b>Written after a defect that shipped and hid.</b> Publishing a layer into a
/// service that did not yet exist created the service, created no layer, and
/// answered 201. It survived because every test written after it published into
/// a service that already existed — the one path nobody exercised was the first
/// one a real user takes.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostgresAdminCatalogTests : PostgresFixture
{
    private async Task<(PostgresAdminCatalog Admin, Guid Source, Guid Owner)> ReadyAsync()
    {
        await MigrateAsync();

        SecretProtector secrets = new(1, new byte[32]);
        PostgresAdminCatalog admin = new(DataSource, secrets);

        Guid source = await admin.RegisterDataSourceAsync(
            "test-source", "postgis", "Host=nowhere;Database=none", CancellationToken.None);

        // A principal to own what gets published. The layer table has a foreign
        // key to it, so publishing without one fails for the wrong reason.
        Guid owner = Guid.NewGuid();

        await using NpgsqlCommand principal = DataSource.CreateCommand(
            "insert into principal (id, name, kind, user_type) values (@id, 'publisher', 'user', "
            + "'unrestricted')");

        principal.Parameters.AddWithValue("id", owner);
        await principal.ExecuteNonQueryAsync(CancellationToken.None);

        return (admin, source, owner);
    }

    private static LayerPublication Publication(
        Guid source, string name, string? service = null, int? cacheSeconds = null) =>
        new(
            name,
            source,
            "public",
            name,
            "geom",
            "objectid",
            "objectid",
            3857,
            GeometryKind.Point,
            SharingScope.Private,
            service,
            null,
            cacheSeconds);

    /// <summary>
    /// A layer published into a service that does not exist yet actually lands.
    /// </summary>
    /// <remarks>
    /// <b>This is the regression.</b> The allocation statement created the
    /// service in one data-modifying CTE and tried to bump its counter in
    /// another — and PostgreSQL gives every CTE the same snapshot, so the update
    /// matched no row, the layer insert selected from an empty result, and
    /// nothing was inserted. The service existed, the response said 201, and the
    /// service document listed no layers.
    /// </remarks>
    [Fact]
    public async Task Publishing_into_a_new_service_creates_the_layer_as_well()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        PublishedLayerAddress address = await admin.PublishLayerAsync(
            Publication(source, "first"), owner, CancellationToken.None);

        Assert.Equal("first", address.ServiceName);
        Assert.Equal(0, address.LayerIndex);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService? service = await catalog
            .FindServiceAsync(null, "first", CancellationToken.None);

        Assert.NotNull(service);

        PublishedLayer only = Assert.Single(service!.Layers);

        Assert.Equal("first", only.Definition.Name);
        Assert.Equal(0, only.LayerIndex);
    }

    [Fact]
    public async Task The_new_service_counter_is_left_ready_for_the_next_layer()
    {
        // A service created with its first layer at 0 must have its counter at 1,
        // or the second layer collides with the first.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "one", service: "shared"), owner, CancellationToken.None);

        PublishedLayerAddress second = await admin.PublishLayerAsync(
            Publication(source, "two", service: "shared"), owner, CancellationToken.None);

        Assert.Equal(1, second.LayerIndex);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "shared", CancellationToken.None))!;

        Assert.Equal(2, service.Layers.Count);
        Assert.Equal([0, 1], service.Layers.Select(l => l.LayerIndex).Order());
    }

    [Fact]
    public async Task An_index_is_never_reused_after_a_layer_is_removed()
    {
        // A saved web map stores this integer. Handing it out again after a
        // removal would silently repoint somebody's map at different data.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "gone", service: "svc"), owner, CancellationToken.None);

        await using NpgsqlCommand remove = DataSource.CreateCommand(
            "delete from layer where name = 'gone'");

        await remove.ExecuteNonQueryAsync(CancellationToken.None);

        PublishedLayerAddress next = await admin.PublishLayerAsync(
            Publication(source, "next", service: "svc"), owner, CancellationToken.None);

        Assert.Equal(1, next.LayerIndex);
    }

    [Fact]
    public async Task A_publish_time_cache_lifetime_is_stored_and_read_back()
    {
        // D-25: volatility is known at publish time by whoever publishes, and
        // A-028 says nobody else knows it.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "fast", cacheSeconds: 15), owner, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "fast", CancellationToken.None))!;

        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(service.Layers).CacheLifetime);
    }

    [Fact]
    public async Task A_layer_with_no_cache_lifetime_reads_back_as_null_rather_than_zero()
    {
        // Null means "nobody has said" and takes the server default; zero means
        // "never cache", which is a different answer somebody may want.
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "unsaid"), owner, CancellationToken.None);

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "unsaid", CancellationToken.None))!;

        Assert.Null(Assert.Single(service.Layers).CacheLifetime);
    }

    [Fact]
    public async Task Setting_the_cache_lifetime_afterwards_takes_effect()
    {
        (PostgresAdminCatalog admin, Guid source, Guid owner) = await ReadyAsync();

        await admin.PublishLayerAsync(
            Publication(source, "later"), owner, CancellationToken.None);

        Assert.True(await admin.SetCacheLifetimeAsync("later", 0, CancellationToken.None));

        PostgresLayerCatalog catalog = new(DataSource, new SecretProtector(1, new byte[32]));

        PublishedService service =
            (await catalog.FindServiceAsync(null, "later", CancellationToken.None))!;

        // Zero, not null: "never cache" survived the round trip as itself.
        Assert.Equal(TimeSpan.Zero, Assert.Single(service.Layers).CacheLifetime);
    }

    [Fact]
    public async Task Setting_it_on_a_layer_that_does_not_exist_says_so()
    {
        (PostgresAdminCatalog admin, _, _) = await ReadyAsync();

        Assert.False(await admin.SetCacheLifetimeAsync("nosuch", 60, CancellationToken.None));
    }
}
