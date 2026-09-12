using System.Collections.Generic;
using System.Linq;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Catalog;

/// <summary>
/// ADR-065: domains and subtypes reach the description every face reads, and only as far as the
/// table today allows.
/// </summary>
public sealed class FieldOverridesDomainTests
{
    private static readonly LayerDescription Table = new(
        [
            new FieldDescription("objectid", FieldType.Integer, false, null),
            new FieldDescription("kind", FieldType.SmallInteger, true, null),
            new FieldDescription("material", FieldType.Text, true, null),
            new FieldDescription("diameter", FieldType.Integer, true, null),
            new FieldDescription("secret", FieldType.Integer, true, null),
        ],
        new Envelope(0, 0, 1, 1));

    private static readonly FieldDomain Materials = FieldDomain.Coded(
        "Material", [new CodedValue(DomainValue.Of("CU"), "Copper"), new CodedValue(DomainValue.Of("PVC"), "PVC")]);

    private static LayerSubtypes Kinds() =>
        new(
            "kind",
            2,
            [
                new Subtype(
                    1,
                    "Main",
                    new Dictionary<string, DomainValue> { ["diameter"] = DomainValue.Of(300), ["secret"] = DomainValue.Of(7) },
                    new Dictionary<string, FieldDomain>
                    {
                        ["secret"] = FieldDomain.Range("Secret", DomainValue.Of(0), DomainValue.Of(9)),
                        ["diameter"] = FieldDomain.Range("MainDiameter", DomainValue.Of(200), DomainValue.Of(900)),
                    }),
                new Subtype(2, "Lateral", new Dictionary<string, DomainValue>(), new Dictionary<string, FieldDomain>()),
            ]);

    [Fact]
    public void A_column_s_domain_rides_on_its_description()
    {
        LayerDescription seen = FieldOverrides.Apply(
            Table, [new FieldOverride("material", null, false, Domain: Materials)]);

        Assert.Same(Materials, seen.Find("material")!.Value.Domain);
        Assert.Null(seen.Find("diameter")!.Value.Domain);
    }

    [Fact]
    public void A_domain_that_no_longer_fits_its_column_is_inert()
    {
        // `material` retyped to an integer in the database after the domain was stored.
        LayerDescription retyped = Table with
        {
            Fields = [.. Table.Fields.Select(f => f.Name == "material" ? f with { Type = FieldType.Integer } : f)],
        };

        LayerDescription seen = FieldOverrides.Apply(
            retyped, [new FieldOverride("material", null, false, Domain: Materials)]);

        Assert.Null(seen.Find("material")!.Value.Domain);
    }

    [Fact]
    public void A_column_this_server_writes_takes_no_domain()
    {
        LayerDescription seen = FieldOverrides.Apply(
            Table,
            [new FieldOverride("material", null, false, EditRole.Creator, Domain: Materials)]);

        Assert.Null(seen.Find("material")!.Value.Domain);
    }

    [Fact]
    public void Subtypes_reach_the_description_narrowed_to_the_columns_a_caller_sees()
    {
        LayerDescription seen = FieldOverrides.Apply(
            Table,
            [
                new FieldOverride("kind", null, false, Subtypes: Kinds()),
                new FieldOverride("secret", null, Hidden: true),
            ]);

        LayerSubtypes subtypes = Assert.IsType<LayerSubtypes>(seen.Subtypes);

        Assert.Equal("kind", subtypes.Field);
        Assert.Equal(2, subtypes.DefaultCode);

        Subtype main = subtypes.Find(1)!;

        // The hidden column's default and domain would put its name in every template.
        Assert.False(main.Defaults.ContainsKey("secret"));
        Assert.False(main.Domains.ContainsKey("secret"));

        Assert.Equal(DomainValue.Of(300), main.Defaults["diameter"]);
        Assert.Equal("MainDiameter", main.Domains["diameter"].Name);
    }

    [Fact]
    public void A_hidden_or_retyped_subtype_column_leaves_the_layer_without_subtypes()
    {
        Assert.Null(FieldOverrides.Apply(
            Table, [new FieldOverride("kind", null, Hidden: true, Subtypes: Kinds())]).Subtypes);

        LayerDescription retyped = Table with
        {
            Fields = [.. Table.Fields.Select(f => f.Name == "kind" ? f with { Type = FieldType.Text } : f)],
        };

        Assert.Null(FieldOverrides.Apply(
            retyped, [new FieldOverride("kind", null, false, Subtypes: Kinds())]).Subtypes);

        // And a dropped one: the override names nothing, so the layer has none.
        LayerDescription dropped = Table with { Fields = [.. Table.Fields.Where(f => f.Name != "kind")] };

        Assert.Null(FieldOverrides.Apply(
            dropped, [new FieldOverride("kind", null, false, Subtypes: Kinds())]).Subtypes);
    }

    [Fact]
    public void An_override_that_only_bounds_or_only_subtypes_says_something()
    {
        Assert.True(new FieldOverride("material", null, false, Domain: Materials).SaysSomething);
        Assert.True(new FieldOverride("kind", null, false, Subtypes: Kinds()).SaysSomething);
    }
}
