using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-152 and ADR-153: a mosaic is a catalog — <c>query</c> lists its images, <c>mosaicRule</c> and <c>time</c> choose
/// which is on top, and ArcGIS's <c>update</c>, <c>delete</c>, <c>uploads</c> and <c>add</c> change it. Two images
/// written here lie on the same ground, one of 100 and one of 200, so which is drawn is read from a single pixel.
/// </summary>
[Collection("catalogue walk")]
public sealed class MosaicCatalogConformanceTests : ArcGisClient
{
    private const int Side = 8;
    private const string Inside = "30.025,40.975";

    private static byte[] Constant(float value)
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);
        float[] values = Enumerable.Repeat(value, Side * Side).ToArray();
        byte[] image = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, image, 0, image.Length);
        int imageAt = 8, scaleAt = imageAt + image.Length, tieAt = scaleAt + 24, geoAt = tieAt + 48, ifdAt = geoAt + 32;

        w.Write("II"u8.ToArray());
        w.Write((ushort)42);
        w.Write(ifdAt);
        w.Write(image);
        foreach (double d in new[] { 0.01, 0.01, 0.0 }) w.Write(d);
        foreach (double d in new[] { 0.0, 0.0, 0.0, 30.0, 41.0, 0.0 }) w.Write(d);
        foreach (ushort k in new ushort[] { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 }) w.Write(k);

        (ushort Tag, ushort Type, int Count, int Value)[] tags =
        [
            (256, 3, 1, Side), (257, 3, 1, Side), (258, 3, 1, 32), (259, 3, 1, 1), (262, 3, 1, 1), (273, 4, 1, imageAt),
            (277, 3, 1, 1), (278, 3, 1, Side), (279, 4, 1, image.Length), (284, 3, 1, 1), (339, 3, 1, 3),
            (33550, 12, 3, scaleAt), (33922, 12, 6, tieAt), (34735, 3, 16, geoAt),
        ];

        w.Write((ushort)tags.Length);
        foreach ((ushort tag, ushort type, int count, int v) in tags)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            if (type == 3 && count == 1)
            {
                w.Write((ushort)v);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(v);
            }
        }

        w.Write(0);
        return file.ToArray();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone());
    }

    private async Task<string> ValueAsync(string root, string token, string service, string extra = "")
    {
        (HttpStatusCode status, JsonElement body) = await SendAsync(root, token, HttpMethod.Get,
            $"{service}/identify?geometry={Inside}&geometryType=esriGeometryPoint&f=json{extra}");
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        return body.GetProperty("value").GetString()!;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new System.Collections.Generic.KeyValuePair<string, string>(f.Key, f.Value)));

    private static string Rule(string json) => "&mosaicRule=" + Uri.EscapeDataString(json);

    [Fact]
    public async Task A_mosaic_is_a_catalog_its_rule_chooses_what_is_on_top_and_ArcGIS_s_edits_change_it()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_cat_{Guid.NewGuid():N}"[..16];
        string service = $"/rest/services/hosted/{name}/ImageServer";

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(Constant(100)), "file", "hundred.tif");
            form.Add(new ByteArrayContent(Constant(200)), "file", "two-hundred.tif");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, JsonElement said) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, said.ToString());
        }

        try
        {
            // The catalog: two rows, named as they were sent, with footprints.
            (_, JsonElement rows) = await SendAsync(root, token!, HttpMethod.Get, $"{service}/query?where=1%3D1&outFields=*&f=json");
            JsonElement[] features = [.. rows.GetProperty("features").EnumerateArray()];
            Assert.Equal([1, 2], features.Select(f => f.GetProperty("attributes").GetProperty("OBJECTID").GetInt32()));
            Assert.Equal(["hundred.tif", "two-hundred.tif"], features.Select(f => f.GetProperty("attributes").GetProperty("Name").GetString()));
            Assert.Equal(5, features[0].GetProperty("geometry").GetProperty("rings")[0].GetArrayLength());

            (_, JsonElement named) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/query?where={Uri.EscapeDataString("Name = 'two-hundred.tif'")}&returnIdsOnly=true&f=json");
            Assert.Equal([2], named.GetProperty("objectIds").EnumerateArray().Select(i => i.GetInt32()));

            JsonElement info = (await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body;
            Assert.Contains("Catalog", info.GetProperty("capabilities").GetString(), StringComparison.Ordinal);
            Assert.Contains("Edit", info.GetProperty("capabilities").GetString(), StringComparison.Ordinal);
            Assert.Equal("esriImageServiceSourceTypeMosaicDataset", info.GetProperty("serviceSourceType").GetString());

            // The last added is on top by default; a rule chooses otherwise.
            Assert.Equal("200", await ValueAsync(root, token!, service));

            // ADR-158: raised, the first is on top; lowered again, it is not — and both keep their ids.
            (HttpStatusCode raised, JsonElement raisedBody) = await SendAsync(root, token!, HttpMethod.Post,
                $"/admin/coverages/{name}/images/1/move?folder=hosted",
                new StringContent("""{"direction":"up"}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.True(raised == HttpStatusCode.OK, raisedBody.ToString());
            Assert.Equal("100", await ValueAsync(root, token!, service));
            await SendAsync(root, token!, HttpMethod.Post, $"/admin/coverages/{name}/images/1/move?folder=hosted",
                new StringContent("""{"direction":"down"}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal("200", await ValueAsync(root, token!, service));
            Assert.Equal("100", await ValueAsync(root, token!, service, Rule("""{"mosaicMethod":"esriMosaicLockRaster","lockRasterIds":[1]}""")));
            Assert.Equal("100", await ValueAsync(root, token!, service, Rule("""{"mosaicMethod":"esriMosaicNone","mosaicOperation":"MT_FIRST"}""")));
            Assert.Equal("200", await ValueAsync(root, token!, service, Rule("""{"mosaicMethod":"esriMosaicNone","where":"OBJECTID = 2"}""")));
            Assert.Equal("NoData", await ValueAsync(root, token!, service, Rule("""{"mosaicMethod":"esriMosaicNone","fids":[9]}""")));

            // ADR-158: the two images' pixels combined — 100 and 200.
            Assert.Equal("100", await ValueAsync(root, token!, service, Rule("""{"mosaicOperation":"MT_MIN"}""")));
            Assert.Equal("200", await ValueAsync(root, token!, service, Rule("""{"mosaicOperation":"MT_MAX"}""")));
            Assert.Equal("150", await ValueAsync(root, token!, service, Rule("""{"mosaicOperation":"MT_MEAN"}""")));
            Assert.Equal("300", await ValueAsync(root, token!, service, Rule("""{"mosaicOperation":"MT_SUM"}""")));
            Assert.Equal("150", await ValueAsync(root, token!, service, Rule("""{"mosaicOperation":"MT_BLEND"}""")));

            // What it does not apply is refused in ArcGIS's way, an error document, naming what it does.
            (_, JsonElement refused) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry={Inside}&geometryType=esriGeometryPoint&f=json{Rule("""{"mosaicOperation":"MT_AVERAGE"}""")}");
            Assert.Contains("MT_MEAN", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

            // ADR-153: an image dated by ArcGIS's update gives the service time, and time chooses it.
            long june = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            (HttpStatusCode updated, JsonElement updateResult) = await SendAsync(root, token!, HttpMethod.Post, $"{service}/update",
                Form(("rasterId", "1"), ("attributes", $$"""{"AcquisitionDate":{{june}},"Name":"June"}"""), ("f", "json")));
            Assert.True(updated == HttpStatusCode.OK, updateResult.ToString());
            Assert.True(updateResult.GetProperty("updateResults")[0].GetProperty("success").GetBoolean());

            info = (await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body;
            Assert.Equal(june, info.GetProperty("timeInfo").GetProperty("timeExtent")[0].GetInt64());
            Assert.Equal("100", await ValueAsync(root, token!, service, $"&time={june + 86_400_000}"));
            Assert.Equal("NoData", await ValueAsync(root, token!, service, $"&time={june - 86_400_000}"));

            // ArcGIS's delete removes an image; the one left keeps its id.
            (HttpStatusCode deleted, JsonElement deleteResult) = await SendAsync(root, token!, HttpMethod.Post, $"{service}/delete",
                Form(("rasterIds", "2"), ("f", "json")));
            Assert.True(deleted == HttpStatusCode.OK, deleteResult.ToString());
            Assert.Equal("100", await ValueAsync(root, token!, service));
            (_, JsonElement left) = await SendAsync(root, token!, HttpMethod.Get, $"{service}/query?where=1%3D1&returnIdsOnly=true&f=json");
            Assert.Equal([1], left.GetProperty("objectIds").EnumerateArray().Select(i => i.GetInt32()));

            // The last one is not removed: deleting the service is how that is done.
            (HttpStatusCode last, _) = await SendAsync(root, token!, HttpMethod.Post, $"{service}/delete", Form(("rasterIds", "1"), ("f", "json")));
            Assert.Equal(HttpStatusCode.BadRequest, last);

            // ArcGIS's two steps to add one: uploads/upload, then add — and the id removed is not given again.
            string item;
            using (MultipartFormDataContent upload = new())
            {
                upload.Add(new ByteArrayContent(Constant(300)), "file", "three-hundred.tif");
                (HttpStatusCode uploaded, JsonElement said) = await SendAsync(root, token!, HttpMethod.Post, $"{service}/uploads/upload", upload);
                Assert.True(uploaded == HttpStatusCode.OK, said.ToString());
                item = said.GetProperty("item").GetProperty("itemID").GetString()!;
            }

            (HttpStatusCode added, JsonElement addResult) = await SendAsync(root, token!, HttpMethod.Post, $"{service}/add",
                Form(("itemIds", item), ("rasterType", "Raster Dataset"), ("f", "json")));
            Assert.True(added == HttpStatusCode.OK, addResult.ToString());
            Assert.Equal(3, addResult.GetProperty("addResults")[0].GetProperty("rasterId").GetInt32());
            Assert.Equal("300", await ValueAsync(root, token!, service));
        }
        finally
        {
            (HttpStatusCode removed, _) = await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, removed);
        }
    }
}
