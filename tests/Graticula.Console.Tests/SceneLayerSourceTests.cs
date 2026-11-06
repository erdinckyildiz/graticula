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
}
