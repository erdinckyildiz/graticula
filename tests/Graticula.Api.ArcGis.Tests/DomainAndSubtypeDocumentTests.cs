using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// ADR-065: the layer document carries domains and subtypes in the shapes the ArcGIS REST API's
/// reference gives them.
/// </summary>
/// <remarks>
/// <b>Written against the public reference rather than a captured document</b>: a field's
/// <c>domain</c> is a <c>codedValue</c> or <c>range</c> object; a layer names its subtype column
/// as both <c>typeIdField</c> and <c>subtypeField</c>; <c>types</c> carry per-type domains, with
/// <c>inherited</c> for a column that keeps its own, and templates whose prototype sets the subtype.
/// </remarks>
public sealed class DomainAndSubtypeDocumentTests
{
    private static LayerDefinition Layer() =>
        new(
            name: "pipes",
            schemaName: "hosted",
            tableName: "pipes",
            geometryColumn: "geom",
            srid: 3857,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: true);

    private static readonly FieldDomain Materials = FieldDomain.Coded(
        "Material", [new CodedValue(DomainValue.Of("CU"), "Copper"), new CodedValue(DomainValue.Of("PVC"), "PVC")]);

    private static LayerDescription Described(bool withSubtypes) =>
        FieldOverrides.Apply(
            new LayerDescription(
                [
                    new FieldDescription("objectid", FieldType.Integer, false, null),
                    new FieldDescription("kind", FieldType.SmallInteger, true, null),
                    new FieldDescription("material", FieldType.Text, true, 8),
                    new FieldDescription("diameter", FieldType.Integer, true, null),
                    new FieldDescription("note", FieldType.Text, true, null),
                ],
                new Envelope(0, 0, 1, 1)),
            withSubtypes
                ?
                [
                    new FieldOverride("material", null, false, Domain: Materials),
                    new FieldOverride("kind", null, false, Subtypes: new LayerSubtypes(
                        "kind",
                        1,
                        [
                            new Subtype(
                                1,
                                "Main",
                                new Dictionary<string, DomainValue> { ["material"] = DomainValue.Of("CU"), ["diameter"] = DomainValue.Of(300) },
                                new Dictionary<string, FieldDomain>
                                {
                                    ["diameter"] = FieldDomain.Range("MainDiameter", DomainValue.Of(200), DomainValue.Of(900)),
                                }),
                            new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
                        ])),
                ]
                : [new FieldOverride("material", null, false, Domain: Materials)]);

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    private static JsonElement Document(bool withSubtypes) =>
        Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.LineString, Described(withSubtypes), "Query,Create,Update,Delete"));

    [Fact]
    public void A_coded_value_domain_is_on_its_field_and_the_others_say_none()
    {
        JsonElement fields = Document(withSubtypes: false).GetProperty("fields");

        JsonElement material = fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "material");
        JsonElement domain = material.GetProperty("domain");

        Assert.Equal("codedValue", domain.GetProperty("type").GetString());
        Assert.Equal("Material", domain.GetProperty("name").GetString());
        Assert.Equal(
            ["CU:Copper", "PVC:PVC"],
            domain.GetProperty("codedValues").EnumerateArray()
                .Select(c => $"{c.GetProperty("code").GetString()}:{c.GetProperty("name").GetString()}"));

        // Present and null, as it always was: a client is told there is no domain.
        JsonElement note = fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "note");
        Assert.Equal(JsonValueKind.Null, note.GetProperty("domain").ValueKind);
    }

    [Fact]
    public void A_layer_without_subtypes_says_so_in_every_key()
    {
        JsonElement document = Document(withSubtypes: false);

        Assert.Equal(JsonValueKind.Null, document.GetProperty("typeIdField").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.GetProperty("subtypeField").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.GetProperty("defaultSubtypeCode").ValueKind);
        Assert.Equal(0, document.GetProperty("types").GetArrayLength());
        Assert.Equal(0, document.GetProperty("subtypes").GetArrayLength());
        Assert.Equal(0, document.GetProperty("templates").GetArrayLength());
    }

    [Fact]
    public void Subtypes_are_named_in_both_pairs_of_keys_from_one_set()
    {
        JsonElement document = Document(withSubtypes: true);

        Assert.Equal("kind", document.GetProperty("typeIdField").GetString());
        Assert.Equal("kind", document.GetProperty("subtypeField").GetString());
        Assert.Equal(1, document.GetProperty("defaultSubtypeCode").GetInt64());

        Assert.Equal(
            ["1:Main", "2:Lateral"],
            document.GetProperty("types").EnumerateArray()
                .Select(t => $"{t.GetProperty("id").GetInt64()}:{t.GetProperty("name").GetString()}"));

        Assert.Equal(
            ["1:Main", "2:Lateral"],
            document.GetProperty("subtypes").EnumerateArray()
                .Select(t => $"{t.GetProperty("code").GetInt64()}:{t.GetProperty("name").GetString()}"));

        // With types, templates live inside them.
        Assert.Equal(0, document.GetProperty("templates").GetArrayLength());
    }

    [Fact]
    public void A_type_gives_its_own_domains_and_inherits_the_rest()
    {
        JsonElement main = Document(withSubtypes: true).GetProperty("types")[0].GetProperty("domains");

        Assert.Equal("range", main.GetProperty("diameter").GetProperty("type").GetString());
        Assert.Equal(
            [200m, 900m],
            main.GetProperty("diameter").GetProperty("range").EnumerateArray().Select(v => v.GetDecimal()));

        // `material` has a domain of its own and Main does not replace it.
        Assert.Equal("inherited", main.GetProperty("material").GetProperty("type").GetString());

        // A column with no domain anywhere is not mentioned.
        Assert.False(main.TryGetProperty("note", out _));

        // Lateral replaces nothing: diameter varies by subtype elsewhere, so it is named as inherited.
        JsonElement lateral = Document(withSubtypes: true).GetProperty("types")[1].GetProperty("domains");
        Assert.Equal("inherited", lateral.GetProperty("diameter").GetProperty("type").GetString());
    }

    [Fact]
    public void A_template_sets_its_subtype_and_carries_its_defaults()
    {
        JsonElement template = Assert.Single(
            Document(withSubtypes: true).GetProperty("types")[0].GetProperty("templates").EnumerateArray());

        Assert.Equal("Main", template.GetProperty("name").GetString());
        Assert.Equal("esriFeatureEditToolLine", template.GetProperty("drawingTool").GetString());

        JsonElement attributes = template.GetProperty("prototype").GetProperty("attributes");

        Assert.Equal(1, attributes.GetProperty("kind").GetInt64());
        Assert.Equal("CU", attributes.GetProperty("material").GetString());
        Assert.Equal(300, attributes.GetProperty("diameter").GetInt64());
    }
}
