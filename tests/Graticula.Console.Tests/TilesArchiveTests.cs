using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-278: a 3D Tiles archive (.3tz) is kept as a document, listed as the 3D tiles service it serves, served by its
/// paths from <c>3DTilesServer/</c>, and drawn in a scene by the Maps SDK's 3D tiles layer. The archive is made here —
/// one 50 m box as glTF, placed by the tileset's transform — so nothing of anyone else's is in the test.
/// </summary>
public sealed class TilesArchiveTests : ConsoleTest
{
    private const double Longitude = 29.0, Latitude = 41.0;

    private static readonly double[] Colour = [0.8, 0.3, 0.2, 1.0];
    private static readonly int[] RootNodes = [0];

    [Fact]
    public async Task A_3d_tiles_archive_is_served_and_drawn_in_a_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(Archive()), "file", "zz-box.3tz");
                form.Add(new StringContent("zz console 3d tiles box"), "title");
                using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/content/documents")) { Content = form };
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage made = await Http.SendAsync(upload);
                string body = await made.Content.ReadAsStringAsync();
                Assert.True(made.IsSuccessStatusCode, $"Uploading the archive answered {(int)made.StatusCode}: {body}");
                id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();
            }

            (int status, string item) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(200, status);
            JsonElement described = JsonDocument.Parse(item).RootElement;
            Assert.Equal("3DTiles Service", described.GetProperty("type").GetString());
            string tileset = described.GetProperty("url").GetString()!;
            Assert.EndsWith($"/rest/services/hosted/{id}/3DTilesServer/tileset.json", tileset);

            // ADR-278 condition 4: the portal's bbox search finds the archive by its root region, and not elsewhere.
            (status, string near) = await AdminAsync(HttpMethod.Get, "/sharing/rest/search?q=type:%223DTiles%20Service%22&bbox=28.9,40.9,29.1,41.1&f=json");
            Assert.Equal(200, status);
            Assert.Contains(id!, near, StringComparison.Ordinal);
            (_, string far) = await AdminAsync(HttpMethod.Get, "/sharing/rest/search?q=type:%223DTiles%20Service%22&bbox=-10,-10,-9,-9&f=json");
            Assert.DoesNotContain(id!, far, StringComparison.Ordinal);

            (status, string read) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{id}/3DTilesServer/tileset.json");
            Assert.Equal(200, status);
            Assert.Equal("box.glb", JsonDocument.Parse(read).RootElement.GetProperty("root").GetProperty("content").GetProperty("uri").GetString());
            (status, _) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{id}/3DTilesServer/../tileset.json");
            Assert.NotEqual(200, status);

            await Browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new
            {
                source = "(() => { const get = HTMLCanvasElement.prototype.getContext; HTMLCanvasElement.prototype.getContext = function (type, attrs) {"
                    + " if (attrs && typeof attrs === 'object') attrs = { ...attrs, failIfMajorPerformanceCaveat: false };"
                    + " return get.call(this, type, attrs); }; })();",
            });
            await OpenAsync("/studio/scene.html", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The scene viewer did not open.");

            // From the add list, by its kind: the layer the SDK makes of it is its 3D tiles layer.
            await Browser.EvaluateAsync<bool>("(document.getElementById('addType').value = '3DTiles Service', document.getElementById('addType').dispatchEvent(new Event('change')), true)");
            await WaitForAsync($"!!document.querySelector('#addList [data-item=\"{id}\"]')", "The archive was not offered among 3D tiles layers.");
            await ClickAsync($"#addList [data-item=\"{id}\"]");
            await WaitForAsync("scState.scene.layers.length === 1 && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'",
                "The 3D tiles layer did not load.");
            Assert.Equal("integrated-mesh-3dtiles", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).type"));

            await Browser.EvaluateAsync<bool>($"(scState.view.goTo({{ center: [{Longitude}, {Latitude}], zoom: 17, tilt: 50 }}, {{ animate: false }}), true)");
            // The SDK fetches tile content from a worker, which the page's own resource timings do not list; that the box
            // was asked for is measured on the server (2026-11-07), and here the view is drawn and settles.
            await Browser.EvaluateAsync<bool>("(scState.view.whenLayerView(scState.scene.layers.getItemAt(0)).then(lv => { const wait = () => lv.updating ? setTimeout(wait, 200) : (window.__tilesView = 'ok'); wait(); }, e => window.__tilesView = e.message), true)");
            for (int waited = 0; waited < 60 && await Browser.EvaluateAsync<string>("String(window.__tilesView || '')") == string.Empty; waited++)
            {
                await Task.Delay(500);
            }

            Assert.Equal("ok", await Browser.EvaluateAsync<string>("String(window.__tilesView)"));

            using HttpRequestMessage askBox = new(HttpMethod.Get, new Uri($"{Root}/rest/services/hosted/{id}/3DTilesServer/box.glb"));
            askBox.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage box = await Http.SendAsync(askBox);
            Assert.Equal("model/gltf-binary", box.Content.Headers.ContentType?.MediaType);
            Assert.Equal(0x46546C67u, BitConverter.ToUInt32(await box.Content.ReadAsByteArrayAsync(), 0));

            if (Environment.GetEnvironmentVariable("GRATICULA_TEST_SHOTS") is { Length: > 0 } shots)
            {
                JsonElement shot = await Browser.CallAsync("Page.captureScreenshot", new { format = "png" });
                File.WriteAllBytes(Path.Combine(shots, "tiles.png"), Convert.FromBase64String(shot.GetProperty("data").GetString()!));
            }

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }

    /// <summary>A .3tz: tileset.json and box.glb, stored, as the 3D Tiles archive format keeps them.</summary>
    private static byte[] Archive()
    {
        using MemoryStream zip = new();
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "tileset.json", Encoding.UTF8.GetBytes(Tileset()));
            Write(archive, "box.glb", Box(50));
        }

        return zip.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using Stream entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        entry.Write(bytes);
    }

    /// <summary>A tileset of one tile, placed at the test's point by an east-north-up transform to earth-centred metres.</summary>
    private static string Tileset()
    {
        double l = Longitude * Math.PI / 180, p = Latitude * Math.PI / 180;
        const double A = 6378137.0, E2 = 6.69437999014e-3;
        double n = A / Math.Sqrt(1 - (E2 * Math.Sin(p) * Math.Sin(p)));
        double x = n * Math.Cos(p) * Math.Cos(l), y = n * Math.Cos(p) * Math.Sin(l), z = n * (1 - E2) * Math.Sin(p);
        double[] transform =
        [
            -Math.Sin(l), Math.Cos(l), 0, 0,
            -Math.Sin(p) * Math.Cos(l), -Math.Sin(p) * Math.Sin(l), Math.Cos(p), 0,
            Math.Cos(p) * Math.Cos(l), Math.Cos(p) * Math.Sin(l), Math.Sin(p), 0,
            x, y, z, 1,
        ];
        double d = 0.001;
        return JsonSerializer.Serialize(new
        {
            asset = new { version = "1.1" },
            geometricError = 100,
            root = new
            {
                boundingVolume = new { region = new[] { l - d, p - d, l + d, p + d, 0.0, 60.0 } },
                geometricError = 0,
                refine = "ADD",
                transform,
                content = new { uri = "box.glb" },
            },
        });
    }

    /// <summary>A glTF binary of a box with sides of <paramref name="size"/> metres, standing on its base (glTF is y-up).</summary>
    private static byte[] Box(float size)
    {
        float h = size / 2;
        float[][] corners = [[-h, 0, -h], [h, 0, -h], [h, size, -h], [-h, size, -h], [-h, 0, h], [h, 0, h], [h, size, h], [-h, size, h]];
        int[][] faces = [[0, 1, 2, 3], [5, 4, 7, 6], [4, 0, 3, 7], [1, 5, 6, 2], [3, 2, 6, 7], [4, 5, 1, 0]];
        float[] positions = [.. faces.SelectMany(f => new[] { f[0], f[1], f[2], f[0], f[2], f[3] }).SelectMany(i => corners[i])];
        byte[] bin = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, bin, 0, bin.Length);
        int count = positions.Length / 3;

        string json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            scene = 0,
            scenes = new[] { new { nodes = RootNodes } },
            nodes = new[] { new { mesh = 0 } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 }, material = 0 } } } },
            materials = new[] { new { pbrMetallicRoughness = new { baseColorFactor = Colour, metallicFactor = 0.0 } } },
            accessors = new[] { new { bufferView = 0, componentType = 5126, count, type = "VEC3",
                min = new[] { -h, 0, -h }, max = new[] { h, size, h } } },
            bufferViews = new[] { new { buffer = 0, byteLength = bin.Length } },
            buffers = new[] { new { byteLength = bin.Length } },
        });
        byte[] jsonChunk = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));

        using MemoryStream glb = new();
        using BinaryWriter writer = new(glb);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonChunk.Length + 8 + bin.Length));
        writer.Write((uint)jsonChunk.Length);
        writer.Write(0x4E4F534Au);
        writer.Write(jsonChunk);
        writer.Write((uint)bin.Length);
        writer.Write(0x004E4942u);
        writer.Write(bin);
        return glb.ToArray();
    }
}
