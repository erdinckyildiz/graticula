using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Features;

namespace Graticula.Catalog;

/// <summary>What one field of a contingent value allows — ADR-312 §5.1, in grapy's words (ADR-235).</summary>
public enum ContingentKind
{
    /// <summary>Any value, null included (<c>ANY</c>).</summary>
    Any,

    /// <summary>A null (<c>NULL</c>).</summary>
    Null,

    /// <summary>One code (<c>CODED_VALUE</c>).</summary>
    CodedValue,

    /// <summary>A value from a minimum to a maximum, both allowed (<c>RANGE</c>).</summary>
    Range,
}

/// <summary>One field's part of a contingent value.</summary>
/// <param name="Field">The column.</param>
/// <param name="Kind">What it allows.</param>
/// <param name="Code">The code, for <see cref="ContingentKind.CodedValue"/>.</param>
/// <param name="Min">The minimum, for <see cref="ContingentKind.Range"/>.</param>
/// <param name="Max">The maximum, for <see cref="ContingentKind.Range"/>.</param>
public sealed record ContingentEntry(
    string Field, ContingentKind Kind, DomainValue? Code = null, DomainValue? Min = null, DomainValue? Max = null);

/// <summary>A set of a layer's fields whose values depend on each other.</summary>
/// <param name="Name">Its name, unique in the layer without regard to case.</param>
/// <param name="Restrictive">
/// Whether an editor is held to its contingent values. Restrictive is the default, as in Pro.
/// </param>
/// <param name="Fields">Its columns, in order.</param>
public sealed record FieldGroup(string Name, bool Restrictive, IReadOnlyList<string> Fields);

/// <summary>One combination a field group's fields may hold together, for one subtype.</summary>
/// <param name="Id">Its id, unique in the layer.</param>
/// <param name="FieldGroup">The group it belongs to.</param>
/// <param name="Subtype">The subtype code it applies to, or null on a layer without subtypes.</param>
/// <param name="Retired">Whether it is kept for the rows that hold it and offered for no new one.</param>
/// <param name="Entries">One per field of the group.</param>
public sealed record ContingentValue(
    int Id, string FieldGroup, long? Subtype, bool Retired, IReadOnlyList<ContingentEntry> Entries);

/// <summary>
/// A layer's field groups and contingent values — ADR-312.
/// </summary>
/// <param name="Groups">The field groups.</param>
/// <param name="Values">The contingent values.</param>
/// <remarks>
/// <para>
/// <b>grapy's model</b> (ADR-235 §5.1): <c>grapy_field_group</c> is a group and <c>grapy_contingent_value</c>'s
/// <c>entries</c> are <see cref="ContingentEntry"/>s, so a definition moves between the two without translation. The
/// stores stay apart: grapy's is a GeoPackage and this one is a catalogue row.
/// </para>
/// <para>
/// <b>Nothing here refuses an edit.</b> The server keeps and serves the definition; ArcGIS's clients, and Studio's,
/// apply it while somebody edits (ADR-312 §5.7).
/// </para>
/// </remarks>
public sealed record ContingentValues(IReadOnlyList<FieldGroup> Groups, IReadOnlyList<ContingentValue> Values)
{
    /// <summary>The most field groups a layer may have — a bound on a request, not a documented limit.</summary>
    public const int MostGroups = 100;

    /// <summary>The most contingent values a layer may have — a bound on a request, not a documented limit.</summary>
    public const int MostValues = 50_000;

    /// <summary>A definition with nothing in it.</summary>
    public static ContingentValues Empty { get; } = new([], []);

    /// <summary>Whether it holds no field group.</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>The group named <paramref name="name"/>, without regard to case, or null.</summary>
    /// <param name="name">A name.</param>
    /// <returns>The group.</returns>
    public FieldGroup? Group(string name) =>
        Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What is wrong with this definition on a layer with these columns, or null when it may be stored — ADR-312 §5.3.
    /// </summary>
    /// <param name="fields">The layer's columns as a client sees them: hidden ones are absent.</param>
    /// <param name="subtypes">The layer's subtypes, or null.</param>
    /// <param name="objectIdField">The object id column, or null.</param>
    /// <returns>The refusal, naming what is wrong.</returns>
    public string? Refusal(IReadOnlyList<FieldDescription> fields, LayerSubtypes? subtypes, string? objectIdField)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (Groups.Count > MostGroups)
        {
            return $"{Groups.Count} field groups is more than the {MostGroups} a layer may have here.";
        }

        if (Values.Count > MostValues)
        {
            return $"{Values.Count} contingent values is more than the {MostValues} a layer may have here.";
        }

        string? globalId = GlobalIds.FieldOf(fields);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (FieldGroup group in Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                return "A field group has no name.";
            }

            if (group.Name.Length > DomainRules.LongestName)
            {
                return $"The field group name '{group.Name[..40]}…' is longer than {DomainRules.LongestName} characters.";
            }

            if (!names.Add(group.Name))
            {
                return $"Two field groups are named '{group.Name}'. A name is unique in a layer, without regard to case.";
            }

            if (group.Fields.Count == 0)
            {
                return $"The field group '{group.Name}' has no fields.";
            }

            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (string name in group.Fields)
            {
                if (!seen.Add(name))
                {
                    return $"The field group '{group.Name}' names '{name}' twice.";
                }

                if (!fields.Any(f => f.Name == name))
                {
                    return $"The field group '{group.Name}' names '{name}', which is not a field of this layer.";
                }

                if (name == objectIdField || name == globalId || name == subtypes?.Field
                    || fields.First(f => f.Name == name).Maintained)
                {
                    return $"The field group '{group.Name}' names '{name}', which this server or the layer's subtypes "
                        + "maintain. A field group holds the fields an editor fills in (ADR-312 §5.3).";
                }
            }
        }

        HashSet<int> ids = [];

        foreach (ContingentValue value in Values)
        {
            if (value.Id <= 0)
            {
                return $"A contingent value has the id {value.Id}. An id is a whole number from 1.";
            }

            if (!ids.Add(value.Id))
            {
                return $"Two contingent values have the id {value.Id}.";
            }

            if (Group(value.FieldGroup) is not { } group)
            {
                return $"Contingent value {value.Id} names the field group '{value.FieldGroup}', which this layer does not have.";
            }

            Subtype? subtype = null;

            if (subtypes is null)
            {
                if (value.Subtype is not null)
                {
                    return $"Contingent value {value.Id} names the subtype {value.Subtype}, and this layer has no subtypes.";
                }
            }
            else if (value.Subtype is not { } code || (subtype = subtypes.Find(code)) is null)
            {
                return $"Contingent value {value.Id} names no subtype of this layer. On a layer with subtypes each value "
                    + "applies to one of them.";
            }

            if (value.Entries.Count != group.Fields.Count
                || group.Fields.Any(f => !value.Entries.Any(e => e.Field == f)))
            {
                return $"Contingent value {value.Id} does not give one entry for each field of '{group.Name}' "
                    + $"({string.Join(", ", group.Fields)}).";
            }

            foreach (ContingentEntry entry in value.Entries)
            {
                FieldDescription field = fields.First(f => f.Name == entry.Field);
                FieldDomain? domain = subtypes is null ? field.Domain : subtypes.DomainFor(field.Name, subtype?.Code, field.Domain);

                if (EntryRefusal(entry, field, domain) is { } wrong)
                {
                    return $"Contingent value {value.Id}, field '{entry.Field}': {wrong}";
                }
            }
        }

        return null;
    }

    private static string? EntryRefusal(ContingentEntry entry, FieldDescription field, FieldDomain? domain)
    {
        switch (entry.Kind)
        {
            case ContingentKind.Any:
                return null;

            case ContingentKind.Null:
                return field.Nullable ? null : "NULL is offered only on a field that allows nulls.";

            case ContingentKind.CodedValue:
                if (entry.Code is not { } code)
                {
                    return "a coded value needs its code.";
                }

                if (DomainRules.Unfit(code, field.Type, field.MaxLength) is { } unfit)
                {
                    return unfit;
                }

                return domain is { Kind: DomainKind.CodedValue } coded && !DomainRules.Allows(coded, code, field.Type)
                    ? $"{code} is not a code of the domain '{coded.Name}'."
                    : null;

            case ContingentKind.Range:
                if (field.Type is not (FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
                    or FieldType.Single or FieldType.Double or FieldType.Date))
                {
                    return "a range is offered on a number or a date field.";
                }

                if (entry.Min is not { Number: { } min } || entry.Max is not { Number: { } max })
                {
                    return "a range needs a minimum and a maximum, both numbers.";
                }

                if (min > max)
                {
                    return $"the minimum {min} is above the maximum {max}.";
                }

                return domain is { Kind: DomainKind.Range } range
                    && (!DomainRules.Allows(range, entry.Min.Value, field.Type) || !DomainRules.Allows(range, entry.Max.Value, field.Type))
                    ? $"the range {min} to {max} is not inside the domain '{range.Name}'."
                    : null;

            default:
                return "an entry is ANY, NULL, CODED_VALUE or RANGE.";
        }
    }

    /// <summary>
    /// The definition a client is served: groups whose fields are all on the layer, and their values — ADR-063's drift
    /// answer, inert rather than broken.
    /// </summary>
    /// <param name="visible">Whether a column is on the layer as a client sees it.</param>
    /// <returns>The served definition, or this instance when nothing had to go.</returns>
    public ContingentValues Served(Func<string, bool> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);

        FieldGroup[] kept = [.. Groups.Where(g => g.Fields.All(visible))];

        if (kept.Length == Groups.Count)
        {
            return this;
        }

        return new ContingentValues(
            kept, [.. Values.Where(v => kept.Any(g => string.Equals(g.Name, v.FieldGroup, StringComparison.OrdinalIgnoreCase)))]);
    }

    /// <summary>The groups whose fields are not all on the layer — what <see cref="Served"/> leaves out.</summary>
    /// <param name="visible">Whether a column is on the layer as a client sees it.</param>
    /// <returns>The inert groups.</returns>
    public IReadOnlyList<FieldGroup> Inert(Func<string, bool> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        return [.. Groups.Where(g => !g.Fields.All(visible))];
    }

    /// <summary>The highest id held, or 0.</summary>
    public int HighestId => Values.Count == 0 ? 0 : Values.Max(v => v.Id);

    /// <summary>
    /// This definition with groups and values added — ArcGIS's <c>addToDefinition</c> (ADR-312 §5.5). A value's id is
    /// one more than the highest held, as grapy's (ADR-235 §5.8 h).
    /// </summary>
    /// <param name="groups">Groups to add; one that already exists is a refusal.</param>
    /// <param name="values">Values to add, whose ids are ignored.</param>
    /// <param name="added">The ids the values took.</param>
    /// <param name="refusal">Why not, when it returns null.</param>
    /// <returns>The new definition, or null.</returns>
    public ContingentValues? Adding(
        IReadOnlyList<FieldGroup> groups, IReadOnlyList<ContingentValue> values, out List<int> added, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(values);

        added = [];
        refusal = null;

        foreach (FieldGroup group in groups)
        {
            if (Group(group.Name) is not null)
            {
                refusal = $"The field group '{group.Name}' already exists. Nothing was applied.";
                return null;
            }
        }

        int next = HighestId;
        List<ContingentValue> all = [.. Values];

        foreach (ContingentValue value in values)
        {
            next++;
            all.Add(value with { Id = next });
            added.Add(next);
        }

        return new ContingentValues([.. Groups, .. groups], all);
    }
}
