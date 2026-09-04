using System;
using System.Linq;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

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
            integerIdentityColumn: objectId,
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
    public void The_service_document_reports_the_capabilities_it_is_given()
    {
        // ADR-008 §2 applied where it bites first. A client reads this string to
        // decide whether to offer editing; claiming "Create,Update,Delete"
        // because applyEdits is planned puts that button in front of a user
        // today.
        JsonElement readOnly = Json(FeatureServerMetadataWriter.Service(
            [OneLayer()], "Query"));

        Assert.Equal("Query", readOnly.GetProperty("capabilities").GetString());
        Assert.False(readOnly.GetProperty("allowGeometryUpdates").GetBoolean());

        // And when the caller may edit, the flag that gates geometry editing in
        // a client follows the capability rather than staying false.
        JsonElement editable = Json(FeatureServerMetadataWriter.Service(
            [OneLayer()], "Query,Create,Update,Delete"));

        Assert.True(editable.GetProperty("allowGeometryUpdates").GetBoolean());
    }

    [Fact]
    public void The_layer_document_does_not_claim_capabilities_the_query_endpoint_refuses()
    {
        // Each of these corresponds to a parameter FeatureServerQueryParameters
        // refuses by name. Declaring support for one is the never-degrade-
        // silently failure inverted: the client asks, and we say no after it
        // has already offered the feature.
        //
        // <b>This list used to include pagination and ordering, and that was
        // the same failure pointing the other way.</b> Both had been honoured by
        // the query endpoint since it was written, and declaring them false told
        // every client not to page — so it asked for whole layers or gave up on
        // the large ones. A capability list is only as good as its last audit
        // against the code, and this test is that audit.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query"));

        JsonElement advanced = layer.GetProperty("advancedQueryCapabilities");

        foreach (string claim in (string[])
            ["supportsSqlExpression", "supportsCountDistinct", "supportsQueryWithResultType",
             "supportsReturningGeometryCentroid"])
        {
            Assert.False(advanced.GetProperty(claim).GetBoolean(), claim);
        }

        // <b>`supportsPercentileStatistics` left this list on 2026-09-04</b>, when
        // `PERCENTILE_CONT` and `PERCENTILE_DISC` were implemented (ADR-052 §3.11). It is
        // asserted true rather than deleted, because a capability that goes from claimed-false
        // to unmentioned is one nothing audits any more — and this test is the audit.
        Assert.True(advanced.GetProperty("supportsPercentileStatistics").GetBoolean());

        // Geometry is stored without z and m, and a client uses these to decide
        // whether to offer the toggles at all.
        Assert.False(layer.GetProperty("hasZ").GetBoolean());
        Assert.False(layer.GetProperty("hasM").GetBoolean());
    }

    [Fact]
    public void The_layer_document_claims_the_paging_and_ordering_it_does_support()
    {
        // resultOffset, resultRecordCount and orderByFields are honoured, and
        // the provider orders by identity whenever an offset is given — which is
        // what makes a paginated query's order consistent across pages, as the
        // ArcGIS specification requires. Both places clients look must say so.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query"));

        JsonElement advanced = layer.GetProperty("advancedQueryCapabilities");

        foreach (string claim in (string[])
            ["supportsPagination", "supportsOrderBy", "supportsStatistics", "supportsDistinct",
             "supportsReturningQueryExtent", "supportsQueryWithDistance"])
        {
            Assert.True(advanced.GetProperty(claim).GetBoolean(), claim);
        }

        // <b>supportsHavingClause moved from the true list to here on 2026-08-16,
        // and the move is the test.</b> The flag was true while `havingClause` was
        // appended to the SQL statement unparsed, so the document advertised an
        // injection as a capability (D-41). Asserting `false` rather than deleting
        // the claim is deliberate: a client reads this to decide whether to send
        // one, and a silently absent flag is not an answer. It goes back to true
        // when the clause is parsed — Q-109.
        Assert.False(advanced.GetProperty("supportsHavingClause").GetBoolean());

        // The older flat spelling, kept because some clients still read it.
        foreach (string claim in (string[])
            ["supportsAdvancedQueries", "supportsPagination", "supportsOrderBy",
             "supportsStatistics", "supportsDistinct", "supportsReturningQueryExtent"])
        {
            Assert.True(layer.GetProperty(claim).GetBoolean(), claim);
        }
    }

    [Fact]
    public void The_object_id_field_is_declared_as_the_OID_type_not_merely_named()
    {
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query"));

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
            Layer(objectId: null), GeometryKind.Polygon, Description(), "Query"));

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
            Layer(), GeometryKind.Polygon, Description(extent: null), "Query"));

        Assert.Equal(JsonValueKind.Null, layer.GetProperty("extent").ValueKind);
    }

    [Fact]
    public void A_known_extent_carries_its_spatial_reference()
    {
        // An extent without a spatial reference is four numbers a client cannot
        // place.
        JsonElement extent = Json(FeatureServerMetadataWriter.Layer(
                Layer(), GeometryKind.Polygon, Description(new Envelope(1, 2, 3, 4)), "Query"))
            .GetProperty("extent");

        Assert.Equal(1, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(4, extent.GetProperty("ymax").GetDouble());
        Assert.Equal(3857, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void The_display_field_prefers_a_text_field_over_the_object_id()
    {
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query"));

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
            Layer(), GeometryKind.Polygon, numeric, "Query"));

        Assert.Equal("objectid", layer.GetProperty("displayField").GetString());
    }

    [Fact]
    public void The_root_catalogue_types_every_service_and_advertises_the_hosted_folder()
    {
        JsonElement catalogue = Json(FeatureServerMetadataWriter.Catalogue(
            ["a", "b"], [FeatureServerMetadataWriter.HostedFolder]));

        Assert.Equal(
            [FeatureServerMetadataWriter.HostedFolder],
            catalogue.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));

        Assert.Equal(2, catalogue.GetProperty("services").GetArrayLength());
        Assert.All(
            catalogue.GetProperty("services").EnumerateArray(),
            s => Assert.Equal("FeatureServer", s.GetProperty("type").GetString()));
    }

    [Fact]
    public void A_service_inside_a_folder_reports_its_name_with_the_folder_on_the_front()
    {
        // A client builds its request URL from this string. Returning the bare
        // name produces a catalogue whose every entry 404s.
        JsonElement catalogue = Json(FeatureServerMetadataWriter.Catalogue(
            ["roads"], [], FeatureServerMetadataWriter.HostedFolder));

        Assert.Equal(
            $"{FeatureServerMetadataWriter.HostedFolder}/roads",
            catalogue.GetProperty("services")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void A_folder_listing_advertises_no_folders_of_its_own()
    {
        // One level. Nested folders would need a path in every service
        // reference, and nothing in this product has asked for one.
        Assert.Empty(
            Json(FeatureServerMetadataWriter.Catalogue(["a"], [], "hosted"))
                .GetProperty("folders").EnumerateArray());
    }

    [Fact]
    public void A_layer_with_both_services_is_listed_twice_with_different_types()
    {
        // ArcGIS lists them as two entries of the same name. Omitting the tile
        // entry means a client browsing the catalogue never finds it.
        JsonElement services = Json(FeatureServerMetadataWriter.Catalogue(
            ["roads"], [], "hosted", ["roads"])).GetProperty("services");

        Assert.Equal(2, services.GetArrayLength());
        Assert.Equal(
            ["FeatureServer", "VectorTileServer"],
            services.EnumerateArray().Select(s => s.GetProperty("type").GetString()));

        Assert.All(
            services.EnumerateArray(),
            s => Assert.Equal("hosted/roads", s.GetProperty("name").GetString()));
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
    public void A_single_layer_service_still_numbers_its_layer_zero()
    {
        // Every route in this server ended in a literal /0 until a service
        // became a container of layers. The common case must not have moved.
        JsonElement layers = Json(FeatureServerMetadataWriter.Service(
            [OneLayer()], "Query")).GetProperty("layers");

        JsonElement only = Assert.Single(layers.EnumerateArray().ToArray());
        Assert.Equal(0, only.GetProperty("id").GetInt32());
        Assert.Equal("esriGeometryPolygon", only.GetProperty("geometryType").GetString());
    }

    [Fact]
    public void A_service_reports_every_layer_it_contains_with_its_own_geometry_type()
    {
        // Owner correction 2026-08-15: "a service is a combination of layers".
        // The screenshot that prompted it showed one service with a point, a
        // line and a polygon layer, which is the case this asserts.
        JsonElement layers = Json(FeatureServerMetadataWriter.Service(
            [
                new FeatureServerMetadataWriter.ServiceLayer(
                    0, "GeoPoint", GeometryKind.Point, 3857, null),
                new FeatureServerMetadataWriter.ServiceLayer(
                    1, "GeoLine", GeometryKind.LineString, 3857, null),
                new FeatureServerMetadataWriter.ServiceLayer(
                    2, "GeoFence", GeometryKind.Polygon, 3857, null),
            ],
            "Query")).GetProperty("layers");

        Assert.Equal(3, layers.GetArrayLength());
        Assert.Equal("GeoLine", layers[1].GetProperty("name").GetString());
        Assert.Equal(2, layers[2].GetProperty("id").GetInt32());

        Assert.Equal(
            ["esriGeometryPoint", "esriGeometryPolyline", "esriGeometryPolygon"],
            layers.EnumerateArray().Select(l => l.GetProperty("geometryType").GetString()));
    }

    [Fact]
    public void The_service_extent_is_the_union_of_its_layers()
    {
        // A client zooms to this. A service whose extent covered only its first
        // layer would open on a map with two of its three layers off-screen.
        JsonElement extent = Json(FeatureServerMetadataWriter.Service(
            [
                new FeatureServerMetadataWriter.ServiceLayer(
                    0, "a", GeometryKind.Point, 3857, new Envelope(0, 0, 10, 10)),
                new FeatureServerMetadataWriter.ServiceLayer(
                    1, "b", GeometryKind.Point, 3857, new Envelope(-5, 20, 3, 40)),
            ],
            "Query")).GetProperty("fullExtent");

        Assert.Equal(-5, extent.GetProperty("xmin").GetDouble());
        Assert.Equal(0, extent.GetProperty("ymin").GetDouble());
        Assert.Equal(10, extent.GetProperty("xmax").GetDouble());
        Assert.Equal(40, extent.GetProperty("ymax").GetDouble());
    }

    [Fact]
    public void An_empty_service_is_still_a_valid_document()
    {
        // A service exists between being created and having layers added, and
        // an administrator will open it in that window.
        JsonElement document = Json(FeatureServerMetadataWriter.Service([], "Query"));

        Assert.Empty(document.GetProperty("layers").EnumerateArray());
        Assert.Equal(10.81, document.GetProperty("currentVersion").GetDouble(), 2);
    }

    private static FeatureServerMetadataWriter.ServiceLayer OneLayer() =>
        new(0, Layer().Name, GeometryKind.Polygon, Layer().Srid, null);

    [Fact]
    public void The_advertised_maximum_matches_the_limit_the_query_endpoint_enforces()
    {
        // A client that respects this never triggers our own clamp. If they
        // drift, a well-behaved client silently gets fewer features than it
        // asked for and no indication why.
        JsonElement layer = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query"));

        Assert.Equal(FeatureQuery.MaximumLimit, layer.GetProperty("maxRecordCount").GetInt32());
    }

    /// <summary>
    /// The advertised maximum is the smallest of the client's three constraints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A client sizes its paging from `maxRecordCount`, so it is a promise rather than a label.</b>
    /// The class already says why for a service ceiling: *reporting the server's figure while enforcing a
    /// lower one is a lie a client acts on.* On 2026-08-19 the server's figure became a setting — the
    /// owner asked for a deployment-wide rule, since a per-service one leaves every new service at the
    /// model's 50,000 — and that made the compile-time constant stop being the truth.
    /// </para>
    /// <para>
    /// <b>So three numbers narrow it and the smallest wins:</b> the model's own bound, the deployment's
    /// setting, and the service's ceiling. Each may only lower.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null, 50_000, 50_000)]     // nothing set anywhere
    [InlineData(null, 2_000, 2_000)]       // the deployment lowered it
    [InlineData(500, 2_000, 500)]          // a service lowered it further
    [InlineData(9_000, 2_000, 2_000)]      // a service asked for more than the deployment allows
    [InlineData(null, 5_000_000, 50_000)]  // a deployment asked for more than the model allows
    public void The_advertised_maximum_is_the_smallest_constraint(
        int? serviceCeiling, int serverCeiling, int expected)
    {
        Assert.Equal(
            expected,
            FeatureServerMetadataWriter.AdvertisedMaxRecordCount(serviceCeiling, serverCeiling));
    }

    /// <summary>
    /// A document that could not ask about relationships says so, rather than reporting none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>[ADR-026](../../docs/adr/ADR-026-serving-through-a-platform-store-outage.md)
    /// condition 3.</b> Relationships live only in the platform store; while it is unreachable
    /// there is nothing to report them from, and an empty array is the same answer a layer with
    /// no relationships gives. The ADR's own consequences called that *thinner than it should
    /// be* — the only distinguishing signal was a response header, which is a fact about the
    /// response rather than about the layer, and is gone the moment somebody saves the document.
    /// </para>
    /// <para>
    /// <b><c>relationships</c> stays an empty array on purpose</b>, and this asserts that too.
    /// ArcGIS has no vocabulary for *unknown* in this field, so null or absent would be read as
    /// none by every client while breaking the ones that read its length. The field a client
    /// reads is left alone and the fields that say what it means are added beside it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_layer_document_served_blind_says_its_relationships_are_unknown()
    {
        JsonElement blind = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query", relationshipsKnown: false));

        Assert.False(blind.GetProperty("relationshipsKnown").GetBoolean());
        Assert.True(blind.GetProperty("catalogStale").GetBoolean());

        Assert.Equal(JsonValueKind.Array, blind.GetProperty("relationships").ValueKind);
        Assert.Empty(blind.GetProperty("relationships").EnumerateArray());
    }

    /// <summary>
    /// An ordinary document gains nothing from the field that says a blind one is blind.
    /// </summary>
    /// <remarks>
    /// <b>The half that keeps the repair from being a regression.</b> Two fields added to every
    /// layer document would be two more things every client parses and every reader has to
    /// discount, and <c>catalogStale: false</c> on a document served from a healthy store says
    /// nothing at all. So the ordinary document is asserted to be exactly what it was: the same
    /// keys, in the same shape, with neither field present.
    /// </remarks>
    [Fact]
    public void An_ordinary_layer_document_carries_neither_field()
    {
        JsonElement ordinary = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query"));

        Assert.False(ordinary.TryGetProperty("relationshipsKnown", out _));
        Assert.False(ordinary.TryGetProperty("catalogStale", out _));

        // And the two documents are otherwise the same document, which is what makes the two
        // extra keys the whole difference rather than the visible part of a larger one.
        JsonElement blind = Json(FeatureServerMetadataWriter.Layer(
            Layer(), GeometryKind.Polygon, Description(), "Query", relationshipsKnown: false));

        string[] ordinaryKeys = ordinary.EnumerateObject().Select(p => p.Name).ToArray();
        string[] blindKeys = blind.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            ordinaryKeys.OrderBy(k => k, StringComparer.Ordinal),
            blindKeys.Except(["relationshipsKnown", "catalogStale"], StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal));

        foreach (string key in ordinaryKeys)
        {
            Assert.Equal(
                ordinary.GetProperty(key).GetRawText(),
                blind.GetProperty(key).GetRawText());
        }
    }
}
