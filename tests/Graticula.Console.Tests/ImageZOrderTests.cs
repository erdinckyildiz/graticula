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
/// D-326, owner decision 2026-10-31: an image's ZOrder is set in Studio's Settings › Images, not only from ArcGIS Pro or
/// a script. A lower ZOrder is drawn on top and comes before the stack, so the list is redrawn in the order the images
/// are drawn, and Raise and Lower move an image only among those of its own ZOrder.
/// </summary>
/// <remarks>
/// The images are uploaded through the API; the save is let through to the server (<c>__letWritesTo</c>) so what it
/// kept is read back, and the test removes the service it made.
/// </remarks>
public sealed class ImageZOrderTests : ConsoleTest
{
    [Fact]
    public async Task An_images_ZOrder_is_set_in_Studio_and_the_list_follows_it()
    {
        (string token, _) = await SignInAsync();
        string name = $"zz_zorder_{Guid.NewGuid():N}"[..18];

        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        byte[] tiff = File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", "gray-byte-nodata.tif"));

        await SendAsync(token, $"{Root}/admin/coverages/upload", tiff, name);

        try
        {
            await SendAsync(token, $"{Root}/admin/coverages/{name}/images?folder=hosted", tiff, null);
            await SendAsync(token, $"{Root}/admin/coverages/{name}/images?folder=hosted", tiff, null);

            await OpenAsync($"/studio/#/service/hosted/{name}?tab=settings&section=images", token);
            await WaitForAsync("document.querySelectorAll('#imagesTable tr[data-image] .imageZ').length === 3",
                "Settings › Images offers no ZOrder for each image (D-326).");

            Assert.Contains("lower ZOrder is drawn on top",
                await Browser.EvaluateAsync<string>("document.getElementById('imagesZHint').textContent") ?? "", StringComparison.Ordinal);

            // The image in the middle of the stack given -1, which puts it on top and leaves the two it stood between
            // apart in the stack and side by side in the list (the ux review's case).
            string bottom = await Browser.EvaluateAsync<string>("[...document.querySelectorAll('#imagesTable tr[data-image]')][1].dataset.image") ?? "";
            await Browser.EvaluateAsync<bool>(
                $"(window.__letWritesTo = ['/admin/coverages/{name}/images/'], true)");
            await Browser.EvaluateAsync<bool>($$"""
                (() => {
                  const z = document.querySelector('#imagesTable tr[data-image="{{bottom}}"] .imageZ');
                  z.value = '-1'; z.dispatchEvent(new Event('input', { bubbles: true }));
                  return true;
                })()
                """);
            Assert.Equal("true", await Browser.EvaluateAsync<string>(
                $"document.querySelector('#imagesTable tr[data-image=\"{bottom}\"]').dataset.dirty"));
            await ClickAsync("#imagesSave");
            await WaitForAsync("(document.getElementById('imagesSays').textContent || '').includes('is now on top')",
                "Saving a ZOrder did not say it was saved and where the image went.");
            Assert.Equal(0, await Browser.EvaluateAsync<int>("document.querySelectorAll('#imagesTable tr[data-dirty=\"true\"]').length"));

            // Redrawn in drawing order: the image given -1 is first, on top, alone in its ZOrder; the other two share
            // theirs and may be swapped, though another image stands between them in the stack.
            await WaitForAsync($"document.querySelector('#imagesTable tr[data-image]').dataset.image === '{bottom}'",
                "The image given the lower ZOrder was not listed on top.");
            Assert.Equal("1", await Browser.EvaluateAsync<string>("document.querySelector('#imagesTable tr[data-image] .drawn').textContent"));
            Assert.Equal("-1", await Browser.EvaluateAsync<string>("document.querySelector('#imagesTable tr[data-image] .imageZ').value"));
            Assert.True(await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('#imagesTable tr[data-image]')[0].querySelectorAll('[data-image-move]')].every(b => b.disabled)"),
                "Raise or Lower is offered to an image alone in its ZOrder, where it would move nothing that is drawn.");
            Assert.Equal("up:dis,down:on|up:on,down:dis", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#imagesTable tr[data-image]')].slice(1).map(tr => "
                + "[...tr.querySelectorAll('[data-image-move]')].map(b => b.dataset.imageMove + ':' + (b.disabled ? 'dis' : 'on')).join()).join('|')"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelectorAll('#imagesTable tr[data-image]')[1].classList.contains('zbreak')"),
                "The second ZOrder group does not begin with a rule.");

            // Lowering the second listed image swaps it with the third, across the image of the other ZOrder.
            string second = await Browser.EvaluateAsync<string>("document.querySelectorAll('#imagesTable tr[data-image]')[1].dataset.image") ?? "";
            await ClickAsync($"#imagesTable tr[data-image=\"{second}\"] [data-image-move=down]");
            await WaitForAsync($"document.querySelectorAll('#imagesTable tr[data-image]')[2]?.dataset.image === '{second}'",
                "Lowering an image past one of another ZOrder did not swap it with the next of its own.");

            (int listed, string body) = await AdminAsync(HttpMethod.Get, $"/admin/coverages/{name}/images?folder=hosted");
            Assert.Equal(200, listed);
            JsonElement kept = JsonDocument.Parse(body).RootElement.GetProperty("images").EnumerateArray()
                .Single(i => i.GetProperty("id").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) == bottom);
            Assert.Equal(-1, kept.GetProperty("zOrder").GetInt32());

            // A ZOrder that is not a whole number is refused here, before anything is sent.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const z = [...document.querySelectorAll('#imagesTable tr[data-image] .imageZ')].at(-1);
                  z.value = '1.5'; z.dispatchEvent(new Event('input', { bubbles: true }));
                  return true;
                })()
                """);
            await ClickAsync("#imagesSave");
            await WaitForAsync("(document.getElementById('imagesSays').textContent || '').includes('whole number')",
                "A ZOrder of 1.5 was not refused as not a whole number.");
            Assert.Equal("true", await Browser.EvaluateAsync<string>(
                "[...document.querySelectorAll('#imagesTable tr[data-image] .imageZ')].at(-1).getAttribute('aria-invalid')"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }

    private static async Task SendAsync(string token, string url, byte[] tiff, string? name)
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(tiff), "file", "gray.tif");
        if (name is not null) form.Add(new StringContent(name), "name");
        using HttpRequestMessage upload = new(HttpMethod.Post, new Uri(url)) { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage made = await Http.SendAsync(upload);
        Assert.True(made.IsSuccessStatusCode, $"{url} answered {(int)made.StatusCode}: {await made.Content.ReadAsStringAsync()}");
    }
}
