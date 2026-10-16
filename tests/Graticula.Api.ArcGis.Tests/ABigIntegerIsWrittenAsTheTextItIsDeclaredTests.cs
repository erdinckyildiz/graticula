using System.Collections.Generic;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// A 64-bit integer is declared a string, so applyEdits takes it as text as well as a number.
/// </summary>
/// <remarks>
/// <b>Found 2026-10-16 by ArcGIS Pro's editing engine.</b> The layer document says <c>esriFieldTypeString</c> for a
/// <c>bigint</c> column, because a JavaScript client cannot hold one past 2^53; Pro believed it and sent
/// <c>"9007199254740993"</c>, and the edit was refused for sending what the field was declared to hold.
/// </remarks>
public sealed class ABigIntegerIsWrittenAsTheTextItIsDeclaredTests
{
    private static readonly LayerDefinition Layer = new(
        name: "counts",
        schemaName: "public",
        tableName: "counts",
        geometryColumn: "geom",
        srid: 4326,
        identityColumn: "objectid",
        integerIdentityColumn: "objectid",
        isHosted: true);

    private static readonly IReadOnlyList<FieldDescription> Fields =
    [
        new FieldDescription("objectid", FieldType.Integer, false, null),
        new FieldDescription("big", FieldType.BigInteger, true, null),
    ];

    [Theory]
    [InlineData("\"9007199254740993\"", 9007199254740993L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("\"-42\"", -42L)]
    public void Text_and_number_are_both_the_integer(string sent, long expected)
    {
        ApplyEditsRequest.Parsed? parsed = ApplyEditsRequest.TryParse(
            "[{\"attributes\":{\"big\":" + sent + "},\"geometry\":{\"x\":1,\"y\":2}}]",
            null, null, rollbackOnFailure: true, Layer, Fields, out string? error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Empty(parsed.RejectedAdds);
        Assert.Equal(expected, Assert.Single(parsed.Batch.Adds).Attributes["big"]);
    }

    [Fact]
    public void Text_that_is_not_an_integer_is_refused_by_name()
    {
        ApplyEditsRequest.Parsed? parsed = ApplyEditsRequest.TryParse(
            """[{"attributes":{"big":"twelve"},"geometry":{"x":1,"y":2}}]""",
            null, null, rollbackOnFailure: false, Layer, Fields, out _);

        Assert.Contains("'big' is BigInteger", Assert.Single(parsed!.RejectedAdds).Error, System.StringComparison.Ordinal);
    }
}
