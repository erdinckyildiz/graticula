using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-267: an item's page in Studio — a document's and a web map's — says whether it has metadata, downloads it, lets
/// whoever manages the item upload or replace it, and lists the item's comments with an add box and Delete, the text
/// shown as text. Organization › Settings › General turns comments off. Every write the page makes is trapped by the
/// harness and asserted by what it sent.
/// </summary>
public sealed class ItemMetadataCommentsScreenTests : ConsoleTest
{
    private static string User => Environment.GetEnvironmentVariable("GRATICULA_TEST_USER") ?? "ci";

    private async Task<JsonElement> PortalAsync(string path, HttpContent content)
    {
        (string token, _) = await SignInAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("f", "json")));

    private async Task<string> AddMapAsync(string title) =>
        (await PortalAsync($"/sharing/rest/content/users/{User}/addItem",
            Form(("type", "Web Map"), ("title", title), ("text", """{"operationalLayers":[],"version":"2.31"}"""))))
        .GetProperty("id").GetString()!;

    private async Task<string> AddDocumentAsync(string title)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "zz-notes.pdf");
        form.Add(new StringContent(title), "title");
        (string token, _) = await SignInAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/content/documents"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = form;
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!).ToString("N");
    }

    private async Task DeleteAsync(string id) =>
        await PortalAsync($"/sharing/rest/content/users/{User}/items/{id}/delete", Form(("permanentDelete", "true")));

    /// <summary>Chooses a file for the panel's hidden input, as the browser's picker would.</summary>
    private async Task ChooseMetadataAsync(string panel) => await Browser.EvaluateAsync<bool>($$"""
        (() => {
          const file = new File(['<metadata><dataIdInfo/></metadata>'], "exported.xml", { type: "text/xml" });
          const carried = new DataTransfer(); carried.items.add(file);
          const input = document.querySelector('#{{panel}} [data-imc-file]'); input.files = carried.files;
          input.dispatchEvent(new Event('change', { bubbles: true }));
          return true;
        })()
        """);

    [Fact]
    public async Task A_documents_page_uploads_metadata_and_shows_adds_and_deletes_comments_as_text()
    {
        string id = await AddDocumentAsync($"zz_imc_{Guid.NewGuid():N}"[..20]);

        try
        {
            // A comment whose text is markup, left through the API: the page must show it, not run it.
            JsonElement left = await PortalAsync($"/sharing/rest/content/items/{id}/addComment",
                Form(("comment", "<img src=x onerror=\"window.__pwned=1\"> looks right")));
            string comment = left.GetProperty("commentId").GetString()!;

            (string token, _) = await SignInAsync();
            await OpenAsync($"/studio/#/item/{id}", token);
            await WaitForAsync("(document.getElementById('docImc')?.textContent || '').includes('looks right')",
                "The document's page lists no comments (ADR-267).");

            Assert.True(await Browser.EvaluateAsync<bool>("!document.querySelector('#docImc img') && !window.__pwned"),
                "A comment's markup was drawn as markup.");
            Assert.Contains("<img src=x", await Browser.EvaluateAsync<string>("document.querySelector('#docImc .imctext').textContent")
                ?? string.Empty, StringComparison.Ordinal);

            // No metadata yet: the owner is offered Upload, and no Download.
            await WaitForAsync(Shown("#docImc [data-imc-upload]"), "The owner is not offered Upload metadata.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#docImc [data-imc-download]')"),
                "Download metadata is offered for an item with none.");

            await ChooseMetadataAsync("docImc");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('POST /sharing/rest/content/users/{User}/items/{id}/update [f,metadata]'))",
                "Choosing a file did not send it as the item's metadata.");

            // Add sends the text to addComment; an empty box sends nothing and says why.
            await ClickAsync("#docImc [data-imc-add]");
            await WaitForAsync("document.querySelector('#docImc [data-imc-text]')?.getAttribute('aria-invalid') === 'true'",
                "An empty comment was not refused on the page.");
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/addComment", StringComparison.Ordinal));

            await Browser.EvaluateAsync<bool>("(document.querySelector('#docImc [data-imc-text]').value = 'Checked against the survey.', true)");
            await ClickAsync("#docImc [data-imc-add]");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('POST /sharing/rest/content/items/{id}/addComment'))",
                "Add comment did not send the comment.");

            await WaitForAsync(Shown($"#docImc [data-imc-delete=\"{comment}\"]"), "The owner is not offered Delete on a comment.");
            await ClickAsync($"#docImc [data-imc-delete=\"{comment}\"]");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('POST /sharing/rest/content/items/{id}/comments/{comment}/delete'))",
                "Delete did not delete the comment.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task A_web_maps_page_says_it_has_metadata_and_downloads_it()
    {
        string id = await AddMapAsync($"zz_imcmap_{Guid.NewGuid():N}"[..22]);

        try
        {
            using MultipartFormDataContent form = new() { { new StringContent("json"), "f" } };
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<metadata><dataIdInfo><idPurp>x</idPurp></dataIdInfo></metadata>")),
                "metadata", "metadata.xml");
            await PortalAsync($"/sharing/rest/content/users/{User}/items/{id}/update", form);

            (string token, _) = await SignInAsync();
            await OpenAsync($"/studio/#/item/{id}", token);
            await WaitForAsync(Shown("#mapImc [data-imc-download]"), "A map with metadata offers no Download metadata.");
            Assert.Contains("Replace metadata", await Browser.EvaluateAsync<string>("document.getElementById('mapImc').textContent")
                ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("No comments yet", await Browser.EvaluateAsync<string>("document.getElementById('mapImc').textContent")
                ?? string.Empty, StringComparison.Ordinal);

            await ClickAsync("#mapImc [data-imc-download]");
            await WaitForAsync("(document.querySelector('#mapImc [data-imc-meta-says]')?.textContent || '').includes('metadata.xml')",
                "Download metadata said nothing on the page.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task Organization_settings_turn_comments_off()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/general", token);
        await WaitForAsync(Shown("#orgComments"), "General offers an administrator no Comments on items (ADR-267).");
        await WaitForAsync("document.getElementById('orgComments').checked === true", "Comments do not read as on.");

        await ClickAsync("#orgComments");
        await WaitForAsync("window.__writes.some(w => w.startsWith('PUT /admin/settings/item-comments'))",
            "Turning comments off sent nothing.");
        NothingWentWrong(await PageErrorsAsync());
    }
}
