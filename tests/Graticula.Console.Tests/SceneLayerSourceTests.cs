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
}
