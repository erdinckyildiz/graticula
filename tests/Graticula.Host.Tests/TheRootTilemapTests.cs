using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Tiles;
using Graticula.Tiles.Packages;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The tilemap with no block — Esri's <i>Vector Tilemap</i> — which ArcGIS Pro's Download Map and Field Maps 26.2.2 ask
/// for (measured 2026-11-12) and which was a 404. The shape and the bound are <c>VectorTilemapIndex</c>'s and are tested in
/// Core; what is tested here is the host's rule for a tile that exists, shared with the block form, and the copy a package
/// carries. The route itself needs a datastore and is in the conformance suite.
/// </summary>
public sealed class TheRootTilemapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "root-tilemap-" + Guid.NewGuid().ToString("N"));

    public TheRootTilemapTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_tile_may_hold_data_when_a_layer_that_draws_there_touches_it_and_an_unknown_extent_never_says_empty()
    {
        Envelope tile = new(0, 0, 10, 10);

        Assert.True(VectorTileEndpoints.MayHold([new Envelope(5, 5, 6, 6)], tile));
        Assert.True(VectorTileEndpoints.MayHold([new Envelope(10, 10, 20, 20)], tile)); // touching counts
        Assert.False(VectorTileEndpoints.MayHold([new Envelope(11, 11, 20, 20)], tile));
        Assert.True(VectorTileEndpoints.MayHold([new Envelope(11, 11, 20, 20), null], tile));
        Assert.False(VectorTileEndpoints.MayHold([], tile));
    }

    [Fact]
    public void A_packages_index_covers_exactly_the_plans_rectangles_and_the_tiles_above_them()
    {
        // A box in the north-east, levels 2 and 3.
        TileSeedPlan plan = TileSeedPlan.For(new Envelope(1_000_000, 1_000_000, 2_000_000, 2_000_000), 2, 3);

        byte[] bytes = TileExportPackage.TilemapOf(plan, VectorTileScheme.WebMercator)!;
        JsonElement index = JsonDocument.Parse(bytes).RootElement.GetProperty("index");

        // Only the north-east quadrant of level 1 exists, and below it only the south-west child (column 2, row 1).
        Assert.Equal(4, index.GetArrayLength());
        Assert.Equal(0, index[0].GetInt32());
        Assert.Equal(JsonValueKind.Array, index[1].ValueKind);
        Assert.Equal(0, index[2].GetInt32());
        Assert.Equal(0, index[3].GetInt32());

        JsonElement northEast = index[1];
        Assert.Equal(0, northEast[0].GetInt32());
        Assert.Equal(0, northEast[1].GetInt32());
        Assert.Equal(JsonValueKind.Array, northEast[2].ValueKind);
        Assert.Equal(0, northEast[3].GetInt32());

        // Level 3 is the plan's last, so what is there is a leaf and nothing is described below it.
        foreach (JsonElement cell in northEast[2].EnumerateArray())
        {
            Assert.True(cell.ValueKind == JsonValueKind.Number && cell.GetInt32() is 0 or 1);
        }

        Assert.Contains(northEast[2].EnumerateArray(), cell => cell.GetInt32() == 1);
    }

    [Fact]
    public async Task A_vtpk_carries_the_index_at_the_tilemap_folders_root_and_one_without_it_is_unchanged()
    {
        using TileExportStaging staging = new(Path.Combine(_directory, "a.staging"));
        await staging.AddAsync(new TileAddress(1, 0, 0), Encoding.UTF8.GetBytes("tile"), CancellationToken.None);

        TileSeedPlan plan = TileSeedPlan.For(new Envelope(1_000_000, 1_000_000, 2_000_000, 2_000_000), 1, 2);
        byte[] index = TileExportPackage.TilemapOf(plan, VectorTileScheme.WebMercator)!;

        foreach ((bool with, string name) in new[] { (true, "with.vtpk"), (false, "without.vtpk") })
        {
            TileExportPackage.VtpkDocuments documents = new(
                Encoding.UTF8.GetBytes("{}"),
                Encoding.UTF8.GetBytes("{\"version\":8}"),
                new Dictionary<string, byte[]>(),
                Encoding.UTF8.GetBytes("<i/>"),
                Encoding.UTF8.GetBytes("<p/>"),
                with ? index : null);

            string path = Path.Combine(_directory, name);

            await using (FileStream output = File.Create(path))
            {
                await TileExportPackage.WriteVtpkAsync(output, staging, documents, CancellationToken.None);
            }

            using ZipArchive package = ZipFile.OpenRead(path);
            ZipArchiveEntry? entry = package.GetEntry("p12/tilemap/root.json");

            Assert.Equal(with, entry is not null);

            if (entry is not null)
            {
                using StreamReader reader = new(entry.Open());
                Assert.Equal(Encoding.UTF8.GetString(index), await reader.ReadToEndAsync());
            }
        }
    }

    [Fact]
    public void A_scheme_whose_levels_do_not_double_gets_no_index_in_its_package()
    {
        Assert.Null(VectorTileScheme.Create("custom", 5254, 0, 1_000_000, [1000.0, 300.0, 100.0], out VectorTileScheme? scheme));

        Assert.NotNull(scheme);

        // Inside the grid: x 0 to 512,000 and y 488,000 to 1,000,000.
        TileSeedPlan plan = TileSeedPlan.For(scheme!, new Envelope(1_000, 990_000, 2_000, 995_000), 1, 2);

        Assert.Equal("p12/tilemap/root.json", VectorTilePackage.TilemapDocument);
        Assert.Null(TileExportPackage.TilemapOf(plan, scheme!));
    }
}
