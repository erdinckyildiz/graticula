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
                // Two, so the tick box for every row shown has a mixed state to show.
                JsonArray adds = [.. new[] { ("Kule", 29.0), ("Saray", 29.01) }.Select(r => (JsonNode)new JsonObject
                {
                    ["geometry"] = new JsonObject { ["x"] = r.Item2, ["y"] = 41.0 },
                    ["attributes"] = new JsonObject { [columns.GetProperty("ad").GetString()!] = r.Item1, [columns.GetProperty("hasar").GetString()!] = "agir" },
                })];
                edit.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["adds"] = adds.ToJsonString() });
                edit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answer = await Http.SendAsync(edit);
                Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
            }

            // The second draws a map, which may leave something out — a basemap at another address — and say so.
            foreach ((string name, byte[] bytes) in new[] { ("denetim.docx", Template("Bina: ${ad}", "Hasar: ${hasar}")), ("konum.docx", Template("Bina: ${ad}", "${konum}")) })
            {
                using MultipartFormDataContent form = new();
                form.Add(new ByteArrayContent(bytes), "file", name);
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

            // UX review 2026-11-17: Create report sits under the table it is chosen in, before the summaries, not after them.
            Assert.True(await Browser.EvaluateAsync<bool>(
                "!!(document.getElementById('formLatest').compareDocumentPosition(document.getElementById('formReports')) & Node.DOCUMENT_POSITION_FOLLOWING)"
                + " && !!(document.getElementById('formReports').compareDocumentPosition(document.querySelector('.formsums')) & Node.DOCUMENT_POSITION_FOLLOWING)"),
                "The Reports section is not between Latest submissions and the summaries.");

            // Not yet available looks it, and the count is a live region, so a tick is heard as a number.
            Assert.True(await Browser.EvaluateAsync<bool>("Number(getComputedStyle(document.getElementById('formReportGo')).opacity) < 1"),
                "Create report looks available with nothing chosen.");
            Assert.Equal("status", await Browser.EvaluateAsync<string>("document.getElementById('formReportPicked').getAttribute('role')"));

            // The header box chooses every row shown; one row off leaves it mixed.
            await ClickAsync("#formPickAll");
            await WaitForAsync("document.getElementById('formReportPicked').textContent === '2 submissions chosen.'"
                + " && document.getElementById('formReportGo').textContent === 'Create 2 reports' && !document.getElementById('formReportModeRow').hidden",
                "Choosing every submission shown did not count them.");
            await ClickAsync("[data-form-pick]");
            await WaitForAsync("document.getElementById('formPickAll').indeterminate && !document.getElementById('formPickAll').checked"
                + " && document.getElementById('formReportGo').getAttribute('aria-disabled') === null"
                + " && document.getElementById('formReportPicked').textContent === '1 submission chosen.'"
                + " && Number(getComputedStyle(document.getElementById('formReportGo')).opacity) === 1",
                "One row off did not leave the header box mixed and one submission chosen.");

            // Create report reaches the server and the document comes back to be downloaded.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/reports'], true)");
            await ClickAsync("#formReportGo");
            await WaitForAsync("/\\.docx \\(.*\\) is downloading/.test(document.getElementById('formReportSays').textContent)",
                "Create report did not download a Word document.");
            Assert.Contains(await WritesAsync(), w => w.StartsWith($"POST /admin/forms/{item}/reports", StringComparison.Ordinal) && w.EndsWith("(sent)", StringComparison.Ordinal));
            Assert.Equal("formReportGo", await Browser.EvaluateAsync<string>("document.activeElement?.id"));

            // What the report left out is shown, not only sent in a header: the same request is asked here, and whatever
            // notes the server gives it are the ones the screen lists (the fixture's basemap decides whether there are any).
            long chosen = await Browser.EvaluateAsync<long>("[...formHere.reportPick][0]");
            string notes;
            using (HttpRequestMessage made = new(HttpMethod.Post, new Uri($"{Root}/admin/forms/{item}/reports")))
            {
                made.Content = new StringContent(new JsonObject { ["template"] = "konum.docx", ["objectIds"] = new JsonArray(chosen) }.ToJsonString(), Encoding.UTF8, "application/json");
                made.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answer = await Http.SendAsync(made);
                Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync());
                notes = Uri.UnescapeDataString(answer.Headers.TryGetValues("Graticula-Report-Notes", out IEnumerable<string>? said) ? string.Concat(said) : string.Empty);
            }

            await Browser.EvaluateAsync<bool>("(document.getElementById('formReportTemplate').value = 'konum.docx', document.getElementById('formReportSays').textContent = '', true)");
            await ClickAsync("#formReportGo");
            await WaitForAsync("/is downloading/.test(document.getElementById('formReportSays').textContent) && !document.getElementById('formReportGo').hasAttribute('aria-busy')",
                "The map report did not download.");
            int listed = await Browser.EvaluateAsync<int>("document.querySelectorAll('#formReportNotes li').length");
            Assert.True(notes.Length == 0 ? listed == 0 : listed > 0, $"The server noted \"{notes}\" and the screen listed {listed}.");

            // The splitting itself, whatever this fixture's basemap is: two sentences are two lines.
            Assert.Equal(2, await Browser.EvaluateAsync<int>(
                "formReportNotesShown(encodeURIComponent(\"A map in the report: 'OpenStreetMap' was not drawn: it is at another address. The web map x is not there for you to read, so the map was drawn on the default basemap.\"))"));
            Assert.Contains("Check the report", await Browser.EvaluateAsync<string>("document.getElementById('formReportNotesHead').textContent"), StringComparison.Ordinal);

            // A template with a rotated map is refused, its paragraph listed, focus on the list.
            string bad = Convert.ToBase64String(Template("Bina: ${ad}", "Yer: ${konum | rotate:90}"));
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
            Assert.StartsWith("harita.docx was not added", await Browser.EvaluateAsync<string>("document.getElementById('formReportSays').textContent"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('formReportSays').classList.contains('bad-inline')"), "The refusal is not shown as one.");
            Assert.Equal(0, await Browser.EvaluateAsync<int>("document.querySelectorAll('#formReportNotes li').length"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            // The view first: a base service with views is refused (409), which left it and its domains behind.
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{formId}_submit?folder=hosted&drop=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{formId}?folder=hosted&drop=true");
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
