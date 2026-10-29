using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// D-267, owner decision 2026-10-29: a layer drawn into a tile denser than its range is measured for says so beside
/// its range, and the range is not changed.
/// </summary>
/// <remarks>
/// A designed layer has no range — nothing was measured when it was empty — and 10,001 points added near one place
/// put all of them into its level-0 tile, which is the zoomed-out map D-267 is about.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ALayerOutgrowingItsRangeIsSuggestedTests : ArcGisClient
{
    private const int Points = 10_001;

    [Fact]
    public async Task A_dense_tile_is_noted_kept_and_cleared_by_a_range_and_the_range_is_never_set_for_you()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_dense_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post,
            $"{root}/admin/hosted/define",
            token!,
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new[] { new { name = "label", type = "text", nullable = true } },
            }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string service = $"{parts[2]}/{parts[3]}";

        try
        {
            StringBuilder features = new("[");

            for (int i = 0; i < Points; i++)
            {
                features.Append(CultureInfo.InvariantCulture,
                    $"{(i == 0 ? "" : ",")}{{\"geometry\":{{\"x\":{3_200_000 + i % 100 * 10},\"y\":{5_000_000 + i / 100 * 10}}},\"attributes\":{{}}}}");
            }

            using (HttpRequestMessage add = new(HttpMethod.Post, $"{root}{feature}/addFeatures"))
            {
                add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                add.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["features"] = features.Append(']').ToString(),
                });

                using HttpResponseMessage added = await Http.SendAsync(add);
                string said = await added.Content.ReadAsStringAsync();
                Assert.True(
                    JsonDocument.Parse(said).RootElement.GetProperty("addResults").GetArrayLength() == Points,
                    $"The points were not added: {said[..Math.Min(400, said.Length)]}");
            }

            JsonElement before = await AdminLayerAsync(layer);
            Assert.Equal(JsonValueKind.Null, before.GetProperty("denseTile").ValueKind);

            using (HttpRequestMessage tile = new(HttpMethod.Get, $"{root}/rest/services/{service}/VectorTileServer/tile/0/0/0.pbf"))
            {
                tile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage drawn = await Http.SendAsync(tile);
                Assert.Equal(HttpStatusCode.OK, drawn.StatusCode);
            }

            JsonElement noted = await AdminLayerAsync(layer);
            JsonElement dense = noted.GetProperty("denseTile");
            Assert.True(dense.ValueKind == JsonValueKind.Object, $"No dense tile was noted: {noted}");
            Assert.Equal(Points, dense.GetProperty("features").GetInt32());
            Assert.Equal(0, dense.GetProperty("level").GetInt32());
            Assert.False(dense.GetProperty("kept").GetBoolean());

            // The suggestion is a suggestion: the range is what it was.
            Assert.Equal(0, noted.GetProperty("minScale").GetDouble());

            (HttpStatusCode keptStatus, string keptBody) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers/{layer}/visible-range/keep", token!, json: null);
            Assert.True(keptStatus == HttpStatusCode.OK, $"Keeping answered {(int)keptStatus}: {keptBody}");
            Assert.True(JsonDocument.Parse(keptBody).RootElement.GetProperty("kept").GetBoolean(), keptBody);
            Assert.True((await AdminLayerAsync(layer)).GetProperty("denseTile").GetProperty("kept").GetBoolean());

            (HttpStatusCode set, string setBody) = await RequestAsync(
                HttpMethod.Put, $"{root}/admin/layers/{layer}/visible-range", token!,
                JsonSerializer.Serialize(new { minScale = 5_000_000, maxScale = 0 }));
            Assert.True(set == HttpStatusCode.OK, $"Setting the range answered {(int)set}: {setBody}");
            Assert.Equal(JsonValueKind.Null, (await AdminLayerAsync(layer)).GetProperty("denseTile").ValueKind);
        }
        finally
        {
            (_, string dropped) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);

            if (JsonDocument.Parse(dropped).RootElement.TryGetProperty("recycled", out JsonElement recycled))
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/admin/recycle-bin/{recycled.GetString()}", token!, json: null);
            }
        }
    }

    [Fact]
    public async Task Keeping_a_range_nothing_suggested_says_there_was_nothing_to_answer()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_dense_none_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/hosted/define", token!,
            JsonSerializer.Serialize(new { name = layer, geometryType = "Point", fields = Array.Empty<object>() }));

        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string[] parts = JsonDocument.Parse(design).RootElement
            .GetProperty("services").GetProperty("feature").GetString()!.Split('/', StringSplitOptions.RemoveEmptyEntries);

        try
        {
            (HttpStatusCode status, string body) = await RequestAsync(
                HttpMethod.Post, $"{root}/admin/layers/{layer}/visible-range/keep", token!, json: null);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("kept").GetBoolean(), body);
        }
        finally
        {
            (_, string dropped) = await RequestAsync(
                HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);

            if (JsonDocument.Parse(dropped).RootElement.TryGetProperty("recycled", out JsonElement recycled))
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/admin/recycle-bin/{recycled.GetString()}", token!, json: null);
            }
        }
    }

    private async Task<JsonElement> AdminLayerAsync(string layer)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/layers");
        Assert.True(status == 200, $"/admin/layers answered {status}");

        return JsonDocument.Parse(body).RootElement.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == layer).Clone();
    }
}
