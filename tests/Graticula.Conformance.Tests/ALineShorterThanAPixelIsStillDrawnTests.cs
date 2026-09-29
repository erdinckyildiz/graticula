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
/// A line is not left out of a tile for being shorter than a pixel — ADR-085 §5.1 as amended 2026-09-29.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's measurement in ArcGIS Pro, on the fixture's own lines.</b> The showcase's province boundaries
/// are 5,433 short lines, and at 1:10.7 million Pro drew them dashed: every piece under a pixel was left out of
/// the tile, and a boundary made of pieces became a row of gaps. The fixture's four routes of the multi-layer
/// service are each under a kilometre across, so at the deepest level where every one of them is smaller than a
/// pixel the old rule left all four out and the tile held none.
/// </para>
/// <para>
/// <b>Against <c>GRATICULA_TEST_MULTILAYER</c></b>, the service <c>seed-conformance.py</c> gives a point, a line
/// and a polygon layer. The MVT walk is written from the specification, every length read into a local before it
/// moves the cursor — the evaluation-order defect <c>VectorTileConformanceTests</c>' walker had.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class ALineShorterThanAPixelIsStillDrawnTests : ArcGisClient
{
    private const double Half = 20037508.342789244;

    [Fact]
    public async Task Every_line_is_in_the_tile_at_a_level_where_each_is_smaller_than_a_pixel()
    {
        string root = await RequireServerAsync();
        string? service = Environment.GetEnvironmentVariable(MultiLayerServiceConformanceTests.ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(service),
            $"{MultiLayerServiceConformanceTests.ServiceVariable} is not set, so this test FAILS rather than skips. "
            + "Name the fixture's multi-layer service, e.g. hosted/ci_EarlyAlert, whose routes layer is lines.");

        (string layer, List<(double MinX, double MinY, double MaxX, double MaxY)> boxes) = await LinesAsync(service!);

        Assert.True(boxes.Count > 0, $"The line layer '{layer}' of {service} holds no feature to look for.");

        // The deepest level at which every line is smaller than a pixel both ways — where the old rule dropped all.
        double largest = boxes.Max(b => Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY));
        int z = (int)Math.Floor(Math.Log2(2 * Half / (512 * Math.Max(largest, 1))));

        Assert.True(z >= 0, $"A line of '{layer}' is wider than a pixel at level 0 ({largest:F0} m), so nothing here is small.");

        // Every tile holding a line's centre, fetched once; a line lies in its centre's tile.
        double size = 2 * Half / (1L << z);
        HashSet<(int X, int Y)> tiles = [.. boxes.Select(b => (
            (int)Math.Floor((((b.MinX + b.MaxX) / 2) + Half) / size),
            (int)Math.Floor((Half - ((b.MinY + b.MaxY) / 2)) / size)))];

        int found = 0;
        HashSet<string> carried = new(StringComparer.Ordinal);

        foreach ((int x, int y) in tiles)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                new Uri(string.Create(CultureInfo.InvariantCulture, $"{root}/rest/services/{service}/VectorTileServer/tile/{z}/{y}/{x}.pbf")));
            await AuthenticateAsync(request, root);

            using HttpResponseMessage response = await Http.SendAsync(request);
            byte[] tile = await response.Content.ReadAsByteArrayAsync();

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Tile {z}/{y}/{x} of {service} answered {(int)response.StatusCode}.");

            found += Features(tile, layer, out IReadOnlyList<string> names);
            carried.UnionWith(names);
        }

        // At least one each: a line near a tile's edge is also carried by its neighbour through the buffer.
        Assert.True(
            found >= boxes.Count,
            $"Level {z} held {found} of the {boxes.Count} lines of '{layer}', each smaller than a pixel there; the tiles "
            + $"carried the layers [{string.Join(", ", carried)}]. A line under a pixel is still drawn (ADR-085 §5.1, "
            + "amended 2026-09-29) — none of them is the dashed boundary the amendment removed. A visible range that "
            + $"excludes level {z} would also answer this, and would mean the fixture has changed.");
    }

    /// <summary>The service's line layer and each of its features' boxes, in Web Mercator.</summary>
    private async Task<(string Layer, List<(double MinX, double MinY, double MaxX, double MaxY)> Boxes)> LinesAsync(string service)
    {
        JsonElement document = await GetJsonAsync($"/rest/services/{service}/FeatureServer");

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            int id = entry.GetProperty("id").GetInt32();
            JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/{id}");

            if (!layer.TryGetProperty("geometryType", out JsonElement type) || type.GetString() != "esriGeometryPolyline")
            {
                continue;
            }

            JsonElement answer = await GetJsonAsync(
                $"/rest/services/{service}/FeatureServer/{id}/query?where=1%3D1&returnGeometry=true&outSR=3857");

            List<(double, double, double, double)> boxes = [];

            foreach (JsonElement feature in answer.GetProperty("features").EnumerateArray())
            {
                double[] xs = [.. Coordinates(feature, 0)];
                double[] ys = [.. Coordinates(feature, 1)];

                if (xs.Length > 0)
                {
                    boxes.Add((xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                }
            }

            return (layer.GetProperty("name").GetString()!, boxes);
        }

        Assert.Fail($"{service} has no polyline layer; the fixture's multi-layer service has one, its routes.");
        throw new InvalidOperationException();

        static IEnumerable<double> Coordinates(JsonElement feature, int axis) =>
            feature.GetProperty("geometry").GetProperty("paths").EnumerateArray()
                .SelectMany(path => path.EnumerateArray())
                .Select(point => point[axis].GetDouble());
    }

    // ---------- the MVT walk, written from the published specification ----------

    /// <summary>How many features the tile's layer of this name holds, and which layers it has.</summary>
    private static int Features(byte[] tile, string layerName, out IReadOnlyList<string> names)
    {
        List<string> seen = [];
        int count = 0;
        int i = 0;

        while (i < tile.Length)
        {
            (int field, int wire) = Tag(tile, ref i);

            if (field != 3 || wire != 2)
            {
                Skip(tile, ref i, wire);
                continue;
            }

            // The length first, then where it ends: `i + Varint(ref i)` reads i before the varint moves it.
            int layerLength = (int)Varint(tile, ref i);
            int end = i + layerLength;
            string name = string.Empty;
            int features = 0;

            while (i < end)
            {
                (int lf, int lw) = Tag(tile, ref i);

                if (lf == 1 && lw == 2)
                {
                    int length = (int)Varint(tile, ref i);
                    name = System.Text.Encoding.UTF8.GetString(tile, i, length);
                    i += length;
                }
                else if (lf == 2 && lw == 2)
                {
                    features++;
                    int featureLength = (int)Varint(tile, ref i);
                    i += featureLength;
                }
                else
                {
                    Skip(tile, ref i, lw);
                }
            }

            seen.Add(name);

            if (name == layerName)
            {
                count += features;
            }
        }

        names = seen;
        return count;
    }

    private static (int Field, int Wire) Tag(byte[] b, ref int i)
    {
        ulong key = Varint(b, ref i);
        return ((int)(key >> 3), (int)(key & 7));
    }

    private static ulong Varint(byte[] b, ref int i)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            byte next = b[i++];
            value |= (ulong)(next & 0x7F) << shift;

            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static void Skip(byte[] b, ref int i, int wire)
    {
        switch (wire)
        {
            case 0: Varint(b, ref i); break;
            case 1: i += 8; break;
            case 2: { int length = (int)Varint(b, ref i); i += length; break; }
            case 5: i += 4; break;
            default: throw new InvalidOperationException($"wire type {wire} is not in the MVT encoding");
        }
    }
}
