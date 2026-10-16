using System;
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
/// A document is an item — ADR-188: filed through Studio's API, listed wherever a web map is listed, and always served
/// as a download.
/// </summary>
/// <remarks>
/// <b>Condition 1 is one walk</b>: the document is made once and looked for on every surface a web map appears on —
/// Studio's list, the portal's search, the member's content and a folder of it, the item, and its data — so a surface
/// that forgets the new kind fails here by name. <b>Condition 2</b> is the second test: what a browser could run is
/// refused at the door, and what is accepted leaves with the four headers.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ADocumentIsAnItemTests : ArcGisClient
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% a conformance document\n%%EOF\n");

    private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(
        string root, string? token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName, string? title = null, string? sharing = null)
    {
        MultipartFormDataContent form = new();
        ByteArrayContent file = new(bytes);

        // What the client says is ignored; a browser sends whatever the file's extension suggests.
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);

        if (title is not null) form.Add(new StringContent(title), "title");
        if (sharing is not null) form.Add(new StringContent(sharing), "sharing");

        return form;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static void AssertDownload(HttpResponseMessage response, string mediaType)
    {
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("sandbox", string.Join(";", response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_is_listed_wherever_a_web_map_is_and_downloads_as_a_file()
    {
        string root = await RequireServerAsync();
        string? admin = await TokenAsync(root);
        Assert.False(admin is null, "No administrator credential; set the suite's user and password.");

        string user = Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "root";
        string tag = Guid.NewGuid().ToString("N")[..8];
        string title = $"zz document {tag}";
        string? id = null;
        string? folder = null;

        try
        {
            (HttpStatusCode made, string madeBody, _) = await SendAsync(
                root, admin, HttpMethod.Post, "/content/documents", Upload(Pdf, $"plan-{tag}.pdf", title));
            Assert.True(made == HttpStatusCode.Created, $"Filing a PDF answered {(int)made}: {madeBody}");

            JsonElement document = JsonDocument.Parse(madeBody).RootElement;
            id = document.GetProperty("id").GetString()!;
            Assert.Equal("PDF", document.GetProperty("type").GetString());
            Assert.Equal(Pdf.Length, document.GetProperty("size").GetInt64());
            Assert.Equal("private", document.GetProperty("sharing").GetString());

            // Studio's own list.
            (_, string listed, _) = await SendAsync(root, admin, HttpMethod.Get, "/content/documents");
            Assert.Contains(JsonDocument.Parse(listed).RootElement.GetProperty("documents").EnumerateArray(),
                d => d.GetProperty("id").GetString() == id);

            // The file, from Studio.
            (HttpStatusCode got, _, HttpResponseMessage file) = await SendAsync(root, admin, HttpMethod.Get, $"/content/documents/{id}/data");
            Assert.Equal(HttpStatusCode.OK, got);
            AssertDownload(file, "application/pdf");
            Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());

            // The portal's search, by its ArcGIS type.
            (_, string found, _) = await SendAsync(root, admin, HttpMethod.Get,
                $"/sharing/rest/search?q={Uri.EscapeDataString($"type:PDF {tag}")}&num=100&f=json");
            JsonElement hit = JsonDocument.Parse(found).RootElement.GetProperty("results").EnumerateArray()
                .FirstOrDefault(r => r.GetProperty("id").GetString() == id);
            Assert.True(hit.ValueKind == JsonValueKind.Object, $"type:PDF does not find the document: {found}");
            Assert.Equal("PDF", hit.GetProperty("type").GetString());

            // The member's content, the item, and its data from the portal.
            (_, string mine, _) = await SendAsync(root, admin, HttpMethod.Get, $"/sharing/rest/content/users/{Uri.EscapeDataString(user)}?f=json");
            Assert.Contains(JsonDocument.Parse(mine).RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

            (_, string item, _) = await SendAsync(root, admin, HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(title, JsonDocument.Parse(item).RootElement.GetProperty("title").GetString());

            (HttpStatusCode data, _, HttpResponseMessage portalFile) = await SendAsync(root, admin, HttpMethod.Get, $"/sharing/rest/content/items/{id}/data");
            Assert.Equal(HttpStatusCode.OK, data);
            AssertDownload(portalFile, "application/pdf");
            Assert.Equal(Pdf, await portalFile.Content.ReadAsByteArrayAsync());

            // Private: an anonymous caller is told it does not exist, on both faces.
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(root, null, HttpMethod.Get, $"/content/documents/{id}/data")).Status);
            Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(root, null, HttpMethod.Get, $"/sharing/rest/content/items/{id}/data")).Status);

            // Filed in a folder, it is listed in the folder and no longer at the root.
            (HttpStatusCode foldered, string folderBody, _) = await SendAsync(root, admin, HttpMethod.Post, "/content/folders", Json(new { title = $"zz papers {tag}" }));
            Assert.True(foldered == HttpStatusCode.Created, $"Making a folder answered {(int)foldered}: {folderBody}");
            folder = JsonDocument.Parse(folderBody).RootElement.GetProperty("id").GetString();

            (HttpStatusCode moved, string moveBody, _) = await SendAsync(root, admin, HttpMethod.Post, "/content/move",
                Json(new { to = folder, items = new[] { new { document = id } } }));
            Assert.True(moved == HttpStatusCode.OK && moveBody.Contains("\"success\":true", StringComparison.Ordinal),
                $"Moving the document answered {(int)moved}: {moveBody}");

            string folderId = Guid.Parse(folder!).ToString("N");
            (_, string inFolder, _) = await SendAsync(root, admin, HttpMethod.Get, $"/sharing/rest/content/users/{Uri.EscapeDataString(user)}/{folderId}?f=json");
            Assert.Contains(JsonDocument.Parse(inFolder).RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

            // Made public, an anonymous caller downloads it.
            (HttpStatusCode published, string publishBody, _) = await SendAsync(root, admin, HttpMethod.Put, $"/content/documents/{id}", Json(new { sharing = "public" }));
            Assert.True(published == HttpStatusCode.OK, $"Making it public answered {(int)published}: {publishBody}");
            (HttpStatusCode open, _, HttpResponseMessage openFile) = await SendAsync(root, null, HttpMethod.Get, $"/sharing/rest/content/items/{id}/data");
            Assert.Equal(HttpStatusCode.OK, open);
            AssertDownload(openFile, "application/pdf");

            // Protected, it is not deleted; unprotected, it is.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(root, admin, HttpMethod.Put, $"/content/documents/{id}/protection", Json(new { @protected = true }))).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(root, admin, HttpMethod.Delete, $"/content/documents/{id}")).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(root, admin, HttpMethod.Put, $"/content/documents/{id}/protection", Json(new { @protected = false }))).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(root, admin, HttpMethod.Delete, $"/content/documents/{id}")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(root, admin, HttpMethod.Get, $"/content/documents/{id}")).Status);
            id = null;
        }
        finally
        {
            if (id is not null)
            {
                await SendAsync(root, admin, HttpMethod.Put, $"/content/documents/{id}/protection", Json(new { @protected = false }));
                await SendAsync(root, admin, HttpMethod.Delete, $"/content/documents/{id}");
            }

            if (folder is not null)
            {
                await SendAsync(root, admin, HttpMethod.Delete, $"/content/folders/{folder}");
            }
        }
    }

    [Theory]
    [InlineData("page.pdf", "<!doctype html><script>alert(document.cookie)</script>")]
    [InlineData("drawing.svg", "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>")]
    [InlineData("table.csv", "<html><body onload='alert(1)'></body></html>")]
    [InlineData("page.html", "<html></html>")]
    public async Task What_a_browser_could_run_is_refused_whatever_it_is_called(string fileName, string content)
    {
        string root = await RequireServerAsync();
        string? admin = await TokenAsync(root);
        Assert.False(admin is null, "No administrator credential; set the suite's user and password.");

        (HttpStatusCode status, string body, _) = await SendAsync(
            root, admin, HttpMethod.Post, "/content/documents", Upload(Encoding.UTF8.GetBytes(content), fileName, "zz refused"));

        Assert.True(status == HttpStatusCode.UnsupportedMediaType, $"'{fileName}' was answered {(int)status}: {body}");
        Assert.Contains("error", body, StringComparison.Ordinal);
    }
}
