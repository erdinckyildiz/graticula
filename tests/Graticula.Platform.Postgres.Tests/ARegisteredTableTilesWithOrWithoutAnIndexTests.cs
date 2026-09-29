using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Npgsql;
using Xunit;
using Mvt = Graticula.Platform.Postgres.Tests.PostGisTileSourceTests.Mvt;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A registered table — not hosted, not in Web Mercator, and with no spatial index — still tiles, and the
/// describe says whether it is indexed — ADR-095 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of somebody else's table, built on purpose.</b> A hosted table is created by the importer in
/// Web Mercator with a GiST index; a registered one arrives however its owner made it. So this one is
/// <c>isHosted: false</c>, outside the <c>hosted</c> schema, in EPSG:4326, and has no index until the test
/// adds one — every assumption the tile statement could have been making about the datastore, removed.
/// </para>
/// <para>
/// <b>Built from the fixture's own rows</b>, so it needs no corpus and runs in CI.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ARegisteredTableTilesWithOrWithoutAnIndexTests : PostgresFixture
{
    private const int Z = 12;
    private const int X = 2377;
    private const int Y = 1535;

    private LayerDefinition Layer(string table) => new(
        name: table, schemaName: SchemaName, tableName: table, geometryColumn: "shape", srid: 4326,
        identityColumn: "gid", integerIdentityColumn: "gid", isHosted: false);

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Longitude and latitude of a point inside tile <see cref="Z"/>/<see cref="X"/>/<see cref="Y"/>.</summary>
    private static (double Lon, double Lat) Inside(double across, double down)
    {
        double n = 1 << Z;
        double lon = ((X + across) / n * 360.0) - 180.0;
        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - (2 * (Y + down) / n)))) * 180.0 / Math.PI;

        return (lon, lat);
    }

    private async Task<string> TableAsync(string name, bool indexed)
    {
        await ExecuteAsync(
            $"create table \"{SchemaName}\".{name} (gid integer primary key, label text, shape geometry(Point, 4326))");

        for (int i = 0; i < 5; i++)
        {
            (double lon, double lat) = Inside(0.2 + (i * 0.15), 0.5);

            await ExecuteAsync(
                $"insert into \"{SchemaName}\".{name} values ({i + 1}, 'p{i}', "
                + $"st_setsrid(st_makepoint({lon.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, "
                + $"{lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}), 4326))");
        }

        if (indexed)
        {
            await ExecuteAsync($"create index on \"{SchemaName}\".{name} using gist (shape)");
        }

        return name;
    }

    [Fact]
    public async Task A_table_with_no_spatial_index_tiles_and_is_described_as_unindexed()
    {
        string table = await TableAsync("reg_unindexed", indexed: false);

        LayerDescription described =
            await new PostGisFeatureSource(DataSource, Layer(table)).DescribeAsync(CancellationToken.None);

        Assert.False(described.SpatiallyIndexed);

        // The `&&` has no index to reach and scans instead; the tile is still the right one.
        byte[] tile = await new PostGisTileSource(DataSource, Layer(table), ["gid", "label"])
            .BuildAsync(new TileAddress(Z, X, Y), table, CancellationToken.None);

        Mvt.Layer layer = Assert.Single(Mvt.Decode(tile));

        Assert.Equal(table, layer.Name);
        Assert.Equal(5, layer.Features.Count);

        // The identity rides as a tag, exactly as a hosted layer's objectid does: no MVT feature id is set
        // for either, so a registered layer is read the same way by a client's hit test.
        Assert.All(layer.Features, feature => Assert.True(feature.Attributes.ContainsKey("gid")));
    }

    [Fact]
    public async Task An_indexed_table_is_described_as_indexed_and_a_view_as_not_known()
    {
        string table = await TableAsync("reg_indexed", indexed: true);

        LayerDescription described =
            await new PostGisFeatureSource(DataSource, Layer(table)).DescribeAsync(CancellationToken.None);

        Assert.True(described.SpatiallyIndexed);

        // A view's rows are indexed, or not, on a table this cannot see from the view — so not *no*.
        await ExecuteAsync($"create view \"{SchemaName}\".reg_view as select * from \"{SchemaName}\".{table}");

        LayerDescription view =
            await new PostGisFeatureSource(DataSource, Layer("reg_view")).DescribeAsync(CancellationToken.None);

        Assert.Null(view.SpatiallyIndexed);
    }
}
