using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Graticula.Catalog;
using Graticula.Features;

namespace Graticula.Api.ArcGis;

/// <summary>
/// A layer's contingent values in the shape ArcGIS's <c>contingentValues</c> resource and <c>queryContingentValues</c>
/// operation answer, and read back from <c>addToDefinition</c>'s <c>contingentValuesDefinition</c> — ADR-312 §5.4–5.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys from the public reference</b>
/// (<see href="https://developers.arcgis.com/rest/services-reference/enterprise/fs-contingent-values/">Contingent
/// Values</see>, <see href="https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/">Query
/// Contingent Values</see>): <c>typeCodes</c>, <c>stringDicts</c> and a definition of <c>layerID</c>,
/// <c>layerName</c>, <c>geometryType</c>, <c>hasSubType</c> and <c>fieldGroups</c>, each with <c>name</c>,
/// <c>restrictive</c>, <c>fields</c> and <c>subTypes</c> of <c>code</c>, <c>name</c>, <c>domains</c> and
/// <c>contingentValues</c> of <c>id</c>, <c>types</c> and <c>values</c>.
/// </para>
/// <para>
/// <b>The encoding of a value is INFERRED from the examples</b> (ADR-312 §5.9): a text code is its index in its
/// dictionary's <c>entries</c>, a range takes two slots, and <c>Any</c> and <c>Null</c> take none.
/// </para>
/// </remarks>
public static class ContingentValuesDocument
{
    /// <summary>The type codes, in the order the reference prints them; a value's <c>types</c> are indexes into it.</summary>
    public static readonly IReadOnlyList<string> TypeCodes = ["Unknown", "Any", "Null", "Code", "Range"];

    private const int AnyCode = 1;
    private const int NullCode = 2;
    private const int CodeCode = 3;
    private const int RangeCode = 4;

    /// <summary>Dictionaries of text codes being built for one answer, shared by every layer in it.</summary>
    public sealed class Dictionaries
    {
        private readonly List<(string Domain, List<string> Entries)> _held = [];

        /// <summary>The dictionaries as the answer's <c>stringDicts</c>.</summary>
        public object[] Written => [.. _held.Select(d => new { domain = d.Domain, entries = d.Entries.ToArray() })];

        /// <summary>The index of <paramref name="code"/> in the dictionary named <paramref name="domain"/>.</summary>
        /// <param name="domain">The domain's name, or the field's for a text field with no coded domain.</param>
        /// <param name="seed">The domain's codes in order, to start a dictionary with; empty for none.</param>
        /// <param name="code">The code.</param>
        /// <returns>Its index.</returns>
        public int IndexOf(string domain, IEnumerable<string> seed, string code)
        {
            int at = _held.FindIndex(d => d.Domain == domain);

            if (at < 0)
            {
                _held.Add((domain, [.. seed]));
                at = _held.Count - 1;
            }

            List<string> entries = _held[at].Entries;
            int index = entries.IndexOf(code);

            if (index < 0)
            {
                entries.Add(code);
                index = entries.Count - 1;
            }

            return index;
        }
    }

    /// <summary>
    /// One layer's definition as the resource's <c>contingentValuesDefinition</c>, or one entry of the operation's
    /// <c>contingentValuesDefinitions</c>.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="layerId">Its number in the service.</param>
    /// <param name="layerName">Its name as the layer document gives it.</param>
    /// <param name="geometryType">Its geometry type's ArcGIS name, or null for a table.</param>
    /// <param name="description">Its columns as a client sees them.</param>
    /// <param name="values">Its definition, already narrowed to those columns.</param>
    /// <param name="dictionaries">The answer's text dictionaries.</param>
    /// <param name="groupSubset">Group names to keep, or null for all (12.2's <c>fieldGroupSubset</c>).</param>
    /// <param name="idSubset">Which value ids to keep, or null for all (12.2's <c>idSubset</c>).</param>
    /// <returns>The object to serialize.</returns>
    public static Dictionary<string, object?> Definition(
        LayerDefinition layer,
        int layerId,
        string layerName,
        string? geometryType,
        LayerDescription description,
        ContingentValues values,
        Dictionaries dictionaries,
        ISet<string>? groupSubset = null,
        Func<int, bool>? idSubset = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(dictionaries);

        LayerSubtypes? subtypes = description.Subtypes;
        List<object> groups = [];

        foreach (FieldGroup group in values.Groups)
        {
            if (groupSubset is not null && !groupSubset.Contains(group.Name))
            {
                continue;
            }

            FieldDescription[] fields = [.. group.Fields.Select(f => description.Fields.First(d => d.Name == f))];
            ContingentValue[] held = [.. values.Values.Where(v =>
                !v.Retired
                && string.Equals(v.FieldGroup, group.Name, StringComparison.OrdinalIgnoreCase)
                && (idSubset is null || idSubset(v.Id)))];

            // INFERRED (ADR-312 §5.4): a subtype entry for each subtype that has values, in the layer's order; on a layer
            // without subtypes, one entry with code 0 and an empty name.
            IEnumerable<(long Code, string Name)> kinds = subtypes is null
                ? [(0L, string.Empty)]
                : subtypes.Types.Select(t => (t.Code, t.Name));

            List<object> subTypes = [];

            foreach ((long code, string name) in kinds)
            {
                ContingentValue[] mine = [.. held.Where(v => subtypes is null ? v.Subtype is null : v.Subtype == code)];

                if (mine.Length == 0)
                {
                    continue;
                }

                Dictionary<string, string> domains = new(StringComparer.Ordinal);

                foreach (FieldDescription field in fields)
                {
                    if (DomainOf(field, subtypes, subtypes is null ? null : code) is { } domain)
                    {
                        domains[field.Name] = domain.Name;
                    }
                }

                subTypes.Add(new
                {
                    code,
                    name,
                    domains,
                    contingentValues = mine.Select(v => Written(v, fields, subtypes, subtypes is null ? null : code, dictionaries)).ToArray(),
                });
            }

            groups.Add(new
            {
                name = group.Name,
                restrictive = group.Restrictive,
                fields = fields.Select(f => new
                {
                    name = f.Name,
                    id = IndexOf(description.Fields, f.Name),
                    fieldType = FeatureServerMetadataWriter.FieldTypeOf(layer, description, f),
                }).ToArray(),
                subTypes = subTypes.ToArray(),
            });
        }

        return new Dictionary<string, object?>
        {
            ["layerID"] = layerId,
            ["layerName"] = layerName,
            ["geometryType"] = geometryType,
            ["hasSubType"] = subtypes is not null,
            ["fieldGroups"] = groups.ToArray(),
        };
    }

    private static int IndexOf(IReadOnlyList<FieldDescription> fields, string name)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static FieldDomain? DomainOf(FieldDescription field, LayerSubtypes? subtypes, long? code) =>
        subtypes is null ? field.Domain : subtypes.DomainFor(field.Name, code, field.Domain);

    private static object Written(
        ContingentValue value, FieldDescription[] fields, LayerSubtypes? subtypes, long? code, Dictionaries dictionaries)
    {
        List<int> types = [];
        List<object> written = [];

        foreach (FieldDescription field in fields)
        {
            ContingentEntry entry = value.Entries.First(e => e.Field == field.Name);

            switch (entry.Kind)
            {
                case ContingentKind.Any:
                    types.Add(AnyCode);
                    break;

                case ContingentKind.Null:
                    types.Add(NullCode);
                    break;

                case ContingentKind.CodedValue:
                    types.Add(CodeCode);

                    if (entry.Code!.Value.Text is { } text)
                    {
                        FieldDomain? domain = DomainOf(field, subtypes, code);
                        IEnumerable<string> seed = domain is { Kind: DomainKind.CodedValue }
                            ? domain.Codes.Select(c => c.Code.Text ?? c.Code.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                            : [];
                        written.Add(dictionaries.IndexOf(domain?.Kind == DomainKind.CodedValue ? domain.Name : field.Name, seed, text));
                    }
                    else
                    {
                        written.Add(entry.Code.Value.Number!.Value);
                    }

                    break;

                case ContingentKind.Range:
                    types.Add(RangeCode);
                    written.Add(entry.Min!.Value.Number!.Value);
                    written.Add(entry.Max!.Value.Number!.Value);
                    break;
            }
        }

        return new { id = value.Id, types = types.ToArray(), values = written.ToArray() };
    }

    /// <summary>
    /// Reads <c>addToDefinition</c>'s <c>contingentValuesDefinition</c> into groups and values — ADR-312 §5.5.
    /// </summary>
    /// <param name="element">The <c>contingentValuesDefinition</c> object.</param>
    /// <param name="description">The layer's columns as a client sees them.</param>
    /// <param name="existing">The layer's definition now, for the fields of a group the request does not define.</param>
    /// <param name="groups">The groups the request adds.</param>
    /// <param name="values">The values it adds, with id 0.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryRead(
        JsonElement element,
        LayerDescription description,
        ContingentValues existing,
        out List<FieldGroup> groups,
        out List<ContingentValue> values,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(existing);

        groups = [];
        values = [];
        error = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "'contingentValuesDefinition' is a JSON object.";
            return false;
        }

        Dictionary<string, string[]> strings = new(StringComparer.Ordinal);

        if (element.TryGetProperty("stringDicts", out JsonElement dicts) && dicts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement dict in dicts.EnumerateArray())
            {
                if (Text(dict, "domain") is { } domain
                    && dict.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    strings[domain] = [.. entries.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString())];
                }
            }
        }

        if (!element.TryGetProperty("fieldGroups", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            error = "'contingentValuesDefinition' carries no fieldGroups.";
            return false;
        }

        LayerSubtypes? subtypes = description.Subtypes;

        foreach (JsonElement group in list.EnumerateArray())
        {
            if (Text(group, "name") is not { } name)
            {
                error = "A field group has no name.";
                return false;
            }

            // INFERRED (ADR-312 §5.5): the fields are on the group, or on a `fields` inside its `contingentValues`, which
            // is where the page's example five is read as putting them.
            List<string>? named = Names(group);

            if (named is null && group.TryGetProperty("contingentValues", out JsonElement inner))
            {
                named = inner.ValueKind == JsonValueKind.Object ? Names(inner)
                    : inner.ValueKind == JsonValueKind.Array ? inner.EnumerateArray().Select(Names).FirstOrDefault(n => n is not null)
                    : null;
            }

            FieldGroup? defined = null;

            if (named is { Count: > 0 })
            {
                bool restrictive = !group.TryGetProperty("restrictive", out JsonElement r) || r.ValueKind != JsonValueKind.False;
                defined = new FieldGroup(name, restrictive, named);
                groups.Add(defined);
            }

            FieldGroup? target = defined ?? existing.Group(name);

            if (!group.TryGetProperty("subTypes", out JsonElement kinds) || kinds.ValueKind != JsonValueKind.Array)
            {
                if (target is null)
                {
                    error = $"The field group '{name}' names no fields and does not exist on this layer.";
                    return false;
                }

                continue;
            }

            if (target is null)
            {
                error = $"Contingent values are given for '{name}', which names no fields and does not exist on this layer.";
                return false;
            }

            foreach (JsonElement kind in kinds.EnumerateArray())
            {
                long? code = kind.TryGetProperty("code", out JsonElement c) && c.TryGetInt64(out long read) ? read : null;
                long? subtype = subtypes is null ? null : code;

                if (!kind.TryGetProperty("contingentValues", out JsonElement held) || held.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement value in held.EnumerateArray())
                {
                    if (ReadValue(value, target, description, subtypes, subtype, strings, out string? wrong) is not { } entries)
                    {
                        error = $"A contingent value of '{name}': {wrong}";
                        return false;
                    }

                    values.Add(new ContingentValue(0, target.Name, subtype, false, entries));
                }
            }
        }

        return true;
    }

    private static List<ContingentEntry>? ReadValue(
        JsonElement value,
        FieldGroup group,
        LayerDescription description,
        LayerSubtypes? subtypes,
        long? subtype,
        Dictionary<string, string[]> strings,
        out string? error)
    {
        error = null;

        if (!value.TryGetProperty("types", out JsonElement types) || types.ValueKind != JsonValueKind.Array
            || types.GetArrayLength() != group.Fields.Count)
        {
            error = $"its types give one entry for each of the group's {group.Fields.Count} fields.";
            return null;
        }

        JsonElement[] slots = value.TryGetProperty("values", out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray()]
            : [];
        int at = 0;
        List<ContingentEntry> entries = [];

        for (int i = 0; i < group.Fields.Count; i++)
        {
            string name = group.Fields[i];
            FieldDescription? field = description.Fields.Where(f => f.Name == name).Select(f => (FieldDescription?)f).FirstOrDefault();

            if (field is null)
            {
                error = $"'{name}' is not a field of this layer.";
                return null;
            }

            int type = types[i].TryGetInt32(out int t) ? t : 0;

            switch (type)
            {
                case AnyCode:
                    entries.Add(new ContingentEntry(name, ContingentKind.Any));
                    break;

                case NullCode:
                    entries.Add(new ContingentEntry(name, ContingentKind.Null));
                    break;

                case CodeCode:
                    if (at >= slots.Length)
                    {
                        error = "its values are fewer than its types need.";
                        return null;
                    }

                    JsonElement slot = slots[at++];

                    if (field.Value.Type == FieldType.Text)
                    {
                        FieldDomain? domain = DomainOf(field.Value, subtypes, subtype);
                        string dictionary = domain?.Kind == DomainKind.CodedValue ? domain.Name : name;

                        if (slot.ValueKind == JsonValueKind.String)
                        {
                            entries.Add(new ContingentEntry(name, ContingentKind.CodedValue, Code: DomainValue.Of(slot.GetString()!)));
                        }
                        else if (slot.TryGetInt32(out int index) && strings.TryGetValue(dictionary, out string[]? said)
                            && index >= 0 && index < said.Length)
                        {
                            entries.Add(new ContingentEntry(name, ContingentKind.CodedValue, Code: DomainValue.Of(said[index])));
                        }
                        else
                        {
                            error = $"the code for '{name}' is an index into the stringDicts entry '{dictionary}', and there is "
                                + "no such entry or index.";
                            return null;
                        }
                    }
                    else if (slot.ValueKind == JsonValueKind.Number && slot.TryGetDecimal(out decimal number))
                    {
                        entries.Add(new ContingentEntry(name, ContingentKind.CodedValue, Code: DomainValue.Of(number)));
                    }
                    else
                    {
                        error = $"the code for '{name}' is not a number.";
                        return null;
                    }

                    break;

                case RangeCode:
                    if (at + 1 >= slots.Length
                        || !slots[at].TryGetDecimal(out decimal min) || !slots[at + 1].TryGetDecimal(out decimal max))
                    {
                        error = $"the range for '{name}' takes two values, minimum then maximum.";
                        return null;
                    }

                    at += 2;
                    entries.Add(new ContingentEntry(name, ContingentKind.Range, Min: DomainValue.Of(min), Max: DomainValue.Of(max)));
                    break;

                default:
                    error = $"{type} is not a type code this definition takes: 1 Any, 2 Null, 3 Code, 4 Range.";
                    return null;
            }
        }

        if (at != slots.Length)
        {
            error = "its values are more than its types take.";
            return null;
        }

        return entries;
    }

    private static List<string>? Names(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> names = [];

        foreach (JsonElement field in fields.EnumerateArray())
        {
            string? name = field.ValueKind == JsonValueKind.String ? field.GetString() : Text(field, "name");

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        return names;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>
    /// Reads an id list such as <c>1,3,5-7</c> (12.2's <c>idSubset</c>) or a layer list with <c>-n</c> exclusions.
    /// </summary>
    /// <param name="text">The parameter's value.</param>
    /// <param name="keep">Whether an id is kept, when it returns true.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryIds(string? text, out Func<int, bool>? keep, out string? error)
    {
        keep = null;
        error = null;
        string raw = (text ?? string.Empty).Trim().Trim('[', ']');

        if (raw.Length == 0)
        {
            return true;
        }

        List<(int From, int To)> wanted = [];
        HashSet<int> excluded = [];

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('-') && int.TryParse(part[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int minus))
            {
                excluded.Add(minus);
                continue;
            }

            int dash = part.IndexOf('-', 1);

            if (dash > 0
                && int.TryParse(part[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                && int.TryParse(part[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int to)
                && from <= to)
            {
                wanted.Add((from, to));
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int one))
            {
                wanted.Add((one, one));
            }
            else
            {
                error = $"'{part}' is not an id, a range like 1-3, or an exclusion like -7.";
                return false;
            }
        }

        keep = id => !excluded.Contains(id) && (wanted.Count == 0 || wanted.Any(w => id >= w.From && id <= w.To));
        return true;
    }
}
