using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A layer's contingent values — ADR-312: the stored shape is grapy's (§5.1), a write is checked against the layer's
/// columns (§5.3), the ArcGIS resource encodes values as ADR-314 §5.5 observed, and <c>addToDefinition</c>'s shape is
/// read back (§5.5).
/// </summary>
public sealed class ContingentValuesAreKeptAsGrapyKeepsThemTests
{
    private static readonly FieldDomain Makes = FieldDomain.Coded(
        "Makes", [new CodedValue(DomainValue.Of("BOE"), "Boeing"), new CodedValue(DomainValue.Of("AIR"), "Airbus")]);

    private static readonly FieldDomain Sizes = FieldDomain.Range("Sizes", DomainValue.Of(0m), DomainValue.Of(10m));

    private static readonly LayerDefinition Layer =
        new("planes", "hosted", "planes", "shape", 3857, "objectid", "objectid", isHosted: true);

    private static LayerDescription Described(LayerSubtypes? subtypes = null) =>
        new LayerDescription(
            [
                new FieldDescription("objectid", FieldType.Integer, false, null),
                new FieldDescription("make", FieldType.Text, true, 10) { Domain = Makes },
                new FieldDescription("model", FieldType.Integer, true, null),
                new FieldDescription("size", FieldType.Double, true, null) { Domain = Sizes },
                new FieldDescription("rating", FieldType.Text, false, 20),
                new FieldDescription("kind", FieldType.SmallInteger, true, null),
                new FieldDescription("edited_by", FieldType.Text, true, 255) { Maintained = true },
            ],
            null)
        { Subtypes = subtypes };

    private static ContingentValues Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ContingentValues? read = ContingentValueJson.Read(document.RootElement, out string? error);
        Assert.True(read is not null, error);
        return read!;
    }

    private const string Stored = """
        {"fieldGroups": [{"name": "make_model", "restrictive": true, "fields": ["make", "model", "size"]}],
         "contingentValues": [
           {"id": 1, "fieldGroup": "make_model", "subtype": null, "retired": false,
            "entries": [["make", "CODED_VALUE", "AIR"], ["model", "CODED_VALUE", 320], ["size", "RANGE", [1.5, 4]]]},
           {"id": 2, "fieldGroup": "make_model", "subtype": null, "retired": true,
            "entries": [["make", "CODED_VALUE", "BOE"], ["model", "ANY", null], ["size", "NULL", null]]}]}
        """;

    [Fact]
    public void The_stored_shape_is_grapys_and_survives_a_round_trip()
    {
        ContingentValues read = Read(Stored);

        Assert.Equal(["make", "model", "size"], read.Groups.Single().Fields);
        Assert.Equal(new ContingentEntry("size", ContingentKind.Range, Min: DomainValue.Of(1.5m), Max: DomainValue.Of(4m)), read.Values[0].Entries[2]);
        Assert.True(read.Values[1].Retired);

        // grapy's own entry words, in grapy's [field, type, value] order (ADR-235 §5.1).
        string written = ContingentValueJson.Serialize(read)!;
        Assert.Contains("[\"make\",\"CODED_VALUE\",\"AIR\"]", written, StringComparison.Ordinal);
        Assert.Contains("[\"size\",\"RANGE\",[1.5,4]]", written, StringComparison.Ordinal);
        Assert.Contains("[\"model\",\"ANY\",null]", written, StringComparison.Ordinal);

        ContingentValues again = ContingentValueJson.Read(written)!;
        Assert.Equal(written, ContingentValueJson.Serialize(again));
    }

    [Fact]
    public void An_empty_definition_is_stored_as_none()
    {
        Assert.Null(ContingentValueJson.Serialize(ContingentValues.Empty));
        Assert.Null(ContingentValueJson.Read("{\"fieldGroups\":[],\"contingentValues\":[]}"));
        Assert.Null(ContingentValueJson.Read("not json"));
    }

    [Fact]
    public void A_definition_that_fits_the_layer_is_allowed()
    {
        Assert.Null(Read(Stored).Refusal(Described().Fields, null, "objectid"));
    }

    [Theory]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["nosuch"]}]}""", "nosuch")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["objectid"]}]}""", "maintain")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["edited_by"]}]}""", "maintain")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["make"]},{"name":"G","fields":["model"]}]}""", "Two field groups")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["make","make"]}]}""", "twice")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":[]}]}""", "no fields")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["make"]}],"contingentValues":[{"id":1,"fieldGroup":"h","entries":[["make","ANY",null]]}]}""", "'h'")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["make","model"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["make","ANY",null]]}]}""", "one entry for each field")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["make"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["make","CODED_VALUE","ZZZ"]]}]}""", "Makes")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["rating"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["rating","NULL",null]]}]}""", "nulls")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["make"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["make","RANGE",[1,2]]]}]}""", "number or a date")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["size"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["size","RANGE",[5,2]]]}]}""", "above")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["size"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["size","RANGE",[5,20]]]}]}""", "Sizes")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["model"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["model","CODED_VALUE","x"]]}]}""", "")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["model"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["model","ANY",null]]},{"id":1,"fieldGroup":"g","entries":[["model","ANY",null]]}]}""", "id 1")]
    [InlineData("""{"fieldGroups":[{"name":"g","fields":["model"]}],"contingentValues":[{"id":1,"fieldGroup":"g","subtype":3,"entries":[["model","ANY",null]]}]}""", "no subtypes")]
    public void A_definition_that_does_not_fit_is_refused_by_name(string json, string named)
    {
        string? refusal = Read(json).Refusal(Described().Fields, null, "objectid");

        Assert.NotNull(refusal);
        Assert.Contains(named, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_subtyped_layer_a_value_names_a_subtype_its_domain_is_the_subtypes_and_the_subtype_column_is_not_a_member()
    {
        FieldDomain lateral = FieldDomain.Coded("LateralMakes", [new CodedValue(DomainValue.Of("CES"), "Cessna")]);
        LayerSubtypes subtypes = new("kind", 1,
        [
            new Subtype(1, "Main", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
            new Subtype(2, "Light", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain> { ["make"] = lateral }),
        ]);

        IReadOnlyList<FieldDescription> fields = Described(subtypes).Fields;

        Assert.Contains("maintain", Read("""{"fieldGroups":[{"name":"g","fields":["kind"]}]}""").Refusal(fields, subtypes, "objectid"), StringComparison.Ordinal);
        Assert.Contains("no subtype", Read("""{"fieldGroups":[{"name":"g","fields":["make"]}],"contingentValues":[{"id":1,"fieldGroup":"g","entries":[["make","ANY",null]]}]}""")
            .Refusal(fields, subtypes, "objectid"), StringComparison.Ordinal);

        // CES is a code of subtype 2's domain and not of the field's own.
        Assert.Null(Read("""{"fieldGroups":[{"name":"g","fields":["make"]}],"contingentValues":[{"id":1,"fieldGroup":"g","subtype":2,"entries":[["make","CODED_VALUE","CES"]]}]}""")
            .Refusal(fields, subtypes, "objectid"));
        Assert.Contains("Makes", Read("""{"fieldGroups":[{"name":"g","fields":["make"]}],"contingentValues":[{"id":1,"fieldGroup":"g","subtype":1,"entries":[["make","CODED_VALUE","CES"]]}]}""")
            .Refusal(fields, subtypes, "objectid"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_naming_a_column_the_layer_no_longer_shows_is_left_out_of_what_is_served()
    {
        ContingentValues held = Read("""
            {"fieldGroups":[{"name":"kept","fields":["model"]},{"name":"gone","fields":["dropped"]}],
             "contingentValues":[{"id":1,"fieldGroup":"kept","entries":[["model","ANY",null]]},
                                 {"id":2,"fieldGroup":"gone","entries":[["dropped","ANY",null]]}]}
            """);

        ContingentValues served = held.Served(c => c != "dropped");

        Assert.Equal(["kept"], served.Groups.Select(g => g.Name));
        Assert.Equal([1], served.Values.Select(v => v.Id));
        Assert.Equal(["gone"], held.Inert(c => c != "dropped").Select(g => g.Name));
        Assert.Same(held, held.Served(_ => true));
    }

    [Fact]
    public void The_resource_encodes_a_text_code_as_its_index_a_range_as_one_slot_and_serves_a_retired_value_as_retired()
    {
        // ADR-314 §5.5, as observed: the shape ADR-312 §5.4 first read off the examples is amended.
        LayerDescription described = Described();
        ContingentValuesDocument.ServedDefinition served = ContingentValuesDocument.Serve(
            Layer, 0, "planes", "esriGeometryPoint", described, Read(Stored));

        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(
            ContingentValuesDocument.Answer([served], ContingentValuesDocument.JsonOptions.Default, oneLayer: true)));
        JsonElement root = written.RootElement;
        JsonElement group = root.GetProperty("contingentValuesDefinition").GetProperty("fieldGroups")[0];

        Assert.False(root.GetProperty("contingentValuesDefinition").GetProperty("hasSubType").GetBoolean());
        Assert.Equal([0, 1, 2], group.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("id").GetInt32()));
        Assert.Equal("esriFieldTypeString", group.GetProperty("fields")[0].GetProperty("fieldType").GetString());
        Assert.False(group.TryGetProperty("subTypes", out _));
        Assert.Equal("Makes", group.GetProperty("domains").GetProperty("make").GetString());
        Assert.Equal("Sizes", group.GetProperty("domains").GetProperty("size").GetString());

        // Value 1 is AIR (index 0, the first code used), 320, and 1.5 to 4; the retired value 2 is BOE, Any and Null.
        JsonElement[] values = [.. group.GetProperty("contingentValues").EnumerateArray()];
        Assert.Equal([3, 3, 4], values[0].GetProperty("types").EnumerateArray().Select(t => t.GetInt32()));
        Assert.Equal("[0,320,{\"range\":[1.5,4]}]", values[0].GetProperty("values").GetRawText());
        Assert.Equal(1, values[1].GetProperty("retired").GetInt32());
        Assert.Equal([3, 1, 2], values[1].GetProperty("types").EnumerateArray().Select(t => t.GetInt32()));
        Assert.Equal(["AIR", "BOE"], root.GetProperty("stringDicts")[0].GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void The_subsets_keep_the_named_groups_and_ids()
    {
        Assert.True(ContingentValuesDocument.TryIds("1,3-5,-4", out Func<int, bool>? keep, out _));
        Assert.Equal([1, 3, 5], Enumerable.Range(0, 8).Where(keep!));

        Assert.True(ContingentValuesDocument.TryIds("-0", out keep, out _));
        Assert.False(keep!(0));
        Assert.True(keep(1));

        Assert.True(ContingentValuesDocument.TryIds("", out keep, out _));
        Assert.Null(keep);

        Assert.False(ContingentValuesDocument.TryIds("x", out _, out string? error));
        Assert.Contains("'x'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AddToDefinitions_shape_is_read_back_through_its_dictionaries()
    {
        LayerDescription described = Described();
        using JsonDocument request = JsonDocument.Parse("""
            {"stringDicts":[{"domain":"Makes","entries":["BOE","AIR"]}],
             "fieldGroups":[
               {"name":"model_size","contingentValues":{"fields":[{"name":"model"},{"name":"size"}]}},
               {"name":"make_model","restrictive":true,
                "subTypes":[{"code":0,"domains":{"make":"Makes"},
                             "contingentValues":[{"types":[3,3,4],"values":[1,320,2,3]},{"types":[3,1,2],"values":[0]}]}]}]}
            """);

        Assert.True(ContingentValuesDocument.TryRead(
            request.RootElement, described, Read(Stored), out List<FieldGroup> groups, out List<ContingentValue> values, out string? error), error);

        // Example five's group, its fields read from inside its contingentValues (INFERRED, ADR-312 §5.5).
        Assert.Equal(["model", "size"], groups.Single().Fields);
        Assert.True(groups.Single().Restrictive);

        Assert.Equal(2, values.Count);
        Assert.Equal(DomainValue.Of("AIR"), values[0].Entries[0].Code);
        Assert.Equal(new ContingentEntry("size", ContingentKind.Range, Min: DomainValue.Of(2m), Max: DomainValue.Of(3m)), values[0].Entries[2]);
        Assert.Equal(DomainValue.Of("BOE"), values[1].Entries[0].Code);
        Assert.Equal(ContingentKind.Null, values[1].Entries[2].Kind);

        ContingentValues? grown = Read(Stored).Adding(groups, values, out List<int> added, out string? clash);
        Assert.Null(clash);
        Assert.Equal([3, 4], added);
        Assert.Equal(2, grown!.Groups.Count);
        Assert.Null(grown.Adding([new FieldGroup("MAKE_MODEL", true, ["make"])], [], out _, out clash));
        Assert.Contains("already exists", clash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"fieldGroups":[{"name":"make_model","subTypes":[{"code":0,"contingentValues":[{"types":[3,3],"values":[1,320]}]}]}]}""", "one entry for each")]
    [InlineData("""{"fieldGroups":[{"name":"make_model","subTypes":[{"code":0,"contingentValues":[{"types":[3,3,1],"values":[7,320]}]}]}]}""", "stringDicts")]
    [InlineData("""{"fieldGroups":[{"name":"make_model","subTypes":[{"code":0,"contingentValues":[{"types":[1,1,1],"values":[1]}]}]}]}""", "more than")]
    [InlineData("""{"fieldGroups":[{"name":"make_model","subTypes":[{"code":0,"contingentValues":[{"types":[1,1,9],"values":[]}]}]}]}""", "9")]
    [InlineData("""{"fieldGroups":[{"name":"nosuch","subTypes":[{"code":0,"contingentValues":[{"types":[1],"values":[]}]}]}]}""", "nosuch")]
    [InlineData("""{"stringDicts":[]}""", "fieldGroups")]
    public void A_contingentValuesDefinition_that_cannot_be_read_is_refused_by_name(string json, string named)
    {
        using JsonDocument request = JsonDocument.Parse(json);

        Assert.False(ContingentValuesDocument.TryRead(request.RootElement, Described(), Read(Stored), out _, out _, out string? error));
        Assert.Contains(named, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_hosted_layer_that_is_not_a_view_is_written()
    {
        PublishedLayer hosted = new(Guid.NewGuid(), Layer, "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Public, ServiceStatus.Started);
        PublishedLayer registered = new(Guid.NewGuid(),
            new LayerDefinition("roads", "public", "roads", "geom", 3857, "id", "id", isHosted: false),
            "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Public, ServiceStatus.Started);

        Assert.Null(ContingentValueEndpoints.WriteRefusal(hosted));
        Assert.Contains("registered", ContingentValueEndpoints.WriteRefusal(registered), StringComparison.Ordinal);
        PublishedLayer view = new(Guid.NewGuid(), Layer, "postgis", "unused", GeometryKind.Point, owner: null, SharingScope.Public, ServiceStatus.Started)
        {
            ViewOf = Guid.NewGuid(),
        };
        Assert.Contains("view", ContingentValueEndpoints.WriteRefusal(view), StringComparison.Ordinal);
    }

    [Fact]
    public void An_addToDefinition_with_contingent_values_goes_to_their_door_and_one_with_fields_does_not()
    {
        Assert.NotNull(ArcGisAdminEndpoints.ContingentDefinition("""{"contingentValuesDefinition":{"fieldGroups":[]}}"""));
        Assert.Null(ArcGisAdminEndpoints.ContingentDefinition("""{"fields":[{"name":"a","type":"esriFieldTypeString"}]}"""));
        Assert.Null(ArcGisAdminEndpoints.ContingentDefinition("not json"));
    }
}
