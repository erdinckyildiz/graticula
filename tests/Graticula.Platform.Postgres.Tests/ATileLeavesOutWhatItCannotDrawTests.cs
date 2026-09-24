using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Npgsql;
using Xunit;
using Mvt = Graticula.Platform.Postgres.Tests.PostGisTileSourceTests.Mvt;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A tile leaves out a shape smaller than a pixel and simplifies at half a pixel through z14 — Q-157.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's decision of 2026-09-23, measured in benchmarks/tile-generalisation</b>: a z10 tile over
/// Istanbul went from 16.6 MB to 918 KB. These build their own few shapes rather than needing that corpus,
/// so they run where the corpus is not — CI among them.
/// </para>
/// <para>
/// <b>Simplification is asserted against the statement without it</b>, run here, rather than against a
/// vertex count written down: the grid ST_AsMVTGeom snaps to decides the count, and what matters is that
/// z14 differs from it and z15 does not.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ATileLeavesOutWhatItCannotDrawTests : PostgresFixture
{
    // The z12 tile touching the origin from the north-east; its width is 9,784 m, so a pixel is 19 m.
    private const int Z = 12;
    private const int X = 2048;
    private const int Y = 2047;

    private async Task<LayerDefinition> ShapesAsync()
    {
        await using NpgsqlCommand create = DataSource.CreateCommand("""
            create table shapes (objectid bigint primary key, kind text, geom geometry(Geometry, 3857));

            insert into shapes values
              (1, 'small',  ST_MakeEnvelope(1000, 1000, 1010, 1010, 3857)),
              (2, 'large',  ST_MakeEnvelope(2000, 2000, 2500, 2500, 3857)),
              (3, 'point',  ST_SetSRID(ST_MakePoint(3000, 3000), 3857)),
              (4, 'circle', ST_Buffer(ST_SetSRID(ST_MakePoint(6000, 6000), 3857), 1500, 'quad_segs=64'));

            create index on shapes using gist (geom);
            analyze shapes;
            """);

        await create.ExecuteNonQueryAsync(CancellationToken.None);

        return new LayerDefinition("shapes", SchemaName, "shapes", "geom", 3857, "objectid", "objectid", false);
    }

    private async Task<Mvt.Layer> TileAsync(LayerDefinition layer, int z, int x, int y)
    {
        byte[] tile = await new PostGisTileSource(DataSource, layer, ["kind"])
            .BuildAsync(new TileAddress(z, x, y), "shapes", CancellationToken.None);

        return Assert.Single(Mvt.Decode(tile));
    }

    /// <summary>The statement before Q-157, for what a tile holds with nothing left out or simplified.</summary>
    private async Task<Mvt.Layer> UngeneralisedAsync(int z, int x, int y)
    {
        await using NpgsqlCommand command = DataSource.CreateCommand($"""
            with bounds as (select ST_TileEnvelope({z}, {x}, {y}) as geom),
            tile as (
                select ST_AsMVTGeom(t.geom, bounds.geom, {PostGisTileSource.Extent}, {PostGisTileSource.Buffer}, true)
                       as geom, t.kind
                from shapes t, bounds
                where t.geom && bounds.geom
            )
            select ST_AsMVT(tile.*, 'shapes', {PostGisTileSource.Extent}, 'geom') from tile
            """);

        return Assert.Single(Mvt.Decode((byte[])(await command.ExecuteScalarAsync(CancellationToken.None))!));
    }

    private static string[] Kinds(Mvt.Layer layer) =>
        [.. layer.Features.Select(f => (string)f.Attributes["kind"]!).Order(StringComparer.Ordinal)];

    private static int Vertices(Mvt.Layer layer, string kind) =>
        layer.Features.Where(f => (string?)f.Attributes["kind"] == kind).Sum(f => f.Rings.Sum(r => r.Count));

    [Fact]
    public async Task A_shape_smaller_than_a_pixel_is_left_out_and_a_point_never_is()
    {
        LayerDefinition layer = await ShapesAsync();

        // z12: a pixel is 19 m, so the 10 m square goes and everything else stays.
        Assert.Equal(["circle", "large", "point"], Kinds(await TileAsync(layer, Z, X, Y)));

        // The statement before Q-157 drew it — this is what changed, not what the fixture holds.
        Assert.Contains("small", Kinds(await UngeneralisedAsync(Z, X, Y)));

        // z16, where a pixel is 1.2 m: the tile 611 m wide holding x and y 1000 m, where the square is large
        // enough to draw.
        Assert.Contains("small", Kinds(await TileAsync(layer, 16, 32769, 32766)));
    }

    [Fact]
    public async Task Simplification_stops_after_zoom_fourteen()
    {
        LayerDefinition layer = await ShapesAsync();

        // The tiles holding the circle centred 6000 m east and north of the origin: at z14 (2,446 m) the
        // whole circle, at z15 (1,223 m) the tile from 4,892 m, which its edge crosses at the corner.
        (int x14, int y14) = (8194, 8189);
        (int x15, int y15) = (16388, 16379);

        int simplified = Vertices(await TileAsync(layer, 14, x14, y14), "circle");
        int raw14 = Vertices(await UngeneralisedAsync(14, x14, y14), "circle");

        Assert.True(raw14 > 0, "The z14 tile does not hold the circle, so it cannot say anything about it.");
        Assert.True(
            simplified < raw14,
            $"The circle came back with {simplified} vertices at z14 and {raw14} without generalising — nothing was simplified.");

        int kept = Vertices(await TileAsync(layer, 15, x15, y15), "circle");
        int raw15 = Vertices(await UngeneralisedAsync(15, x15, y15), "circle");

        Assert.True(raw15 > 0, "The z15 tile does not hold the circle, so it cannot say anything about it.");
        Assert.Equal(raw15, kept);
    }
}
