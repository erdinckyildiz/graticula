using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-223 condition 5: a toolbox's page asks each tool's questions, refuses a run with a question left empty on its
/// field, and sends a run by the parameters' own names — reached the way a reader reaches it, through the address the
/// router rewrites (design review 2026-10-29: every button on the page did nothing there).
/// </summary>
/// <remarks>
/// The suite publishes a two-tool toolbox for this test through the server's own route and removes it afterwards; the
/// harness traps the run, so what is asserted is what was sent.
/// </remarks>
public sealed class ToolboxPageTests : ConsoleTest
{
    private const string Toolbox = """
        import grapy as arcpy

        class Toolbox:
            def __init__(self):
                self.tools = [Count]

        class Count:
            def __init__(self):
                self.label = "Count features"

            def getParameterInfo(self):
                return [arcpy.Parameter(name="in_features", displayName="Features", datatype="GPFeatureLayer"),
                        arcpy.Parameter(name="count", datatype="GPLong", direction="Output", parameterType="Derived")]

            def execute(self, parameters, messages):
                parameters[1].value = int(arcpy.management.GetCount(parameters[0].valueAsText).getOutput(0))
        """;

    [Fact]
    public async Task A_tool_is_refused_with_an_empty_question_and_sent_by_its_parameters_names()
    {
        (string token, _) = await SignInAsync();
        string name = "zz_console_tb_" + Guid.NewGuid().ToString("N")[..6];

        await OpenAsync("/studio/#/content", token);
        await Browser.EvaluateAsync<bool>($$"""
            (async () => {
              const form = new FormData();
              form.set("name", "{{name}}");
              form.set("file", new File([{{System.Text.Json.JsonSerializer.Serialize(Toolbox)}}], "probe.pyt"));
              window.__letWritesTo = ["/content/toolboxes"];
              const r = await fetch("/content/toolboxes", { method: "POST", body: form, headers: { Authorization: "Bearer " + sessionStorage.getItem("gis-token") } });
              window.__letWritesTo = [];
              window.__published = r.status;
              return true;
            })()
            """);
        await WaitForAsync("window.__published === 201", "The test's toolbox could not be published.");

        try
        {
            await Browser.EvaluateAsync<bool>($"(location.hash = '#/toolbox/{name}', true)");
            await WaitForAsync("!!document.querySelector('[data-tb-run=Count]')", "The toolbox page shows no Run for its tool.");
            await WaitForAsync("document.querySelectorAll('[data-tb-param=in_features] option').length > 1", "The layer question offers no layer.");

            // Nothing chosen to start, and Run with nothing chosen is refused on the field, in red.
            Assert.Equal(string.Empty, await Browser.EvaluateAsync<string>("document.querySelector('[data-tb-param=in_features]').value"));
            await ClickAsync("[data-tb-run=Count]");
            await WaitForAsync("(document.getElementById('tbSays_Count').textContent || '').includes('Features is required')",
                "Run with no layer chosen was not refused by the field's label.");
            Assert.True(await Browser.EvaluateAsync<bool>("document.getElementById('tbSays_Count').classList.contains('bad-inline')"),
                "The refusal is not said as a failure.");
            Assert.Equal("in_features", await Browser.EvaluateAsync<string>("document.activeElement?.dataset.tbParam || ''"));

            await Browser.EvaluateAsync<bool>(
                "(() => { const s = document.querySelector('[data-tb-param=in_features]'); s.selectedIndex = 1; return true; })()");
            await ClickAsync("[data-tb-run=Count]");
            await WaitForAsync(
                $"window.__writes.some(w => w.startsWith('POST /rest/services/Tools/{name}/GPServer/Count/submitJob [f,in_features]'))",
                "Run did not send the tool by its parameters' names.");
            await WaitForAsync("(document.getElementById('tbSays_Count').textContent || '').includes('did not start')",
                "The trapped run was not said.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(System.Net.Http.HttpMethod.Delete, $"/content/toolboxes/{name}");
        }
    }
}
