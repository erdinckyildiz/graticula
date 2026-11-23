using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis.Pbf;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Testing;
using Xunit;
using Entry = Graticula.Testing.ContingentValuesPbfReader.Entry;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// ADR-314: a layer's contingent values as the protobuf answer an ArcGIS server was observed to write, and as the JSON
/// answer in the shape it was observed to have — and the two agree.
/// </summary>
/// <remarks>
/// Every definition here is this project's own; nothing an ArcGIS server wrote is a test vector. The protobuf is read back
/// by <see cref="ContingentValuesPbfReader"/>, a second implementation that reads the research note's field map and
/// nothing in <c>/src</c>.
/// </remarks>
public sealed class ContingentValuesAnswerTests
{
    private static readonly FieldDomain Makes = FieldDomain.Coded(
        "Makes", [new CodedValue(DomainValue.Of("BOE"), "Boeing"), new CodedValue(DomainValue.Of("AIR"), "Airbus")]);

    private static readonly FieldDomain Owners = FieldDomain.Coded(
        "Owners", [new CodedValue(DomainValue.Of("CITY"), "City"), new CodedValue(DomainValue.Of("AIR"), "Air base")]);

    private static readonly FieldDomain Sizes = FieldDomain.Range("Sizes", DomainValue.Of(0m), DomainValue.Of(10m));

    private static readonly FieldDomain Prices = FieldDomain.Coded(
        "Prices", [new CodedValue(DomainValue.Of(1.5m), "Low"), new CodedValue(DomainValue.Of(2m), "High")]);

    private static readonly FieldDomain Models = FieldDomain.Coded(
        "Models", [new CodedValue(DomainValue.Of(320m), "A320"), new CodedValue(DomainValue.Of(-7m), "Minus seven"), new CodedValue(DomainValue.Of(737m), "B737")]);

    private static readonly LayerDefinition Layer =
        new("planes", "hosted", "planes", "shape", 3857, "objectid", "objectid", isHosted: true);

    private static LayerDescription Described(LayerSubtypes? subtypes = null) =>
        new LayerDescription(
            [
                new FieldDescription("objectid", FieldType.Integer, false, null),
                new FieldDescription("make", FieldType.Text, true, 10) { Domain = Makes },
                new FieldDescription("owner", FieldType.Text, true, 10) { Domain = Owners },
                new FieldDescription("model", FieldType.Integer, true, null) { Domain = Models },
                new FieldDescription("kind", FieldType.SmallInteger, true, null),
                new FieldDescription("size", FieldType.Double, true, null) { Domain = Sizes },
                new FieldDescription("installed", FieldType.Date, true, null),
                new FieldDescription("ratio", FieldType.Single, true, null),
                new FieldDescription("price", FieldType.Double, true, null) { Domain = Prices },
                new FieldDescription("tail", FieldType.Text, true, 10),
            ],
            null)
        { Subtypes = subtypes };

    private static ContingentEntry Any(string field) => new(field, ContingentKind.Any);

    private static ContingentEntry Null(string field) => new(field, ContingentKind.Null);

    private static ContingentEntry Code(string field, string code) => new(field, ContingentKind.CodedValue, Code: DomainValue.Of(code));

    private static ContingentEntry Code(string field, decimal code) => new(field, ContingentKind.CodedValue, Code: DomainValue.Of(code));

    private static ContingentEntry Range(string field, decimal min, decimal max) =>
        new(field, ContingentKind.Range, Min: DomainValue.Of(min), Max: DomainValue.Of(max));

    private const long Jan2019 = 1_546_300_800_000;
    private const long Jan1966 = -126_230_400_000;

    /// <summary>Every observed kind of entry, and the unobserved ones ADR-314 §5.3 degrades, on a layer without subtypes.</summary>
    private static ContingentValues Definition() => new(
        [
            new FieldGroup("make_model", true, ["make", "model", "size"]),
            new FieldGroup("owner_kind", false, ["owner", "kind", "installed", "tail"]),
            new FieldGroup("unobserved", true, ["price", "ratio", "size"]),
            new FieldGroup("ranges", true, ["model", "kind"]),
        ],
        [
            new ContingentValue(1, "make_model", null, false, [Code("make", "AIR"), Code("model", 320m), Range("size", 1m, 4m)]),
            new ContingentValue(2, "make_model", null, true, [Code("make", "BOE"), Any("model"), Null("size")]),
            new ContingentValue(3, "make_model", null, false, [Code("make", "AIR"), Code("model", -7m), Range("size", 0m, 10m)]),
            new ContingentValue(4, "owner_kind", null, false, [Code("owner", "AIR"), Code("kind", 2m), Code("installed", Jan2019), Code("tail", "G-ABCD")]),
            new ContingentValue(5, "owner_kind", null, false, [Code("owner", "CITY"), Null("kind"), Range("installed", Jan1966, Jan2019), Any("tail")]),
            new ContingentValue(6, "unobserved", null, false, [Code("price", 1.5m), Range("ratio", 0.5m, 1m), Range("size", 1.5m, 4m)]),
            new ContingentValue(7, "ranges", null, false, [Range("model", -5m, 70_000m), Range("kind", 0m, 3m)]),
        ]);

    private static ContingentValuesDocument.ServedDefinition Served(
        ContingentValues? values = null, LayerSubtypes? subtypes = null, int layerId = 0) =>
        ContingentValuesDocument.Serve(Layer, layerId, "planes", "esriGeometryPoint", Described(subtypes), values ?? Definition());

    private static JsonElement JsonAnswer(
        IReadOnlyList<ContingentValuesDocument.ServedDefinition> served, ContingentValuesDocument.JsonOptions? options = null, bool oneLayer = true) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            ContingentValuesDocument.Answer(served, options ?? ContingentValuesDocument.JsonOptions.Default, oneLayer))).RootElement;

    [Fact]
    public void The_protobuf_answer_decodes_to_the_json_answers_definition_except_what_was_not_observed()
    {
        ContingentValuesDocument.ServedDefinition served = Served();

        ContingentValuesPbfReader.Layer fromPbf = ContingentValuesPbfReader.FromPbf(ContingentValuesPbf.Write([served])).Single();
        ContingentValuesPbfReader.Layer fromJson = ContingentValuesPbfReader.FromJson(JsonAnswer([served])).Single();

        Assert.Equal(fromJson.Groups.Select(g => g.Name), fromPbf.Groups.Select(g => g.Name));

        foreach ((ContingentValuesPbfReader.Group json, ContingentValuesPbfReader.Group pbf) in fromJson.Groups.Zip(fromPbf.Groups))
        {
            if (json.Name == "unobserved")
            {
                // ADR-314 §5.3 (D-484): a code on a Double field, a range on a Single field and a fractional Double range
                // go to a protobuf reader as Any; the JSON answer keeps them.
                Assert.Equal([new Entry("Any"), new Entry("Any"), new Entry("Any")], pbf.Values.Single().Entries);
                Assert.Equal(new Entry("Code", 1.5m), json.Values.Single().Entries[0]);
                Assert.Equal(new Entry("Range", Min: 1.5m, Max: 4m), json.Values.Single().Entries[2]);
                continue;
            }

            Assert.Equal(json.Values.Select(v => v.ToString()), pbf.Values.Select(v => v.ToString()));
        }

        // A spot check that the comparison compares something: value 5's date range and value 4's date code.
        ContingentValuesPbfReader.Value five = fromPbf.Groups[1].Values.Single(v => v.Id == 5);
        Assert.Equal(new Entry("Range", Min: Jan1966, Max: Jan2019), five.Entries[2]);
        Assert.Equal(new Entry("Code", (decimal)Jan2019), fromPbf.Groups[1].Values.Single(v => v.Id == 4).Entries[2]);
        Assert.True(fromPbf.Groups[0].Values.Single(v => v.Id == 2).Retired);
    }

    [Fact]
    public void The_bytes_are_the_observed_encoding()
    {
        ContingentValues one = new(
            [new FieldGroup("g", true, ["make", "model"])],
            [new ContingentValue(1, "g", null, false, [Code("make", "AIR"), Code("model", 320m)])]);

        // Answer{1: Layer{2: Definition{1: FieldGroup{1:"g", 2: Values{1: Value{1:1, 4:{10:0}, 4:{5:sint 320}}}}, 2:"AIR"}}}
        byte[] expected =
        [
            0x0a, 0x1b,                         // answer.1 layer, 27 bytes (layerID 0 is not written)
            0x12, 0x19,                         // layer.2 definition, 25 bytes
            0x0a, 0x12,                         // definition.1 field group, 18 bytes
            0x0a, 0x01, (byte)'g',              // group.1 name
            0x12, 0x0d,                         // group.2 values, 13 bytes
            0x0a, 0x0b,                         // values.1 value, 11 bytes
            0x08, 0x01,                         // value.1 id 1
            0x22, 0x02, 0x50, 0x00,             // value.4 entry: 10 text code, index 0 (a one-of member, written at 0)
            0x22, 0x03, 0x28, 0x80, 0x05,       // value.4 entry: 5 Integer code, zigzag(320) = 640
            0x12, 0x03, (byte)'A', (byte)'I', (byte)'R', // definition.2 the string table
        ];

        Assert.Equal(expected, ContingentValuesPbf.Write([Served(one)]));
    }

    [Fact]
    public void A_layer_id_a_subtype_retirement_null_and_any_are_written_as_observed()
    {
        LayerSubtypes subtypes = new("kind", 1,
        [
            new Subtype(0, "Ground", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
            new Subtype(2, "Air", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
        ]);
        ContingentValues values = new(
            [new FieldGroup("g", true, ["make", "size"])],
            [
                new ContingentValue(1, "g", 2, true, [Code("make", "AIR"), Null("size")]),
                new ContingentValue(2, "g", 0, false, [Any("make"), Range("size", 0m, 3m)]),
            ]);

        byte[] bytes = ContingentValuesPbf.Write([Served(values, subtypes, layerId: 3)]);
        IReadOnlyList<PbfReader.Field> layer = PbfReader.Read(bytes).One(1).Message;
        Assert.Equal(3UL, layer.One(1).Varint);

        List<PbfReader.Field> written = layer.One(2).Message.One(1).Message.One(2).Message.All(1);
        IReadOnlyList<PbfReader.Field> air = written.Single(v => v.Message.One(1).Varint == 1).Message;
        IReadOnlyList<PbfReader.Field> ground = written.Single(v => v.Message.One(1).Varint == 2).Message;

        Assert.Equal(4UL, air.One(2).Varint);                    // subtype 2, zigzag
        Assert.Equal(1UL, air.One(3).Varint);                    // retired
        Assert.Equal(1, air.All(4)[1].Message.One(1).Number);    // Null: member 1, an empty message
        Assert.Empty(air.All(4)[1].Message.One(1).Bytes);
        Assert.Null(ground.Find(f => f.Number is 2 or 3));       // subtype 0 and not retired: not written
        Assert.Empty(ground.All(4)[0].Bytes);                    // Any: an entry with no member
        IReadOnlyList<PbfReader.Field> range = ground.All(4)[1].Message.One(12).Message;
        Assert.Null(range.Find(f => f.Number == 1));             // a bound of 0 is not written
        Assert.Equal(6UL, range.One(2).Varint);
    }

    [Fact]
    public void A_date_code_is_an_ole_date_and_a_date_range_is_milliseconds()
    {
        ContingentValues values = new(
            [new FieldGroup("g", true, ["installed"])],
            [
                new ContingentValue(1, "g", null, false, [Code("installed", Jan2019)]),
                new ContingentValue(2, "g", null, false, [Range("installed", Jan1966, Jan2019)]),
            ]);

        List<PbfReader.Field> written = PbfReader.Read(ContingentValuesPbf.Write([Served(values)]))
            .One(1).Message.One(2).Message.One(1).Message.One(2).Message.All(1);

        Assert.Equal(43466d, written[0].Message.One(4).Message.One(8).AsDouble);
        IReadOnlyList<PbfReader.Field> range = written[1].Message.One(4).Message.One(3).Message;
        Assert.Equal(Jan1966, range.One(1).AsDouble);
        Assert.Equal(Jan2019, range.One(2).AsDouble);
    }

    [Fact]
    public void The_string_table_holds_each_text_once_in_first_use_order_and_json_keeps_one_dictionary_per_domain()
    {
        ContingentValuesDocument.ServedDefinition served = Served();
        ContingentValuesPbfReader.Layer layer = ContingentValuesPbfReader.FromPbf(ContingentValuesPbf.Write([served])).Single();

        // AIR is a code of both Makes and Owners, and is one entry; BOE's value is retired and still served.
        Assert.Equal(["AIR", "BOE", "G-ABCD", "CITY"], layer.Strings);

        JsonElement dicts = JsonAnswer([served]).GetProperty("stringDicts");
        Assert.Equal(["Makes", "Owners", "tail"], dicts.EnumerateArray().Select(d => d.GetProperty("domain").GetString()));
        Assert.Equal(["AIR", "BOE"], dicts[0].GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["AIR", "CITY"], dicts[1].GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void A_layer_with_no_group_answers_an_empty_body_and_several_layers_repeat_the_layer_message()
    {
        Assert.Empty(ContingentValuesPbf.Write([Served(ContingentValues.Empty)]));

        byte[] two = ContingentValuesPbf.Write([Served(layerId: 0), Served(ContingentValues.Empty, layerId: 1), Served(layerId: 4)]);
        Assert.Equal([0, 4], ContingentValuesPbfReader.FromPbf(two).Select(l => l.LayerId));
    }

    [Fact]
    public void The_json_answer_has_the_observed_shape()
    {
        JsonElement answer = JsonAnswer([Served()]);
        JsonElement definition = answer.GetProperty("contingentValuesDefinition");
        JsonElement group = definition.GetProperty("fieldGroups")[0];

        Assert.Equal(["Unknown", "Any", "Null", "Code", "Range"], answer.GetProperty("typeCodes").EnumerateArray().Select(t => t.GetString()));
        Assert.False(answer.TryGetProperty("domainDicts", out _));
        Assert.False(definition.GetProperty("hasSubType").GetBoolean());

        // A field's id is its position in the group; a layer without subtypes puts domains and values on the group.
        Assert.Equal([0, 1, 2], group.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("id").GetInt32()));
        Assert.False(group.TryGetProperty("subTypes", out _));
        Assert.Equal("Sizes", group.GetProperty("domains").GetProperty("size").GetString());

        // A range is one slot; a retired value is served with retired: 1, a live one without the key.
        JsonElement first = group.GetProperty("contingentValues")[0];
        Assert.Equal("[3,3,4]", first.GetProperty("types").GetRawText());
        Assert.Equal("[0,320,{\"range\":[1,4]}]", first.GetProperty("values").GetRawText());
        Assert.False(first.TryGetProperty("retired", out _));
        JsonElement retired = group.GetProperty("contingentValues")[1];
        Assert.Equal(1, retired.GetProperty("retired").GetInt32());
        Assert.Equal("[3,1,2]", retired.GetProperty("types").GetRawText());
        Assert.Equal("[1]", retired.GetProperty("values").GetRawText());
    }

    [Fact]
    public void A_layer_with_subtypes_serves_each_subtype_that_has_values_with_its_domains()
    {
        FieldDomain lateral = FieldDomain.Coded("LateralMakes", [new CodedValue(DomainValue.Of("CES"), "Cessna")]);
        LayerSubtypes subtypes = new("kind", 1,
        [
            new Subtype(1, "Main", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
            new Subtype(2, "Light", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain> { ["make"] = lateral }),
            new Subtype(3, "Unused", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
        ]);
        ContingentValues values = new(
            [new FieldGroup("g", true, ["make"])],
            [new ContingentValue(1, "g", 2, false, [Code("make", "CES")]), new ContingentValue(2, "g", 1, false, [Code("make", "AIR")])]);

        JsonElement answer = JsonAnswer([Served(values, subtypes)]);
        JsonElement kinds = answer.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[0].GetProperty("subTypes");

        Assert.Equal([1L, 2L], kinds.EnumerateArray().Select(k => k.GetProperty("code").GetInt64()));
        Assert.Equal("LateralMakes", kinds[1].GetProperty("domains").GetProperty("make").GetString());
        Assert.Equal(["Makes", "LateralMakes"], answer.GetProperty("stringDicts").EnumerateArray().Select(d => d.GetProperty("domain").GetString()));

        ContingentValuesPbfReader.Layer fromPbf = ContingentValuesPbfReader.FromPbf(ContingentValuesPbf.Write([Served(values, subtypes)])).Single();
        Assert.Equal(
            ContingentValuesPbfReader.FromJson(answer).Single().Groups[0].Values.Select(v => v.ToString()),
            fromPbf.Groups[0].Values.Select(v => v.ToString()));
    }

    [Fact]
    public void Compact_format_false_writes_names_and_retired_on_every_value()
    {
        JsonElement answer = JsonAnswer([Served()], new ContingentValuesDocument.JsonOptions(Compact: false));
        JsonElement values = answer.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[0].GetProperty("contingentValues");

        Assert.False(answer.TryGetProperty("typeCodes", out _));
        Assert.False(answer.TryGetProperty("stringDicts", out _));
        Assert.Equal("[\"Code\",\"Code\",\"Range\"]", values[0].GetProperty("types").GetRawText());
        Assert.Equal("[\"Airbus\",\"A320\",{\"range\":[1,4]}]", values[0].GetProperty("values").GetRawText());
        Assert.False(values[0].GetProperty("retired").GetBoolean());
        Assert.True(values[1].GetProperty("retired").GetBoolean());

        // A code on a field with no coded domain is written as itself (INFERRED, ADR-314 §5.8 item 6).
        JsonElement four = answer.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[1].GetProperty("contingentValues")[0];
        Assert.Equal($"[\"Air base\",2,{Jan2019},\"G-ABCD\"]", four.GetProperty("values").GetRawText());
    }

    [Theory]
    [InlineData("complete", "{\"BOE\":\"Boeing\",\"AIR\":\"Airbus\"}", "{\"320\":\"A320\",\"-7\":\"Minus seven\",\"737\":\"B737\"}", "{\"rangeMin\":0,\"rangeMax\":10}")]
    [InlineData("trimmed", "{\"BOE\":\"Boeing\",\"AIR\":\"Airbus\"}", "{\"320\":\"A320\",\"-7\":\"Minus seven\"}", "{}")]
    public void Domain_dictionaries_list_each_named_domain_in_its_own_order(string kind, string makes, string models, string sizes)
    {
        foreach (bool compact in new[] { true, false })
        {
            JsonElement dicts = JsonAnswer([Served()], new ContingentValuesDocument.JsonOptions(compact, kind)).GetProperty("domainDicts");

            Assert.Equal(makes, dicts.GetProperty("Makes").GetRawText());
            Assert.Equal(models, dicts.GetProperty("Models").GetRawText());
            Assert.Equal(sizes, dicts.GetProperty("Sizes").GetRawText());
            Assert.Equal(["Makes", "Models", "Sizes", "Owners", "Prices"], dicts.EnumerateObject().Select(p => p.Name));
        }
    }

    [Fact]
    public void The_field_groups_resource_lists_each_groups_name_restrictive_and_fields()
    {
        string written = JsonSerializer.Serialize(ContingentValuesDocument.FieldGroups(Served()));

        Assert.StartsWith(
            "{\"fieldGroups\":[{\"name\":\"make_model\",\"restrictive\":true,\"fields\":[\"make\",\"model\",\"size\"]},"
            + "{\"name\":\"owner_kind\",\"restrictive\":false,",
            written,
            StringComparison.Ordinal);
        Assert.Equal("{\"fieldGroups\":[]}", JsonSerializer.Serialize(ContingentValuesDocument.FieldGroups(Served(ContingentValues.Empty))));
    }

    [Fact]
    public void A_served_answer_can_be_sent_back_through_add_to_definition()
    {
        JsonElement answer = JsonAnswer([Served()]);
        string definition = answer.GetProperty("contingentValuesDefinition").GetRawText();
        string sent = "{\"stringDicts\":" + answer.GetProperty("stringDicts").GetRawText() + "," + definition[1..];

        using JsonDocument request = JsonDocument.Parse(sent);
        Assert.True(ContingentValuesDocument.TryRead(
            request.RootElement, Described(), ContingentValues.Empty, out List<FieldGroup> groups, out List<ContingentValue> values, out string? error), error);

        Assert.Equal(4, groups.Count);
        Assert.Equal(7, values.Count);
        Assert.Equal(Range("size", 1m, 4m), values[0].Entries[2]);
        Assert.Equal(DomainValue.Of("AIR"), values[3].Entries[0].Code);
        Assert.Equal(Code("tail", "G-ABCD"), values[3].Entries[3]);
    }
}
