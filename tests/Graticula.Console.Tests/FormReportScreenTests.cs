using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-301 §5.8: on a form's results, submissions are ticked, a template is chosen, and Create report downloads the Word
/// document the server fills; a template that does not fit is refused at upload with its paragraphs listed, and focus goes
/// to the list. The templates are written here — never one of Esri's.
/// </summary>
public sealed class FormReportScreenTests : ConsoleTest
{
    private static byte[] Template(params string[] paragraphs)
    {
        const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        using MemoryStream output = new();

        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Ns}><w:body>"
                + string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(p)}</w:t></w:r></w:p>"))
                + "<w:sectPr/></w:body></w:document>");
        }

        return output.ToArray();
    }

    private static string NewFormId() => "zz_freport_" + Guid.NewGuid().ToString("N")[..8];

    private static JsonObject Sheet(string name, string[] columns, params string[][] rows) => new()
    {
        ["name"] = name,
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]),
        ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]),
    };

    [Fact]
    public async Task Ticked_submissions_are_made_into_a_report_and_a_template_that_does_not_fit_is_listed_by_paragraph()
    {
        string formId = NewFormId();
        string? item = null;

        try
        {
            JsonObject model = new()
            {
                ["format"] = "xlsform/1",
                ["sheets"] = new JsonArray(
                    Sheet("survey", ["type", "name", "label"], ["geopoint", "konum", "Konum"], ["text", "ad", "Bina adı"], ["select_one hasar", "hasar", "Hasar"]),
                    Sheet("choices", ["list_name", "name", "label"], ["hasar", "yok", "Yok"], ["hasar", "agir", "Ağır"]),
                    Sheet("settings", ["form_title", "form_id"], ["Rapor", formId])),
            };
            (int status, string body) = await AdminAsync(HttpMethod.Post, "/admin/forms",
                new JsonObject { ["title"] = "Rapor " + formId, ["model"] = model }.ToJsonString());
            Assert.True(status == 201, $"Adding the form answered {status}: {body}");
            item = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

            (int published, string publication) = await AdminAsync(HttpMethod.Post, $"/admin/forms/{item}/publish", "{}");
            Assert.True(published == 200, $"Publishing answered {published}: {publication}");
            JsonElement top = JsonDocument.Parse(publication).RootElement.GetProperty("layers").EnumerateArray()
                .First(l => l.GetProperty("repeat").GetString() is null or "");
            string url = top.GetProperty("url").GetString()!;
            JsonElement columns = top.GetProperty("columns");
            (string token, string cookie) = await SignInAsync();

            using (HttpRequestMessage edit = new(HttpMethod.Post, new Uri($"{Root}{url}/applyEdits")))
            {
                JsonArray adds = [new JsonObject
                {
                    ["geometry"] = new JsonObject { ["x"] = 29.0, ["y"] = 41.0 },
                    ["attributes"] = new JsonObject { [columns.GetProperty("ad").GetString()!] = "Kule", [columns.GetProperty("hasar").GetString()!] = "agir" },
                }];
                edit.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["adds"] = adds.ToJsonString() });
                edit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answer = await Http.SendAsync(edit);
                Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
            }

            using (MultipartFormDataContent form = new())
            {
                form.Add(new ByteArrayContent(Template("Bina: ${ad}", "Hasar: ${hasar}")), "file", "denetim.docx");
                using HttpRequestMessage upload = new(HttpMethod.Post, new Uri($"{Root}/admin/forms/{item}/reports/templates")) { Content = form };
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answer = await Http.SendAsync(upload);
                Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
            }

            await OpenAsync($"/studio/#/item/{item}", token, cookie);
            await WaitForAsync("!!document.getElementById('formReportTemplate')", "Results offered no report templates.");

            // Nothing ticked: the button says so rather than failing.
            Assert.Equal("true", await Browser.EvaluateAsync<string>("document.getElementById('formReportGo').getAttribute('aria-disabled')"));
            Assert.StartsWith("No submission chosen", await Browser.EvaluateAsync<string>("document.getElementById('formReportPicked').textContent"));
            Assert.Equal("denetim.docx", await Browser.EvaluateAsync<string>("document.getElementById('formReportTemplate').value"));

            // Every new control has a name a screen reader reads.
            Assert.True(await Browser.EvaluateAsync<bool>(
                "[...document.querySelectorAll('[data-form-pick], #formPickAll')].every(b => (b.getAttribute('aria-label') || '').length > 10)"),
                "A tick box has no label.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "['formReportTemplate', 'formReportName', 'formReportFile'].every(id => !!document.querySelector(`label[for=${id}]`))"),
                "A report control has no label.");

            await ClickAsync("[data-form-pick]");
            await WaitForAsync("document.getElementById('formReportGo').getAttribute('aria-disabled') === null"
                + " && document.getElementById('formReportPicked').textContent.startsWith('One submission')",
                "Ticking a submission did not make Create report available.");

            // Create report reaches the server and the document comes back to be downloaded.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/reports'], true)");
            await ClickAsync("#formReportGo");
            await WaitForAsync("/\\.docx \\(.*\\) is downloading/.test(document.getElementById('formReportSays').textContent)",
                "Create report did not download a Word document.");
            Assert.Contains(await WritesAsync(), w => w.StartsWith($"POST /admin/forms/{item}/reports", StringComparison.Ordinal) && w.EndsWith("(sent)", StringComparison.Ordinal));

            // A template with a map is refused, its paragraph listed, focus on the list.
            string bad = Convert.ToBase64String(Template("Bina: ${ad}", "Yer: ${konum | map:\"\"}"));
            await Browser.EvaluateAsync<bool>($$"""
                (() => {
                  const bytes = Uint8Array.from(atob('{{bad}}'), c => c.charCodeAt(0));
                  const carried = new DataTransfer();
                  carried.items.add(new File([bytes], 'harita.docx'));
                  const input = document.getElementById('formReportFile'); input.files = carried.files;
                  return true;
                })()
                """);
            await ClickAsync("#formReportUpload");
            await WaitForAsync("document.querySelectorAll('.formreportissues li').length === 1 && document.activeElement?.id === 'formReportIssuesHead'",
                "A refused template did not list its placeholder and take focus to the list.");
            string issue = await Browser.EvaluateAsync<string>("document.querySelector('.formreportissues li').textContent") ?? string.Empty;
            Assert.Contains("paragraph 2", issue, StringComparison.Ordinal);
            Assert.Contains("map", issue, StringComparison.Ordinal);
            Assert.StartsWith("Not added", await Browser.EvaluateAsync<string>("document.getElementById('formReportSays').textContent"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{formId}?folder=hosted&drop=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{formId}_submit?folder=hosted&drop=true");
            (int status, string body) = await AdminAsync(HttpMethod.Get, "/admin/domains");

            if (status == 200 && JsonNode.Parse(body) is { } domains)
            {
                foreach (JsonNode? domain in (domains["domains"] as JsonArray ?? []).Where(d => ((string?)d?["name"])?.StartsWith(formId, StringComparison.Ordinal) == true))
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/domains/{(string?)domain!["id"]}");
                }
            }

            if (item is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{item}");
            }
        }
    }
}
