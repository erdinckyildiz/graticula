using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>What a layer's geometry type is said to be, from Oracle's own codes — no database needed.</summary>
public sealed class GeometryTypeTests
{
    [Theory]
    [InlineData(new[] { 1 }, "Point")]
    [InlineData(new[] { 1, 5 }, "MultiPoint")]
    [InlineData(new[] { 2 }, "LineString")]
    [InlineData(new[] { 6, 2 }, "MultiLineString")]
    [InlineData(new[] { 3 }, "Polygon")]
    [InlineData(new[] { 3, 7 }, "MultiPolygon")]
    [InlineData(new[] { 1, 3 }, null)]
    [InlineData(new[] { 4 }, null)]
    [InlineData(new int[0], null)]
    public void The_rows_agree_on_a_type_or_none_is_claimed(int[] kinds, string? expected) =>
        Assert.Equal(expected, OracleDataSourceProbe.TypeOfGtypes(kinds));

    [Theory]
    [InlineData("POINT", "Point")]
    [InlineData("multipolygon", "MultiPolygon")]
    [InlineData("COLLECTION", null)]
    [InlineData(null, null)]
    public void A_spatial_index_s_layer_type_is_read_and_collection_says_nothing(string? gtype, string? expected) =>
        Assert.Equal(expected, OracleDataSourceProbe.GeometryTypeOf(gtype));
}
