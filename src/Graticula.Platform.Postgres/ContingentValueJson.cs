using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Catalog;

namespace Graticula.Platform.Postgres;

/// <summary>
/// A layer's contingent values as <c>layer.contingent_values</c> holds them, and as Studio's admin route sends and
/// receives them — ADR-312 §5.1–5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>grapy's shape.</b> <c>fieldGroups</c> is <c>[{"name", "restrictive", "fields": [column, …]}]</c>, and
/// <c>contingentValues</c> is <c>[{"id", "fieldGroup", "subtype", "retired", "entries": [[field, type, value], …]}]</c>,
/// where an entry is grapy's <c>grapy_contingent_value.entries</c> item exactly: <c>type</c> is <c>ANY</c>,
/// <c>NULL</c>, <c>CODED_VALUE</c> or <c>RANGE</c>, and a range's value is <c>[min, max]</c> (ADR-235 §5.1).
/// </para>
/// <para>
/// <b>Not the ArcGIS REST shape</b>, which indexes text codes into dictionaries and flattens types into numbers; that
/// is written from this by the REST face, so the stored form never depends on a reading of the reference that §5.9
/// may revise.
/// </para>
/// </remarks>
public static class ContingentValueJson
{
    /// <summary>The words of an entry's type, as grapy writes them.</summary>
    public static readonly IReadOnlyDictionary<ContingentKind, string> Words = new Dictionary<ContingentKind, string>
    {
        [ContingentKind.Any] = "ANY",
        [ContingentKind.Null] = "NULL",
        [ContingentKind.CodedValue] = "CODED_VALUE",
        [ContingentKind.Range] = "RANGE",
    };

    /// <summary>Reads a stored or requested definition.</summary>
    /// <param name="element">The JSON object.</param>
    /// <param name="error">Why not, when it returns null.</param>
    /// <returns>The definition, or null.</returns>
    public static ContingentValues? Read(JsonElement element, out string? error)
    {
        error = null;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return ContingentValues.Empty;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "A contingent-values definition is a JSON object with fieldGroups and contingentValues.";
            return null;
        }

        List<FieldGroup> groups = [];

        if (element.TryGetProperty("fieldGroups", out JsonElement list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                error = "fieldGroups is an array.";
                return null;
            }

            foreach (JsonElement group in list.EnumerateArray())
            {
                string? name = Text(group, "name");
                bool restrictive = !group.TryGetProperty("restrictive", out JsonElement r) || r.ValueKind != JsonValueKind.False;
                List<string> fields = [];

                if (name is null)
                {
                    error = "A field group has no name.";
                    return null;
                }

                if (group.TryGetProperty("fields", out JsonElement names) && names.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement field in names.EnumerateArray())
                    {
                        string? column = field.ValueKind == JsonValueKind.String ? field.GetString()
                            : field.ValueKind == JsonValueKind.Object ? Text(field, "name")
                            : null;

                        if (string.IsNullOrWhiteSpace(column))
                        {
                            error = $"The field group '{name}' names a field that is not a column name.";
                            return null;
                        }

                        fields.Add(column.Trim());
                    }
                }

                groups.Add(new FieldGroup(name.Trim(), restrictive, fields));
            }
        }

        List<ContingentValue> values = [];

        if (element.TryGetProperty("contingentValues", out JsonElement held) && held.ValueKind != JsonValueKind.Null)
        {
            if (held.ValueKind != JsonValueKind.Array)
            {
                error = "contingentValues is an array.";
                return null;
            }

            int position = 0;

            foreach (JsonElement value in held.EnumerateArray())
            {
                position++;

                int id = value.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number
                    && i.TryGetInt32(out int read) ? read : 0;
                string? group = Text(value, "fieldGroup");
                long? subtype = value.TryGetProperty("subtype", out JsonElement s) && s.ValueKind == JsonValueKind.Number
                    && s.TryGetInt64(out long code) ? code : null;
                bool retired = value.TryGetProperty("retired", out JsonElement t) && t.ValueKind == JsonValueKind.True;

                if (group is null)
                {
                    error = $"Contingent value {(id > 0 ? id : position)} names no fieldGroup.";
                    return null;
                }

                if (!value.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                {
                    error = $"Contingent value {(id > 0 ? id : position)} has no entries.";
                    return null;
                }

                List<ContingentEntry> read2 = [];

                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (ReadEntry(entry, out string? wrong) is not { } one)
                    {
                        error = $"Contingent value {(id > 0 ? id : position)}: {wrong}";
                        return null;
                    }

                    read2.Add(one);
                }

                values.Add(new ContingentValue(id, group.Trim(), subtype, retired, read2));
            }
        }

        return new ContingentValues(groups, values);
    }

    /// <summary>Reads a stored definition's text, or null when there is none or it cannot be read.</summary>
    /// <param name="json">The column's text.</param>
    /// <returns>The definition, or null.</returns>
    public static ContingentValues? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ContingentValues? read = Read(document.RootElement, out _);
            return read is { IsEmpty: false } ? read : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ContingentEntry? ReadEntry(JsonElement entry, out string? error)
    {
        error = null;

        if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() is < 2 or > 3
            || entry[0].ValueKind != JsonValueKind.String || entry[1].ValueKind != JsonValueKind.String)
        {
            error = "an entry is [field, type, value].";
            return null;
        }

        string field = entry[0].GetString()!.Trim();
        string word = entry[1].GetString()!.Trim().ToUpperInvariant();
        JsonElement value = entry.GetArrayLength() == 3 ? entry[2] : default;
        ContingentKind? kind = Words.FirstOrDefault(w => w.Value == word) is { Value: not null } found ? found.Key : null;

        switch (kind)
        {
            case ContingentKind.Any:
            case ContingentKind.Null:
                return new ContingentEntry(field, kind.Value);

            case ContingentKind.CodedValue:
                if (!TryScalar(value, out DomainValue code))
                {
                    error = $"the coded value of '{field}' is not a number or a string.";
                    return null;
                }

                return new ContingentEntry(field, ContingentKind.CodedValue, Code: code);

            case ContingentKind.Range:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2
                    || !TryScalar(value[0], out DomainValue min) || !TryScalar(value[1], out DomainValue max))
                {
                    error = $"the range of '{field}' is [min, max].";
                    return null;
                }

                return new ContingentEntry(field, ContingentKind.Range, Min: min, Max: max);

            default:
                error = $"'{word}' is not ANY, NULL, CODED_VALUE or RANGE.";
                return null;
        }
    }

    /// <summary>A definition as it is stored and as the admin route answers it.</summary>
    /// <param name="values">The definition.</param>
    /// <returns>The object to serialize.</returns>
    public static Dictionary<string, object?> Write(ContingentValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new Dictionary<string, object?>
        {
            ["fieldGroups"] = values.Groups.Select(g => new Dictionary<string, object?>
            {
                ["name"] = g.Name,
                ["restrictive"] = g.Restrictive,
                ["fields"] = g.Fields,
            }).ToArray(),
            ["contingentValues"] = values.Values.Select(v => new Dictionary<string, object?>
            {
                ["id"] = v.Id,
                ["fieldGroup"] = v.FieldGroup,
                ["subtype"] = v.Subtype,
                ["retired"] = v.Retired,
                ["entries"] = v.Entries.Select(e => e.Kind switch
                {
                    ContingentKind.CodedValue => new object?[] { e.Field, Words[e.Kind], FieldDomainJson.Scalar(e.Code) },
                    ContingentKind.Range => [e.Field, Words[e.Kind], new[] { FieldDomainJson.Scalar(e.Min), FieldDomainJson.Scalar(e.Max) }],
                    _ => [e.Field, Words[e.Kind], null],
                }).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>A definition as the column's text, or null for an empty one.</summary>
    /// <param name="values">The definition.</param>
    /// <returns>The text, or null.</returns>
    public static string? Serialize(ContingentValues? values) =>
        values is null || values.IsEmpty ? null : JsonSerializer.Serialize(Write(values));

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool TryScalar(JsonElement element, out DomainValue value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDecimal(out decimal number):
                value = DomainValue.Of(number);
                return true;

            case JsonValueKind.String:
                value = DomainValue.Of(element.GetString()!);
                return true;

            default:
                value = default;
                return false;
        }
    }
}
