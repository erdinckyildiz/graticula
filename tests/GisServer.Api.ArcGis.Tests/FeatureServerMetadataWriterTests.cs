using System;
using System.Linq;
using System.Text.Json;
using GisServer.Api.ArcGis;
using GisServer.Catalog;
using GisServer.Features;
using GisServer.Geometries;
using Xunit;

namespace GisServer.Api.ArcGis.Tests;

/// <summary>
/// The documents an ArcGIS client reads before it asks anything.
/// </summary>
/// <remarks>
/// These are claims made to a client that acts on them: a capability string
/// decides whether an edit button appears, and a <c>supportsStatistics</c> of
/// true produces a panel that returns an error. Every assertion below is about
/// not over-claiming.
/// </remarks>
public sealed class FeatureServerMetadataWriterTests
{
    private static LayerDefinition Layer(string? objectId = "objectid") =>
        new(
            name: "buildings",
            schemaName: "public",
            tableName: "osm_buildings",
            geometryColumn: "way",
            srid: 3857,
            identityColumn: "objectid",
            objectIdColumn: objectId,
            isHosted: false);

    private static LayerDescription Description(Envelope? extent = null) =>
        new(
            [
                new FieldDescription("objectid", FieldType.Integer, false, null),
                new FieldDescription("osm_id", FieldType.BigInteger, true, null),
                new FieldDescription("name", FieldType.Text, true, 255),
            ],
            extent);

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public void The_service_document_claims_query_and_nothing_else()
    {
        // ADR-008 §2 applied where it bites first. A client reads this string to
        // decide whether to offer editing; claiming "Create,Update,Delete"
        // because applyEdits is planned puts that button in front of a user
        // today.
        JsonElement service = Json(FeatureServerMetadataWriter.Service(
            Layer(), GeometryKind.Polygon, null));

        Assert.Equal("Query", service.GetProperty("capabilities").GetString());
        Assert.False(service.GetProperty("allowGeometryUpdates").GetBoolean());
    }

    [Fact]
    public void The_layer_document_does_not_claim_capabilities_the_query_endpoint_refuses()
    {
        // Each of these corresponds to a parameter FeatureServerQueryParameters
        // refuses by name. Declaring support for one is the never-degrade-
        // silently failure inverted: the client asks, and we say no after it
        // has already offered the feature.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description()));

        foreach (string claim in (string[])
            ["supportsAdvancedQueries", "supportsStatistics", "supportsPagination",
             "supportsOrderBy", "supportsDistinct", "supportsReturningQueryExtent"])
        {
            Assert.False(layer.GetProperty(claim).GetBoolean(), claim);
        }
    }

    [Fact]
    public void The_object_id_field_is_declared_as_the_OID_type_not_merely_named()
    {
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description()));

        Assert.Equal("objectid", layer.GetProperty("objectIdField").GetString());

        JsonElement field = layer.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "objectid");

        Assert.Equal("esriFieldTypeOID", field.GetProperty("type").GetString());
    }

    [Fact]
    public void A_layer_with_no_integer_identity_reports_a_null_object_id_field()
    {
        // ADR-013 §2a. The query endpoint refuses such a layer; a client reading
        // this document learns why before it tries, rather than after.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(objectId: null), GeometryKind.Polygon, Description()));

        Assert.Equal(JsonValueKind.Null, layer.GetProperty("objectIdField").ValueKind);
    }

    [Fact]
    public void A_sixty_four_bit_integer_is_declared_as_text_matching_how_it_is_written()
    {
        // The declared type and the emitted value must agree. The attribute
        // writer sends bigint as a string because JavaScript loses precision
        // above 2^53; declaring it as an integer here would make the client
        // parse it back and lose exactly what the string was protecting.
        Assert.Equal(
            "esriFieldTypeString",
            FeatureServerMetadataWriter.TypeName(FieldType.BigInteger));
    }

    [Fact]
    public void A_boolean_is_declared_as_the_small_integer_it_is_written_as()
    {
        Assert.Equal(
            "esriFieldTypeSmallInteger",
            FeatureServerMetadataWriter.TypeName(FieldType.Boolean));
    }

    [Fact]
    public void An_unknown_field_type_is_declared_as_text_rather_than_omitted()
    {
        // Omitting it would hide a column that queries can still return, and the
        // client would then receive an attribute it has no field for.
        Assert.Equal(
            "esriFieldTypeString",
            FeatureServerMetadataWriter.TypeName(FieldType.Unknown));
    }

    [Fact]
    public void An_unknown_extent_is_null_rather_than_a_box_at_the_origin()
    {
        // Null means unknown; a zeroed box means "the features are off the coast
        // of Africa", which is the classic symptom of confusing the two.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(extent: null)));

        Assert.Equal(JsonValueKind.Null, layer.GetProperty("extent").ValueKind);
    }

    [Fact]
    public void A_known_extent_carries_its_spatial_reference()
    {
        // An extent without a spatial reference is four numbers a client cannot
        // place.
        JsonElement extent = Json(FeatureServerMetadataWriter.Layer(
                Layer(), GeometryKind.Polygon, Description(new Envelope(1, 2, 3, 4))))
            .GetProperty("extent");

        Assert.Equal(1, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(4, extent.GetProperty("ymax").GetDouble());
        Assert.Equal(3857, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void The_display_field_prefers_a_text_field_over_the_object_id()
    {
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description()));

        Assert.Equal("name", layer.GetProperty("displayField").GetString());
    }

    [Fact]
    public void A_layer_with_no_text_field_falls_back_rather_than_leaving_it_blank()
    {
        // Some clients render an empty display field as a blank callout on every
        // feature, which looks like a data problem rather than a metadata one.
        LayerDescription numeric = new(
            [new FieldDescription("objectid", FieldType.Integer, false, null)], null);

        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, numeric));

        Assert.Equal("objectid", layer.GetProperty("displayField").GetString());
    }

    [Fact]
    public void The_catalogue_is_flat_and_types_every_service()
    {
        JsonElement catalogue = Json(FeatureServerMetadataWriter.Catalogue(["a", "b"]));

        Assert.Empty(catalogue.GetProperty("folders").EnumerateArray());
        Assert.Equal(2, catalogue.GetProperty("services").GetArrayLength());
        Assert.All(
            catalogue.GetProperty("services").EnumerateArray(),
            s => Assert.Equal("FeatureServer", s.GetProperty("type").GetString()));
    }

    [Fact]
    public void Server_info_declares_token_security_and_where_to_get_one()
    {
        // A client that cannot find the token URL assumes anonymous and fails
        // later, less clearly.
        JsonElement info = Json(FeatureServerMetadataWriter.ServerInfo("https://example/login"));

        Assert.True(info.GetProperty("authInfo").GetProperty("isTokenBasedSecurity").GetBoolean());
        Assert.Equal(
            "https://example/login",
            info.GetProperty("authInfo").GetProperty("tokenServicesUrl").GetString());
    }

    [Fact]
    public void The_service_reports_exactly_one_layer_at_id_zero()
    {
        // ADR-013's model is one service per published layer, which is why every
        // query route in this server ends in /0. If this ever reports more, the
        // routes are wrong too.
        JsonElement layers = Json(FeatureServerMetadataWriter.Service(
            Layer(), GeometryKind.Polygon, null)).GetProperty("layers");

        JsonElement only = Assert.Single(layers.EnumerateArray().ToArray());
        Assert.Equal(0, only.GetProperty("id").GetInt32());
        Assert.Equal("esriGeometryPolygon", only.GetProperty("geometryType").GetString());
    }

    [Fact]
    public void The_advertised_maximum_matches_the_limit_the_query_endpoint_enforces()
    {
        // A client that respects this never triggers our own clamp. If they
        // drift, a well-behaved client silently gets fewer features than it
        // asked for and no indication why.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description()));

        Assert.Equal(FeatureQuery.MaximumLimit, layer.GetProperty("maxRecordCount").GetInt32());
    }
}
