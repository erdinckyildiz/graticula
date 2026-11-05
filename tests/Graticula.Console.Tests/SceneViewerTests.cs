using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-269: Studio's scene viewer saves a web scene and opens it again, and a polygon layer's page makes a scene layer
/// that opens in it.
/// </summary>
public sealed class SceneViewerTests : ConsoleTest
{
    /// <summary>
    /// <b>Lets the map SDK draw a scene in software, in the test's browser and nowhere else.</b> The SDK asks for WebGL with
    /// <c>failIfMajorPerformanceCaveat</c>, so a browser without a GPU — the suite's, and CI's — is refused a context and the
    /// scene viewer says it cannot draw (measured 2026-11-05: "Your WebGL implementation doesn't seem to support hardware
    /// accelerated rendering"). The page's own behaviour is not changed: only the flag is dropped from the request.
    /// </summary>
    private async Task DrawInSoftwareAsync() =>
        await Browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new
        {
            source = "(() => { const get = HTMLCanvasElement.prototype.getContext; HTMLCanvasElement.prototype.getContext = function (type, attrs) {"
                + " if (attrs && typeof attrs === 'object') attrs = { ...attrs, failIfMajorPerformanceCaveat: false };"
                + " return get.call(this, type, attrs); }; })();",
        });

    /// <summary>A new scene with a layer is saved as a Web Scene item and opens again with that layer.</summary>
    [Fact]
    public async Task A_scene_is_saved_and_opens_again_with_its_layer()
    {
        (string token, string cookie) = await SignInAsync();
        string layer = $"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0";
        string? id = null;

        try
        {
            await DrawInSoftwareAsync();
            await OpenAsync("/studio/scene.html", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The scene viewer did not open a new scene.");
            Assert.Equal("New scene", await Browser.EvaluateAsync<string>("document.getElementById('title').textContent"));

            await Browser.EvaluateAsync<bool>($"(document.getElementById('addUrl').value = {JsonSerializer.Serialize(layer)}, document.getElementById('addUrlButton').click(), true)");
            await WaitForAsync("/was added/.test(document.getElementById('addSaid').textContent)", "Adding a layer by its address did not say it was added.");
            Assert.Equal(1, await Browser.EvaluateAsync<int>("scState.scene.layers.length"));

            // The save reaches the server — its item is read back below and deleted in finally.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/addItem'], true)");
            await Browser.EvaluateAsync<bool>("(document.getElementById('sceneTitle').value = 'zz console scene', document.getElementById('sceneTitle').dispatchEvent(new Event('input')), true)");
            await ClickAsync("#headSave");
            await WaitForAsync("/Saved as/.test(document.getElementById('status').textContent)", "Save did not say the scene was saved.");
            id = await Browser.EvaluateAsync<string>("scState.id");
            Assert.False(string.IsNullOrEmpty(id));

            await OpenAsync($"/studio/scene.html?id={id}", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The saved scene did not open.");
            Assert.Equal("zz console scene", await Browser.EvaluateAsync<string>("document.getElementById('title').textContent"));
            Assert.Equal(1, await Browser.EvaluateAsync<int>("scState.scene.layers.length"));

            (int status, string item) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Equal(200, status);
            Assert.Equal("Web Scene", JsonDocument.Parse(item).RootElement.GetProperty("type").GetString());

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }

    /// <summary>A polygon layer's More menu makes a scene layer, and its link opens it in the viewer with its buildings drawn.</summary>
    [Fact]
    public async Task A_polygon_layer_is_made_a_scene_layer_and_opened_in_the_viewer()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await DrawInSoftwareAsync();
            await OpenAsync("/studio/#/service/hosted/ci_buildings", token, cookie);
            await WaitForAsync("!!document.getElementById('createSceneOpen')", "A polygon layer's page offers no Create scene layer.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('createSceneOpen').click(), true)");
            await WaitForAsync("document.getElementById('createScene').open && document.getElementById('csHeight').options.length > 1",
                "The dialog did not offer the layer's number fields as its height.");
            Assert.Equal("floors", await Browser.EvaluateAsync<string>("document.getElementById('csHeight').value"));
            Assert.Equal("3", await Browser.EvaluateAsync<string>("document.getElementById('csScale').value"));

            // The scene layer is made on the server — it is opened below and deleted in finally.
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/content/scenes'], true)");
            await ClickAsync("#createSceneGo");
            await WaitForAsync("!!document.getElementById('createSceneOpenViewer')", "Creating the scene layer did not offer to open it.");
            string href = await Browser.EvaluateAsync<string>("document.getElementById('createSceneOpenViewer').getAttribute('href')") ?? string.Empty;
            id = System.Text.RegularExpressions.Regex.Match(System.Uri.UnescapeDataString(href), "hosted/([0-9a-f]{32})/SceneServer").Groups[1].Value;

            await OpenAsync(href, token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true && scState.scene.layers.length === 1",
                "The scene layer did not open in the viewer.");
            await WaitForAsync("scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "The scene layer did not load.");
            Assert.Equal("scene", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).type"));
            // ADR-270's design review: the header says what was added, then what the scene now is.
            Assert.Matches(@" was added\. A new scene\. 1 layer\.$", await Browser.EvaluateAsync<string>("document.getElementById('status').textContent") ?? string.Empty);

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (!string.IsNullOrEmpty(id))
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }

    /// <summary>D-386: a point layer is offered the same way, its points standing on the ground without a height.</summary>
    [Fact]
    public async Task A_point_layer_is_made_a_point_scene_layer_and_drawn()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await DrawInSoftwareAsync();
            await OpenAsync("/studio/#/service/hosted/ci_EarlyAlert", token, cookie);
            await WaitForAsync("!!document.getElementById('createSceneOpen')", "A point layer's page offers no Create scene layer.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('createSceneOpen').click(), true)");
            await WaitForAsync("document.getElementById('createScene').open && /placed in the scene/.test(document.getElementById('csSays').textContent)",
                "The dialog did not say what a point layer becomes.");
            await WaitForAsync("document.getElementById('csHeight').options.length > 1", "The dialog did not offer the layer's number fields.");
            Assert.Equal("On the ground", await Browser.EvaluateAsync<string>("document.getElementById('csHeight').options[0].textContent"));
            Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.getElementById('csHeight').value"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('csScaleRow').hidden"), "Metres per unit was offered with no field to scale.");

            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/content/scenes'], true)");
            await ClickAsync("#createSceneGo");
            await WaitForAsync("!!document.getElementById('createSceneOpenViewer')", "Creating the point scene layer did not offer to open it.");
            Assert.Matches(@"was created with \d+\s+points?\.", await Browser.EvaluateAsync<string>("document.getElementById('createSceneDone').textContent") ?? string.Empty);
            string href = await Browser.EvaluateAsync<string>("document.getElementById('createSceneOpenViewer').getAttribute('href')") ?? string.Empty;
            id = System.Text.RegularExpressions.Regex.Match(System.Uri.UnescapeDataString(href), "hosted/([0-9a-f]{32})/SceneServer").Groups[1].Value;

            await OpenAsync(href, token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true && scState.scene.layers.length === 1",
                "The point scene layer did not open in the viewer.");
            await WaitForAsync("scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "The point scene layer did not load.");
            Assert.Equal("point", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).geometryType"));
            await WaitForAsync("/Points/.test(document.getElementById('layerList').textContent)", "The layer list did not call the point scene layer Points.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (!string.IsNullOrEmpty(id))
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }
}
