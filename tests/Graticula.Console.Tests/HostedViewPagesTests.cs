using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-113 condition 4: a source's page offers Create view and lists its views; a view's page names its source, shows
/// its filter and offers no Update data.
/// </summary>
/// <remarks>
/// <b>The source and its view are made through the API</b>, because the page's own writes are trapped by the harness;
/// what this checks is what the pages draw from real ones. Both are deleted, the view first.
/// </remarks>
public sealed class HostedViewPagesTests : ConsoleTest
{
    [Fact]
    public async Task A_source_offers_and_lists_its_views_and_a_view_names_its_source()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string source = $"zz_vsrc_{suffix}";
        string view = $"zz_vview_{suffix}";

        (int defined, string design) = await AdminAsync(HttpMethod.Post, "/admin/hosted/define",
            JsonSerializer.Serialize(new
            {
                name = source,
                geometryType = "Point",
                fields = new object[] { new { name = "status", type = "text", nullable = true } },
            }));
        Assert.True(defined == 201, $"Defining {source} answered {defined}: {design}");

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, $"/admin/services/{source}/views?folder=hosted",
                JsonSerializer.Serialize(new { name = view, definitions = new Dictionary<string, string> { ["0"] = "status = 'open'" } }));
            Assert.True(made == 201, $"Making the view answered {made}: {madeBody}");

            (string token, _) = await SignInAsync();

            // ------------------------------------------------ the source
            await OpenAsync($"/studio/#/service/hosted/{source}", token);

            await WaitForAsync(Shown("#createViewOpen"), "A hosted service's page offers no Create view.");
            await WaitForAsync(
                $"!!document.querySelector('#svcViews a[href$=\"{view}\"]')",
                "The source's details do not list its view.");

            await ClickAsync("#createViewOpen");
            await WaitForAsync(Shown("#cvName"), "Create view did not open its dialog.");
            Assert.True(await Browser.EvaluateAsync<bool>(
                "document.querySelectorAll('#createViewBody input[name=cvAllows]').length === 3"),
                "The dialog does not offer read only, read and edit, and add only.");

            // The field names are offered, and one goes into the filter where the cursor is.
            await WaitForAsync("!!document.querySelector('#createViewBody [data-insert-field=\"status\"]')",
                "The dialog does not say what the fields are called.");
            await ClickAsync("#createViewBody [data-insert-field=\"status\"]");
            Assert.Equal("status", await Browser.EvaluateAsync<string>(
                "document.querySelector('#createViewBody [data-cv-filter]').value"));

            await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
            await ClickAsync("#createViewGo");
            await WaitForAsync(
                $"(window.__writes || []).some(w => w.startsWith('POST') && w.includes('/admin/services/{source}/views'))",
                "Create view did not post to the source's views.");

            // ------------------------------------------------ the view
            await OpenAsync($"/studio/#/service/hosted/{view}", token);

            await WaitForAsync(
                $"!!document.querySelector('#svcViewOf a[href$=\"{source}\"]')",
                "The view's details do not name its source.");
            Assert.Contains("status = 'open'",
                await Browser.EvaluateAsync<string>("document.getElementById('svcViewOf').textContent") ?? "",
                StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('updateDataOpen')"),
                "A view offers Update data, which the server refuses.");
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('createViewOpen')"),
                "A view offers Create view, which the server refuses.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{view}?folder=hosted&drop=true");
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{source}?folder=hosted&drop=true");
        }
    }
}
