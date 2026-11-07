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

    /// <summary>A stored zip with a gzipped layer document and a filler entry of <paramref name="size"/> bytes.</summary>
    private static byte[] Package(int size)
    {
        byte[] layer = Encoding.UTF8.GetBytes("""{"id":0,"layerType":"3DObject","name":"zz large","version":"zz","store":{"version":"1.8","extent":[29.0,41.0,29.01,41.01]}}""");
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
