using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Npgsql;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// Tiles built by PostGIS, decoded here and checked against the source.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tile is decoded rather than measured.</b> Byte count and status code
/// pass on a tile that is empty, y-flipped or in the wrong place; the only check
/// worth writing walks the protobuf and looks at the coordinates. The decoder is
/// written here from the MVT specification rather than taken from a library,
/// because a library sharing assumptions with the encoder agrees with it about
/// whatever they both get wrong.
/// </para>
/// <para>
/// ADR-021 put encoding in the database. That makes these tests the only place
/// the tile bytes are examined at all.
/// </para>
/// </remarks>
/// <remarks>
/// <b>Excluded from CI, deliberately and out loud — [ADR-048](../../docs/adr/ADR-048-ci-does-not-run-the-real-data-suites.md).</b>
/// This class reads a real OpenStreetMap extract, which a developer machine has and
/// CI does not. It fails rather than skips when the table is absent, which is the
/// right behaviour and is why CI cannot simply run it. The trait is what CI filters
/// on, and the workflow prints what it excluded so a green run never claims more
/// than it proved.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Needs", "RealCorpus")]
public sealed class PostGisTileSourceTests : PostgresFixture
{
    /// <summary>A dense Istanbul tile: 792 buildings, confirmed against the table.</summary>
    private const int Z = 16;
    private const int X = 38031;
    private const int Y = 24571;

    private static LayerDefinition Buildings => new(
        name: "osm-buildings",
        schemaName: "public",
        tableName: "osm_buildings",
        geometryColumn: "way",
        srid: 3857,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: true);

    private async Task RequireCorpusAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand(
            "select to_regclass('public.osm_buildings') is not null");

        Assert.True(
            (bool)(await command.ExecuteScalarAsync())!,
            "public.osm_buildings is not loaded. These tests build real tiles from real data and "
            + "fail rather than skip; load the corpus with experiments/_env.");
    }

    private PostGisTileSource Source(params string[] attributes) =>
        new(DataSource, Buildings, attributes);

    // ---------- the tile ----------

    [Fact]
    public async Task A_populated_tile_decodes_to_the_features_the_table_holds_there()
    {
        await RequireCorpusAsync();

        byte[] tile = await Source().BuildAsync(new TileAddress(Z, X, Y), "buildings", CancellationToken.None);
        Mvt.Layer layer = Assert.Single(Mvt.Decode(tile));

        await using NpgsqlCommand count = DataSource.CreateCommand(
            $"select count(*) from public.osm_buildings where way && ST_TileEnvelope({Z},{X},{Y})");

        Assert.Equal((long)(await count.ExecuteScalarAsync())!, layer.Features.Count);
        Assert.True(layer.Features.Count > 100, "the chosen tile should be a dense one");
    }

    [Fact]
    public async Task The_layer_inside_the_tile_carries_the_name_it_was_given()
    {
        // The style document's source-layer must match this exactly. When they
        // disagree the tile arrives, the style loads, and nothing draws.
        await RequireCorpusAsync();

        byte[] tile = await Source().BuildAsync(
            new TileAddress(Z, X, Y), "a-particular-name", CancellationToken.None);

        Assert.Equal("a-particular-name", Mvt.Decode(tile).Single().Name);
    }

    [Fact]
    public async Task Every_coordinate_lands_inside_the_tile_plus_its_declared_buffer()
    {
        // The one geometric property checkable without a reference image. A
        // y-flip, an off-by-one tile or the wrong extent all put coordinates
        // outside this range, and all three produce a tile that decodes cleanly.
        await RequireCorpusAsync();

        byte[] tile = await Source().BuildAsync(new TileAddress(Z, X, Y), "buildings", CancellationToken.None);
        Mvt.Layer layer = Mvt.Decode(tile).Single();

        int low = -PostGisTileSource.Buffer;
        int high = PostGisTileSource.Extent + PostGisTileSource.Buffer;

        foreach ((int px, int py) in layer.Features.SelectMany(f => f.Rings).SelectMany(r => r))
        {
            Assert.InRange(px, low, high);
            Assert.InRange(py, low, high);
        }
    }

    [Fact]
    public async Task The_declared_extent_is_what_the_tile_actually_declares()
    {
        await RequireCorpusAsync();

        byte[] tile = await Source().BuildAsync(new TileAddress(Z, X, Y), "buildings", CancellationToken.None);

        Assert.Equal(PostGisTileSource.Extent, Mvt.Decode(tile).Single().Extent);
    }

    [Fact]
    public async Task Every_ring_is_closed()
    {
        await RequireCorpusAsync();

        byte[] tile = await Source().BuildAsync(new TileAddress(Z, X, Y), "buildings", CancellationToken.None);

        foreach (List<(int X, int Y)> ring in
                 Mvt.Decode(tile).Single().Features.SelectMany(f => f.Rings))
        {
            Assert.True(ring.Count >= 4, "a closed ring needs at least four positions");
            Assert.Equal(ring[0], ring[^1]);
        }
    }

    // ---------- emptiness ----------

    [Fact]
    public async Task A_tile_with_nothing_in_it_comes_back_empty_rather_than_failing()
    {
        // Most of a pyramid is empty and the ocean must not be an error. The
        // endpoint turns this into 204; a throw here would make it a 500.
        await RequireCorpusAsync();

        byte[] tile = await Source().BuildAsync(new TileAddress(1, 0, 0), "buildings", CancellationToken.None);

        Assert.Empty(tile);
    }

    // ---------- attributes ----------

    [Fact]
    public async Task A_requested_column_rides_along_as_a_tag()
    {
        await RequireCorpusAsync();

        byte[] tile = await Source("objectid").BuildAsync(
            new TileAddress(Z, X, Y), "buildings", CancellationToken.None);

        Assert.Contains("objectid", Mvt.Decode(tile).Single().Keys);
    }

    [Fact]
    public async Task No_attributes_means_no_keys_and_a_smaller_tile()
    {
        // The reason the endpoint caps attributes: the key and value tables are
        // repeated in every tile of the pyramid.
        await RequireCorpusAsync();

        byte[] bare = await Source().BuildAsync(new TileAddress(Z, X, Y), "b", CancellationToken.None);
        byte[] tagged = await Source("objectid").BuildAsync(new TileAddress(Z, X, Y), "b", CancellationToken.None);

        Assert.Empty(Mvt.Decode(bare).Single().Keys);
        Assert.True(bare.Length < tagged.Length, "tags cost bytes in every tile that carries them");
    }

    // ---------- refusals ----------

    [Fact]
    public async Task An_address_outside_the_pyramid_is_refused_before_it_reaches_the_database()
    {
        // Left to PostGIS this is a database error, which the error mapper turns
        // into a 500 — the server reporting a fault of its own for a caller's
        // arithmetic.
        await RequireCorpusAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Source().BuildAsync(new TileAddress(2, 99, 1), "b", CancellationToken.None));
    }

    // ---------- the spatial reference ----------

    /// <summary>
    /// A layer in another reference is tiled, not refused and not empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test used to assert the opposite, and the opposite was a
    /// symptom.</b> ST_TileEnvelope returns a Web Mercator rectangle; against a
    /// 4326 layer the <c>&amp;&amp;</c> operator compared two boxes whose
    /// numbers are in different units, found no overlap, and ST_AsMVTGeom
    /// clipped everything away — with nothing raising. Every tile on Earth came
    /// back empty, silently. The first fix was to refuse SRID ≠ 3857 at the
    /// endpoint.
    /// </para>
    /// <para>
    /// <b>Owner correction 2026-08-15: refusing was the wrong fix.</b> A layer
    /// keeps the projection it arrived in, and the tile path transforms per
    /// request — the envelope once into the layer's reference so the index still
    /// answers the filter, each surviving row on the way out. So this asserts a
    /// non-empty tile from a 4326 layer, which is the behaviour the silent
    /// emptiness was hiding all along.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_layer_in_another_spatial_reference_is_tiled_by_transforming_it()
    {
        await using (NpgsqlCommand setup = DataSource.CreateCommand("""
            drop table if exists public.srid_probe;
            create table public.srid_probe (
                objectid serial primary key,
                geom     geometry(Polygon, 4326));
            insert into public.srid_probe (geom)
            select ST_SetSRID(ST_MakeEnvelope(
                       28.9 + i * 0.0005, 41.0 + i * 0.0005,
                       28.9 + i * 0.0005 + 0.0003, 41.0 + i * 0.0005 + 0.0003), 4326)
            from generate_series(1, 200) i;
            """))
        {
            await setup.ExecuteNonQueryAsync();
        }

        try
        {
            LayerDefinition wgs84 = new(
                name: "srid-probe",
                schemaName: "public",
                tableName: "srid_probe",
                geometryColumn: "geom",
                srid: 4326,
                identityColumn: "objectid",
                integerIdentityColumn: "objectid",
                isHosted: true);

            // The tile covering that longitude and latitude in Web Mercator.
            byte[] tile = await new PostGisTileSource(DataSource, wgs84, [])
                .BuildAsync(new TileAddress(12, 2377, 1535), "probe", CancellationToken.None);

            Assert.NotEmpty(tile);

            Mvt.Layer only = Assert.Single(Mvt.Decode(tile));

            Assert.Equal("probe", only.Name);
            Assert.NotEmpty(only.Features);
        }
        finally
        {
            await using NpgsqlCommand drop = DataSource.CreateCommand("drop table public.srid_probe");
            await drop.ExecuteNonQueryAsync();
        }
    }

    // ---------- a decoder written from the specification ----------

    /// <summary>A minimal Mapbox Vector Tile reader.</summary>
    private static class Mvt
    {
        internal sealed record Feature(int Kind, List<List<(int X, int Y)>> Rings);

        internal sealed record Layer(
            string Name, int Extent, List<string> Keys, List<Feature> Features);

        public static List<Layer> Decode(byte[] tile)
        {
            List<Layer> layers = [];
            int i = 0;

            while (i < tile.Length)
            {
                (int field, int wire) = Tag(tile, ref i);

                if (field == 3 && wire == 2)
                {
                    int length = (int)Varint(tile, ref i);
                    layers.Add(ReadLayer(tile, i, i + length));
                    i += length;
                }
                else
                {
                    Skip(tile, ref i, wire);
                }
            }

            return layers;
        }

        private static Layer ReadLayer(byte[] b, int i, int end)
        {
            string name = "?";
            int extent = 4096;
            List<string> keys = [];
            List<Feature> features = [];

            while (i < end)
            {
                (int field, int wire) = Tag(b, ref i);

                switch (field)
                {
                    case 1 when wire == 2:
                        int nameLength = (int)Varint(b, ref i);
                        name = System.Text.Encoding.UTF8.GetString(b, i, nameLength);
                        i += nameLength;
                        break;
                    case 2 when wire == 2:
                        int featureLength = (int)Varint(b, ref i);
                        features.Add(ReadFeature(b, i, i + featureLength));
                        i += featureLength;
                        break;
                    case 3 when wire == 2:
                        int keyLength = (int)Varint(b, ref i);
                        keys.Add(System.Text.Encoding.UTF8.GetString(b, i, keyLength));
                        i += keyLength;
                        break;
                    case 5 when wire == 0:
                        extent = (int)Varint(b, ref i);
                        break;
                    default:
                        Skip(b, ref i, wire);
                        break;
                }
            }

            return new Layer(name, extent, keys, features);
        }

        private static Feature ReadFeature(byte[] b, int i, int end)
        {
            int kind = 0;
            List<uint> commands = [];

            while (i < end)
            {
                (int field, int wire) = Tag(b, ref i);

                if (field == 3 && wire == 0)
                {
                    kind = (int)Varint(b, ref i);
                }
                else if (field == 4 && wire == 2)
                {
                    int length = (int)Varint(b, ref i);
                    int stop = i + length;

                    while (i < stop)
                    {
                        commands.Add((uint)Varint(b, ref i));
                    }
                }
                else
                {
                    Skip(b, ref i, wire);
                }
            }

            return new Feature(kind, Rings(commands));
        }

        /// <summary>Walks the command stream into rings.</summary>
        private static List<List<(int X, int Y)>> Rings(List<uint> commands)
        {
            List<List<(int X, int Y)>> rings = [];
            List<(int X, int Y)> current = [];
            int x = 0, y = 0, i = 0;

            while (i < commands.Count)
            {
                uint header = commands[i++];
                int command = (int)(header & 0x7);
                int count = (int)(header >> 3);

                switch (command)
                {
                    case 1:                                   // MoveTo
                        for (int n = 0; n < count; n++)
                        {
                            if (current.Count > 0)
                            {
                                rings.Add(current);
                                current = [];
                            }

                            x += ZigZag(commands[i++]);
                            y += ZigZag(commands[i++]);
                            current.Add((x, y));
                        }

                        break;

                    case 2:                                   // LineTo
                        for (int n = 0; n < count; n++)
                        {
                            x += ZigZag(commands[i++]);
                            y += ZigZag(commands[i++]);
                            current.Add((x, y));
                        }

                        break;

                    case 7:                                   // ClosePath
                        if (current.Count > 0)
                        {
                            // ClosePath does not repeat the first position on the
                            // wire; the ring is closed by definition. Materialised
                            // here so "is it closed" is a real assertion rather
                            // than one that is true by construction.
                            current.Add(current[0]);
                            rings.Add(current);
                            current = [];
                        }

                        break;

                    default:
                        i = commands.Count;
                        break;
                }
            }

            if (current.Count > 0)
            {
                rings.Add(current);
            }

            return rings;
        }

        private static int ZigZag(uint n) => (int)(n >> 1) ^ -(int)(n & 1);

        private static (int Field, int Wire) Tag(byte[] b, ref int i)
        {
            ulong key = Varint(b, ref i);
            return ((int)(key >> 3), (int)(key & 7));
        }

        private static ulong Varint(byte[] b, ref int i)
        {
            ulong value = 0;
            int shift = 0;

            while (i < b.Length)
            {
                byte x = b[i++];
                value |= (ulong)(x & 0x7F) << shift;

                if ((x & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
            }

            return value;
        }

        private static void Skip(byte[] b, ref int i, int wire)
        {
            switch (wire)
            {
                case 0:
                    Varint(b, ref i);
                    break;
                case 1:
                    i += 8;
                    break;
                case 2:
                    i += (int)Varint(b, ref i);
                    break;
                case 5:
                    i += 4;
                    break;
                default:
                    i = b.Length;
                    break;
            }
        }
    }
}
