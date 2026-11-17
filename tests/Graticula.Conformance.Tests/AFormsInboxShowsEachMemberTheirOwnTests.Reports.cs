using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
/// ADR-301 §5.5: a feature report is made of the records its asker may read, through the layer — the owner's from the
/// form's own layer, a member's through the Inbox, which the database filters per reader — and a template is checked
/// against the form when it is uploaded, by whoever manages the form only.
/// </summary>
public sealed partial class AFormsInboxShowsEachMemberTheirOwnTests
{
    /// <summary>A template written here: the answer, the repeat's rows as paragraphs, and the photograph's name.</summary>
    private static byte[] ReportTemplate(params string[] paragraphs)
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
                + string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"))
                + "<w:sectPr/></w:body></w:document>");
        }

        return output.ToArray();
    }

    /// <summary>The text of a filled document's paragraphs, joined by '|'.</summary>
    private static string ReportText(byte[] docx)
    {
        using ZipArchive zip = new(new MemoryStream(docx), ZipArchiveMode.Read);
        using Stream part = zip.GetEntry("word/document.xml")!.Open();
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join("|", System.Xml.Linq.XDocument.Load(part).Descendants(w + "p")
            .Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
    }

    private async Task<(HttpStatusCode Status, string Body)> UploadTemplateAsync(string root, string token, string item, string name, byte[] docx)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(docx);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        form.Add(file, "file", name);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/admin/forms/{item}/reports/templates") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, byte[] Body, string? Type, string? FileName)> ReportAsync(string root, string token, string item, object request)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, $"{root}/admin/forms/{item}/reports")
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(message);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType,
            (response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName)?.Trim('"'));
    }

    [Fact]
    public async Task A_feature_report_is_made_of_the_records_its_asker_may_read_from_a_template_checked_at_upload()
    {
        InboxForm? f = null;
        string root = await RequireServerAsync();
        string run = Guid.NewGuid().ToString("N")[..6];

        try
        {
            f = await SetUpAsync(root, run, "organization");

            // Refused at upload, with the paragraph and the reason, and nothing kept.
            (HttpStatusCode refused, string refusedBody) = await UploadTemplateAsync(root, f.Admin, f.Item, "bad.docx",
                ReportTemplate("Name: ${ad}", "Where: ${konum | rotate:90}", "${nosuch}"));
            Assert.True(refused == HttpStatusCode.BadRequest, $"A template with a rotated map was answered {(int)refused}: {Head(refusedBody)}");
            JsonElement issues = JsonDocument.Parse(refusedBody).RootElement.GetProperty("issues");
            Assert.Equal(2, issues.GetArrayLength());
            Assert.Equal(2, issues[0].GetProperty("paragraph").GetInt32());
            Assert.Contains("map", issues[0].GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal(3, issues[1].GetProperty("paragraph").GetInt32());

            // A member may not add one: the form is not theirs.
            (HttpStatusCode byMember, _) = await UploadTemplateAsync(root, f.A.Token, f.Item, "mine.docx", ReportTemplate("${ad}"));
            Assert.Equal(HttpStatusCode.NotFound, byMember);

            byte[] template = ReportTemplate("Name: ${ad}", "${#katlar}", "Floor ${kat_no}", "${/}", "Photo: ${foto | getValue:\"name\"}");
            (HttpStatusCode added, string addedBody) = await UploadTemplateAsync(root, f.Admin, f.Item, "inspection.docx", template);
            Assert.True(added == HttpStatusCode.Created, $"The template was answered {(int)added}: {Head(addedBody)}");

            (HttpStatusCode listed, string listBody) = await RequestAsync(HttpMethod.Get, $"{root}/admin/forms/{f.Item}/reports/templates", f.A.Token, null);
            Assert.True(listed == HttpStatusCode.OK && listBody.Contains("inspection.docx", StringComparison.Ordinal), $"A member's list: {(int)listed} {Head(listBody)}");

            // The owner: both records, one document each, zipped, from the form's own layer.
            var both = await ReportAsync(root, f.Admin, f.Item, new { template = "inspection.docx", objectIds = new[] { f.A.ObjectId, f.B.ObjectId } });
            Assert.True(both.Status == HttpStatusCode.OK, $"The owner's report answered {(int)both.Status}: {Head(Encoding.UTF8.GetString(both.Body))}");
            Assert.Equal("application/zip", both.Type);
            using (ZipArchive zip = new(new MemoryStream(both.Body), ZipArchiveMode.Read))
            {
                Assert.Equal(2, zip.Entries.Count);
                List<string> texts = [.. zip.Entries.Select(e =>
                {
                    using MemoryStream copy = new();
                    e.Open().CopyTo(copy);
                    return ReportText(copy.ToArray());
                })];
                Assert.Contains($"Name: {f.A.Name}|Floor 3|Photo: foto.png", texts);
                Assert.Contains($"Name: {f.B.Name}|Floor 3|Photo: foto.png", texts);
            }

            // Merged: one document, both records.
            var merged = await ReportAsync(root, f.Admin, f.Item, new { template = "inspection.docx", objectIds = new[] { f.A.ObjectId, f.B.ObjectId }, mode = "merged", name = "All" });
            Assert.Equal(HttpStatusCode.OK, merged.Status);
            Assert.Equal("All.docx", merged.FileName);
            string mergedText = ReportText(merged.Body);
            Assert.Contains(f.A.Name, mergedText, StringComparison.Ordinal);
            Assert.Contains(f.B.Name, mergedText, StringComparison.Ordinal);

            // A member: their own record through the Inbox, named by its answer; the other's is not there for them.
            var own = await ReportAsync(root, f.A.Token, f.Item, new { template = "inspection.docx", objectIds = new[] { f.A.ObjectId }, name = "Report ${ad}" });
            Assert.True(own.Status == HttpStatusCode.OK, $"A's own report answered {(int)own.Status}: {Head(Encoding.UTF8.GetString(own.Body))}");
            Assert.Equal($"Report {f.A.Name}.docx", own.FileName);
            Assert.Equal($"Name: {f.A.Name}|Floor 3|Photo: foto.png", ReportText(own.Body));

            var other = await ReportAsync(root, f.A.Token, f.Item, new { template = "inspection.docx", objectIds = new[] { f.B.ObjectId } });
            string otherBody = Encoding.UTF8.GetString(other.Body);
            Assert.True(other.Status == HttpStatusCode.NotFound && !otherBody.Contains(f.B.Name, StringComparison.Ordinal),
                $"A asked for B's record and was answered {(int)other.Status}: {Head(otherBody)}");

            // PDF is refused in words, not offered and failed.
            var pdf = await ReportAsync(root, f.Admin, f.Item, new { template = "inspection.docx", objectIds = new[] { f.A.ObjectId }, format = "pdf" });
            Assert.Equal(HttpStatusCode.BadRequest, pdf.Status);
            Assert.Contains("PDF", Encoding.UTF8.GetString(pdf.Body), StringComparison.Ordinal);

            // Removed by the owner; then there is nothing to report with.
            (HttpStatusCode removed, _) = await RequestAsync(HttpMethod.Delete, $"{root}/admin/forms/{f.Item}/reports/templates/inspection.docx", f.Admin, null);
            Assert.Equal(HttpStatusCode.NoContent, removed);
            var gone = await ReportAsync(root, f.Admin, f.Item, new { template = "inspection.docx", objectIds = new[] { f.A.ObjectId } });
            Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        }
        finally
        {
            await TearDownAsync(root, run, f);
        }
    }
}
