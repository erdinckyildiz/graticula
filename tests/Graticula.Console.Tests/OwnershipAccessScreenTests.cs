using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// ADR-115 in Settings › Feature layer: recording who edits stops as well as starts, and editors can be kept to the
/// features they added once it records who added each one.
/// </summary>
public sealed class OwnershipAccessScreenTests : ConsoleTest
{
    [Fact]
    public async Task A_tracked_layer_offers_stop_and_keeping_editors_to_their_own()
    {
        string layer = $"zz_obac_{Guid.NewGuid():N}"[..20];

        (int defined, string design) = await AdminAsync(HttpMethod.Post, "/admin/hosted/define",
            JsonSerializer.Serialize(new
            {
                name = layer,
                geometryType = "Point",
                fields = new object[] { new { name = "label", type = "text", nullable = true } },
            }));
        Assert.True(defined == 201, $"Defining {layer} answered {defined}: {design}");

        try
        {
            (int tracked, string trackedBody) = await AdminAsync(HttpMethod.Post, $"/admin/hosted/{layer}/editor-tracking", "{}");
            Assert.True(tracked == 200, $"Turning tracking on answered {tracked}: {trackedBody}");

            (string token, _) = await SignInAsync();
            await OpenAsync($"/studio/#/service/hosted/{layer}?tab=settings&section=feature", token);

            string own = $"#featureLayers [data-own-only=\"{layer}\"]";
            string stop = $"#featureLayers [data-track-edits=\"{layer}\"]";

            await WaitForAsync($"!!document.querySelector('{own}') && !document.querySelector('{own}').disabled",
                "A tracked layer does not offer keeping editors to their own features.");
            Assert.Equal("Stop recording", await Browser.EvaluateAsync<string>($"document.querySelector('{stop}').textContent.trim()"));

            await Browser.EvaluateAsync<bool>("(window.__writes = [], true)");
            await ClickAsync(own);
            await WaitForAsync(
                $"window.__writes.some(w => w.startsWith('PUT') && w.includes('/admin/layers/{layer}/ownership-access'))",
                "Ticking the box sent nothing.");

            await ClickAsync(stop);
            await WaitForAsync(
                $"window.__writes.some(w => w.startsWith('DELETE') && w.includes('/admin/hosted/{layer}/editor-tracking'))",
                "Stop recording sent no request.");

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{layer}?folder=hosted&drop=true");
        }
    }
}
