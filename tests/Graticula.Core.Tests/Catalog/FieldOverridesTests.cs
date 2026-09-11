using System;
using System.Linq;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Catalog;

/// <summary>
/// ADR-063: a layer's overrides applied to the field list its table reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism rather than a fixture.</b> These say what <see cref="FieldOverrides.Apply"/>
/// does to a description, because every serving face takes its columns from that description;
/// whether each face then refuses a hidden column is the conformance suite's, over real HTTP.
/// </para>
/// </remarks>
public sealed class FieldOverridesTests
{
    private static readonly LayerDescription Table = new(
        [
            new FieldDescription("objectid", FieldType.Integer, false, null),
            new FieldDescription("name", FieldType.Text, true, 80),
            new FieldDescription("salary", FieldType.Double, true, null),
            new FieldDescription("pop_2020", FieldType.BigInteger, true, null),
        ],
        new Envelope(0, 0, 1, 1));

    [Fact]
    public void A_layer_with_no_overrides_gets_the_same_instance_back()
    {
        // A-037: allocation is the binding constraint, and almost every layer has none.
        Assert.Same(Table, FieldOverrides.Apply(Table, []));
    }

    [Fact]
    public void A_hidden_column_is_removed_rather_than_marked()
    {
        LayerDescription seen = FieldOverrides.Apply(
            Table, [new FieldOverride("salary", null, Hidden: true)]);

        Assert.Equal(["objectid", "name", "pop_2020"], seen.Fields.Select(f => f.Name));

        // The refusal every face already writes for an unknown column comes from `Find`
        // returning null, so this is the property the whole decision rests on.
        Assert.Null(seen.Find("salary"));
    }

    [Fact]
    public void An_alias_is_a_label_and_the_name_is_unchanged()
    {
        LayerDescription seen = FieldOverrides.Apply(
            Table, [new FieldOverride("pop_2020", "Population (2020)", Hidden: false)]);

        FieldDescription field = Assert.Single(seen.Fields, f => f.Name == "pop_2020");

        Assert.Equal("Population (2020)", field.Label);

        // Every place that matches, filters, orders or writes goes on using the name — a
        // caller may not ask for a column by a label two services may spell differently.
        Assert.NotNull(seen.Find("pop_2020"));
        Assert.Null(seen.Find("Population (2020)"));
    }

    [Fact]
    public void A_column_with_no_alias_is_labelled_with_its_own_name()
    {
        Assert.Equal("name", Table.Find("name")!.Value.Label);
    }

    [Fact]
    public void An_override_naming_a_dropped_column_changes_nothing_and_is_reported()
    {
        FieldOverride orphan = new("population", "Population", Hidden: false);

        LayerDescription seen = FieldOverrides.Apply(Table, [orphan]);

        // Inert: it describes nothing, so it says nothing false — and it does not even
        // allocate a new description to say so.
        Assert.Same(Table, seen);

        // Visible — ADR-063 condition 3.
        Assert.Equal([orphan], FieldOverrides.Inert(Table, [orphan]));
    }

    [Fact]
    public void A_column_the_table_gains_appears_unlabelled_and_unhidden()
    {
        LayerDescription grown = Table with
        {
            Fields = [.. Table.Fields, new FieldDescription("added_later", FieldType.Text, true, null)],
        };

        LayerDescription seen = FieldOverrides.Apply(
            grown, [new FieldOverride("salary", null, Hidden: true)]);

        FieldDescription added = Assert.Single(seen.Fields, f => f.Name == "added_later");
        Assert.Null(added.Alias);
    }

    [Fact]
    public void An_override_matches_its_column_exactly_and_not_by_case()
    {
        // PostgreSQL permits `Name` and `name` on one table; this server quotes identifiers,
        // so they are two columns and one override must not claim both.
        LayerDescription seen = FieldOverrides.Apply(
            Table, [new FieldOverride("NAME", null, Hidden: true)]);

        Assert.NotNull(seen.Find("name"));
    }

    [Fact]
    public void An_override_that_says_nothing_says_nothing()
    {
        Assert.False(new FieldOverride("name", "  ", Hidden: false).SaysSomething);
        Assert.True(new FieldOverride("name", null, Hidden: true).SaysSomething);
        Assert.True(new FieldOverride("name", "Name", Hidden: false).SaysSomething);
    }
}
