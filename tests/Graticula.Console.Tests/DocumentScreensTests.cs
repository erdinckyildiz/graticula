using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio's documents — ADR-188 condition 4: a file a member uploaded is listed in Content, has its own item page, and
/// is uploaded, replaced and deleted from Studio (the brief, docs/design/document-items.md).
/// </summary>
/// <remarks>
/// A real document is filed through the API so the list and the page read the server; every write the page makes is
/// trapped by the harness and asserted by what it sent. The document is deleted and purged afterwards.
/// </remarks>
public sealed class DocumentScreensTests : ConsoleTest
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");

    /// <summary>A 1×1 PNG, real enough for the server to read its first bytes as a picture.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private async Task<string> FileDocumentAsync(string title, byte[]? bytes = null, string fileName = "zz-plan.pdf", string type = "application/pdf")
    {
        (string token, _) = await SignInAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/content/documents"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(bytes ?? Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(title), "title");
        form.Add(new StringContent("A plan, filed by a test."), "snippet");
        request.Content = form;

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Filing a document returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Deletes it and empties its place in the recycle bin, so the fixture is left as it was.</summary>
    private async Task RemoveAsync(string id)
    {
        await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        (_, string bin) = await AdminAsync(HttpMethod.Get, "/admin/recycle-bin");
        foreach (JsonElement entry in JsonDocument.Parse(bin).RootElement.GetProperty("entries").EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("item").GetString(), Guid.Parse(id).ToString("N"), StringComparison.OrdinalIgnoreCase))
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetProperty("id").GetString()}");
            }
        }
    }

    [Fact]
    public async Task A_document_is_listed_and_its_page_offers_download_replace_share_and_delete()
    {
        string title = $"zz_doc_{Guid.NewGuid():N}"[..20];
        string id = await FileDocumentAsync(title);

        try
        {
            (string token, _) = await SignInAsync();
            await OpenAsync("/studio/#/content", token);
            await FilterAsync("contentFilter", title);

            // Listed beside services and maps, with its kind's tile and the kind in words.
            await WaitForAsync($"(document.getElementById('contentRows')?.textContent || '').includes('{title}')",
                "Content does not list the document (ADR-188 condition 4).");
            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#contentRows .doctile[data-kind=\"pdf\"]')"),
                "The document's row has no tile saying it is a PDF.");
            Assert.Contains("PDF", await Browser.EvaluateAsync<string>("document.getElementById('contentRows').innerText") ?? string.Empty,
                StringComparison.Ordinal);

            // Its page, from the row's link.
            await Browser.EvaluateAsync<bool>("(document.querySelector('#contentRows td.name a').click(), true)");
            await WaitForAsync($"(document.getElementById('docTitle')?.textContent || '').includes('{title}')",
                "The row did not open the document's page.");
            foreach (string control in new[] { "docDownload", "docReplace", "docShareOpen" })
            {
                Assert.True(await Browser.EvaluateAsync<bool>($"!!document.getElementById('{control}') && document.getElementById('{control}').offsetParent !== null"),
                    $"The owner's page has no visible {control}.");
            }
            Assert.Contains("zz-plan.pdf", await Browser.EvaluateAsync<string>("document.getElementById('docSide').textContent") ?? string.Empty,
                StringComparison.Ordinal);

            // Download asks first and lets the browser take the file; the page says which file and how large.
            await ClickAsync("#docDownload");
            await WaitForAsync("(document.getElementById('docSays')?.textContent || '').includes('zz-plan.pdf')",
                "Download said nothing on the page.");

            // D-321: the item id as ArcGIS clients name it, a favourite, and terms of use written to the item.
            string bare = Guid.Parse(id).ToString("N");
            Assert.Contains(bare, (await Browser.EvaluateAsync<string>("document.getElementById('docSide').textContent") ?? string.Empty)
                .Replace("​", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            await WaitForAsync(Shown("#docFavorite"), "The document's page offers no Add favorite.");
            await ClickAsync("#docFavorite");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('PUT /content/items/{id}/favorite'))",
                "Add favorite did not mark the document.");

            await ClickAsync("#docEditOpen");
            await WaitForAsync(Shown("#docEditTerms"), "Editing a document offers no terms of use.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('docEditTerms').value = 'CC BY 4.0', true)");
            await ClickAsync("#docEditSave");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('PUT /content/items/{id}/license'))",
                "Terms of use were not written to the document's item.");
            await WaitForAsync(Shown("#docEditOpen"), "The page did not come back from editing.");

            // Editing refuses an empty title by name and keeps the reader in the field — counted from here, past the save above.
            await Browser.EvaluateAsync<bool>("(window.__writes.length = 0, true)");
            await ClickAsync("#docEditOpen");
            await Browser.EvaluateAsync<bool>("(document.getElementById('docEditTitle').value = '', true)");
            await ClickAsync("#docEditSave");
            await WaitForAsync("document.getElementById('docEditTitle').getAttribute('aria-invalid') === 'true'",
                "An empty title was not refused.");
            Assert.DoesNotContain(await WritesAsync(), w => w.StartsWith("PUT /content/documents/", StringComparison.Ordinal));
            await ClickAsync("#docEditCancel");

            // Replace sends the chosen file to the document's data, keeping the item.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const file = new File([new Uint8Array([37, 80, 68, 70, 45, 49, 46, 52, 10])], "zz-plan-2.pdf", { type: "application/pdf" });
                  const carried = new DataTransfer(); carried.items.add(file);
                  const input = document.getElementById('docReplaceFile'); input.files = carried.files;
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                })()
                """);
            await WaitForAsync($"window.__writes.some(w => w.startsWith('PUT /content/documents/{id}/data [file]'))",
                "Replace file did not send the file to the document's data.");

            // Settings: Delete asks first, then deletes this document.
            await Browser.EvaluateAsync<bool>("(document.querySelector('[data-doc-tab=\"settings\"]').click(), true)");
            await WaitForAsync(Shown("#docDelete"), "Settings has no Delete.");
            await ClickAsync("#docDelete");
            await WaitForAsync("document.getElementById('serverConfirm')?.open === true", "Delete did not ask first.");
            await ClickAsync("#serverConfirmGo");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('DELETE /content/documents/{id}'))",
                "Delete did not delete the document.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await RemoveAsync(id);
        }
    }

    /// <summary>D-321: a picture is its own thumbnail on its page, fetched with the reader's credential.</summary>
    [Fact]
    public async Task A_picture_document_shows_itself_as_its_thumbnail()
    {
        string title = $"zz_pic_{Guid.NewGuid():N}"[..20];
        string id = await FileDocumentAsync(title, Png, "zz-dot.png", "image/png");

        try
        {
            (string token, _) = await SignInAsync();
            await OpenAsync($"/studio/#/item/{id}", token);
            await WaitForAsync($"(document.getElementById('docTitle')?.textContent || '').includes('{title}')",
                "The picture's page did not open.");
            await WaitForAsync("(document.querySelector('#docThumb img')?.getAttribute('src') || '').startsWith('blob:')",
                "The picture is not shown as its own thumbnail.");
            await WaitForAsync("document.querySelector('#docThumb img')?.naturalWidth === 1",
                "The thumbnail is not the picture that was filed.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await RemoveAsync(id);
        }
    }

    /// <summary>
    /// D-321: a document in a folder names the folder in its breadcrumb, and its owner sets, shows and removes a thumbnail
    /// of its own from the page's More menu.
    /// </summary>
    [Fact]
    public async Task A_document_names_its_folder_and_keeps_a_thumbnail_its_owner_sets()
    {
        string title = $"zz_thumb_{Guid.NewGuid():N}"[..20];
        string folderTitle = $"zz folder {Guid.NewGuid():N}"[..18];
        string id = await FileDocumentAsync(title);
        string? folder = null;

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/content/folders", JsonSerializer.Serialize(new { title = folderTitle }));
            Assert.True(made == 201, $"Making the folder answered {made}: {madeBody}");
            folder = JsonDocument.Parse(madeBody).RootElement.GetProperty("id").GetString();
            (int moved, string movedBody) = await AdminAsync(HttpMethod.Post, "/content/move",
                JsonSerializer.Serialize(new { items = new[] { new { document = id } }, to = folder }));
            Assert.True(moved == 200, $"Moving answered {moved}: {movedBody}");

            // Opened from its own address, not through Content, so the folders have not been read yet.
            (string token, _) = await SignInAsync();
            await OpenAsync($"/studio/#/item/{id}", token);
            await WaitForAsync($"(document.getElementById('docTitle')?.textContent || '').includes('{title}')",
                "The document's page did not open.");
            await WaitForAsync($"document.querySelector('#docCrumb a[data-crumb-folder=\"{folder}\"]')?.textContent === '{folderTitle}'",
                "The breadcrumb does not name the folder the document is in (D-321).");
            Assert.EndsWith($"/studio/content?folder={folder}",
                await Browser.EvaluateAsync<string>($"document.querySelector('#docCrumb a[data-crumb-folder=\"{folder}\"]').href") ?? string.Empty,
                StringComparison.Ordinal);

            // Cancelling Move gives the focus back to More rather than to the page (design review 2026-10-31).
            await ClickAsync("#docActions details.itemmore summary");
            await ClickAsync("#docActions [data-move-document]");
            await WaitForAsync("document.getElementById('contentFolderDialog')?.open === true", "Move to folder… did not open its dialog.");
            await ClickAsync("#contentFolderCancel");
            await WaitForAsync("document.activeElement?.matches('#docActions details.itemmore summary') === true",
                "Cancelling Move left the focus on the page instead of on More.");

            // No picture of its own yet: More offers to set one, and not to remove one.
            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#docThumb .doctile')"),
                "A PDF with no thumbnail of its own does not show its kind's tile.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "document.getElementById('docThumbChange')?.textContent.trim() === 'Set thumbnail…' && !document.getElementById('docThumbRemove')"),
                "More does not offer Set thumbnail… alone for a document with none.");

            // A file that is not a picture is refused before it is sent; a picture is sent as the file part.
            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const carried = new DataTransfer(); carried.items.add(new File(["%PDF-"], "zz.pdf", { type: "application/pdf" }));
                  const input = document.getElementById('docThumbFile'); input.files = carried.files;
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                })()
                """);
            await WaitForAsync("(document.getElementById('docSays')?.textContent || '').includes('not a PNG, JPEG, GIF or WebP')",
                "A PDF chosen as a thumbnail was not refused before sending.");
            Assert.False(await Browser.EvaluateAsync<bool>("window.__writes.some(w => w.includes('/thumbnail'))"),
                "A refused thumbnail was sent anyway.");

            await Browser.EvaluateAsync<bool>("""
                (() => {
                  const png = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='), c => c.charCodeAt(0));
                  const carried = new DataTransfer(); carried.items.add(new File([png], "zz-cover.png", { type: "image/png" }));
                  const input = document.getElementById('docThumbFile'); input.files = carried.files;
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                })()
                """);
            await WaitForAsync($"window.__writes.some(w => w.startsWith('PUT /content/documents/{id}/thumbnail'))",
                "Choosing a picture did not send it as the document's thumbnail.");

            // Set through the API, the page shows it, and More offers to change or remove it.
            using (HttpRequestMessage request = new(HttpMethod.Put, new Uri($"{Root}/content/documents/{id}/thumbnail")))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using MultipartFormDataContent form = new();
                ByteArrayContent file = new(Png);
                file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(file, "file", "zz-cover.png");
                request.Content = form;
                using HttpResponseMessage response = await Http.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode, $"Setting the thumbnail answered {(int)response.StatusCode}");
            }

            await OpenAsync($"/studio/#/item/{id}", token);
            await WaitForAsync("(document.querySelector('#docThumb img')?.getAttribute('src') || '').startsWith('blob:')",
                "The document's own thumbnail is not shown on its page.");
            await WaitForAsync("document.querySelector('#docThumb img')?.naturalWidth === 1",
                "The thumbnail shown is not the picture that was set.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "document.getElementById('docThumbChange')?.textContent.trim() === 'Change thumbnail…' && !!document.getElementById('docThumbRemove')"),
                "More does not offer Change thumbnail… and Remove thumbnail for a document with one.");

            await ClickAsync("#docActions details.itemmore summary");
            await ClickAsync("#docThumbRemove");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('DELETE /content/documents/{id}/thumbnail'))",
                "Remove thumbnail did not ask the server to forget it.");

            // The folder step goes back to Content on that folder.
            await ClickAsync($"#docCrumb a[data-crumb-folder=\"{folder}\"]");
            await WaitForAsync($"document.querySelector('#contentFolders [data-folder-pick=\"{folder}\"]')?.getAttribute('aria-pressed') === 'true'",
                "The folder step of the breadcrumb did not open Content on the folder.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await RemoveAsync(id);
            if (folder is not null) await AdminAsync(HttpMethod.Delete, $"/content/folders/{folder}");
        }
    }

    [Fact]
    public async Task New_item_uploads_a_document_with_its_title_from_the_file_name()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("!!document.getElementById('newLayer')", "Studio has no New item.");
        await ClickAsync("#newLayer");
        await WaitForAsync("!!document.getElementById('kindDocument')", "New item offers no Document (ADR-188 condition 4).");
        await ClickAsync("#kindDocument");
        await WaitForAsync("!!document.getElementById('docFile')", "Document did not open its form.");

        // Upload with nothing chosen is refused by name, before anything is sent.
        await ClickAsync("#docUpload");
        await WaitForAsync("(document.getElementById('docNewSays')?.textContent || '').includes('choose a file')",
            "Upload with no file said nothing.");

        // A file that is not a kind the server keeps is refused before it is sent.
        await Browser.EvaluateAsync<bool>("""
            (() => {
              const carried = new DataTransfer(); carried.items.add(new File(["x"], "zz.exe"));
              const input = document.getElementById('docFile'); input.files = carried.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);
        await ClickAsync("#docUpload");
        await WaitForAsync("(document.getElementById('docNewSays')?.textContent || '').includes('not a kind')",
            "A file of the wrong kind was not refused before sending.");

        await Browser.EvaluateAsync<bool>("""
            (() => {
              const carried = new DataTransfer();
              carried.items.add(new File([new Uint8Array([37, 80, 68, 70, 45])], "zz-annual-report.pdf", { type: "application/pdf" }));
              const input = document.getElementById('docFile'); input.files = carried.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);
        Assert.Equal("zz-annual-report", await Browser.EvaluateAsync<string>("document.getElementById('docNewTitle').value"));

        await ClickAsync("#docUpload");
        await WaitForAsync("window.__writes.some(w => w.startsWith('POST /content/documents [file,sharing,title]'))",
            "Upload did not send the file, its title and its sharing.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
