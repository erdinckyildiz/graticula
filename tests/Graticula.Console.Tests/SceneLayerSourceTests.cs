using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-270 §5.4: a scene layer made here is related to the feature layer it was made from — the Maps SDK finds it as the
/// scene layer's associated layer — and is made again from it in place, from Studio's page of the scene layer.
/// </summary>
public sealed class SceneLayerSourceTests : ConsoleTest
{
    private async Task DrawInSoftwareAsync() =>
        await Browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new
        {
            source = "(() => { const get = HTMLCanvasElement.prototype.getContext; HTMLCanvasElement.prototype.getContext = function (type, attrs) {"
                + " if (attrs && typeof attrs === 'object') attrs = { ...attrs, failIfMajorPerformanceCaveat: false };"
                + " return get.call(this, type, attrs); }; })();",
        });

    private async Task<string> MakeAsync()
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/content/scenes",
            """{"service":"ci_buildings","folder":"hosted","layer":0,"heightField":"floors","heightScale":3,"title":"zz console scene source"}""");
        Assert.True(status == 201, $"Making the scene layer answered {status}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>The scene layer says where it came from, is related to it as Service2Service, and the SDK finds it.</summary>
    [Fact]
    public async Task A_made_scene_layer_names_its_feature_layer_and_the_sdk_finds_it()
    {
        (string token, string cookie) = await SignInAsync();
        string id = await MakeAsync();

        try
        {
            (int status, string source) = await AdminAsync(HttpMethod.Get, $"/content/scenes/{id}/source");
            Assert.Equal(200, status);
            JsonElement said = JsonDocument.Parse(source).RootElement;
            Assert.Equal("hosted/ci_buildings", said.GetProperty("service").GetString());
            Assert.Equal("floors", said.GetProperty("heightField").GetString());
            Assert.True(said.GetProperty("exists").GetBoolean());

            (status, string related) = await AdminAsync(HttpMethod.Get,
                $"/sharing/rest/content/items/{id}/relatedItems?relationshipType=Service2Service&direction=reverse&f=json");
            Assert.Equal(200, status);
            JsonElement[] items = JsonDocument.Parse(related).RootElement.GetProperty("relatedItems").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Equal("Feature Service", items[0].GetProperty("type").GetString());
            Assert.Contains("ci_buildings", items[0].GetProperty("url").GetString());

            // The Maps SDK, given the scene layer by its item, finds the feature layer through that relationship — it asks
            // for it going reverse, from the scene layer back to its origin.
            await DrawInSoftwareAsync();
            await OpenAsync($"/studio/scene.html?layer={System.Uri.EscapeDataString($"{Root}/rest/services/hosted/{id}/SceneServer")}&title=zz", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true && scState.scene.layers.length === 1"
                + " && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "The scene layer did not open by its item.");
            Assert.Equal(id, await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).portalItem?.id"));
            await WaitForAsync("!!scState.scene.layers.getItemAt(0).associatedLayer",
                "The Maps SDK found no associated feature layer for the scene layer.");
            Assert.Contains("ci_buildings", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).associatedLayer.url") ?? string.Empty);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        }
    }

    /// <summary>Studio's page of the scene layer says what it was made from, and Rebuild makes it again in place.</summary>
    [Fact]
    public async Task Rebuild_makes_the_scene_layer_again_in_place_from_studio()
    {
        (string token, string cookie) = await SignInAsync();
        string id = await MakeAsync();

        try
        {
            (int _, string before) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            long modified = JsonDocument.Parse(before).RootElement.GetProperty("modified").GetInt64();

            await OpenAsync($"/studio/#/item/{id}", token, cookie);
            await WaitForAsync("!document.getElementById('docScene')?.hidden && /Made from/.test(document.getElementById('docScene').textContent)",
                "The scene layer's page did not say what it was made from.");
            Assert.Contains("hosted/ci_buildings", await Browser.EvaluateAsync<string>("document.getElementById('docScene').textContent"));
            // Made again from its layer, so a file put in by hand is not offered: the next Rebuild would overwrite it.
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('docReplace') && !document.getElementById('docReplace').hidden"),
                "A scene layer made from a layer offered Replace file beside Rebuild.");

            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/rebuild'], true)");
            await ClickAsync("#docSceneRebuild");
            await WaitForAsync("/was rebuilt with 8 buildings/.test(document.getElementById('docSays').textContent)", "Rebuild did not say it rebuilt the layer.");
            // ADR-277 §5.2: made from the package this server wrote, and the layer unchanged, nothing is written again.
            Assert.Contains("nothing had changed", await Browser.EvaluateAsync<string>("document.getElementById('docSays').textContent") ?? string.Empty);

            (int status, string after) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(200, status);
            Assert.True(JsonDocument.Parse(after).RootElement.GetProperty("modified").GetInt64() > modified, "The rebuilt package did not replace the old one.");

            // Served from the new package: the scene layer still answers.
            (status, _) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{id}/SceneServer/layers/0?f=json");
            Assert.Equal(200, status);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        }
    }

    /// <summary>A package uploaded rather than made here has no source, and Rebuild is refused with the reason.</summary>
    [Fact]
    public async Task A_package_not_made_here_has_no_source_to_rebuild_from()
    {
        string id = await MakeAsync();

        try
        {
            // Its source removed, it is a package like any uploaded one.
            (string token, _) = await SignInAsync();
            using HttpRequestMessage remove = new(HttpMethod.Post, new System.Uri($"{Root}/sharing/rest/content/users/ci/items/{id}/removeResources"))
            {
                Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["resource"] = "graticula/scene-source.json", ["f"] = "json",
                }),
            };
            remove.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage removed = await Http.SendAsync(remove);
            string said = await removed.Content.ReadAsStringAsync();
            Assert.True(removed.IsSuccessStatusCode && !said.Contains("\"error\""), $"removeResources answered {(int)removed.StatusCode}: {said}");

            int status;

            (status, string refused) = await AdminAsync(HttpMethod.Post, $"/content/scenes/{id}/rebuild");
            Assert.Equal(409, status);
            Assert.Contains("not made by this server", refused);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        }
    }

    /// <summary>
    /// ADR-277: a scene layer whose owner asked is made again on its own after its layer changes — Enterprise's scheduled
    /// rebuild — and Studio's page says when it was and with how many features.
    /// </summary>
    [Fact]
    public async Task A_scene_layer_is_rebuilt_on_its_own_after_its_layer_changes()
    {
        (string token, string cookie) = await SignInAsync();
        string editable = (Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable").Trim('/');
        string folder = editable.Contains('/') ? editable[..editable.IndexOf('/')] : string.Empty;
        string name = editable[(editable.IndexOf('/') + 1)..];
        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/scenes",
            JsonSerializer.Serialize(new { service = name, folder, layer = 0, title = "zz console scene auto rebuild" }));
        Assert.True(status == 201, $"Making the point scene layer answered {status}: {body}");
        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        long added = 0;

        try
        {
            await OpenAsync($"/studio/#/item/{id}", token, cookie);
            await WaitForAsync("!!document.getElementById('docSceneAuto')", "The scene layer's page offered no automatic rebuild.");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/source'], true)");
            await ClickAsync("#docSceneAuto");
            await WaitForAsync("/rebuilt on its own when the layer changes/.test(document.getElementById('docSays').textContent)",
                "Turning the automatic rebuild on was not said.");

            (status, string source) = await AdminAsync(HttpMethod.Get, $"/content/scenes/{id}/source");
            Assert.Equal(200, status);
            Assert.True(JsonDocument.Parse(source).RootElement.GetProperty("autoRebuild").GetBoolean());

            // The layer changes: one point more.
            using (HttpRequestMessage edit = new(HttpMethod.Post, new Uri($"{Root}/rest/services/{editable}/FeatureServer/0/applyEdits"))
            {
                Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string>
                {
                    ["adds"] = "[{\"geometry\":{\"x\":29.01,\"y\":41.01,\"spatialReference\":{\"wkid\":4326}},\"attributes\":{\"label\":\"zz auto rebuild\"}}]",
                    ["f"] = "json",
                }),
            })
            {
                edit.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage edited = await Http.SendAsync(edit);
                JsonElement result = JsonDocument.Parse(await edited.Content.ReadAsStringAsync()).RootElement.GetProperty("addResults")[0];
                Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
                added = result.GetProperty("objectId").GetInt64();
            }

            // The sweep looks every few seconds on a test server (Graticula:SceneRebuildSeconds); allow it a minute.
            JsonElement automatic = default;
            for (int waited = 0; waited < 60; waited++)
            {
                (_, source) = await AdminAsync(HttpMethod.Get, $"/content/scenes/{id}/source");
                if (JsonDocument.Parse(source).RootElement.TryGetProperty("lastAutomatic", out automatic) && automatic.ValueKind == JsonValueKind.Object)
                {
                    break;
                }

                await Task.Delay(1000);
            }

            Assert.True(automatic.ValueKind == JsonValueKind.Object, "The scene layer was not rebuilt on its own within a minute of its layer changing.");
            Assert.True(automatic.GetProperty("made").GetBoolean(), automatic.GetRawText());

            await OpenAsync($"/studio/#/item/{id}", token, cookie);
            await WaitForAsync("/Last rebuilt on its own/.test(document.getElementById('docSceneAutomatic')?.textContent || '')",
                "Studio's page did not say the scene layer was rebuilt on its own.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (added > 0)
            {
                using HttpRequestMessage undo = new(HttpMethod.Post, new Uri($"{Root}/rest/services/{editable}/FeatureServer/0/applyEdits"))
                {
                    Content = new FormUrlEncodedContent(new System.Collections.Generic.Dictionary<string, string> { ["deletes"] = added.ToString(System.Globalization.CultureInfo.InvariantCulture), ["f"] = "json" }),
                };
                undo.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage _ = await Http.SendAsync(undo);
            }

            await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        }
    }
    /// <summary>
    /// ADR-276 condition 3: Edit, in a scene holding a scene layer its owner made here, edits the layer it was made from,
    /// kept out of the scene's own list, and offers to rebuild the scene layer after an edit.
    /// </summary>
    [Fact]
    public async Task A_scene_layer_is_edited_through_its_features_and_rebuilt_from_the_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string editable = (Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable").Trim('/');
        string folder = editable.Contains('/') ? editable[..editable.IndexOf('/')] : string.Empty;
        string name = editable[(editable.IndexOf('/') + 1)..];
        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/scenes",
            JsonSerializer.Serialize(new { service = name, folder, layer = 0, title = "zz console scene edit through" }));
        Assert.True(status == 201, $"Making the point scene layer answered {status}: {body}");
        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await DrawInSoftwareAsync();
            await OpenAsync($"/studio/scene.html?layer={System.Uri.EscapeDataString($"{Root}/rest/services/hosted/{id}/SceneServer")}&title=zz", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true && scState.scene.layers.length === 1"
                + " && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "The scene layer did not open.");
            await WaitForAsync("!!document.querySelector('#tools [data-group=edit]') && !document.querySelector('#tools [data-group=edit]').hidden",
                "A scene layer made from an editable layer did not offer Edit to its owner.");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/applyEdits', '/rebuild'], true)");
            await ClickAsync("#tools [data-group=edit]");
            await WaitForAsync("scState.tool === 'edit' && !!document.querySelector('#toolPanel .esri-editor')", "Edit did not open the editor.");
            Assert.True(await Browser.EvaluateAsync<bool>("scState.tools.edit.layerInfos.some(i => i.enabled && i.layer.scSourceOf === scState.scene.layers.getItemAt(0))"),
                "The editor was not offered the scene layer's own feature layer.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelectorAll('#layerList > li').length === 1"),
                "The editor's feature layer was listed among the scene's layers: " + await Browser.EvaluateAsync<string>("[...document.querySelectorAll('#layerList > li')].map(li => li.dataset.layer + ':' + li.textContent.trim().slice(0, 60)).join(' | ')"));

            await Browser.EvaluateAsync<bool>("(scRequire(['esri/Graphic']).then(([Graphic]) => { const l = scLeaves(true).find(l => l.scSourceOf);"
                + " return l.applyEdits({ addFeatures: [new Graphic({ geometry: { type: 'point', longitude: 29.02, latitude: 41.02 }, attributes: { label: 'zz edit through' } })] }); })"
                + ".then(r => window.__edited = r.addFeatureResults[0], e => window.__edited = { error: e.message }), true)");
            await WaitForAsync("!!window.__edited", "The edit was not answered.");
            Assert.True(await Browser.EvaluateAsync<bool>("!window.__edited.error && window.__edited.objectId > 0"),
                "The edit through the scene layer was refused: " + await Browser.EvaluateAsync<string>("JSON.stringify(window.__edited)"));

            await WaitForAsync("!!document.querySelector('#toolPanel [data-rebuild] button')", "No rebuild was offered after the edit.");
            await ClickAsync("#toolPanel [data-rebuild] button");
            await WaitForAsync("/was rebuilt/.test(document.querySelector('#toolPanel [data-rebuild]').textContent)",
                "The scene layer was not rebuilt from the scene: " + await Browser.EvaluateAsync<string>("document.querySelector('#toolPanel [data-rebuild]')?.textContent || ''"));
            await WaitForAsync("scLeaves().length === 1 && !!scLeaves()[0].customParameters?.built && scLeaves()[0].loadStatus === 'loaded'",
                "The rebuilt scene layer was not put back in its place.");
            Assert.True(await Browser.EvaluateAsync<bool>("scState.tool === 'edit' && scLeaves(true).find(l => l.scSourceOf).scSourceOf === scLeaves()[0]"),
                "Edit did not stay open on the rebuilt scene layer.");

            await Browser.EvaluateAsync<bool>("(scLeaves(true).find(l => l.scSourceOf).applyEdits({ deleteFeatures: [{ objectId: window.__edited.objectId }] })"
                + ".then(r => window.__removed = r.deleteFeatureResults[0]), true)");
            await WaitForAsync("!!window.__removed && !window.__removed.error", "The feature the test added was not removed.");

            await ClickAsync("#tools [data-group=edit]");
            await WaitForAsync("scState.tool === null && !scLeaves(true).some(l => l.scSourceOf)", "Closing Edit left the feature layer in the scene.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
        }
    }
    /// <summary>
    /// ADR-280: a 3D object layer is created from the scene with its scene layer; the Maps SDK places a model in it through
    /// the scene layer, the server keeps it and says it changed, Rebuild puts it in the scene layer, and deleting it takes
    /// it out again.
    /// </summary>
    [Fact]
    public async Task A_3d_object_layer_takes_a_model_from_the_scene_and_its_scene_layer_is_rebuilt_with_it()
    {
        (string token, string cookie) = await SignInAsync();
        string? meshId = null, sceneId = null;
        try
        {
            await DrawInSoftwareAsync();
            await OpenAsync("/studio/scene.html", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The scene viewer did not open.");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/content/meshes', '/applyEdits', '/uploadAssets', '/rebuild'], true)");
            await Browser.EvaluateAsync<bool>("(document.getElementById('createLayer').open = true, document.getElementById('createName').value = 'zz_models',"
                + " document.getElementById('createKind').value = 'mesh', true)");
            await ClickAsync("#createGo");
            await WaitForAsync("scLeaves().length === 1 && scLeaves()[0].loadStatus === 'loaded' && !!scLeaves()[0].scSource?.mesh",
                "Creating a 3D object layer did not add its scene layer: " + await Browser.EvaluateAsync<string>("document.getElementById('addSaid').textContent"));
            meshId = await Browser.EvaluateAsync<string>("scLeaves()[0].scSource.mesh");
            sceneId = await Browser.EvaluateAsync<string>("scLeaves()[0].scSource.id");

            (int status, string layer) = await AdminAsync(HttpMethod.Get, $"/rest/services/meshes/{meshId}/FeatureServer/0?f=json");
            Assert.Equal(200, status);
            Assert.Equal("esriGeometryMultiPatch", JsonDocument.Parse(layer).RootElement.GetProperty("geometryType").GetString());

            // The SDK finds the 3D object layer from the scene layer's item, as a mesh layer it can edit.
            await WaitForAsync("!!scLeaves()[0].associatedLayer && scLeaves()[0].associatedLayer.loaded && scLeaves()[0].associatedLayer.geometryType === 'mesh'",
                "The Maps SDK did not find the 3D object layer behind the scene layer.");
            await WaitForAsync("scState.tool === 'edit'", "Edit was not opened on the new layer.");
            Assert.True(await Browser.EvaluateAsync<bool>("scState.tools.edit.layerInfos.some(i => i.enabled && i.layer === scLeaves()[0])"),
                "The editor was not offered the 3D object layer's scene layer.");

            // A 10 by 20 by 10 m box placed through the scene layer, as the SDK's editor places a model.
            await Browser.EvaluateAsync<bool>("(scRequire(['esri/geometry/Mesh', 'esri/geometry/Point', 'esri/Graphic']).then(([Mesh, Point, Graphic]) => {"
                + " const mesh = Mesh.createBox(new Point({ longitude: 29.01, latitude: 41.01, z: 0 }), { size: { width: 10, height: 20, depth: 10 } });"
                + " return scLeaves()[0].applyEdits({ addFeatures: [new Graphic({ geometry: mesh, attributes: { name: 'zz box' } })] }); })"
                + ".then(r => window.__placed = r.addFeatureResults[0], e => window.__placed = { error: e.message }), true)");
            await WaitForAsync("!!window.__placed", "Placing the model was not answered.");
            Assert.True(await Browser.EvaluateAsync<bool>("!window.__placed.error && window.__placed.objectId > 0"),
                "The model was refused: " + await Browser.EvaluateAsync<string>("JSON.stringify(window.__placed)"));

            (status, string features) = await AdminAsync(HttpMethod.Get, $"/rest/services/meshes/{meshId}/FeatureServer/0/query3D?where=1%3D1&outFields=*&f=json");
            Assert.Equal(200, status);
            JsonElement feature = JsonDocument.Parse(features).RootElement.GetProperty("features")[0];
            Assert.Equal("zz box", feature.GetProperty("attributes").GetProperty("name").GetString());
            Assert.Equal(29.01, feature.GetProperty("attributes").GetProperty("ESRI3DO_OX").GetDouble(), 6);
            Assert.Equal(1, feature.GetProperty("assetMaps").GetArrayLength());
            Assert.Equal(20, feature.GetProperty("geometry").GetProperty("zmax").GetDouble() - feature.GetProperty("geometry").GetProperty("zmin").GetDouble(), 1);

            string gens = Uri.EscapeDataString("[{\"id\":0,\"serverGen\":1}]");
            (status, string changes) = await AdminAsync(HttpMethod.Get,
                $"/rest/services/meshes/{meshId}/FeatureServer/extractChanges?layers=%5B0%5D&layerServerGens={gens}&returnIdsOnly=true&f=json");
            Assert.Equal(200, status);
            Assert.Equal(1, JsonDocument.Parse(changes).RootElement.GetProperty("edits")[0].GetProperty("objectIds").GetProperty("adds").GetArrayLength());

            // Rebuild: the scene layer holds the box.
            (status, string rebuilt) = await AdminAsync(HttpMethod.Post, $"/content/scenes/{sceneId}/rebuild");
            Assert.True(status == 200, rebuilt);
            Assert.Equal(1, JsonDocument.Parse(rebuilt).RootElement.GetProperty("features").GetInt32());
            (status, string page) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{sceneId}/SceneServer/layers/0/nodepages/0?f=json");
            Assert.Equal(200, status);
            Assert.True(JsonDocument.Parse(page).RootElement.GetProperty("nodes").GetArrayLength() >= 2, "The rebuilt scene layer has no node holding the model.");

            // Deleted through the scene layer, by its GlobalID, and gone from the store.
            await Browser.EvaluateAsync<bool>("(scLeaves()[0].applyEdits({ deleteFeatures: [{ objectId: window.__placed.objectId, globalId: window.__placed.globalId }] })"
                + ".then(r => window.__gone = r.deleteFeatureResults[0], e => window.__gone = { error: e.message }), true)");
            await WaitForAsync("!!window.__gone", "Deleting the model was not answered.");
            Assert.True(await Browser.EvaluateAsync<bool>("!window.__gone.error"), await Browser.EvaluateAsync<string>("JSON.stringify(window.__gone)"));
            (status, string count) = await AdminAsync(HttpMethod.Get, $"/rest/services/meshes/{meshId}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
            Assert.Equal(0, JsonDocument.Parse(count).RootElement.GetProperty("count").GetInt32());
            NothingWentWrong(await PageErrorsAsync());

            // Its item in Studio: a page like any item's, naming what it is.
            await OpenAsync($"/studio/#/item/{meshId}", token, cookie);
            await WaitForAsync("/zz_models/.test(document.body.innerText)", "Studio did not open the 3D object layer's item.");
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (sceneId is not null) await AdminAsync(HttpMethod.Delete, $"/content/documents/{sceneId}");
            if (meshId is not null) await AdminAsync(HttpMethod.Delete, $"/content/documents/{meshId}");
        }
    }
}
