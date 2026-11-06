using System;
using System.Collections.Generic;
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
/// ADR-281 condition 2: a 3D Tiles archive of Gaussian splats, as ArcGIS's tools write them, is kept, served and drawn
/// in a scene as the Maps SDK's splat layer. The splats are made here — a sphere of a few hundred — written in Niantic's
/// open SPZ format (version 3, gzip, MIT-licensed reference) inside a glb with <c>KHR_gaussian_splatting</c> and
/// <c>KHR_gaussian_splatting_compression_spz_2</c>, the only splats SDK 5.2 accepts (measured, ADR-281 §4). Nothing of
/// anyone else's is in the test.
/// </summary>
public sealed class GaussianSplatArchiveTests : ConsoleTest
{
    private const double Longitude = 29.0, Latitude = 41.0;
    private static readonly int[] RootNodes = [0];
    private static readonly string[] SplatExtensions = ["KHR_gaussian_splatting_compression_spz_2", "KHR_gaussian_splatting"];
    private static readonly string[] TilesExtensions = ["3DTILES_content_gltf"];

    [Fact]
    public async Task A_splat_archive_is_served_and_drawn_as_a_splat_layer()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;
        try
        {
            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(Archive()), "file", "zz-console-splats.3tz");
                using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/content/documents")) { Content = form };
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage made = await Http.SendAsync(upload);
                string body = await made.Content.ReadAsStringAsync();
                Assert.True(made.IsSuccessStatusCode, body);
                id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
            }

            await Browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new
            {
                source = "(() => { const get = HTMLCanvasElement.prototype.getContext; HTMLCanvasElement.prototype.getContext = function (type, attrs) {"
                    + " if (attrs && typeof attrs === 'object') attrs = { ...attrs, failIfMajorPerformanceCaveat: false };"
                    + " return get.call(this, type, attrs); }; })();",
            });
            await OpenAsync("/studio/scene.html", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The scene viewer did not open.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('addType').value = '3DTiles Service', document.getElementById('addType').dispatchEvent(new Event('change')), true)");
            await WaitForAsync($"!!document.querySelector('#addList [data-item=\"{id}\"]')", "The splat archive was not offered among 3D tiles layers.");
            await ClickAsync($"#addList [data-item=\"{id}\"]");
            await WaitForAsync("scState.scene.layers.length === 1 && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'",
                "The splat layer did not load: " + await Browser.EvaluateAsync<string>("document.getElementById('addSaid').textContent"));
            Assert.Equal("gaussian-splat", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).type"));

            await Browser.EvaluateAsync<bool>($"(scState.view.goTo({{ center: [{Longitude}, {Latitude}], zoom: 18, tilt: 55 }}, {{ animate: false }}), true)");
            await Browser.EvaluateAsync<bool>("(scState.view.whenLayerView(scState.scene.layers.getItemAt(0)).then(lv => { const wait = () => lv.updating ? setTimeout(wait, 200) : (window.__splatView = 'ok'); wait(); }, e => window.__splatView = e.message), true)");
            for (int waited = 0; waited < 90 && await Browser.EvaluateAsync<string>("String(window.__splatView || '')") == string.Empty; waited++)
            {
                await Task.Delay(500);
            }

            Assert.Equal("ok", await Browser.EvaluateAsync<string>("String(window.__splatView)"));
            if (Environment.GetEnvironmentVariable("GRATICULA_TEST_SHOTS") is { Length: > 0 } shots)
            {
                JsonElement shot = await Browser.CallAsync("Page.captureScreenshot", new { format = "png" });
                File.WriteAllBytes(Path.Combine(shots, "splats.png"), Convert.FromBase64String(shot.GetProperty("data").GetString()!));
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

    private static byte[] Archive()
    {
        using MemoryStream zip = new();
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in new[] { ("tileset.json", Encoding.UTF8.GetBytes(Tileset())), ("splats.glb", Splats(600)) })
            {
                using Stream entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                entry.Write(bytes);
            }
        }

        return zip.ToArray();
    }

    /// <summary>One tile at the test's point, placed by an east-north-up transform; the content declares SPZ splats.</summary>
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
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["asset"] = new { version = "1.1" },
            ["extensionsUsed"] = TilesExtensions,
            ["extensionsRequired"] = TilesExtensions,
            ["extensions"] = new Dictionary<string, object>
            {
                ["3DTILES_content_gltf"] = new { extensionsUsed = SplatExtensions, extensionsRequired = SplatExtensions },
            },
            ["geometricError"] = 100,
            ["root"] = new
            {
                boundingVolume = new { box = new double[] { 0, 0, 25, 30, 0, 0, 0, 30, 0, 0, 0, 30 } },
                geometricError = 0,
                refine = "REPLACE",
                transform,
                content = new { uri = "splats.glb" },
            },
        });
    }

    /// <summary>
    /// A glb of <paramref name="count"/> splats on a sphere 20 m across, 25 m up: its attributes declared as accessors
    /// with no data, and its data one SPZ stream (version 3: a 16-byte header, then positions as 24-bit fixed point,
    /// opacities, colours as the zeroth spherical harmonic, log scales and rotations as the smallest three, all gzipped).
    /// </summary>
    private static byte[] Splats(int count)
    {
        Random random = new(7);
        const int Fraction = 12;
        List<float[]> positions = [];
        using MemoryStream raw = new();
        using (BinaryWriter w = new(raw, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(0x5053474Eu); // "NGSP"
            w.Write(3u);          // version 3, as ArcGIS's splats are (measured), whatever the extension's name says
            w.Write((uint)count);
            w.Write((byte)0);     // spherical harmonics degree
            w.Write((byte)Fraction);
            w.Write((byte)1);     // flags: antialiased, as ArcGIS's are
            w.Write((byte)0);
            List<byte> alphas = [], colours = [], scales = [], rotations = [];
            for (int i = 0; i < count; i++)
            {
                double u = (random.NextDouble() * 2) - 1, th = random.NextDouble() * 2 * Math.PI, r = Math.Sqrt(1 - (u * u));
                float[] at = [(float)(20 * r * Math.Cos(th)), (float)(25 + (20 * u)), (float)(20 * r * Math.Sin(th))];
                positions.Add(at);
                foreach (float c in at)
                {
                    int fixedPoint = (int)Math.Round(c * (1 << Fraction));
                    w.Write((byte)(fixedPoint & 0xff));
                    w.Write((byte)((fixedPoint >> 8) & 0xff));
                    w.Write((byte)((fixedPoint >> 16) & 0xff));
                }

                alphas.Add(230);
                double[] rgb = [0.5 + (0.5 * u), 0.3 + (0.4 * r), 0.9 - (0.5 * u)];
                // A colour is stored as its zeroth harmonic, x = (c − 0.5) / 0.282095, packed as x × 0.15 × 255 + 127.5.
                colours.AddRange(rgb.Select(c => (byte)Math.Clamp(Math.Round((((c - 0.5) / 0.282095) * 0.15 * 255) + 127.5), 0, 255)));
                // A scale is stored as (ln s + 10) × 16: 0.9 m.
                scales.AddRange(Enumerable.Repeat((byte)Math.Clamp(Math.Round((Math.Log(0.9) + 10) * 16), 0, 255), 3));
                // The identity rotation, as version 3's "smallest three": the largest component's index (w, 3) in the top
                // two bits and the other three, here zero, in ten bits each — 3 << 30, little-endian.
                rotations.AddRange([0, 0, 0, 0xC0]);
            }

            w.Write(alphas.ToArray());
            w.Write(colours.ToArray());
            w.Write(scales.ToArray());
            w.Write(rotations.ToArray());
        }

        byte[] spz;
        using (MemoryStream packed = new())
        {
            using (GZipStream gzip = new(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(raw.ToArray());
            }

            spz = packed.ToArray();
        }

        float[] min = [.. Enumerable.Range(0, 3).Select(k => positions.Min(p => p[k]))];
        float[] max = [.. Enumerable.Range(0, 3).Select(k => positions.Max(p => p[k]))];
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0" },
            ["extensionsUsed"] = SplatExtensions,
            ["extensionsRequired"] = SplatExtensions,
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = RootNodes } },
            ["nodes"] = new[] { new { mesh = 0 } },
            ["meshes"] = new[] { new { primitives = new[] { new Dictionary<string, object>
            {
                ["attributes"] = new Dictionary<string, int>
                {
                    ["POSITION"] = 0, ["COLOR_0"] = 1, ["KHR_gaussian_splatting:SCALE"] = 2, ["KHR_gaussian_splatting:ROTATION"] = 3,
                },
                ["mode"] = 0,
                ["extensions"] = new Dictionary<string, object>
                {
                    ["KHR_gaussian_splatting"] = new Dictionary<string, object>
                    {
                        ["extensions"] = new Dictionary<string, object> { ["KHR_gaussian_splatting_compression_spz_2"] = new { bufferView = 0 } },
                    },
                },
            } } } },
            ["accessors"] = new object[]
            {
                new { componentType = 5126, count, type = "VEC3", min, max },
                new { componentType = 5121, count, type = "VEC4", normalized = true },
                new { componentType = 5126, count, type = "VEC3" },
                new { componentType = 5126, count, type = "VEC4" },
            },
            ["bufferViews"] = new[] { new { buffer = 0, byteOffset = 0, byteLength = spz.Length } },
            ["buffers"] = new[] { new { byteLength = spz.Length } },
        });

        byte[] jsonChunk = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        byte[] bin = new byte[(spz.Length + 3) / 4 * 4];
        spz.CopyTo(bin, 0);
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
