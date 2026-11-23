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
/// operation answer, and read back from <c>addToDefinition</c>'s <c>contingentValuesDefinition</c> — ADR-312 §5.4–5.5,
/// with the shape ADR-314 §5.5 observed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys from the public reference</b>
/// (<see href="https://developers.arcgis.com/rest/services-reference/enterprise/fs-contingent-values/">Contingent
/// Values</see>, <see href="https://developers.arcgis.com/rest/services-reference/enterprise/query-contingent-values-feature-service/">Query
/// Contingent Values</see>): <c>typeCodes</c>, <c>stringDicts</c> and a definition of <c>layerID</c>,
/// <c>layerName</c>, <c>geometryType</c>, <c>hasSubType</c> and <c>fieldGroups</c>.
/// </para>
/// <para>
/// <b>The encoding of a value is observed</b> (ADR-314 §5.5, <c>docs/research/contingent-values-pbf.md</c> §5): a text
/// code is its index in its dictionary's <c>entries</c>, a range is one slot <c>{"range":[min,max]}</c>, <c>Any</c> and
/// <c>Null</c> take none, a field's <c>id</c> is its position in the group, a layer without subtypes puts
/// <c>domains</c> and <c>contingentValues</c> on the group, and a retired value is served with <c>"retired": 1</c>.
/// </para>
/// <para>
/// <b>One served shape, two writers.</b> <see cref="Serve"/> picks what is served; <see cref="Json"/> and
/// <see cref="Pbf.ContingentValuesPbf"/> write it, so the two formats cannot disagree on which values a client sees.
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

    /// <summary>One layer's definition as it is served.</summary>
    /// <param name="LayerId">Its number in the service.</param>
    /// <param name="LayerName">Its name as the layer document gives it.</param>
    /// <param name="GeometryType">Its geometry type's ArcGIS name, or null for a table.</param>
    /// <param name="HasSubType">Whether the layer has subtypes.</param>
    /// <param name="Groups">Its served groups, in the stored order.</param>
    public sealed record ServedDefinition(
        int LayerId, string LayerName, string? GeometryType, bool HasSubType, IReadOnlyList<ServedGroup> Groups);

    /// <summary>One served field group.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="Restrictive">Whether it is restrictive.</param>
    /// <param name="Fields">Its fields, in the group's order.</param>
    /// <param name="Kinds">
    /// On a layer with subtypes, one entry for each subtype that has values, in the layer's order; on a layer without,
    /// exactly one entry with code 0, values or not.
    /// </param>
    public sealed record ServedGroup(string Name, bool Restrictive, IReadOnlyList<ServedField> Fields, IReadOnlyList<ServedKind> Kinds);

    /// <summary>One field of a served group.</summary>
    /// <param name="Name">The column.</param>
    /// <param name="FieldType">Its <c>esriFieldType…</c> name, as the layer document says it.</param>
    public sealed record ServedField(string Name, string FieldType);

    /// <summary>A group's values for one subtype, or for a layer without subtypes.</summary>
    /// <param name="Code">The subtype code, or 0.</param>
    /// <param name="Name">The subtype's name, or empty.</param>
    /// <param name="Domains">Each group field's domain for this subtype, or null; in the group's field order.</param>
    /// <param name="Values">The values, retired ones included, by id.</param>
    public sealed record ServedKind(long Code, string Name, IReadOnlyList<FieldDomain?> Domains, IReadOnlyList<ServedValue> Values);

    /// <summary>One served contingent value.</summary>
    /// <param name="Id">Its id.</param>
    /// <param name="Subtype">Its subtype code, or null on a layer without subtypes.</param>
    /// <param name="Retired">Whether it is retired.</param>
    /// <param name="Entries">One entry per group field, in the group's field order.</param>
    public sealed record ServedValue(int Id, long? Subtype, bool Retired, IReadOnlyList<ContingentEntry> Entries);

    /// <summary>How the JSON answer is written: <c>compactFormat</c> and <c>domainDictionaries</c>.</summary>
    /// <param name="Compact">
    /// <c>compactFormat=true</c>, the default: <c>typeCodes</c> and <c>stringDicts</c>, types as numbers, text codes as
    /// indexes. False: types as names, codes as their coded value's name, <c>retired</c> on every value.
    /// </param>
    /// <param name="DomainDictionaries"><c>none</c>, <c>complete</c> or <c>trimmed</c>.</param>
    public sealed record JsonOptions(bool Compact = true, string DomainDictionaries = "none")
    {
        /// <summary>The default answer.</summary>
        public static JsonOptions Default { get; } = new();
    }

    /// <summary>Dictionaries of text codes being built for one answer, shared by every layer in it.</summary>
    public sealed class Dictionaries
    {
        private readonly List<(string Domain, List<string> Entries)> _held = [];

        /// <summary>The dictionaries as the answer's <c>stringDicts</c>.</summary>
        public object[] Written => [.. _held.Select(d => new { domain = d.Domain, entries = d.Entries.ToArray() })];

        /// <summary>
        /// The index of <paramref name="code"/> in the dictionary named <paramref name="domain"/>, added at the end the
        /// first time it is used — a dictionary holds the codes the values use, in first-use order (ADR-314 §5.5).
        /// </summary>
        /// <param name="domain">The domain's name, or the field's for a text field with no coded domain.</param>
        /// <param name="code">The code.</param>
        /// <returns>Its index.</returns>
        public int IndexOf(string domain, string code)
        {
            int at = _held.FindIndex(d => d.Domain == domain);

            if (at < 0)
            {
                _held.Add((domain, []));
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
    /// The answer's <c>domainDicts</c> — <c>domainDictionaries=complete</c> or <c>trimmed</c> (ADR-314 §5.5): each
    /// domain a served group names, a coded one as code to name in the domain's order, a range as its bounds.
    /// </summary>
    public sealed class DomainDictionaries
    {
        private readonly bool _trimmed;
        private readonly List<FieldDomain> _domains = [];
        private readonly Dictionary<string, HashSet<DomainValue>> _used = new(StringComparer.Ordinal);

        /// <summary>A collector.</summary>
        /// <param name="trimmed">Whether only the codes the values use are kept.</param>
        public DomainDictionaries(bool trimmed) => _trimmed = trimmed;

        /// <summary>Notes the domains and used codes of one served definition.</summary>
        /// <param name="definition">The definition.</param>
        public void Add(ServedDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);

            foreach (ServedGroup group in definition.Groups)
            {
                foreach (ServedKind kind in group.Kinds)
                {
                    for (int i = 0; i < group.Fields.Count; i++)
                    {
                        if (kind.Domains[i] is not { } domain)
                        {
                            continue;
                        }

                        if (!_domains.Any(d => d.Name == domain.Name))
                        {
                            _domains.Add(domain);
                            _used[domain.Name] = [];
                        }

                        foreach (ServedValue value in kind.Values)
                        {
                            if (value.Entries[i] is { Kind: ContingentKind.CodedValue, Code: { } code })
                            {
                                _used[domain.Name].Add(code);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>The answer's <c>domainDicts</c> object.</summary>
        public Dictionary<string, object> Written
        {
            get
            {
                Dictionary<string, object> written = new(StringComparer.Ordinal);

                foreach (FieldDomain domain in _domains)
                {
                    if (domain.Kind == DomainKind.CodedValue)
                    {
                        Dictionary<string, string> codes = new(StringComparer.Ordinal);

                        foreach (CodedValue coded in domain.Codes)
                        {
                            if (!_trimmed || _used[domain.Name].Contains(coded.Code))
                            {
                                codes[Key(coded.Code)] = coded.Name;
                            }
                        }

                        written[domain.Name] = codes;
                    }
                    else
                    {
                        written[domain.Name] = _trimmed
                            ? new Dictionary<string, object>()
                            : (object)new Dictionary<string, object?> { ["rangeMin"] = domain.Min?.Number, ["rangeMax"] = domain.Max?.Number };
                    }
                }

                return written;
            }
        }

        private static string Key(DomainValue code) =>
            code.Text ?? code.Number!.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What a layer serves: its groups in the stored order, each with its fields and its values by subtype, retired ones
    /// included (ADR-314 §5.5).
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="layerId">Its number in the service.</param>
    /// <param name="layerName">Its name as the layer document gives it.</param>
    /// <param name="geometryType">Its geometry type's ArcGIS name, or null for a table.</param>
    /// <param name="description">Its columns as a client sees them.</param>
    /// <param name="values">Its definition, already narrowed to those columns.</param>
    /// <param name="groupSubset">Group names to keep, or null for all (12.2's <c>fieldGroupSubset</c>).</param>
    /// <param name="idSubset">Which value ids to keep, or null for all (12.2's <c>idSubset</c>).</param>
    /// <returns>The served definition.</returns>
    public static ServedDefinition Serve(
        LayerDefinition layer,
        int layerId,
        string layerName,
        string? geometryType,
        LayerDescription description,
        ContingentValues values,
        ISet<string>? groupSubset = null,
        Func<int, bool>? idSubset = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(values);

        LayerSubtypes? subtypes = description.Subtypes;
        List<ServedGroup> groups = [];

        foreach (FieldGroup group in values.Groups)
        {
            if (groupSubset is not null && !groupSubset.Contains(group.Name))
            {
                continue;
            }

            FieldDescription[] fields = [.. group.Fields.Select(f => description.Fields.First(d => d.Name == f))];
            ContingentValue[] held = [.. values.Values
                .Where(v => string.Equals(v.FieldGroup, group.Name, StringComparison.OrdinalIgnoreCase)
                    && (idSubset is null || idSubset(v.Id)))
                .OrderBy(v => v.Id)];

            List<ServedKind> kinds = [];

            if (subtypes is null)
            {
                kinds.Add(Kind(0, string.Empty, null, fields, held, subtypes));
            }
            else
            {
                foreach (Subtype type in subtypes.Types)
                {
                    ContingentValue[] mine = [.. held.Where(v => v.Subtype == type.Code)];

                    if (mine.Length > 0)
                    {
                        kinds.Add(Kind(type.Code, type.Name, type.Code, fields, mine, subtypes));
                    }
                }
            }

            groups.Add(new ServedGroup(
                group.Name,
                group.Restrictive,
                [.. fields.Select(f => new ServedField(f.Name, FeatureServerMetadataWriter.FieldTypeOf(layer, description, f)))],
                kinds));
        }

        return new ServedDefinition(layerId, layerName, geometryType, subtypes is not null, groups);
    }

    private static ServedKind Kind(
        long code, string name, long? subtype, FieldDescription[] fields, IEnumerable<ContingentValue> values, LayerSubtypes? subtypes) =>
        new(
            code,
            name,
            [.. fields.Select(f => DomainOf(f, subtypes, subtype))],
            [.. values.Select(v => new ServedValue(
                v.Id, v.Subtype, v.Retired, [.. fields.Select(f => v.Entries.First(e => e.Field == f.Name))]))]);

    /// <summary>
    /// One layer's definition as the resource's <c>contingentValuesDefinition</c>, or one entry of the operation's
    /// <c>contingentValuesDefinitions</c> — the shape ADR-314 §5.5 observed.
    /// </summary>
    /// <param name="served">What the layer serves.</param>
    /// <param name="dictionaries">The answer's text dictionaries; unused when <paramref name="options"/> is not compact.</param>
    /// <param name="options">The answer's format options, or null for the default.</param>
    /// <returns>The object to serialize.</returns>
    public static Dictionary<string, object?> Json(ServedDefinition served, Dictionaries dictionaries, JsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(served);
        ArgumentNullException.ThrowIfNull(dictionaries);
        options ??= JsonOptions.Default;

        List<object> groups = [];

        foreach (ServedGroup group in served.Groups)
        {
            Dictionary<string, object?> written = new()
            {
                ["name"] = group.Name,
                ["restrictive"] = group.Restrictive,
                ["fields"] = group.Fields.Select((f, i) => new { id = i, name = f.Name, fieldType = f.FieldType }).ToArray(),
            };

            if (served.HasSubType)
            {
                written["subTypes"] = group.Kinds.Select(k => new Dictionary<string, object?>
                {
                    ["code"] = k.Code,
                    ["name"] = k.Name,
                    ["domains"] = Domains(group, k),
                    ["contingentValues"] = k.Values.Select(v => Written(v, group, k, dictionaries, options)).ToArray(),
                }).ToArray();
            }
            else
            {
                ServedKind only = group.Kinds.Single();
                written["domains"] = Domains(group, only);
                written["contingentValues"] = only.Values.Select(v => Written(v, group, only, dictionaries, options)).ToArray();
            }

            groups.Add(written);
        }

        return new Dictionary<string, object?>
        {
            ["layerID"] = served.LayerId,
            ["layerName"] = served.LayerName,
            ["geometryType"] = served.GeometryType,
            ["hasSubType"] = served.HasSubType,
            ["fieldGroups"] = groups.ToArray(),
        };
    }

    /// <summary>
    /// The whole answer of the resource (one definition) or the operation (several): <c>typeCodes</c>,
    /// <c>stringDicts</c> and <c>domainDicts</c> as the options ask, then the definitions.
    /// </summary>
    /// <param name="definitions">What each layer serves.</param>
    /// <param name="options">The answer's format options.</param>
    /// <param name="oneLayer">True for the layer resource's <c>contingentValuesDefinition</c>, false for the operation's list.</param>
    /// <returns>The object to serialize.</returns>
    public static Dictionary<string, object?> Answer(IReadOnlyList<ServedDefinition> definitions, JsonOptions options, bool oneLayer)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(options);

        Dictionaries dictionaries = new();
        object[] written = [.. definitions.Select(d => Json(d, dictionaries, options))];
        Dictionary<string, object?> answer = [];

        if (options.Compact)
        {
            answer["typeCodes"] = TypeCodes;
            answer["stringDicts"] = dictionaries.Written;
        }

        if (!string.Equals(options.DomainDictionaries, "none", StringComparison.OrdinalIgnoreCase))
        {
            DomainDictionaries domains = new(string.Equals(options.DomainDictionaries, "trimmed", StringComparison.OrdinalIgnoreCase));

            foreach (ServedDefinition definition in definitions)
            {
                domains.Add(definition);
            }

            answer["domainDicts"] = domains.Written;
        }

        if (oneLayer)
        {
            answer["contingentValuesDefinition"] = written.Single();
        }
        else
        {
            answer["contingentValuesDefinitions"] = written;
        }

        return answer;
    }

    /// <summary>The layer's <c>fieldGroups</c> resource: each served group's name, restrictive and field names.</summary>
    /// <param name="served">What the layer serves.</param>
    /// <returns>The object to serialize.</returns>
    public static object FieldGroups(ServedDefinition served)
    {
        ArgumentNullException.ThrowIfNull(served);
        return new
        {
            fieldGroups = served.Groups.Select(g => new
            {
                name = g.Name,
                restrictive = g.Restrictive,
                fields = g.Fields.Select(f => f.Name).ToArray(),
            }).ToArray(),
        };
    }

    private static Dictionary<string, string> Domains(ServedGroup group, ServedKind kind)
    {
        Dictionary<string, string> domains = new(StringComparer.Ordinal);

        for (int i = 0; i < group.Fields.Count; i++)
        {
            if (kind.Domains[i] is { } domain)
            {
                domains[group.Fields[i].Name] = domain.Name;
            }
        }

        return domains;
    }

    private static FieldDomain? DomainOf(FieldDescription field, LayerSubtypes? subtypes, long? code) =>
        subtypes is null ? field.Domain : subtypes.DomainFor(field.Name, code, field.Domain);

    /// <summary>Whether a field is written as text to an ArcGIS client — its text codes go through a dictionary.</summary>
    internal static bool IsText(ServedField field) =>
        string.Equals(field.FieldType, "esriFieldTypeString", StringComparison.Ordinal);

    /// <summary>A code as text, for a field a client reads as text.</summary>
    internal static string TextOf(DomainValue code) =>
        code.Text ?? code.Number!.Value.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> Written(
        ServedValue value, ServedGroup group, ServedKind kind, Dictionaries dictionaries, JsonOptions options)
    {
        List<object> types = [];
        List<object> written = [];

        for (int i = 0; i < group.Fields.Count; i++)
        {
            ServedField field = group.Fields[i];
            ContingentEntry entry = value.Entries[i];
            FieldDomain? domain = kind.Domains[i];

            switch (entry.Kind)
            {
                case ContingentKind.Any:
                    types.Add(options.Compact ? AnyCode : "Any");
                    break;

                case ContingentKind.Null:
                    types.Add(options.Compact ? NullCode : "Null");
                    break;

                case ContingentKind.CodedValue:
                    types.Add(options.Compact ? CodeCode : "Code");
                    DomainValue code = entry.Code!.Value;

                    if (!options.Compact)
                    {
                        // Observed: the coded value's name. INFERRED (ADR-314 §5.8 item 6): the code itself without one.
                        string? name = domain is { Kind: DomainKind.CodedValue } coded
                            ? coded.Codes.FirstOrDefault(c => c.Code == code)?.Name
                            : null;
                        written.Add(name ?? (object?)code.Text ?? code.Number!.Value);
                    }
                    else if (IsText(field))
                    {
                        written.Add(dictionaries.IndexOf(domain?.Kind == DomainKind.CodedValue ? domain.Name : field.Name, TextOf(code)));
                    }
                    else
                    {
                        written.Add(code.Number ?? (object)code.Text!);
                    }

                    break;

                case ContingentKind.Range:
                    types.Add(options.Compact ? RangeCode : "Range");
                    written.Add(new { range = new[] { entry.Min!.Value.Number!.Value, entry.Max!.Value.Number!.Value } });
                    break;
            }
        }

        Dictionary<string, object?> result = new() { ["id"] = value.Id };

        if (!options.Compact)
        {
            result["retired"] = value.Retired;
        }
        else if (value.Retired)
        {
            result["retired"] = 1;
        }

        result["types"] = types.ToArray();
        result["values"] = written.ToArray();
        return result;
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

            // The values sit under `subTypes`, or — on a layer without subtypes, the shape ArcGIS serves (ADR-314 §5.5) —
            // in a `contingentValues` array on the group itself.
            List<(long? Code, JsonElement Held)> kinds = [];

            if (group.TryGetProperty("subTypes", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement kind in listed.EnumerateArray())
                {
                    if (kind.TryGetProperty("contingentValues", out JsonElement held) && held.ValueKind == JsonValueKind.Array)
                    {
                        kinds.Add((kind.TryGetProperty("code", out JsonElement c) && c.TryGetInt64(out long read) ? read : null, held));
                    }
                }
            }
            else if (group.TryGetProperty("contingentValues", out JsonElement flat) && flat.ValueKind == JsonValueKind.Array
                && flat.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("types", out _)))
            {
                kinds.Add((null, flat));
            }
            else
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

            foreach ((long? code, JsonElement held) in kinds)
            {
                long? subtype = subtypes is null ? null : code;

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
                    // One slot `{"range":[min,max]}`, as ArcGIS serves it (ADR-314 §5.5), or two slots, minimum then
                    // maximum, as ADR-312 first read the examples.
                    decimal min;
                    decimal max;

                    if (at < slots.Length && slots[at].ValueKind == JsonValueKind.Object)
                    {
                        if (!slots[at].TryGetProperty("range", out JsonElement bounds) || bounds.ValueKind != JsonValueKind.Array
                            || bounds.GetArrayLength() != 2
                            || !bounds[0].TryGetDecimal(out min) || !bounds[1].TryGetDecimal(out max))
                        {
                            error = $"the range for '{name}' is {{\"range\":[minimum, maximum]}}.";
                            return null;
                        }

                        at += 1;
                    }
                    else if (at + 1 >= slots.Length
                        || !slots[at].TryGetDecimal(out min) || !slots[at + 1].TryGetDecimal(out max))
                    {
                        error = $"the range for '{name}' is {{\"range\":[minimum, maximum]}}, or two values, minimum then maximum.";
                        return null;
                    }
                    else
                    {
                        at += 2;
                    }

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
