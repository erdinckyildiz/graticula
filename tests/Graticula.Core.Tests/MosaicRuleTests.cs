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
    [InlineData("""{"mosaicOperation":"MT_MEAN"}""", "MT_FIRST")]
    [InlineData("""{"mosaicMethod":"esriMosaicLockRaster"}""", "lockRasterIds")]
    [InlineData("""{"mosaicMethod":"esriMosaicAttribute","sortField":"Cloud"}""", "AcquisitionDate")]
    [InlineData("""{"multidimensionalDefinition":[{"variableName":"t"}]}""", "multidimensional")]
    public void What_it_does_not_order_by_is_refused_saying_what_it_does(string json, string said)
    {
        Assert.False(MosaicRule.TryParse(json, out _, out string? error));
        Assert.Contains(said, error, StringComparison.Ordinal);
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
