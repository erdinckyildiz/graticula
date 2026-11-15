using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.17 against a real server and PostGIS: a form whose settings sheet has <c>submission_url</c> writes to that
/// layer and makes none, refuses what does not match with the row and the column, changes nothing in the layer when it is
/// published again, and leaves the service standing when the form is deleted.
/// </summary>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AFormThatNamesALayerWritesToItTests(OneStartedNode started)
{
    private TwoNodeSite.Node Node => started.Node;

    private static JsonArray Row(params string[] cells) => new([.. cells.Select(c => (JsonNode?)c)]);

    private static JsonObject Model(string formId, string? url, params string[][] survey) => new()
    {
        ["format"] = "xlsform/1",
        ["sheets"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "survey",
                ["columns"] = Row("type", "name", "label"),
                ["rows"] = new JsonArray([.. survey.Select(r => (JsonNode?)Row(r))]),
            },
            new JsonObject
            {
                ["name"] = "choices",
                ["columns"] = Row("list_name", "name", "label"),
                ["rows"] = new JsonArray(Row("hasar", "yok", "None"), Row("hasar", "agir", "Heavy")),
            },
            new JsonObject
            {
                ["name"] = "settings",
                ["columns"] = url is null ? Row("form_title", "form_id") : Row("form_title", "form_id", "submission_url"),
                ["rows"] = new JsonArray(url is null ? Row("Form " + formId, formId) : Row("Form " + formId, formId, url)),
            }),
    };

    private static readonly string[][] Questions =
    [
        ["geopoint", "yer", "Place"],
        ["text", "ad", "Name"],
        ["integer", "kat", "Floor"],
        ["select_one hasar", "hasar", "Damage"],
        ["image", "foto", "Photo"],
        ["begin repeat", "odalar", "Rooms"],
        ["text", "oda", "Room"],
        ["end repeat"],
    ];

    private async Task<(int Status, JsonNode? Body)> CallAsync(HttpMethod method, string path, string token, object? body = null)
    {
        (int status, string text) = await Node.SendAsync(method, path, token, body);
        return (status, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task<string> AddFormAsync(string token, JsonObject model, string formId)
    {
        (int status, JsonNode? body) = await CallAsync(HttpMethod.Post, "/admin/forms", token, new { title = "Form " + formId, model });
        Assert.True(status == 201, $"Adding the form answered {status}: {body}");
        return (string)body!["id"]!;
    }

    private static string Details(JsonNode? body) =>
        string.Join("\n", (body?["error"]?["details"] as JsonArray ?? []).Select(d => (string?)d));

    [Fact]
    public async Task A_form_that_names_a_layer_writes_to_it_makes_none_and_refuses_what_does_not_match()
    {
        string token = await Node.SignInAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];

        {
            // ---- a layer somebody else made: another form's own, with a related table, attachments and GlobalIDs ----
            string first = "zz_target_" + suffix;
            string target = await AddFormAsync(token, Model(first, null, Questions), first);
            (int madeStatus, JsonNode? made) = await CallAsync(HttpMethod.Post, $"/admin/forms/{target}/publish", token, new { });
            Assert.True(madeStatus == 200, $"Publishing the target form answered {madeStatus}: {made}");

            string service = (string)made!["service"]!;
            JsonObject topLayer = made["layers"]!.AsArray().Select(l => l!.AsObject()).First(l => (string)l["repeat"]! == string.Empty);
            JsonObject roomsLayer = made["layers"]!.AsArray().Select(l => l!.AsObject()).First(l => (string)l["repeat"]! == "odalar");
            string topUrl = (string)topLayer["url"]!;

            JsonNode before = (await CallAsync(HttpMethod.Get, $"{topUrl}?f=json", token)).Body!;
            JsonNode roomsBefore = (await CallAsync(HttpMethod.Get, $"{(string)roomsLayer["url"]!}?f=json", token)).Body!;

            // ---- the form that names it, by an absolute address of this server ----
            string second = "zz_names_" + suffix;
            string absolute = new Uri(Node.Http.BaseAddress!, topUrl).ToString();
            string named = await AddFormAsync(token, Model(second, absolute, Questions), second);
            (int status, JsonNode? publication) = await CallAsync(HttpMethod.Post, $"/admin/forms/{named}/publish", token, new { });
            Assert.True(status == 200, $"Publishing the form that names a layer answered {status}: {publication}");

            Assert.True((bool)publication!["existing"]!);
            Assert.Equal(service, (string)publication["service"]!);
            Assert.Equal(topUrl, (string)publication["layers"]![0]!["url"]!);
            Assert.Equal((string)roomsLayer["url"]!, (string)publication["layers"]![1]!["url"]!);
            Assert.Equal((int)roomsLayer["relationshipId"]!, (int)publication["layers"]![1]!["relationshipId"]!);
            Assert.Equal("ad", (string)publication["layers"]![0]!["columns"]!["ad"]!);

            // It made no service of its own, and the target's schema is exactly what it was.
            (int ownStatus, _) = await CallAsync(HttpMethod.Get, $"/rest/services/hosted/{second}/FeatureServer?f=json", token);
            Assert.NotEqual(200, ownStatus);
            Assert.Equal(before["fields"]!.ToJsonString(), (await CallAsync(HttpMethod.Get, $"{topUrl}?f=json", token)).Body!["fields"]!.ToJsonString());
            Assert.Equal(roomsBefore["fields"]!.ToJsonString(), (await CallAsync(HttpMethod.Get, $"{(string)roomsLayer["url"]!}?f=json", token)).Body!["fields"]!.ToJsonString());

            // The form's item points at the existing service.
            (_, JsonNode? read) = await CallAsync(HttpMethod.Get, $"/admin/forms/{named}", token);
            Assert.Contains($"/FeatureServer", (string?)read!["publication"]!["featureServer"], StringComparison.Ordinal);

            // ---- sharing builds the Create-only view over the existing service, as it does over a new one ----
            (int shared, JsonNode? sharing) = await CallAsync(HttpMethod.Put, $"/admin/forms/{named}/sharing", token, new { sharing = "organization" });
            Assert.True(shared == 200, $"Sharing answered {shared}: {sharing}");
            string view = (string)sharing!["publication"]!["submitService"]!;
            Assert.Equal(service + "_submit", view);
            string submitUrl = (string)sharing["publication"]!["layers"]![0]!["submitUrl"]!;

            string record = Guid.NewGuid().ToString("B").ToUpperInvariant();
            using (HttpRequestMessage add = new(HttpMethod.Post, $"{submitUrl}/addFeatures"))
            {
                add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                add.Content = new FormUrlEncodedContent(
                [
                    KeyValuePair.Create("features", $$$"""[{"geometry":{"x":29.0,"y":41.0,"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{record}}}","ad":"Bina","kat":2,"hasar":"agir"}}]"""),
                    KeyValuePair.Create("f", "json"),
                ]);
                using HttpResponseMessage sent = await Node.Http.SendAsync(add);
                string sentBody = await sent.Content.ReadAsStringAsync();
                Assert.True(sent.IsSuccessStatusCode && JsonNode.Parse(sentBody)!["addResults"]![0]!["success"]!.GetValue<bool>(), $"The answer was refused: {sentBody}");
            }

            JsonNode count = (await CallAsync(HttpMethod.Get, $"{topUrl}/query?where=1%3D1&returnCountOnly=true&f=json", token)).Body!;
            Assert.Equal(1, (int)count["count"]!);

            // ---- publishing again changes nothing in the layer, and cannot be pointed elsewhere ----
            (int again, JsonNode? againBody) = await CallAsync(HttpMethod.Post, $"/admin/forms/{named}/publish", token, new { });
            Assert.True(again == 200, $"Publishing again answered {again}: {againBody}");
            Assert.Equal(0, (int)againBody!["fieldsAdded"]!);
            Assert.Equal(view, (string)againBody["submitService"]!);
            Assert.Equal(before["fields"]!.ToJsonString(), (await CallAsync(HttpMethod.Get, $"{topUrl}?f=json", token)).Body!["fields"]!.ToJsonString());

            // ---- every mismatch is refused with its row and column, and nothing is made ----
            string third = "zz_extra_" + suffix;
            string[][] extra = [.. Questions, ["text", "yeni_soru", "New"]];
            string extraForm = await AddFormAsync(token, Model(third, topUrl, extra), third);
            (int refused, JsonNode? refusal) = await CallAsync(HttpMethod.Post, $"/admin/forms/{extraForm}/publish", token, new { });
            Assert.Equal(400, refused);
            Assert.Contains("survey row 10, name", Details(refusal), StringComparison.Ordinal);
            Assert.Contains("yeni_soru", Details(refusal), StringComparison.Ordinal);
            Assert.Null((await CallAsync(HttpMethod.Get, $"/admin/forms/{extraForm}", token)).Body!["publication"]);
            Assert.Equal(before["fields"]!.ToJsonString(), (await CallAsync(HttpMethod.Get, $"{topUrl}?f=json", token)).Body!["fields"]!.ToJsonString());

            string[][] wrongType = [["geopoint", "yer", "Place"], ["decimal", "ad", "Name"], ["integer", "kat", "Floor"], ["select_one hasar", "hasar", "Damage"]];
            string typeForm = await AddFormAsync(token, Model("zz_type_" + suffix, topUrl, wrongType), "zz_type_" + suffix);
            (_, JsonNode? typeRefusal) = await CallAsync(HttpMethod.Post, $"/admin/forms/{typeForm}/publish", token, new { });
            Assert.Contains("survey row 3, type", Details(typeRefusal), StringComparison.Ordinal);

            string[][] noRepeat = [["geopoint", "yer", "Place"], ["text", "ad", "Name"], ["begin repeat", "garajlar", "Garages"], ["text", "plaka", "Plate"], ["end repeat"]];
            string repeatForm = await AddFormAsync(token, Model("zz_repeat_" + suffix, topUrl, noRepeat), "zz_repeat_" + suffix);
            (_, JsonNode? repeatRefusal) = await CallAsync(HttpMethod.Post, $"/admin/forms/{repeatForm}/publish", token, new { });
            Assert.Contains("survey row 4, name", Details(repeatRefusal), StringComparison.Ordinal);
            Assert.Contains("garajlar", Details(repeatRefusal), StringComparison.Ordinal);

            // ---- an outside server, a missing service and a layer that is not this server's are refused at settings row 2 ----
            foreach ((string url, string said) in new[]
            {
                ("https://elsewhere.example.com/rest/services/hosted/" + service + "/FeatureServer/0", "elsewhere.example.com"),
                ($"/rest/services/hosted/no_such_{suffix}/FeatureServer/0", "no feature service"),
                ($"/rest/services/hosted/{service}/FeatureServer/9", "no layer 9"),
            })
            {
                string bad = await AddFormAsync(token, Model("zz_bad_" + Guid.NewGuid().ToString("N")[..6], url, Questions), "zz_bad");
                (int badStatus, JsonNode? badBody) = await CallAsync(HttpMethod.Post, $"/admin/forms/{bad}/publish", token, new { });
                Assert.Equal(400, badStatus);
                Assert.Contains("settings row 2, submission_url", Details(badBody), StringComparison.Ordinal);
                Assert.Contains(said, Details(badBody), StringComparison.Ordinal);
            }

            // ---- an address that is not a feature layer's is refused already when the form is added ----
            (int notAdded, JsonNode? notAddedBody) = await CallAsync(HttpMethod.Post, "/admin/forms", token,
                new { title = "x", model = Model("zz_map_" + suffix, $"/rest/services/hosted/{service}/MapServer/0", Questions) });
            Assert.Equal(400, notAdded);
            Assert.Contains("settings row 2, submission_url", Details(notAddedBody), StringComparison.Ordinal);

            // ---- a form already published cannot stop naming its layer ----
            JsonNode model = (await CallAsync(HttpMethod.Get, $"/admin/forms/{named}", token)).Body!["model"]!;
            model["sheets"]![2]!["rows"]![0]![2] = string.Empty;
            (int saved, JsonNode? savedBody) = await CallAsync(HttpMethod.Put, $"/admin/forms/{named}/model", token, model);
            Assert.True(saved == 200, $"Saving answered {saved}: {savedBody}");
            (int moved, JsonNode? movedBody) = await CallAsync(HttpMethod.Post, $"/admin/forms/{named}/publish", token, new { });
            Assert.Equal(400, moved);
            Assert.Contains("submission_url", Details(movedBody), StringComparison.Ordinal);

            // ---- deleting the form takes the form item and leaves the service it names ----
            (int deleted, _) = await CallAsync(HttpMethod.Delete, $"/content/documents/{named}", token);
            Assert.InRange(deleted, 200, 299);
            (int still, _) = await CallAsync(HttpMethod.Get, $"{topUrl}?f=json", token);
            Assert.Equal(200, still);
            Assert.Equal(1, (int)(await CallAsync(HttpMethod.Get, $"{topUrl}/query?where=1%3D1&returnCountOnly=true&f=json", token)).Body!["count"]!);
        }
    }

    [Fact]
    public async Task A_layer_in_another_reference_takes_a_form_s_answers()
    {
        string token = await Node.SignInAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string name = "zz_web_" + suffix;

        // A layer designed by hand, in Web Mercator (the default), which a hosted layer made through define already has GlobalIDs for.
        (int defined, JsonNode? design) = await CallAsync(HttpMethod.Post, "/admin/hosted/define", token, new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "ad", type = "text", nullable = true }, new { name = "kat", type = "integer", nullable = true } },
        });
        Assert.True(defined is >= 200 and < 300, $"Defining answered {defined}: {design}");
        string url = (string)design!["services"]!["feature"]!;

        string[][] questions = [["geopoint", "yer", "Place"], ["text", "ad", "Name"], ["integer", "kat", "Floor"]];

        string with = await AddFormAsync(token, Model("zz_withglobal_" + suffix, url, questions), "zz_withglobal");
        (int status, JsonNode? publication) = await CallAsync(HttpMethod.Post, $"/admin/forms/{with}/publish", token, new { });
        Assert.True(status == 200, $"Publishing answered {status}: {publication}");

        string record = Guid.NewGuid().ToString("B").ToUpperInvariant();
        using HttpRequestMessage add = new(HttpMethod.Post, $"{url}/addFeatures");
        add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        add.Content = new FormUrlEncodedContent(
        [
            KeyValuePair.Create("features", $$$"""[{"geometry":{"x":29.0,"y":41.0,"spatialReference":{"wkid":4326}},"attributes":{"globalid":"{{{record}}}","ad":"Bina","kat":2}}]"""),
            KeyValuePair.Create("f", "json"),
        ]);
        using HttpResponseMessage sent = await Node.Http.SendAsync(add);
        string sentBody = await sent.Content.ReadAsStringAsync();
        Assert.True(sent.IsSuccessStatusCode && JsonNode.Parse(sentBody)!["addResults"]![0]!["success"]!.GetValue<bool>(), sentBody);

        JsonNode found = (await CallAsync(HttpMethod.Get, $"{url}/query?where=1%3D1&outSR=4326&outFields=ad&f=json", token)).Body!;
        Assert.Equal(29.0, (double)found["features"]![0]!["geometry"]!["x"]!, 3);
    }
}
