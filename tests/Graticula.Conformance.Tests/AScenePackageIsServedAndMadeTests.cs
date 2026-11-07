using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-269: a polygon layer's footprints are made into a scene layer package, kept as an item the caller owns and served
/// as a SceneServer, its I3S resources answered from the package.
/// </summary>
[Collection("catalogue walk")]
public sealed class AScenePackageIsServedAndMadeTests : ArcGisClient
{
    [Fact]
    public async Task A_polygon_layer_becomes_a_scene_layer_served_from_its_package()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        JsonElement layer = await GetJsonAsync("/rest/services/hosted/ci_buildings/FeatureServer/0/query?where=1%3D1&returnCountOnly=true");
        int count = layer.GetProperty("count").GetInt32();

        (HttpStatusCode made, string body) = await RequestAsync(HttpMethod.Post, $"{root}/content/scenes", token!,
            JsonSerializer.Serialize(new { service = "ci_buildings", folder = "hosted", layer = 0, heightField = "floors", heightScale = 3, title = "zz scene conformance" }));
        Assert.True(made == HttpStatusCode.Created, $"Making a scene layer answered {(int)made}: {body}");

        JsonElement answer = JsonDocument.Parse(body).RootElement;
        string id = answer.GetProperty("id").GetString()!;
        string service = answer.GetProperty("url").GetString()!;

        try
        {
            Assert.Equal(count, answer.GetProperty("buildings").GetInt32());

            // The item is the scene service, at its own address.
            JsonElement item = await GetJsonAsync($"/sharing/rest/content/items/{id}");
            Assert.Equal("Scene Service", item.GetProperty("type").GetString());
            Assert.EndsWith($"/rest/services/hosted/{id}/SceneServer", item.GetProperty("url").GetString(), StringComparison.Ordinal);

            JsonElement document = await AuthenticatedJsonAsync(service + "?f=json", token!);
            Assert.Equal("zz scene conformance", document.GetProperty("serviceName").GetString());
            Assert.Equal("1.7", document.GetProperty("serviceVersion").GetString());
            JsonElement layerDocument = document.GetProperty("layers")[0];
            Assert.Equal("3DObject", layerDocument.GetProperty("layerType").GetString());
            Assert.Equal("./layers/0", layerDocument.GetProperty("href").GetString());

            JsonElement nodes = (await AuthenticatedJsonAsync(service + "/layers/0/nodepages/0", token!)).GetProperty("nodes");
            JsonElement leaf = nodes[1];
            int vertices = leaf.GetProperty("mesh").GetProperty("geometry").GetProperty("vertexCount").GetInt32();
            int features = leaf.GetProperty("mesh").GetProperty("geometry").GetProperty("featureCount").GetInt32();
            int resource = leaf.GetProperty("mesh").GetProperty("geometry").GetProperty("resource").GetInt32();

            // A gzipped entry goes out as it is to a client that takes gzip, and its header agrees with its node.
            using HttpRequestMessage ask = new(HttpMethod.Get, $"{service}/layers/0/nodes/{resource}/geometries/0");
            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            ask.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using HttpResponseMessage geometry = await Http.SendAsync(ask);
            Assert.Equal(HttpStatusCode.OK, geometry.StatusCode);
            Assert.Contains("gzip", geometry.Content.Headers.ContentEncoding);
            byte[] stored = await geometry.Content.ReadAsByteArrayAsync();
            byte[] buffer = stored.Length > 1 && stored[0] == 0x1f && stored[1] == 0x8b ? Gunzip(stored) : stored;
            Assert.Equal((uint)vertices, BinaryPrimitives.ReadUInt32LittleEndian(buffer));
            Assert.Equal((uint)features, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)));

            // Private by default: a stranger is asked for a token, as at any service.
            (HttpStatusCode stranger, _) = await AnonymousAsync($"/rest/services/hosted/{id}/SceneServer/layers/0?f=json");
            Assert.Equal(HttpStatusCode.Unauthorized, stranger);

            (HttpStatusCode none, _) = await RequestAsync(HttpMethod.Get, $"{service}/layers/0/nodes/9999/geometries/0", token!, json: null);
            Assert.Equal(HttpStatusCode.NotFound, none);
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{id}", token!, json: null);
        }
    }

    /// <summary>D-386: a point layer becomes a point scene layer, its geometry Draco as I3S requires.</summary>
    [Fact]
    public async Task A_point_layer_becomes_a_point_scene_layer_of_draco_geometry()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        JsonElement layer = await GetJsonAsync("/rest/services/hosted/ci_EarlyAlert/FeatureServer/0/query?where=1%3D1&returnCountOnly=true");
        int count = layer.GetProperty("count").GetInt32();

        (HttpStatusCode refused, _) = await RequestAsync(HttpMethod.Post, $"{root}/content/scenes", token!,
            JsonSerializer.Serialize(new { service = "ci_EarlyAlert", folder = "hosted", layer = 0, baseField = "objectid" }));
        Assert.Equal(HttpStatusCode.BadRequest, refused);

        (HttpStatusCode made, string body) = await RequestAsync(HttpMethod.Post, $"{root}/content/scenes", token!,
            JsonSerializer.Serialize(new { service = "ci_EarlyAlert", folder = "hosted", layer = 0, title = "zz point scene conformance" }));
        Assert.True(made == HttpStatusCode.Created, $"Making a point scene layer answered {(int)made}: {body}");

        JsonElement answer = JsonDocument.Parse(body).RootElement;
        string id = answer.GetProperty("id").GetString()!;
        string service = answer.GetProperty("url").GetString()!;

        try
        {
            Assert.Equal(count, answer.GetProperty("features").GetInt32());
            Assert.Equal("points", answer.GetProperty("of").GetString());

            JsonElement layerDocument = await AuthenticatedJsonAsync(service + "/layers/0?f=json", token!);
            Assert.Equal("Point", layerDocument.GetProperty("layerType").GetString());

            JsonElement node = (await AuthenticatedJsonAsync(service + "/layers/0/nodepages/0", token!)).GetProperty("nodes")[0];
            Assert.Equal(count, node.GetProperty("mesh").GetProperty("geometry").GetProperty("featureCount").GetInt32());

            using HttpRequestMessage ask = new(HttpMethod.Get, $"{service}/layers/0/nodes/0/geometries/0");
            ask.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage geometry = await Http.SendAsync(ask);
            Assert.Equal(HttpStatusCode.OK, geometry.StatusCode);
            byte[] stored = await geometry.Content.ReadAsByteArrayAsync();
            byte[] draco = stored.Length > 1 && stored[0] == 0x1f && stored[1] == 0x8b ? Gunzip(stored) : stored;
            Assert.Equal("DRACO", System.Text.Encoding.ASCII.GetString(draco, 0, 5));
            Assert.Equal(0, draco[7]); // a point cloud
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{id}", token!, json: null);
        }
    }

    private async Task<JsonElement> AuthenticatedJsonAsync(string url, string token)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, url, token, json: null);
        Assert.True(status == HttpStatusCode.OK, $"{url} answered {(int)status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static byte[] Gunzip(byte[] stored)
    {
        using GZipStream gzip = new(new MemoryStream(stored), CompressionMode.Decompress);
        using MemoryStream plain = new();
        gzip.CopyTo(plain);
        return plain.ToArray();
    }
}
