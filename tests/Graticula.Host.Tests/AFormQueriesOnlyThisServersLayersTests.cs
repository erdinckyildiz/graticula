using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-287 §5.23 against a real server and PostGIS: a form whose <c>search()</c> or <c>pulldata("@layer")</c> names a
/// layer is added and published when the layer is this server's and the publisher can read it, and refused at the row
/// and column otherwise; and the where clauses the form client writes for a search are ones this server's query answers.
/// </summary>
[Trait("Needs", "TwoNodes")]
[Collection("one sync node")]
public sealed class AFormQueriesOnlyThisServersLayersTests(OneStartedNode started)
{
    private TwoNodeSite.Node Node => started.Node;

    private static JsonArray Row(params string[] cells) => new([.. cells.Select(c => (JsonNode?)c)]);

    private static JsonObject Model(string formId, string[][] survey, string[][] choices) => new()
    {
        ["format"] = "xlsform/1",
        ["sheets"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "survey",
                ["columns"] = Row("type", "name", "label", "appearance", "calculation"),
                ["rows"] = new JsonArray([.. survey.Select(r => (JsonNode?)Row(r))]),
            },
            new JsonObject
            {
                ["name"] = "choices",
                ["columns"] = Row("list_name", "name", "label"),
                ["rows"] = new JsonArray([.. choices.Select(r => (JsonNode?)Row(r))]),
            },
            new JsonObject
            {
                ["name"] = "settings",
                ["columns"] = Row("form_title", "form_id"),
                ["rows"] = new JsonArray(Row("Form " + formId, formId)),
            }),
    };

    private async Task<(int Status, JsonNode? Body)> CallAsync(HttpMethod method, string path, string token, object? body = null)
    {
        (int status, string text) = await Node.SendAsync(method, path, token, body);
        return (status, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Details(JsonNode? body) =>
        string.Join("\n", (body?["error"]?["details"] as JsonArray ?? []).Select(d => (string?)d));

    [Fact]
    public async Task A_layer_of_this_server_is_queried_and_one_elsewhere_or_missing_is_refused_at_its_row()
    {
        string token = await Node.SignInAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];

        // ---- a layer to query: a form's own, with a text and a number field, and two rows in it ----
        string targetId = "zz_queried_" + suffix;
        (int addStatus, JsonNode? added) = await CallAsync(HttpMethod.Post, "/admin/forms", token, new
        {
            title = "Form " + targetId,
            model = Model(targetId, [["text", "ad", "Name"], ["integer", "kat", "Floor"]], [["x", "a", "A"]]),
        });
        Assert.True(addStatus == 201, $"Adding the target answered {addStatus}: {added}");
        (int madeStatus, JsonNode? made) = await CallAsync(HttpMethod.Post, $"/admin/forms/{(string)added!["id"]!}/publish", token, new { });
        Assert.True(madeStatus == 200, $"Publishing the target answered {madeStatus}: {made}");
        string layerUrl = (string)made!["layers"]![0]!["url"]!;

        using (HttpRequestMessage add = new(HttpMethod.Post, $"{layerUrl}/addFeatures"))
        {
            add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            add.Content = new FormUrlEncodedContent(
            [
                KeyValuePair.Create("features", """[{"attributes":{"ad":"Ankara","kat":3}},{"attributes":{"ad":"Berlin","kat":4}}]"""),
                KeyValuePair.Create("f", "json"),
            ]);
            using HttpResponseMessage sent = await Node.Http.SendAsync(add);
            Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        }

        // ---- the where clauses the client writes (fill.js searchLayer and layerAnswer) are answered ----
        foreach ((string where, int rows) in new[] { ("(UPPER(ad) LIKE '%ANK%')", 1), ("(ad = 'Berlin') AND kat = 4", 1), ("1=1", 2), ("kat = 9", 0) })
        {
            (int status, JsonNode? answer) = await CallAsync(HttpMethod.Get,
                $"{layerUrl}/query?f=json&outFields=*&returnGeometry=false&where={Uri.EscapeDataString(where)}", token);
            Assert.True(status == 200 && answer?["error"] is null, $"'{where}' answered {status}: {answer}");
            Assert.Equal(rows, answer!["features"]!.AsArray().Count);
        }

        (int fieldsStatus, JsonNode? description) = await CallAsync(HttpMethod.Get, $"{layerUrl}?f=json", token);
        Assert.Equal(200, fieldsStatus);
        Assert.Contains(description!["fields"]!.AsArray(), f => (string?)f!["name"] == "kat" && ((string?)f["type"])!.Contains("Integer", StringComparison.Ordinal));

        // ---- a form that queries it, by a relative and by an absolute address of this server: added and published ----
        string absolute = new Uri(Node.Http.BaseAddress!, layerUrl).ToString();
        string ownId = "zz_queries_" + suffix;
        (int ownStatus, JsonNode? own) = await CallAsync(HttpMethod.Post, "/admin/forms", token, new
        {
            title = "Form " + ownId,
            model = Model(ownId,
                [
                    ["select_one yerler", "yer", "Place", $"autocomplete search(\"yerler?url={layerUrl}&orderByFields=ad\", 'contains', 'ad', 'a')"],
                    ["text", "kat_bilgisi", "Floor", "", $"pulldata(\"@layer\", \"getValue\", \"attributes.kat\", \"{absolute}\", concat(\"ad = '\", ${{yer}}, \"'\"))"],
                ],
                [["yerler", "ad", "ad"]]),
        });
        Assert.True(ownStatus == 201, $"Adding the form that queries this server answered {ownStatus}: {own}");
        (int published, JsonNode? publication) = await CallAsync(HttpMethod.Post, $"/admin/forms/{(string)own!["id"]!}/publish", token, new { });
        Assert.True(published == 200, $"Publishing it answered {published}: {publication}");

        // ---- another server's layer: refused at the upload, at the row and column, and the host named ----
        string foreignId = "zz_foreign_" + suffix;
        (int foreignStatus, JsonNode? foreign) = await CallAsync(HttpMethod.Post, "/admin/forms", token, new
        {
            title = "Form " + foreignId,
            model = Model(foreignId,
                [["select_one yerler", "yer", "Place", "search(\"yerler?url=https://services.arcgis.com/abc/arcgis/rest/services/X/FeatureServer/2\")"]],
                [["yerler", "ad", "ad"]]),
        });
        Assert.Equal(400, foreignStatus);
        string said = Details(foreign) + (foreign?.ToJsonString() ?? string.Empty);
        Assert.Contains("services.arcgis.com", said, StringComparison.Ordinal);
        Assert.Contains("appearance", said, StringComparison.Ordinal);

        // ---- a layer this server does not have: refused the same way ----
        string missingId = "zz_missing_" + suffix;
        (int missingStatus, JsonNode? missing) = await CallAsync(HttpMethod.Post, "/admin/forms", token, new
        {
            title = "Form " + missingId,
            model = Model(missingId,
                [["text", "x", "X", "", $"pulldata(\"@layer\", \"getRecord\", \"{layerUrl[..layerUrl.LastIndexOf('/')]}/9\")"]],
                [["yerler", "ad", "ad"]]),
        });
        Assert.Equal(400, missingStatus);
        Assert.Contains("There is no layer 9", Details(missing) + (missing?.ToJsonString() ?? string.Empty), StringComparison.Ordinal);
    }
}
