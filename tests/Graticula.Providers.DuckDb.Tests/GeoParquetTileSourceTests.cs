using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Testing;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Providers.DuckDb.Tests;

/// <summary>
/// The mechanism of a GeoParquet layer's vector tiles — ADR-066 §9, amended 2026-09-13.
/// </summary>
/// <remarks>
/// <b>The encoder is a fake here, on purpose.</b> This is about what <see cref="GeoParquetTileSource"/>
/// reads and hands over — the box test, the bound, the attribute shape — not about what PostGIS does
/// with it; that comparison is <c>GeoParquetAgainstPostgisTests</c>' job and needs a real database
/// this project's tests cannot reach.
/// </remarks>
public sealed class GeoParquetTileSourceTests : IDisposable
{
    private readonly TemporaryFolder _temporary = new();
    private readonly GeoParquetFolder _folder;
    private readonly ShiftingProjector _projector = new();

    public GeoParquetTileSourceTests()
    {
        GeoParquetFixture.Write(_temporary.File("grid.parquet"), Shapes.GridColumns, Shapes.Grid(3), srid: 3857);
        _folder = new GeoParquetFolder(_temporary.Path, new GeoParquetOptions { MemoryLimit = "256MB", Threads = 2 });
    }

    public void Dispose()
    {
        _folder.Dispose();
        _temporary.Dispose();
    }

    private GeoParquetTileSource Source(RecordingEncoder encoder, IReadOnlyList<FieldDescription>? attributes = null)
    {
        LayerDefinition layer = new("grid", "main", "grid", "geom", 3857, "objectid", "objectid", isHosted: false);
        GeoParquetFeatureSource reader = new(_folder, layer, _projector);

        return new GeoParquetTileSource(
            reader, layer,
            attributes ?? [new FieldDescription("name", FieldType.Text, true, null)],
            encoder);
    }

    [Fact]
    public async Task An_invalid_address_is_refused_before_anything_is_read()
    {
        RecordingEncoder encoder = new();
        GeoParquetTileSource source = Source(encoder);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => source.BuildAsync(new TileAddress(-1, 0, 0), "grid", CancellationToken.None));

        Assert.Empty(encoder.Calls);
    }

    [Fact]
    public async Task A_tile_covering_the_whole_world_reaches_every_row_and_the_encoder_is_never_asked_twice()
    {
        // The fixture's 3×3 grid sits at (0,0)–(5,5) in EPSG:3857 units, which is inside every
        // tile at zoom 0 — the whole world, one tile.
        RecordingEncoder encoder = new();
        GeoParquetTileSource source = Source(encoder);

        byte[] result = await source.BuildAsync(new TileAddress(0, 0, 0), "grid", CancellationToken.None);

        Assert.Same(encoder.Answer, result);
        MvtRow[] rows = Assert.Single(encoder.Calls).Rows;
        Assert.Equal(9, rows.Length);

        MvtTag tag = Assert.Single(rows[0].Attributes);
        Assert.Equal("name", tag.Name);
        Assert.Equal(FieldType.Text, tag.Type);
        Assert.Equal("p1", tag.Value);
    }

    [Fact]
    public async Task A_tile_nowhere_near_the_data_reads_nothing_and_never_asks_the_encoder()
    {
        // z=4 tile 15,0 is the far edge of the pyramid at that zoom — nowhere near (0,0)-(5,5).
        RecordingEncoder encoder = new();
        GeoParquetTileSource source = Source(encoder);

        byte[] result = await source.BuildAsync(new TileAddress(4, 15, 0), "grid", CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(encoder.Calls);
    }

    [Fact]
    public async Task No_attributes_is_a_real_shape_and_still_reaches_the_encoder()
    {
        RecordingEncoder encoder = new();
        GeoParquetTileSource source = Source(encoder, attributes: []);

        await source.BuildAsync(new TileAddress(0, 0, 0), "grid", CancellationToken.None);

        MvtRow[] rows = Assert.Single(encoder.Calls).Rows;
        Assert.All(rows, r => Assert.Empty(r.Attributes));
    }

    [Fact]
    public async Task A_tile_denser_than_one_page_reaches_the_encoder_whole()
    {
        // The million-building layer's tile over central Istanbul matched 93,923 features at z6.
        // The first version of this class read one page of 50,000 and drew the lowest-numbered
        // half of the city; a tile cannot say it is partial, so every row has to arrive.
        int side = 230;
        Assert.True(side * side > GeoParquetTileSource.PageRows);

        GeoParquetFixture.Write(_temporary.File("dense.parquet"), Shapes.GridColumns, Shapes.Grid(side), srid: 3857);
        LayerDefinition layer = new("dense", "main", "dense", "geom", 3857, "objectid", "objectid", isHosted: false);
        RecordingEncoder encoder = new();

        GeoParquetTileSource source = new(
            new GeoParquetFeatureSource(_folder, layer, _projector),
            layer,
            [new FieldDescription("objectid", FieldType.BigInteger, false, null)],
            encoder);

        await source.BuildAsync(new TileAddress(0, 0, 0), "dense", CancellationToken.None);

        MvtRow[] rows = Assert.Single(encoder.Calls).Rows;
        Assert.Equal(side * side, rows.Length);
        Assert.Equal(side * side, rows.Select(r => Convert.ToInt64(r.Attributes[0].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().Count());
    }

    private sealed class RecordingEncoder : IMvtEncoder
    {
        public List<(MvtRow[] Rows, TileAddress Address, string LayerName, int Srid)> Calls { get; } = [];

        public byte[] Answer { get; } = [1, 2, 3];

        public Task<byte[]> EncodeAsync(
            IReadOnlyList<MvtRow> rows, TileAddress address, string layerName, int srid,
            CancellationToken cancellationToken)
        {
            Calls.Add(([.. rows], address, layerName, srid));
            return Task.FromResult(Answer);
        }
    }
}
