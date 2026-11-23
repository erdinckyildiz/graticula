using System;
using System.Collections.Generic;
using Graticula.Catalog;

namespace Graticula.Api.ArcGis.Pbf;

/// <summary>
/// A layer's contingent values as the protobuf answer of <c>contingentValues?f=pbf</c> and
/// <c>queryContingentValues?f=pbf</c> — ADR-314 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding is observed, not published.</b> Esri publishes no schema for this answer; every field number below was
/// read off answers ArcGIS Online wrote for public layers and paired, value by value, with the same layers' JSON
/// (owner decision of 2026-11-23, <i>"Gözleyerek türet"</i>; <c>docs/research/contingent-values-pbf.md</c> §2–3). No file
/// Esri wrote is used here.
/// </para>
/// <code>
/// Answer      1 Layer (repeated)
/// Layer       1 layerID varint · 2 Definition
/// Definition  1 FieldGroup (repeated) · 2 string table (repeated, first-use order)
/// FieldGroup  1 name · 2 Values
/// Values      1 Value (repeated)
/// Value       1 id · 2 subtype sint32 · 3 retired · 4 Entry (repeated, one per group field)
/// Entry       none = Any · 1 Null (empty) · 2 integer range {1,2 sint} · 3 date range {1,2 double ms}
///             4 SmallInteger code sint32 · 5 Integer code sint32 · 8 date code double (OLE days)
///             10 text code: string-table index · 12 Double range {1,2 sint}, whole-number bounds only observed
/// </code>
/// <para>
/// <b>Proto3 rules:</b> a field at its default is not written — a layer id, subtype code or range bound of 0, retired
/// false — except an entry's member, which is a one-of member and is written even when 0, as observed.
/// </para>
/// <para>
/// <b>What was not observed is written as <c>Any</c></b> (ADR-314 §5.3, D-484): a code on a Double, Single or other
/// field, a range on a Single or text-typed field, and a Double range with a fractional bound. An entry with no member
/// widens a client's list; a guessed field number could narrow it.
/// </para>
/// </remarks>
public static class ContingentValuesPbf
{
    /// <summary>The content type ArcGIS answers with.</summary>
    public const string ContentType = "application/x-protobuf";

    /// <summary>Days from the OLE Automation epoch (1899-12-30) to 1970-01-01.</summary>
    private const double OleEpochDays = 25569d;

    private const double MillisecondsPerDay = 86_400_000d;

    /// <summary>The answer for one or several layers. A layer with no group writes nothing, so its answer is empty.</summary>
    /// <param name="definitions">What each layer serves.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(IEnumerable<ContingentValuesDocument.ServedDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ProtoBuffer answer = new();

        foreach (ContingentValuesDocument.ServedDefinition definition in definitions)
        {
            if (definition.Groups.Count == 0)
            {
                continue;
            }

            ProtoBuffer layer = new();

            if (definition.LayerId != 0)
            {
                layer.UInt(1, (ulong)definition.LayerId);
            }

            layer.Message(2, Definition(definition));
            answer.Message(1, layer);
        }

        return answer.Span.ToArray();
    }

    private static ProtoBuffer Definition(ContingentValuesDocument.ServedDefinition definition)
    {
        ProtoBuffer written = new();
        List<string> table = [];
        Dictionary<string, int> indexes = new(StringComparer.Ordinal);

        foreach (ContingentValuesDocument.ServedGroup group in definition.Groups)
        {
            ProtoBuffer values = new();

            foreach (ContingentValuesDocument.ServedKind kind in group.Kinds)
            {
                foreach (ContingentValuesDocument.ServedValue value in kind.Values)
                {
                    values.Message(1, Value(value, group, table, indexes));
                }
            }

            ProtoBuffer fieldGroup = new();
            fieldGroup.String(1, group.Name);
            fieldGroup.Message(2, values);
            written.Message(1, fieldGroup);
        }

        // INFERRED (ADR-314 §5.8 item 2): one entry per distinct text, whichever domain it came from.
        foreach (string text in table)
        {
            written.String(2, text);
        }

        return written;
    }

    private static ProtoBuffer Value(
        ContingentValuesDocument.ServedValue value,
        ContingentValuesDocument.ServedGroup group,
        List<string> table,
        Dictionary<string, int> indexes)
    {
        ProtoBuffer written = new();
        written.UInt(1, (ulong)value.Id);

        if (value.Subtype is { } subtype && subtype != 0)
        {
            written.SInt(2, subtype);
        }

        if (value.Retired)
        {
            written.UInt(3, 1);
        }

        for (int i = 0; i < group.Fields.Count; i++)
        {
            written.Message(4, Entry(value.Entries[i], group.Fields[i], table, indexes));
        }

        return written;
    }

    private static ProtoBuffer Entry(
        ContingentEntry entry, ContingentValuesDocument.ServedField field, List<string> table, Dictionary<string, int> indexes)
    {
        ProtoBuffer written = new();

        switch (entry.Kind)
        {
            case ContingentKind.Null:
                written.Message(1, new ProtoBuffer());
                break;

            case ContingentKind.CodedValue when entry.Code is { } code:
                Code(written, code, field, table, indexes);
                break;

            case ContingentKind.Range when entry.Min is { Number: { } min } && entry.Max is { Number: { } max }:
                Range(written, min, max, field);
                break;
        }

        // Any, and anything §5.3 does not know how to write, is an entry with no member.
        return written;
    }

    private static void Code(
        ProtoBuffer written, DomainValue code, ContingentValuesDocument.ServedField field, List<string> table, Dictionary<string, int> indexes)
    {
        if (ContingentValuesDocument.IsText(field))
        {
            string text = ContingentValuesDocument.TextOf(code);

            if (!indexes.TryGetValue(text, out int index))
            {
                index = table.Count;
                table.Add(text);
                indexes[text] = index;
            }

            written.UInt(10, (ulong)index);
            return;
        }

        if (code.Number is not { } number)
        {
            return;
        }

        switch (field.FieldType)
        {
            case "esriFieldTypeSmallInteger" when IsInt32(number):
                written.SInt(4, (long)number);
                break;

            case "esriFieldTypeInteger" when IsInt32(number):
                written.SInt(5, (long)number);
                break;

            case "esriFieldTypeDate":
                written.Double(8, ((double)number / MillisecondsPerDay) + OleEpochDays);
                break;
        }
    }

    private static void Range(ProtoBuffer written, decimal min, decimal max, ContingentValuesDocument.ServedField field)
    {
        ProtoBuffer bounds = new();

        switch (field.FieldType)
        {
            case "esriFieldTypeSmallInteger" or "esriFieldTypeInteger" when IsInt32(min) && IsInt32(max):
                Whole(bounds, min, max);
                written.Message(2, bounds);
                break;

            case "esriFieldTypeDate":
                if (min != 0)
                {
                    bounds.Double(1, (double)min);
                }

                if (max != 0)
                {
                    bounds.Double(2, (double)max);
                }

                written.Message(3, bounds);
                break;

            case "esriFieldTypeDouble" when IsInt64(min) && IsInt64(max):
                Whole(bounds, min, max);
                written.Message(12, bounds);
                break;
        }
    }

    private static void Whole(ProtoBuffer bounds, decimal min, decimal max)
    {
        if (min != 0)
        {
            bounds.SInt(1, (long)min);
        }

        if (max != 0)
        {
            bounds.SInt(2, (long)max);
        }
    }

    private static bool IsInt32(decimal value) => value == decimal.Truncate(value) && value is >= int.MinValue and <= int.MaxValue;

    private static bool IsInt64(decimal value) => value == decimal.Truncate(value) && value is >= long.MinValue and <= long.MaxValue;
}
