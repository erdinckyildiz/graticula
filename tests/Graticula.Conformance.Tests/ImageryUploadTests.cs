using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-123 condition 2: a GeoTIFF uploaded from Studio is kept, published and drawn; what is not one is refused; and
/// removing the service removes the file it was uploaded with.
/// </summary>
[Collection("catalogue walk")]
public sealed class ImageryUploadTests : ArcGisClient
{
    private static readonly string[] Corners = ["xmin", "ymin", "xmax", "ymax"];

    private static byte[] Corpus(string name)
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);

        while (at is not null && at.GetFiles("*.sln").Length == 0)
        {
            at = at.Parent;
        }

        Assert.False(at is null, "This test could not find the repository root from its own path.");
        return File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", name));
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static MultipartFormDataContent Upload(string name, byte[] bytes, string fileName)
    {
        MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        form.Add(new StringContent(name), "name");
        return form;
    }

    [Fact]
    public async Task An_imagery_item_has_its_owners_description_tags_and_a_picture()
    {
        // ADR-126: an image service's portal item said nothing about itself — null description, the folder as its
        // only tag, no picture — and its owner could not change that, the description and tags writes refusing an
        // image service as not one they owned.
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_imgmeta_{Guid.NewGuid():N}"[..20];
        string? folder = null;

        try
        {
            using MultipartFormDataContent form = Upload(name, Corpus("rgb-byte-deflate.tif"), "ortho.tif");
            (HttpStatusCode made, string madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, $"Uploading answered {(int)made}: {madeBody}");

            (HttpStatusCode described, string describedBody) = await SendAsync(root, token!, HttpMethod.Put,
                $"/admin/services/{name}/description?folder=hosted",
                new StringContent("{\"description\":\"Orthophoto of the test square.\"}", Encoding.UTF8, "application/json"));
            Assert.True(described == HttpStatusCode.OK, $"Describing it answered {(int)described}: {describedBody}");
            (HttpStatusCode tagged, string taggedBody) = await SendAsync(root, token!, HttpMethod.Put,
                $"/admin/services/{name}/tags?folder=hosted",
                new StringContent("{\"tags\":[\"ortho\",\"2026\"]}", Encoding.UTF8, "application/json"));
            Assert.True(tagged == HttpStatusCode.OK, $"Tagging it answered {(int)tagged}: {taggedBody}");

            (_, string search) = await SendAsync(root, token!, HttpMethod.Get, $"/sharing/rest/search?q={name}&f=json");
            JsonElement item = JsonDocument.Parse(search).RootElement.GetProperty("results").EnumerateArray()
                .First(r => r.GetProperty("name").GetString() == name);
            Assert.Equal("Orthophoto of the test square.", item.GetProperty("description").GetString());
            string[] tags = [.. item.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? "")];
            Assert.Contains("ortho", tags);
            Assert.Contains("Hosted Service", item.GetProperty("typeKeywords").EnumerateArray().Select(t => t.GetString()));

            string thumbnail = item.GetProperty("thumbnail").GetString()!;
            using HttpRequestMessage picture = new(HttpMethod.Get,
                new Uri($"{root}/sharing/rest/content/items/{item.GetProperty("id").GetString()}/info/{thumbnail}"));
            picture.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage drawn = await Http.SendAsync(picture);
            Assert.True(drawn.IsSuccessStatusCode, $"The item's picture answered {(int)drawn.StatusCode}.");
            Assert.Equal("image/png", drawn.Content.Headers.ContentType?.MediaType);

            // ADR-129: it moves into its owner's folder as any item does — Move answered *no such item* for an image.
            (HttpStatusCode madeFolder, string folderBody) = await SendAsync(root, token!, HttpMethod.Post, "/content/folders",
                new StringContent($"{{\"title\":\"{name}\"}}", Encoding.UTF8, "application/json"));
            Assert.True(madeFolder is HttpStatusCode.OK or HttpStatusCode.Created, $"Making a folder answered {(int)madeFolder}: {folderBody}");
            folder = JsonDocument.Parse(folderBody).RootElement.GetProperty("id").GetString();
            (_, string moved) = await SendAsync(root, token!, HttpMethod.Post, "/content/move",
                new StringContent($"{{\"items\":[{{\"service\":\"hosted/{name}\"}}],\"to\":\"{folder}\"}}", Encoding.UTF8, "application/json"));
            Assert.True(JsonDocument.Parse(moved).RootElement.GetProperty("results")[0].GetProperty("success").GetBoolean(), moved);
            (_, string mine) = await SendAsync(root, token!, HttpMethod.Get, "/content/items");
            JsonElement listed = JsonDocument.Parse(mine).RootElement.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("name").GetString() == $"hosted/{name}");
            Assert.Equal(folder, listed.GetProperty("contentFolder").GetString());
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            if (folder is not null) await SendAsync(root, token!, HttpMethod.Delete, $"/content/folders/{folder}");
        }
    }

    [Fact]
    public async Task A_GeoTIFF_is_uploaded_published_and_drawn_and_goes_with_its_service()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_img_{Guid.NewGuid():N}"[..16];

        try
        {
            using MultipartFormDataContent first = Upload(name, Corpus("rgb-byte-deflate.tif"), "ortho.tif");
            (HttpStatusCode made, string madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", first);
            Assert.True(made == HttpStatusCode.Created, $"Uploading a GeoTIFF answered {(int)made}: {madeBody}");
            Assert.Equal($"hosted/{name}", JsonDocument.Parse(madeBody).RootElement.GetProperty("name").GetString());

            (HttpStatusCode described, string document) = await SendAsync(root, token!, HttpMethod.Get,
                $"/rest/services/hosted/{name}/ImageServer?f=json");
            Assert.True(described == HttpStatusCode.OK, $"The service document answered {(int)described}: {document}");
            JsonElement extent = JsonDocument.Parse(document).RootElement.GetProperty("extent");

            string box = string.Join(",", Corners
                .Select(k => extent.GetProperty(k).GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using HttpRequestMessage draw = new(HttpMethod.Get,
                new Uri($"{root}/rest/services/hosted/{name}/ImageServer/exportImage?bbox={box}&size=64,64&f=image"));
            draw.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage drawn = await Http.SendAsync(draw);
            Assert.Equal("image/png", drawn.Content.Headers.ContentType?.MediaType);

            // The same name again, and something that is not a GeoTIFF, are refused.
            using MultipartFormDataContent again = Upload(name, Corpus("rgb-byte-deflate.tif"), "ortho.tif");
            (HttpStatusCode twice, _) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", again);
            Assert.Equal(HttpStatusCode.Conflict, twice);

            using MultipartFormDataContent notTiff = Upload($"{name}x", Encoding.UTF8.GetBytes("not an image"), "ortho.tif");
            (HttpStatusCode refused, string refusedBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", notTiff);
            Assert.True(refused == HttpStatusCode.BadRequest, $"A file that is not a GeoTIFF answered {(int)refused}: {refusedBody}");

            // Removing the service removes the file it was uploaded with.
            (HttpStatusCode removed, string removedBody) = await SendAsync(root, token!, HttpMethod.Delete,
                $"/admin/coverages/{name}?folder=hosted");
            Assert.True(removed == HttpStatusCode.OK, $"Removing answered {(int)removed}: {removedBody}");
            Assert.True(JsonDocument.Parse(removedBody).RootElement.GetProperty("fileRemoved").GetBoolean(), removedBody);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
        }
    }
}
