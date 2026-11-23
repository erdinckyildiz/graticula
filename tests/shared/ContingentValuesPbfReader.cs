using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Field = Graticula.Testing.PbfReader.Field;

namespace Graticula.Testing;

/// <summary>
/// Reads the protobuf answer of <c>contingentValues?f=pbf</c> from its bytes, and the JSON answer into the same shape, so
/// a test can say the two agree — ADR-314 condition 2.
/// </summary>
/// <remarks>
/// <b>A second implementation, on purpose.</b> It reads the wire format through <see cref="PbfReader"/> and the field map
/// of <c>docs/research/contingent-values-pbf.md</c> §2, and nothing in <c>/src</c>, so a mistake the writer makes is not
/// one the reader shares.
/// </remarks>
public static class ContingentValuesPbfReader
{
    /// <summary>One entry of a value: <c>Any</c>, <c>Null</c>, <c>Code</c> or <c>Range</c>.</summary>
    /// <param name="Kind">The kind.</param>
    /// <param name="Code">A code: text, or a number (a date as milliseconds since 1970).</param>
    /// <param name="Min">A range's minimum.</param>
    /// <param name="Max">A range's maximum.</param>
    public sealed record Entry(string Kind, object? Code = null, decimal? Min = null, decimal? Max = null)
    {
        /// <inheritdoc/>
        public override string ToString() => Kind switch
        {
            "Code" => $"Code {Code}",
            "Range" => $"Range {Min}..{Max}",
            _ => Kind,
        };
    }

    /// <summary>One value.</summary>
    /// <param name="Id">Its id.</param>
    /// <param name="Subtype">Its subtype code, 0 when none.</param>
    /// <param name="Retired">Whether it is retired.</param>
    /// <param name="Entries">One entry per group field.</param>
    public sealed record Value(int Id, long Subtype, bool Retired, IReadOnlyList<Entry> Entries)
    {
        /// <inheritdoc/>
        public override string ToString() => $"{Id}/{Subtype}/{(Retired ? "retired" : "live")}: {string.Join(", ", Entries)}";
    }

    /// <summary>One group.</summary>
    /// <param name="Name">Its name.</param>
    /// <param name="Values">Its values, by id.</param>
    public sealed record Group(string Name, IReadOnlyList<Value> Values);

    /// <summary>One layer.</summary>
    /// <param name="LayerId">Its id.</param>
    /// <param name="Groups">Its groups.</param>
    /// <param name="Strings">The string table, for tests on it.</param>
    public sealed record Layer(int LayerId, IReadOnlyList<Group> Groups, IReadOnlyList<string> Strings);

    private const decimal OleEpochDays = 25569m;

    /// <summary>The layers of a protobuf answer.</summary>
    /// <param name="bytes">The answer.</param>
    /// <returns>Its layers.</returns>
    public static List<Layer> FromPbf(byte[] bytes)
    {
        List<Layer> layers = [];

        foreach (Field layer in PbfReader.Read(bytes).All(1))
        {
            IReadOnlyList<Field> fields = layer.Message;
            IReadOnlyList<Field> definition = fields.One(2).Message;
            string[] strings = [.. definition.All(2).Select(s => s.Text)];
            List<Group> groups = [];

            foreach (Field group in definition.All(1))
            {
                IReadOnlyList<Field> g = group.Message;
                List<Value> values = [];

                foreach (Field value in g.One(2).Message.All(1))
                {
                    IReadOnlyList<Field> v = value.Message;
                    Field? subtype = v.Find(f => f.Number == 2);
                    values.Add(new Value(
                        (int)v.OptionalVarint(1),
                        subtype?.SInt ?? 0,
                        v.OptionalVarint(3) == 1,
                        [.. v.All(4).Select(e => EntryOf(e.Message, strings))]));
                }

                groups.Add(new Group(g.One(1).Text, [.. values.OrderBy(x => x.Id)]));
            }

            layers.Add(new Layer((int)fields.OptionalVarint(1), groups, strings));
        }

        return layers;
    }

    private static Entry EntryOf(IReadOnlyList<Field> entry, string[] strings)
    {
        if (entry.Count == 0)
        {
            return new Entry("Any");
        }

        if (entry.Count != 1)
        {
            throw new FormatException($"An entry holds {entry.Count} members; a one-of holds one.");
        }

        Field member = entry[0];

        return member.Number switch
        {
            1 => new Entry("Null"),
            2 or 12 => new Entry("Range", Min: Bound(member.Message, 1), Max: Bound(member.Message, 2)),
            3 => new Entry("Range", Min: DoubleBound(member.Message, 1), Max: DoubleBound(member.Message, 2)),
            4 or 5 => new Entry("Code", (decimal)member.SInt),
            8 => new Entry("Code", decimal.Round(((decimal)member.AsDouble - OleEpochDays) * 86_400_000m)),
            10 => new Entry("Code", strings[(int)member.Varint]),
            _ => throw new FormatException($"Entry member {member.Number} is not in the observed map."),
        };
    }

    private static decimal Bound(IReadOnlyList<Field> range, int number) =>
        range.Find(f => f.Number == number) is { } field ? field.SInt : 0;

    private static decimal DoubleBound(IReadOnlyList<Field> range, int number) =>
        range.Find(f => f.Number == number) is { } field ? (decimal)field.AsDouble : 0;

    /// <summary>
    /// The layers of a compact JSON answer — the resource's (<c>contingentValuesDefinition</c>) or the operation's
    /// (<c>contingentValuesDefinitions</c>) — with text codes resolved through <c>stringDicts</c>.
    /// </summary>
    /// <param name="answer">The answer.</param>
    /// <returns>Its layers, with an empty string table.</returns>
    public static List<Layer> FromJson(JsonElement answer)
    {
        Dictionary<string, string[]> dictionaries = [];

        foreach (JsonElement dict in answer.GetProperty("stringDicts").EnumerateArray())
        {
            dictionaries[dict.GetProperty("domain").GetString()!] = [.. dict.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!)];
        }

        IEnumerable<JsonElement> definitions = answer.TryGetProperty("contingentValuesDefinition", out JsonElement one)
            ? [one]
            : answer.GetProperty("contingentValuesDefinitions").EnumerateArray();
        List<Layer> layers = [];

        foreach (JsonElement definition in definitions)
        {
            List<Group> groups = [];

            foreach (JsonElement group in definition.GetProperty("fieldGroups").EnumerateArray())
            {
                JsonElement[] fields = [.. group.GetProperty("fields").EnumerateArray()];
                List<Value> values = [];
                IEnumerable<(long Code, JsonElement Domains, JsonElement Held)> kinds = group.TryGetProperty("subTypes", out JsonElement subTypes)
                    ? subTypes.EnumerateArray().Select(k => (k.GetProperty("code").GetInt64(), k.GetProperty("domains"), k.GetProperty("contingentValues")))
                    : [(0L, group.GetProperty("domains"), group.GetProperty("contingentValues"))];

                foreach ((long code, JsonElement domains, JsonElement held) in kinds)
                {
                    foreach (JsonElement value in held.EnumerateArray())
                    {
                        JsonElement[] slots = [.. value.GetProperty("values").EnumerateArray()];
                        int at = 0;
                        List<Entry> entries = [];

                        for (int i = 0; i < fields.Length; i++)
                        {
                            int type = value.GetProperty("types")[i].GetInt32();
                            string name = fields[i].GetProperty("name").GetString()!;
                            bool text = fields[i].GetProperty("fieldType").GetString() == "esriFieldTypeString";

                            entries.Add(type switch
                            {
                                1 => new Entry("Any"),
                                2 => new Entry("Null"),
                                3 when text => new Entry("Code", dictionaries[domains.TryGetProperty(name, out JsonElement d) ? d.GetString()! : name][slots[at++].GetInt32()]),
                                3 => new Entry("Code", slots[at++].GetDecimal()),
                                4 => new Entry("Range",
                                    Min: slots[at].GetProperty("range")[0].GetDecimal(),
                                    Max: slots[at++].GetProperty("range")[1].GetDecimal()),
                                _ => throw new FormatException($"Type {type}."),
                            });
                        }

                        Assert(at == slots.Length, "every slot is read");
                        values.Add(new Value(
                            value.GetProperty("id").GetInt32(),
                            code,
                            value.TryGetProperty("retired", out JsonElement retired) && retired.GetInt32() == 1,
                            entries));
                    }
                }

                groups.Add(new Group(group.GetProperty("name").GetString()!, [.. values.OrderBy(v => v.Id)]));
            }

            layers.Add(new Layer(definition.GetProperty("layerID").GetInt32(), groups, []));
        }

        return layers;
    }

    private static void Assert(bool condition, string what)
    {
        if (!condition)
        {
            throw new FormatException($"The JSON answer is malformed: not {what}.");
        }
    }
}
