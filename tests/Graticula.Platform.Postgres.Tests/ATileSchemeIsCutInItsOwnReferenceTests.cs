using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Npgsql;
using Xunit;
using Mvt = Graticula.Platform.Postgres.Tests.PostGisTileSourceTests.Mvt;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A tile of a service cut on TUREF / TM30 is cut on TM30's grid, in TM30, by the tile statement — ADR-096.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every position below is worked by hand from the grid</b>, not read back from the code. TM30's origin is
/// (97,000, 4,921,000) and level 0 is 3,460.9375 m a pixel (D-288, 2026-09-30), so level 3 is 221,500 m a tile:
/// tile (3, 1, 1) runs x 318,500–540,000 and y 4,478,000–4,699,500, and a point at (500,000, 4,500,000) is
/// 181,500 / 221,500 × 4,096 = 3,356.3 units from its west edge and 199,500 / 221,500 × 4,096 = 3,689.2 from its top.
/// </para>
/// <para>
/// <b>Against PostGIS, because the envelope is PostGIS's half of the arithmetic</b>: the box is computed in C#
/// and handed over as four numbers, and what these check is that PostGIS clips, transforms and simplifies
/// against it — the claim a unit test of the C# alone cannot make.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ATileSchemeIsCutInItsOwnReferenceTests : PostgresFixture
{
    private static VectorTileScheme Tm30 => VectorTileSchemes.Find("turef-tm30")!.Scheme;

    private async Task<LayerDefinition> ShapesAsync(int srid)
    {
        // Built in TM30 and stored in the reference asked for, so a layer in 4326 holds the same ground.
        string stored = srid == 5254 ? "g" : $"ST_Transform(g, {srid})";

        await using NpgsqlCommand create = DataSource.CreateCommand($"""
            create table shapes (objectid bigint primary key, kind text, geom geometry(Geometry, {srid}));

            insert into shapes
            select id, kind, {stored} from (values
              (1, 'square', ST_MakeEnvelope(495000, 4495000, 505000, 4505000, 5254)),
              (2, 'point',  ST_SetSRID(ST_MakePoint(500000, 4500000), 5254)),
              (3, 'circle', ST_Buffer(ST_SetSRID(ST_MakePoint(500000, 4500000), 5254), 1500, 'quad_segs=64'))
            ) as v(id, kind, g);

            create index on shapes using gist (geom);
            analyze shapes;
            """);

        await create.ExecuteNonQueryAsync(CancellationToken.None);

        return new LayerDefinition("shapes", SchemaName, "shapes", "geom", srid, "objectid", "objectid", false);
    }

    private async Task<Mvt.Layer?> TileAsync(LayerDefinition layer, TileAddress address)
    {
        byte[] tile = await new PostGisTileSource(DataSource, layer, ["kind"], Tm30)
            .BuildAsync(address, "shapes", CancellationToken.None);

        return Mvt.Decode(tile).SingleOrDefault();
    }

    /// <summary>The TM30 tile with nothing left out or simplified — what the grid alone decides.</summary>
    private async Task<Mvt.Layer> UngeneralisedAsync(TileAddress address)
    {
        Envelope box = Tm30.Envelope(address);

        await using NpgsqlCommand command = DataSource.CreateCommand($"""
            with bounds as (select ST_MakeEnvelope(@minx, @miny, @maxx, @maxy, 5254) as geom),
            tile as (
                select ST_AsMVTGeom(t.geom, bounds.geom, {PostGisTileSource.Extent}, {PostGisTileSource.Buffer}, true)
                       as geom, t.kind
                from shapes t, bounds
                where t.geom && bounds.geom
            )
            select ST_AsMVT(tile.*, 'shapes', {PostGisTileSource.Extent}, 'geom') from tile
            """);

        command.Parameters.AddWithValue("minx", box.MinX);
        command.Parameters.AddWithValue("miny", box.MinY);
        command.Parameters.AddWithValue("maxx", box.MaxX);
        command.Parameters.AddWithValue("maxy", box.MaxY);

        return Assert.Single(Mvt.Decode((byte[])(await command.ExecuteScalarAsync(CancellationToken.None))!));
    }

    private static (int X, int Y) PointOf(Mvt.Layer layer) =>
        layer.Features.Single(f => (string?)f.Attributes["kind"] == "point").Rings.Single().Single();

    private static int Vertices(Mvt.Layer layer, string kind) =>
        layer.Features.Where(f => (string?)f.Attributes["kind"] == kind).Sum(f => f.Rings.Sum(r => r.Count));

    [Fact]
    public async Task A_TM30_tile_holds_its_features_where_the_TM30_grid_puts_them()
    {
        LayerDefinition layer = await ShapesAsync(5254);

        Mvt.Layer tile = (await TileAsync(layer, new TileAddress(3, 1, 1)))!;

        Assert.Equal(PostGisTileSource.Extent, tile.Extent);
        Assert.Equal(["circle", "point", "square"], tile.Features.Select(f => (string)f.Attributes["kind"]!).Order());

        // Level 3 is 221,500 m a tile from (97,000, 4,921,000), D-288's grid over TUREF's area of use. The point is
        // 403,000 m east and 421,000 m south of the origin: column 1, row 1, and 181,500 m and 199,500 m into that
        // tile, which is 3,356.3 and 3,689.2 of 4,096.
        (int x, int y) = PointOf(tile);
        Assert.InRange(x, 3355, 3358);
        Assert.InRange(y, 3688, 3691);

        // The 10 km square: 3,264–3,449 across and 3,597–3,782 down, give or take the grid's rounding.
        foreach ((int px, int py) in tile.Features
                     .Where(f => (string?)f.Attributes["kind"] == "square").SelectMany(f => f.Rings).SelectMany(r => r))
        {
            Assert.InRange(px, 3261, 3451);
            Assert.InRange(py, 3594, 3784);
        }

        // The tile beside it, to the west, holds nothing: the grid is TM30's and not Web Mercator's.
        Assert.Null(await TileAsync(layer, new TileAddress(3, 0, 1)));
    }

    [Fact]
    public async Task A_layer_stored_in_degrees_is_moved_onto_the_TM30_grid()
    {
        LayerDefinition layer = await ShapesAsync(4326);

        Mvt.Layer tile = (await TileAsync(layer, new TileAddress(3, 1, 1)))!;

        // TUREF and WGS 84 differ by centimetres; a level-3 unit is 54 m.
        (int x, int y) = PointOf(tile);
        Assert.InRange(x, 3354, 3359);
        Assert.InRange(y, 3687, 3692);
    }

    [Fact]
    public async Task A_TM30_level_is_simplified_by_its_pixel_size()
    {
        LayerDefinition layer = await ShapesAsync(5254);

        // Level 9 (6.76 m a pixel) is simplified; level 10 (3.38 m) is not. The tiles holding the circle's centre:
        // level 9 is 3,460.9375 m a tile, column 403,000 / 3,460.9 = 116 and row 421,000 / 3,460.9 = 121; level 10
        // is 1,730.46875 m, column 232 and row 243.
        TileAddress seven = new(9, 116, 121);
        TileAddress eight = new(10, 232, 243);

        int simplified = Vertices((await TileAsync(layer, seven))!, "circle");
        int raw7 = Vertices(await UngeneralisedAsync(seven), "circle");

        Assert.True(raw7 > 0, "The level-9 tile does not hold the circle, so it cannot say anything about it.");
        Assert.True(simplified < raw7, $"The circle has {simplified} vertices at level 9 and {raw7} unsimplified.");

        int kept = Vertices((await TileAsync(layer, eight))!, "circle");
        int raw8 = Vertices(await UngeneralisedAsync(eight), "circle");

        Assert.True(raw8 > 0, "The level-10 tile does not hold the circle, so it cannot say anything about it.");
        Assert.Equal(raw8, kept);
    }

    [Fact]
    public async Task An_address_outside_the_TM30_grid_is_refused_before_the_database()
    {
        LayerDefinition layer = await ShapesAsync(5254);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new PostGisTileSource(DataSource, layer, ["kind"], Tm30)
                .BuildAsync(new TileAddress(19, 0, 0), "shapes", CancellationToken.None));
    }

    /// <remarks>
    /// <b>The built-ins' frozen numbers against this PostGIS</b> — ADR-096 §2 Alternative D: the grids are
    /// written down rather than derived at start, and this is where a register that moved an area of use, or a
    /// projection that disagrees with the Krüger series the numbers were made with, would show. Needs
    /// <c>postgis_srs</c>, PostGIS 3.4.
    /// </remarks>
    /// <summary>The country each built-in's grid is derived from projects where its frozen numbers say — D-288.</summary>
    [Fact]
    public async Task Every_built_in_s_country_projects_where_its_numbers_say()
    {
        // The ground itself is TUREF's own area of use, as the register states it.
        await using (NpgsqlCommand register = DataSource.CreateCommand(
            "select st_x(point_sw), st_y(point_sw), st_x(point_ne), st_y(point_ne) from postgis_srs('EPSG', '5252')"))
        await using (NpgsqlDataReader area = await register.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await area.ReadAsync(CancellationToken.None), "postgis_srs knows nothing of EPSG:5252.");
            Assert.Equal(VectorTileSchemes.Country, new Envelope(area.GetDouble(0), area.GetDouble(1), area.GetDouble(2), area.GetDouble(3)));
        }

        foreach (BuiltInTileScheme built in VectorTileSchemes.BuiltIn)
        {
            await using NpgsqlCommand command = DataSource.CreateCommand("""
                select ST_XMin(t), ST_YMin(t), ST_XMax(t), ST_YMax(t)
                  from (select ST_Transform(ST_Segmentize(ST_MakeEnvelope(@w, @s, @e, @n, 4326), 0.05), @srid) as t) m
                """);

            command.Parameters.AddWithValue("w", built.Covers.MinX);
            command.Parameters.AddWithValue("s", built.Covers.MinY);
            command.Parameters.AddWithValue("e", built.Covers.MaxX);
            command.Parameters.AddWithValue("n", built.Covers.MaxY);
            command.Parameters.AddWithValue("srid", built.Scheme.Srid);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));

            Assert.InRange(reader.GetDouble(0), built.CoversProjected.MinX - 1, built.CoversProjected.MinX + 1);
            Assert.InRange(reader.GetDouble(1), built.CoversProjected.MinY - 1, built.CoversProjected.MinY + 1);
            Assert.InRange(reader.GetDouble(2), built.CoversProjected.MaxX - 1, built.CoversProjected.MaxX + 1);
            Assert.InRange(reader.GetDouble(3), built.CoversProjected.MaxY - 1, built.CoversProjected.MaxY + 1);
        }
    }

    [Fact]
    public async Task Every_built_in_s_area_of_use_is_the_register_s_and_projects_where_its_numbers_say()
    {
        foreach (BuiltInTileScheme built in VectorTileSchemes.BuiltIn)
        {
            await using NpgsqlCommand command = DataSource.CreateCommand("""
                with area as (
                    select ST_MakeEnvelope(st_x(point_sw), st_y(point_sw), st_x(point_ne), st_y(point_ne), 4326) as g
                      from postgis_srs('EPSG', @code)
                ),
                moved as (select ST_Transform(ST_Segmentize(g, 0.05), @srid) as t from area)
                select ST_XMin(g), ST_YMin(g), ST_XMax(g), ST_YMax(g),
                       ST_XMin(t), ST_YMin(t), ST_XMax(t), ST_YMax(t)
                  from area, moved
                """);

            command.Parameters.AddWithValue("code", built.Scheme.Srid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("srid", built.Scheme.Srid);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

            Assert.True(await reader.ReadAsync(CancellationToken.None), $"postgis_srs knows nothing of EPSG:{built.Scheme.Srid}.");

            Assert.Equal(built.AreaOfUse.MinX, reader.GetDouble(0), 6);
            Assert.Equal(built.AreaOfUse.MinY, reader.GetDouble(1), 6);
            Assert.Equal(built.AreaOfUse.MaxX, reader.GetDouble(2), 6);
            Assert.Equal(built.AreaOfUse.MaxY, reader.GetDouble(3), 6);

            // Within a metre: the kilometre rounding is what the grid depends on, and a metre is far inside it.
            Assert.InRange(reader.GetDouble(4), built.Projected.MinX - 1, built.Projected.MinX + 1);
            Assert.InRange(reader.GetDouble(5), built.Projected.MinY - 1, built.Projected.MinY + 1);
            Assert.InRange(reader.GetDouble(6), built.Projected.MaxX - 1, built.Projected.MaxX + 1);
            Assert.InRange(reader.GetDouble(7), built.Projected.MaxY - 1, built.Projected.MaxY + 1);
        }
    }

    [Fact]
    public async Task Rows_handed_to_the_encoder_come_out_as_the_table_s_tile_does()
    {
        // The GeoParquet path: rows read elsewhere, encoded by the same statement on the same grid.
        LayerDefinition layer = await ShapesAsync(5254);
        TileAddress address = new(3, 1, 1);

        List<MvtRow> rows =
        [
            Row("square", new Polygon(new LinearRing(XySequence.Wrap(
                [495000, 4495000, 505000, 4495000, 505000, 4505000, 495000, 4505000, 495000, 4495000])))),
            Row("point", new Point(500000, 4500000)),
        ];

        byte[] encoded = await new PostGisMvtEncoder(DataSource)
            .EncodeAsync(rows, address, Tm30, "shapes", 5254, CancellationToken.None);

        Mvt.Layer fromRows = Assert.Single(Mvt.Decode(encoded));
        Mvt.Layer fromTable = (await TileAsync(layer, address))!;

        Assert.Equal(PointOf(fromTable), PointOf(fromRows));

        // <b>The same rings, compared from the same start.</b> The two paths hand PostGIS the square
        // differently and each ring came back starting at another corner (measured on the fixture,
        // 2026-09-29): the same closed ring, drawn identically, which an element-by-element compare
        // called different. Each ring is rotated to start at its smallest vertex before comparing.
        Assert.Equal(
            Canonical(fromTable.Features.Single(f => (string?)f.Attributes["kind"] == "square").Rings),
            Canonical(fromRows.Features.Single(f => (string?)f.Attributes["kind"] == "square").Rings));

        static List<List<(int X, int Y)>> Canonical(List<List<(int X, int Y)>> rings) =>
            rings.Select(ring =>
            {
                List<(int X, int Y)> open = ring.Count > 1 && ring[0] == ring[^1] ? ring[..^1] : ring;
                int start = open.IndexOf(open.Min());
                List<(int X, int Y)> turned = [.. open.Skip(start), .. open.Take(start)];
                turned.Add(turned[0]);
                return turned;
            }).ToList();

        static MvtRow Row(string kind, Geometry geometry) =>
            new(geometry, [new MvtTag("kind", FieldType.Text, kind)]);
    }
}
