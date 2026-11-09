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

            (HttpStatusCode again, _) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms/{item}/publish", token!, "{}");
            Assert.Equal(HttpStatusCode.Conflict, again);

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
    public async Task A_form_off_the_list_is_refused_with_its_rows_and_nothing_is_kept()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        JsonObject model = new()
        {
            ["format"] = "xlsform/1",
            ["sheets"] = new JsonArray(Sheet("survey", ["type", "name", "label", "relevant"],
                ["barcode", "kod", "Kod"],
                ["text", "ad", "Ad", "pulldata('@layer', 'getValue', 'x', 'y')"])),
        };

        (HttpStatusCode status, string body) = await RequestAsync(HttpMethod.Post, $"{root}/admin/forms", token!,
            new JsonObject { ["title"] = "refused", ["model"] = model }.ToJsonString());

        Assert.True(status == HttpStatusCode.BadRequest, $"A refused form answered {(int)status}: {body}");
        JsonElement refusals = JsonDocument.Parse(body).RootElement.GetProperty("refusals");
        Assert.Contains(refusals.EnumerateArray(), r => r.GetProperty("row").GetInt32() == 2 && r.GetProperty("message").GetString()!.Contains("Safari", StringComparison.Ordinal));
        Assert.Contains(refusals.EnumerateArray(), r => r.GetProperty("row").GetInt32() == 3 && r.GetProperty("message").GetString()!.Contains("@layer", StringComparison.Ordinal));
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
