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
    [InlineData("outStatistics")]
    [InlineData("returnIdsOnly")]
    [InlineData("objectIds")]
    [InlineData("having")]
    public void A_parameter_that_changes_the_answer_is_refused_rather_than_ignored(string parameter)
    {
        Assert.Contains(parameter, Refuse((parameter, "anything")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_where_clause_is_refused_and_says_what_would_be_needed()
    {
        string error = Refuse(("where", "name = 'x'"));

        Assert.Contains("always-true", error, StringComparison.Ordinal);
        Assert.Contains("ADR-008", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spatial_relationship_other_than_intersects_is_refused()
    {
        Assert.Contains(
            "esriSpatialRelIntersects",
            Refuse(("spatialRel", "esriSpatialRelContains")),
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
    public void A_geometry_type_other_than_an_envelope_is_refused()
    {
        Assert.Contains(
            "esriGeometryEnvelope",
            Refuse(("geometryType", "esriGeometryPolygon")),
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
    public void An_outSR_that_would_need_reprojection_is_refused()
    {
        // Returning geometry in a system it was not asked for, or claiming a
        // system it is not in, both put a client's features in the sea.
        string error = Refuse(("outSR", "4326"));

        Assert.Contains("does not reproject", error, StringComparison.Ordinal);
        Assert.Contains("4326", error, StringComparison.Ordinal);
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
    public void A_geometry_that_is_not_an_envelope_is_refused_with_the_reason()
    {
        Assert.Contains(
            "envelope",
            Refuse(("geometry", "{\"rings\":[]}")),
            StringComparison.Ordinal);
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
    public void returnCountOnly_is_reported_separately_from_the_query()
    {
        Assert.True(FeatureServerQueryParameters.TryParse(
            Query(("returnCountOnly", "true")), "objectid", Srid, Fields, out _, out bool countOnly, out _));

        Assert.True(countOnly);
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
    [InlineData("maxAllowableOffset")]
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
