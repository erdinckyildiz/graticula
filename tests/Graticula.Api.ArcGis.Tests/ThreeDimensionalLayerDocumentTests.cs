using System;
using System.Text.Json;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// A layer whose column carries Z or M does not offer geometry editing — ADR-074 §4.
/// </summary>
/// <remarks>
/// <c>PostGisFeatureWriter</c> refuses every geometry update to a feature whose stored geometry
/// carries an ordinate this server does not read (ADR-008 §4.5a), and until 2026-09-16 the layer
/// document computed <c>allowGeometryUpdates</c> from the capability string alone — so ArcGIS Pro
/// put an edit tool in front of somebody whose every save came back refused. That is the
/// over-claim ADR-008 §2 exists to refuse, one layer narrower than the capability string can say.
/// </remarks>
public sealed class ThreeDimensionalLayerDocumentTests
{
    private static LayerDefinition Layer() =>
        new(
            name: "contours",
            schemaName: "public",
            tableName: "contours",
            geometryColumn: "geom",
            srid: 3857,
            identityColumn: "objectid",
            integerIdentityColumn: "objectid",
            isHosted: false);

    private static LayerDescription Description(GeometryOrdinates ordinates) =>
        new([new FieldDescription("objectid", FieldType.Integer, false, null)], null)
        {
            StoredOrdinates = ordinates,
        };

    private static JsonElement Document(GeometryOrdinates ordinates) =>
        JsonDocument.Parse(JsonSerializer.Serialize(FeatureServerMetadataWriter.Layer(
            Layer(),
            GeometryKind.LineString,
            Description(ordinates),
            "Query,Create,Update,Delete,Editing"))).RootElement;

    [Fact]
    public void A_flat_layer_offers_geometry_editing()
    {
        JsonElement flat = Document(GeometryOrdinates.None);

        Assert.True(flat.GetProperty("allowGeometryUpdates").GetBoolean());
        Assert.Contains("Update", flat.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GeometryOrdinates.Z)]
    [InlineData(GeometryOrdinates.M)]
    [InlineData(GeometryOrdinates.Z | GeometryOrdinates.M)]
    public void A_layer_whose_column_carries_more_than_x_and_y_does_not(GeometryOrdinates ordinates)
    {
        JsonElement document = Document(ordinates);

        Assert.False(document.GetProperty("allowGeometryUpdates").GetBoolean());

        // <b>And the capability string is untouched.</b> Attribute editing works on these
        // layers, so removing `Update` would understate by as much as the old answer overstated.
        Assert.Contains("Update", document.GetProperty("capabilities").GetString()!, StringComparison.Ordinal);

        // <b>`hasZ` stays false, which is the other half of the honest answer.</b> The column
        // carries an elevation; `query` returns x and y, and this flag describes the answer.
        Assert.False(document.GetProperty("hasZ").GetBoolean());
        Assert.False(document.GetProperty("hasM").GetBoolean());
    }
}
