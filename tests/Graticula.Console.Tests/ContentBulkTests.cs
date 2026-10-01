using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-129: My content ticks several of the reader's own items and shares, moves or deletes them together, as
/// Portal's Content page does — one request an item, through the same endpoints a single item's actions use.
/// </summary>
/// <remarks>
/// <b>The items are made through the API</b> — an uploaded image and a saved map, one of each kind a row can be — and
/// the harness answers the page's writes without sending them, so what is asserted is what the page asks for.
/// </remarks>
public sealed class ContentBulkTests : ConsoleTest
{
    [Fact]
    public async Task Ticked_items_are_shared_and_moved_together()
    {
        (string token, _) = await SignInAsync();
        string tag = Guid.NewGuid().ToString("N")[..8];
        string image = $"zz_bulk_{tag}";

        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        byte[] tiff = File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "rgb-byte-deflate.tif"));

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(tiff), "file", "b.tif");
            form.Add(new StringContent(image), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/admin/coverages/upload")) { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage made = await Http.SendAsync(upload);
            Assert.True(made.IsSuccessStatusCode, $"Uploading answered {(int)made.StatusCode}: {await made.Content.ReadAsStringAsync()}");
        }

        (int saved, string savedBody) = await AdminAsync(HttpMethod.Post, "/content/webmaps", JsonSerializer.Serialize(new
        {
            title = $"zz bulk map {tag}",
            sharing = "private",
            document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
        }));
        Assert.True(saved == 201, $"Saving a map answered {saved}: {savedBody}");
        string map = JsonDocument.Parse(savedBody).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync("/studio/#/content/mine", token);
            await WaitForAsync("!!document.getElementById('contentFilter')", "My content did not open.");
            await Browser.EvaluateAsync<bool>(
                $"(() => {{ const f = document.getElementById('contentFilter'); f.value = '{tag}'; f.dispatchEvent(new Event('input', {{ bubbles: true }})); return true; }})()");
            string[] keys = [$"service:hosted/{image}", $"webmap:{map}"];
            string tick = $"[...document.querySelectorAll('#contentRows [data-pick]')].filter(b => {JsonSerializer.Serialize(keys)}.includes(b.dataset.pick))";

            await WaitForAsync($"{tick}.length === 2", "My content offers no tick for the reader's own image and map.");
            await Browser.EvaluateAsync<bool>($"({tick}.forEach(b => {{ b.checked = true; b.dispatchEvent(new Event('change', {{ bubbles: true }})); }}), true)");
            await WaitForAsync("!document.getElementById('contentBulk').hidden && document.getElementById('contentBulk').textContent.includes('2 selected')",
                "Ticking two items did not show what can be done to them.");

            // Share both with the organization: one sharing request each, through each item's own endpoint.
            await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
            await ClickAsync("#bulkShare");
            await WaitForAsync("document.getElementById('contentFolderDialog').open", "Share did not open.");
            // Nothing is chosen for the reader: Share waits for a scope.
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('contentFolderGo').disabled"),
                "Bulk Share offered to apply a scope nobody chose.");
            await Browser.EvaluateAsync<bool>(
                "(() => { const s = document.getElementById('bulkScope'); s.value = 'organization'; s.dispatchEvent(new Event('change')); return true; })()");
            await ClickAsync("#contentFolderGo");
            await WaitForAsync(
                $"window.__writes.some(w => w.startsWith('PUT') && w.includes('/admin/services/{image}/sharing'))"
                + $" && window.__writes.some(w => w.startsWith('PUT') && w.includes('/content/webmaps/{map}'))",
                "Sharing the ticked items did not send a sharing change for each.");

            // Move both: one request naming both.
            await Browser.EvaluateAsync<bool>($"({tick}.forEach(b => {{ b.checked = true; b.dispatchEvent(new Event('change', {{ bubbles: true }})); }}), window.__writes = [], true)");
            await ClickAsync("#bulkMove");
            await WaitForAsync("document.getElementById('contentFolderDialog').open", "Move did not open.");
            await ClickAsync("#contentFolderGo");
            await WaitForAsync("window.__writes.some(w => w.startsWith('POST') && w.includes('/content/move'))",
                "Moving the ticked items sent nothing.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}");
            await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{image}?folder=hosted");
        }
    }
}
