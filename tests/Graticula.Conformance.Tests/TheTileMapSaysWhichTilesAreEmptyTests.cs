using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A vector tile service's tile map — V-63, owner decision 2026-09-25: which tiles of a block can hold anything.
/// </summary>
/// <remarks>
/// <b>The promise is in one direction, and that direction is what is asserted.</b> A <c>0</c> tells a client not to
/// ask; if a tile said to be empty had features, the client would never draw them. So every tile the map calls empty,
/// in a whole level, is fetched and must be empty — and the tile under the middle of the data must be a <c>1</c>, or
/// the map would be all zeros and pass the first half vacuously.
/// </remarks>
[Collection("catalogue walk")]
public sealed class TheTileMapSaysWhichTilesAreEmptyTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";
    private const double Half = 20037508.342789244;
    private const int Level = 12;
    private const int Block = 8;

    [Fact]
    public async Task A_tile_the_map_calls_empty_is_empty_and_the_middle_of_the_data_is_not()
    {
        string root = await RequireServerAsync();
        string? service = Environment.GetEnvironmentVariable(ServiceVariable);
        Assert.False(string.IsNullOrWhiteSpace(service), $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string at = $"{root}/rest/services/{service!.Trim('/')}/VectorTileServer";

        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        JsonElement document = JsonDocument.Parse(await http.GetStringAsync(new Uri($"{at}?f=json"))).RootElement;
        Assert.Equal("tilemap", document.GetProperty("tileMap").GetString());

        JsonElement extent = document.GetProperty("fullExtent");
        double x = (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2;
        double y = (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2;

        // A level where the tile under the data carries features, so a 0 there would be caught below.
        int side = 1 << Level;
        double size = 2 * Half / side;
        int column = (int)Math.Floor((x + Half) / size);
        int row = (int)Math.Floor((Half - y) / size);
        int top = row - (Block / 2), left = column - (Block / 2);

        using (HttpResponseMessage middle = await http.GetAsync(new Uri($"{at}/tile/{Level}/{row}/{column}.pbf")))
        {
            Assert.True((await middle.Content.ReadAsByteArrayAsync()).Length > 0,
                $"The tile under the middle of the data, {Level}/{row}/{column}, is empty, so this level proves nothing.");
        }

        using HttpResponseMessage answered = await http.GetAsync(new Uri($"{at}/tilemap/{Level}/{top}/{left}/{Block}/{Block}?f=json"));
        string body = await answered.Content.ReadAsStringAsync();
        Assert.True(answered.StatusCode == HttpStatusCode.OK, $"The tile map answered {(int)answered.StatusCode}: {body}");

        JsonElement map = JsonDocument.Parse(body).RootElement;
        int[] data = [.. map.GetProperty("data").EnumerateArray().Select(v => v.GetInt32())];
        Assert.Equal(Block * Block, data.Length);
        Assert.Equal(top, map.GetProperty("location").GetProperty("top").GetInt32());

        Assert.True(data[((row - top) * Block) + (column - left)] == 1, $"The tile under the middle of the data, {Level}/{row}/{column}, is called empty.");
        Assert.Contains(0, data);

        // Every tile the map calls empty is fetched: none may carry anything.
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                continue;
            }

            int r = top + (i / Block), c = left + (i % Block);
            using HttpResponseMessage tile = await http.GetAsync(new Uri($"{at}/tile/{Level}/{r}/{c}.pbf"));
            byte[] bytes = await tile.Content.ReadAsByteArrayAsync();

            Assert.True(tile.IsSuccessStatusCode && bytes.Length == 0,
                $"Tile {Level}/{r}/{c} is called empty by the tile map and answered {(int)tile.StatusCode} with {bytes.Length} bytes.");
        }
    }

    [Fact]
    public async Task A_block_larger_than_the_map_answers_is_refused_with_the_limit()
    {
        string root = await RequireServerAsync();
        string? service = Environment.GetEnvironmentVariable(ServiceVariable);
        Assert.False(string.IsNullOrWhiteSpace(service), $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        using HttpResponseMessage refused = await http.GetAsync(
            new Uri($"{root}/rest/services/{service!.Trim('/')}/VectorTileServer/tilemap/10/0/0/1000/1000?f=json"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("4096", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
