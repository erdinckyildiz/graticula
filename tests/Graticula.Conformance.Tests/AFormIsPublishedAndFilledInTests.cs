using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-287 §5.1–§5.3: a form is added as an XLSForm model, downloaded as a workbook, published to a layer with a related
/// layer and a related table, and its answers are stored and related through the FeatureServer as any client stores them.
/// </summary>
[Collection("catalogue walk")]
public sealed class AFormIsPublishedAndFilledInTests : ArcGisClient
{
    private static JsonObject Model(string formId) => new()
    {
        ["format"] = "xlsform/1",
        ["sheets"] = new JsonArray(
            Sheet("survey", ["type", "name", "label::Türkçe (tr)", "label::English (en)", "required", "relevant"],
                ["geopoint", "konum", "Konum", "Location", "yes"],
                ["text", "bina_adı", "Bina adı", "Building name", "yes"],
                ["select_one hasar", "hasar", "Hasar", "Damage"],
                ["image", "foto", "Fotoğraf", "Photo", "", "${hasar} != 'yok'"],
                ["begin repeat", "katlar", "Katlar", "Floors"],
                ["integer", "kat_no", "Kat", "Floor"],
                ["begin repeat", "odalar", "Odalar", "Rooms"],
                ["text", "oda", "Oda", "Room"],
                ["end repeat"],
                ["end repeat"],
                ["begin repeat", "agaclar", "Ağaçlar", "Trees"],
                ["geopoint", "agac_yeri", "Yeri", "Place"],
                ["end repeat"]),
            Sheet("choices", ["list_name", "name", "label::Türkçe (tr)", "label::English (en)"],
                ["hasar", "yok", "Yok", "None"],
                ["hasar", "agir", "Ağır", "Heavy"]),
            Sheet("settings", ["form_title", "form_id", "default_language"],
                ["Bina denetimi", formId, "Türkçe (tr)"])),
    };

    private static JsonObject Sheet(string name, string[] columns, params string[][] rows) => new()
    {
        ["name"] = name,
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)c)]),
        ["rows"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]),
    };

    [Fact]
    public async Task A_form_is_added_published_and_its_answers_are_stored_and_related()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string formId = "zz_form_" + Guid.NewGuid().ToString("N")[..8];
        string? item = null;

        try
        {
            (HttpStatusCode added, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
                new JsonObject { ["title"] = "Bina denetimi " + formId, ["model"] = Model(formId) }.ToJsonString());
            Assert.True(added == HttpStatusCode.Created, $"Adding the form answered {(int)added}: {addBody}");
            item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;

            (HttpStatusCode read, string readBody) = await RequestAsync(HttpMethod.Get, $"{root}/admin/forms/{item}", token!, null);
            Assert.True(read == HttpStatusCode.OK, $"Reading the form answered {(int)read}: {readBody}");
            JsonElement stored = JsonDocument.Parse(readBody).RootElement;
            Assert.Equal(0, stored.GetProperty("refusals").GetArrayLength());
            Assert.Equal("bina_adı", stored.GetProperty("model").GetProperty("sheets")[0].GetProperty("rows")[1][1].GetString());

            using (HttpRequestMessage download = new(HttpMethod.Get, $"{root}/admin/forms/{item}/workbook"))
            {
                download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage book = await Http.SendAsync(download);
                byte[] bytes = await book.Content.ReadAsByteArrayAsync();
                Assert.Equal(HttpStatusCode.OK, book.StatusCode);
                Assert.Equal((byte)'P', bytes[0]);
                Assert.Equal((byte)'K', bytes[1]);
            }

            (HttpStatusCode published, string publication) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publication}");
            JsonElement layers = JsonDocument.Parse(publication).RootElement.GetProperty("layers");
            Assert.Equal(4, layers.GetArrayLength());
            JsonElement top = layers[0];
            JsonElement floors = layers.EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "katlar");
            JsonElement rooms = layers.EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "odalar");
            JsonElement trees = layers.EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "agaclar");
            Assert.Equal("Point", top.GetProperty("geometryType").GetString());
            Assert.Equal(JsonValueKind.Null, floors.GetProperty("geometryType").ValueKind);
            Assert.Equal("Point", trees.GetProperty("geometryType").GetString());
            Assert.Equal("katlar", rooms.GetProperty("parentRepeat").GetString());
            Assert.Equal("bina_adi", top.GetProperty("columns").GetProperty("bina_adı").GetString());

            // Publishing again with nothing changed changes nothing (§5.13): no field is added, nothing is refused.
            (HttpStatusCode again, string againBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(again == HttpStatusCode.OK, $"Publishing an unchanged form again answered {(int)again}: {againBody}");
            Assert.Equal(0, JsonDocument.Parse(againBody).RootElement.GetProperty("fieldsAdded").GetInt32());

            // A building, a floor of it, and a room of the floor, as a client sends them: by GlobalID.
            string building = Guid.NewGuid().ToString("B").ToUpperInvariant();
            string floor = Guid.NewGuid().ToString("B").ToUpperInvariant();
            string room = Guid.NewGuid().ToString("B").ToUpperInvariant();

            await AddAsync(root, token!, top.GetProperty("url").GetString()!, $$$"""
                [{"geometry":{"x":29.0,"y":41.0,"spatialReference":{"wkid":4326}},
                  "attributes":{"globalid":"{{{building}}}","bina_adi":"Ağır hasarlı yapı","hasar":"agir"}}]
                """);
            await AddAsync(root, token!, floors.GetProperty("url").GetString()!, $$$"""
                [{"attributes":{"globalid":"{{{floor}}}","parentglobalid":"{{{building}}}","kat_no":3}}]
                """);
            await AddAsync(root, token!, rooms.GetProperty("url").GetString()!, $$$"""
                [{"attributes":{"globalid":"{{{room}}}","parentglobalid":"{{{floor}}}","oda":"Mutfak"}}]
                """);

            // The choice list is the field's domain, and the value outside it is refused by the layer, not by the form.
            (HttpStatusCode layerStatus, string layerBody) = await RequestAsync(HttpMethod.Get, $"{root}{top.GetProperty("url").GetString()}?f=json", token!, null);
            Assert.Equal(HttpStatusCode.OK, layerStatus);
            JsonElement damage = JsonDocument.Parse(layerBody).RootElement.GetProperty("fields").EnumerateArray()
                .First(f => f.GetProperty("name").GetString() == "hasar");
            Assert.Equal("codedValue", damage.GetProperty("domain").GetProperty("type").GetString());
            Assert.Equal("Hasar", damage.GetProperty("alias").GetString());
            Assert.True(JsonDocument.Parse(layerBody).RootElement.GetProperty("hasAttachments").GetBoolean());

            int relationship = floors.GetProperty("relationshipId").GetInt32();
            (HttpStatusCode relatedStatus, string related) = await RequestAsync(HttpMethod.Get,
                $"{root}{top.GetProperty("url").GetString()}/queryRelatedRecords?objectIds=1&relationshipId={relationship}&outFields=*&f=json", token!, null);
            Assert.True(relatedStatus == HttpStatusCode.OK, $"queryRelatedRecords answered {(int)relatedStatus}: {related}");
            JsonElement group = JsonDocument.Parse(related).RootElement.GetProperty("relatedRecordGroups")[0];
            Assert.Equal(3, group.GetProperty("relatedRecords")[0].GetProperty("attributes").GetProperty("kat_no").GetInt32());

            // §5.2: a photo says which question it answers, and an attachment query can ask by it.
            string topUrl = top.GetProperty("url").GetString()!;

            using (MultipartFormDataContent photo = new())
            {
                photo.Add(new StringContent("json"), "f");
                photo.Add(new StringContent("foto"), "keywords");
                ByteArrayContent png = new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
                png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                photo.Add(png, "attachment", "foto.png");
                using HttpRequestMessage attach = new(HttpMethod.Post, $"{root}{topUrl}/1/addAttachment") { Content = photo };
                attach.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage attached = await Http.SendAsync(attach);
                string attachBody = await attached.Content.ReadAsStringAsync();
                Assert.True(attached.StatusCode == HttpStatusCode.OK, $"addAttachment answered {(int)attached.StatusCode}: {attachBody}");
            }

            (HttpStatusCode listed, string list) = await RequestAsync(HttpMethod.Get, $"{root}{topUrl}/1/attachments?f=json", token!, null);
            Assert.True(listed == HttpStatusCode.OK, $"attachments answered {(int)listed}: {list}");
            Assert.Equal("foto", JsonDocument.Parse(list).RootElement.GetProperty("attachmentInfos")[0].GetProperty("keywords").GetString());

            (HttpStatusCode byWord, string found) = await RequestAsync(HttpMethod.Get, $"{root}{topUrl}/queryAttachments?objectIds=1&keywords=foto&f=json", token!, null);
            (HttpStatusCode byOther, string none) = await RequestAsync(HttpMethod.Get, $"{root}{topUrl}/queryAttachments?objectIds=1&keywords=imza&f=json", token!, null);
            Assert.True(byWord == HttpStatusCode.OK, $"queryAttachments by keywords answered {(int)byWord}: {found}");
            Assert.Equal(1, JsonDocument.Parse(found).RootElement.GetProperty("attachmentGroups").GetArrayLength());
            Assert.Equal(HttpStatusCode.OK, byOther);
            Assert.Equal(0, JsonDocument.Parse(none).RootElement.GetProperty("attachmentGroups").GetArrayLength());

            // The client's form: the tree with groups and repeats, the choices, and where each answer goes.
            (HttpStatusCode clientStatus, string client) = await RequestAsync(HttpMethod.Get, $"{root}/forms/{item}/form.json", token!, null);
            Assert.True(clientStatus == HttpStatusCode.OK, $"The client's form answered {(int)clientStatus}: {client}");
            JsonElement form = JsonDocument.Parse(client).RootElement.GetProperty("form");
            JsonElement body = form.GetProperty("root").GetProperty("body");
            JsonElement floorsNode = body.EnumerateArray().First(n => n.GetProperty("name").GetString() == "katlar");
            Assert.Equal("repeat", floorsNode.GetProperty("kind").GetString());
            Assert.Equal("odalar", floorsNode.GetProperty("body")[1].GetProperty("name").GetString());
            Assert.Equal("Ağır", form.GetProperty("choices").GetProperty("hasar")[1].GetProperty("labels").GetProperty("Türkçe (tr)").GetString());

            // A private form is not served to a caller who cannot read the item.
            using HttpRequestMessage anonymous = new(HttpMethod.Get, $"{root}/forms/{item}/form.json");
            using HttpResponseMessage refused = await Http.SendAsync(anonymous);
            Assert.True(refused.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                || (await refused.Content.ReadAsStringAsync()).Contains("\"error\"", StringComparison.Ordinal),
                $"An anonymous read of a private form answered {(int)refused.StatusCode}.");
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", token!, null);
            await DeleteDomainsAsync(root, token!, formId);

            if (item is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{item}", token!, null);
            }
        }
    }

    [Fact]
    public async Task A_public_form_takes_answers_from_anyone_through_a_view_that_shows_none()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string formId = "zz_form_" + Guid.NewGuid().ToString("N")[..8];
        string? item = null;
        string? folderId = null;

        try
        {
            (_, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
                new JsonObject { ["title"] = "Açık anket " + formId, ["model"] = Model(formId) }.ToJsonString());
            item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;
            (HttpStatusCode published, string publishBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publishBody}");

            (HttpStatusCode shared, string sharedBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/sharing", token!, """{"sharing":"public"}""");
            Assert.True(shared == HttpStatusCode.OK, $"Sharing answered {(int)shared}: {sharedBody}");

            // <b>Survey123's arrangement — owner decision 2026-11-09, ADR-287 §5.12.</b> The form, its layer named after
            // it and its answers view named after it with _form, all in the owner's Survey-<title> folder, as the portal
            // lists them to Studio and to Pro; and no vector tile item for the view, which serves no tiles (Q-231).
            folderId = await FormFolderAsync(root, token!, "Survey-Açık anket " + formId);
            Assert.False(folderId is null, $"No folder 'Survey-Açık anket {formId}' among the owner's folders.");
            (HttpStatusCode listed, string listing) = await RequestAsync(HttpMethod.Get,
                $"{root}/sharing/rest/content/users/{Uri.EscapeDataString(Environment.GetEnvironmentVariable(UserVariable)!)}/{folderId}?f=json&num=100", token!, null);
            Assert.Equal(HttpStatusCode.OK, listed);
            (string Title, string Type, string Url)[] filed = [.. JsonDocument.Parse(listing).RootElement.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("title").GetString() ?? "", i.GetProperty("type").GetString() ?? "",
                    i.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : ""))];
            string said = string.Join("; ", filed.Select(f => $"{f.Type} '{f.Title}' {f.Url}"));
            Assert.True(filed.Any(f => f.Type == "Form" && f.Title == "Açık anket " + formId), $"The form is not in its folder: {said}");
            Assert.True(filed.Any(f => f.Type == "Feature Service" && f.Title == "Açık anket " + formId && f.Url.EndsWith($"/{formId}/FeatureServer", StringComparison.Ordinal)),
                $"The form's layer is not in its folder under the form's title: {said}");
            Assert.True(filed.Any(f => f.Type == "Feature Service" && f.Title == $"Açık anket {formId}_form" && f.Url.Contains($"/{formId}_submit/", StringComparison.Ordinal)),
                $"The answers view is not in the folder as '<title>_form': {said}");
            Assert.False(filed.Any(f => f.Type == "Vector Tile Service" && f.Url.Contains("_submit", StringComparison.Ordinal)),
                $"The answers view has a vector tile item: {said}");

            using HttpRequestMessage open = new(HttpMethod.Get, $"{root}/forms/{item}/form.json");
            using HttpResponseMessage opened = await Http.SendAsync(open);
            string client = await opened.Content.ReadAsStringAsync();
            Assert.True(opened.StatusCode == HttpStatusCode.OK, $"An anonymous read of a public form answered {(int)opened.StatusCode}: {client}");
            JsonElement layers = JsonDocument.Parse(client).RootElement.GetProperty("publication").GetProperty("layers");
            string top = layers[0].GetProperty("submitUrl").GetString()!;
            string floors = layers.EnumerateArray().First(l => l.GetProperty("repeat").GetString() == "katlar").GetProperty("submitUrl").GetString()!;
            Assert.EndsWith("_submit/FeatureServer/0", top, StringComparison.Ordinal);

            string building = Guid.NewGuid().ToString("B").ToUpperInvariant();
            int objectId = await AnonymousAddAsync(root, top, $$$"""
                [{"geometry":{"x":32.85,"y":39.93,"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{building}}}","bina_adi":"Anonim","hasar":"yok"}}]
                """);
            await AnonymousAddAsync(root, floors, $$$"""
                [{"attributes":{"globalid":"{{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}}","parentglobalid":"{{{building}}}","kat_no":1}}]
                """);

            using (MultipartFormDataContent photo = new())
            {
                photo.Add(new StringContent("json"), "f");
                photo.Add(new StringContent("foto"), "keywords");
                ByteArrayContent png = new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
                png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                photo.Add(png, "attachment", "foto.png");
                using HttpRequestMessage attach = new(HttpMethod.Post,
                    $"{root}{top}/{objectId}/addAttachment?parentGlobalId={Uri.EscapeDataString(building)}") { Content = photo };
                using HttpResponseMessage attached = await Http.SendAsync(attach);
                string attachBody = await attached.Content.ReadAsStringAsync();
                Assert.True(attached.StatusCode == HttpStatusCode.OK && attachBody.Contains("\"success\":true", StringComparison.Ordinal),
                    $"An anonymous photo through the view answered {(int)attached.StatusCode}: {attachBody}");
            }

            // 2026-11-16: the view shows no feature, so it protects none by its filter; a stranger who counts object ids
            // and cannot name the feature's GlobalID attaches nothing to somebody else's submission.
            foreach (string stranger in (string[])["", $"?parentGlobalId={Uri.EscapeDataString(Guid.NewGuid().ToString("B"))}"])
            {
                using MultipartFormDataContent junk = new();
                junk.Add(new StringContent("json"), "f");
                ByteArrayContent bytes = new([1, 2, 3]);
                bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                junk.Add(bytes, "attachment", "junk.bin");
                using HttpRequestMessage attach = new(HttpMethod.Post, $"{root}{top}/{objectId}/addAttachment{stranger}") { Content = junk };
                using HttpResponseMessage attached = await Http.SendAsync(attach);
                string attachBody = await attached.Content.ReadAsStringAsync();
                Assert.False(attachBody.Contains("\"success\":true", StringComparison.Ordinal),
                    $"A stranger's attachment through the view{stranger} answered {(int)attached.StatusCode}: {attachBody}");
            }

            using HttpRequestMessage peek = new(HttpMethod.Get, $"{root}{top}/query?where=1%3D1&outFields=*&f=json");
            using HttpResponseMessage peeked = await Http.SendAsync(peek);
            string peekBody = await peeked.Content.ReadAsStringAsync();
            Assert.False(peeked.StatusCode == HttpStatusCode.OK && peekBody.Contains("Anonim", StringComparison.Ordinal),
                $"The public view showed an answer: {peekBody}");

            // Found 2026-11-09 on dev: the view's query refused, and its tiles, its renderer and its documents' extent
            // still gave the answers and the point away. Every face of the view is asked, not only the query.
            string viewService = top[..top.LastIndexOf("/FeatureServer/", StringComparison.Ordinal)];
            string[] faces =
            [
                $"{viewService}/VectorTileServer/tile/0/0/0.pbf",
                $"{viewService}/VectorTileServer?f=json",
                $"{top}/generateRenderer?classificationDef=%7B%22type%22%3A%22uniqueValueDef%22%2C%22uniqueValueFields%22%3A%5B%22bina_adi%22%5D%7D&f=json",
                $"{viewService}/FeatureServer?f=json",
                $"{top}?f=json",
                $"{viewService}/MapServer?f=json",
            ];
            foreach (string face in faces)
            {
                using HttpRequestMessage look = new(HttpMethod.Get, $"{root}{face}");
                using HttpResponseMessage looked = await Http.SendAsync(look);
                string seen = System.Text.Encoding.Latin1.GetString(await looked.Content.ReadAsByteArrayAsync());
                Assert.False(seen.Contains("Anonim", StringComparison.Ordinal) || seen.Contains("32.85", StringComparison.Ordinal)
                    || seen.Contains("39.93", StringComparison.Ordinal),
                    $"The public view's {face} answered {(int)looked.StatusCode} with what was submitted: {seen[..Math.Min(seen.Length, 400)]}");
            }

            // Found 2026-11-16 reading the code for the Inbox: a view's attachments are its source's (ADR-113, 2026-11-09),
            // and the attachment faces asked neither the view's Query nor its filter, so anybody who could add through the
            // public view could list and download every submission's photographs by counting object ids.
            string[] attachmentFaces =
            [
                $"{top}/{objectId}/attachments?f=json",
                $"{top}/queryAttachments?objectIds={objectId}&f=json",
                $"{top}/queryAttachments?definitionExpression=1%3D1&f=json",
                $"{top}/{objectId}/attachments/1",
            ];
            foreach (string face in attachmentFaces)
            {
                using HttpRequestMessage look = new(HttpMethod.Get, $"{root}{face}");
                using HttpResponseMessage looked = await Http.SendAsync(look);
                string seen = System.Text.Encoding.Latin1.GetString(await looked.Content.ReadAsByteArrayAsync());
                Assert.False(looked.StatusCode == HttpStatusCode.OK && (seen.Contains("foto", StringComparison.Ordinal) || seen.StartsWith("\u0089PNG", StringComparison.Ordinal)),
                    $"The public view's {face} answered {(int)looked.StatusCode} with the submission's photograph: {seen[..Math.Min(seen.Length, 400)]}");
            }

            (HttpStatusCode owned, string ownBody) = await RequestAsync(HttpMethod.Get,
                $"{root}{layers[0].GetProperty("url").GetString()}/query?where=1%3D1&outFields=bina_adi&f=json", token!, null);
            Assert.True(owned == HttpStatusCode.OK && ownBody.Contains("Anonim", StringComparison.Ordinal), $"The owner's layer answered {(int)owned}: {ownBody}");

            // Published again with a new question, the answers view is made again and takes the new field from anyone (§5.13).
            JsonObject changed = Model(formId);
            ((JsonArray)changed["sheets"]![0]!["rows"]!).Insert(2, new JsonArray("text", "not", "Not", "Note"));
            (HttpStatusCode draft, string draftBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/model", token!, changed.ToJsonString());
            Assert.True(draft == HttpStatusCode.OK, $"Saving the draft answered {(int)draft}: {draftBody}");
            (HttpStatusCode again, string againBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(again == HttpStatusCode.OK, $"Publishing a shared form again answered {(int)again}: {againBody}");
            string topAgain = JsonDocument.Parse(againBody).RootElement.GetProperty("layers")[0].GetProperty("submitUrl").GetString()!;
            await AnonymousAddAsync(root, topAgain, $$$"""
                [{"geometry":{"x":32.8,"y":39.9,"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}}","bina_adi":"Yeni","hasar":"yok","not":"yeni alan"}}]
                """);

            (HttpStatusCode closed, _) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/sharing", token!, """{"sharing":"private"}""");
            Assert.Equal(HttpStatusCode.OK, closed);
            using HttpRequestMessage gone = new(HttpMethod.Get, $"{root}/forms/{item}/form.json");
            using HttpResponseMessage goneAnswer = await Http.SendAsync(gone);
            Assert.NotEqual(HttpStatusCode.OK, goneAnswer.StatusCode);
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}_submit?folder=hosted&drop=true", token!, null);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", token!, null);
            await DeleteDomainsAsync(root, token!, formId);

            if (item is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{item}", token!, null);
            }

            folderId ??= await FormFolderAsync(root, token!, "Survey-Açık anket " + formId);

            if (folderId is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/folders/{Guid.Parse(folderId)}", token!, null);
            }
        }
    }

    /// <summary>The id of the signed-in member's folder of this title, as the portal writes it, or null.</summary>
    private async Task<string?> FormFolderAsync(string root, string token, string title)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get,
            $"{root}/sharing/rest/content/users/{Uri.EscapeDataString(Environment.GetEnvironmentVariable(UserVariable)!)}?f=json&num=1", token, null);

        return status != HttpStatusCode.OK || !JsonDocument.Parse(body).RootElement.TryGetProperty("folders", out JsonElement folders)
            ? null
            : folders.EnumerateArray().Where(f => f.GetProperty("title").GetString() == title)
                .Select(f => f.GetProperty("id").GetString()).FirstOrDefault();
    }

    [Fact]
    public async Task A_form_shared_before_it_is_published_takes_answers_once_it_is()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string formId = "zz_form_" + Guid.NewGuid().ToString("N")[..8];
        string? item = null;

        try
        {
            (_, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
                new JsonObject { ["title"] = "Önce paylaşılan " + formId, ["model"] = Model(formId) }.ToJsonString());
            item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;
            (HttpStatusCode shared, string sharedBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/sharing", token!, """{"sharing":"public"}""");
            Assert.True(shared == HttpStatusCode.OK, $"Sharing an unpublished form answered {(int)shared}: {sharedBody}");

            (HttpStatusCode published, string publication) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(published == HttpStatusCode.OK, $"Publishing a shared form answered {(int)published}: {publication}");

            using HttpRequestMessage open = new(HttpMethod.Get, $"{root}/forms/{item}/form.json");
            using HttpResponseMessage opened = await Http.SendAsync(open);
            string client = await opened.Content.ReadAsStringAsync();
            Assert.True(opened.StatusCode == HttpStatusCode.OK, $"An anonymous read answered {(int)opened.StatusCode}: {client}");
            string top = JsonDocument.Parse(client).RootElement.GetProperty("publication").GetProperty("layers")[0].GetProperty("submitUrl").GetString()!;
            await AnonymousAddAsync(root, top, $$$"""
                [{"geometry":{"x":27.14,"y":38.42,"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}}","bina_adi":"Sonra yayımlanan","hasar":"agir"}}]
                """);

            (HttpStatusCode checkedStatus, string plan) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/check", token!,
                new JsonObject { ["model"] = Model(formId) }.ToJsonString());
            Assert.Equal(HttpStatusCode.OK, checkedStatus);
            Assert.Equal(formId, JsonDocument.Parse(plan).RootElement.GetProperty("plan").GetProperty("service").GetString());
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}_submit?folder=hosted&drop=true", token!, null);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", token!, null);
            await DeleteDomainsAsync(root, token!, formId);

            if (item is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{item}", token!, null);
            }
        }
    }

    /// <summary>
    /// A published form changed in the designer is a draft until it is published again; then a new question is a new
    /// field, a question taken out keeps its answers, and a question whose type changed is refused — ADR-287 §5.13.
    /// </summary>
    [Fact]
    public async Task A_published_form_is_changed_as_a_draft_and_published_again_as_Survey123_does()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string formId = "zz_form_" + Guid.NewGuid().ToString("N")[..8];
        string? item = null;
        JsonObject First() => new()
        {
            ["format"] = "xlsform/1",
            ["sheets"] = new JsonArray(
                Sheet("survey", ["type", "name", "label"], ["text", "ad", "Ad"], ["integer", "yas", "Yaş"]),
                Sheet("settings", ["form_title", "form_id", "version"], ["Değişen", formId, "1"])),
        };

        try
        {
            (_, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
                new JsonObject { ["title"] = "Değişen " + formId, ["model"] = First() }.ToJsonString());
            item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;
            (HttpStatusCode published, string publication) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publication}");
            string layer = JsonDocument.Parse(publication).RootElement.GetProperty("layers")[0].GetProperty("url").GetString()!;
            await AddAsync(root, token!, layer, $$$"""[{"attributes":{"globalid":"{{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}}","ad":"Eski","yas":40}}]""");

            // The draft: yas taken out, a new question, a label changed, the version moved.
            JsonObject second = new()
            {
                ["format"] = "xlsform/1",
                ["sheets"] = new JsonArray(
                    Sheet("survey", ["type", "name", "label"], ["text", "ad", "Adı soyadı"], ["text", "telefon", "Telefon"]),
                    Sheet("settings", ["form_title", "form_id", "version"], ["Değişen", formId, "2"])),
            };
            (HttpStatusCode saved, string savedBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/model", token!, second.ToJsonString());
            Assert.True(saved == HttpStatusCode.OK, $"Saving a published form's draft answered {(int)saved}: {savedBody}");
            Assert.True(JsonDocument.Parse(savedBody).RootElement.GetProperty("changedSincePublished").GetBoolean());

            // Until it is published again the client is served the published version.
            using (HttpRequestMessage open = new(HttpMethod.Get, $"{root}/forms/{item}/form.json"))
            {
                open.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!);
                using HttpResponseMessage opened = await Http.SendAsync(open);
                string client = await opened.Content.ReadAsStringAsync();
                Assert.Contains("\"yas\"", client, StringComparison.Ordinal);
                Assert.DoesNotContain("telefon", client, StringComparison.Ordinal);
            }

            (HttpStatusCode again, string againBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(again == HttpStatusCode.OK, $"Publishing again answered {(int)again}: {againBody}");
            JsonElement republished = JsonDocument.Parse(againBody).RootElement;
            Assert.Equal(1, republished.GetProperty("fieldsAdded").GetInt32());
            JsonElement columns = republished.GetProperty("layers")[0].GetProperty("columns");
            Assert.Equal("telefon", columns.GetProperty("telefon").GetString());
            Assert.False(columns.TryGetProperty("yas", out _), "A question taken out is still sent to.");

            // The answer given before stays, in the field the form no longer asks; the new field is there, empty.
            (HttpStatusCode read, string rows) = await RequestAsync(HttpMethod.Get, $"{root}{layer}/query?where=1%3D1&outFields=ad,yas,telefon&f=json", token!, null);
            Assert.True(read == HttpStatusCode.OK && rows.Contains("\"yas\":40", StringComparison.Ordinal) && rows.Contains("\"telefon\"", StringComparison.Ordinal),
                $"After publishing again the layer answered {(int)read}: {rows}");

            // A type cannot change: ad as a number is refused, by name, and nothing changes.
            JsonObject third = new()
            {
                ["format"] = "xlsform/1",
                ["sheets"] = new JsonArray(
                    Sheet("survey", ["type", "name", "label"], ["integer", "ad", "Ad"], ["text", "telefon", "Telefon"]),
                    Sheet("settings", ["form_title", "form_id", "version"], ["Değişen", formId, "3"])),
            };
            await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/model", token!, third.ToJsonString());
            (HttpStatusCode refused, string refusedBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True((int)refused is >= 400 and < 500 && refusedBody.Contains("keeps its type", StringComparison.Ordinal),
                $"Changing a published field's type answered {(int)refused}: {refusedBody}");
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", token!, null);
            await DeleteDomainsAsync(root, token!, formId);

            if (item is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{item}", token!, null);
            }
        }
    }

    /// <summary>
    /// A filtered export takes the submissions the filter keeps and, under them, their repeats' rows and no others —
    /// ADR-107 §8, owner decision 2026-11-09.
    /// </summary>
    [Fact]
    public async Task A_filtered_export_takes_the_submissions_it_keeps_with_their_repeats_and_nothing_else()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string formId = "zz_form_" + Guid.NewGuid().ToString("N")[..8];
        string? item = null;

        try
        {
            (_, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
                new JsonObject { ["title"] = "Süzülen " + formId, ["model"] = Model(formId) }.ToJsonString());
            item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;
            (HttpStatusCode published, string publication) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publication}");
            JsonElement layers = JsonDocument.Parse(publication).RootElement.GetProperty("layers");
            string Url(string? repeat) => (repeat is null ? layers[0] : layers.EnumerateArray()
                .First(l => l.TryGetProperty("repeat", out JsonElement r) && r.ValueKind == JsonValueKind.String && r.GetString() == repeat))
                .GetProperty("url").GetString()!;
            int Id(string url) => int.Parse(url[(url.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

            // Two buildings, one heavily damaged, each with a floor and a room on it.
            foreach (string damage in (string[])["agir", "yok"])
            {
                string building = Guid.NewGuid().ToString("B").ToUpperInvariant();
                string floor = Guid.NewGuid().ToString("B").ToUpperInvariant();
                await AddAsync(root, token!, Url(null), $$$"""
                    [{"geometry":{"x":29.0,"y":41.0,"spatialReference":{"wkid":4326}},
                      "attributes":{"globalid":"{{{building}}}","bina_adi":"{{{damage}}}","hasar":"{{{damage}}}"}}]
                    """);
                await AddAsync(root, token!, Url("katlar"), $$$"""
                    [{"attributes":{"globalid":"{{{floor}}}","parentglobalid":"{{{building}}}","kat_no":1}}]
                    """);
                await AddAsync(root, token!, Url("odalar"), $$$"""
                    [{"attributes":{"globalid":"{{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}}","parentglobalid":"{{{floor}}}","oda":"{{{damage}}}"}}]
                    """);
            }

            int top = Id(Url(null)), floors = Id(Url("katlar")), rooms = Id(Url("odalar")), trees = Id(Url("agaclar"));
            string ask = JsonSerializer.Serialize(new
            {
                format = "geojson",
                where = new Dictionary<string, string> { [top.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "hasar = 'agir'" },
            });

            // The dry run counts what the filter keeps on each layer, and says which layer each repeat follows.
            (HttpStatusCode dry, string dryBody) = await RequestAsync(HttpMethod.Post,
                $"{root}/admin/services/{formId}/data-exports?folder=hosted&dryRun=true", token!, ask);
            Assert.True(dry == HttpStatusCode.OK, $"The dry run answered {(int)dry}: {dryBody}");
            Dictionary<int, JsonElement> counted = JsonDocument.Parse(dryBody).RootElement.GetProperty("layers").EnumerateArray()
                .ToDictionary(l => l.GetProperty("id").GetInt32());
            Assert.Equal(1, counted[top].GetProperty("rows").GetInt64());
            Assert.Equal(1, counted[floors].GetProperty("rows").GetInt64());
            Assert.Equal(1, counted[rooms].GetProperty("rows").GetInt64());
            Assert.Equal(0, counted[trees].GetProperty("rows").GetInt64());
            Assert.Equal(top, counted[floors].GetProperty("follows").GetInt32());
            Assert.Equal(floors, counted[rooms].GetProperty("follows").GetInt32());

            // The export itself: the file holds the heavy building, its floor and its room, and nothing of the other.
            (HttpStatusCode started, string startBody) = await RequestAsync(HttpMethod.Post,
                $"{root}/admin/services/{formId}/data-exports?folder=hosted", token!, ask);
            Assert.True(started == HttpStatusCode.Accepted, $"Starting the export answered {(int)started}: {startBody}");
            string watch = JsonDocument.Parse(startBody).RootElement.GetProperty("watch").GetString()!;
            JsonElement done = default;

            for (int i = 0; i < 120; i++)
            {
                (_, string state) = await RequestAsync(HttpMethod.Get, root + watch, token!, null);
                done = JsonDocument.Parse(state).RootElement.Clone();
                string status = done.GetProperty("status").GetString()!;
                if (status is "done" or "failed" or "cancelled") break;
                await Task.Delay(500);
            }

            Assert.True(done.GetProperty("status").GetString() == "done", $"The export did not finish: {done}");
            Assert.Equal("hasar = 'agir'", done.GetProperty("where").GetProperty(top.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString());

            using HttpRequestMessage fetch = new(HttpMethod.Get, root + done.GetProperty("download").GetString());
            fetch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage file = await Http.SendAsync(fetch);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            using System.IO.Compression.ZipArchive zip = new(new System.IO.MemoryStream(await file.Content.ReadAsByteArrayAsync()));
            string all = string.Join("\n", zip.Entries.Select(e =>
            {
                using System.IO.StreamReader reader = new(e.Open());
                return reader.ReadToEnd();
            }));
            Assert.Contains("\"agir\"", all, StringComparison.Ordinal);
            Assert.DoesNotContain("\"yok\"", all, StringComparison.Ordinal);

            // A filter naming a column the layer does not have is refused with its reason, before anything starts.
            (HttpStatusCode wrong, string wrongBody) = await RequestAsync(HttpMethod.Post,
                $"{root}/admin/services/{formId}/data-exports?folder=hosted&dryRun=true", token!,
                JsonSerializer.Serialize(new { format = "csv", where = new Dictionary<string, string> { [top.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "nope = 1" } }));
            Assert.Equal(HttpStatusCode.BadRequest, wrong);
            Assert.Contains("could not be read", wrongBody, StringComparison.Ordinal);

            await RequestAsync(HttpMethod.Delete, root + watch, token!, null);
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}_submit?folder=hosted&drop=true", token!, null);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", token!, null);
            await DeleteDomainsAsync(root, token!, formId);

            if (item is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{item}", token!, null);
            }
        }
    }

    /// <summary>
    /// A form shared with a group is opened and answered by that group's members and by nobody else — ADR-287 §5.14.
    /// </summary>
    [Fact]
    public async Task A_form_shared_with_a_group_takes_answers_from_its_members_only()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string formId = "zz_form_" + Guid.NewGuid().ToString("N")[..8];
        string group = "zz_fgrp_" + Guid.NewGuid().ToString("N")[..6];
        const string Member = "zz_form_member";
        const string Outsider = "zz_form_outsider";
        const string Password = "Zz-form-group-9!";
        string? item = null;

        await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Member}?deleteOwned=true", token!, null);
        await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Outsider}?deleteOwned=true", token!, null);

        try
        {
            string memberToken = await NewMemberAsync(root, token!, Member, Password);
            string outsiderToken = await NewMemberAsync(root, token!, Outsider, Password);
            (HttpStatusCode made, string madeBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/groups", token!,
                JsonSerializer.Serialize(new { name = group, title = "Form group" }));
            Assert.True((int)made is >= 200 and < 300, $"Making the group answered {(int)made}: {madeBody}");
            await RequestAsync(HttpMethod.Put, $"{root}/admin/groups/{group}/members/{Member}", token!, JsonSerializer.Serialize(new { manager = false }));

            (_, string addBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
                new JsonObject { ["title"] = "Grup anketi " + formId, ["model"] = Model(formId) }.ToJsonString());
            item = JsonDocument.Parse(addBody).RootElement.GetProperty("id").GetString()!;
            (HttpStatusCode published, string publishBody) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.True(published == HttpStatusCode.OK, $"Publishing answered {(int)published}: {publishBody}");

            (HttpStatusCode refused, _) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/sharing", token!, """{"sharing":"group"}""");
            Assert.Equal(HttpStatusCode.BadRequest, refused);

            (HttpStatusCode shared, string sharedBody) = await RequestAsync(HttpMethod.Put, $"{root}/admin/forms/{item}/sharing", token!,
                JsonSerializer.Serialize(new { sharing = "group", groups = new[] { group } }));
            Assert.True(shared == HttpStatusCode.OK, $"Sharing with the group answered {(int)shared}: {sharedBody}");

            // The member opens it and answers through the view; the outsider cannot open it, nor add through the view.
            (HttpStatusCode opened, string client) = await RequestAsync(HttpMethod.Get, $"{root}/forms/{item}/form.json", memberToken, null);
            Assert.True(opened == HttpStatusCode.OK, $"The group's member could not open the form: {(int)opened} {client}");
            string top = JsonDocument.Parse(client).RootElement.GetProperty("publication").GetProperty("layers")[0].GetProperty("submitUrl").GetString()!;
            string adds = $$$"""[{"geometry":{"x":32.8,"y":39.9,"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}}","bina_adi":"Üye"}}]""";
            (HttpStatusCode memberAdd, string memberAddBody) = await AddAsAsync(root, memberToken, top, adds);
            Assert.True(memberAdd == HttpStatusCode.OK && memberAddBody.Contains("\"success\":true", StringComparison.Ordinal),
                $"The group's member could not send an answer: {(int)memberAdd} {memberAddBody}");

            (HttpStatusCode outsiderOpen, _) = await RequestAsync(HttpMethod.Get, $"{root}/forms/{item}/form.json", outsiderToken, null);
            Assert.NotEqual(HttpStatusCode.OK, outsiderOpen);
            (HttpStatusCode outsiderAdd, string outsiderAddBody) = await AddAsAsync(root, outsiderToken, top, adds.Replace("Üye", "Yabancı", StringComparison.Ordinal));
            Assert.False(outsiderAdd == HttpStatusCode.OK && outsiderAddBody.Contains("\"success\":true", StringComparison.Ordinal),
                $"Somebody outside the group sent an answer: {outsiderAddBody}");
        }
        finally
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}_submit?folder=hosted&drop=true", token!, null);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{formId}?folder=hosted&drop=true", token!, null);
            await DeleteDomainsAsync(root, token!, formId);

            if (item is not null)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/documents/{item}", token!, null);
            }

            await RequestAsync(HttpMethod.Delete, $"{root}/admin/groups/{group}", token!, null);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Member}?deleteOwned=true", token!, null);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/members/{Outsider}?deleteOwned=true", token!, null);
        }
    }

    /// <summary>A member made for a test, signed in with a password of the test's choosing; its token.</summary>
    private async Task<string> NewMemberAsync(string root, string token, string name, string password)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/members", token,
            JsonSerializer.Serialize(new { name, role = "user", userType = "creator" }));
        Assert.True((int)status is >= 200 and < 300, $"Making the member '{name}' answered {(int)status}: {body}");
        string generated = JsonDocument.Parse(body).RootElement.GetProperty("password").GetString()!;
        string first = await LoginAsync(root, name, generated);
        using HttpRequestMessage change = new(HttpMethod.Post, $"{root}/rest/auth/password");
        change.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", first);
        change.Content = new StringContent(JsonSerializer.Serialize(new { currentPassword = generated, newPassword = password }),
            System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage changed = await Http.SendAsync(change);
        Assert.True(changed.IsSuccessStatusCode, $"Setting '{name}''s password answered {(int)changed.StatusCode}: {await changed.Content.ReadAsStringAsync()}");
        return await LoginAsync(root, name, password);
    }

    private async Task<string> LoginAsync(string root, string name, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/rest/auth/login");
        request.Content = new StringContent(JsonSerializer.Serialize(new { name, password }), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Signing in as '{name}' answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
    }

    private async Task<(HttpStatusCode, string)> AddAsAsync(string root, string token, string layerUrl, string adds)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{layerUrl}/addFeatures");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["features"] = adds });
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> AnonymousAddAsync(string root, string layerUrl, string adds)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{layerUrl}/applyEdits")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["adds"] = adds,
                ["useGlobalIds"] = "true",
                ["rollbackOnFailure"] = "true",
            }),
        };
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"An anonymous applyEdits on {layerUrl} answered {(int)response.StatusCode}: {body}");
        JsonElement result = JsonDocument.Parse(body).RootElement.GetProperty("addResults")[0];
        Assert.True(result.GetProperty("success").GetBoolean(), $"An anonymous applyEdits on {layerUrl} did not add: {body}");
        return result.GetProperty("objectId").GetInt32();
    }

    [Fact]
    public async Task A_form_off_the_list_is_refused_with_its_rows_and_nothing_is_kept()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        JsonObject model = new()
        {
            ["format"] = "xlsform/1",
            ["sheets"] = new JsonArray(Sheet("survey", ["type", "name", "label", "relevant"],
                ["audit", "kod", "Kod"],
                ["text", "ad", "Ad", "pulldata('@javascript', 'f/g.js', 'go')"])),
        };

        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
            new JsonObject { ["title"] = "refused", ["model"] = model }.ToJsonString());

        Assert.True(status == HttpStatusCode.BadRequest, $"A refused form answered {(int)status}: {body}");
        JsonElement refusals = JsonDocument.Parse(body).RootElement.GetProperty("refusals");
        Assert.Contains(refusals.EnumerateArray(), r => r.GetProperty("row").GetInt32() == 2 && r.GetProperty("message").GetString()!.Contains("audit", StringComparison.Ordinal));
        Assert.Contains(refusals.EnumerateArray(), r => r.GetProperty("row").GetInt32() == 3 && r.GetProperty("message").GetString()!.Contains("not a script file", StringComparison.Ordinal));
    }

    private async Task AddAsync(string root, string token, string layerUrl, string adds)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{layerUrl}/applyEdits")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["adds"] = adds,
                ["useGlobalIds"] = "true",
                ["rollbackOnFailure"] = "true",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"applyEdits on {layerUrl} answered {(int)response.StatusCode}: {body}");
        JsonElement result = JsonDocument.Parse(body).RootElement.GetProperty("addResults")[0];
        Assert.True(result.GetProperty("success").GetBoolean(), $"applyEdits on {layerUrl} did not add: {body}");
    }

    private async Task DeleteDomainsAsync(string root, string token, string formId)
    {
        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Get, $"{root}/admin/domains", token, null);

        if (status != HttpStatusCode.OK)
        {
            return;
        }

        foreach (JsonElement domain in Objects(JsonDocument.Parse(body).RootElement)
            .Where(o => o.TryGetProperty("name", out JsonElement n) && n.GetString()?.StartsWith(formId, StringComparison.Ordinal) == true
                && o.TryGetProperty("id", out _)))
        {
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/domains/{domain.GetProperty("id").GetString()}", token, null);
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
}
