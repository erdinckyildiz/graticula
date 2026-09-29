using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A service set to a TUREF tiling scheme is served on that grid, and set back it is served on Web Mercator
/// exactly as before — ADR-096.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against <c>GRATICULA_TEST_TILE_SERVICE</c>, and it changes that service's grid for as long as it runs</b>,
/// so it is in the collection that runs alone and puts Web Mercator back in <c>finally</c>. Every other tile
/// test reads the same service and would see a TUREF document if it ran beside this one.
/// </para>
/// <para>
/// <b>The zone is chosen from the data</b>: the TUREF TM zone whose central meridian is nearest the centre of
/// the service's extent. A fixture outside Turkey gets a grid derived from its UTM zone's area of use instead
/// (<c>{"wkid": 326nn}</c>), so the test still proves a grid other than Web Mercator — and says which it used.
/// </para>
/// <para>
/// <b>References none of the server's assemblies</b>, like the rest of this suite; the MVT walk is written
/// from the specification, and every length is read into a local before it is added to the cursor — the
/// evaluation-order defect <c>VectorTileConformanceTests</c>' walker had.
/// </para>
/// </remarks>
[Collection("server settings")]
public sealed class ATilingSchemeIsTheServiceSGridTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const double Half = 20037508.342789244;

    [Fact]
    public async Task A_service_on_a_TUREF_grid_serves_that_grid_and_goes_back_to_Mercator_unchanged()
    {
        (string root, string qualified, string name) = await ServiceAsync();
        string tiling = $"/admin/services/{Uri.EscapeDataString(name)}/tiling{FolderQuery(qualified)}";

        // Start from Web Mercator, whatever a failed run left.
        await SetAsync(tiling, """{"scheme":"webmercator"}""");

        JsonElement mercatorDocument = await GetJsonAsync($"/rest/services/{qualified}/VectorTileServer");
        Assert.Equal(102100, mercatorDocument.GetProperty("tileInfo").GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        (int mz, int mx, int my, byte[] mercatorBytes) = await FirstPopulatedMercatorTileAsync(root, qualified, mercatorDocument);

        (string ask, string said) = SchemeFor(mercatorDocument.GetProperty("fullExtent"));

        try
        {
            JsonElement set = await SetAsync(tiling, ask);
            Assert.True(set.GetProperty("changed").GetBoolean(), $"Setting {said} changed nothing: {set}");

            JsonElement grid = set.GetProperty("scheme");
            int wkid = grid.GetProperty("latestWkid").GetInt32();

            Assert.NotEqual(3857, wkid);

            // ---- the document states the grid, everywhere ----
            JsonElement document = await GetJsonAsync($"/rest/services/{qualified}/VectorTileServer");
            JsonElement info = document.GetProperty("tileInfo");

            Assert.Equal(wkid, info.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
            Assert.Equal(wkid, document.GetProperty("fullExtent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
            Assert.Equal(grid.GetProperty("origin").GetProperty("x").GetDouble(), info.GetProperty("origin").GetProperty("x").GetDouble());
            Assert.Equal(grid.GetProperty("origin").GetProperty("y").GetDouble(), info.GetProperty("origin").GetProperty("y").GetDouble());

            JsonElement[] lods = [.. info.GetProperty("lods").EnumerateArray()];
            JsonElement[] expected = [.. grid.GetProperty("lods").EnumerateArray()];

            Assert.Equal(expected.Length, lods.Length);

            for (int i = 0; i < lods.Length; i++)
            {
                Assert.Equal(i, lods[i].GetProperty("level").GetInt32());
                Assert.Equal(expected[i].GetProperty("resolution").GetDouble(), lods[i].GetProperty("resolution").GetDouble());
                Assert.True(
                    i == 0 || lods[i].GetProperty("resolution").GetDouble() < lods[i - 1].GetProperty("resolution").GetDouble(),
                    "The levels of detail do not go from coarse to fine.");
            }

            // ---- the Mercator address is not answered from the Mercator cache ----
            (HttpStatusCode _, byte[] _, string? state) = await FetchAsync(root, qualified, mz, my, mx);
            Assert.True(
                state is null or "MISS" or "COALESCED",
                $"Tile {mz}/{my}/{mx} answered X-Tile-Cache: {state} straight after the grid changed — a tile of the "
                + "other grid was found under this grid's key.");

            // ---- a tile of the new grid covers the data ----
            (int z, int x, int y, byte[] tile) = await FirstPopulatedTileAsync(root, qualified, document);
            (string layer, int extent, int features, int minimum, int maximum) = Decode(tile);

            Assert.True(features > 0, $"The {said} tile {z}/{y}/{x} decoded with no features.");
            Assert.False(string.IsNullOrWhiteSpace(layer));
            Assert.True(
                minimum >= -extent && maximum <= extent * 2,
                $"The {said} tile {z}/{y}/{x} has coordinates {minimum}..{maximum} against an extent of {extent} — outside "
                + "the tile and its buffer, which is what a tile cut on the wrong grid looks like.");

            // The second request for it is the cache's: the grid's own key is written and read.
            (_, byte[] again, string? second) = await FetchAsync(root, qualified, z, y, x);
            Assert.Equal("HIT", second);
            Assert.Equal(tile, again);
        }
        finally
        {
            await SetAsync(tiling, """{"scheme":"webmercator"}""");
        }

        // ---- back on Web Mercator: the same document and the same bytes ----
        JsonElement back = await GetJsonAsync($"/rest/services/{qualified}/VectorTileServer");
        Assert.Equal(102100, back.GetProperty("tileInfo").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(
            mercatorDocument.GetProperty("tileInfo").GetRawText(),
            back.GetProperty("tileInfo").GetRawText());

        (HttpStatusCode status, byte[] restored, string? after) = await FetchAsync(root, qualified, mz, my, mx);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(after is "MISS" or "COALESCED", $"The Mercator tile answered {after} after the switch back; the switch purges.");
        Assert.Equal(mercatorBytes, restored);
    }

    [Fact]
    public async Task A_grid_that_cannot_be_served_is_refused_and_nothing_changes()
    {
        (_, string qualified, string name) = await ServiceAsync();
        string tiling = $"/admin/services/{Uri.EscapeDataString(name)}/tiling{FolderQuery(qualified)}";

        foreach (string wrong in new[]
                 {
                     """{"scheme":"turef-tm31"}""",
                     """{"wkid":4326,"origin":{"x":0,"y":0},"level0Resolution":1,"levels":3}""",
                     """{"wkid":5254,"origin":{"x":0,"y":0},"resolutions":[10,20]}""",
                     """{"scheme":"turef-tm30","wkid":5254}""",
                 })
        {
            (int status, string body) = await AdminAsync(HttpMethod.Put, tiling, wrong);
            Assert.True(status == 400, $"{wrong} answered {status}: {body}");
        }

        JsonElement document = await GetJsonAsync($"/rest/services/{qualified}/VectorTileServer");
        Assert.Equal(102100, document.GetProperty("tileInfo").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    // ---------- helpers ----------

    private async Task<(string Root, string Qualified, string Name)> ServiceAsync()
    {
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string qualified = configured!.Trim('/');
        int cut = qualified.LastIndexOf('/');

        return (root, qualified, cut < 0 ? qualified : qualified[(cut + 1)..]);
    }

    private async Task<JsonElement> SetAsync(string tiling, string body)
    {
        (int status, string said) = await AdminAsync(HttpMethod.Put, tiling, body);
        Assert.True(status == 200, $"PUT {tiling} {body} answered {status}: {said}");
        return JsonDocument.Parse(said).RootElement;
    }

    /// <summary>The TUREF zone nearest the data, or a grid derived from its UTM zone when it is not in Turkey.</summary>
    private static (string Body, string Said) SchemeFor(JsonElement mercatorExtent)
    {
        double x = (mercatorExtent.GetProperty("xmin").GetDouble() + mercatorExtent.GetProperty("xmax").GetDouble()) / 2;
        double y = (mercatorExtent.GetProperty("ymin").GetDouble() + mercatorExtent.GetProperty("ymax").GetDouble()) / 2;

        double longitude = x / Half * 180;
        double latitude = (2 * Math.Atan(Math.Exp(y / Half * Math.PI)) - (Math.PI / 2)) * 180 / Math.PI;

        if (longitude is >= 25.6 and <= 44.9 && latitude is >= 35.8 and <= 42.2)
        {
            int meridian = (int)(Math.Clamp(Math.Round(longitude / 3), 9, 15) * 3);
            return ($$"""{"scheme":"turef-tm{{meridian}}"}""", $"TUREF / TM{meridian}");
        }

        int zone = (int)Math.Clamp(Math.Floor((longitude + 180) / 6) + 1, 1, 60);
        int utm = (latitude >= 0 ? 32600 : 32700) + zone;

        return ($$"""{"wkid":{{utm}}}""", $"a grid derived from EPSG:{utm}");
    }

    private static async Task<(HttpStatusCode Status, byte[] Bytes, string? State)> FetchAsync(
        string root, string qualified, int z, int y, int x)
    {
        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient http = new(handler);

        using HttpResponseMessage response = await http.GetAsync(new Uri(string.Create(
            CultureInfo.InvariantCulture, $"{root}/rest/services/{qualified}/VectorTileServer/tile/{z}/{y}/{x}.pbf")));

        string? state = response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), state);
    }

    /// <summary>The first Mercator tile at level 12 inside the service's extent that holds anything.</summary>
    private static async Task<(int Z, int X, int Y, byte[] Bytes)> FirstPopulatedMercatorTileAsync(
        string root, string qualified, JsonElement document)
    {
        JsonElement extent = document.GetProperty("fullExtent");
        const int Z = 12;
        long side = 1L << Z;
        double size = 2 * Half / side;

        int Column(double v) => (int)Math.Clamp(Math.Floor((v + Half) / size), 0, side - 1);
        int Row(double v) => (int)Math.Clamp(Math.Floor((Half - v) / size), 0, side - 1);

        return await FirstOfAsync(
            root, qualified, Z,
            Column(extent.GetProperty("xmin").GetDouble()), Column(extent.GetProperty("xmax").GetDouble()),
            Row(extent.GetProperty("ymax").GetDouble()), Row(extent.GetProperty("ymin").GetDouble()));
    }

    /// <summary>
    /// The first tile of the document's own grid inside its extent that holds anything, at the level whose tile
    /// is nearest ten kilometres across — a neighbourhood, whatever the grid.
    /// </summary>
    private static async Task<(int Z, int X, int Y, byte[] Bytes)> FirstPopulatedTileAsync(
        string root, string qualified, JsonElement document)
    {
        JsonElement info = document.GetProperty("tileInfo");
        JsonElement extent = document.GetProperty("fullExtent");
        double left = info.GetProperty("origin").GetProperty("x").GetDouble();
        double top = info.GetProperty("origin").GetProperty("y").GetDouble();
        int pixels = info.GetProperty("rows").GetInt32();

        JsonElement[] lods = [.. info.GetProperty("lods").EnumerateArray()];
        JsonElement chosen = lods.OrderBy(l => Math.Abs(Math.Log(l.GetProperty("resolution").GetDouble() * pixels / 10_000))).First();

        int z = chosen.GetProperty("level").GetInt32();
        double size = chosen.GetProperty("resolution").GetDouble() * pixels;
        double frame = lods[0].GetProperty("resolution").GetDouble() * pixels;
        long side = (long)Math.Ceiling((frame / size) - 1e-9);

        int Column(double v) => (int)Math.Clamp(Math.Floor((v - left) / size), 0, side - 1);
        int Row(double v) => (int)Math.Clamp(Math.Floor((top - v) / size), 0, side - 1);

        return await FirstOfAsync(
            root, qualified, z,
            Column(extent.GetProperty("xmin").GetDouble()), Column(extent.GetProperty("xmax").GetDouble()),
            Row(extent.GetProperty("ymax").GetDouble()), Row(extent.GetProperty("ymin").GetDouble()));
    }

    private static async Task<(int Z, int X, int Y, byte[] Bytes)> FirstOfAsync(
        string root, string qualified, int z, int x0, int x1, int y0, int y1)
    {
        // Bounded, for VectorTileConformanceTests' reason: a wide extent is thousands of tiles.
        const int Limit = 12;

        foreach (int x in Enumerable.Range(x0, Math.Min(Limit, x1 - x0 + 1)))
        {
            foreach (int y in Enumerable.Range(y0, Math.Min(Limit, y1 - y0 + 1)))
            {
                (HttpStatusCode status, byte[] bytes, _) = await FetchAsync(root, qualified, z, y, x);

                if (status == HttpStatusCode.OK && bytes.Length > 0)
                {
                    return (z, x, y, bytes);
                }
            }
        }

        Assert.Fail($"None of the level-{z} tiles covering the service's declared extent held anything.");
        throw new InvalidOperationException();
    }

    /// <summary>Walks the tile far enough to know it is real — every length read into a local first.</summary>
    private static (string Name, int Extent, int Features, int Minimum, int Maximum) Decode(byte[] tile)
    {
        string name = "";
        int extent = 4096, features = 0, minimum = int.MaxValue, maximum = int.MinValue;
        int i = 0;

        while (i < tile.Length)
        {
            (int field, int wire) = Tag(tile, ref i);

            if (field != 3 || wire != 2)
            {
                Skip(tile, ref i, wire);
                continue;
            }

            int layerLength = (int)Varint(tile, ref i);
            int end = i + layerLength;

            while (i < end)
            {
                (int lf, int lw) = Tag(tile, ref i);

                switch (lf)
                {
                    case 1 when lw == 2:
                        int nameLength = (int)Varint(tile, ref i);
                        name = System.Text.Encoding.UTF8.GetString(tile, i, nameLength);
                        i += nameLength;
                        break;

                    case 5 when lw == 0:
                        extent = (int)Varint(tile, ref i);
                        break;

                    case 2 when lw == 2:
                        features++;
                        int featureLength = (int)Varint(tile, ref i);
                        int stop = i + featureLength;
                        Coordinates(tile, i, stop, ref minimum, ref maximum);
                        i = stop;
                        break;

                    default:
                        Skip(tile, ref i, lw);
                        break;
                }
            }
        }

        return (name, extent, features, minimum, maximum);
    }

    private static void Coordinates(byte[] b, int i, int end, ref int minimum, ref int maximum)
    {
        while (i < end)
        {
            (int field, int wire) = Tag(b, ref i);

            if (field == 4 && wire == 2)
            {
                int geometryLength = (int)Varint(b, ref i);
                int stop = i + geometryLength;
                int x = 0, y = 0;

                while (i < stop)
                {
                    uint header = (uint)Varint(b, ref i);
                    int command = (int)(header & 7);
                    int count = (int)(header >> 3);

                    if (command == 7)
                    {
                        continue;
                    }

                    for (int n = 0; n < count && i < stop; n++)
                    {
                        int dx = ZigZag((uint)Varint(b, ref i));
                        int dy = ZigZag((uint)Varint(b, ref i));
                        x += dx;
                        y += dy;
                        minimum = Math.Min(minimum, Math.Min(x, y));
                        maximum = Math.Max(maximum, Math.Max(x, y));
                    }
                }
            }
            else
            {
                Skip(b, ref i, wire);
            }
        }
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
                int skipLength = (int)Varint(b, ref i);
                i += skipLength;
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
