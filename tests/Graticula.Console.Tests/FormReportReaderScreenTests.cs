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
/// Owner decision 2026-11-17: a member who may open a form and does not manage it sees its Results — the records they may
/// read, and nothing that manages the form — and makes feature reports of them (ADR-301). Signed in as a real member made
/// here, against the live fixture: first through the form's Inbox, which gives them only the records its filter names
/// them in, then with the Inbox turned off, when the form's answers are not theirs to read and the page says so.
/// </summary>
public sealed class FormReportReaderScreenTests : ConsoleTest
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

    private static JsonObject Sheet(string name, string[] columns, params string[][] rows) => new()
    {
        ["name"] = name,
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]),
        ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]),
    };

    private async Task<(int Status, string Body)> AsAsync(string? token, HttpMethod method, string path, object? body = null)
    {
        using HttpRequestMessage request = new(method, new Uri($"{Root}{path}"));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A member with the user role, signed in with the password they chose: a real reader who is not the owner.</summary>
    private async Task<string> MemberAsync(string name)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members", JsonSerializer.Serialize(new { name, role = "user" }));
        Assert.True(made == 201, $"Making {name} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;

        (int s, string b) = await AsAsync(null, HttpMethod.Post, "/rest/auth/login", new { name, password = issued });
        Assert.True(s == 200, $"Signing in as {name} answered {s}.");
        string first = JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;

        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        (s, _) = await AsAsync(first, HttpMethod.Post, "/rest/auth/password", new { currentPassword = issued, newPassword = chosen });
        Assert.True(s is >= 200 and < 300, $"Replacing {name}'s issued password answered {s}.");

        (s, b) = await AsAsync(null, HttpMethod.Post, "/rest/auth/login", new { name, password = chosen });
        Assert.True(s == 200, $"Signing in again as {name} answered {s}.");
        return JsonDocument.Parse(b).RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>What a reader must not be offered on a form's page: the controls that manage the form.</summary>
    private const string ManagementControls =
        "['formStateHead', 'formPublishOpen', 'formInboxSave', 'formInboxOn', 'formReportUpload', 'formReportFile', 'formExportGo',"
        + " 'docShareOpen', 'formDesign', 'formDownload'].filter(id => !!document.getElementById(id))"
        + ".concat(document.querySelector('[data-form-report-remove]') ? ['data-form-report-remove'] : [])";

    [Fact]
    public async Task A_member_who_reads_a_form_sees_only_the_records_they_may_read_and_makes_a_report_of_them()
    {
        string run = Guid.NewGuid().ToString("N")[..8];
        string formId = "zz_frread_" + run;
        string reader = "zz_frr_" + run;
        string? item = null;
        bool memberMade = false;

        try
        {
            JsonObject model = new()
            {
                ["format"] = "xlsform/1",
                ["sheets"] = new JsonArray(
                    Sheet("survey", ["type", "name", "label"], ["geopoint", "konum", "Konum"], ["text", "ad", "Bina adı"],
                        ["text", "atanan", "Atanan"], ["select_one hasar", "hasar", "Hasar"]),
                    Sheet("choices", ["list_name", "name", "label"], ["hasar", "yok", "Yok"], ["hasar", "agir", "Ağır"]),
                    Sheet("settings", ["form_title", "form_id"], ["Okur", formId])),
            };
            (int status, string body) = await AdminAsync(HttpMethod.Post, "/admin/forms",
                new JsonObject { ["title"] = "Okur " + formId, ["model"] = model }.ToJsonString());
            Assert.True(status == 201, $"Adding the form answered {status}: {body}");
            item = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

            (int published, string publication) = await AdminAsync(HttpMethod.Post, $"/admin/forms/{item}/publish", "{}");
            Assert.True(published == 200, $"Publishing answered {published}: {publication}");
            (int shared, string sharing) = await AdminAsync(HttpMethod.Put, $"/admin/forms/{item}/sharing", "{\"sharing\":\"organization\"}");
            Assert.True(shared == 200, $"Sharing answered {shared}: {sharing}");
            JsonElement top = JsonDocument.Parse(publication).RootElement.GetProperty("layers").EnumerateArray()
                .First(l => l.GetProperty("repeat").GetString() is null or "");
            string url = top.GetProperty("url").GetString()!;
            JsonElement columns = top.GetProperty("columns");
            string Column(string question) => columns.GetProperty(question).GetString()!;

            string readerToken = await MemberAsync(reader);
            memberMade = true;

            // The Inbox gives each member the records whose Atanan names them: two of the three are this reader's.
            (int inboxed, string inbox) = await AdminAsync(HttpMethod.Put, $"/admin/forms/{item}/inbox",
                JsonSerializer.Serialize(new { enabled = true, filter = Column("atanan") + " = ${username}", limit = 1000 }));
            Assert.True(inboxed == 200, $"Turning on the Inbox answered {inboxed}: {inbox}");

            (string token, _) = await SignInAsync();

            using (HttpRequestMessage edit = new(HttpMethod.Post, new Uri($"{Root}{url}/applyEdits")))
            {
                JsonArray adds = [.. new[] { ("Kule", reader, 29.0), ("Saray", reader, 29.01), ("Han", "baskasi", 29.02) }.Select(r => (JsonNode)new JsonObject
                {
                    ["geometry"] = new JsonObject { ["x"] = r.Item3, ["y"] = 41.0 },
                    ["attributes"] = new JsonObject { [Column("ad")] = r.Item1, [Column("atanan")] = r.Item2, [Column("hasar")] = "agir" },
                })];
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

            // --- Through the Inbox: their own two, said to be theirs, and nothing that manages the form.
            await OpenAsync($"/studio/#/item/{item}", readerToken);
            await WaitForAsync("(document.getElementById('formResultsSource')?.textContent || '').includes('Inbox')"
                + " && document.querySelectorAll('#formLatest tbody tr').length === 2 && !!document.getElementById('formReportTemplate')",
                "A reader's Results did not show their own submissions through the Inbox with a report template.");

            string latest = await Browser.EvaluateAsync<string>("document.getElementById('formLatest').textContent") ?? string.Empty;
            Assert.Contains("Kule", latest, StringComparison.Ordinal);
            Assert.Contains("Saray", latest, StringComparison.Ordinal);
            Assert.DoesNotContain("Han", latest, StringComparison.Ordinal);
            Assert.StartsWith("2 submissions", (await Browser.EvaluateAsync<string>("document.getElementById('formResultCount').textContent") ?? string.Empty).Trim(), StringComparison.Ordinal);
            // The Inbox's filter may pick a member's own submissions or assign them others': the sentence must be true of both.
            Assert.Equal("Only the submissions this form's Inbox gives you, not everyone's. Its owner decides which those are.",
                await Browser.EvaluateAsync<string>("document.getElementById('formResultsSource').textContent"));

            // The summary counts what they may read, not the form's three.
            Assert.Contains("2", await Browser.EvaluateAsync<string>(
                "document.querySelector('.formsum[data-question=\"hasar\"] tbody tr:nth-child(2) .num').textContent"), StringComparison.Ordinal);

            Assert.Equal("[]", await Browser.EvaluateAsync<string>($"JSON.stringify({ManagementControls})"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('docForm').hidden"), "The Form panel is shown to a reader.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#formResultCount a')"),
                "A reader seeing the Inbox's records is sent to the form's whole layer in Data.");

            // A filter narrows what they read, and what they read only.
            await Browser.EvaluateAsync<bool>("(document.getElementById('formFilterQ').value = 'ad', document.getElementById('formFilterQ').dispatchEvent(new Event('change', { bubbles: true })), true)");
            await WaitForAsync("!!document.getElementById('formFilterV')", "Choosing a question did not offer a value box.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('formFilterV').value = 'kule', true)");
            await ClickAsync("#formFilter button[type=submit]");
            await WaitForAsync("document.querySelectorAll('#formLatest tbody tr').length === 1"
                + " && (document.getElementById('formResultCount').textContent || '').includes('1 of 2')",
                "The filter did not narrow the reader's two records to one.");
            await ClickAsync("#formFilterClear");
            await WaitForAsync("document.querySelectorAll('#formLatest tbody tr').length === 2", "Clearing the filter did not bring both back.");

            // A report of the records they ticked, with the form's template.
            await ClickAsync("#formPickAll");
            await WaitForAsync("document.getElementById('formReportPicked').textContent === '2 submissions chosen.'", "Ticking every row did not count two.");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/reports'], true)");
            await ClickAsync("#formReportGo");
            await WaitForAsync("/\\.(zip|docx) \\(.*\\) is downloading/.test(document.getElementById('formReportSays').textContent)",
                "A reader's Create report did not download.");
            Assert.Contains(await WritesAsync(), w => w.StartsWith($"POST /admin/forms/{item}/reports", StringComparison.Ordinal) && w.EndsWith("(sent)", StringComparison.Ordinal));

            NothingWentWrong(await PageErrorsAsync());

            // --- An Inbox whose filter gives this reader nothing: the form has answers, so the page must not say it has none.
            (inboxed, inbox) = await AdminAsync(HttpMethod.Put, $"/admin/forms/{item}/inbox",
                JsonSerializer.Serialize(new { enabled = true, filter = Column("atanan") + " = 'nobody_" + run + "'", limit = 1000 }));
            Assert.True(inboxed == 200, $"Narrowing the Inbox answered {inboxed}: {inbox}");

            await OpenAsync($"/studio/#/item/{item}", readerToken);
            await WaitForAsync("!!document.getElementById('formResultsNone')", "An Inbox giving the reader nothing drew no empty state.");
            Assert.Equal("The Inbox gives you no submissions yet. When one is meant for you, it appears here.",
                await Browser.EvaluateAsync<string>("document.getElementById('formResultsNone').textContent"));
            Assert.Equal("[]", await Browser.EvaluateAsync<string>($"JSON.stringify({ManagementControls})"));
            NothingWentWrong(await PageErrorsAsync());

            // --- No Inbox and no access to the layer: the page says whose the answers are, and shows no empty table.
            (inboxed, inbox) = await AdminAsync(HttpMethod.Put, $"/admin/forms/{item}/inbox",
                JsonSerializer.Serialize(new { enabled = false, filter = Column("atanan") + " = ${username}", limit = 1000 }));
            Assert.True(inboxed == 200, $"Turning off the Inbox answered {inboxed}: {inbox}");

            await OpenAsync($"/studio/#/item/{item}", readerToken);
            await WaitForAsync("(document.getElementById('formResultsSource')?.textContent || '').includes('not shared with you')",
                "A reader who may read none of the answers was not told so.");
            // Said to the reader, with what they can do and whom to ask — not the owner's settings in the owner's words.
            Assert.Equal("You cannot see this form's answers: they are not shared with you. To see them, ask its owner, "
                + Environment.GetEnvironmentVariable(UserVariable) + ", to share its results with you.",
                await Browser.EvaluateAsync<string>("document.getElementById('formResultsSource').textContent"));
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('formLatest') || !!document.getElementById('formReports')"),
                "A reader who may read none of the answers is shown a table or Create report.");
            Assert.Equal("[]", await Browser.EvaluateAsync<string>($"JSON.stringify({ManagementControls})"));
            NothingWentWrong(await PageErrorsAsync());

            // --- The owner shares the results layer itself: every submission, said to be everyone's, and still no management.
            (int opened, string why) = await AdminAsync(HttpMethod.Put, $"/admin/services/{formId}/sharing?folder=hosted", "{\"sharing\":\"organization\"}");
            Assert.True(opened is >= 200 and < 300, $"Sharing the results layer answered {opened}: {why}");

            await OpenAsync($"/studio/#/item/{item}", readerToken);
            await WaitForAsync("(document.getElementById('formResultsSource')?.textContent || '').startsWith('Every submission')"
                + " && document.querySelectorAll('#formLatest tbody tr').length === 3",
                "A reader the results layer is shared with did not see every submission.");
            Assert.Contains("Han", await Browser.EvaluateAsync<string>("document.getElementById('formLatest').textContent"), StringComparison.Ordinal);
            // The export is offered as the export endpoint allows it: Extract is off unless chosen, so not to this reader.
            Assert.Equal("[]", await Browser.EvaluateAsync<string>($"JSON.stringify({ManagementControls})"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            // The views first: a service with views over it is not dropped.
            foreach (string service in new[] { formId + "_inbox", formId + "_submit", formId })
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}?folder=hosted&drop=true");
            }

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

            if (memberMade)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/members/{reader}");
            }
        }
    }
}
