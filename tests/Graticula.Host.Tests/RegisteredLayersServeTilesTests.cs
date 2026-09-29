using System;
using System.Collections.Generic;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-095: a registered PostGIS layer serves vector tiles, keyed by the database it reads, kept for a
/// shorter default than a hosted one, and thrown away when its source is pointed somewhere else.
/// </summary>
/// <remarks>
/// <b>Against the host's own functions, with hand-built layers</b> — the rule, the key, the lifetime and the
/// move test are all decided without a database. What a real registered table does with them is the
/// conformance suite's <c>ARegisteredPostGisLayerServesTilesTests</c>.
/// </remarks>
public sealed class RegisteredLayersServeTilesTests
{
    private const string Here =
        "Host=db.example;Port=5432;Database=gis;Username=reader;Password=first";

    private static PublishedLayer Layer(
        string connection, bool hosted = false, TimeSpan? cacheLifetime = null, Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            new LayerDefinition(
                "parcels", "public", "parcels", "geom", 2320, "objectid", "objectid", isHosted: hosted),
            hosted ? "datastore" : "registered",
            connection,
            GeometryKind.Polygon,
            owner: null,
            SharingScope.Public,
            ServiceStatus.Started,
            cacheLifetime: cacheLifetime,
            capabilityCeiling: ["Query"]);

    private static readonly IReadOnlyList<FieldDescription> Attributes =
    [
        new FieldDescription("objectid", FieldType.Integer, false, null),
        new FieldDescription("name", FieldType.Text, true, 80),
    ];

    private static readonly TileAddress Tile = new(12, 2408, 1532);

    [Fact]
    public void A_registered_PostGIS_layer_is_tileable_and_so_is_every_other_kind()
    {
        Assert.True(VectorTileEndpoints.Tileable(Layer(Here, hosted: true)));

        // The reversal: Q-67 answered false here until 2026-09-29.
        Assert.True(VectorTileEndpoints.Tileable(Layer(Here)));

        Assert.True(VectorTileEndpoints.Tileable(Layer(GeoParquetLocator.For("/data/parquet"))));
        Assert.True(VectorTileEndpoints.Tileable(Layer(GeoParquetLocator.ForRemote("{\"url\":\"https://x/y.parquet\"}"))));
        Assert.True(VectorTileEndpoints.Tileable(Layer(GeoParquetLocator.ForAttached(false, "{\"path\":\"a.duckdb\"}"))));
        Assert.True(VectorTileEndpoints.Tileable(Layer(GeoParquetLocator.ForAttached(true, "{\"database\":\"md\"}"))));
    }

    [Fact]
    public void The_services_directory_and_the_seed_ask_the_same_rule()
    {
        PublishedService service = new(
            Guid.NewGuid(), "parcels", null, "FeatureServer", null, null,
            SharingScope.Public, ServiceStatus.Started, [Layer(Here)]);

        Assert.True(ServiceFaces.Tileable(service));
        Assert.Null(TileSeeder.WhyNotSeedable(service));
    }

    [Fact]
    public void A_registered_layers_key_carries_its_database_and_not_its_credential()
    {
        Guid id = Guid.NewGuid();

        TileCacheKey before = VectorTileEndpoints.KeyOf(Layer(Here, id: id), Attributes, Tile, Sources);

        // A rotated password is the same database: the pyramid is kept.
        TileCacheKey rotated = VectorTileEndpoints.KeyOf(
            Layer(Here.Replace("Password=first", "Password=second", StringComparison.Ordinal), id: id),
            Attributes, Tile, Sources);

        Assert.Equal(before, rotated);

        // Another database under the same layer id is not: the old tiles are unreachable.
        TileCacheKey moved = VectorTileEndpoints.KeyOf(
            Layer(Here.Replace("Database=gis", "Database=elsewhere", StringComparison.Ordinal), id: id),
            Attributes, Tile, Sources);

        Assert.NotEqual(before.Fingerprint, moved.Fingerprint);

        // And another host.
        TileCacheKey otherHost = VectorTileEndpoints.KeyOf(
            Layer(Here.Replace("db.example", "db2.example", StringComparison.Ordinal), id: id),
            Attributes, Tile, Sources);

        Assert.NotEqual(before.Fingerprint, otherHost.Fingerprint);
    }

    [Fact]
    public void A_hosted_layers_key_is_what_it_was_before_ADR_095()
    {
        // TilePipeline.Version did not move, so every hosted key must be byte for byte the one it was: the
        // fingerprint as it was computed before, with no version.
        TileCacheKey key = VectorTileEndpoints.KeyOf(Layer(Here, hosted: true), Attributes, Tile, Sources);

        Assert.Equal(
            TileCacheKey.FingerprintOf(2320, "geom", ["objectid", "name"], PostGisTileSource.Extent, PostGisTileSource.Buffer),
            key.Fingerprint);
    }

    [Fact]
    public void A_registered_layer_nobody_gave_a_lifetime_is_kept_five_minutes_here_and_downstream()
    {
        TimeSpan server = TimeSpan.FromMinutes(60);

        Assert.Equal(TimeSpan.FromMinutes(5), VectorTileEndpoints.LifetimeOf(Layer(Here), server));
        Assert.Equal(server, VectorTileEndpoints.LifetimeOf(Layer(Here, hosted: true), server));

        // The layer's own setting wins, longer or shorter — ADR-010 §5.3's declared volatility.
        Assert.Equal(
            TimeSpan.FromHours(6),
            VectorTileEndpoints.LifetimeOf(Layer(Here, cacheLifetime: TimeSpan.FromHours(6)), server));

        // ADR-069: the query face carries the same number for a layer nobody can edit.
        Assert.Equal(TimeSpan.FromMinutes(5), QueryResponseCaching.LifetimeOf(Layer(Here), server, writable: false));
    }

    [Fact]
    public void A_source_moved_to_another_database_is_a_move_and_a_new_password_is_not()
    {
        Assert.False(AdminEndpoints.Moved(Here, Here.Replace("Password=first", "Password=second", StringComparison.Ordinal)));
        Assert.False(AdminEndpoints.Moved(Here, Here + ";Maximum Pool Size=40"));

        Assert.True(AdminEndpoints.Moved(Here, Here.Replace("Database=gis", "Database=other", StringComparison.Ordinal)));
        Assert.True(AdminEndpoints.Moved(Here, Here.Replace("Port=5432", "Port=5433", StringComparison.Ordinal)));

        // A locator this server could not read is treated as moved: keeping tiles it cannot vouch for is worse.
        Assert.True(AdminEndpoints.Moved(null, Here));

        // A folder is compared by its place.
        Assert.False(AdminEndpoints.Moved(GeoParquetLocator.For("/data/a"), GeoParquetLocator.For("/data/a")));
        Assert.True(AdminEndpoints.Moved(GeoParquetLocator.For("/data/a"), GeoParquetLocator.For("/data/b")));
    }

    [Fact]
    public void A_table_with_no_spatial_index_is_said_once()
    {
        UnindexedLayerNotices notices = new();
        Guid id = Guid.NewGuid();

        notices.Note(id, "parcels", NullLogger.Instance);
        notices.Note(id, "parcels", NullLogger.Instance);
        notices.Note(Guid.NewGuid(), "roads", NullLogger.Instance);

        Assert.Equal(["parcels", "roads"], notices.Report());
        Assert.False(notices.Truncated);
    }

    private static readonly GeoParquetSources Sources = new(null);
}
