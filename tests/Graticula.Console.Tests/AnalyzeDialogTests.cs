using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-222 condition 7: Studio runs an analysis from a hosted layer's page — the tools that fit the layer, their own
/// questions, a name for the result — and sends it to the Spatial Analysis Service by the parameters it documents.
/// </summary>
/// <remarks>
/// The harness traps the submission, so what is asserted is what was sent; the service itself is the conformance suite's
/// (SpatialAnalysisServiceTests). The layer is the suite's polygon layer, only read.
/// </remarks>
public sealed class AnalyzeDialogTests : ConsoleTest
{
    private static string Service() =>
        (Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings").Trim('/');

    [Fact]
    public async Task A_polygon_layer_offers_its_tools_and_sends_a_buffer_by_the_documented_names()
    {
        (string token, _) = await SignInAsync();
        await OpenAsync($"/studio/#/service/{Service()}", token);

        await WaitForAsync("!!document.getElementById('analyzeOpen')", "A hosted layer's page offers no Analyze.");
        await Browser.EvaluateAsync<bool>("(() => { document.querySelector('#itemActions .itemmore').open = true; return true; })()");
        await ClickAsync("#analyzeOpen");
        await WaitForAsync(Shown("#azName"), "Analyze did not open its dialog.");

        // A polygon layer: dissolve is offered, counting points is not.
        Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#azTool option[value=DissolveBoundaries]')"),
            "A polygon layer is not offered Dissolve boundaries.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#azTool option[value=AggregatePoints]')"),
            "A polygon layer is offered Aggregate points, which counts points.");

        // Each tool asks its own questions, and the result's name follows the tool until somebody types one.
        await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById(\'azTool\'); s.value = \'CreateBuffers\'; s.dispatchEvent(new Event(\'change\', { bubbles: true })); return true; })()");
        await WaitForAsync(Shown("#azDistances"), "Create buffers asks no distances.");
        Assert.True(await Browser.EvaluateAsync<bool>(Shown("#azDistances")), "Create buffers asks no distances.");
        string bufferName = await Browser.EvaluateAsync<string>("document.getElementById('azName').value") ?? string.Empty;
        Assert.EndsWith("_buffers", bufferName, StringComparison.Ordinal);
        await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById(\'azTool\'); s.value = \'OverlayLayers\'; s.dispatchEvent(new Event(\'change\', { bubbles: true })); return true; })()");
        await WaitForAsync("document.querySelectorAll('input[name=azOverlay]').length === 3", "Overlay offers no choice of what to keep.");
        Assert.EndsWith("_overlay", await Browser.EvaluateAsync<string>("document.getElementById('azName').value"), StringComparison.Ordinal);

        // A name the server would refuse is said here, on the field.
        await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById(\'azTool\'); s.value = \'CreateBuffers\'; s.dispatchEvent(new Event(\'change\', { bubbles: true })); return true; })()");
        await WaitForAsync(Shown("#azDistances"), "Create buffers did not come back.");
        await Browser.EvaluateAsync<bool>("(document.getElementById('azName').value = 'not a name!', true)");
        await ClickAsync("#analyzeGo");
        await WaitForAsync("document.getElementById('azName').getAttribute('aria-invalid') === 'true'", "A bad name was not refused on its field.");
        Assert.True(await Browser.EvaluateAsync<bool>(
            "!!document.getElementById('azNameError') && document.getElementById('azName').getAttribute('aria-describedby').includes('azNameError')"),
            "The refusal is not said under the name field, or the field is not described by it.");
        Assert.DoesNotContain(await WritesAsync(), w => w.Contains("submitJob", StringComparison.Ordinal));

        // A good one is sent with the documented parameter names.
        await Browser.EvaluateAsync<bool>("(document.getElementById('azName').value = 'zz_console_buffers', true)");
        await Browser.EvaluateAsync<bool>("(document.getElementById('azDistances').value = '50, 150', true)");
        await ClickAsync("#analyzeGo");
        await WaitForAsync(
            "window.__writes.some(w => w.startsWith('POST /rest/services/System/SpatialAnalysisTools/GPServer/CreateBuffers/submitJob "
            + "[dissolveType,distances,f,inputLayer,outputName,ringType,units]'))",
            "Run did not submit Create buffers with its documented parameters.");

        // The trapped answer starts no job, and the dialog says so rather than waiting for ever.
        await WaitForAsync("(document.getElementById('analyzeSays')?.textContent || '').includes('did not start')",
            "An answer with no job id was not said.");

        // Closed, the reader is back on something they can see: Analyze… is in the More menu, closed by then.
        await ClickAsync("#analyzeCancel");
        await WaitForAsync("document.activeElement?.matches('#itemActions .itemmore summary')",
            "Closing the dialog left the focus on the page rather than on the menu it was opened from.");

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// A design review: the polygon list for Aggregate points opened on a point layer, so every run of it failed. Each
    /// second-layer list holds only what its tool can use.
    /// </summary>
    [Fact]
    public async Task A_point_layer_counts_into_polygons_and_is_offered_only_polygons_to_count_in()
    {
        string points = (Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/');
        (string token, _) = await SignInAsync();
        await OpenAsync($"/studio/#/service/{points}", token);

        await WaitForAsync("!!document.getElementById('analyzeOpen')", "A point layer's page offers no Analyze.");
        await Browser.EvaluateAsync<bool>("(() => { document.querySelector('#itemActions .itemmore').open = true; return true; })()");
        await ClickAsync("#analyzeOpen");
        await WaitForAsync("!!document.querySelector('#azTool option[value=AggregatePoints]:checked')",
            "A point layer does not open on Aggregate points, Map Viewer's first tool.");
        Assert.False(await Browser.EvaluateAsync<bool>("!!document.querySelector('#azTool option[value=DissolveBoundaries]')"),
            "A point layer is offered Dissolve boundaries, which takes polygons.");
        Assert.Equal("azTool", await Browser.EvaluateAsync<string>("document.activeElement?.name || ''"));

        await WaitForAsync("document.querySelectorAll('#azOther option').length > 0", "There is no polygon layer to count in.");
        string[] offered = await Browser.EvaluateAsync<string[]>("[...document.querySelectorAll('#azOther option')].map(o => o.value)")
            ?? Array.Empty<string>();

        foreach (string url in offered)
        {
            string kind = await Browser.EvaluateAsync<string>(
                $"fetch('{url}?f=json', {{ headers: {{ Authorization: 'Bearer ' + token }} }}).then(r => r.json()).then(d => d.geometryType)") ?? string.Empty;
            Assert.Equal("esriGeometryPolygon", kind);
        }

        NothingWentWrong(await PageErrorsAsync());
    }

    /// <summary>
    /// ADR-222 condition 3: points counted in hexagon bins send the bin parameters and no polygon layer; a join within a
    /// distance asks the distance and sends it with its unit.
    /// </summary>
    [Fact]
    public async Task Points_count_into_bins_and_a_join_within_a_distance_sends_its_distance()
    {
        string points = (Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/');
        (string token, _) = await SignInAsync();
        await OpenAsync($"/studio/#/service/{points}", token);

        await WaitForAsync("!!document.getElementById('analyzeOpen')", "A point layer's page offers no Analyze.");
        await Browser.EvaluateAsync<bool>("(() => { document.querySelector('#itemActions .itemmore').open = true; return true; })()");
        await ClickAsync("#analyzeOpen");
        await WaitForAsync("!!document.querySelector('#azTool option[value=AggregatePoints]:checked')", "Analyze did not open.");

        // Hexagon bins: the polygon list goes, the bin size comes, and a polygon with no point is not a question any more.
        await ClickAsync("input[name=azWhere][value=Hexagon]");
        await WaitForAsync(Shown("#azBinSize"), "Choosing hexagon bins showed no bin size.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#azOther")), "Bins left the polygon list on the screen.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#azKeep")), "Bins still ask whether to keep empty polygons.");

        await Browser.EvaluateAsync<bool>("(document.getElementById('azName').value = 'zz_console_hex', true)");
        await ClickAsync("#analyzeGo");
        await WaitForAsync(
            "window.__writes.some(w => w.startsWith('POST /rest/services/System/SpatialAnalysisTools/GPServer/AggregatePoints/submitJob "
            + "[binSize,binSizeUnit,binType,f,keepBoundariesWithNoPoints,outputName,pointLayer]'))",
            "Aggregate points in bins was not sent with the bin parameters alone.");
        await WaitForAsync("(document.getElementById('analyzeSays')?.textContent || '').includes('did not start')", "The trapped run was not said.");

        // Join features within a distance.
        await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById(\'azTool\'); s.value = \'JoinFeatures\'; s.dispatchEvent(new Event(\'change\', { bubbles: true })); return true; })()");
        await WaitForAsync("!!document.getElementById('azRelation')", "Join features asks no relationship.");
        Assert.False(await Browser.EvaluateAsync<bool>(Shown("#azJoinDistance")), "A distance is asked before Within a distance is chosen.");
        await Browser.EvaluateAsync<bool>(
            "(() => { const s = document.getElementById('azRelation'); s.value = 'WithinDistance'; s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()");
        await WaitForAsync(Shown("#azJoinDistance"), "Within a distance asks no distance.");
        await WaitForAsync("document.querySelectorAll('#azOther option').length > 0", "There is no layer to join with.");

        await Browser.EvaluateAsync<bool>("(document.getElementById('azName').value = 'zz_console_join', true)");
        await ClickAsync("#analyzeGo");
        await WaitForAsync(
            "window.__writes.some(w => w.startsWith('POST /rest/services/System/SpatialAnalysisTools/GPServer/JoinFeatures/submitJob "
            + "[f,joinLayer,joinOperation,joinType,outputName,spatialRelationship,spatialRelationshipDistance,spatialRelationshipDistanceUnits,targetLayer]'))",
            "Join features within a distance was not sent with its distance and unit.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
