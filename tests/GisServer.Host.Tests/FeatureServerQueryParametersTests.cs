using System;
using System.Collections.Generic;
using GisServer.Features;
using GisServer.Geometries;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GisServer.Host.Tests;

/// <summary>
/// What the query endpoint accepts, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The refusals carry as much weight as the acceptances. ADR-008 §2's principle
/// is that we never degrade silently, and a parameter list is exactly where that
/// decays: ignoring one unknown parameter is a one-line change that turns a
/// wrong answer into the normal case.
/// </para>
/// <para>
/// <b>The acceptances now carry weight too.</b> The first version of this class
/// refused four parameters that every ArcGIS client sends, which made the
/// surface defensible on paper and unusable in practice.
/// </para>
/// </remarks>
public sealed class FeatureServerQueryParametersTests
{
    private const int Srid = 3857;

    private static readonly FieldDescription[] Fields =
    [
        new("objectid", FieldType.Integer, false, null),
        new("name", FieldType.Text, true, 255),
        new("height", FieldType.Double, true, null),
    ];

    private static QueryCollection Query(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values = [];

        foreach ((string key, string value) in pairs)
        {
            values[key] = value;
        }

        return new QueryCollection(values);
    }

    private static FeatureQuery Parse(params (string Key, string Value)[] pairs)
    {
        Assert.True(
            FeatureServerQueryParameters.TryParse(
                Query(pairs), "objectid", Srid, Fields,
                out FeatureQuery? query, out _, out string? error),
            error);

        return query!;
    }

    private static string Refuse(params (string Key, string Value)[] pairs)
    {
        Assert.False(FeatureServerQueryParameters.TryParse(
            Query(pairs), "objectid", Srid, Fields, out _, out _, out string? error));

        Assert.False(string.IsNullOrWhiteSpace(error), "A refusal must say why.");
        return error!;
    }

    // ---------- refusals ----------

    [Theory]
    [InlineData("time")]
    [InlineData("fullText")]
    [InlineData("uniqueIds")]
    [InlineData("returnUniqueIdsOnly")]
    public void A_parameter_that_changes_the_answer_is_refused_rather_than_ignored(string parameter)
    {
        // <b>What is left, and each is absent for a reason that is not
        // effort.</b> outStatistics, returnIdsOnly, objectIds and havingClause
        // were all on this list until 2026-08-15 and are now implemented; these
        // four need something the product does not have — a time-aware layer, a
        // tsvector index, a version tree.
        Assert.Contains(parameter, Refuse((parameter, "anything")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_says_what_is_missing_rather_than_that_it_is_unsupported()
    {
        // "Not supported" sends somebody to file an issue. Naming the thing that
        // is absent answers the question in the message.
        Assert.Contains(
            "timeInfo", Refuse(("time", "1,2")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_where_clause_is_parsed_rather_than_refused()
    {
        // <b>Refused with "ADR-008's query AST does not exist yet" until
        // 2026-08-15.</b> It exists now — WhereClause — and it is what makes the
        // most-used parameter of the query API usable without handing user text
        // to the database.
        FeatureQuery query = Parse(("where", "name = 'x'"));

        Assert.NotNull(query.Where);
        Assert.Equal("\"name\" = @w0", query.Where!.Value.Sql);
        Assert.Equal("x", Assert.Single(query.Where.Value.Parameters));
    }

    [Fact]
    public void A_where_clause_that_is_not_in_the_grammar_is_still_refused()
    {
        // The parser is the boundary, and the boundary has to hold: anything it
        // cannot rebuild is refused rather than forwarded.
        Assert.Contains(
            "could not be parsed",
            Refuse(("where", "1=1; drop table x")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("esriSpatialRelIntersects")]
    [InlineData("esriSpatialRelContains")]
    [InlineData("esriSpatialRelCrosses")]
    [InlineData("esriSpatialRelEnvelopeIntersects")]
    [InlineData("esriSpatialRelIndexIntersects")]
    [InlineData("esriSpatialRelOverlaps")]
    [InlineData("esriSpatialRelTouches")]
    [InlineData("esriSpatialRelWithin")]
    public void Every_spatial_relationship_but_relate_is_understood(string relation)
    {
        // All nine are implemented; relate is the one that also needs a pattern
        // and has its own test.
        Parse(("geometry", "0,0,1,1"), ("spatialRel", relation));
    }

    [Fact]
    public void An_invented_spatial_relationship_is_refused()
    {
        Assert.Contains(
            "nine",
            Refuse(("geometry", "0,0,1,1"), ("spatialRel", "esriSpatialRelSortOfNear")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Relate_needs_a_pattern_and_a_pattern_needs_relate()
    {
        // Each without the other is a request that cannot be honoured as
        // written, and accepting either would let a caller believe a relation
        // was applied that was not.
        Assert.Contains(
            "relationParam",
            Refuse(("geometry", "0,0,1,1"), ("spatialRel", "esriSpatialRelRelation")),
            StringComparison.Ordinal);

        Assert.Contains(
            "esriSpatialRelRelation",
            Refuse(("geometry", "0,0,1,1"), ("relationParam", "T********")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_relate_pattern_must_be_nine_characters_of_the_right_alphabet()
    {
        Assert.Contains(
            "nine characters",
            Refuse(
                ("geometry", "0,0,1,1"),
                ("spatialRel", "esriSpatialRelRelation"),
                ("relationParam", "nonsense")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Esri_spells_web_mercator_102100_and_that_is_the_same_system()
    {
        // Not a typo for 3857 — it is Esri's own code for Web Mercator Auxiliary
        // Sphere, and every ArcGIS client sends it. Comparing the numbers alone
        // refused the SDK on every request against a 3857 layer, which is
        // exactly what happened the first time one was pointed at this.
        Parse(("outSR", "102100"));
        Parse(("outSR", "102113"));
    }

    [Fact]
    public void An_order_by_is_parsed_with_its_direction()
    {
        Assert.Equal(
            [new GisServer.Features.SortKey("name", false), new GisServer.Features.SortKey("height", true)],
            Parse(("orderByFields", "name, height DESC")).OrderBy);
    }

    [Fact]
    public void Ordering_by_a_column_that_does_not_exist_is_refused()
    {
        // The one place a client-supplied identifier reaches an ORDER BY, and an
        // identifier cannot be a parameter — so the whitelist is the safety.
        Assert.Contains("not a field", Refuse(("orderByFields", "nope")), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_parameter_is_refused_rather_than_passing_in_silence()
    {
        // The hole this closes: anything absent from the refused list used to
        // pass unnoticed, so returnCentroid was accepted and ignored without
        // anybody deciding it should be.
        string error = Refuse(("somethingNew", "1"));

        Assert.Contains("somethingNew", error, StringComparison.Ordinal);
        Assert.Contains("refused rather than ignored", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_polygon_filter_is_read_from_ArcGIS_geometry_JSON()
    {
        // Only an envelope was accepted until 2026-08-15, on the grounds that a
        // bounding box is what the index answers. The index still answers the
        // box; the predicate that follows it is what the geometry engine does,
        // and PostGIS has had one all along.
        FeatureQuery query = Parse(
            ("geometry", "{\"rings\":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}"),
            ("geometryType", "esriGeometryPolygon"),
            ("spatialRel", "esriSpatialRelWithin"));

        Assert.NotNull(query.Spatial);
        Assert.Equal(SpatialRelation.Within, query.Spatial!.Relation);
    }

    [Fact]
    public void A_point_filter_is_read_from_the_short_syntax()
    {
        FeatureQuery query = Parse(
            ("geometry", "10,20"),
            ("geometryType", "esriGeometryPoint"),
            ("distance", "5"));

        Assert.NotNull(query.Spatial);
        Assert.Equal(5, query.Spatial!.Distance);
    }

    [Fact]
    public void The_short_syntax_is_only_defined_for_envelopes_and_points()
    {
        // Four numbers and geometryType=polygon is a client mistake worth naming
        // rather than guessing at.
        Assert.Contains(
            "must be ArcGIS geometry JSON",
            Refuse(("geometry", "0,0,1,1"), ("geometryType", "esriGeometryPolygon")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_envelope_stated_in_another_spatial_reference_is_refused()
    {
        // The SDK puts the spatial reference inside the geometry rather than in
        // inSR. Ignoring it accepts a box stated in one system and filters with
        // it in another, which returns the wrong features rather than none.
        Refuse(("geometry", "{\"xmin\":1,\"ymin\":2,\"xmax\":3,\"ymax\":4,"
            + "\"spatialReference\":{\"wkid\":4326}}"));
    }

    [Fact]
    public void An_outSR_is_carried_into_the_query_for_the_database_to_apply()
    {
        // Refused until 2026-08-15 on the grounds that this server does not
        // reproject. It does now, in the database, on the way out — and the
        // response reports the reference the geometry is actually in, which is
        // the half that matters.
        Assert.Equal(4326, Parse(("outSR", "4326")).OutSrid);
    }

    [Fact]
    public void An_outSR_that_matches_the_layer_costs_nothing()
    {
        // Transforming into the reference the data is already in changes no
        // coordinate and costs a function call per row.
        Assert.Null(Parse(("outSR", Srid.ToString(System.Globalization.CultureInfo.InvariantCulture))).OutSrid);
    }

    [Fact]
    public void An_inSR_that_would_need_reprojection_is_still_refused()
    {
        // <b>The asymmetry is deliberate.</b> Output reprojection is a transform
        // the database applies to the answer; input reprojection would mean
        // comparing a filter in one reference against data in another, and when
        // that is skipped there is no error — the boxes simply never meet and
        // the answer is zero features. That is the defect that made every 4326
        // tile silently empty.
        Assert.Contains(
            "no error and no features",
            Refuse(("geometry", "0,0,1,1"), ("inSR", "4326")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_that_does_not_exist_is_refused_rather_than_dropped()
    {
        Assert.Contains("not a field", Refuse(("outFields", "nosuch")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void A_record_count_that_is_not_a_positive_integer_is_refused(string value) =>
        Refuse(("resultRecordCount", value));

    [Fact]
    public void A_negative_offset_is_refused() => Refuse(("resultOffset", "-1"));

    [Fact]
    public void A_geometry_that_is_not_a_shape_at_all_is_refused_with_the_reason()
    {
        // An empty ring list is not a polygon; the reader says so rather than
        // producing a geometry with nothing in it.
        Refuse(("geometry", "{\"rings\":[]}"), ("geometryType", "esriGeometryPolygon"));
    }

    // ---------- what every ArcGIS client sends ----------

    [Fact]
    public void The_always_true_predicate_is_accepted_in_the_spellings_clients_use()
    {
        // Refusing this refuses every ArcGIS client for no safety gained.
        foreach (string where in (string[])["1=1", "1 = 1", ""])
        {
            Parse(("where", where));
        }
    }

    [Fact]
    public void outFields_star_expands_to_every_column()
    {
        // Refused until the catalogue could describe columns. The stated reason
        // expired when DescribeAsync arrived, and a refusal whose reason has
        // expired is just an obstacle.
        FeatureQuery query = Parse(("outFields", "*"));

        Assert.Contains("name", query.Fields);
        Assert.Contains("height", query.Fields);
        Assert.Contains("objectid", query.Fields);
    }

    [Fact]
    public void The_object_id_is_requested_even_when_outFields_omits_it()
    {
        // A response whose objectIdFieldName names a field the features do not
        // carry is one a client cannot page or select against.
        Assert.Contains("objectid", Parse(("outFields", "name")).Fields);
    }

    [Fact]
    public void The_object_id_is_not_requested_twice_when_outFields_names_it()
    {
        Assert.Equal(["objectid", "name"], Parse(("outFields", "objectid,name")).Fields);
    }

    [Fact]
    public void An_offset_is_carried_through_for_paging()
    {
        Assert.Equal(250, Parse(("resultOffset", "250")).Offset);
    }

    [Fact]
    public void returnGeometry_false_is_honoured_and_the_default_is_true()
    {
        Assert.False(Parse(("returnGeometry", "false")).IncludeGeometry);
        Assert.True(Parse(("outFields", "name")).IncludeGeometry);
    }

    [Fact]
    public void The_response_shape_is_reported_separately_from_the_query()
    {
        // <b>An enum, not a boolean, since 2026-08-15.</b> Four parameters each
        // replace the response with something different, and they are mutually
        // exclusive — so the parser reports which one rather than a flag per
        // parameter that could be set in combinations with no meaning.
        Assert.Equal(QueryShape.Count, Shape(("returnCountOnly", "true")));
        Assert.Equal(QueryShape.Ids, Shape(("returnIdsOnly", "true")));
        Assert.Equal(QueryShape.Extent, Shape(("returnExtentOnly", "true")));
        Assert.Equal(QueryShape.Features, Shape(("outFields", "*")));

        Assert.Equal(
            QueryShape.Statistics,
            Shape(("outStatistics",
                "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\"}]")));
    }

    [Fact]
    public void Two_response_shapes_at_once_are_refused_rather_than_ranked()
    {
        // Guessing a precedence means answering one question and silently
        // dropping the other, which is the failure this whole class avoids.
        Assert.False(FeatureServerQueryParameters.TryParse(
            Query(("returnCountOnly", "true"), ("returnIdsOnly", "true")),
            "objectid", Srid, Fields, out _, out _, out string? error));

        Assert.Contains("Ask for one", error!, StringComparison.Ordinal);
    }

    private static QueryShape Shape(params (string Key, string Value)[] parameters)
    {
        Assert.True(
            FeatureServerQueryParameters.TryParse(
                Query(parameters), "objectid", Srid, Fields, out _, out QueryShape shape,
                out string? error),
            error);

        return shape;
    }

    [Fact]
    public void An_envelope_is_parsed_in_both_the_forms_clients_send()
    {
        // Comma-separated from a hand-built URL, JSON from the ArcGIS SDKs.
        // Supporting one is a compatibility surface that works for half of them.
        foreach (string geometry in (string[])
            ["1,2,3,4", "{\"xmin\":1,\"ymin\":2,\"xmax\":3,\"ymax\":4}"])
        {
            Envelope box = Parse(("geometry", geometry)).BoundingBox!.Value;

            Assert.Equal(1, box.MinX);
            Assert.Equal(2, box.MinY);
            Assert.Equal(3, box.MaxX);
            Assert.Equal(4, box.MaxY);
        }
    }

    [Fact]
    public void A_matching_outSR_is_accepted()
    {
        Parse(("outSR", Srid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_request_for_more_than_the_maximum_is_clamped_rather_than_refused()
    {
        Assert.Equal(FeatureQuery.MaximumLimit, Parse(("resultRecordCount", "999999999")).Limit);
    }

    // ---------- the ignored set ----------

    [Theory]
    [InlineData("quantizationParameters")]
    [InlineData("cacheHint")]
    [InlineData("returnCentroid")]
    [InlineData("f")]
    public void A_parameter_that_cannot_lose_data_is_accepted_and_declares_itself_ignored(string name)
    {
        Parse((name, "whatever"));

        Assert.True(
            FeatureServerQueryParameters.IsIgnored(name, out string reason),
            $"'{name}' is accepted but not declared as ignored, so nothing logs it.");

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Nothing_is_both_refused_and_ignored()
    {
        // The two lists are the whole policy, and a name in both would make the
        // behaviour depend on evaluation order rather than on a decision.
        foreach (string refused in (string[])
            ["outStatistics", "orderByFields", "returnIdsOnly", "objectIds", "having",
             "returnDistinctValues", "returnExtentOnly", "time", "distance", "relationParam",
             "groupByFieldsForStatistics"])
        {
            Assert.False(
                FeatureServerQueryParameters.IsIgnored(refused, out _),
                $"'{refused}' is listed as both refused and ignored.");
        }
    }
}
