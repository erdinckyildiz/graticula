using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Graticula.Host.Forms;

/// <summary>
/// A checked form as the client reads it: questions, groups and repeats as a tree, choice lists, languages, settings.
/// </summary>
/// <remarks>
/// <b>Worked out here, once.</b> The client is sent this rather than the model's sheets, so it never reads XLSForm's
/// columns or aliases (<c>select one</c>, <c>begin_group</c>, <c>readonly</c>) and cannot read them differently from
/// the checker. Expressions are passed as written: evaluating them is the client's (ADR-287 §5.9 (i)).
/// </remarks>
public static class FormCompiled
{
    /// <summary>The client's JSON of a definition.</summary>
    public static JsonObject Json(FormDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new JsonObject
        {
            ["format"] = "graticula-form/1",
            ["title"] = definition.Settings.Title,
            ["formId"] = definition.Settings.FormId,
            ["version"] = definition.Settings.Version,
            ["defaultLanguage"] = definition.Settings.DefaultLanguage,
            ["languages"] = Strings(definition.Languages),
            ["pages"] = definition.Settings.Pages,
            ["instanceName"] = definition.Settings.InstanceName,
            ["basemap"] = definition.Settings.Basemap,
            ["thankYou"] = definition.Settings.ThankYou,
            ["redirect"] = definition.Settings.Redirect,
            ["root"] = Table(definition.Root),
            ["choices"] = new JsonObject([.. definition.ChoiceLists.Select(list => KeyValuePair.Create(list.Key, (JsonNode?)new JsonArray(
                [.. list.Value.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name,
                    ["labels"] = Texts(c.Labels),
                    ["images"] = Texts(c.Images),
                    ["filters"] = Texts(c.Filters),
                })])))]),
        };
    }

    private static JsonObject Table(FormTable table) => new()
    {
        ["kind"] = "repeat",
        ["name"] = table.Name,
        ["labels"] = Texts(table.Label ?? new Dictionary<string, string>()),
        ["relevant"] = table.Relevant,
        ["repeatCount"] = table.RepeatCount,
        ["appearance"] = Strings(table.Appearance),
        ["body"] = Body(table.Body),
    };

    private static JsonArray Body(IReadOnlyList<object> body) => new([.. body.Select(node => (JsonNode)(node switch
    {
        FormQuestion q => Question(q),
        FormGroup g => new JsonObject
        {
            ["kind"] = "group",
            ["name"] = g.Name,
            ["labels"] = Texts(g.Labels),
            ["relevant"] = g.Relevant,
            ["appearance"] = Strings(g.Appearance),
            ["body"] = Body(g.Body),
        },
        FormTable t => Table(t),
        _ => throw new InvalidOperationException($"A form body holds questions, groups and repeats, not {node.GetType().Name}."),
    }))]);

    private static JsonObject Question(FormQuestion q) => new()
    {
        ["kind"] = "question",
        ["name"] = q.Name,
        ["type"] = q.Type,
        ["list"] = q.Type.StartsWith("select_", StringComparison.Ordinal) && !q.Type.EndsWith("_from_file", StringComparison.Ordinal) ? q.Argument : null,
        ["file"] = q.Type.EndsWith("_from_file", StringComparison.Ordinal) ? q.Argument : null,
        ["labels"] = Texts(q.Labels),
        ["hints"] = Texts(q.Hints),
        ["guidanceHints"] = Texts(q.GuidanceHints),
        ["images"] = Texts(q.Images),
        ["requiredMessages"] = Texts(q.RequiredMessages),
        ["constraintMessages"] = Texts(q.ConstraintMessages),
        ["required"] = q.Required,
        ["relevant"] = q.Relevant,
        ["constraint"] = q.Constraint,
        ["calculation"] = q.Calculation,
        ["default"] = q.Default,
        ["readOnly"] = q.ReadOnly,
        ["choiceFilter"] = q.ChoiceFilter,
        ["appearance"] = Strings([.. q.Appearance.Select(a => a.ToLowerInvariant())]),
        ["parameters"] = Texts(q.Parameters),
        ["visible"] = q.Visible,
        ["accuracyThreshold"] = q.AccuracyThreshold,
        ["accept"] = q.Accept,
        ["calculationMode"] = q.CalculationMode,
    };

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);

    private static JsonObject Texts(IReadOnlyDictionary<string, string> texts) =>
        new([.. texts.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)JsonValue.Create(t.Value)))]);
}
