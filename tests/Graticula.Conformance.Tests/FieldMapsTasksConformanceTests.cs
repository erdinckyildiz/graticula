using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ArcGIS Field Maps tasks turned on and off from a map's Settings — ADR-286 — against the running server.
/// </summary>
/// <remarks>
/// Each assertion is something Field Maps 26.2.2 was seen to need on 2026-11-08: the task fields with their lists, the
/// configuration naming the lists by the names the layer gives them, the <c>Tasks</c> keyword its filter reads, and a
/// query with <c>returnTrueCurves=true</c>, which it sends to fill the task list.
/// </remarks>
[Collection("catalogue walk")]
public sealed class FieldMapsTasksConformanceTests : ArcGisClient
{
    private static readonly string[] Types = ["Inspection", "Repair"];
    private static readonly string[] CodedFields = ["esritask_type", "esritask_assignee", "esritask_status", "esritask_priority"];
    private static readonly string[] Statuses = ["Unassigned", "Assigned", "In Progress", "Completed"];

    [Fact]
    public async Task Tasks_add_the_fields_write_the_configuration_and_come_off_leaving_the_fields()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential.");

        string layer = "zz_tasks_" + Guid.NewGuid().ToString("N")[..8];
        (HttpStatusCode defined, string design) = await RequestAsync(
            HttpMethod.Post, $"{root}/admin/hosted/define", token!,
            JsonSerializer.Serialize(new { name = layer, geometryType = "Point", fields = new[] { new { name = "site", type = "text", nullable = true } } }));
        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}: {design}");

        string feature = JsonDocument.Parse(design).RootElement.GetProperty("services").GetProperty("feature").GetString()!;
        string[] parts = feature.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? map = null;

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/content/webmaps", JsonSerializer.Serialize(new
            {
                title = "zz tasks map", sharing = "private",
                document = new
                {
                    operationalLayers = new[]
                    {
                        new { id = "tasks1", title = "Tasks", layerType = "ArcGISFeatureLayer", url = $"{root}{feature}" },
                        new { id = "tasks2", title = "Same layer again", layerType = "ArcGISFeatureLayer", url = $"{root}{feature}" },
                    },
                    baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                    version = "2.31",
                },
            }));
            Assert.True(made == 201, $"making the map: {made} {madeBody}");
            map = JsonDocument.Parse(madeBody).RootElement.GetProperty("id").GetString()!;

            (int listed, string before) = await AdminAsync(HttpMethod.Get, $"/content/webmaps/{map}/tasks");
            Assert.True(listed == 200, $"reading the tasks: {listed} {before}");
            JsonElement offered = JsonDocument.Parse(before).RootElement;
            Assert.False(offered.GetProperty("enabled").GetBoolean());
            Assert.True(offered.GetProperty("layers")[0].GetProperty("eligible").GetBoolean(), before);

            (int refused, string why) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tasks",
                JsonSerializer.Serialize(new { layer = "tasks1", types = Array.Empty<string>(), assignees = new[] { new { username = "x" } } }));
            Assert.True(refused == 400 && why.Contains("task type", StringComparison.Ordinal), $"{refused} {why}");

            (int on, string onBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tasks", JsonSerializer.Serialize(new
            {
                layer = "tasks1",
                types = Types,
                assignees = new[] { new { username = "zz_worker", label = "A worker" } },
            }));
            Assert.True(on == 200, $"turning tasks on: {on} {onBody}");

            (_, string layerBody) = await RequestAsync(HttpMethod.Get, $"{root}{feature}?f=json", token!, json: null);
            JsonElement[] fields = [.. JsonDocument.Parse(layerBody).RootElement.GetProperty("fields").EnumerateArray()];
            JsonElement Field(string name) => fields.Single(f => f.GetProperty("name").GetString() == name);

            foreach (string coded in CodedFields)
            {
                Assert.Equal("codedValue", Field(coded).GetProperty("domain").GetProperty("type").GetString());
            }

            Assert.Equal("esriFieldTypeDate", Field("esritask_duedate").GetProperty("type").GetString());
            Assert.Equal(
                Statuses,
                Field("esritask_status").GetProperty("domain").GetProperty("codedValues").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

            (HttpStatusCode read, string configuration) = await RequestAsync(
                HttpMethod.Get, $"{root}/sharing/rest/content/items/{map}/resources/tasks-configuration.json", token!, json: null);
            Assert.True(read == HttpStatusCode.OK, $"{(int)read} {configuration}");
            JsonElement config = JsonDocument.Parse(configuration).RootElement;
            Assert.Equal("tasks1", config.GetProperty("layerId").GetString());
            Assert.Equal(
                Field("esritask_assignee").GetProperty("domain").GetProperty("name").GetString(),
                config.GetProperty("fieldSchema").GetProperty("assigneeField").GetProperty("codedValueDomain").GetProperty("name").GetString());

            Assert.Contains("Tasks", await KeywordsAsync(root, token!, map));

            // Who the answer says can work the tasks: the owner edits the layer; a private map reaches its owner.
            (_, string after) = await AdminAsync(HttpMethod.Get, $"/content/webmaps/{map}/tasks");
            JsonElement told = JsonDocument.Parse(after).RootElement;
            Assert.Equal("private", told.GetProperty("sharing").GetString());
            Assert.Equal(JsonValueKind.Array, told.GetProperty("readers").ValueKind);
            JsonElement first = told.GetProperty("layers")[0];
            Assert.Equal(JsonValueKind.Array, first.GetProperty("editors").ValueKind);
            Assert.True(first.GetProperty("editors").GetArrayLength() > 0, after);
            Assert.Empty(first.GetProperty("sharedUpdateGroups").EnumerateArray());
            Assert.Equal(Types, first.GetProperty("types").EnumerateArray().Select(t => t.GetString()));

            // One task layer per map: another layer is refused while tasks are on this one.
            (int other, string otherBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tasks",
                JsonSerializer.Serialize(new { layer = "tasks2", types = Types, assignees = new[] { new { username = "zz_worker" } } }));
            Assert.True(other == 409 && otherBody.Contains("disable tasks first", StringComparison.Ordinal), $"{other} {otherBody}");

            // The task layer taken out of the map elsewhere leaves the settings behind, as ArcGIS's Map Viewer does: the
            // answer says so, and no other layer is enabled until they are removed.
            (int saved, string savedBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}", JsonSerializer.Serialize(new
            {
                title = "zz tasks map", sharing = "private",
                document = new
                {
                    operationalLayers = new[] { new { id = "tasks2", title = "Same layer again", layerType = "ArcGISFeatureLayer", url = $"{root}{feature}" } },
                    baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" },
                    version = "2.31",
                },
            }));
            Assert.True(saved == 200, $"saving the map without its task layer: {saved} {savedBody}");

            (_, string orphan) = await AdminAsync(HttpMethod.Get, $"/content/webmaps/{map}/tasks");
            JsonElement left = JsonDocument.Parse(orphan).RootElement;
            Assert.True(left.GetProperty("orphaned").GetBoolean(), orphan);
            Assert.False(left.GetProperty("enabled").GetBoolean(), orphan);

            (int blocked, string blockedBody) = await AdminAsync(HttpMethod.Put, $"/content/webmaps/{map}/tasks",
                JsonSerializer.Serialize(new { layer = "tasks2", types = Types, assignees = new[] { new { username = "zz_worker" } } }));
            Assert.True(blocked == 409 && blockedBody.Contains("no longer in it", StringComparison.Ordinal), $"{blocked} {blockedBody}");

            // What Field Maps sends to fill the task list.
            (HttpStatusCode curves, string answer) = await RequestAsync(
                HttpMethod.Get, $"{root}{feature}/query?f=json&where=1%3D1&outFields=*&returnTrueCurves=true", token!, json: null);
            Assert.True(curves == HttpStatusCode.OK && !answer.Contains("\"error\"", StringComparison.Ordinal), answer);

            (int off, string offBody) = await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}/tasks");
            Assert.True(off == 200, $"turning tasks off: {off} {offBody}");
            Assert.DoesNotContain("Tasks", await KeywordsAsync(root, token!, map));

            (_, string kept) = await RequestAsync(HttpMethod.Get, $"{root}{feature}?f=json", token!, json: null);
            Assert.Equal(7, JsonDocument.Parse(kept).RootElement.GetProperty("fields").EnumerateArray()
                .Count(f => f.GetProperty("name").GetString()!.StartsWith("esritask_", StringComparison.Ordinal)));
        }
        finally
        {
            if (map is not null)
            {
                (_, string gone) = await AdminAsync(HttpMethod.Delete, $"/content/webmaps/{map}");

                if (JsonDocument.Parse(gone).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
                {
                    await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
                }
            }

            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token!, json: null);
        }
    }

    private async Task<string[]> KeywordsAsync(string root, string token, string map)
    {
        (_, string item) = await RequestAsync(HttpMethod.Get, $"{root}/sharing/rest/content/items/{map}?f=json", token, json: null);
        return [.. JsonDocument.Parse(item).RootElement.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()!)];
    }
}
