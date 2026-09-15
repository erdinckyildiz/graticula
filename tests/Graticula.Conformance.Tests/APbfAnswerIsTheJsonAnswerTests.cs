using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Graticula.Testing;
using Xunit;
using Field = Graticula.Testing.PbfReader.Field;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>f=pbf</c> answers the same rows, columns and coordinates as <c>f=json</c> — ADR-073.
/// </summary>
/// <remarks>
/// Written 2026-09-15: <c>f=pbf</c> was refused and <c>supportedQueryFormats</c> said JSON, so the
/// Maps SDK fell back to JSON on every layer and drew large layers several times slower than it
/// draws the same data from ArcGIS. The decoder is <see cref="PbfReader"/>, written from the
/// published proto and not from the server.
/// </remarks>
[Collection("catalogue walk")]
public sealed class APbfAnswerIsTheJsonAnswerTests : ArcGisClient
{
    private async Task<(string Path, string Oid, int Srid)> LayerAsync()
    {
        string? name = Environment.GetEnvironmentVariable(AdvertisedCapabilityTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{AdvertisedCapabilityTests.LayerVariable} is not set, so this test FAILS rather than skips.");

        string path = $"/rest/services/{name}/FeatureServer/0";
        JsonElement document = await GetJsonAsync(path);

        Assert.Contains("PBF", document.GetProperty("supportedQueryFormats").GetString(), StringComparison.Ordinal);
        Assert.True(document.GetProperty("supportsCoordinatesQuantization").GetBoolean());

        int srid = (await GetJsonAsync($"{path}/query?where=1%3D1&resultRecordCount=1&returnGeometry=false"))
            .GetProperty("spatialReference").GetProperty("wkid").GetInt32();
        return (path, document.GetProperty("objectIdField").GetString()!, srid);
    }

    private async Task<(HttpStatusCode Status, string? MediaType, byte[] Body)> GetBytesAsync(string path)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_features_match_the_json_answer_row_for_row()
    {
        (string path, string oid, _) = await LayerAsync();
        string query = $"{path}/query?where=1%3D1&outFields=*&orderByFields={oid}&resultRecordCount=25&returnGeometry=true";

        JsonElement json = await GetJsonAsync(query);
        (HttpStatusCode status, string? media, byte[] body) = await GetBytesAsync(query + "&f=pbf");

        Assert.True(status == HttpStatusCode.OK, $"f=pbf answered {(int)status}: {System.Text.Encoding.UTF8.GetString(body)}");
        Assert.Equal("application/x-protobuf", media);

        IReadOnlyList<Field> result = PbfReader.QueryResult(body).One(1).Message;

        Assert.Equal(oid, result.One(1).Text);
        Assert.Equal(json.GetProperty("exceededTransferLimit").GetBoolean(), result.OptionalVarint(9) == 1);

        string[] jsonFields = [.. json.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
        string[] pbfFields = [.. result.All(13).Select(f => f.Message.One(1).Text)];
        Assert.Equal(jsonFields, pbfFields);

        JsonElement[] jsonFeatures = [.. json.GetProperty("features").EnumerateArray()];
        List<Field> pbfFeatures = result.All(15);
        Assert.Equal(jsonFeatures.Length, pbfFeatures.Count);
        Assert.NotEmpty(pbfFeatures);

        IReadOnlyList<Field> transform = result.One(12).Message;
        double tolerance = transform.One(2).Message.One(1).AsDouble;
        int oidIndex = Array.IndexOf(pbfFields, oid);

        for (int i = 0; i < pbfFeatures.Count; i++)
        {
            IReadOnlyList<Field> feature = pbfFeatures[i].Message;
            IReadOnlyList<Field> id = feature.All(1)[oidIndex].Message;

            Field idValue = id.Find(v => v.Number is 5 or 8)
                ?? throw new Xunit.Sdk.XunitException($"Feature {i}'s object id is not an unsigned or sint64 value.");

            Assert.Equal(
                jsonFeatures[i].GetProperty("attributes").GetProperty(oid).GetInt64(),
                idValue.Number == 5 ? (long)idValue.Varint : idValue.SInt);

            if (!jsonFeatures[i].TryGetProperty("geometry", out JsonElement geometry))
            {
                Assert.Null(feature.Find(f => f.Number == 2));
                continue;
            }

            List<List<(double X, double Y)>> parts = PbfReader.Parts(feature.One(2).Message, transform);
            List<List<(double X, double Y)>> expected = JsonParts(geometry);

            Assert.Equal(expected.Count, parts.Count);

            for (int p = 0; p < parts.Count; p++)
            {
                Assert.Equal(expected[p].Count, parts[p].Count);

                for (int v = 0; v < parts[p].Count; v++)
                {
                    Assert.True(
                        Math.Abs(expected[p][v].X - parts[p][v].X) <= tolerance
                        && Math.Abs(expected[p][v].Y - parts[p][v].Y) <= tolerance,
                        $"Feature {i}, part {p}, vertex {v}: json {expected[p][v]}, pbf {parts[p][v]}, grid {tolerance}.");
                }
            }
        }
    }

    [Fact]
    public async Task Counts_and_ids_match_and_a_bad_grid_is_refused()
    {
        (string path, string oid, int srid) = await LayerAsync();

        long count = (await GetJsonAsync($"{path}/query?where=1%3D1&returnCountOnly=true")).GetProperty("count").GetInt64();
        (_, _, byte[] counted) = await GetBytesAsync($"{path}/query?where=1%3D1&returnCountOnly=true&f=pbf");
        Assert.Equal((ulong)count, PbfReader.QueryResult(counted).One(2).Message.OptionalVarint(1));

        long[] ids = [.. (await GetJsonAsync($"{path}/query?where=1%3D1&returnIdsOnly=true"))
            .GetProperty("objectIds").EnumerateArray().Select(e => e.GetInt64())];
        (_, _, byte[] listed) = await GetBytesAsync($"{path}/query?where=1%3D1&returnIdsOnly=true&f=pbf");
        IReadOnlyList<Field> idsResult = PbfReader.QueryResult(listed).One(3).Message;
        Assert.Equal(oid, idsResult.One(1).Text);

        long[] decoded = idsResult.Find(f => f.Number == 3) is { } packed
            ? [.. PbfReader.Packed(packed.Bytes).Select(v => (long)v)]
            : [];
        Assert.Equal(ids.Order(), decoded.Order());

        string grid = Uri.EscapeDataString(
            $"{{\"mode\":\"view\",\"originPosition\":\"upperLeft\",\"tolerance\":1,\"extent\":{{\"xmin\":0,\"ymin\":0,\"xmax\":1,\"ymax\":1,\"spatialReference\":{{\"wkid\":{(srid == 4326 ? 3857 : 4326)}}}}}}}");
        (HttpStatusCode refused, _, byte[] why) = await GetBytesAsync(
            $"{path}/query?where=1%3D1&resultRecordCount=1&f=pbf&quantizationParameters={grid}");

        Assert.True(refused == HttpStatusCode.BadRequest, $"An extent in another reference answered {(int)refused}.");
        Assert.Contains("quantizationParameters", System.Text.Encoding.UTF8.GetString(why), StringComparison.Ordinal);
    }

    private static List<List<(double X, double Y)>> JsonParts(JsonElement geometry)
    {
        if (geometry.TryGetProperty("x", out JsonElement x))
        {
            return [[(x.GetDouble(), geometry.GetProperty("y").GetDouble())]];
        }

        string key = geometry.TryGetProperty("rings", out _) ? "rings"
            : geometry.TryGetProperty("paths", out _) ? "paths"
            : "points";

        if (key == "points")
        {
            return [[.. geometry.GetProperty(key).EnumerateArray().Select(Pair)]];
        }

        return [.. geometry.GetProperty(key).EnumerateArray().Select(part => part.EnumerateArray().Select(Pair).ToList())];

        static (double, double) Pair(JsonElement pair) => (pair[0].GetDouble(), pair[1].GetDouble());
    }
}
