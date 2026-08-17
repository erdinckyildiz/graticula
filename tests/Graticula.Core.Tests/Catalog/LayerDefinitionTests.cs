using System;
using Graticula.Catalog;
using Xunit;

namespace Graticula.Core.Tests.Catalog;

public sealed class LayerDefinitionTests
{
    private static LayerDefinition Valid(string? objectIdColumn = "objectid") => new(
        name: "Parcels",
        schemaName: "public",
        tableName: "parcels",
        geometryColumn: "geom",
        srid: 3857,
        identityColumn: "parcel_id",
        objectIdColumn: objectIdColumn,
        isHosted: false);

    [Fact]
    public void A_valid_definition_quotes_its_table()
    {
        Assert.Equal("\"public\".\"parcels\"", Valid().QuotedTable);
    }

    [Theory]
    [InlineData("parcels; drop table users --")]
    [InlineData("parcels\"")]
    [InlineData("public.parcels")]
    [InlineData("par cels")]
    [InlineData("1parcels")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_identifier_that_is_not_a_plain_name_is_refused(string tableName)
    {
        // Identifiers cannot be bound as SQL parameters, so every provider has
        // to interpolate them. Refusing anything but a plain name at the
        // boundary is what makes that interpolation provably safe rather than
        // carefully written.
        Assert.ThrowsAny<ArgumentException>(() => new LayerDefinition(
            "Parcels", "public", tableName, "geom", 3857, "id", null, false));
    }

    [Fact]
    public void An_identifier_longer_than_PostgreSQL_allows_is_refused()
    {
        // PostgreSQL truncates at 63 bytes, so a longer name would silently
        // refer to something other than what was registered.
        string tooLong = new('a', 64);

        ArgumentException error = Assert.Throws<ArgumentException>(() => new LayerDefinition(
            "Parcels", "public", tooLong, "geom", 3857, "id", null, false));

        Assert.Contains("truncate at 63", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_doubles_an_embedded_quote()
    {
        // Belt and braces: nothing valid reaches here containing a quote, but a
        // future caller who skips validation still cannot break out.
        Assert.Equal("\"we\"\"ird\"", LayerDefinition.Quote("we\"ird"));
    }

    [Fact]
    public void A_layer_without_an_integer_identity_is_not_ArcGIS_servable()
    {
        // ADR-013 §2a: OGC accepts a string id, ArcGIS FeatureServer requires a
        // unique integer. The capability report reads this rather than
        // discovering it when a request fails.
        Assert.False(Valid(objectIdColumn: null).IsArcGisServable);
        Assert.True(Valid().IsArcGisServable);
    }

    [Fact]
    public void A_non_positive_srid_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LayerDefinition(
            "Parcels", "public", "parcels", "geom", 0, "id", null, false));
    }
}
