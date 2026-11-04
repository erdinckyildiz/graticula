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
/// ADR-267: an item's page in Studio — a document's, a web map's and, since condition 1, a service's — says whether it
/// has metadata, downloads it, lets whoever manages the item upload or replace it, and lists the item's comments with an
/// add box and Delete, the text shown as text. Organization › Settings › General turns comments off. Every write the page makes is trapped by the
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
            // Asked in the console's own dialog (UX review, m11), and Cancel sends nothing.
            await ClickAsync($"#docImc [data-imc-delete=\"{comment}\"]");
            await WaitForAsync("document.getElementById('serverConfirm').open", "Delete did not ask in the console's dialog.");
            Assert.Contains("comment", await Browser.EvaluateAsync<string>("document.getElementById('serverConfirmTitle').textContent"),
                StringComparison.Ordinal);
            await ClickAsync("#serverConfirmCancel");
            await WaitForAsync("!document.getElementById('serverConfirm').open", "Cancel did not close the dialog.");
            Assert.False(await Browser.EvaluateAsync<bool>($"window.__writes.some(w => w.includes('/comments/{comment}/delete'))"),
                "Cancel deleted the comment.");
            await WaitForAsync($"document.activeElement?.matches('#docImc [data-imc-delete=\"{comment}\"]') === true",
                "Cancel did not give the cursor back to Delete.");

            await ClickAsync($"#docImc [data-imc-delete=\"{comment}\"]");
            await WaitForAsync("document.getElementById('serverConfirm').open", "Delete did not ask a second time.");
            await ClickAsync("#serverConfirmGo");
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

    // ---------------------------------------------------------------- a service's item page (ADR-267 condition 1)

    private static string Service => Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings";

    /// <summary>Opens the service's item page in Studio and waits until its panel has read the item; returns the item id.</summary>
    private async Task<string> OpenServiceAsync(string token)
    {
        await OpenAsync($"/studio/#/service/{Service}", token);
        await WaitForAsync(
            "typeof imcOpen !== 'undefined' && imcOpen?.box?.id === 'serviceImc' && !!document.querySelector('#serviceImc [data-imc-body]') && !document.querySelector('#serviceImc [data-imc-body]').textContent.includes('Reading the comments')",
            "The service's item page drew no metadata and comments panel (ADR-267 condition 1).");
        return await Browser.EvaluateAsync<string>("imcOpen.id") ?? string.Empty;
    }

    /// <summary>A fetch that rewrites the answers of the listed paths, planted before the page loads.</summary>
    private static string Rewriting(string paths, string change) => $$"""
        (() => {
          const inner = window.fetch.bind(window);
          const paths = {{paths}};
          window.fetch = async (input, init) => {
            const url = typeof input === "string" ? input : input.url;
            const response = await inner(input, init);
            const path = new URL(url, location.href).pathname;
            if (!paths.some(p => p instanceof RegExp ? p.test(path) : p === path)) return response;
            const v = await response.clone().json().catch(() => null);
            if (v === null) return response;
            {{change}}
            return new Response(JSON.stringify(v), { status: response.status, headers: { "Content-Type": "application/json" } });
          };
        })();
        """;

    [Fact]
    public async Task A_services_page_has_the_same_panel_and_its_owner_uploads_metadata_and_adds_and_deletes_comments()
    {
        (string token, _) = await SignInAsync();
        string id = await OpenServiceAsync(token);
        Assert.Matches("^[0-9a-f]{32}$", id);

        // First run: no metadata, no comments — the owner is offered Upload and the box, and no Download.
        await WaitForAsync(Shown("#serviceImc [data-imc-upload]"), "The service's owner is not offered Upload metadata.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#serviceImc [data-imc-download]')"),
            "Download metadata is offered for a service with none.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#serviceImc [data-imc-text]")), "The service's page offers no comment box.");

        // The panel is as far below the card above it as the cards above are from each other (UX review 2026-11-04, m10):
        // each gap between the main column's shown cards, the Layers card's head and list counted as one card.
        string gaps = await Browser.EvaluateAsync<string>("""
            (() => {
              const cards = [...document.querySelectorAll('#serviceOverview > .itemmain > *')]
                .filter(e => e.getClientRects().length && e.id !== 'serviceLayersPanel');
              const bottomOf = e => (e.id === 'serviceLayersHead' ? document.getElementById('serviceLayersPanel') : e).getBoundingClientRect().bottom;
              const out = [];
              for (let i = 1; i < cards.length; i++) out.push(Math.round(cards[i].getBoundingClientRect().top - bottomOf(cards[i - 1])));
              return out.join(',');
            })()
            """) ?? string.Empty;
        Assert.True(gaps.Split(',').Distinct().Count() == 1, $"The service's main column spaces its cards unevenly: {gaps}.");

        // A comment left through the API, as a client would, keyed on the service's portal item.
        JsonElement left = await PortalAsync($"/sharing/rest/content/items/{id}/addComment",
            Form(("comment", "<img src=x onerror=\"window.__pwned=1\"> service checked")));
        string comment = left.GetProperty("commentId").GetString()!;

        try
        {
            await OpenServiceAsync(token);
            await WaitForAsync("(document.getElementById('serviceImc')?.textContent || '').includes('service checked')",
                "The service's page does not list the comment left on its item.");
            Assert.True(await Browser.EvaluateAsync<bool>("!document.querySelector('#serviceImc img') && !window.__pwned"),
                "A comment's markup was drawn as markup on the service's page.");

            // The panel is last in the main column: below the description and the layers, where Portal keeps comments.
            Assert.True(await Browser.EvaluateAsync<bool>("""
                (() => {
                  const panel = document.getElementById('serviceImc').getBoundingClientRect();
                  const layers = document.getElementById('serviceLayersPanel');
                  const head = document.getElementById('serviceHead').getBoundingClientRect();
                  return panel.top >= head.bottom && (layers.hidden || panel.top >= layers.getBoundingClientRect().bottom);
                })()
                """), "The service's panel is not below its description and its layers.");

            await ChooseMetadataAsync("serviceImc");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('POST /sharing/rest/content/users/') && w.includes('/items/{id}/update [f,metadata]'))",
                "Choosing a file did not send it as the service item's metadata.");

            await ClickAsync("#serviceImc [data-imc-add]");
            await WaitForAsync("document.querySelector('#serviceImc [data-imc-text]')?.getAttribute('aria-invalid') === 'true'",
                "An empty comment was not refused on the service's page.");
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/addComment", StringComparison.Ordinal));

            await Browser.EvaluateAsync<bool>("(document.querySelector('#serviceImc [data-imc-text]').value = 'Matches the survey.', true)");
            await ClickAsync("#serviceImc [data-imc-add]");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('POST /sharing/rest/content/items/{id}/addComment'))",
                "Add comment did not send the comment from the service's page.");

            await WaitForAsync(Shown($"#serviceImc [data-imc-delete=\"{comment}\"]"), "The owner is not offered Delete on the service's comment.");
            await ClickAsync($"#serviceImc [data-imc-delete=\"{comment}\"]");
            await WaitForAsync("document.getElementById('serverConfirm').open", "Delete did not ask in the console's dialog.");
            await ClickAsync("#serverConfirmGo");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('POST /sharing/rest/content/items/{id}/comments/{comment}/delete'))",
                "Delete did not delete the service's comment.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PortalAsync($"/sharing/rest/content/items/{id}/comments/{comment}/delete", Form());
        }
    }

    [Fact]
    public async Task A_reader_of_a_service_downloads_its_metadata_and_comments_and_is_offered_no_upload()
    {
        // A reader the service is shared with: the listing says it does not manage it, and the item has metadata. Made so in
        // the browser, as SomebodyElsesLayerIsReadNotOfferedTests does, because this suite signs in as an administrator.
        await Browser.PlantAsync(Rewriting("""["/content/items", /^\/sharing\/rest\/content\/items\/[0-9a-f]{32}$/]""", """
            const mark = x => {
              if (Array.isArray(x)) { x.forEach(mark); return; }
              if (x && typeof x === "object") { if ("manages" in x) x.manages = false; Object.values(x).forEach(mark); }
            };
            if (path === "/content/items") mark(v); else v.typeKeywords = [...(v.typeKeywords || []), "Metadata"];
            """));

        (string token, _) = await SignInAsync();
        await OpenServiceAsync(token);

        await WaitForAsync(Shown("#serviceImc [data-imc-download]"), "A reader of a service with metadata is offered no Download metadata.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#serviceImc [data-imc-upload]')"),
            "A reader who does not manage the service is offered Upload or Replace metadata.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#serviceImc [data-imc-text]")), "A reader is not offered the comment box.");

        await ClickAsync("#serviceImc [data-imc-download]");
        await WaitForAsync("(document.querySelector('#serviceImc [data-imc-meta-says]')?.textContent || '').length > 0",
            "Download metadata said nothing on the service's page.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>A member with the user role, signed in with the password they chose: a token for a real non-owner reader.</summary>
    private async Task<string> MemberAsync(string name)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members", JsonSerializer.Serialize(new { name, role = "user" }));
        Assert.True(made == 201, $"Making {name} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;

        async Task<string> LoginAsync(string password)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/rest/auth/login"));
            request.Content = new StringContent(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"Signing in as {name} answered {(int)response.StatusCode}.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        }

        string first = await LoginAsync(issued);
        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        using HttpRequestMessage change = new(HttpMethod.Post, new Uri($"{Root}/rest/auth/password"));
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
        change.Content = new StringContent(JsonSerializer.Serialize(new { currentPassword = issued, newPassword = chosen }), Encoding.UTF8, "application/json");
        using HttpResponseMessage changed = await Http.SendAsync(change);
        Assert.True(changed.IsSuccessStatusCode, $"Replacing {name}'s issued password answered {(int)changed.StatusCode}.");
        return await LoginAsync(chosen);
    }

    [Fact]
    public async Task A_member_who_reads_a_service_sees_its_comments_and_may_comment_but_not_upload_or_delete_anothers()
    {
        // Signed in as a real member the service is shared with (it is public), not an administrator made to look like one.
        (string admin, _) = await SignInAsync();
        string id = await OpenServiceAsync(admin);
        string member = "zz_imcr_" + Guid.NewGuid().ToString("N")[..8];
        JsonElement left = await PortalAsync($"/sharing/rest/content/items/{id}/addComment", Form(("comment", "Left by the owner.")));
        string comment = left.GetProperty("commentId").GetString()!;

        try
        {
            string token = await MemberAsync(member);
            await OpenServiceAsync(token);

            await WaitForAsync("(document.getElementById('serviceImc')?.textContent || '').includes('Left by the owner.')",
                "A member reading the service does not see its comments.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#serviceImc [data-imc-upload]')"),
                "A member who does not manage the service is offered Upload metadata.");
            Assert.False(await Browser.EvaluateAsync<bool>($"!!document.querySelector('#serviceImc [data-imc-delete=\"{comment}\"]')"),
                "A member is offered Delete on the owner's comment.");
            Assert.True(await Browser.EvaluateAsync<bool>(Shown("#serviceImc [data-imc-text]")), "A member is not offered the comment box.");
            // Told what the item has, not invited to upload: whether it has metadata depends on the suites run before.
            Assert.DoesNotContain("Upload", await Browser.EvaluateAsync<string>("document.querySelector('#serviceImc [data-imc-meta]').textContent")
                ?? string.Empty, StringComparison.Ordinal);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await PortalAsync($"/sharing/rest/content/items/{id}/comments/{comment}/delete", Form());
            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
        }
    }

    [Fact]
    public async Task A_services_page_says_comments_are_off_when_the_organization_turned_them_off()
    {
        await Browser.PlantAsync(Rewriting("""["/sharing/rest/portals/self"]""", "v.commentsEnabled = false;"));

        (string token, _) = await SignInAsync();
        await OpenServiceAsync(token);

        await WaitForAsync("(document.getElementById('serviceImc')?.textContent || '').includes('Comments are turned off in this organization.')",
            "The service's page does not say comments are off.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#serviceImc [data-imc-text]')"),
            "The service's page offers a comment box with comments off.");

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task A_services_panel_fits_a_phone_and_comes_last()
    {
        await Browser.CallAsync("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });

        try
        {
            (string token, _) = await SignInAsync();
            await OpenServiceAsync(token);
            await WaitForAsync(Shown("#serviceImc [data-imc-add]"), "The service's panel draws no Add comment at 390.");

            Assert.True(await Browser.EvaluateAsync<bool>("document.documentElement.scrollWidth <= 390"),
                "The service's page scrolls sideways at 390 with the panel on it.");

            // Every control in the panel is a 44-pixel target.
            double smallest = await Browser.EvaluateAsync<double>("""
                Math.min(...[...document.querySelectorAll('#serviceImc button, #serviceImc textarea')]
                  .filter(e => e.getClientRects().length > 0).map(e => e.getBoundingClientRect().height))
                """);
            Assert.True(smallest >= 44, $"A control in the service's panel is {smallest} pixels tall at 390.");

            // The page's last part, after the item's information and the layers — as on a document's and a map's page.
            Assert.True(await Browser.EvaluateAsync<bool>("""
                (() => {
                  const top = document.getElementById('serviceImc').getBoundingClientRect().top;
                  return ['serviceDetails', 'serviceHead', 'serviceLayersPanel'].map(id => document.getElementById(id))
                    .filter(e => e && e.getClientRects().length > 0).every(e => e.getBoundingClientRect().bottom <= top);
                })()
                """), "At 390 the service's panel is not the page's last part: " + await Browser.EvaluateAsync<string>(
                "JSON.stringify(['serviceDetails','serviceHead','serviceLayersPanel','serviceImc'].map(id => { const r = document.getElementById(id).getBoundingClientRect(); return [id, Math.round(r.top), Math.round(r.bottom), Math.round(r.left), Math.round(r.width)]; }))"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await Browser.CallAsync("Emulation.clearDeviceMetricsOverride");
        }
    }

    [Fact]
    public async Task An_image_services_page_has_the_panel_through_its_coverages_item()
    {
        (string token, _) = await SignInAsync();

        // Found signed in, through the portal's search: the fixture's image service is not in the anonymous directory, and
        // a test that returned when it found none passed without looking (2026-11-04).
        await OpenAsync("/studio/", token);
        string image = await Browser.EvaluateAsync<string>("""
            (async () => {
              const found = await api('/sharing/rest/search?f=json&num=100&q=' + encodeURIComponent('type:"Image Service"'));
              const url = ((found.results || [])[0] || {}).url || '';
              const at = /\/rest\/services\/(.+)\/ImageServer/.exec(url);
              return at ? at[1] : '';
            })()
            """) ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(image), "The fixture lists no image service to open, signed in.");

        await OpenAsync($"/studio/#/service/{image}", token);
        await WaitForAsync("typeof serviceItem !== 'undefined' && serviceItem?.name === " + JsonSerializer.Serialize(image),
            "The image service's page never read its listing.");

        // ADR-267 condition 1: its item is its coverage's, which the listing carries as imageItemId and the server accepts.
        await WaitForAsync(Shown("#serviceImc"), "An image service's page has no metadata and comments panel.");
        await WaitForAsync("!document.querySelector('#serviceImc .bad, #serviceImc .bad-inline')"
            + " && !!document.querySelector('#serviceImc textarea, #serviceImc [role=status]')",
            "The image service's panel did not load, or loaded with an error.");
        NothingWentWrong(await PageErrorsAsync());
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
