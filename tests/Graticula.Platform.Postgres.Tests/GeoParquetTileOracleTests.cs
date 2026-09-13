using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Providers.DuckDb;
using Graticula.Providers.PostGis;
using Graticula.Testing;
using Graticula.Tiles;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A vector tile built by <see cref="PostGisTileSource"/> over a table, and the same rows served
/// from a GeoParquet file by <see cref="GeoParquetTileSource"/> and <see cref="PostGisMvtEncoder"/>
/// — ADR-066 §9, amended 2026-09-13.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoded content, not raw bytes.</b> <c>PostGisTileSource</c>'s own query carries no
/// <c>order by</c> — PostGIS streams whatever its scan meets, in scan order — while
/// <see cref="GeoParquetFeatureSource"/> always orders by identity (D-21's tiebreak). Two tiles
/// built from identical rows can therefore place the same feature at a different index inside
/// <c>ST_AsMVT</c>'s output, which is a different arrangement of identical bytes rather than a
/// disagreement. Features are matched across the two tiles by their <c>objectid</c> tag — carried
/// in every query below for exactly this reason — and everything else about each matched pair
/// (kind, rings, every other tag) is compared for exact equality.
/// </para>
/// <para>
/// <b>Three tiles: whole, clipped, empty.</b> Zoom 0 is the entire world and holds every row
/// unclipped. A deep zoom is searched at run time for one tile that holds some rows and not all —
/// the case where <c>ST_AsMVTGeom</c>'s buffer and clip actually do something — rather than a
/// hand-picked address that could stop being true if the fixture ever changes. A tile on the
/// opposite side of the pyramid from the data is empty from both providers.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GeoParquetTileOracleTests : PostgresFixture
{
    private const int Srid = 3857;
    private const int Side = 6;      // a 6×6 grid of squares
    private const double Cell = 100; // spacing, in metres — small enough to sit inside one deep tile

    private static readonly string[] Attributes = ["objectid", "name", "kind"];

    private static readonly IReadOnlyList<FieldDescription> AttributeDescriptions =
    [
        new("objectid", FieldType.BigInteger, false, null),
        new("name", FieldType.Text, true, null),
        new("kind", FieldType.Text, true, null),
    ];

    private async Task<(LayerDefinition PostGis, LayerDefinition Parquet, GeoParquetFolder Folder, string Path)>
        FixturesAsync()
    {
        await using (NpgsqlCommand create = DataSource.CreateCommand($"""
            create table tilegrid (
              objectid bigint primary key, name text, kind text, geom geometry(Polygon, {Srid}));

            insert into tilegrid
            select i,
                   'n' || i,
                   (array['field', 'forest', 'water'])[1 + i % 3],
                   ST_MakeEnvelope(x, y, x + 50, y + 50, {Srid})
            from (
              select i, (i % {Side}) * {Cell} as x, (i / {Side}) * {Cell} as y
              from generate_series(0, {Side * Side - 1}) as i
            ) placed;

            create index on tilegrid using gist (geom);
            analyze tilegrid;
            """))
        {
            await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        List<(object?[] Values, Graticula.Geometries.Geometry? Shape)> rows = [];

        await using (NpgsqlCommand read = DataSource.CreateCommand(
            "select objectid, name, kind, st_asbinary(geom) from tilegrid order by objectid"))
        await using (NpgsqlDataReader reader = await read.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                rows.Add((
                    [reader.GetInt64(0), reader.GetString(1), reader.GetString(2)],
                    Graticula.Geometries.WkbReader.Read((byte[])reader[3])));
            }
        }

        string folder = Path.Combine(Path.GetTempPath(), "graticula-tile-oracle-" + Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(folder);

        GeoParquetFixture.Write(
            Path.Combine(folder, "tilegrid.parquet"),
            [new("objectid", "BIGINT"), new("name", "VARCHAR"), new("kind", "VARCHAR")],
            rows,
            srid: Srid);

        GeoParquetFolder opened = new(folder, new GeoParquetOptions { MemoryLimit = "256MB", Threads = 2 });

        LayerDefinition postgis = new(
            "tilegrid", SchemaName, "tilegrid", "geom", Srid, "objectid", "objectid", isHosted: true);

        LayerDefinition parquet = new(
            "tilegrid", "main", "tilegrid", "geom", Srid, "objectid", "objectid", isHosted: false);

        return (postgis, parquet, opened, folder);
    }

    /// <summary>A deep-zoom tile that holds some rows of the grid and not all — found by asking
    /// PostGIS itself, so this stays true if the fixture's numbers ever change.</summary>
    private async Task<TileAddress> ClippedTileAsync(int z)
    {
        // The tile the grid's own centre sits in, and its eight neighbours — the grid is small
        // enough (550m) to be within a couple of tiles of that point at a deep zoom.
        double centre = (Side * Cell) / 2.0;
        TileAddress guess = CoveringTile(z, centre, centre);

        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                TileAddress candidate = new(z, guess.X + dx, guess.Y + dy);

                if (!candidate.IsValid)
                {
                    continue;
                }

                await using NpgsqlCommand count = DataSource.CreateCommand(
                    "select count(*) from tilegrid where geom && ST_TileEnvelope(@z, @x, @y)");

                count.Parameters.AddWithValue("z", candidate.Z);
                count.Parameters.AddWithValue("x", candidate.X);
                count.Parameters.AddWithValue("y", candidate.Y);

                long matched = (long)(await count.ExecuteScalarAsync(CancellationToken.None))!;

                if (matched > 0 && matched < Side * Side)
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException(
            $"No tile at z={z} near the grid's centre holds some rows and not all; the fixture's "
            + "geometry changed under this search.");
    }

    private static TileAddress CoveringTile(int z, double x, double y)
    {
        double half = TileAddress.WebMercatorHalfExtent;
        double size = half * 2.0 / (1L << z);

        int column = (int)Math.Floor((x + half) / size);
        int row = (int)Math.Floor((half - y) / size);

        return new TileAddress(z, column, row);
    }

    [Fact]
    public async Task The_tile_holding_the_whole_grid_holds_every_row_from_both_providers()
    {
        // <b>z14, not z0.</b> This was the whole-world tile and its guard failed on first run: a
        // 50 m square at z0 is a two-hundredth of one of the tile's 4,096 units, ST_AsMVTGeom
        // drops it, and both providers agreed on an empty tile — a comparison of two nothings.
        // At z14 a unit is 0.6 m, so every square survives, and the grid (0–550 m from the
        // origin, which is a tile corner at every zoom) sits inside the one tile north-east of it.
        (LayerDefinition postgis, _, GeoParquetFolder folder, string path) = await FixturesAsync();

        try
        {
            double centre = (Side * Cell) / 2.0;
            TileAddress holding = CoveringTile(14, centre, centre);

            PostGisTileSource fromTable = new(DataSource, postgis, Attributes);
            byte[] tile = await fromTable.BuildAsync(holding, "tilegrid", CancellationToken.None);

            // A fixture-only guard, independent of GeoParquet: if this ever came back short, the
            // comparison below would still pass by both providers agreeing on the wrong answer.
            Assert.Equal(Side * Side, PostGisTileSourceTests.Mvt.Decode(tile).Single().Features.Count);

            await CompareAtAsync(postgis, folder, holding);
        }
        finally
        {
            folder.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]    // the whole world, where every 50 m square is below one unit and dropped: both empty
    [InlineData(5, 31, 0)]   // the far side of the pyramid at a shallow zoom: nothing
    public async Task A_fixed_tile_decodes_to_the_same_content_from_both_providers(int z, int x, int y) =>
        await CompareAsync(new TileAddress(z, x, y));

    [Fact]
    public async Task A_tile_that_clips_some_rows_and_not_others_decodes_to_the_same_content()
    {
        (LayerDefinition postgis, _, GeoParquetFolder folder, string path) = await FixturesAsync();

        try
        {
            TileAddress address = await ClippedTileAsync(18);
            await using NpgsqlCommand count = DataSource.CreateCommand(
                "select count(*) from tilegrid where geom && ST_TileEnvelope(@z, @x, @y)");
            count.Parameters.AddWithValue("z", address.Z);
            count.Parameters.AddWithValue("x", address.X);
            count.Parameters.AddWithValue("y", address.Y);
            long matched = (long)(await count.ExecuteScalarAsync(CancellationToken.None))!;

            Assert.InRange(matched, 1, (Side * Side) - 1);

            await CompareAtAsync(postgis, folder, address);
        }
        finally
        {
            folder.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    private async Task CompareAsync(TileAddress address)
    {
        (LayerDefinition postgis, _, GeoParquetFolder folder, string path) = await FixturesAsync();

        try
        {
            await CompareAtAsync(postgis, folder, address);
        }
        finally
        {
            folder.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    private async Task CompareAtAsync(LayerDefinition postgis, GeoParquetFolder folder, TileAddress address)
    {
        PostGisTileSource fromTable = new(DataSource, postgis, Attributes);

        GeoParquetFeatureSource reader = new(
            folder,
            new LayerDefinition("tilegrid", "main", "tilegrid", "geom", Srid, "objectid", "objectid", isHosted: false),
            new NoopProjector());

        GeoParquetTileSource fromFile = new(
            reader,
            new LayerDefinition("tilegrid", "main", "tilegrid", "geom", Srid, "objectid", "objectid", isHosted: false),
            AttributeDescriptions,
            new PostGisMvtEncoder(DataSource));

        byte[] fromTableBytes = await fromTable.BuildAsync(address, "tilegrid", CancellationToken.None);
        byte[] fromFileBytes = await fromFile.BuildAsync(address, "tilegrid", CancellationToken.None);

        if (fromTableBytes.Length == 0 && fromFileBytes.Length == 0)
        {
            // Both agree the tile is empty — a correct answer, and there is no layer to decode.
            return;
        }

        PostGisTileSourceTests.Mvt.Layer expected = Assert.Single(PostGisTileSourceTests.Mvt.Decode(fromTableBytes));
        PostGisTileSourceTests.Mvt.Layer actual = Assert.Single(PostGisTileSourceTests.Mvt.Decode(fromFileBytes));

        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Extent, actual.Extent);
        Assert.Equal(expected.Features.Count, actual.Features.Count);

        Dictionary<string, PostGisTileSourceTests.Mvt.Feature> byObjectId = expected.Features
            .ToDictionary(f => Convert.ToString(f.Attributes["objectid"], CultureInfo.InvariantCulture)!);

        List<string> disagreements = [];

        foreach (PostGisTileSourceTests.Mvt.Feature feature in actual.Features)
        {
            string id = Convert.ToString(feature.Attributes["objectid"], CultureInfo.InvariantCulture)!;

            if (!byObjectId.TryGetValue(id, out PostGisTileSourceTests.Mvt.Feature? counterpart))
            {
                disagreements.Add($"objectid {id}: in the GeoParquet tile and not the PostGIS one");
                continue;
            }

            if (counterpart.Kind != feature.Kind)
            {
                disagreements.Add($"objectid {id}: geometry kind {counterpart.Kind} vs {feature.Kind}");
            }

            if (!RingsEqual(counterpart.Rings, feature.Rings))
            {
                disagreements.Add($"objectid {id}: rings differ");
            }

            foreach ((string key, object? value) in feature.Attributes)
            {
                if (!counterpart.Attributes.TryGetValue(key, out object? expectedValue)
                    || !Equals(Normalise(expectedValue), Normalise(value)))
                {
                    disagreements.Add(
                        $"objectid {id}: tag '{key}' PostGIS={counterpart.Attributes.GetValueOrDefault(key)} "
                        + $"GeoParquet={value}");
                }
            }
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));
    }

    /// <summary>Numeric tag values compare as <see cref="long"/> regardless of which MVT value
    /// variant carried them (int, uint or sint), since PostGIS is free to choose any of the three
    /// for the same PostgreSQL integer type.</summary>
    private static object? Normalise(object? value) => value switch
    {
        int i => (long)i,
        uint u => (long)u,
        ulong ul => (long)ul,
        _ => value,
    };

    private static bool RingsEqual(List<List<(int X, int Y)>> a, List<List<(int X, int Y)>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].SequenceEqual(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class NoopProjector : Graticula.Geometries.IProjector
    {
        public Task<IReadOnlyList<Graticula.Geometries.Geometry>> GeneralizeAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid, double tolerance,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fixture is entirely in EPSG:3857; nothing should ask to move it.");

        public Task<(IReadOnlyList<Graticula.Geometries.Geometry> Projected, Graticula.Geometries.ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, int toSrid,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fixture is entirely in EPSG:3857; nothing should ask to move it.");

        public Task<IReadOnlyList<Graticula.Geometries.Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Graticula.Geometries.Geometry> geometries, int fromSrid, string definition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Graticula.Geometries.Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult<Graticula.Geometries.Envelope?>(null);

        public Task<IReadOnlyList<Graticula.Geometries.KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Graticula.Geometries.KnownReference>>([]);

        public Task<Graticula.Geometries.ProjectionProvenance> DescribeAsync(
            int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            Task.FromResult(new Graticula.Geometries.ProjectionProvenance("test", null));
    }
}
