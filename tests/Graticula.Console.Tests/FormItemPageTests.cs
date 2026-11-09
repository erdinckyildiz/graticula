using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// Studio's screens for a form — ADR-287 §5.3 (a workbook is checked at upload and a refused one is never stored),
/// §5.7 (the results screen is on the form's item page) and §5.11 (l) (sharing opens a submit-only view).
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes reach the server only where a test lets them</b>, through the harness's <c>__letWritesTo</c>: the upload's
/// check and add, and Publish. Every test that lets one through removes what it made — the item, the service and the
/// choice lists named after its <c>form_id</c>.
/// </para>
/// <para>
/// <b>The workbooks are written here</b>, as the minimum of SpreadsheetML an XLSForm reader needs — a workbook, its
/// relationships and one sheet part per sheet, every cell an inline string — so the test does not depend on the
/// server's own writer to make what the server reads.
/// </para>
/// </remarks>
public sealed class FormItemPageTests : ConsoleTest
{
    // ------------------------------------------------------------------------------------------------------- the tests

    [Fact]
    public async Task A_refused_workbook_shows_its_refusals_by_row_and_is_not_stored()
    {
        string formId = NewFormId();
        byte[] book = Workbook(
            ("survey", [["type", "name", "label"], ["text", "ad", "Ad"], ["rank", "sira", "Sıra"]]),
            ("settings", [["form_title", "form_id"], ["Refused", formId]]));

        await OpenUploadAsync();
        await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/check'], true)");
        await ChooseWorkbookAsync(book, $"{formId}.xlsx");
        await ClickAsync("#formAdd");

        await WaitForAsync(Shown("#formNewRefusals"), "A refused workbook showed no table of refusals.");
        string[][] rows = await Browser.EvaluateAsync<string[][]>(
            "[...document.querySelectorAll('#formNewRefusals tbody tr')].map(r => [...r.cells].map(c => c.textContent.trim()))") ?? [];
        Assert.Contains(rows, r => r.Length == 4 && r[0] == "survey" && r[1] == "3" && r[3].Length > 0);
        Assert.Contains("Not added", await Browser.EvaluateAsync<string>("document.getElementById('formNewSays').textContent"));
        Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('addItem').open"), "The dialog closed on a refused workbook.");

        // Only the check was sent; nothing tried to add the form.
        string[] writes = await WritesAsync();
        Assert.Contains(writes, w => w.StartsWith("POST /admin/forms/", StringComparison.Ordinal) && w.Contains("/check", StringComparison.Ordinal));
        Assert.DoesNotContain(writes, w => w.StartsWith("POST /admin/forms ", StringComparison.Ordinal) || w == "POST /admin/forms");
        Assert.Null(await FindItemAsync(formId));

        NothingWentWrong(await PageErrorsAsync());
    }

    [Fact]
    public async Task An_accepted_workbook_is_added_and_its_item_page_opens_not_published()
    {
        string formId = NewFormId();
        byte[] book = Workbook(
            ("survey", [["type", "name", "label"], ["geopoint", "konum", "Konum"], ["text", "ad", "Ad"], ["integer", "yas", "Yaş"]]),
            ("settings", [["form_title", "form_id"], ["Kabul", formId]]));
        string? item = null;

        try
        {
            await OpenUploadAsync();
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/admin/forms'], true)");
            await ChooseWorkbookAsync(book, $"{formId}.xlsx");
            await ClickAsync("#formAdd");

            await WaitForAsync("document.getElementById('view-document')?.classList.contains('on')"
                + " && (document.getElementById('formStatus')?.textContent || '') === 'Not published'",
                "An accepted workbook did not open its item page as a form that is not published.");
            item = await FindItemAsync(formId);
            Assert.NotNull(item);
            Assert.Contains(item!, await Browser.EvaluateAsync<string>("location.href"), StringComparison.Ordinal);
            Assert.Equal("3", await Browser.EvaluateAsync<string>("document.getElementById('formQuestionCount').firstChild.textContent.trim()"));
            await WaitForAsync(Shown("#formPublishOpen"), "A form that is not published offers no Publish.");
            Assert.Equal($"/forms/{item}", await Browser.EvaluateAsync<string>("document.getElementById('formOpen').getAttribute('href')"));

            // Share goes through the form's own route, which explains the submit-only view before it is chosen.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = [], window.__writes.length = 0, true)");
            await ClickAsync("#docShareOpen");
            await WaitForAsync(Shown("input[name=formShareScope][value=public]"), "A form's Share offers no public scope.");
            Assert.Contains("only adds records", await Browser.EvaluateAsync<string>("document.getElementById('formShareWhy').textContent"));
            await ClickAsync("input[name=formShareScope][value=public]");
            await ClickAsync("#formShareSave");
            await WaitForAsync($"window.__writes.some(w => w.startsWith('PUT /admin/forms/{item}/sharing'))",
                "Sharing a form did not go to its sharing route.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await RemoveAsync(item ?? await FindItemAsync(formId), formId);
        }
    }

    [Fact]
    public async Task Publish_shows_the_plan_and_then_the_published_state_with_its_service()
    {
        string formId = NewFormId();
        string item = await AddAsync(formId);

        try
        {
            (string token, string cookie) = await SignInAsync();
            await OpenAsync($"/studio/#/item/{item}", token, cookie);
            await WaitForAsync(Shown("#formPublishOpen"), "The form's page offers no Publish.");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/admin/forms/'], true)");

            await ClickAsync("#formPublishOpen");
            await WaitForAsync(Shown("#formPlanHead"), "Publish showed no plan.");
            string plan = await Browser.EvaluateAsync<string>("document.getElementById('formPlan').textContent") ?? string.Empty;
            Assert.Contains(formId, plan, StringComparison.Ordinal);
            Assert.Contains("hasar", plan, StringComparison.Ordinal);
            Assert.Contains("choices", plan, StringComparison.Ordinal);
            Assert.Equal("formPlanHead", await Browser.EvaluateAsync<string>("document.activeElement.id"));

            await ClickAsync("#formPublishGo");
            await WaitForAsync("(document.getElementById('formStatus')?.textContent || '').startsWith('Published')"
                + " && !!document.getElementById('formServiceLink')",
                "Publishing did not turn the page to Published with its service.");
            // Studio writes its hash links as paths (ADR-034); either spelling names the service's page.
            Assert.EndsWith($"service/hosted/{formId}",
                await Browser.EvaluateAsync<string>("document.getElementById('formServiceLink').getAttribute('href')"));
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('formPublishOpen')"),
                "A published form still offers Publish.");
            await WaitForAsync(Shown("#formResultCount"), "A published form shows no Results.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await RemoveAsync(item, formId);
        }
    }

    [Fact]
    public async Task Results_show_a_submitted_row_its_count_and_a_summary_per_question()
    {
        string formId = NewFormId();
        string item = await AddAsync(formId);

        try
        {
            (int published, string publication) = await AdminAsync(HttpMethod.Post, $"/admin/forms/{item}/publish", "{}");
            Assert.True(published == 200, $"Publishing answered {published}: {publication}");
            JsonElement top = JsonDocument.Parse(publication).RootElement.GetProperty("layers").EnumerateArray()
                .First(l => l.GetProperty("repeat").GetString() is null or "");
            string url = top.GetProperty("url").GetString()!;
            JsonElement columns = top.GetProperty("columns");
            string name = "Bina " + Guid.NewGuid().ToString("N")[..6];
            JsonArray adds = [new JsonObject
            {
                ["geometry"] = new JsonObject { ["x"] = 29.0, ["y"] = 41.0 },
                ["attributes"] = new JsonObject
                {
                    [columns.GetProperty("ad").GetString()!] = name,
                    [columns.GetProperty("yas").GetString()!] = 37,
                    [columns.GetProperty("hasar").GetString()!] = "agir",
                },
            }];
            (string token, string cookie) = await SignInAsync();
            using (HttpRequestMessage edit = new(HttpMethod.Post, new Uri($"{Root}{url}/applyEdits")))
            {
                edit.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["adds"] = adds.ToJsonString() });
                edit.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answer = await Http.SendAsync(edit);
                string said = await answer.Content.ReadAsStringAsync();
                Assert.True(answer.IsSuccessStatusCode && said.Contains("\"success\":true", StringComparison.Ordinal), $"The row was not added: {said}");
            }

            await OpenAsync($"/studio/#/item/{item}", token, cookie);
            await WaitForAsync(Shown("#formLatest"), "Results showed no table of submissions.");
            Assert.StartsWith("1 submission", await Browser.EvaluateAsync<string>("document.getElementById('formResultCount').textContent.trim()"));
            string[] cells = await Browser.EvaluateAsync<string[]>(
                "[...document.querySelectorAll('#formLatest tbody tr:first-child td')].map(c => c.textContent.trim())") ?? [];
            Assert.Contains(name, cells);
            Assert.Contains("Ağır", cells);   // a choice by its label, not its name
            string[] heads = await Browser.EvaluateAsync<string[]>(
                "[...document.querySelectorAll('#formLatest thead th')].map(c => c.textContent.trim())") ?? [];
            Assert.Contains("Bina adı", heads);

            // The summary: the choice counted, the one nobody chose listed at zero, and the number's range.
            string[][] choices = await Browser.EvaluateAsync<string[][]>(
                "[...document.querySelectorAll('.formsum[data-question=hasar] tbody tr')].map(r => [...r.cells].slice(0, 2).map(c => c.textContent.trim()))") ?? [];
            Assert.Contains(choices, r => r[0] == "Ağır" && r[1] == "1");
            Assert.Contains(choices, r => r[0] == "Yok" && r[1] == "0");
            string number = await Browser.EvaluateAsync<string>("document.querySelector('.formsum[data-question=yas]').textContent") ?? string.Empty;
            Assert.Contains("37", number, StringComparison.Ordinal);
            Assert.Contains("Average", number, StringComparison.Ordinal);
            Assert.EndsWith("tab=data&layer=0", await Browser.EvaluateAsync<string>("document.querySelector('.formlayers a').getAttribute('href')"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await RemoveAsync(item, formId);
        }
    }

    // ----------------------------------------------------------------------------------------------------- the helpers

    private static string NewFormId() => "zz_fstudio_" + Guid.NewGuid().ToString("N")[..8];

    private async Task OpenUploadAsync()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync("/studio/#/content", token);
        await WaitForAsync("!!document.getElementById('newLayer')", "Studio has no New item.");
        await ClickAsync("#newLayer");
        await WaitForAsync(Shown("#kindForm"), "New item offers no Form (XLSForm).");
        await ClickAsync("#kindForm");
        await WaitForAsync(Shown("#formFile"), "Form (XLSForm) did not open its upload.");
    }

    private async Task ChooseWorkbookAsync(byte[] book, string fileName)
    {
        string bytes = Convert.ToBase64String(book);
        await Browser.EvaluateAsync<bool>($$"""
            (() => {
              const bytes = Uint8Array.from(atob('{{bytes}}'), c => c.charCodeAt(0));
              const carried = new DataTransfer();
              carried.items.add(new File([bytes], '{{fileName}}', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }));
              const input = document.getElementById('formFile'); input.files = carried.files;
              input.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            })()
            """);
    }

    private static JsonObject Sheet(string name, string[] columns, params string[][] rows) => new()
    {
        ["name"] = name,
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]),
        ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]),
    };

    /// <summary>Adds a form through the API, as its JSON model: a location, a name, an age and a choice.</summary>
    private async Task<string> AddAsync(string formId)
    {
        JsonObject model = new()
        {
            ["format"] = "xlsform/1",
            ["sheets"] = new JsonArray(
                Sheet("survey", ["type", "name", "label"],
                    ["geopoint", "konum", "Konum"],
                    ["text", "ad", "Bina adı"],
                    ["integer", "yas", "Bina yaşı"],
                    ["select_one hasar", "hasar", "Hasar"]),
                Sheet("choices", ["list_name", "name", "label"],
                    ["hasar", "yok", "Yok"],
                    ["hasar", "agir", "Ağır"]),
                Sheet("settings", ["form_title", "form_id"],
                    ["Bina denetimi", formId])),
        };
        (int status, string body) = await AdminAsync(HttpMethod.Post, "/admin/forms",
            new JsonObject { ["title"] = "Bina denetimi " + formId, ["model"] = model }.ToJsonString());
        Assert.True(status == 201, $"Adding the form answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>The form item whose title carries this form_id, or null — how a test sees whether the upload kept one.</summary>
    private async Task<string?> FindItemAsync(string formId)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/content/documents?num=200");
        Assert.True(status == 200, $"Listing documents answered {status}: {body}");
        return Objects(JsonDocument.Parse(body).RootElement)
            .Where(o => o.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String
                && t.GetString()!.Contains(formId, StringComparison.Ordinal) && o.TryGetProperty("id", out _))
            .Select(o => o.GetProperty("id").GetString())
            .FirstOrDefault();
    }

    private async Task RemoveAsync(string? item, string formId)
    {
        await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{formId}?folder=hosted&drop=true");
        await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{formId}_submit?folder=hosted&drop=true");
        (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/domains");

        if (status == 200)
        {
            foreach (JsonElement domain in Objects(JsonDocument.Parse(body).RootElement)
                .Where(o => o.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    && n.GetString()!.StartsWith(formId, StringComparison.Ordinal) && o.TryGetProperty("id", out _)))
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/domains/{domain.GetProperty("id").GetString()}");
            }
        }

        if (!string.IsNullOrEmpty(item))
        {
            await AdminAsync(HttpMethod.Delete, $"/content/documents/{item}");
        }
    }

    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;

            foreach (JsonProperty p in element.EnumerateObject())
            {
                foreach (JsonElement inner in Objects(p.Value))
                {
                    yield return inner;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in element.EnumerateArray())
            {
                foreach (JsonElement inner in Objects(e))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>The least SpreadsheetML an XLSForm reader needs: every cell an inline string, rows in order.</summary>
    private static byte[] Workbook(params (string Name, string[][] Rows)[] sheets)
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        using MemoryStream stream = new();

        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string path, string xml)
            {
                using StreamWriter writer = new(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }

            static string Column(int i) => i < 26 ? ((char)('A' + i)).ToString() : $"{(char)('A' + (i / 26) - 1)}{(char)('A' + (i % 26))}";

            Part("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + string.Concat(sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"))
                + "</Types>");
            Part("_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Pkg}\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Part("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets>"
                + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{SecurityElement.Escape(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))
                + "</sheets></workbook>");
            Part("xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Pkg}\">"
                + string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
                + "</Relationships>");

            for (int s = 0; s < sheets.Length; s++)
            {
                StringBuilder xml = new($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{Main}\"><sheetData>");

                for (int r = 0; r < sheets[s].Rows.Length; r++)
                {
                    xml.Append($"<row r=\"{r + 1}\">");

                    for (int c = 0; c < sheets[s].Rows[r].Length; c++)
                    {
                        xml.Append($"<c r=\"{Column(c)}{r + 1}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(sheets[s].Rows[r][c])}</t></is></c>");
                    }

                    xml.Append("</row>");
                }

                Part($"xl/worksheets/sheet{s + 1}.xml", xml.Append("</sheetData></worksheet>").ToString());
            }
        }

        return stream.ToArray();
    }
}
