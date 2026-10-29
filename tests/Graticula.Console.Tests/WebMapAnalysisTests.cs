using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-222 condition 7, in the map: the Analysis panel offers the map's hosted layers and runs the same tools as an item's
/// Analyze… dialog, by the same documented parameters.
/// </summary>
/// <remarks>
/// The harness traps the submission, so what is asserted is what was sent; that a run's result joins the map is the
/// panel's <c>succeeded</c>, which adds the service as Layers' Add does. The tools themselves are analysis.js's, which
/// <c>AnalyzeDialogTests</c> drives from the item page.
/// </remarks>
public sealed class WebMapAnalysisTests : ConsoleTest
{
    private static string Service() =>
        (Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? "hosted/ci_buildings").Trim('/');

    [Fact]
    public async Task The_map_analyzes_its_hosted_layer_with_the_shared_tools()
    {
        (string token, string cookie) = await SignInAsync();
        await OpenAsync($"/studio/webmap.html?service={Uri.EscapeDataString(Service())}", token, cookie);

        await WaitForAsync("wmAnalysable().length > 0", "The map's hosted layer never became readable.");
        await ClickAsync("#tab-analysis");
        await WaitForAsync(Shown("#pane-analysis"), "The Analysis tab showed no panel.");
        await WaitForAsync("document.querySelectorAll('#azLayer option').length > 0", "The panel offers none of the map's layers.");
        await WaitForAsync("!!document.querySelector('#pane-analysis #azTool option')", "The panel offers no tools.");

        // The same tools as the item's dialog, for the same kind of layer.
        Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#azTool option[value=DissolveBoundaries]')"),
            "A polygon layer in the map is not offered Dissolve boundaries.");

        await Browser.EvaluateAsync<bool>("(() => { const s = document.getElementById(\'azTool\'); s.value = \'CreateBuffers\'; s.dispatchEvent(new Event(\'change\', { bubbles: true })); return true; })()");
        await WaitForAsync(Shown("#azDistances"), "Create buffers asks no distances in the map.");
        await Browser.EvaluateAsync<bool>("(document.getElementById('azName').value = 'zz_map_buffers', true)");
        await ClickAsync("#analyzeGo");
        await WaitForAsync(
            "window.__writes.some(w => w.startsWith('POST /rest/services/System/SpatialAnalysisTools/GPServer/CreateBuffers/submitJob "
            + "[dissolveType,distances,f,inputLayer,outputName,ringType,units]'))",
            "Run in the map did not submit Create buffers with its documented parameters.");
        await WaitForAsync("(document.getElementById('analyzeSays')?.textContent || '').includes('did not start')",
            "The trapped run was not said in the panel.");

        NothingWentWrong(await PageErrorsAsync());
    }
}
