using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-270: the scene viewer's Scene Viewer tools — a layer's order, opacity, look and pop-up, slides, the basemap and
/// the ground, each kept in the web scene it saves; the analysis tools and the legend, each opened in the scene.
/// </summary>
public sealed class SceneViewerToolsTests : ConsoleTest
{
    /// <summary>As <see cref="SceneViewerTests"/>: the SDK refuses software WebGL unless the caveat flag is dropped.</summary>
    private async Task DrawInSoftwareAsync() =>
        await Browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new
        {
            source = "(() => { const get = HTMLCanvasElement.prototype.getContext; HTMLCanvasElement.prototype.getContext = function (type, attrs) {"
                + " if (attrs && typeof attrs === 'object') attrs = { ...attrs, failIfMajorPerformanceCaveat: false };"
                + " return get.call(this, type, attrs); }; })();",
        });

    private async Task OpenNewSceneAsync(string token, string cookie)
    {
        await DrawInSoftwareAsync();
        await OpenAsync("/studio/scene.html", token, cookie);
        await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The scene viewer did not open a new scene.");
    }

    private async Task AddByAddressAsync(string url, int expected)
    {
        await Browser.EvaluateAsync<bool>($"(document.getElementById('addUrl').value = {JsonSerializer.Serialize(url)}, document.getElementById('addUrlButton').click(), true)");
        await WaitForAsync($"scState.scene.layers.length === {expected} && /was added/.test(document.getElementById('addSaid').textContent)",
            $"Adding {url} did not say it was added.");
    }

    private async Task<string> SaveAsAsync(string title)
    {
        await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/addItem', '/update'], true)");
        await Browser.EvaluateAsync<bool>($"(document.getElementById('sceneTitle').value = {JsonSerializer.Serialize(title)}, document.getElementById('sceneTitle').dispatchEvent(new Event('input')), true)");
        await ClickAsync("#headSave");
        await WaitForAsync("/Saved as/.test(document.getElementById('status').textContent)", "Save did not say the scene was saved.");
        string id = await Browser.EvaluateAsync<string>("scState.id") ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(id));
        return id;
    }

    private async Task<JsonElement> SavedSceneAsync(string id)
    {
        (int status, string body) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}/data?f=json");
        Assert.Equal(200, status);
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>
    /// Two layers are reordered, one is made half transparent, extruded by its floors and given a pop-up whose title is a
    /// field; the saved web scene carries each, and the scene opens again in that order with that look.
    /// </summary>
    [Fact]
    public async Task A_layers_order_opacity_look_and_popup_are_kept_in_the_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string buildings = $"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0";
        string sites = $"{Root}/rest/services/hosted/ci_EarlyAlert/FeatureServer/0";
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            Assert.Equal("true", await Browser.EvaluateAsync<string>("document.getElementById('tab-add').getAttribute('aria-selected')"));
            await AddByAddressAsync(buildings, 1);
            await AddByAddressAsync(sites, 2);
            await ClickAsync("#tab-layers");

            // The list is drawn top first: the sites, added last, are on top; moving them down puts the buildings on top.
            string firstRow = "document.querySelector('#layerList [data-layer]')";
            Assert.Equal("Features", await Browser.EvaluateAsync<string>($"{firstRow}.querySelector('span.lkind').textContent.trim()"));
            string sitesTitle = await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(1).title") ?? string.Empty;
            await ClickAsync("#layerList [data-layer] [data-act=down]");
            await WaitForAsync($"scState.scene.layers.getItemAt(0).title === {JsonSerializer.Serialize(sitesTitle)}", "Move down did not lower the top layer.");
            Assert.Matches("was moved down", await Browser.EvaluateAsync<string>("document.getElementById('status').textContent") ?? string.Empty);

            // The buildings' settings: half opacity, extruded by floors × 3, a pop-up titled by a field.
            await ClickAsync("#layerList [data-layer] [data-act=settings]");
            await WaitForAsync("!!document.querySelector('[data-detail] [data-style=shape]')", "The buildings' settings offered no look.");
            await Browser.EvaluateAsync<bool>("(() => { const r = document.querySelector('[data-detail] [data-act=opacity]'); r.value = '50'; r.dispatchEvent(new Event('input', { bubbles: true })); return true; })()");
            Assert.Equal(0.5, await Browser.EvaluateAsync<double>("scState.scene.layers.getItemAt(1).opacity"));

            Assert.True(await Browser.EvaluateAsync<bool>("[...document.querySelectorAll('[data-detail] [data-style=shape] option')].some(o => o.value === 'extrude')"),
                "A polygon layer was not offered Extruded.");
            await Browser.EvaluateAsync<bool>("(() => { const d = document.querySelector('[data-detail]');"
                + " d.querySelector('[data-style=shape]').value = 'extrude';"
                + " d.querySelector('[data-style=sizeField]').value = 'floors';"
                + " d.querySelector('[data-style=sizeScale]').value = '3';"
                + " d.querySelector('[data-style=color]').value = '#c0392b';"
                + " d.querySelector('[data-style=shape]').dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("/The look of/.test(document.getElementById('status').textContent)", "Apply look did not say it was set.");
            Assert.Equal("extrude", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(1).renderer.symbol.symbolLayers.getItemAt(0).type"));

            Assert.True(await Browser.EvaluateAsync<bool>("[...document.querySelectorAll('[data-detail] [data-popup=title] option')].some(o => o.value === 'floors')"),
                "The pop-up's title did not offer the layer's fields.");
            // Applied as it changes: there is no Apply button to forget (ADR-270's design review).
            await Browser.EvaluateAsync<bool>("(() => { const t = document.querySelector('[data-detail] [data-popup=title]'); t.value = 'floors'; t.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("/The pop-up of/.test(document.getElementById('status').textContent)", "Apply pop-up did not say it was set.");
            Assert.Equal("{floors}", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(1).popupTemplate.title"));

            // A slide gone to does not close the open settings or undo what they set (the review's blocker).
            await ClickAsync("#tab-slides");
            await ClickAsync("#slideAdd");
            await WaitForAsync("scState.scene.presentation.slides.length === 1", "Capture slide did not add a slide.");
            await ClickAsync("#slideList [data-act=go]");
            await ClickAsync("#tab-layers");
            Assert.Equal("extrude", await Browser.EvaluateAsync<string>("document.querySelector('[data-detail] [data-style=shape]')?.value"));

            id = await SaveAsAsync("zz console scene tools");
            JsonElement saved = await SavedSceneAsync(id);
            JsonElement[] layers = saved.GetProperty("operationalLayers").EnumerateArray().ToArray();
            Assert.Equal(2, layers.Length);
            Assert.Equal(sitesTitle, layers[0].GetProperty("title").GetString());
            JsonElement building = layers[1];
            Assert.Equal(0.5, building.GetProperty("opacity").GetDouble());
            JsonElement renderer = building.GetProperty("layerDefinition").GetProperty("drawingInfo").GetProperty("renderer");
            Assert.Equal("Extrude", renderer.GetProperty("symbol").GetProperty("symbolLayers")[0].GetProperty("type").GetString());
            Assert.Contains(renderer.GetProperty("visualVariables").EnumerateArray(),
                v => v.GetProperty("type").GetString() == "sizeInfo" && v.GetProperty("valueExpression").GetString() == "$feature.floors * 3");
            Assert.Equal("{floors}", building.GetProperty("popupInfo").GetProperty("title").GetString());

            // Opened again: the order, the look and the form read back from the renderer.
            await OpenAsync($"/studio/scene.html?id={id}", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The saved scene did not open.");
            Assert.Equal(sitesTitle, await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).title"));
            await ClickAsync("#layerList [data-layer] [data-act=settings]");
            await WaitForAsync("document.querySelector('[data-detail] [data-style=shape]')?.value === 'extrude'", "The reopened form did not read the look back.");
            Assert.Equal("floors", await Browser.EvaluateAsync<string>("document.querySelector('[data-detail] [data-style=sizeField]').value"));

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

    /// <summary>
    /// Slides are captured, renamed, reordered and deleted, and played; the basemap is set to none and the ground made
    /// half transparent with the camera allowed below it; the saved web scene keeps all of it.
    /// </summary>
    [Fact]
    public async Task Slides_the_basemap_and_the_ground_are_kept_in_the_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);

            await ClickAsync("#tab-slides");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('slidePlay').disabled"), "Play was offered with no slides.");
            await ClickAsync("#slideAdd");
            await WaitForAsync("scState.scene.presentation.slides.length === 1", "Capture slide did not add a slide.");
            await Browser.EvaluateAsync<bool>("(scState.view.goTo({ heading: 90 }, { animate: false }), true)");
            await ClickAsync("#slideAdd");
            await WaitForAsync("scState.scene.presentation.slides.length === 2", "A second capture did not add a slide.");
            await ClickAsync("#slideAdd");
            await WaitForAsync("scState.scene.presentation.slides.length === 3", "A third capture did not add a slide.");

            await Browser.EvaluateAsync<bool>("(() => { const i = document.querySelector('#slideList [data-act=rename]'); i.value = 'Overview'; i.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            Assert.Equal("Overview", await Browser.EvaluateAsync<string>("scState.scene.presentation.slides.getItemAt(0).title.text"));
            await ClickAsync("#slideList li:nth-child(3) [data-act=delete]");
            await WaitForAsync("scState.scene.presentation.slides.length === 2", "Delete did not remove the slide.");
            await ClickAsync("#slideList li:nth-child(2) [data-act=up]");
            await WaitForAsync("scState.scene.presentation.slides.getItemAt(1).title.text === 'Overview'", "Moving a slide earlier did not reorder the slides.");

            await Browser.EvaluateAsync<bool>("(window.__slideSeconds = 0.2, true)");
            await ClickAsync("#slidePlay");
            await WaitForAsync("/The slides were played/.test(document.getElementById('status').textContent)", "Play did not go through the slides.");
            Assert.Equal(await Browser.EvaluateAsync<string>("scState.scene.presentation.slides.getItemAt(1).id"),
                await Browser.EvaluateAsync<string>("scState.currentSlide"));

            await ClickAsync("#tab-basemap");
            await WaitForAsync("!!document.querySelector('#baseList [data-base=none]')", "The basemap list offered no None.");
            // OpenStreetMap exactly once: the portal's default is often OpenStreetMap already (ADR-270's design review).
            Assert.Equal(1, await Browser.EvaluateAsync<int>(
                "scState.gallery.filter(e => (e.json.baseMapLayers || []).some(l => l.layerType === 'OpenStreetMap')).length"));
            await ClickAsync("#baseList [data-base=none]");
            await WaitForAsync("document.querySelector('#baseList [data-base=none]').getAttribute('aria-pressed') === 'true'", "None was not marked as the basemap.");

            await ClickAsync("#tab-ground");
            await Browser.EvaluateAsync<bool>("(() => { const r = document.getElementById('groundOpacity'); r.value = '50'; r.dispatchEvent(new Event('input')); return true; })()");
            await ClickAsync("#groundUnder");
            Assert.Equal("none", await Browser.EvaluateAsync<string>("scState.scene.ground.navigationConstraint.type"));

            id = await SaveAsAsync("zz console scene slides");
            JsonElement saved = await SavedSceneAsync(id);
            JsonElement[] slides = saved.GetProperty("presentation").GetProperty("slides").EnumerateArray().ToArray();
            Assert.Equal(2, slides.Length);
            Assert.Equal("Overview", slides[1].GetProperty("title").GetProperty("text").GetString());
            Assert.Equal(0, saved.GetProperty("baseMap").GetProperty("baseMapLayers").GetArrayLength());
            JsonElement ground = saved.GetProperty("ground");
            Assert.Equal(50, ground.GetProperty("transparency").GetDouble());
            Assert.Equal("none", ground.GetProperty("navigationConstraint").GetProperty("type").GetString());

            await OpenAsync($"/studio/scene.html?id={id}", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The saved scene did not open.");
            Assert.Equal(2, await Browser.EvaluateAsync<int>("document.querySelectorAll('#slideList [data-slide]').length"));
            Assert.Equal("50", await Browser.EvaluateAsync<string>("document.getElementById('groundOpacity').value"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('groundUnder').checked"), "The reopened scene forgot the camera may go below the ground.");

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

    /// <summary>
    /// A local scene is made, a feature layer projected into it, clipped to the view and saved; the web scene keeps its
    /// type and its clip and opens again local and clipped. A WGS 84 scene layer is refused with the reason.
    /// </summary>
    [Fact]
    public async Task A_local_scene_is_clipped_and_kept()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;
        string? sceneLayer = null;

        try
        {
            await DrawInSoftwareAsync();
            await OpenAsync("/studio/scene.html", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The scene viewer did not open a new scene.");
            Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('sceneMode').disabled"), "A new scene could not choose its type.");
            await Browser.EvaluateAsync<bool>("(document.getElementById('sceneMode').value = 'local', document.getElementById('sceneMode').dispatchEvent(new Event('change')), true)");
            await WaitForAsync("location.search === '?mode=local' && typeof scState !== 'undefined' && scState.opened === true", "Choosing Local did not start a local scene.");
            Assert.Equal("local", await Browser.EvaluateAsync<string>("scState.view.viewingMode"));

            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('sceneMode').disabled"), "The type could still change after a layer was added.");

            // A WGS 84 scene layer: said, not left undrawn.
            (int status, string made) = await AdminAsync(HttpMethod.Post, "/content/scenes",
                """{"service":"ci_buildings","folder":"hosted","layer":0,"heightField":"floors","heightScale":3,"title":"zz console local refused"}""");
            Assert.Equal(201, status);
            sceneLayer = JsonDocument.Parse(made).RootElement.GetProperty("id").GetString();
            await Browser.EvaluateAsync<bool>($"(document.getElementById('addUrl').value = {JsonSerializer.Serialize($"{Root}/rest/services/hosted/{sceneLayer}/SceneServer")}, document.getElementById('addUrlButton').click(), true)");
            await WaitForAsync("/global scene only/.test(document.getElementById('addSaid').textContent)", "A WGS 84 scene layer was not refused with the reason in a local scene.");
            Assert.Equal(1, await Browser.EvaluateAsync<int>("scState.scene.layers.length"));

            await ClickAsync("#tab-ground");
            Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('clipPart').hidden"), "A local scene offered no clipping.");
            await ClickAsync("#clipToView");
            await WaitForAsync("!!scState.view.clippingArea && scState.scene.clippingEnabled === true", "Clip to the current view did not clip.");

            id = await SaveAsAsync("zz console local scene");
            JsonElement saved = await SavedSceneAsync(id);
            Assert.Equal("local", saved.GetProperty("viewingMode").GetString());
            Assert.True(saved.TryGetProperty("clippingArea", out JsonElement clip),
                "The saved scene has no clippingArea; it has " + string.Join(", ", saved.EnumerateObject().Select(p => p.Name))
                + (saved.TryGetProperty("initialState", out JsonElement state) ? "; initialState: " + state.ToString()[..System.Math.Min(600, state.ToString().Length)] : ""));
            // The web scene specification's clippingArea: { clip, geometry }.
            Assert.True(clip.TryGetProperty("clip", out JsonElement on) && on.GetBoolean(), "clippingArea was " + clip);
            Assert.True(clip.GetProperty("geometry").TryGetProperty("xmin", out _), "clippingArea was " + clip);
            (status, string item) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            Assert.Contains("ViewingMode-local", item);

            await OpenAsync($"/studio/scene.html?id={id}", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The saved local scene did not open.");
            Assert.Equal("local", await Browser.EvaluateAsync<string>("scState.view.viewingMode"));
            Assert.True(await Browser.EvaluateAsync<bool>("!!scState.view.clippingArea"), "The reopened scene was not clipped.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }

            if (sceneLayer is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{sceneLayer}");
            }
        }
    }

    /// <summary>
    /// ADR-182 and ADR-270 §5.8: an image service made an elevation surface is offered as the ground, one that is not is
    /// not, and the chosen ground is kept in the web scene as a tiled elevation layer.
    /// </summary>
    [Fact]
    public async Task An_elevation_surface_is_offered_as_the_ground_and_kept()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await ClickAsync("#tab-ground");
            await WaitForAsync("!document.getElementById('groundNone').hidden || document.getElementById('groundSelect').options.length > 1",
                "The ground's choices were never filled.");
            Assert.False(await Browser.EvaluateAsync<bool>("[...document.getElementById('groundSelect').options].some(o => /ci_imagery/.test(o.value))"),
                "An image service that is not an elevation surface was offered as the ground.");

            (int status, string said) = await AdminAsync(HttpMethod.Put, "/admin/coverages/ci_imagery/elevation?folder=hosted", """{"elevation":true}""");
            Assert.True(status == 200, $"Making ci_imagery an elevation surface answered {status}: {said}");

            await OpenNewSceneAsync(token, cookie);
            await ClickAsync("#tab-ground");
            await WaitForAsync("[...document.getElementById('groundSelect').options].some(o => /ci_imagery/.test(o.value))",
                "An elevation surface was not offered as the ground.");
            await Browser.EvaluateAsync<bool>("(() => { const g = document.getElementById('groundSelect'); g.value = [...g.options].find(o => /ci_imagery/.test(o.value)).value; g.dispatchEvent(new Event('change')); return true; })()");
            await WaitForAsync("scState.scene.ground.layers.length === 1", "Choosing the surface did not make it the ground.");

            id = await SaveAsAsync("zz console scene ground");
            JsonElement ground = (await SavedSceneAsync(id)).GetProperty("ground");
            JsonElement layer = ground.GetProperty("layers")[0];
            Assert.Equal("ArcGISTiledElevationServiceLayer", layer.GetProperty("layerType").GetString());
            Assert.Contains("ci_imagery", layer.GetProperty("url").GetString());

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, "/admin/coverages/ci_imagery/elevation?folder=hosted", """{"elevation":false}""");
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }
        }
    }

    /// <summary>
    /// ADR-270 §5.9, from the comparison with ArcGIS Online: a layer's visible range and filter, a slide's description, a
    /// duplicated slide and a slide opened by its address; the Time tool offered only with a time-aware layer.
    /// </summary>
    [Fact]
    public async Task Ranges_filters_slide_descriptions_and_the_time_tool_are_kept()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('#tools [data-tool=time]').hidden"), "Time was offered with no time-aware layer.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('#tools [data-tool=building]').hidden"), "Building explorer was offered with no building layer.");
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_observations/FeatureServer/0", 2);
            await WaitForAsync("!document.querySelector('#tools [data-tool=time]').hidden", "Time was not offered with a time-aware layer.");
            await ClickAsync("#tools [data-tool=time]");
            await WaitForAsync("scState.tool === 'time' && !!document.querySelector('#toolPanel .esri-widget')", "The Time tool did not open.");
            await ClickAsync("#tools [data-tool=time]");

            // The buildings — second in the list, the observations being on top.
            await ClickAsync("#tab-layers");
            await ClickAsync("#layerList li:nth-child(2) [data-act=settings]");
            await WaitForAsync("!!document.querySelector('[data-detail] [data-vis=filter]')", "A feature layer's settings offered no filter.");
            await Browser.EvaluateAsync<bool>("(() => { const s = document.querySelector('[data-detail] [data-vis=minScale]'); s.value = '144448'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            Assert.Equal(144448, await Browser.EvaluateAsync<double>("scState.scene.layers.getItemAt(0).minScale"));

            await Browser.EvaluateAsync<bool>("(() => { const f = document.querySelector('[data-detail] [data-vis=filter]'); f.value = 'no_such_field = 1'; f.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("/was not accepted/.test(document.getElementById('status').textContent)", "A filter the layer refuses was not said.");
            Assert.Null(await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).definitionExpression"));
            await Browser.EvaluateAsync<bool>("(() => { const f = document.querySelector('[data-detail] [data-vis=filter]'); f.value = 'floors >= 0'; f.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("scState.scene.layers.getItemAt(0).definitionExpression === 'floors >= 0'", "A filter the layer accepts was not kept.");

            await ClickAsync("#tab-slides");
            await ClickAsync("#slideAdd");
            await WaitForAsync("scState.scene.presentation.slides.length === 1", "Capture slide did not add a slide.");
            await Browser.EvaluateAsync<bool>("(() => { const d = document.querySelector('#slideList [data-act=describe]'); d.value = 'The district from the south.'; d.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await ClickAsync("#slideList [data-act=duplicate]");
            await WaitForAsync("scState.scene.presentation.slides.length === 2", "Duplicate did not add a slide.");
            Assert.NotEqual(await Browser.EvaluateAsync<string>("scState.scene.presentation.slides.getItemAt(0).id"),
                await Browser.EvaluateAsync<string>("scState.scene.presentation.slides.getItemAt(1).id"));

            id = await SaveAsAsync("zz console scene ranges");
            JsonElement saved = await SavedSceneAsync(id);
            JsonElement buildings = saved.GetProperty("operationalLayers")[0];
            Assert.Equal(144448, buildings.GetProperty("layerDefinition").GetProperty("minScale").GetDouble());
            Assert.Equal("floors >= 0", buildings.GetProperty("layerDefinition").GetProperty("definitionExpression").GetString());
            JsonElement[] slides = saved.GetProperty("presentation").GetProperty("slides").EnumerateArray().ToArray();
            Assert.Equal(2, slides.Length);
            Assert.Equal("The district from the south.", slides[0].GetProperty("description").GetProperty("text").GetString());
            Assert.EndsWith("(copy)", slides[1].GetProperty("title").GetProperty("text").GetString());

            // Scene Viewer's address of a slide: #2 opens the scene at the second.
            await OpenAsync($"/studio/scene.html?id={id}#2", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true && scState.currentSlide === scState.scene.presentation.slides.getItemAt(1).id",
                "#2 did not open the scene at its second slide.");

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

    /// <summary>Every analysis tool opens its widget in the scene and closes again; the legend names a layer; the add list searches.</summary>
    [Fact]
    public async Task Every_analysis_tool_opens_and_the_legend_and_search_answer()
    {
        (string token, string cookie) = await SignInAsync();

        await OpenNewSceneAsync(token, cookie);

        // The add list searches the portal by title and kind.
        await WaitForAsync("document.querySelectorAll('#addList [data-add]').length > 0", "The add list offered nothing.");
        await Browser.EvaluateAsync<bool>("(document.getElementById('addType').value = 'Feature Service', document.getElementById('addType').dispatchEvent(new Event('change')), true)");
        await WaitForAsync("[...document.querySelectorAll('#addList span.lkind')].length > 0 && [...document.querySelectorAll('#addList span.lkind')].every(k => /Feature layer/.test(k.textContent))",
            "Feature layers listed something that is not a feature layer.");
        await Browser.EvaluateAsync<bool>("(document.getElementById('addFilter').value = 'ci_buildings', document.getElementById('addFilter').dispatchEvent(new Event('input')), true)");
        await WaitForAsync("document.querySelectorAll('#addList [data-add]').length >= 1 && [...document.querySelectorAll('#addList span.lname')].every(n => /ci_buildings/i.test(n.textContent))",
            "Searching by a title did not narrow the list to it.");
        await ClickAsync("#addList [data-add]");
        await WaitForAsync("scState.scene.layers.length === 1 && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "Add from the list did not add the layer.");
        await WaitForAsync("/was added/.test(document.getElementById('status').textContent) && !document.querySelector('#addList [data-add]')",
            "The header did not say the layer was added, or the list still offered it again.");

        // The SDK's tools, and since the comparison with ArcGIS Online (ADR-270 §5.10) Location, and Search over the
        // scene's feature layers.
        foreach (string tool in new[] { "distance", "area", "sight", "slice", "shadow", "daylight", "weather", "location", "search", "profile" })
        {
            await ClickAsync($"#tools [data-tool={tool}]");
            await WaitForAsync($"scState.tool === '{tool}' && !!document.querySelector('#toolPanel .esri-widget')", $"The {tool} tool did not open its widget.");
            Assert.Equal("true", await Browser.EvaluateAsync<string>($"document.querySelector('#tools [data-tool={tool}]').getAttribute('aria-pressed')"));
        }

        await ClickAsync("#tools [data-tool=profile]");
        await WaitForAsync("scState.tool === null && !document.querySelector('#toolPanel .esri-widget')", "Clicking the open tool again did not close it.");
        await ClickAsync("#tools [data-tool=slice]");
        await WaitForAsync("scState.tool === 'slice'", "Slice did not open again.");
        await Browser.EvaluateAsync<bool>("(document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })), true)");
        await WaitForAsync("scState.tool === null && document.activeElement === document.querySelector('#tools [data-tool=slice]')",
            "Escape did not close the tool and give focus back to its button.");

        await ClickAsync("#tab-legend");
        await WaitForAsync("/ci_buildings/i.test(document.getElementById('legendBox').textContent)", "The legend did not name the layer.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
