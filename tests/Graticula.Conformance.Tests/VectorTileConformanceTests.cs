using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The sequence a vector tile client walks, over HTTP against a real process.
/// </summary>
/// <remarks>
/// <para>
/// <b>References none of our assemblies</b>, like the rest of this suite. A
/// conformance test that asserts against the same constant the server reads will
/// agree with the server while both are wrong — and for tiles that matters more
/// than for features, because the numbers here (4096, 64, the z/y/x order) are
/// exactly the ones both sides would get wrong together.
/// </para>
/// <para>
/// <b>It decodes the tile.</b> Status and byte count pass on a tile that is
/// empty or in the wrong place. The protobuf walk below is written from the
/// published MVT specification.
/// </para>
/// </remarks>
public sealed class VectorTileConformanceTests : ArcGisClient
{
    /// <summary>Where a tile service is expected, if one is published.</summary>
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private static string? TileService => Environment.GetEnvironmentVariable(ServiceVariable);

    private async Task<string> RequireTileServiceAsync()
    {
        await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(TileService),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip. Tiles come only "
            + "from hosted data (Q-67), so this suite cannot discover a tile service from the "
            + "catalogue the way the feature suite discovers a feature service — a published "
            + "layer may legitimately have no VectorTileServer. Name one that does.");

        return TileService!;
    }

    private async Task<JsonElement> ServiceDocumentAsync() =>
        await GetJsonAsync($"/rest/services/{await RequireTileServiceAsync()}/VectorTileServer");

    // ---------- what a client reads first ----------

    [Fact]
    public async Task Step1_the_service_document_says_where_the_tiles_are()
    {
        JsonElement service = await ServiceDocumentAsync();

        JsonElement tiles = Require(service, "tiles", "A client has no way to build a tile URL.");

        Assert.True(tiles.GetArrayLength() > 0, "'tiles' is present and empty, which is the same thing.");

        string template = tiles[0].GetString()!;

        // Row before column. This is the ArcGIS order and it is the reverse of
        // almost every other tile scheme; emitting {z}/{x}/{y} yields a map
        // mirrored about the diagonal, which reads as corrupt data.
        Assert.Contains("{z}", template, StringComparison.Ordinal);
        Assert.True(
            template.IndexOf("{y}", StringComparison.Ordinal)
                < template.IndexOf("{x}", StringComparison.Ordinal),
            $"'{template}' puts the column before the row. ArcGIS tile URLs are z/y/x.");
    }

    [Fact]
    public async Task Step2_the_tiling_scheme_is_complete_enough_to_place_a_tile()
    {
        JsonElement info = Require(
            await ServiceDocumentAsync(), "tileInfo", "Without it a client cannot place a tile.");

        JsonElement origin = Require(info, "origin", "A client cannot locate level 0.");
        JsonElement lods = Require(info, "lods", "A client cannot map a zoom to a scale.");

        Assert.True(origin.GetProperty("x").GetDouble() < 0, "the origin is the top-LEFT corner");
        Assert.True(origin.GetProperty("y").GetDouble() > 0, "the origin is the TOP-left corner");
        Assert.True(lods.GetArrayLength() > 1, "one level of detail is not a pyramid");

        // Esri's own code for Web Mercator Auxiliary Sphere. A client matches on
        // this, and declaring only 3857 — the same projection — is what made
        // every FeatureServer request from the real SDK fail.
        Assert.Equal(102100, info.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task Step3_the_default_style_resolves_and_names_a_source_layer()
    {
        string service = await RequireTileServiceAsync();
        JsonElement document = await ServiceDocumentAsync();

        string styles = Require(document, "defaultStyles", "A client has no style to load.")
            .GetString()!;

        JsonElement style = await GetJsonAsync(
            $"/rest/services/{service}/VectorTileServer/{styles}/root.json");

        Assert.Equal(8, style.GetProperty("version").GetInt32());

        JsonElement layer = style.GetProperty("layers")[0];

        Assert.True(
            layer.TryGetProperty("source-layer", out JsonElement sourceLayer),
            "'source-layer' is missing. The style loads and the map stays empty, with nothing in "
            + "the browser console to say why.");

        Assert.False(string.IsNullOrWhiteSpace(sourceLayer.GetString()));
    }

    // ---------- the tile itself ----------

    [Fact]
    public async Task Step4_a_tile_built_from_the_documents_decodes_to_real_geometry()
    {
        string service = await RequireTileServiceAsync();
        JsonElement document = await ServiceDocumentAsync();

        (int z, int x, int y, byte[] tile) = await FirstPopulatedTileAsync(service, document);

        (string name, int extent, int features, int minimum, int maximum) = Decode(tile);

        Assert.True(features > 0, "the tile decoded with no features in it");
        Assert.False(string.IsNullOrWhiteSpace(name), "the layer inside the tile has no name");

        // A y-flip, an off-by-one tile or the wrong extent all put coordinates
        // outside the tile plus its buffer, and all three decode cleanly.
        Assert.True(
            minimum >= -extent && maximum <= extent * 2,
            $"coordinates run {minimum}..{maximum} against a declared extent of {extent}, which "
            + "is far outside the tile and its buffer.");
    }

    [Fact]
    public async Task Step5_the_source_layer_in_the_style_matches_the_layer_in_the_tile()
    {
        // The join between the two documents, and the failure mode is silent:
        // the tile arrives, the style loads, nothing draws, and no error is
        // reported anywhere.
        string service = await RequireTileServiceAsync();
        JsonElement document = await ServiceDocumentAsync();

        string styles = document.GetProperty("defaultStyles").GetString()!;
        JsonElement style = await GetJsonAsync(
            $"/rest/services/{service}/VectorTileServer/{styles}/root.json");

        string declared = style.GetProperty("layers")[0].GetProperty("source-layer").GetString()!;

        (_, _, _, byte[] tile) = await FirstPopulatedTileAsync(service, document);
        (string actual, _, _, _, _) = Decode(tile);

        Assert.Equal(declared, actual);
    }

    [Fact]
    public async Task An_empty_tile_is_a_success_rather_than_a_failure()
    {
        // Most of a pyramid is empty. A 404 is a failure a client may retry and
        // may not cache, which turns the ocean into a retry storm.
        string service = await RequireTileServiceAsync();

        int status = await StatusOfAsync(
            $"/rest/services/{service}/VectorTileServer/tile/1/0/0.pbf");

        Assert.True(
            status is 200 or 204,
            $"an empty tile answered {status}; a client reads anything else as a fault.");
    }

    [Fact]
    public async Task An_address_outside_the_pyramid_is_refused_rather_than_faulted()
    {
        string service = await RequireTileServiceAsync();

        Assert.Equal(
            400,
            await StatusOfAsync($"/rest/services/{service}/VectorTileServer/tile/99/0/0.pbf"));
    }

    [Fact]
    public async Task A_tile_is_served_with_a_content_type_a_client_recognises()
    {
        string service = await RequireTileServiceAsync();
        JsonElement document = await ServiceDocumentAsync();
        (int z, int x, int y, _) = await FirstPopulatedTileAsync(service, document);

        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient http = new(handler);

        using HttpResponseMessage response = await http.GetAsync(
            new Uri($"{await RequireServerAsync()}/rest/services/{service}"
                    + $"/VectorTileServer/tile/{z}/{y}/{x}.pbf"));

        string? type = response.Content.Headers.ContentType?.MediaType;

        Assert.True(
            type is "application/vnd.mapbox-vector-tile" or "application/x-protobuf"
                or "application/octet-stream",
            $"a tile was served as '{type}'.");
    }

    // ---------- helpers ----------

    private async Task<byte[]> TileAsync(string service, int z, int y, int x)
    {
        using HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using HttpClient http = new(handler);

        return await http.GetByteArrayAsync(
            new Uri($"{await RequireServerAsync()}/rest/services/{service}"
                    + $"/VectorTileServer/tile/{z}/{y}/{x}.pbf"));
    }

    /// <summary>
    /// Every tile at zoom 12 that the service's declared extent overlaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All of them, not the centre one.</b> The first version of this took the
    /// centroid of the extent and asserted the tile there had features. It
    /// failed, and the server was right: the test layer is 20,000 buildings
    /// scattered across a wide extent, and the centroid landed in a gap between
    /// clusters. An extent is a bounding box, and a bounding box says nothing
    /// about what is in the middle of it.
    /// </para>
    /// <para>
    /// Derived from the document rather than hard-coded, so the suite works
    /// against any published layer. A service whose whole extent yields nothing
    /// is then a real defect rather than a badly chosen address.
    /// </para>
    /// </remarks>
    private static (int Z, int X, int Y)[] TilesCoveringExtent(JsonElement service)
    {
        JsonElement extent = service.GetProperty("fullExtent");
        JsonElement origin = service.GetProperty("tileInfo").GetProperty("origin");

        double left = origin.GetProperty("x").GetDouble();
        double top = origin.GetProperty("y").GetDouble();
        double span = Math.Abs(left) * 2;

        // Zoom 12 is deep enough that a tile is a neighbourhood rather than a
        // continent, and shallow enough that any real layer covers several.
        const int Zoom = 12;
        int side = 1 << Zoom;
        double size = span / side;

        int x0 = Math.Clamp((int)((extent.GetProperty("xmin").GetDouble() - left) / size), 0, side - 1);
        int x1 = Math.Clamp((int)((extent.GetProperty("xmax").GetDouble() - left) / size), 0, side - 1);
        int y0 = Math.Clamp((int)((top - extent.GetProperty("ymax").GetDouble()) / size), 0, side - 1);
        int y1 = Math.Clamp((int)((top - extent.GetProperty("ymin").GetDouble()) / size), 0, side - 1);

        // Bounded, because a world-wide extent at zoom 12 is 16 million tiles
        // and a conformance suite that walks them is a denial of service against
        // the thing it is testing.
        const int Limit = 12;

        return
        [
            .. from x in Enumerable.Range(x0, Math.Min(Limit, x1 - x0 + 1))
               from y in Enumerable.Range(y0, Math.Min(Limit, y1 - y0 + 1))
               select (Zoom, x, y),
        ];
    }

    /// <summary>The first tile in the service's extent that has anything in it.</summary>
    private async Task<(int Z, int X, int Y, byte[] Bytes)> FirstPopulatedTileAsync(
        string service, JsonElement document)
    {
        (int Z, int X, int Y)[] candidates = TilesCoveringExtent(document);

        foreach ((int z, int x, int y) in candidates)
        {
            byte[] bytes = await TileAsync(service, z, y, x);

            if (bytes.Length > 0)
            {
                return (z, x, y, bytes);
            }
        }

        Assert.Fail(
            $"None of the {candidates.Length} zoom-12 tiles covering this service's own declared "
            + "extent returned any bytes. Either the extent is wrong or the tile path is.");
        throw new InvalidOperationException();
    }

    /// <summary>Walks the tile far enough to know it is real.</summary>
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

            int end = i + (int)Varint(tile, ref i);

            while (i < end)
            {
                (int lf, int lw) = Tag(tile, ref i);

                switch (lf)
                {
                    case 1 when lw == 2:
                        int length = (int)Varint(tile, ref i);
                        name = System.Text.Encoding.UTF8.GetString(tile, i, length);
                        i += length;
                        break;

                    case 5 when lw == 0:
                        extent = (int)Varint(tile, ref i);
                        break;

                    case 2 when lw == 2:
                        features++;
                        int stop = i + (int)Varint(tile, ref i);
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
                int stop = i + (int)Varint(b, ref i);
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
                        x += ZigZag((uint)Varint(b, ref i));
                        y += ZigZag((uint)Varint(b, ref i));
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
