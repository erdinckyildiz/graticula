using System;
using System.Linq;
using System.Text.Json;
using Graticula.Catalog;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The task fields, lists and configuration ArcGIS Field Maps reads — ADR-286.
/// </summary>
public sealed class FieldMapsTasksTests
{
    private static JsonElement Configuration()
    {
        int next = 0;
        byte[] bytes = FieldMapsTasks.Configuration("tasks-layer", "types_x", "people_x", "status_x", () => $"id{next++}");
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    /// <summary>The file names the map's layer, and the lists by the names the layer gives them.</summary>
    [Fact]
    public void The_configuration_names_the_layer_and_its_lists()
    {
        JsonElement read = Configuration();

        Assert.Equal("1.0.0", read.GetProperty("version").GetString());
        Assert.Equal("tasks-layer", read.GetProperty("layerId").GetString());

        JsonElement schema = read.GetProperty("fieldSchema");
        Assert.Equal("esritask_assignee", schema.GetProperty("assigneeField").GetProperty("name").GetString());
        Assert.Equal("people_x", schema.GetProperty("assigneeField").GetProperty("codedValueDomain").GetProperty("name").GetString());
        Assert.Equal("status_x", schema.GetProperty("statusField").GetProperty("codedValueDomain").GetProperty("name").GetString());
        Assert.Equal("types_x", schema.GetProperty("taskTypeField").GetProperty("codedValueDomain").GetProperty("name").GetString());
        Assert.Equal(
            "In Progress",
            schema.GetProperty("statusField").GetProperty("codedValueDomain").GetProperty("keyMap").GetProperty("inProgress").GetString());
    }

    /// <summary>
    /// The three filters are Designer's defaults, URL-encoded as Field Maps reads them, and <c>CURRENT_USER</c> is left
    /// for Field Maps to replace with whoever is signed in.
    /// </summary>
    [Fact]
    public void The_filters_are_the_documented_defaults()
    {
        JsonElement[] filters = [.. Configuration().GetProperty("displayOptions").EnumerateArray()];

        Assert.Equal(["Assigned to me", "Unassigned", "Completed"], filters.Select(f => f.GetProperty("title").GetString()));
        Assert.Equal(
            "esritask_assignee = 'CURRENT_USER' AND NOT(esritask_status = 3)",
            Uri.UnescapeDataString(filters[0].GetProperty("definitionExpression").GetString()!));
        Assert.Equal("esritask_status = 0", Uri.UnescapeDataString(filters[1].GetProperty("definitionExpression").GetString()!));
    }

    /// <summary>Every action a layout offers is an action the file defines or one Field Maps has of its own.</summary>
    [Fact]
    public void Every_layout_offers_actions_that_exist()
    {
        JsonElement read = Configuration();
        string[] defined = [.. read.GetProperty("featureActions").EnumerateArray().Select(a => a.GetProperty("id").GetString()!)];
        string[] expressions = [.. read.GetProperty("expressionInfos").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

        foreach (JsonElement layout in read.GetProperty("featureActionLayouts").EnumerateArray())
        {
            Assert.Contains(layout.GetProperty("visibilityExpression").GetString(), expressions);

            JsonElement[] uses = [.. layout.GetProperty("actions").EnumerateArray()];
            Assert.Single(uses, u => u.TryGetProperty("predominant", out JsonElement p) && p.GetBoolean());

            foreach (JsonElement use in uses)
            {
                string action = use.GetProperty("featureActionId").GetString()!;
                Assert.True(defined.Contains(action) || action is "directions" or "compass" or "edit" or "related", action);
            }
        }

        // And each automatic update names an expression the file holds.
        foreach (JsonElement action in read.GetProperty("featureActions").EnumerateArray())
        {
            foreach (JsonElement update in action.GetProperty("updates").EnumerateArray())
            {
                Assert.Contains(update.GetProperty("valueExpression").GetString(), expressions);
            }
        }
    }

    /// <summary>Pick up gives the task to whoever presses it and marks it assigned.</summary>
    [Fact]
    public void Pick_up_assigns_to_the_current_user()
    {
        JsonElement read = Configuration();
        JsonElement pickUp = read.GetProperty("featureActions").EnumerateArray().Single(a => a.GetProperty("title").GetString() == "Pick up");
        string[] sets = [.. pickUp.GetProperty("updates").EnumerateArray().Select(u => u.GetProperty("field").GetString()!)];

        Assert.Equal(["esritask_status", "esritask_assignee"], sets);

        JsonElement current = read.GetProperty("expressionInfos").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "expr/set-assignee-to-current-user");
        Assert.Equal("'CURRENT_USER'", current.GetProperty("expression").GetString());
    }

    /// <summary>The layer named in a stored file is read back; a file that is not one is no layer.</summary>
    [Fact]
    public void The_layer_is_read_back_from_a_stored_file()
    {
        Assert.Equal("L1", FieldMapsTasks.LayerIdOf(FieldMapsTasks.Configuration("L1", "a", "b", "c")));
        Assert.Null(FieldMapsTasks.LayerIdOf("not json"u8.ToArray()));
        Assert.Null(FieldMapsTasks.LayerIdOf("[]"u8.ToArray()));
    }

    /// <summary>A map with tasks says so once; one without does not.</summary>
    [Fact]
    public void The_Tasks_keyword_is_added_once_when_the_map_has_tasks()
    {
        string[] plain = ["Map", "Web Map"];

        Assert.Equal(plain, FieldMapsTasks.WithTasks(plain, tasks: false));
        Assert.Equal(["Map", "Web Map", "Tasks"], FieldMapsTasks.WithTasks(plain, tasks: true));
        Assert.Equal(["Map", "Tasks"], FieldMapsTasks.WithTasks(["Map", "Tasks"], tasks: true));
    }

    /// <summary>The lists: statuses and priorities are the documented codes; types are coded in order; an assignee's
    /// code is the account name.</summary>
    [Fact]
    public void The_lists_are_coded_as_Field_Maps_reads_them()
    {
        FieldDomain status = FieldMapsTasks.StatusDomain("s");
        Assert.Equal([0m, 1m, 2m, 3m], status.Codes.Select(c => c.Code.Number!.Value));
        Assert.Equal("Completed", status.Codes[3].Name);

        Assert.Equal(5, FieldMapsTasks.PriorityDomain("p").Codes.Count);

        FieldDomain types = FieldMapsTasks.TypeDomain("t", ["Inspect", "Repair"]);
        Assert.Equal([0m, 1m], types.Codes.Select(c => c.Code.Number!.Value));

        FieldDomain people = FieldMapsTasks.AssigneeDomain("a", [("ayse", "Ayşe Yılmaz")]);
        Assert.Equal("ayse", people.Codes[0].Code.Text);
        Assert.Equal("Ayşe Yılmaz", people.Codes[0].Name);
    }

    /// <summary>The request's lists are trimmed and refused, in words, when Field Maps could not use them.</summary>
    [Fact]
    public void The_request_lists_are_checked()
    {
        static string? Check(string[] types, params (string User, string? Label)[] people) =>
            AdminEndpoints.TaskListsOf(
                new AdminEndpoints.WebMapTasksRequest("L", types, [.. people.Select(p => new AdminEndpoints.WebMapTaskAssignee(p.User, p.Label))]),
                out _, out _);

        Assert.Null(Check(["Inspect"], ("ayse", null)));
        Assert.Contains("task type", Check([" "], ("ayse", null)), StringComparison.Ordinal);
        Assert.Contains("assigned to", Check(["Inspect"]), StringComparison.Ordinal);
        Assert.Contains("twice", Check(["Inspect", "inspect"], ("ayse", null)), StringComparison.Ordinal);
        Assert.Contains("twice", Check(["Inspect"], ("ayse", null), ("AYSE", "x")), StringComparison.Ordinal);
        Assert.Contains("account name", Check(["Inspect"], ("o'brien", null)), StringComparison.Ordinal);

        AdminEndpoints.TaskListsOf(
            new AdminEndpoints.WebMapTasksRequest("L", [" Inspect "], [new AdminEndpoints.WebMapTaskAssignee(" ayse ", "")]),
            out string[] types, out (string Username, string Label)[] people);
        Assert.Equal(["Inspect"], types);
        Assert.Equal(("ayse", "ayse"), people[0]);
    }

    /// <summary>The feature layers of a map are found at the top and inside group layers; other kinds are not.</summary>
    [Fact]
    public void A_maps_feature_layers_are_found_inside_groups_too()
    {
        const string document = """
            {"operationalLayers":[
              {"id":"a","title":"A","layerType":"ArcGISFeatureLayer","url":"https://x/rest/services/hosted/a/FeatureServer/0"},
              {"id":"t","layerType":"VectorTileLayer","styleUrl":"https://x/s"},
              {"id":"g","layerType":"GroupLayer","layers":[{"id":"b","layerType":"ArcGISFeatureLayer","url":"u"}]}]}
            """;

        Assert.Equal(["a", "b"], AdminEndpoints.OperationalFeatureLayers(document).Select(l => l.Id));
        Assert.Equal("b", AdminEndpoints.OperationalFeatureLayers(document).Last().Title);
        Assert.Empty(AdminEndpoints.OperationalFeatureLayers("not json"));
        Assert.Empty(AdminEndpoints.OperationalFeatureLayers(null));
    }
}
