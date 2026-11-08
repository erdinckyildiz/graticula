using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Catalog;
using Graticula.Features;

namespace Graticula.Host;

/// <summary>
/// What ArcGIS Field Maps needs to show a map's tasks — ADR-286: the task fields on one layer, and the task
/// configuration its map carries as an item resource.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fields are Esri's documented schema</b> (Field Maps, <i>Prepare tasks</i>): <c>esritask_type</c>,
/// <c>esritask_assignee</c> and <c>esritask_status</c> are required and each has a list of values; priority, due date,
/// description and notes are the defaults Field Maps Designer adds beside them. The status codes 0–3 and priority codes
/// 0–4 are the documented defaults.
/// </para>
/// <para>
/// <b>The configuration's shape is not documented.</b> It was read from the task configurations of nine public web maps
/// on ArcGIS Online (2026-11-08) for its structure only, and this file is written here from that structure and from the
/// documented default filters and actions; no other organisation's file is copied (ADR-286 §4, the clean-room
/// disclosure). Field Maps 26.2.2 read a file written by this class and showed, filtered and changed tasks with it.
/// </para>
/// </remarks>
internal static class FieldMapsTasks
{
    /// <summary>The item resource Field Maps reads for a map's tasks.</summary>
    public const string ResourcePath = "tasks-configuration.json";

    /// <summary>The type keyword a map with tasks carries, which Field Maps' <i>Tasks</i> filter reads.</summary>
    public const string Keyword = "Tasks";

    public const string TypeField = "esritask_type";
    public const string AssigneeField = "esritask_assignee";
    public const string StatusField = "esritask_status";
    public const string PriorityField = "esritask_priority";
    public const string DueDateField = "esritask_duedate";
    public const string DescriptionField = "esritask_description";
    public const string NotesField = "esritask_notes";

    /// <summary>The fields a task layer holds, with their type and length, in the order they are added.</summary>
    public static readonly IReadOnlyList<(string Name, string Alias, FieldType Type, int? Length)> Fields =
    [
        (TypeField, "Task type", FieldType.Integer, null),
        (AssigneeField, "Assignee", FieldType.Text, 255),
        (StatusField, "Status", FieldType.Integer, null),
        (PriorityField, "Priority", FieldType.Integer, null),
        (DueDateField, "Due date", FieldType.Date, null),
        (DescriptionField, "Description", FieldType.Text, 4000),
        (NotesField, "Notes", FieldType.Text, 4000),
    ];

    /// <summary>The four statuses Field Maps' default filters and actions are written against.</summary>
    public static readonly IReadOnlyList<(int Code, string Name)> Statuses =
        [(0, "Unassigned"), (1, "Assigned"), (2, "In Progress"), (3, "Completed")];

    /// <summary>The documented default priorities.</summary>
    public static readonly IReadOnlyList<(int Code, string Name)> Priorities =
        [(0, "None"), (1, "Low"), (2, "Medium"), (3, "High"), (4, "Critical")];

    /// <summary>The name a task list's domain is given on a layer: unique, since domain names are (ADR-087).</summary>
    public static string DomainName(string field, Guid layer) => $"{field}_{layer:N}";

    /// <summary>The status list.</summary>
    public static FieldDomain StatusDomain(string name) =>
        FieldDomain.Coded(name, Statuses.Select(s => new CodedValue(DomainValue.Of(s.Code), s.Name)));

    /// <summary>The priority list.</summary>
    public static FieldDomain PriorityDomain(string name) =>
        FieldDomain.Coded(name, Priorities.Select(p => new CodedValue(DomainValue.Of(p.Code), p.Name)));

    /// <summary>The task types, coded 0, 1, 2… in the order given.</summary>
    public static FieldDomain TypeDomain(string name, IReadOnlyList<string> types) =>
        FieldDomain.Coded(name, types.Select((t, i) => new CodedValue(DomainValue.Of(i), t)));

    /// <summary>The people a task may be assigned to: the code is the account name, which Field Maps matches against
    /// the signed-in user, and the name is what the list shows.</summary>
    public static FieldDomain AssigneeDomain(string name, IReadOnlyList<(string Username, string Label)> people) =>
        FieldDomain.Coded(name, people.Select(p => new CodedValue(DomainValue.Of(p.Username), p.Label)));

    /// <summary>
    /// The task configuration for one layer of a map, as Field Maps reads it.
    /// </summary>
    /// <param name="layerId">The operational layer's <c>id</c> in the web map.</param>
    /// <param name="typeDomain">The name of the task type list.</param>
    /// <param name="assigneeDomain">The name of the assignee list.</param>
    /// <param name="statusDomain">The name of the status list.</param>
    /// <param name="newId">Where each element's id comes from; a test passes a counter.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Configuration(
        string layerId, string typeDomain, string assigneeDomain, string statusDomain, Func<string>? newId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        newId ??= () => Guid.NewGuid().ToString();

        // Field Maps reads the filters and expressions URL-encoded, as Designer writes them.
        static string Encoded(string text) => Uri.EscapeDataString(text);

        string pickUp = newId(), start = newId(), finish = newId(), status = newId(), note = newId();

        JsonObject Filter(string title, string where) => new()
        {
            ["id"] = newId(),
            ["title"] = title,
            ["definitionExpression"] = Encoded(where),
        };

        static JsonObject Expression(string name, string expression, string title, string returns, bool encode = true) => new()
        {
            ["name"] = name,
            ["expression"] = encode ? Uri.EscapeDataString(expression) : expression,
            ["title"] = title,
            ["returnType"] = returns,
        };

        static JsonObject Set(string field, string expression) => new() { ["field"] = field, ["valueExpression"] = expression };

        JsonObject Use(string action, bool predominant = false)
        {
            JsonObject use = new() { ["featureActionId"] = action, ["id"] = newId() };

            if (predominant)
            {
                use["predominant"] = true;
            }

            return use;
        }

        JsonObject Layout(string title, string visibility, params JsonObject[] actions) => new()
        {
            ["id"] = newId(),
            ["title"] = title,
            ["visibilityExpression"] = visibility,
            ["actions"] = new JsonArray([.. actions]),
        };

        JsonObject configuration = new()
        {
            ["version"] = "1.0.0",
            ["layerId"] = layerId,

            // The three filters Field Maps Designer gives a new task layer, in its words (Prepare tasks).
            ["displayOptions"] = new JsonArray(
                Filter("Assigned to me", $"{AssigneeField} = 'CURRENT_USER' AND NOT({StatusField} = 3)"),
                Filter("Unassigned", $"{StatusField} = 0"),
                Filter("Completed", $"{AssigneeField} = 'CURRENT_USER' AND {StatusField} = 3")),

            ["expressionInfos"] = new JsonArray(
                Expression("expr/unassigned", $"$feature[\"{StatusField}\"] == 0", "Unassigned", "boolean"),
                Expression("expr/assigned", $"$feature[\"{StatusField}\"] == 1", "Assigned", "boolean"),
                Expression("expr/in-progress", $"$feature[\"{StatusField}\"] == 2", "In progress", "boolean"),
                Expression("expr/completed", $"$feature[\"{StatusField}\"] == 3", "Completed", "boolean"),
                Expression("expr/set-assignee-to-current-user", "'CURRENT_USER'", "Assignee = current user", "string", encode: false),
                Expression("expr/set-status-assigned", "1", "Status = Assigned", "number", encode: false),
                Expression("expr/set-status-in-progress", "2", "Status = In progress", "number", encode: false),
                Expression("expr/set-status-completed", "3", "Status = Completed", "number", encode: false)),

            // The documented default actions: Pick up, Start and Finish change the status by themselves; Status and
            // Note ask for a value.
            ["featureActions"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = pickUp, ["type"] = "edit", ["title"] = "Pick up", ["icon"] = "user-plus",
                    ["updateType"] = "automatic",
                    ["updates"] = new JsonArray(
                        Set(StatusField, "expr/set-status-assigned"),
                        Set(AssigneeField, "expr/set-assignee-to-current-user")),
                },
                new JsonObject
                {
                    ["id"] = start, ["type"] = "edit", ["title"] = "Start", ["icon"] = "play",
                    ["updateType"] = "automatic",
                    ["updates"] = new JsonArray(Set(StatusField, "expr/set-status-in-progress")),
                },
                new JsonObject
                {
                    ["id"] = finish, ["type"] = "edit", ["title"] = "Finish", ["icon"] = "check",
                    ["updateType"] = "automatic",
                    ["updates"] = new JsonArray(Set(StatusField, "expr/set-status-completed")),
                },
                new JsonObject
                {
                    ["id"] = status, ["type"] = "edit", ["title"] = "Status", ["icon"] = "list-bullet",
                    ["promptField"] = StatusField, ["updateType"] = "manual", ["updates"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["id"] = note, ["type"] = "edit", ["title"] = "Note", ["icon"] = "pencil",
                    ["promptField"] = NotesField, ["updateType"] = "manual", ["updates"] = new JsonArray(),
                }),

            // One layout per status: what a task offers first depends on where it is.
            ["featureActionLayouts"] = new JsonArray(
                Layout("Unassigned", "expr/unassigned", Use(pickUp, predominant: true), Use("directions")),
                Layout("Assigned", "expr/assigned", Use(start, predominant: true), Use(status), Use("directions")),
                Layout("In progress", "expr/in-progress", Use(finish, predominant: true), Use(note), Use(status)),
                Layout("Completed", "expr/completed", Use(status, predominant: true), Use("directions"))),

            ["fieldSchema"] = new JsonObject
            {
                ["assigneeField"] = new JsonObject
                {
                    ["name"] = AssigneeField,
                    ["codedValueDomain"] = new JsonObject { ["name"] = assigneeDomain },
                },
                ["statusField"] = new JsonObject
                {
                    ["name"] = StatusField,
                    ["codedValueDomain"] = new JsonObject
                    {
                        ["name"] = statusDomain,
                        ["keyMap"] = new JsonObject
                        {
                            ["unassigned"] = "Unassigned",
                            ["assigned"] = "Assigned",
                            ["inProgress"] = "In Progress",
                            ["completed"] = "Completed",
                        },
                    },
                },
                ["taskTypeField"] = new JsonObject
                {
                    ["name"] = TypeField,
                    ["codedValueDomain"] = new JsonObject { ["name"] = typeDomain },
                },
            },

            ["groupOptions"] = new JsonArray(
                new JsonObject { ["title"] = "Distance" },
                new JsonObject { ["title"] = "Type", ["field"] = TypeField },
                new JsonObject { ["title"] = "Priority", ["field"] = PriorityField, ["descending"] = true },
                new JsonObject { ["title"] = "Due date", ["field"] = DueDateField },
                new JsonObject { ["title"] = "Status", ["field"] = StatusField, ["descending"] = true }),
        };

        return JsonSerializer.SerializeToUtf8Bytes(configuration);
    }

    /// <summary>The operational layer a stored configuration names, or null when it is not one this can read.</summary>
    public static string? LayerIdOf(byte[] configuration)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(configuration);
            return read.RootElement.ValueKind == JsonValueKind.Object
                && read.RootElement.TryGetProperty("layerId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A map's type keywords with <see cref="Keyword"/> added when it has tasks.</summary>
    public static string[] WithTasks(string[] keywords, bool tasks) =>
        tasks && !keywords.Contains(Keyword, StringComparer.Ordinal)
            ? [.. keywords, Keyword]
            : keywords;
}
