using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A web map layer's form — ADR-313 condition 3: the <c>formInfo</c> Studio's Forms tool writes, and ArcGIS Field Maps
/// reads, comes back whole through every door a web map passes: Studio's create, read and update (which its Save,
/// Save as and the content page's sharing all use), the portal's <c>/data</c> (what Field Maps and Pro open a map from),
/// the portal's <c>update</c> (<c>WebMap.save()</c>) and Pro's <c>addItem</c> (<i>Share As Web Map</i>).
/// </summary>
/// <remarks>
/// The form holds every element kind the web map specification lists, the utility network element that Studio does not
/// draw among them, and expressions with characters a careless writer escapes (backticks, quotes, braces, a newline and
/// non-ASCII text), so a door that rewrote any of it would fail here rather than on a device.
/// </remarks>
[Trait("Needs", "RunningHost")]
public sealed class AMapLayersFormSurvivesEveryDoorTests : ArcGisClient
{
    private const string FormInfo = """
        {
          "title": "Ağaç denetimi — tree inspection",
          "description": "Fill in {tree_type}.",
          "preserveFieldValuesWhenHidden": true,
          "supportsAttachmentElements": true,
          "formElements": [
            { "type": "field", "fieldName": "tree_type", "label": "Tree type", "inputType": { "type": "combo-box", "showNoValueOption": false, "noValueOptionLabel": "Not defined" }, "editableExpression": "expr/system/true" },
            { "type": "field", "fieldName": "height", "label": "Height", "hint": "metres", "inputType": { "type": "text-box", "maxLength": 100, "minLength": 0 }, "requiredExpression": "expr1" },
            { "type": "group", "label": "Trimming", "initialState": "collapsed", "visibilityExpression": "expr2", "formElements": [
              { "type": "field", "fieldName": "needs_trimming", "inputType": { "type": "switch", "onValue": "Yes", "offValue": "No" }, "valueExpression": "expr/0", "editableExpression": "expr/system/false" },
              { "type": "text", "textFormat": "markdown", "text": "### Some **descriptive text**\nLast inspection maximum was {expr/maxPSI}." },
              { "type": "attachment", "attachmentKeyword": "image-of-tree", "label": "Photo", "inputType": { "type": "image", "inputMethod": "any", "maxImageSize": 1280 }, "maxAttachmentCount": 1, "minAttachmentCount": 1, "useOriginalFilename": false,
                "filenameExpression": "StandardizeFilename(`${$formElement.attachmentKeyword}_${Text(Now(), \"YMMDDHHmmss\")}`)" },
              { "type": "relationship", "relationshipId": 0, "displayCount": 3, "displayType": "list", "label": "Previous inspections", "orderByFields": [ { "field": "inspectionDate", "order": "desc" } ] }
            ] },
            { "type": "utilityNetworkAssociations", "label": "Associations", "associationTypes": [ { "type": "connectivity" } ] }
          ],
          "expressionInfos": [
            { "name": "expr1", "title": "Is Pine Tree", "expression": "$feature.tree_type == 3", "returnType": "boolean" },
            { "name": "expr2", "title": "Tall Tree", "expression": "$feature.height >= 50 ", "returnType": "boolean" },
            { "name": "expr/0", "title": "Calculated value — needs_trimming", "expression": "var t = `${$feature.height}`;\nreturn IIf(Number(t) > 50, 'Yes', \"No\");", "returnType": "string" },
            { "name": "expr/maxPSI", "title": "Max PSI", "expression": "Max([1, 2, 3])", "returnType": "number" },
            { "name": "expr/system/true", "title": "True", "expression": "true", "returnType": "boolean" },
            { "name": "expr/system/false", "title": "False", "expression": "false", "returnType": "boolean" }
          ]
        }
        """;

    private static string Document(string formInfo) => $$"""
        {
          "operationalLayers": [
            { "id": "trees", "layerType": "ArcGISFeatureLayer", "url": "https://example.test/rest/services/hosted/trees/FeatureServer/0",
              "title": "Trees", "visibility": true, "popupInfo": { "title": "{tree_type}" }, "formInfo": {{formInfo}} }
          ],
          "tables": [
            { "id": "inspections", "url": "https://example.test/rest/services/hosted/trees/FeatureServer/1", "title": "Inspections", "formInfo": {{formInfo}} }
          ],
          "baseMap": { "baseMapLayers": [], "title": "None" },
          "spatialReference": { "wkid": 102100 },
          "version": "2.31"
        }
        """;

    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    [Fact]
    public async Task A_layers_form_comes_back_whole_through_Studio_the_portals_data_and_update_and_Pros_addItem()
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
        string document = Document(FormInfo);
        List<string> made = [];

        try
        {
            // ---- Studio's Save (and Save as, which is the same create with the document it holds) ----
            (HttpStatusCode created, string body) = await RequestAsync(HttpMethod.Post, $"{root}/content/webmaps", token, Body("ADR-313 form", document));
            Assert.True(created == HttpStatusCode.Created, $"Creating the map answered {(int)created}: {body}");
            string id = Parsed(body).GetProperty("id").GetString()!;
            made.Add(id);

            AssertSameForm(document, await JsonAsync(HttpMethod.Get, $"{root}/content/webmaps/{id}", token, "document"), "Studio's read");
            AssertSameForm(document, await JsonAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{id}/data?f=json", token), "the portal's /data");

            // ---- Studio's update: the map saved again, as the viewer and the content page's sharing send it ----
            (HttpStatusCode updated, string said) = await RequestAsync(HttpMethod.Put, $"{root}/content/webmaps/{id}", token, Body("ADR-313 form, saved again", document, "organization"));
            Assert.True(updated == HttpStatusCode.OK, $"Updating the map answered {(int)updated}: {said}");
            AssertSameForm(document, await JsonAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{id}/data?f=json", token), "/data after Studio's update");

            // ---- the portal's update, WebMap.save(): a changed form replaces the old one, and nothing else is lost ----
            JsonNode changedForm = JsonNode.Parse(FormInfo)!;
            changedForm["title"] = "Changed by a portal client";
            changedForm["formElements"]!.AsArray().RemoveAt(0);
            string changed = Document(changedForm.ToJsonString());
            JsonElement portal = await FormAsync(root, token, $"/sharing/rest/content/users/{User}/items/{id}/update?f=json",
                new Dictionary<string, string> { ["text"] = changed });
            Assert.True(portal.TryGetProperty("success", out JsonElement ok) && ok.GetBoolean(), portal.GetRawText());
            AssertSameForm(changed, await JsonAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{id}/data?f=json", token), "/data after the portal's update");
            AssertSameForm(changed, await JsonAsync(HttpMethod.Get, $"{root}/content/webmaps/{id}", token, "document"), "Studio's read after the portal's update");

            // ---- Pro's Share As Web Map: addItem with the map in text ----
            JsonElement added = await FormAsync(root, token, $"/sharing/rest/content/users/{User}/addItem?f=json",
                new Dictionary<string, string> { ["type"] = "Web Map", ["title"] = $"zz_adr313_{Guid.NewGuid():N}"[..24], ["tags"] = "adr313", ["text"] = document });
            Assert.True(added.TryGetProperty("success", out JsonElement addedOk) && addedOk.GetBoolean(), added.GetRawText());
            string proId = added.GetProperty("id").GetString()!;
            made.Add(proId);
            AssertSameForm(document, await JsonAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{proId}/data?f=json", token), "/data of a map Pro added");
        }
        finally
        {
            foreach (string id in made)
            {
                await RequestAsync(HttpMethod.Delete, $"{root}/content/webmaps/{id}", token, null);
            }
        }
    }

    /// <summary>The document read back equals the one sent, and its layer's and table's forms are the ones sent.</summary>
    private static void AssertSameForm(string sent, JsonElement read, string where)
    {
        JsonElement expected = Parsed(sent);
        JsonElement layerForm = read.GetProperty("operationalLayers")[0].GetProperty("formInfo");
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("operationalLayers")[0].GetProperty("formInfo"), layerForm),
            $"The layer's formInfo through {where} is not the one sent:\n{layerForm.GetRawText()}");
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("tables")[0].GetProperty("formInfo"), read.GetProperty("tables")[0].GetProperty("formInfo")),
            $"The table's formInfo through {where} is not the one sent.");
        Assert.True(JsonElement.DeepEquals(expected, read), $"The document through {where} is not the one sent:\n{read.GetRawText()}");
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string url, string token, string? property = null)
    {
        (HttpStatusCode status, string body) = await RequestAsync(method, url, token, null);
        Assert.True(status == HttpStatusCode.OK, $"{method} {url} answered {(int)status}: {body}");
        JsonElement parsed = Parsed(body);
        return property is null ? parsed : parsed.GetProperty(property);
    }

    private async Task<JsonElement> FormAsync(string root, string token, string path, IDictionary<string, string> form)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {path} answered {(int)response.StatusCode}: {body}");
        return Parsed(body);
    }

    private static string Body(string title, string document, string sharing = "private") =>
        $$"""{"title":{{JsonSerializer.Serialize(title)}},"snippet":"ADR-313 test","sharing":"{{sharing}}","document":{{document}}}""";

    private static JsonElement Parsed(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
