using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A web map's Settings turn ArcGIS Field Maps tasks on for a layer of the map — ADR-286.
/// </summary>
public sealed class MapTasksScreenTests : ConsoleTest
{
    private async Task<(string Map, string Layer, string Folder, string Service)> MapWithALayerAsync(bool withLayer)
    {
        string name = "zz_tasks_ui_" + Guid.NewGuid().ToString("N")[..6];
        string layerUrl = string.Empty, folder = string.Empty, service = string.Empty;

        if (withLayer)
        {
            (int defined, string design) = await AdminAsync(HttpMethod.Post, "/admin/hosted/define", JsonSerializer.Serialize(new
            {
                name, geometryType = "Point", fields = new[] { new { name = "site", type = "text", nullable = true } },
            }));
            Assert.True(defined == 201, $"defining a layer: {defined} {design}");
            string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
            string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
            (folder, service) = (parts[2], parts[3]);
            layerUrl = Root + feature;
        }

        (int made, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", JsonSerializer.Serialize(new
        {
            title = "zz tasks screen " + name,
            sharing = "private",
            document = new
            {
                operationalLayers = withLayer
                    ? new object[] { new { id = "tasks1", title = "Hydrant checks", layerType = "ArcGISFeatureLayer", url = layerUrl } }
                    : [],
                baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                version = "2.31",
            },
        }));
        Assert.True(made == 201, $"making the map: {made} {body}");
        return (JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, name, folder, service);
    }

    private async Task CleanUpAsync(string map, string folder, string service)
    {
        (_, string gone) = await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}");

        if (JsonDocument.Parse(gone).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
        }

        if (service.Length > 0)
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{service}?folder={folder}&drop=true");
        }
    }

    /// <summary>
    /// The section is visible on Settings, lists the map's layer and the members, and Turn on sends the types and the
    /// person ticked; the server then reports the tasks on, and the page says so where a screen reader hears it.
    /// </summary>
    [Fact]
    public async Task Tasks_are_turned_on_from_the_maps_settings()
    {
        (string token, _) = await SignInAsync();
        (string map, _, string folder, string service) = await MapWithALayerAsync(withLayer: true);

        try
        {
            await OpenAsync($"/studio/#/item/{map}?tab=settings", token);
            await WaitForAsync("!!document.getElementById('mapTasksOn')", "The map's Settings never offered to turn tasks on.");

            bool visible = await Browser.EvaluateAsync<bool>(
                "(() => { const b = document.getElementById('mapTasksOn'); const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !b.closest('[hidden]'); })()");
            Assert.True(visible, "Turn on tasks exists but is not visible to the person who needs it.");

            string layers = await Browser.EvaluateAsync<string>("[...document.querySelectorAll('#mapTasksLayer option')].map(o => o.textContent).join('|')") ?? "";
            Assert.Contains("Hydrant checks", layers, StringComparison.Ordinal);

            int people = await Browser.EvaluateAsync<int>("document.querySelectorAll('[data-map-task-person]').length");
            Assert.True(people > 0, "No member was offered to assign tasks to.");

            await Browser.EvaluateAsync<bool>("""
                (() => {
                  window.__letWritesTo = ['/tasks'];
                  document.getElementById('mapTasksTypes').value = 'Inspection\nRepair';
                  const first = document.querySelector('[data-map-task-person]');
                  first.checked = true;
                  first.dispatchEvent(new Event('change', { bubbles: true }));
                  document.getElementById('mapTasksOn').click();
                  return true;
                })()
                """);

            await WaitForAsync("!!document.getElementById('mapTasksEdit')", "Turning tasks on never finished.");

            string says = await Browser.EvaluateAsync<string>("document.getElementById('mapTasksSays').textContent") ?? "";
            Assert.Contains("Tasks are on", says, StringComparison.Ordinal);
            Assert.Equal("polite", await Browser.EvaluateAsync<string>("document.getElementById('mapTasksSays').getAttribute('aria-live')"));
            Assert.Equal("mapTasksEdit", await Browser.EvaluateAsync<string>("document.activeElement && document.activeElement.id"));

            (int read, string tasks) = await AdminAsync(HttpMethod.Get, $"/content/webmaps/{map}/tasks");
            Assert.True(read == 200, tasks);
            JsonElement state = JsonDocument.Parse(tasks).RootElement;
            Assert.True(state.GetProperty("enabled").GetBoolean(), tasks);
            Assert.Equal(["Inspection", "Repair"], state.GetProperty("types").EnumerateArray().Select(t => t.GetString()));
            Assert.Single(state.GetProperty("assignees").EnumerateArray());
        }
        finally
        {
            await CleanUpAsync(map, folder, service);
        }
    }

    /// <summary>A map with no feature layer says why tasks cannot be turned on, and offers no button that would fail.</summary>
    [Fact]
    public async Task A_map_without_a_feature_layer_says_why_and_offers_nothing()
    {
        (string token, _) = await SignInAsync();
        (string map, _, _, _) = await MapWithALayerAsync(withLayer: false);

        try
        {
            await OpenAsync($"/studio/#/item/{map}?tab=settings", token);
            await WaitForAsync(
                "(document.getElementById('mapTasks')?.textContent || '').includes('No layer of this map can hold tasks')",
                "A map with no feature layer never said why tasks cannot be turned on.");

            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('mapTasksOn')"),
                "Turn on tasks is offered on a map with no layer to put them on.");
        }
        finally
        {
            await CleanUpAsync(map, string.Empty, string.Empty);
        }
    }
}
