using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
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

    /// <summary>
    /// Opens a tool as a reader does (ADR-279): its group's button on the right toolbar, then the tool in the group's
    /// panel when the group holds more than one.
    /// </summary>
    private async Task OpenToolAsync(string tool)
    {
        string group = await Browser.EvaluateAsync<string>($"SC_TOOLS.find(t => t.key === '{tool}').group") ?? string.Empty;
        if (await Browser.EvaluateAsync<bool>($"scState.group !== '{group}' || document.getElementById('side').hidden"))
        {
            await ClickAsync($"#tools [data-group={group}]");
        }

        await WaitForAsync($"!document.getElementById('side').hidden && scState.group === '{group}'", $"{group}'s button did not open its panel.");
        if (await Browser.EvaluateAsync<bool>($"scState.tool !== '{tool}'"))
        {
            await ClickAsync($"#toolPick [data-tool={tool}]");
        }
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
            await ClickAsync("#layerList [data-layer] [data-act=select]");
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
            await ClickAsync("#layerList [data-layer] [data-act=select]");
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

            // ADR-270 §5.19 (SDK 4.34): the second slide is played as a cover, and the first, updated with the legend open,
            // keeps that its legend is shown.
            await Browser.EvaluateAsync<bool>("(() => { const s = document.querySelector('#slideList li:nth-child(2) [data-act=layout]'); s.value = 'cover';"
                + " s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            Assert.Equal("cover", await Browser.EvaluateAsync<string>("scState.scene.presentation.slides.getItemAt(1).layout"));
            await ClickAsync("#tab-legend");
            await Browser.EvaluateAsync<bool>("(document.querySelector('#slideList li:nth-child(1) [data-act=update]').click(), true)");
            await WaitForAsync("scState.scene.presentation.slides.getItemAt(0).elements?.legendInfo?.visible === true",
                "Updating a slide with the legend open did not keep that the legend is shown.");
            await ClickAsync("#tab-slides");

            // ADR-270 §5.16: Play captions each slide over the scene, and takes the caption away when it ends.
            await Browser.EvaluateAsync<bool>("(window.__slideSeconds = 1.5, true)");
            await ClickAsync("#slidePlay");
            await WaitForAsync("!!document.getElementById('slideCaption') && !document.getElementById('slideCaption').hidden"
                + " && /1 of 2/.test(document.getElementById('slideCaption').textContent)", "Play showed no caption for the first slide.");
            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#slideCaption #captionStop')"), "The caption offered no Stop.");
            await WaitForAsync("/2 of 2/.test(document.getElementById('slideCaption')?.textContent || '') && /Overview/.test(document.getElementById('slideCaption').textContent)",
                "The caption did not follow Play to the second slide, by its title.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('slideCaption').classList.contains('cover')"),
                "A slide shown as a cover was played with a caption.");
            await WaitForAsync("/The slides were played/.test(document.getElementById('status').textContent)", "Play did not go through the slides.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('slideCaption').hidden"), "The caption stayed after Play ended.");
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
            Assert.Equal("cover", slides[1].GetProperty("layout").GetString());
            Assert.Contains("legendInfo", slides[0].GetRawText(), StringComparison.Ordinal);
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
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('#tools [data-group=time]').hidden"), "Time was offered with no time-aware layer.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('#tools [data-group=building]').hidden"), "Building explorer was offered with no building layer.");
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_observations/FeatureServer/0", 2);
            await WaitForAsync("!document.querySelector('#tools [data-group=time]').hidden", "Time was not offered with a time-aware layer.");
            await ClickAsync("#tools [data-group=time]");
            await WaitForAsync("scState.tool === 'time' && !!document.querySelector('#toolPanel .esri-widget')", "The Time tool did not open.");
            await ClickAsync("#tools [data-group=time]");
            await WaitForAsync("scState.tool === null && document.getElementById('side').hidden", "Time's button did not close its panel.");

            // The buildings — second in the list, the observations being on top.
            await ClickAsync("#tab-layers");
            await ClickAsync("#layerList li:nth-child(2) [data-act=select]");
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

            // The copy left out of the presentation: Scene Viewer's Hide in presentation.
            await Browser.EvaluateAsync<bool>("(() => { const c = document.querySelectorAll('#slideList [data-act=shown]')[1]; c.checked = false; c.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            Assert.True(await Browser.EvaluateAsync<bool>("scState.scene.presentation.slides.getItemAt(1).hidden"), "The slide was not left out of the presentation.");

            id = await SaveAsAsync("zz console scene ranges");
            JsonElement saved = await SavedSceneAsync(id);
            JsonElement buildings = saved.GetProperty("operationalLayers")[0];
            Assert.Equal(144448, buildings.GetProperty("layerDefinition").GetProperty("minScale").GetDouble());
            Assert.Equal("floors >= 0", buildings.GetProperty("layerDefinition").GetProperty("definitionExpression").GetString());
            JsonElement[] slides = saved.GetProperty("presentation").GetProperty("slides").EnumerateArray().ToArray();
            Assert.Equal(2, slides.Length);
            Assert.Equal("The district from the south.", slides[0].GetProperty("description").GetProperty("text").GetString());
            Assert.EndsWith("(copy)", slides[1].GetProperty("title").GetProperty("text").GetString());
            Assert.True(slides[1].TryGetProperty("hidden", out JsonElement hidden) && hidden.GetBoolean(), "The saved slide is not hidden: " + slides[1]);

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

    /// <summary>A member with the user role, signed in with the password they chose — as ItemMetadataCommentsScreenTests makes one.</summary>
    private async Task<string> MemberAsync(string name)
    {
        (int made, string answer) = await AdminAsync(HttpMethod.Post, "/admin/members", JsonSerializer.Serialize(new { name, role = "user" }));
        Assert.True(made == 201, $"Making {name} answered {made}.");
        string issued = JsonDocument.Parse(answer).RootElement.GetProperty("password").GetString()!;

        async Task<string> LoginAsync(string password)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/rest/auth/login"));
            request.Content = new StringContent(JsonSerializer.Serialize(new { name, password }), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"Signing in as {name} answered {(int)response.StatusCode}.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        }

        string first = await LoginAsync(issued);
        string chosen = "Zz" + Guid.NewGuid().ToString("N")[..20] + "!7";
        using HttpRequestMessage change = new(HttpMethod.Post, new Uri($"{Root}/rest/auth/password"));
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", first);
        change.Content = new StringContent(JsonSerializer.Serialize(new { currentPassword = issued, newPassword = chosen }), Encoding.UTF8, "application/json");
        using HttpResponseMessage changed = await Http.SendAsync(change);
        Assert.True(changed.IsSuccessStatusCode, $"Replacing {name}'s issued password answered {(int)changed.StatusCode}.");
        return await LoginAsync(chosen);
    }

    /// <summary>
    /// ADR-269 condition 4, ADR-270 condition 1: a member who is not the scene's owner, on a fresh page, sees what the scene
    /// is and is offered nothing that changes it — no Save, no title, no Add tab, sentences for the basemap and ground — and
    /// the tab row stays visible however long the pane is (the confirming review's blocker).
    /// </summary>
    [Fact]
    public async Task A_reader_who_is_not_the_owner_is_offered_nothing_that_changes_the_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;
        string member = "zz_scene_reader_" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await OpenNewSceneAsync(token, cookie);
            // The first run lands on Add, the longest pane: the tab row is still there to leave it by.
            Assert.True(await Browser.EvaluateAsync<double>("document.getElementById('tabs').getBoundingClientRect().height") >= 30,
                "The tab row collapsed on the first run.");
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            await ClickAsync("#tab-layers");
            await ClickAsync("#layerList [data-act=select]");
            await WaitForAsync("!!document.querySelector('[data-detail] [data-style=shape]')", "The settings did not open.");
            Assert.True(await Browser.EvaluateAsync<double>("document.getElementById('tabs').getBoundingClientRect().height") >= 30,
                "The tab row collapsed with a layer's settings open.");
            await ClickAsync("#tab-slides");
            await ClickAsync("#slideAdd");
            await WaitForAsync("scState.scene.presentation.slides.length === 1", "Capture slide did not add a slide.");
            id = await SaveAsAsync("zz console scene for a reader");
            (int shared, string said) = await AdminAsync(HttpMethod.Put, $"/content/documents/{id}",
                JsonSerializer.Serialize(new { title = "zz console scene for a reader", sharing = "public" }));
            Assert.True(shared == 200, $"Sharing the scene answered {shared}: {said}");

            string reader = await MemberAsync(member);
            await OpenAsync($"/studio/scene.html?id={id}", reader);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The shared scene did not open for a reader.");
            Assert.Equal(member, await Browser.EvaluateAsync<string>("scState.user"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('headSave').hidden"), "A reader was offered Save.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('sceneDetails').hidden"), "A reader was offered the title.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('tab-add').hidden"), "A reader was offered the Add tab.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#layerList [data-act=up], #layerList [data-act=remove]')"),
                "A reader was offered to reorder or remove a layer.");
            await ClickAsync("#tab-ground");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('groundControls').hidden"), "A reader was offered the ground's controls.");
            Assert.Matches("Only the scene's owner can change it", await Browser.EvaluateAsync<string>("document.getElementById('groundReadOnly').textContent") ?? string.Empty);
            Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('sceneQuality').closest('[hidden]') !== null"), "A reader was not offered the drawing quality.");
            await ClickAsync("#tab-basemap");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('baseList').hidden"), "A reader was offered the basemap list.");
            await ClickAsync("#tab-slides");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('slideAdd').hidden"), "A reader was offered Capture slide.");
            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#slideList [data-act=go]') && !document.querySelector('#slideList [data-act=delete]')"),
                "A reader could not go to the slide, or was offered Delete.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            if (id is not null)
            {
                await AdminAsync(HttpMethod.Delete, $"/content/documents/{id}");
            }

            await AdminAsync(HttpMethod.Delete, $"/admin/members/{member}");
        }
    }

    /// <summary>Uploads a document as an administrator, as Content's upload does, and answers its id.</summary>
    private async Task<string> UploadAsync(string fileName, string mediaType, string content, string title)
    {
        (string token, _) = await SignInAsync();
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(title), "title");
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Root}/content/documents")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Uploading {fileName} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// ADR-270 §5.11: a CSV or GeoJSON file in the reader's content is offered in the add list, drawn from the document's
    /// own data, and kept in the web scene as a CSV or GeoJSON layer — Scene Viewer's adding a file.
    /// </summary>
    [Fact]
    public async Task A_csv_or_geojson_file_from_content_is_added_and_kept()
    {
        (string token, string cookie) = await SignInAsync();
        string csv = await UploadAsync("zz-scene-points.csv", "text/csv",
            "name,latitude,longitude\nAnkara,39.93,32.86\nIstanbul,41.01,28.98\n", "zz console scene csv");
        string geojson = await UploadAsync("zz-scene-area.geojson", "application/geo+json",
            """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"a square"},"geometry":{"type":"Polygon","coordinates":[[[32.8,39.9],[32.9,39.9],[32.9,40.0],[32.8,40.0],[32.8,39.9]]]}}]}""",
            "zz console scene geojson");
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await Browser.EvaluateAsync<bool>("(document.getElementById('addType').value = 'CSV', document.getElementById('addType').dispatchEvent(new Event('change')), true)");
            await WaitForAsync($"!!document.querySelector('#addList [data-item=\"{csv}\"]')", "The CSV file was not offered among CSV files.");
            await ClickAsync($"#addList [data-item=\"{csv}\"]");
            await WaitForAsync("scState.scene.layers.length === 1 && scState.scene.layers.getItemAt(0).type === 'csv' && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'",
                "The CSV file was not added as a CSV layer.");

            await Browser.EvaluateAsync<bool>("(document.getElementById('addType').value = 'GeoJson', document.getElementById('addType').dispatchEvent(new Event('change')), true)");
            await WaitForAsync($"!!document.querySelector('#addList [data-item=\"{geojson}\"]')", "The GeoJSON file was not offered among GeoJSON files.");
            await ClickAsync($"#addList [data-item=\"{geojson}\"]");
            await WaitForAsync("scState.scene.layers.length === 2 && scState.scene.layers.getItemAt(1).type === 'geojson' && scState.scene.layers.getItemAt(1).loadStatus === 'loaded'",
                "The GeoJSON file was not added as a GeoJSON layer.");

            id = await SaveAsAsync("zz console scene files");
            JsonElement[] layers = (await SavedSceneAsync(id)).GetProperty("operationalLayers").EnumerateArray().ToArray();
            Assert.Equal("CSV", layers[0].GetProperty("layerType").GetString());
            Assert.Contains($"/content/documents/{csv}/data", layers[0].GetProperty("url").GetString());
            Assert.Equal("GeoJSON", layers[1].GetProperty("layerType").GetString());

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            foreach (string? made in new[] { id, csv, geojson })
            {
                if (made is not null)
                {
                    await AdminAsync(HttpMethod.Delete, $"/content/documents/{made}");
                }
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
            await OpenToolAsync(tool);
            await WaitForAsync($"scState.tool === '{tool}' && !!document.querySelector('#toolPanel .esri-widget')", $"The {tool} tool did not open its widget.");
            Assert.Equal("true", await Browser.EvaluateAsync<string>($"document.querySelector('#tools [data-group=' + SC_TOOLS.find(t => t.key === '{tool}').group + ']').getAttribute('aria-expanded')"));
        }

        // ADR-270 §5.18 (SDK 4.34, ADR-273): volume, viewshed and dimensions, each an analysis in the view while its tool
        // is open, and taken out of the scene when it closes.
        foreach ((string tool, string type) in new[] { ("volume", "volume-measurement"), ("viewshed", "viewshed"), ("dimension", "dimension") })
        {
            await OpenToolAsync(tool);
            await WaitForAsync($"scState.tool === '{tool}' && !!document.querySelector('#toolPanel .sc-analysis #anStart')"
                + $" && scState.view.analyses.some(a => a.type === '{type}')", $"The {tool} tool did not open its analysis.");
            await ClickAsync("#anStart");
            await WaitForAsync("!document.getElementById('anDone').hidden", $"Start did not begin placing a {tool}.");
            await ClickAsync("#anDone");
            await WaitForAsync("document.getElementById('anDone').hidden && /placed/.test(document.getElementById('anSays').textContent)", $"Done did not end placing a {tool}.");
        }

        // A dimension placed through the analysis is measured, and the scene keeps no analysis once the tool closes.
        await Browser.EvaluateAsync<bool>("(scRequire(['esri/analysis/LengthDimension', 'esri/geometry/Point']).then(([LengthDimension, Point]) => {"
            + " const a = scState.tools.dimension.analysis; const c = scState.view.center;"
            + " a.dimensions.add(new LengthDimension({ startPoint: new Point({ longitude: c.longitude, latitude: c.latitude, z: 0 }),"
            + " endPoint: new Point({ longitude: c.longitude + 0.001, latitude: c.latitude, z: 0 }) })); }), true)");
        await WaitForAsync("scState.tools.dimension.analysisView.results.length === 1 && scState.tools.dimension.analysisView.results.getItemAt(0).length?.value > 50",
            "A dimension of about 90 m was not measured.");
        // The open tool's pill stays pressed (a choice of one); the group's own button closes the tool and its panel.
        await ClickAsync("#toolPick [data-tool=dimension]");
        Assert.Equal("dimension", await Browser.EvaluateAsync<string>("scState.tool"));
        await ClickAsync("#tools [data-group=measure]");
        await Task.Delay(1500);
        string closed = await Browser.EvaluateAsync<string>("JSON.stringify({ tool: scState.tool, side: document.getElementById('side').hidden, group: scState.group, analyses: scState.view.analyses.toArray().map(a => a.type) })") ?? "";
        Assert.True(closed.Contains("\"tool\":null", StringComparison.Ordinal) && closed.Contains("\"side\":true", StringComparison.Ordinal) && closed.Contains("\"analyses\":[]", StringComparison.Ordinal),
            "Closing Measure left the scene as " + closed);

        await OpenToolAsync("profile");
        await WaitForAsync("scState.tool === 'profile'", "The profile tool did not open again.");
        await ClickAsync("#tools [data-group=analyze]");
        await WaitForAsync("scState.tool === null && !document.querySelector('#toolPanel .esri-widget')", "The group's button did not close its tool.");

        // Escape hides the panel and gives focus back to the group's button, and keeps the tool and what it drew: one key
        // must not wipe a measurement (design review). The group's button shows it again as it was.
        await OpenToolAsync("slice");
        await WaitForAsync("scState.tool === 'slice'", "Slice did not open again.");
        await Browser.EvaluateAsync<bool>("(document.querySelector('#tools [data-group=analyze]').focus(), document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })), true)");
        await WaitForAsync("scState.tool === 'slice' && document.getElementById('side').hidden && document.activeElement === document.querySelector('#tools [data-group=analyze]')",
            "Escape did not hide the panel, keep the tool and give focus back to its group's button.");
        await ClickAsync("#tools [data-group=analyze]");
        await WaitForAsync("!document.getElementById('side').hidden && scState.tool === 'slice' && !!document.querySelector('#toolPanel .esri-widget')",
            "The group's button did not show the hidden tool again as it was.");

        await ClickAsync("#tab-legend");
        await WaitForAsync("/ci_buildings/i.test(document.getElementById('legendBox').textContent)", "The legend did not name the layer.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-271: "Only within the current view" asks the portal's search for a box, so a layer far from the view is not
    /// offered and one under it is, and the list follows the camera when it settles.
    /// </summary>
    [Fact]
    public async Task The_add_list_follows_the_view_when_asked_to()
    {
        (string token, string cookie) = await SignInAsync();

        (int status, string search) = await AdminAsync(HttpMethod.Get, "/sharing/rest/search?q=title:ci_buildings&f=json");
        Assert.Equal(200, status);
        string item = JsonDocument.Parse(search).RootElement.GetProperty("results").EnumerateArray()
            .First(r => r.GetProperty("type").GetString() == "Feature Service").GetProperty("id").GetString()!;
        (status, string described) = await AdminAsync(HttpMethod.Get, $"/sharing/rest/content/items/{item}?f=json");
        Assert.Equal(200, status);
        double[][] extent = [.. JsonDocument.Parse(described).RootElement.GetProperty("extent").EnumerateArray()
            .Select(corner => corner.EnumerateArray().Select(v => v.GetDouble()).ToArray())];
        Assert.True(extent.Length == 2, "ci_buildings has no extent to look at.");
        string centre = FormattableString.Invariant($"[{(extent[0][0] + extent[1][0]) / 2}, {(extent[0][1] + extent[1][1]) / 2}]");

        await OpenNewSceneAsync(token, cookie);
        await Browser.EvaluateAsync<bool>("(document.getElementById('addType').value = 'Feature Service', document.getElementById('addType').dispatchEvent(new Event('change')), true)");
        await Browser.EvaluateAsync<bool>("(document.getElementById('addFilter').value = 'ci_buildings', document.getElementById('addFilter').dispatchEvent(new Event('input')), true)");
        await WaitForAsync($"!!document.querySelector('#addList [data-item=\"{item}\"]')", "ci_buildings was not offered before the view narrowed the list.");

        // The view far from it, over the South Pacific: the list says nothing lies within the view.
        await Browser.EvaluateAsync<bool>("(scState.view.goTo({ center: [-150, -50], zoom: 9 }, { animate: false }), true)");
        await WaitForAsync("scState.view.stationary && Math.abs(scState.view.center.longitude + 150) < 1", "The view did not move to the South Pacific.");
        await Browser.EvaluateAsync<bool>("(document.getElementById('addInView').checked = true, document.getElementById('addInView').dispatchEvent(new Event('change')), true)");
        await WaitForAsync("/within the current view/.test(document.getElementById('addList').textContent) && !document.querySelector('#addList [data-add]')",
            "A layer far from the view was still offered, or the list did not say why it was empty.");

        // The camera over the layer: it is offered again when the view settles.
        await Browser.EvaluateAsync<bool>($"(scState.moved = true, scState.view.goTo({{ center: {centre}, zoom: 14 }}, {{ animate: false }}), true)");
        await WaitForAsync($"!!document.querySelector('#addList [data-item=\"{item}\"]')", "The layer under the view was not offered when the camera settled over it.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-270 §5.13: the Analysis tab runs the server's tools, analysis.js's as Studio's map runs them, on a feature layer
    /// of the scene; a real run's result is a hosted layer that joins the scene.
    /// </summary>
    [Fact]
    public async Task The_scene_runs_an_analysis_and_its_result_joins_the_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string result = $"zz_scene_buffers_{Guid.NewGuid():N}"[..28];

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await WaitForAsync("!document.querySelector('#tools [data-group=server]').hidden", "The scene's owner was not offered Analysis.");
            await ClickAsync("#tools [data-group=server]");
            await WaitForAsync("/hosted feature layers from this server/.test(document.getElementById('azNone').textContent) && !document.getElementById('azNone').hidden",
                "With no layer, Analysis did not say there was nothing to analyze.");

            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            if (await Browser.EvaluateAsync<bool>("document.getElementById('side').hidden"))
            {
                await ClickAsync("#tools [data-group=server]");
            }

            await WaitForAsync("document.querySelectorAll('#azLayer option').length === 1 && !!document.querySelector('#pane-analysis #azTool option[value=CreateBuffers]')",
                "Analysis did not offer the scene's feature layer and its tools.");

            await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById('azTool'); s.value = 'CreateBuffers'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("!!document.getElementById('azDistances')", "Create buffers asked no distances in the scene.");
            await Browser.EvaluateAsync<bool>($"(document.getElementById('azDistances').value = '25', document.getElementById('azName').value = {JsonSerializer.Serialize(result)}, true)");
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/submitJob', '/cancel'], true)");
            await ClickAsync("#analyzeGo");

            // A real job, polled every 1.5 s by analysis.js: up to two minutes, longer than one ten-second wait.
            const string ended = "/was added to the scene|failed|is in Content/.test(document.getElementById('analyzeSays')?.textContent || '')";
            for (int waited = 0; waited < 240 && !await Browser.EvaluateAsync<bool>(ended); waited++)
            {
                await Task.Delay(500);
            }

            await WaitForAsync("/was added to the scene/.test(document.getElementById('analyzeSays')?.textContent || '')",
                "The run's end was not said as a result added to the scene.");
            Assert.Equal(2, await Browser.EvaluateAsync<int>("scState.scene.layers.length"));
            Assert.Equal(result, await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(1).title"));
            Assert.Equal("polygon", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(1).geometryType"));
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('headSave').classList.contains('primary')"),
                "The scene with a result added was not marked as having something to save.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{result}?folder=hosted&drop=true");
        }
    }

    /// <summary>
    /// ADR-270 §5.15: a layer is coloured by a field's values, a colour each, or by classes of a number field, as Scene
    /// Viewer's styles colour one; the web scene keeps the renderer and the form reads it back.
    /// </summary>
    [Fact]
    public async Task A_layer_is_coloured_by_its_values_or_by_classes_and_kept()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            await ClickAsync("#tab-layers");
            await ClickAsync("#layerList [data-layer] [data-act=select]");
            await WaitForAsync("!!document.querySelector('[data-detail] [data-style=colorBy]')", "The look offered no way to colour by a field.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('[data-detail] [data-part=colorField]').hidden"),
                "One colour offered a field.");

            // A colour for each name: the field list offers text fields, not the id fields.
            await Browser.EvaluateAsync<bool>("(() => { const c = document.querySelector('[data-detail] [data-style=colorBy]'); c.value = 'unique';"
                + " c.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("scState.scene.layers.getItemAt(0).renderer.type === 'unique-value'", "A colour for each value was not applied.");
            Assert.Equal("name", await Browser.EvaluateAsync<string>("scState.scene.layers.getItemAt(0).renderer.field"));
            // A colour each takes no colour of the reader's: the well is not offered (design review 2026-11-07).
            Assert.True(await Browser.EvaluateAsync<bool>("document.querySelector('[data-detail] [data-part=colorSwatch]').hidden"),
                "A colour each still offered a colour well that does nothing.");
            Assert.Equal(8, await Browser.EvaluateAsync<int>("scState.scene.layers.getItemAt(0).renderer.uniqueValueInfos.length"));
            Assert.False(await Browser.EvaluateAsync<bool>("[...document.querySelectorAll('[data-detail] [data-style=colorField] option')].some(o => /objectid|globalid/.test(o.value))"),
                "An id field was offered to colour by.");

            id = await SaveAsAsync($"zz scene colours {Guid.NewGuid():N}"[..30]);
            JsonElement renderer = (await SavedSceneAsync(id)).GetProperty("operationalLayers")[0]
                .GetProperty("layerDefinition").GetProperty("drawingInfo").GetProperty("renderer");
            Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
            Assert.Equal("name", renderer.GetProperty("field1").GetString());
            Assert.Equal(8, renderer.GetProperty("uniqueValueInfos").GetArrayLength());

            // Opened again, the form says how the layer is coloured; then classes of floors replace it.
            await OpenAsync($"/studio/scene.html?id={id}", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true", "The saved scene did not open.");
            await ClickAsync("#layerList [data-layer] [data-act=select]");
            await WaitForAsync("document.querySelector('[data-detail] [data-style=colorBy]')?.value === 'unique'", "The reopened form did not read a colour each back.");
            Assert.Equal("name", await Browser.EvaluateAsync<string>("document.querySelector('[data-detail] [data-style=colorField]').value"));

            await Browser.EvaluateAsync<bool>("(() => { const c = document.querySelector('[data-detail] [data-style=colorBy]'); c.value = 'classes';"
                + " c.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
            await WaitForAsync("scState.scene.layers.getItemAt(0).renderer.type === 'class-breaks'", "Classes were not applied.");
            Assert.True(await Browser.EvaluateAsync<bool>("['floors', 'built'].includes(scState.scene.layers.getItemAt(0).renderer.field)"),
                "Classes were not of a number field.");
            Assert.True(await Browser.EvaluateAsync<int>("scState.scene.layers.getItemAt(0).renderer.classBreakInfos.length") >= 2, "Fewer than two classes were made.");
            Assert.Equal("Darkest class", await Browser.EvaluateAsync<string>("document.querySelector('[data-detail] [data-part=colorSwatchLabel]').textContent"));

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
    /// ADR-275: a scene's embed address draws the scene and nothing that changes it, links to the scene's own page, and
    /// is the one address another site may frame — the sites Graticula:EmbedOrigins names, and none when it names none.
    /// </summary>
    [Fact]
    public async Task The_embed_address_shows_the_scene_alone_and_only_it_may_be_framed()
    {
        (string token, string cookie) = await SignInAsync();
        string? id = null;

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
            id = await SaveAsAsync("zz console scene embed");

            // ADR-275 condition 2: the saved scene offers its embed code exactly when a site may frame it.
            string? named = Environment.GetEnvironmentVariable("GRATICULA_TEST_EMBED_ORIGIN");
            Assert.Equal(string.IsNullOrEmpty(named), await Browser.EvaluateAsync<bool>("document.getElementById('embedPart').hidden"));
            if (!string.IsNullOrEmpty(named))
            {
                Assert.Contains($"/studio/scene-embed.html?id={id}", await Browser.EvaluateAsync<string>("document.getElementById('embedCode').value") ?? string.Empty);
            }

            await OpenAsync($"/studio/scene-embed.html?id={id}", token, cookie);
            await WaitForAsync("typeof scState !== 'undefined' && scState.opened === true && scState.scene.layers.length === 1",
                "The embed address did not open the scene.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.body.classList.contains('embed') && SC_EMBED && !scCanEdit()"),
                "The embed address was not the embed view, or offered to change the scene.");
            Assert.Equal("none", await Browser.EvaluateAsync<string>("getComputedStyle(document.getElementById('panel')).display"));
            Assert.Equal("none", await Browser.EvaluateAsync<string>("getComputedStyle(document.getElementById('studioBar')).display"));
            Assert.EndsWith($"/studio/scene.html?id={id}", await Browser.EvaluateAsync<string>("document.getElementById('embedOpen').href") ?? string.Empty);
            NothingWentWrong(await PageErrorsAsync());

            using HttpResponseMessage embed = await Http.GetAsync($"{Root}/studio/scene-embed.html?id={id}");
            using HttpResponseMessage page = await Http.GetAsync($"{Root}/studio/scene.html");
            string embedPolicy = string.Join(';', embed.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("DENY", string.Join(',', page.Headers.GetValues("X-Frame-Options")));
            Assert.Contains("frame-ancestors 'none'", string.Join(';', page.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

            // The fixture names a site when GRATICULA_TEST_EMBED_ORIGIN says which; CI's names none, and then the embed
            // address is as unframable as every other.
            string? site = Environment.GetEnvironmentVariable("GRATICULA_TEST_EMBED_ORIGIN");
            if (string.IsNullOrEmpty(site))
            {
                Assert.Contains("frame-ancestors 'none'", embedPolicy, StringComparison.Ordinal);
                Assert.True(embed.Headers.Contains("X-Frame-Options"), "With no site named, the embed address was framable.");
            }
            else
            {
                Assert.Contains($"frame-ancestors {site}", embedPolicy, StringComparison.Ordinal);
                Assert.False(embed.Headers.Contains("X-Frame-Options"), "The embed address said DENY beside the sites that may frame it.");
            }
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
    /// ADR-276: Scene Viewer's Edit — the SDK's editor over the scene's layers the reader may edit, and an edit the SDK
    /// sends reaches the layer with the reader's token (the server takes writes on a token only).
    /// </summary>
    [Fact]
    public async Task An_editable_layer_is_edited_in_the_scene()
    {
        (string token, string cookie) = await SignInAsync();
        string editable = (Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? "hosted/ci_editable").Trim('/');

        await OpenNewSceneAsync(token, cookie);
        await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/applyEdits'], true)");
        await AddByAddressAsync($"{Root}/rest/services/{editable}/FeatureServer/0", 1);
        await WaitForAsync("!!document.querySelector('#tools [data-group=edit]') && !document.querySelector('#tools [data-group=edit]').hidden",
            "An editable layer in the scene did not offer Edit.");
        await ClickAsync("#tools [data-group=edit]");
        await WaitForAsync("scState.tool === 'edit' && !!document.querySelector('#toolPanel .esri-editor')", "Edit did not open the editor.");
        Assert.True(await Browser.EvaluateAsync<bool>("scState.tools.edit.layerInfos.some(i => i.enabled && i.layer === scState.scene.layers.getItemAt(0))"),
            "The editor did not offer the scene's editable layer.");

        // What the editor sends when a feature is placed: the layer's applyEdits, through the SDK, with this tab's token.
        await Browser.EvaluateAsync<bool>("(scRequire(['esri/Graphic']).then(([Graphic]) => { const l = scState.scene.layers.getItemAt(0); const c = scState.view.center;"
            + " return l.applyEdits({ addFeatures: [new Graphic({ geometry: { type: 'point', longitude: c.longitude, latitude: c.latitude }, attributes: { label: 'zz scene edit' } })] }); })"
            + ".then(r => window.__edited = r.addFeatureResults[0], e => window.__edited = { error: e.message }), true)");
        await WaitForAsync("!!window.__edited", "The edit was not answered.");
        Assert.True(await Browser.EvaluateAsync<bool>("!window.__edited.error && window.__edited.objectId > 0"),
            "The scene's edit was refused: " + await Browser.EvaluateAsync<string>("JSON.stringify(window.__edited)"));

        await Browser.EvaluateAsync<bool>("(scState.scene.layers.getItemAt(0).applyEdits({ deleteFeatures: [{ objectId: window.__edited.objectId }] })"
            + ".then(r => window.__removed = r.deleteFeatureResults[0]), true)");
        await WaitForAsync("!!window.__removed && !window.__removed.error", "The feature the test added was not removed.");

        await ClickAsync("#tools [data-group=edit]");
        await WaitForAsync("scState.tool === null", "Edit did not close.");
        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-276 condition 2: Create layer makes an empty hosted layer with heights through the portal's createService, adds
    /// it to the scene and opens Edit on it; a point placed with a height keeps it.
    /// </summary>
    [Fact]
    public async Task An_empty_3d_layer_is_created_from_the_scene_and_edited()
    {
        (string token, string cookie) = await SignInAsync();
        string name = $"zz_scene_new_{Guid.NewGuid():N}"[..24];

        try
        {
            await OpenNewSceneAsync(token, cookie);
            await Browser.EvaluateAsync<bool>("(window.__letWritesTo = ['/createService', '/applyEdits'], true)");
            await Browser.EvaluateAsync<bool>($"(document.getElementById('createLayer').open = true, document.getElementById('createName').value = {JsonSerializer.Serialize(name)}, true)");
            await ClickAsync("#createGo");
            await WaitForAsync("scState.scene.layers.length === 1 && scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "Create layer did not add the new layer.");
            Assert.True(await Browser.EvaluateAsync<bool>("scState.scene.layers.getItemAt(0).hasZ === true"), "The new layer has no heights.");
            await WaitForAsync("scState.tool === 'edit' && !!document.querySelector('#toolPanel .esri-editor')", "Edit was not opened on the new layer.");

            await Browser.EvaluateAsync<bool>("(scRequire(['esri/Graphic']).then(([Graphic]) => { const l = scState.scene.layers.getItemAt(0); const c = scState.view.center;"
                + " return l.applyEdits({ addFeatures: [new Graphic({ geometry: { type: 'point', longitude: c.longitude, latitude: c.latitude, z: 42, hasZ: true }, attributes: { label: 'zz high' } })] }); })"
                + ".then(r => window.__made = r.addFeatureResults[0], e => window.__made = { error: e.message }), true)");
            await WaitForAsync("!!window.__made", "The edit was not answered.");
            Assert.True(await Browser.EvaluateAsync<bool>("!window.__made.error && window.__made.objectId > 0"), await Browser.EvaluateAsync<string>("JSON.stringify(window.__made)"));

            (int status, string read) = await AdminAsync(HttpMethod.Get, $"/rest/services/hosted/{name}/FeatureServer/0/query?where=1%3D1&returnZ=true&outFields=label&f=json");
            Assert.Equal(200, status);
            Assert.Contains("\"z\":42", read, StringComparison.Ordinal);
            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    /// <summary>
    /// ADR-270 §5.19, the half the slides test leaves: a slide captured with a slice and a pop-up open keeps both, and
    /// going back to it puts the slice in the scene and opens the pop-up on the same feature.
    /// </summary>
    [Fact]
    public async Task A_slide_keeps_its_slice_and_its_popup()
    {
        (string token, string cookie) = await SignInAsync();

        await OpenNewSceneAsync(token, cookie);
        await AddByAddressAsync($"{Root}/rest/services/hosted/ci_buildings/FeatureServer/0", 1);
        await WaitForAsync("scState.scene.layers.getItemAt(0).loadStatus === 'loaded'", "The buildings did not load.");

        // A slice, placed through the widget's own analysis, and a pop-up on one building.
        await OpenToolAsync("slice");
        await WaitForAsync("scState.tool === 'slice' && !!scState.tools.slice", "Slice did not open.");
        await Browser.EvaluateAsync<bool>("(scRequire(['esri/analysis/SlicePlane']).then(([SlicePlane]) => { const c = scState.view.center;"
            + " scState.tools.slice.analysis.shape = new SlicePlane({ position: { longitude: c.longitude, latitude: c.latitude, z: 10, spatialReference: { wkid: 4326 } }, tilt: 0, width: 500, height: 500 }); }), true)");
        await WaitForAsync("!!scState.tools.slice.analysis.shape", "The slice was not placed.");
        await Browser.EvaluateAsync<bool>("(scState.scene.layers.getItemAt(0).queryFeatures({ where: '1=1', outFields: ['*'], returnGeometry: true, num: 1 })"
            + ".then(r => scState.view.openPopup({ features: r.features, location: r.features[0].geometry.centroid || r.features[0].geometry })).then(() => window.__opened = true), true)");
        await WaitForAsync("window.__opened === true && scState.view.popup.visible", "The pop-up did not open.");
        string feature = await Browser.EvaluateAsync<string>("String(scState.view.popup.features[0].getObjectId())") ?? string.Empty;

        await ClickAsync("#tab-slides");
        await ClickAsync("#slideAdd");
        await WaitForAsync("scState.scene.presentation.slides.length === 1", "Capture slide did not add a slide.");
        Assert.Equal(1, await Browser.EvaluateAsync<int>("scState.scene.presentation.slides.getItemAt(0).elements.analyses.length"));
        Assert.Equal(feature, await Browser.EvaluateAsync<string>("String(scState.scene.presentation.slides.getItemAt(0).elements.popupInfo.features.getItemAt(0).id.value)"));

        // Away from both, then back to the slide: the slice is in the scene and the pop-up is open on the same building.
        await ClickAsync("#tools [data-group=analyze]");
        await WaitForAsync("scState.tool === null", "The slice did not close.");
        await Browser.EvaluateAsync<bool>("(scState.view.closePopup(), true)");
        await WaitForAsync("!scState.view.analyses.some(a => a.type === 'slice') && !scState.view.popup.visible", "The slice or the pop-up stayed.");
        await ClickAsync("#slideList [data-act=go]");
        await WaitForAsync("scState.view.analyses.some(a => a.type === 'slice')", "Going to the slide did not put its slice back.");
        await WaitForAsync($"scState.view.popup.visible && String(scState.view.popup.features[0]?.getObjectId()) === '{feature}'",
            "Going to the slide did not open its pop-up on the same feature.");
        NothingWentWrong(await PageErrorsAsync());
    }
}
