using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The web map viewer — ADR-079 — opened on a saved map and asked whether it ran and drew its layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same three questions <see cref="ViewerPageTests"/> asks of the other two viewers</b>, for
/// the same reason: a page with its own script is outside <see cref="EveryScreenTests"/>, and a parse
/// error in it leaves a static header over an empty panel with nothing to say why. So: the page threw
/// nothing, and the layer list its script fills is filled.
/// </para>
/// <para>
/// <b>The map is saved through the API, not through the page.</b> This harness answers every write the
/// page makes with an empty document and never sends it, so a map saved by clicking would not exist to
/// open. Saving is asserted by <c>WebMapEndpointTests</c>; this is about opening.
/// </para>
/// <para>
/// <b>One of its layers is one nobody can read</b>, because ADR-079 §3 says such a layer is shown as
/// <em>not available to you</em> rather than hidden, and a viewer that dropped it would pass every
/// other assertion here.
/// </para>
/// </remarks>
public sealed class WebMapViewerTests : ConsoleTest
{
    /// <summary>A client of its own, because the harness keeps its own private.</summary>
    private static readonly HttpClient Reader = new(new HttpClientHandler
    {
        // The suite's server presents a self-signed certificate, as the harness's own client allows.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private const string LayerNames =
        "[...document.querySelectorAll('#layerList > li[data-layer] .lhead label')].map(l => l.textContent.trim())";

    [Fact]
    public async Task A_saved_map_opens_draws_its_layers_and_names_the_one_it_cannot_read()
    {
        (string token, string cookie) = await SignInAsync();

        string layerUrl = await AnyFeatureLayerUrlAsync(token);

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-079 console test",
            sharing = "private",
            document = new
            {
                operationalLayers = new object[]
                {
                    new { id = "readable", layerType = "ArcGISFeatureLayer", url = layerUrl, title = "Readable layer", visibility = true, opacity = 1 },
                    new
                    {
                        id = "unreadable",
                        layerType = "ArcGISFeatureLayer",
                        url = $"{Root}/rest/services/zz_adr079_nobody/FeatureServer/0",
                        title = "Nobody's layer",
                        visibility = true,
                        opacity = 1,
                    },
                },
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        });

        (int status, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", document);

        Assert.True(status == 201, $"Saving the map through the API answered {status}: {body}");

        string id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        try
        {
            await OpenAsync($"/studio/webmap.html?id={id}", token, cookie);

            await WaitForAsync(
                $"{LayerNames}.length === 2",
                "The layer list never showed the map's two layers. The script either did not run or did "
                + "not reach the document.");

            await WaitForAsync(
                "!document.getElementById('layerList').innerText.includes('Loading')",
                "A layer is still loading after the wait; its document was never read.");

            string[] names = await Browser.EvaluateAsync<string[]>(LayerNames) ?? [];

            // Top of the list is top of the map, which is the document's last layer.
            Assert.Equal(["Nobody's layer", "Readable layer"], names);

            string said = await Browser.EvaluateAsync<string>(
                "document.getElementById('layerList').innerText") ?? string.Empty;

            Assert.Contains("Not available to you", said, StringComparison.Ordinal);

            Assert.Equal(
                "ADR-079 console test",
                (await Browser.EvaluateAsync<string>("document.getElementById('title').textContent") ?? "").Trim());

            await WaitForAsync(
                Shown("#layerList"),
                "The layer list is in the markup and not on screen.");

            // <b>The recurring defect: a redraw drops focus to the body.</b> Moving the readable layer up
            // redraws the list; the keyboard must still be on that layer's controls.
            await Browser.EvaluateAsync<string>(
                "(() => { const b = document.querySelector('#layerList button[data-act=up][data-layer=readable]'); b.focus(); b.click(); return 'ok'; })()");

            await WaitForAsync(
                "(document.activeElement && document.activeElement.dataset.layer) === 'readable'",
                "After moving a layer the keyboard focus was not on that layer any more — the redraw dropped it.");

            // <b>Save is on screen without opening the Map tab</b> — design review 2026-09-19.
            await WaitForAsync(
                Shown("#headSave"),
                "There is no Save in the header. A reader who changed the map had nothing on screen saying "
                + "how to keep it unless they opened the Map tab.");

            // <b>Add keeps the keyboard</b> — the pressed button was disabled, and focus fell to <body>.
            await Browser.EvaluateAsync<string>("(() => { document.getElementById('addLayer').click(); return 'ok'; })()");

            // <b>The first one on screen</b>, not the first in the document: a service whose features are
            // already on the map offers its other kinds only inside a closed disclosure, and a button
            // there cannot take focus — which CI's fixture reached first and this machine's did not.
            const string VisibleAdd =
                "[...document.querySelectorAll('#addList button[data-add]')].find(b => b.offsetParent)";

            await WaitForAsync(
                $"!!{VisibleAdd}",
                "The Add layer list never showed a service to add.");

            await Browser.EvaluateAsync<string>(
                $"(() => {{ const b = {VisibleAdd}; b.focus(); b.click(); return 'ok'; }})()");

            await WaitForAsync(
                "document.activeElement && document.activeElement !== document.body"
                + " && document.getElementById('addList').contains(document.activeElement)"
                + " && !document.querySelector('#addList [aria-disabled=true]')",
                "After Add the keyboard focus left the list it was in — dropped to the body by a disabled button.");

            string[] failures = await PageErrorsAsync();

            Assert.True(failures.Length == 0, "webmap.html threw:\n  " + string.Join("\n  ", failures));

            // Moving a layer is an edit to the map, not a write to the server.
            Assert.Empty(await WritesAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }

    /// <summary>The address of some feature layer this server publishes.</summary>
    private async Task<string> AnyFeatureLayerUrlAsync(string token)
    {
        (string Folder, string[] Services)[] folders = await FoldersWithServicesAsync();
        HashSet<string> images = await ImageServicesAsync();

        foreach (string service in folders.SelectMany(f => f.Services).Where(s => !images.Contains(s)))
        {
            string at = $"{Root}/rest/services/{service}/FeatureServer";

            using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{at}?f=json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Reader.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using JsonDocument described = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (described.RootElement.TryGetProperty("layers", out JsonElement layers)
                && layers.ValueKind == JsonValueKind.Array
                && layers.EnumerateArray().FirstOrDefault(l =>
                    !(l.TryGetProperty("type", out JsonElement t) && t.GetString() == "Group Layer")) is
                    { ValueKind: JsonValueKind.Object } layer)
            {
                return $"{at}/{layer.GetProperty("id").GetInt32()}";
            }
        }

        Assert.Fail("No feature layer is published, so there is nothing to put on the map.");
        return string.Empty;
    }
}
