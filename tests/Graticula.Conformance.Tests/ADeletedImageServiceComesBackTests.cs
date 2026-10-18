using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A deleted image service waits in the recycle bin with the image uploaded for it, and comes back drawing — D-306,
/// ADR-203.
/// </summary>
public sealed class ADeletedImageServiceComesBackTests : ArcGisClient
{
    [Fact]
    public async Task An_uploaded_image_service_is_restored_with_its_image()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_rbimg_{Guid.NewGuid():N}"[..16];
        string service = $"/rest/services/hosted/{name}/ImageServer";
        string image = Path.Combine(RepositoryRoot(), "tests", "Graticula.Raster.Tiff.Tests", "corpus", "rgb-byte-3857.tif");
        Assert.True(File.Exists(image), $"The corpus image is not at {image}.");

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(image)), "file", "rgb.tif");
            form.Add(new StringContent(name), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, $"{root}/admin/coverages/upload") { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage made = await Http.SendAsync(upload);
            Assert.True(made.IsSuccessStatusCode, $"uploading: {(int)made.StatusCode} {await made.Content.ReadAsStringAsync()}");
        }

        string? second = null;

        try
        {
            (int deleted, string deletedBody) = await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            Assert.True(deleted == 200, $"deleting: {deleted} {deletedBody}");
            string entry = JsonDocument.Parse(deletedBody).RootElement.GetProperty("recycled").GetString()!;
            // The image face answers a missing service in ArcGIS's envelope, its code inside a 200.
            (_, string gone) = await RequestAsync(HttpMethod.Get, $"{root}{service}?f=json", token!, null);
            Assert.Equal(404, JsonDocument.Parse(gone).RootElement.GetProperty("error").GetProperty("code").GetInt32());

            (int restored, string restoredBody) = await AdminAsync(HttpMethod.Post, $"/admin/recycle-bin/{entry}/restore");
            Assert.True(restored == 200, $"restoring: {restored} {restoredBody}");

            // Back, and drawing: the image the service reads came back to where it was.
            (HttpStatusCode drawn, string picture) = await RequestAsync(HttpMethod.Get,
                $"{root}{service}/exportImage?bbox=-20037508,-20037508,20037508,20037508&size=64,64&format=png&f=json", token!, null);
            Assert.True(drawn == HttpStatusCode.OK, $"drawing after the restore: {(int)drawn} {picture}");
            Assert.False(JsonDocument.Parse(picture).RootElement.TryGetProperty("error", out _), picture);
        }
        finally
        {
            (int again, string againBody) = await AdminAsync(HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");

            if (again == 200 && JsonDocument.Parse(againBody).RootElement.TryGetProperty("recycled", out JsonElement recycled))
            {
                second = recycled.GetString();
                await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{second}");
            }
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && !File.Exists(Path.Combine(at.FullName, "CLAUDE.md")))
        {
            at = at.Parent;
        }

        Assert.NotNull(at);
        return at!.FullName;
    }
}
