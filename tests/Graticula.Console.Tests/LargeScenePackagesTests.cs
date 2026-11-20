using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-274: a scene layer package above the document limit is streamed to the server directories and recorded by
/// reference; it is served from its file and downloads byte for byte. The package is made here: a layer document and
/// a filler entry that takes it past the default 64 MB limit, so nothing of anyone else's is in the test.
/// </summary>
public sealed class LargeScenePackagesTests : ConsoleTest
{
    [Fact]
    public async Task A_package_above_the_document_limit_is_kept_as_a_file_served_and_downloaded_whole()
    {
        (string token, _) = await SignInAsync();
        byte[] package = Package(65 * 1024 * 1024 + 4096);
        string? id = null;

        try
        {
            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(package), "file", "zz-large.slpk");
                form.Add(new StringContent("zz console large package"), "title");
                using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/content/documents")) { Content = form };
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage made = await Http.SendAsync(upload);
                string body = await made.Content.ReadAsStringAsync();
                Assert.True(made.IsSuccessStatusCode, $"Uploading a package above the document limit answered {(int)made.StatusCode}: {body}");
                JsonElement said = JsonDocument.Parse(body).RootElement;
                id = said.GetProperty("id").GetString();
                Assert.Equal(package.Length, said.GetProperty("size").GetInt64());
            }

            (int status, string service) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{id}/SceneServer?f=json");
            Assert.Equal(200, status);
            Assert.Contains("zz large", service, StringComparison.Ordinal);
            (status, _) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{id}/SceneServer/layers/0?f=json");
            Assert.Equal(200, status);

            using HttpRequestMessage download = new(HttpMethod.Get, new Uri($"{Root}/content/documents/{id}/data"));
            download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage got = await Http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(got.IsSuccessStatusCode, $"The download answered {(int)got.StatusCode}.");
            await using Stream stream = await got.Content.ReadAsStreamAsync();
            byte[] hash = await SHA256.HashDataAsync(stream);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), Convert.ToHexString(hash));
        }
        finally
        {
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }

    /// <summary>
    /// ADR-274 condition 3: Replace file takes a package above the document limit through the same streaming path as an
    /// upload, and a small package replacing a large one goes back into the store; the scene service and the download
    /// follow each time. The portal's item update, which Pro's overwrite sends, takes the same path.
    /// </summary>
    [Fact]
    public async Task Replace_file_takes_a_package_above_the_document_limit_and_back_and_the_portal_update_does_too()
    {
        (string token, _) = await SignInAsync();
        byte[] small = Package(4096, "zz small first");
        byte[] large = Package(65 * 1024 * 1024 + 8192, "zz large replaced");
        byte[] smallAgain = Package(8192, "zz small again");
        byte[] largeByPortal = Package(65 * 1024 * 1024 + 12288, "zz large by portal");
        string? id = null;

        try
        {
            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(small), "file", "zz-replace.slpk");
                form.Add(new StringContent("zz console replaced package"), "title");
                using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/content/documents")) { Content = form };
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage made = await Http.SendAsync(upload);
                string body = await made.Content.ReadAsStringAsync();
                Assert.True(made.IsSuccessStatusCode, $"Uploading a small package answered {(int)made.StatusCode}: {body}");
                id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();
            }

            await ReplaceAsync(token, id!, large, "zz-replace-large.slpk");
            await ServesAsync(token, id!, large, "zz large replaced");

            // Back into the store: the row stops naming the file (it named it and the update was refused before).
            await ReplaceAsync(token, id!, smallAgain, "zz-replace-small.slpk");
            await ServesAsync(token, id!, smallAgain, "zz small again");

            // The portal's update, as Pro's overwrite sends it.
            string user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "ci";
            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(largeByPortal), "file", "zz-replace-portal.slpk");
                form.Add(new StringContent("json"), "f");
                using HttpRequestMessage update = new(HttpMethod.Post, new Uri($"{Root}/sharing/rest/content/users/{user}/items/{id}/update")) { Content = form };
                update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage updated = await Http.SendAsync(update);
                string body = await updated.Content.ReadAsStringAsync();
                Assert.True(updated.IsSuccessStatusCode && body.Contains("\"success\":true", StringComparison.Ordinal),
                    $"The portal update with a package above the document limit answered {(int)updated.StatusCode}: {body}");
            }

            await ServesAsync(token, id!, largeByPortal, "zz large by portal");
        }
        finally
        {
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }

    private async Task ReplaceAsync(string token, string id, byte[] package, string fileName)
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(package), "file", fileName);
        using HttpRequestMessage replace = new(HttpMethod.Put, new Uri($"{Root}/content/documents/{id}/data")) { Content = form };
        replace.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage replaced = await Http.SendAsync(replace);
        string body = await replaced.Content.ReadAsStringAsync();
        Assert.True(replaced.IsSuccessStatusCode, $"Replacing the file with {package.Length:N0} bytes answered {(int)replaced.StatusCode}: {body}");
        Assert.Equal(package.Length, JsonDocument.Parse(body).RootElement.GetProperty("size").GetInt64());
    }

    private async Task ServesAsync(string token, string id, byte[] package, string name)
    {
        (int status, string service) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{id}/SceneServer/layers/0?f=json");
        Assert.Equal(200, status);
        Assert.Contains(name, service, StringComparison.Ordinal);

        using HttpRequestMessage download = new(HttpMethod.Get, new Uri($"{Root}/content/documents/{id}/data"));
        download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage got = await Http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(got.IsSuccessStatusCode, $"The download answered {(int)got.StatusCode}.");
        await using Stream stream = await got.Content.ReadAsStreamAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), Convert.ToHexString(await SHA256.HashDataAsync(stream)));
    }

    /// <summary>A stored zip with a gzipped layer document named <paramref name="name"/> and a filler entry of <paramref name="size"/> bytes.</summary>
    private static byte[] Package(int size, string name = "zz large")
    {
        byte[] layer = Encoding.UTF8.GetBytes(
            """{"id":0,"layerType":"3DObject","name":"NAME","version":"zz","store":{"version":"1.8","extent":[29.0,41.0,29.01,41.01]}}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        using MemoryStream gz = new();
        using (GZipStream zip = new(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(layer);
        }

        byte[] filler = new byte[size];
        new Random(274).NextBytes(filler);
        using MemoryStream archive = new();
        using (ZipArchive written = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream entry = written.CreateEntry("3dSceneLayer.json.gz", CompressionLevel.NoCompression).Open())
            {
                entry.Write(gz.ToArray());
            }

            using (Stream entry = written.CreateEntry("nodes/0/geometries/0.bin.gz", CompressionLevel.NoCompression).Open())
            {
                entry.Write(filler);
            }
        }

        return archive.ToArray();
    }
}
