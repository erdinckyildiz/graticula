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

        // <b>`hasZ` and `hasM` say what the column declares, since ADR-077 step 3</b>: `query` returns them to a
        // caller who asks, which is what a client reads these flags to decide to do.
        Assert.Equal((ordinates & GeometryOrdinates.Z) != 0, document.GetProperty("hasZ").GetBoolean());
        Assert.Equal((ordinates & GeometryOrdinates.M) != 0, document.GetProperty("hasM").GetBoolean());
    }

    /// <summary>
    /// A tracked layer does not advertise per-feature ownership, because nothing enforces it — ADR-075.
    /// </summary>
    /// <remarks>
    /// Kept beside the other layer-document claims rather than in a file of its own: it is the same
    /// kind of assertion — a flag a client acts on, and what the server actually does. It was first
    /// written only in the conformance run, and a local edit that never reached the disk passed every
    /// test here while CI found the object still emitted.
    /// </remarks>
    [Fact]
    public void A_tracked_layer_does_not_claim_ownership_based_access()
    {
        LayerDescription tracked =
            new([new FieldDescription("objectid", FieldType.Integer, false, null)], null)
            {
                Tracking = new EditorTracking("created_user", "created_date", "last_edited_user", "last_edited_date"),
            };

        JsonElement document = JsonDocument.Parse(JsonSerializer.Serialize(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Point, tracked, "Query,Create,Update,Delete,Editing"))).RootElement;

        Assert.Equal(
            "created_user",
            document.GetProperty("editFieldsInfo").GetProperty("creatorField").GetString());

        Assert.True(
            !document.TryGetProperty("ownershipBasedAccessControlForFeatures", out JsonElement ownership)
                || ownership.ValueKind == JsonValueKind.Null,
            $"ownershipBasedAccessControlForFeatures is advertised: {ownership}");
    }
}
