using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// A web map's Settings enable ArcGIS Field Maps tasks for a layer of the map — ADR-286.
/// </summary>
public sealed class MapTasksScreenTests : ConsoleTest
{
    private static readonly string[] Inspection = ["Inspection"];

    private async Task<(string Map, string Folder, string Service, string LayerUrl)> MapWithALayerAsync(bool withLayer)
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

        (int made, string body) = await AdminAsync(HttpMethod.Post, "/content/webmaps", MapBody(name, withLayer ? layerUrl : null));
        Assert.True(made == 201, $"making the map: {made} {body}");
        return (JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!, folder, service, layerUrl);
    }

    private static string MapBody(string name, string? layerUrl) => JsonSerializer.Serialize(new
    {
        title = "zz tasks screen " + name,
        sharing = "private",
        document = new
        {
            operationalLayers = layerUrl is null
                ? Array.Empty<object>()
                : [new { id = "tasks1", title = "Hydrant checks", layerType = "ArcGISFeatureLayer", url = layerUrl }],
            baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
            version = "2.31",
        },
    });

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

    private async Task OpenSettingsAsync(string map, string token, string ready)
    {
        await OpenAsync($"/studio/#/item/{map}?tab=settings", token);
        await WaitForAsync(ready, "The map's task section never drew.");
    }

    /// <summary>Real keystrokes, one key at a time, as a person types them.</summary>
    private async Task KeysAsync(string text)
    {
        foreach (char c in text)
        {
            string key = c.ToString();
            await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key, text = key });
            await Browser.CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key });
        }
    }

    /// <summary>
    /// The section is visible on Settings, lists the map's layer and the members, and Enable tasks sends the types and
    /// the assignee ticked; the server then reports the tasks enabled, and the page says so where a screen reader hears it.
    /// </summary>
    [Fact]
    public async Task Tasks_are_enabled_from_the_maps_settings()
    {
        (string token, _) = await SignInAsync();
        (string map, string folder, string service, _) = await MapWithALayerAsync(withLayer: true);

        try
        {
            await OpenSettingsAsync(map, token, "!!document.querySelector('#mapTasksPeople [data-map-task-person]')");

            bool visible = await Browser.EvaluateAsync<bool>(
                "(() => { const b = document.getElementById('mapTasksOn'); const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !b.closest('[hidden]'); })()");
            Assert.True(visible, "Enable tasks exists but is not visible to the person who needs it.");
            Assert.Contains("Hydrant checks", await Browser.EvaluateAsync<string>("document.getElementById('mapTasksBody').textContent") ?? "", StringComparison.Ordinal);

            // The status line is outside what is redrawn: the node a screen reader was told about is the one that speaks.
            await Browser.EvaluateAsync<bool>("(() => { window.__says = document.getElementById('mapTasksSays'); return true; })()");

            await Browser.EvaluateAsync<bool>("""
                (() => {
                  window.__letWritesTo = ['/tasks'];
                  const types = document.getElementById('mapTasksTypes');
                  types.value = 'Inspection\nRepair';
                  types.dispatchEvent(new Event('input', { bubbles: true }));
                  const first = document.querySelector('[data-map-task-person]');
                  first.checked = true;
                  first.dispatchEvent(new Event('change', { bubbles: true }));
                  document.getElementById('mapTasksOn').click();
                  return true;
                })()
                """);

            await WaitForAsync("!!document.getElementById('mapTasksEdit')", "Enabling tasks never finished.");

            Assert.True(await Browser.EvaluateAsync<bool>("window.__says === document.getElementById('mapTasksSays')"),
                "The status line was replaced by the redraw, so what it says is not announced.");
            Assert.Contains("Tasks are enabled", await Browser.EvaluateAsync<string>("document.getElementById('mapTasksSays').textContent") ?? "", StringComparison.Ordinal);
            Assert.Equal("mapTasksEdit", await Browser.EvaluateAsync<string>("document.activeElement && document.activeElement.id"));

            // The map is private and the layer is in no shared update group: the section says what that means.
            string body = await Browser.EvaluateAsync<string>("document.getElementById('mapTasksBody').textContent") ?? "";
            Assert.Contains("shared update group", body, StringComparison.Ordinal);

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

    /// <summary>
    /// Typing in the member search keeps the letters in order and the caret at the end — the first version redrew the
    /// list on every keystroke and *esra* came out *arse* (design review C1).
    /// </summary>
    [Fact]
    public async Task Typing_a_name_in_the_member_search_keeps_what_was_typed()
    {
        (string token, _) = await SignInAsync();
        (string map, string folder, string service, _) = await MapWithALayerAsync(withLayer: true);

        try
        {
            await OpenSettingsAsync(map, token, "!!document.querySelector('#mapTasksPeople [data-map-task-person]')");
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('mapTasksFind').focus(); return true; })()");

            await KeysAsync("zzqx");

            Assert.Equal("zzqx", await Browser.EvaluateAsync<string>("document.getElementById('mapTasksFind').value"));
            Assert.Equal("mapTasksFind", await Browser.EvaluateAsync<string>("document.activeElement && document.activeElement.id"));
            Assert.True(await Browser.EvaluateAsync<bool>("!document.getElementById('mapTasksNoMatch').hidden"),
                "A search matching nobody does not say so.");
        }
        finally
        {
            await CleanUpAsync(map, folder, service);
        }
    }

    /// <summary>
    /// Enabling with no task type marks the field, says why next to it and moves focus there; nothing is sent
    /// (design review M3).
    /// </summary>
    [Fact]
    public async Task A_missing_task_type_is_marked_at_the_field_and_focused()
    {
        (string token, _) = await SignInAsync();
        (string map, string folder, string service, _) = await MapWithALayerAsync(withLayer: true);

        try
        {
            await OpenSettingsAsync(map, token, "!!document.getElementById('mapTasksOn')");
            await Browser.EvaluateAsync<bool>("(() => { document.getElementById('mapTasksOn').click(); return true; })()");

            Assert.Equal("true", await Browser.EvaluateAsync<string>("document.getElementById('mapTasksTypes').getAttribute('aria-invalid')"));
            Assert.Equal("mapTasksTypes", await Browser.EvaluateAsync<string>("document.activeElement && document.activeElement.id"));
            Assert.Contains("at least one task type",
                await Browser.EvaluateAsync<string>("document.getElementById('mapTasksTypesError').textContent") ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(await WritesAsync(), w => w.Contains("/tasks", StringComparison.Ordinal));
        }
        finally
        {
            await CleanUpAsync(map, folder, service);
        }
    }

    /// <summary>Disabled tasks come back with the lists the layer still holds (design review M4).</summary>
    [Fact]
    public async Task Enabling_again_starts_from_the_lists_the_layer_holds()
    {
        (string token, _) = await SignInAsync();
        (string map, string folder, string service, _) = await MapWithALayerAsync(withLayer: true);

        try
        {
            (int on, string onBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tasks", JsonSerializer.Serialize(new
            {
                layer = "tasks1", types = Inspection, assignees = new[] { new { username = "zz_worker", label = "A worker" } },
            }));
            Assert.True(on == 200, onBody);
            await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}/tasks");

            await OpenSettingsAsync(map, token, "!!document.getElementById('mapTasksTypes')");

            Assert.Equal("Inspection", await Browser.EvaluateAsync<string>("document.getElementById('mapTasksTypes').value"));
            Assert.Contains("already holds are filled in",
                await Browser.EvaluateAsync<string>("document.getElementById('mapTasksTypesHint').textContent") ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await CleanUpAsync(map, folder, service);
        }
    }

    /// <summary>
    /// Settings left behind by a layer taken out of the map are said, and offered for removal (owner decision
    /// 2026-11-08, *"ArcGIS ile aynı olsun"*).
    /// </summary>
    [Fact]
    public async Task Settings_for_a_layer_no_longer_in_the_map_are_offered_for_removal()
    {
        (string token, _) = await SignInAsync();
        (string map, string folder, string service, _) = await MapWithALayerAsync(withLayer: true);

        try
        {
            (int on, string onBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tasks", JsonSerializer.Serialize(new
            {
                layer = "tasks1", types = Inspection, assignees = new[] { new { username = "zz_worker" } },
            }));
            Assert.True(on == 200, onBody);
            (int saved, string savedBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}", MapBody("emptied", null));
            Assert.True(saved == 200, savedBody);

            await OpenSettingsAsync(map, token, "!!document.getElementById('mapTasksClear')");
            Assert.Contains("no longer in this map",
                await Browser.EvaluateAsync<string>("document.getElementById('mapTasksBody').textContent") ?? "", StringComparison.Ordinal);
            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('mapTasksOn')"),
                "Enable tasks is offered while settings for a removed layer are still on the map.");
        }
        finally
        {
            await CleanUpAsync(map, folder, service);
        }
    }

    /// <summary>A map with no feature layer says why tasks cannot be enabled, and offers no button that would fail.</summary>
    [Fact]
    public async Task A_map_without_a_feature_layer_says_why_and_offers_nothing()
    {
        (string token, _) = await SignInAsync();
        (string map, _, _, _) = await MapWithALayerAsync(withLayer: false);

        try
        {
            await OpenSettingsAsync(map, token, "(document.getElementById('mapTasksBody')?.textContent || '').includes('Not available')");

            Assert.False(await Browser.EvaluateAsync<bool>("!!document.getElementById('mapTasksOn')"),
                "Enable tasks is offered on a map with no layer to put them on.");
            Assert.True(await Browser.EvaluateAsync<bool>("!!document.querySelector('#mapTasksBody a[href*=\"webmap.html\"]')"),
                "The way to add a layer is not a link.");
        }
        finally
        {
            await CleanUpAsync(map, string.Empty, string.Empty);
        }
    }
}
