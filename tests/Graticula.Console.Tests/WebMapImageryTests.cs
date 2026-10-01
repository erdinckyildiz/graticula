using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-123 condition 4: the Map Viewer adds an image service, draws it from <c>exportImage</c>, and a click on it
/// answers the pixel's value.
/// </summary>
/// <remarks>
/// <b>The image is uploaded through the API</b>, because this harness never sends a page's writes; what is asserted
/// is what the viewer does with an image service once one exists.
/// </remarks>
public sealed class WebMapImageryTests : ConsoleTest
{
    [Fact]
    public async Task An_image_service_is_added_to_a_map_drawn_and_its_pixel_identified()
    {
        (string token, _) = await SignInAsync();
        string name = $"zz_wmimg_{Guid.NewGuid():N}"[..16];

        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        byte[] tiff = File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "gray-float32-deflate.tif"));

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(tiff), "file", "f.tif");
            form.Add(new StringContent(name), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/admin/coverages/upload")) { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage made = await Http.SendAsync(upload);
            Assert.True(made.IsSuccessStatusCode, $"Uploading answered {(int)made.StatusCode}: {await made.Content.ReadAsStringAsync()}");
        }

        try
        {
            await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString($"hosted/{name}")}", token);
            await WaitForAsync(
                "typeof wmLayers === 'function' && !!wmState.doc && wmLayers().some(l => l.layerType === 'ArcGISImageServiceLayer' && (wmRuntime.get(l) || {}).status === 'ok')",
                "The Map Viewer did not add the image service as a layer it draws.");

            Assert.Contains("Imagery", await Browser.EvaluateAsync<string>("document.getElementById('layerList').innerText") ?? "",
                StringComparison.Ordinal);

            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#layerList [data-act=pixels][aria-pressed=true]')"),
                "An imagery layer offers no way to stop answering clicks.");

            // Drawn from exportImage, as a PNG so its no-data shows the map beneath.
            await WaitForAsync(
                "performance.getEntriesByType('resource').some(e => e.name.includes('/ImageServer/exportImage') && e.name.includes('FORMAT=png'))",
                "The image service was not asked for a picture.");

            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const layer = wmLayers().find(l => l.layerType === 'ArcGISImageServiceLayer');
                  wmIdentify(ol.extent.getCenter(wmExtentOf(layer)));
                  return true;
                })()
                """);
            await WaitForAsync("!document.getElementById('identify').hidden", "A click on the image answered nothing.");
            Assert.Contains("Pixel value", await Browser.EvaluateAsync<string>("document.getElementById('identify').innerText") ?? "",
                StringComparison.Ordinal);

            NothingWentWrong(await PageErrorsAsync());

            // Saved with the image in it, and opened again: the layer comes back as one this viewer draws, with its
            // pixel values off as they were left.
            string document = System.Text.Json.JsonSerializer.Serialize(new
            {
                title = $"zz imagery map {name}",
                sharing = "private",
                document = new
                {
                    operationalLayers = new object[]
                    {
                        new
                        {
                            id = "img", layerType = "ArcGISImageServiceLayer", title = "Saved imagery",
                            url = $"{Root}/rest/services/hosted/{name}/ImageServer", visibility = true, opacity = 0.8,
                            popupEnabled = false,
                        },
                    },
                    baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                    version = "2.31",
                },
            });
            (int saved, string savedBody) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);
            Assert.True(saved == 201, $"Saving a map with an image in it answered {saved}: {savedBody}");
            string map = System.Text.Json.JsonDocument.Parse(savedBody).RootElement.GetProperty("id").GetString()!;

            try
            {
                await OpenAsync($"/studio/webmap.html?id={map}", token);
                await WaitForAsync(
                    "!!wmState.doc && wmLayers().some(l => l.title === 'Saved imagery' && (wmRuntime.get(l) || {}).status === 'ok')",
                    "A saved map's imagery layer did not come back as one the viewer draws.");
                Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#layerList [data-act=pixels][aria-pressed=false]')"),
                    "The saved map's choice not to answer clicks with pixel values was lost.");
                NothingWentWrong(await PageErrorsAsync());
            }
            finally
            {
                await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}");
            }
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }
}
