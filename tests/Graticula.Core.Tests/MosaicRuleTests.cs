using System;
using System.Linq;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>ADR-152: which images a mosaic rule draws, and in what order — the last drawn on top.</summary>
public sealed class MosaicRuleTests
{
    // Three images in a row from west to east, ids 1, 2, 3, the third dated, positions 0, 1, 2.
    private static readonly CatalogImage[] Images =
    [
        new(1, "west", new Envelope(0, 0, 10, 10), 1, null, 0),
        new(2, "middle", new Envelope(10, 0, 20, 10), 1, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 1),
        new(3, "east", new Envelope(20, 0, 30, 10), 1, new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), 2),
    ];

    private static readonly Envelope View = new(0, 0, 30, 10);

    private static int[] Order(string json)
    {
        Assert.True(MosaicRule.TryParse(json, out MosaicRule? rule, out string? error), error);
        return [.. rule!.DrawingOrder(Images, View)];
    }

    [Fact]
    public void Without_a_rule_nothing_is_reordered_and_an_empty_rule_is_none()
    {
        Assert.True(MosaicRule.TryParse("{}", out MosaicRule? none, out _));
        Assert.Null(none);
        Assert.True(MosaicRule.TryParse(null, out none, out _));
        Assert.Null(none);
    }

    [Fact]
    public void Mt_first_puts_the_lowest_id_on_top_and_mt_last_the_highest()
    {
        // Drawing order: the last drawn is on top.
        Assert.Equal([2, 1, 0], Order("""{"mosaicMethod":"esriMosaicNone"}"""));
        Assert.Equal([0, 1, 2], Order("""{"mosaicMethod":"esriMosaicNone","mosaicOperation":"MT_LAST"}"""));
    }

    [Fact]
    public void A_lock_draws_only_what_it_names_first_named_on_top()
    {
        Assert.Equal([0, 2], Order("""{"mosaicMethod":"esriMosaicLockRaster","lockRasterIds":[3,1]}"""));
    }

    [Fact]
    public void Northwest_and_center_order_by_distance_from_the_view()
    {
        // The west image is nearest the north-west corner, so on top.
        Assert.Equal(0, Order("""{"mosaicMethod":"esriMosaicNorthwest"}""")[^1]);
        Assert.Equal(1, Order("""{"mosaicMethod":"esriMosaicCenter"}""")[^1]);
        Assert.Equal(2, Order("""{"mosaicMethod":"esriMosaicViewpoint","viewpoint":{"x":29,"y":5}}""")[^1]);
    }

    [Fact]
    public void An_attribute_order_puts_the_nearest_value_on_top()
    {
        long june = new DateTimeOffset(2024, 5, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(2, Order($$"""{"mosaicMethod":"esriMosaicAttribute","sortField":"AcquisitionDate","sortValue":"{{june}}"}""")[^1]);
        Assert.Equal(1, Order("""{"mosaicMethod":"esriMosaicAttribute","sortField":"AcquisitionDate","sortValue":"2024/01/02"}""")[^1]);
    }

    [Theory]
    [InlineData("""{"mosaicMethod":"esriMosaicSeamline"}""", "seamline")]
    [InlineData("""{"mosaicOperation":"MT_AVERAGE"}""", "MT_MEAN")]
    [InlineData("""{"mosaicMethod":"esriMosaicLockRaster"}""", "lockRasterIds")]
    [InlineData("""{"mosaicMethod":"esriMosaicAttribute","sortField":"Cloud"}""", "AcquisitionDate")]
    [InlineData("""{"multidimensionalDefinition":[{"variableName":"t","dimensionName":"depth"}]}""", "no values")]
    [InlineData("""{"multidimensionalDefinition":[{"dimensionName":"depth","values":["deep"]}]}""", "[from, to]")]
    public void What_it_does_not_order_by_is_refused_saying_what_it_does(string json, string said)
    {
        Assert.False(MosaicRule.TryParse(json, out _, out string? error));
        Assert.Contains(said, error, StringComparison.Ordinal);
    }
}

/// <summary>ADR-159: which slices of a multidimensional service a definition draws.</summary>
public sealed class DimensionSliceTests
{
    private static readonly DateTimeOffset January = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset February = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Two variables, two months, two depths: ids 1 to 8, temp first.
    private static readonly CatalogImage[] Slices =
    [
        .. new[] { "temp", "salt" }.SelectMany((v, vi) => new[] { January, February }.SelectMany((t, ti) => new[] { 0d, 100d }.Select((d, di) =>
            new CatalogImage((vi * 4) + (ti * 2) + di + 1, $"{v} {t:yyyy-MM} {d}", new Envelope(0, 0, 1, 1), 1, t,
                (vi * 4) + (ti * 2) + di, v, new System.Collections.Generic.Dictionary<string, double> { ["depth"] = d })))),
    ];

    private static int[] Kept(string definition)
    {
        Assert.True(MosaicRule.TryParse($$"""{"multidimensionalDefinition":{{definition}}}""", out MosaicRule? rule, out string? error), error);
        System.Collections.Generic.List<CatalogImage> kept = DimensionSlice.Slice(Slices, rule?.Multidimensional, out error);
        Assert.Null(error);
        return [.. kept.Select(i => i.Id)];
    }

    [Fact]
    public void Without_a_definition_the_first_variable_at_its_first_depth_every_month()
    {
        Assert.Equal([1, 3], DimensionSlice.Slice(Slices, null, out _).Select(i => i.Id));
    }

    [Fact]
    public void A_definition_chooses_a_variable_a_depth_and_a_month_and_a_range_is_inclusive()
    {
        Assert.Equal([6, 8], Kept("""[{"variableName":"salt","dimensionName":"depth","values":[100]}]"""));
        Assert.Equal([5], Kept($$"""[{"variableName":"salt","dimensionName":"StdTime","values":[{{January.ToUnixTimeMilliseconds()}}]}]"""));
        Assert.Equal([2, 4], Kept("""[{"dimensionName":"depth","values":[[50,150]]}]"""));
        Assert.Equal([2, 4], Kept("""[{"dimensionName":"StdZ","values":[100]}]"""));
        Assert.Equal([1, 3, 5, 7], Kept("""[{"variableName":"temp"},{"variableName":"salt"}]"""));
    }

    [Fact]
    public void A_variable_or_dimension_it_does_not_have_is_refused_naming_those_it_has()
    {
        Assert.True(MosaicRule.TryParse("""{"multidimensionalDefinition":[{"variableName":"wind"}]}""", out MosaicRule? rule, out _));
        DimensionSlice.Slice(Slices, rule!.Multidimensional, out string? error);
        Assert.Contains("temp, salt", error, StringComparison.Ordinal);

        Assert.True(MosaicRule.TryParse("""{"multidimensionalDefinition":[{"dimensionName":"height","values":[2]}]}""", out rule, out _));
        DimensionSlice.Slice(Slices, rule!.Multidimensional, out error);
        Assert.Contains("StdTime, depth", error, StringComparison.Ordinal);
    }
}

/// <summary>ADR-154: a classified image's table — found by value, painted in its colours.</summary>
public sealed class RasterAttributeTableTests
{
    [Fact]
    public void A_value_is_drawn_in_its_class_s_colour_and_one_without_a_class_is_transparent()
    {
        RasterAttributeTable table = new(
        [
            new AttributeClass(1, "Water", new Rgba(0, 0, 255, 255)),
            new AttributeClass(2, "Forest", new Rgba(0, 128, 0, 255)),
            new AttributeClass(1, "Again", new Rgba(9, 9, 9, 255)),
        ]);

        Rgba[] pixels = table.Paint(new CoverageWindow(4, 1, 1, [1, 2, 3, 0]), [new BandInfo(0, SampleKind.Unsigned8, 0, null, null)]);

        Assert.Equal(new Rgba(0, 0, 255, 255), pixels[0]);
        Assert.Equal(new Rgba(0, 128, 0, 255), pixels[1]);
        Assert.Equal(Rgba.Transparent, pixels[2]);
        Assert.Equal(Rgba.Transparent, pixels[3]);
        Assert.Equal("Water", table.Find(1)!.Name);
        Assert.Equal(2, table.Classes.Count);
        Assert.Equal("#00ff80", RasterAttributeTable.Hex(new Rgba(0, 255, 128, 255)));
        Assert.Equal(new Rgba(0, 255, 128, 255), RasterAttributeTable.FromHex("#00ff80"));
        Assert.Null(RasterAttributeTable.FromHex("green"));
    }
}
