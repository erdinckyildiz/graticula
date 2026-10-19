using System;
using Graticula.Features;
using Xunit;

namespace Graticula.Providers.Oracle.Tests;

/// <summary>Oracle column types and values as the faces see them — no database needed.</summary>
public sealed class OracleValueTests
{
    [Theory]
    [InlineData("NUMBER", 5, 0, FieldType.SmallInteger)]
    [InlineData("NUMBER", 10, 0, FieldType.Integer)]
    [InlineData("NUMBER", 19, 0, FieldType.BigInteger)]
    [InlineData("NUMBER", 38, 0, FieldType.BigInteger)]
    [InlineData("NUMBER", null, null, FieldType.Double)]
    [InlineData("NUMBER", 12, 3, FieldType.Double)]
    [InlineData("BINARY_FLOAT", null, null, FieldType.Single)]
    [InlineData("NVARCHAR2", null, null, FieldType.Text)]
    [InlineData("DATE", null, null, FieldType.Date)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", null, null, FieldType.Date)]
    [InlineData("SDO_GEOMETRY", null, null, FieldType.Unknown)]
    public void A_column_type_is_mapped_as_arcgis_maps_it(string type, int? precision, int? scale, FieldType expected) =>
        Assert.Equal(expected, OracleFeatureSource.TypeOf(type, precision, scale));

    [Fact]
    public void A_number_becomes_the_clr_type_its_field_promises_and_is_widened_rather_than_refused()
    {
        Assert.IsType<short>(OracleFeatureSource.Normalise(12m, FieldType.SmallInteger));
        Assert.IsType<int>(OracleFeatureSource.Normalise(1312m, FieldType.Integer));
        Assert.Equal(9_999_999_999L, OracleFeatureSource.Normalise(9_999_999_999m, FieldType.Integer));
        Assert.Equal(1312L, OracleFeatureSource.Normalise(1312m, FieldType.BigInteger));
        Assert.Equal(1312L, OracleFeatureSource.Normalise(1312d, FieldType.BigInteger));
        Assert.Equal(7.5d, OracleFeatureSource.Normalise(7.5m, FieldType.Double));
        Assert.Null(OracleFeatureSource.Normalise(DBNull.Value, FieldType.Text));
    }
}
